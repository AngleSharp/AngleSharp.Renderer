namespace AngleSharp.Renderer.Skia.Svg;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Walks an SVG element tree that AngleSharp has already parsed (either as part of the host HTML
/// document, for inline &lt;svg&gt;, or as a standalone document for an SVG image source) and
/// paints its shapes directly onto an <see cref="SKCanvas"/>. No SVG markup is re-parsed here -
/// this only reads the attributes AngleSharp already exposes on the DOM.
/// </summary>
internal static class SvgElementRenderer
{
    private static readonly HashSet<string> NonRenderingTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "defs", "title", "desc", "metadata", "style", "symbol", "clipPath", "mask", "linearGradient", "radialGradient", "pattern", "filter",
    };

    public static void Render(SKCanvas canvas, IElement svgRoot, SvgPaintState initialState, SvgRenderContext context, SvgViewport viewport)
    {
        // The root itself is never visited by RenderElement (that only walks its children), so its
        // own presentation attributes/style (e.g. a `color` establishing currentColor for the whole
        // document) would otherwise be silently ignored.
        var rootState = initialState.Resolve(svgRoot, context, viewport);

        foreach (var child in svgRoot.Children)
        {
            RenderElement(canvas, child, rootState, context, viewport);
        }
    }

    private static void RenderElement(SKCanvas canvas, IElement element, SvgPaintState inheritedState, SvgRenderContext context, SvgViewport viewport)
    {
        var tagName = element.LocalName;

        if (NonRenderingTags.Contains(tagName))
        {
            return;
        }

        var state = inheritedState.Resolve(element, context, viewport);
        var transform = SvgTransformParser.Parse(element.GetAttribute("transform"));
        var hasTransform = !transform.IsIdentity;

        var clipPathElement = ResolveReferencedElement(element, "clip-path", "clipPath", context);
        var maskElement = ResolveReferencedElement(element, "mask", "mask", context);
        var filterElement = ResolveReferencedElement(element, "filter", "filter", context);
        var needsScope = hasTransform || clipPathElement is not null || maskElement is not null || filterElement is not null;

        if (needsScope)
        {
            canvas.Save();
        }

        if (hasTransform)
        {
            canvas.Concat(ref transform);
        }

        if (clipPathElement is not null)
        {
            using var clipPath = BuildClipPath(clipPathElement, viewport);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
        }

        var filterLayerCount = -1;

        if (filterElement is not null && SvgFilterBuilder.Build(filterElement) is { } imageFilter)
        {
            using var filterPaint = new SKPaint { ImageFilter = imageFilter };
            filterLayerCount = canvas.SaveLayer(filterPaint);
        }

        if (maskElement is not null)
        {
            RenderMasked(canvas, element, maskElement, tagName, state, context, viewport);
        }
        else
        {
            RenderElementContent(canvas, element, tagName, state, context, viewport);
        }

        if (filterLayerCount >= 0)
        {
            canvas.RestoreToCount(filterLayerCount);
        }

        if (needsScope)
        {
            canvas.Restore();
        }
    }

    private static void RenderMasked(SKCanvas canvas, IElement element, IElement maskElement, string tagName, SvgPaintState state, SvgRenderContext context, SvgViewport viewport)
    {
        var contentLayerCount = canvas.SaveLayer();

        ApplyMaskRegionClip(canvas, maskElement, element, viewport, context);
        RenderElementContent(canvas, element, tagName, state, context, viewport);

        using (var maskPaint = new SKPaint { ColorFilter = SKColorFilter.CreateLumaColor(), BlendMode = SKBlendMode.DstIn })
        {
            canvas.SaveLayer(maskPaint);

            var contentUnitsIsObjectBoundingBox = string.Equals(maskElement.GetAttribute("maskContentUnits"), "objectBoundingBox", StringComparison.OrdinalIgnoreCase);
            var maskViewport = viewport;

            if (contentUnitsIsObjectBoundingBox && SvgGeometry.ComputeBounds(element, viewport, context) is { } bbox)
            {
                canvas.Translate(bbox.Left, bbox.Top);
                canvas.Scale(bbox.Width, bbox.Height);
                maskViewport = new SvgViewport(1f, 1f);
            }

            foreach (var maskChild in maskElement.Children)
            {
                RenderElement(canvas, maskChild, SvgPaintState.Initial, context, maskViewport);
            }
        }

        canvas.RestoreToCount(contentLayerCount);
    }

    private static void ApplyMaskRegionClip(SKCanvas canvas, IElement maskElement, IElement maskedElement, SvgViewport viewport, SvgRenderContext context)
    {
        var isObjectBoundingBox = !string.Equals(maskElement.GetAttribute("maskUnits"), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);

        if (isObjectBoundingBox)
        {
            var bounds = SvgGeometry.ComputeBounds(maskedElement, viewport, context);

            if (bounds is not { } bbox)
            {
                // Can't determine a bounding box (e.g. a group of only text) - don't clip, so the
                // mask still applies rather than silently painting nothing.
                return;
            }

            var x = ParseFraction(maskElement.GetAttribute("x"), -0.1f);
            var y = ParseFraction(maskElement.GetAttribute("y"), -0.1f);
            var w = ParseFraction(maskElement.GetAttribute("width"), 1.2f);
            var h = ParseFraction(maskElement.GetAttribute("height"), 1.2f);

            canvas.ClipRect(new SKRect(
                bbox.Left + (x * bbox.Width),
                bbox.Top + (y * bbox.Height),
                bbox.Left + ((x + w) * bbox.Width),
                bbox.Top + ((y + h) * bbox.Height)));
        }
        else
        {
            var x = SvgLength.Parse(maskElement.GetAttribute("x"), viewport.Width, -0.1f * viewport.Width);
            var y = SvgLength.Parse(maskElement.GetAttribute("y"), viewport.Height, -0.1f * viewport.Height);
            var w = SvgLength.Parse(maskElement.GetAttribute("width"), viewport.Width, 1.2f * viewport.Width);
            var h = SvgLength.Parse(maskElement.GetAttribute("height"), viewport.Height, 1.2f * viewport.Height);

            canvas.ClipRect(new SKRect(x, y, x + w, y + h));
        }
    }

    private static float ParseFraction(string? raw, float defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        var trimmed = raw.Trim();

        if (trimmed.EndsWith('%') && float.TryParse(trimmed[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100f;
        }

        return float.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : defaultValue;
    }

    private static void RenderElementContent(SKCanvas canvas, IElement element, string tagName, SvgPaintState state, SvgRenderContext context, SvgViewport viewport)
    {
        switch (tagName.ToLowerInvariant())
        {
            case "g":
            case "a":
                foreach (var child in element.Children)
                {
                    RenderElement(canvas, child, state, context, viewport);
                }

                break;

            case "svg":
                RenderNestedViewport(canvas, element, element.Children, state, context, viewport, translate: true);
                break;

            case "use":
                RenderUse(canvas, element, state, context, viewport);
                break;

            case "rect":
                RenderPath(canvas, SvgShapeBuilder.BuildRect(element, viewport), state, context, viewport);
                break;

            case "circle":
                RenderPath(canvas, SvgShapeBuilder.BuildCircle(element, viewport), state, context, viewport);
                break;

            case "ellipse":
                RenderPath(canvas, SvgShapeBuilder.BuildEllipse(element, viewport), state, context, viewport);
                break;

            case "line":
                RenderPath(canvas, SvgShapeBuilder.BuildLine(element, viewport), state, context, viewport, strokeOnly: true);
                break;

            case "polyline":
            case "polygon":
            case "path":
                if (SvgShapeBuilder.Build(element, viewport) is { } shapePath)
                {
                    RenderPath(canvas, shapePath, state, context, viewport);
                }

                break;

            case "text":
                var x = SvgShapeBuilder.ReadX(element, "x", viewport);
                var y = SvgShapeBuilder.ReadY(element, "y", viewport);
                RenderTextContent(canvas, element, state, context, viewport, ref x, y);
                break;

            default:
                // Unsupported element (image, ...): descend in case it groups further shapes, but
                // paint nothing for the element itself.
                foreach (var child in element.Children)
                {
                    RenderElement(canvas, child, state, context, viewport);
                }

                break;
        }
    }

    /// <summary>
    /// Establishes a new viewport for a nested &lt;svg&gt; or a &lt;symbol&gt;/&lt;svg&gt;
    /// referenced through &lt;use&gt;: translates to `x`/`y` (only meaningful for a direct nested
    /// &lt;svg&gt;, not a &lt;use&gt;'s already-applied translate), clips to `width`x`height`, maps
    /// the element's own `viewBox` onto that box, and renders its children in the resulting
    /// coordinate system.
    /// </summary>
    private static void RenderNestedViewport(SKCanvas canvas, IElement viewportElement, IEnumerable<IElement> content, SvgPaintState state, SvgRenderContext context, SvgViewport outerViewport, bool translate)
    {
        var width = SvgShapeBuilder.ReadOptionalX(viewportElement, "width", outerViewport) ?? outerViewport.Width;
        var height = SvgShapeBuilder.ReadOptionalY(viewportElement, "height", outerViewport) ?? outerViewport.Height;

        if (width <= 0f || height <= 0f)
        {
            return;
        }

        canvas.Save();

        if (translate)
        {
            var x = SvgShapeBuilder.ReadX(viewportElement, "x", outerViewport);
            var y = SvgShapeBuilder.ReadY(viewportElement, "y", outerViewport);
            canvas.Translate(x, y);
        }

        canvas.ClipRect(new SKRect(0f, 0f, width, height));

        var viewBox = SvgViewBoxMapping.ParseViewBox(viewportElement.GetAttribute("viewBox"));
        var (align, meet) = SvgViewBoxMapping.ParsePreserveAspectRatio(viewportElement.GetAttribute("preserveAspectRatio"));
        var viewBoxMatrix = SvgViewBoxMapping.ComputeMatrix(viewBox, width, height, align, meet);
        canvas.Concat(ref viewBoxMatrix);

        var innerViewport = viewBox is { } box ? new SvgViewport(box.Width, box.Height) : new SvgViewport(width, height);

        foreach (var child in content)
        {
            RenderElement(canvas, child, state, context, innerViewport);
        }

        canvas.Restore();
    }

    private static void RenderUse(SKCanvas canvas, IElement element, SvgPaintState state, SvgRenderContext context, SvgViewport viewport)
    {
        var href = SvgUrlReference.GetHref(element);

        if (href is not { Length: > 1 } || !href.StartsWith('#') ||
            !context.ElementsById.TryGetValue(href[1..], out var referenced) ||
            !context.ActiveUseReferences.Add(referenced))
        {
            return;
        }

        try
        {
            var x = SvgShapeBuilder.ReadX(element, "x", viewport);
            var y = SvgShapeBuilder.ReadY(element, "y", viewport);

            canvas.Save();
            canvas.Translate(x, y);

            var referencedTag = referenced.LocalName.ToLowerInvariant();

            if (referencedTag is "symbol" or "svg")
            {
                // A <use> referencing a <symbol>/<svg> establishes a new viewport sized from the
                // <use>'s own width/height (falling back to the referenced element's, then to the
                // outer viewport), per the SVG spec - it is not just a translated copy.
                var useWidth = SvgShapeBuilder.ReadOptionalX(element, "width", viewport)
                    ?? SvgShapeBuilder.ReadOptionalX(referenced, "width", viewport)
                    ?? viewport.Width;
                var useHeight = SvgShapeBuilder.ReadOptionalY(element, "height", viewport)
                    ?? SvgShapeBuilder.ReadOptionalY(referenced, "height", viewport)
                    ?? viewport.Height;

                if (useWidth > 0f && useHeight > 0f)
                {
                    canvas.Save();
                    canvas.ClipRect(new SKRect(0f, 0f, useWidth, useHeight));

                    var viewBox = SvgViewBoxMapping.ParseViewBox(referenced.GetAttribute("viewBox"));
                    var (align, meet) = SvgViewBoxMapping.ParsePreserveAspectRatio(referenced.GetAttribute("preserveAspectRatio"));
                    var viewBoxMatrix = SvgViewBoxMapping.ComputeMatrix(viewBox, useWidth, useHeight, align, meet);
                    canvas.Concat(ref viewBoxMatrix);

                    var innerViewport = viewBox is { } box ? new SvgViewport(box.Width, box.Height) : new SvgViewport(useWidth, useHeight);

                    foreach (var child in referenced.Children)
                    {
                        RenderElement(canvas, child, state, context, innerViewport);
                    }

                    canvas.Restore();
                }
            }
            else
            {
                RenderElement(canvas, referenced, state, context, viewport);
            }

            canvas.Restore();
        }
        finally
        {
            context.ActiveUseReferences.Remove(referenced);
        }
    }

    private static IElement? ResolveReferencedElement(IElement element, string property, string expectedTag, SvgRenderContext context)
    {
        var raw = element.GetAttribute(property);
        var style = element.GetAttribute("style");

        if (!string.IsNullOrWhiteSpace(style))
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIndex = declaration.IndexOf(':');

                if (colonIndex > 0 && declaration[..colonIndex].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                {
                    raw = declaration[(colonIndex + 1)..].Trim();
                }
            }
        }

        return SvgUrlReference.TryExtract(raw, out var id) &&
               context.ElementsById.TryGetValue(id, out var referenced) &&
               string.Equals(referenced.LocalName, expectedTag, StringComparison.OrdinalIgnoreCase)
            ? referenced
            : null;
    }

    private static SKPath BuildClipPath(IElement clipPathElement, SvgViewport viewport)
    {
        var combined = new SKPath();

        foreach (var child in clipPathElement.Children)
        {
            using var shapePath = SvgShapeBuilder.Build(child, viewport);

            if (shapePath is null)
            {
                continue;
            }

            var transform = SvgTransformParser.Parse(child.GetAttribute("transform"));

            using var transformedPath = transform.IsIdentity ? null : new SKPath();

            if (transformedPath is not null)
            {
                shapePath.Transform(transform, transformedPath);
                combined.AddPath(transformedPath);
            }
            else
            {
                combined.AddPath(shapePath);
            }
        }

        return combined;
    }

    private static void RenderPath(SKCanvas canvas, SKPath path, SvgPaintState state, SvgRenderContext context, SvgViewport viewport, bool strokeOnly = false)
    {
        path.FillType = state.FillRule;
        var bounds = path.Bounds;

        if (!strokeOnly)
        {
            using var fillPaint = state.CreateFillPaint(bounds, context, viewport);

            if (fillPaint is not null)
            {
                canvas.DrawPath(path, fillPaint);
            }
        }

        using var strokePaint = state.CreateStrokePaint(bounds, context, viewport);

        if (strokePaint is not null)
        {
            canvas.DrawPath(path, strokePaint);
        }

        path.Dispose();
    }

    private static void RenderTextContent(SKCanvas canvas, IElement element, SvgPaintState inheritedState, SvgRenderContext context, SvgViewport viewport, ref float cursorX, float baselineY)
    {
        var state = inheritedState.Resolve(element, context, viewport);

        var explicitX = SvgShapeBuilder.ReadOptionalX(element, "x", viewport);
        var explicitY = SvgShapeBuilder.ReadOptionalY(element, "y", viewport);

        if (explicitX is { } x)
        {
            cursorX = x;
        }

        var y = explicitY ?? baselineY;

        foreach (var node in element.ChildNodes)
        {
            if (node is IText textNode)
            {
                var text = NormalizeWhitespace(textNode.Data);

                if (text.Length > 0)
                {
                    DrawTextRun(canvas, text, state, context, viewport, ref cursorX, y);
                }
            }
            else if (node is IElement childElement && string.Equals(childElement.LocalName, "tspan", StringComparison.OrdinalIgnoreCase))
            {
                RenderTextContent(canvas, childElement, state, context, viewport, ref cursorX, y);
            }
        }
    }

    private static void DrawTextRun(SKCanvas canvas, string text, SvgPaintState state, SvgRenderContext context, SvgViewport viewport, ref float cursorX, float y)
    {
        using var measurePaint = new SKPaint
        {
            Typeface = SkiaTextShaping.CreateTypeface(state.FontFamily, SkiaTextShaping.CreateFontStyle(state.FontWeight, state.IsItalic)),
            TextSize = state.FontSize,
        };

        var width = measurePaint.MeasureText(text);
        var ascent = -measurePaint.FontMetrics.Ascent;
        var descent = measurePaint.FontMetrics.Descent;
        var bounds = new SKRect(cursorX, y - ascent, cursorX + width, y + descent);

        var anchorOffset = state.TextAnchor switch
        {
            SvgTextAnchor.Middle => -width / 2f,
            SvgTextAnchor.End => -width,
            _ => 0f,
        };

        var drawX = cursorX + anchorOffset;

        using var fillPaint = state.CreateFillPaint(bounds, context, viewport);

        if (fillPaint is not null)
        {
            fillPaint.Typeface = measurePaint.Typeface;
            fillPaint.TextSize = state.FontSize;
            canvas.DrawText(text, drawX, y, fillPaint);
        }

        using var strokePaint = state.CreateStrokePaint(bounds, context, viewport);

        if (strokePaint is not null)
        {
            strokePaint.Typeface = measurePaint.Typeface;
            strokePaint.TextSize = state.FontSize;
            canvas.DrawText(text, drawX, y, strokePaint);
        }

        cursorX += width;
    }

    private static string NormalizeWhitespace(string text)
    {
        var normalized = text.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        return normalized.Trim();
    }
}
