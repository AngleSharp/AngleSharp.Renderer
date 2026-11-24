namespace AngleSharp.Renderer.Skia.Svg;

using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

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

    /// <summary>
    /// Renders one element in user space with initial paint state, the way a filter's `feImage`
    /// draws an `#id` reference.
    /// </summary>
    public static void RenderReferencedElement(SKCanvas canvas, IElement element, SvgRenderContext context, SvgViewport viewport) =>
        RenderElement(canvas, element, SvgPaintState.Initial, context, viewport);

    private static void RenderElement(SKCanvas canvas, IElement element, SvgPaintState inheritedState, SvgRenderContext context, SvgViewport viewport)
    {
        var tagName = element.LocalName;

        if (NonRenderingTags.Contains(tagName))
        {
            return;
        }

        // an element not displayed renders nothing, nor do its descendants (e.g. the layers of a
        // Lottie animation that are not in at the current frame)
        if (string.Equals(SvgPaintState.ReadProperty(element, context, "display"), "none", StringComparison.OrdinalIgnoreCase))
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
            canvas.Concat(in transform);
        }

        if (clipPathElement is not null)
        {
            using var clipPath = BuildClipPath(clipPathElement, viewport);
            canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
        }

        var filterLayerCount = -1;

        if (filterElement is not null && SvgFilterBuilder.Build(filterElement, element, context, viewport) is { } filter)
        {
            // The filter region clips both what the filter sees and what it produces.
            canvas.ClipRect(filter.Region);
            using var filterPaint = new SKPaint { ImageFilter = filter.ImageFilter, ColorFilter = filter.ColorFilter };
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
        // the mask region clips the layers, too, so they are only as large as it is
        var contentLayerCount = canvas.Save();
        ApplyMaskRegionClip(canvas, maskElement, element, viewport, context);
        canvas.SaveLayer();
        RenderElementContent(canvas, element, tagName, state, context, viewport);

        // a mask's luminance is its coverage, unless it says its alpha is (mask-type: alpha)
        var isAlphaMask = string.Equals(SvgPaintState.ReadProperty(maskElement, context, "mask-type"), "alpha", StringComparison.OrdinalIgnoreCase);

        using (var maskPaint = new SKPaint { ColorFilter = isAlphaMask ? null : SKColorFilter.CreateLumaColor(), BlendMode = SKBlendMode.DstIn })
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

    /// <summary>
    /// Paints an <c>image</c> element whose <c>href</c> is a <c>data:</c> URL (e.g. the bitmaps a
    /// Lottie animation is made of), or one the page loaded (see <see cref="LoadedImages"/>), into
    /// its viewport (<c>x</c>, <c>y</c>, <c>width</c>, <c>height</c>), fitted per
    /// <c>preserveAspectRatio</c>; other images are not painted.
    /// </summary>
    private static void RenderImage(SKCanvas canvas, IElement element, SvgPaintState state, SvgRenderContext context, SvgViewport viewport)
    {
        if (string.Equals(SvgPaintState.ReadProperty(element, context, "visibility"), "hidden", StringComparison.OrdinalIgnoreCase) ||
            (element.GetAttribute("href") ?? element.GetAttribute("xlink:href")) is not { } href ||
            (DecodedImages.Get(href) ?? LoadedImages.Get(element)) is not { } image)
        {
            return;
        }

        var x = SvgShapeBuilder.ReadX(element, "x", viewport);
        var y = SvgShapeBuilder.ReadY(element, "y", viewport);
        // an auto size is the image's own
        var width = SvgLength.Parse(element.GetAttribute("width"), viewport.Width, image.Width);
        var height = SvgLength.Parse(element.GetAttribute("height"), viewport.Height, image.Height);

        if (width <= 0f || height <= 0f || state.Opacity <= 0f)
        {
            return;
        }

        var (align, meet) = SvgViewBoxMapping.ParsePreserveAspectRatio(element.GetAttribute("preserveAspectRatio"));
        var fit = SvgViewBoxMapping.ComputeMatrix((0f, 0f, image.Width, image.Height), width, height, align, meet);
        var destination = fit.MapRect(SKRect.Create(0f, 0f, image.Width, image.Height));
        destination.Offset(x, y);
        // drawn smaller, from a copy scaled down once (see SkiaRenderBackend.GetScaledDown)
        var scaled = SkiaRenderBackend.GetScaledDown(image, destination, canvas.TotalMatrix);
        var sampling = scaled is not null
            ? new SKSamplingOptions(SKFilterMode.Linear)
            : SkiaRenderBackend.GetImageSamplingOptions(image, destination, canvas.TotalMatrix);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha((byte)Math.Round(255f * Math.Clamp(state.Opacity, 0f, 1f))) };

        canvas.Save();
        // what slices the viewport is cut off
        canvas.ClipRect(SKRect.Create(x, y, width, height));
        canvas.DrawImage(scaled ?? image, destination, sampling, paint);
        canvas.Restore();
    }

    /// <summary>
    /// The images of <c>data:</c> URLs decoded once: an animation renders the same ones in every
    /// frame. The cache follows the URL strings (the attribute values), so it goes with them.
    /// </summary>
    private static class DecodedImages
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, Holder> Images = new();

        public static SKImage? Get(string href) => Images.GetValue(href, static key => new Holder(Decode(key))).Image;

        private static SKImage? Decode(string href)
        {
            var value = href.Trim();
            var comma = value.IndexOf(',');

            if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
            {
                return null;
            }

            try
            {
                var data = value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(value[(comma + 1)..])
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(value[(comma + 1)..]));
                return SKImage.FromEncodedData(data);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private sealed record Holder(SKImage? Image);
    }

    /// <summary>
    /// The images of <c>image</c> elements in a page's inline SVG whose <c>href</c> is not a
    /// <c>data:</c> URL, as the page loaded them (by the address it resolves), given before the
    /// SVG is recorded. They are decoded once per loaded data.
    /// </summary>
    internal static class LoadedImages
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IElement, byte[]> DataByElement = new();
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Holder> Images = new();

        public static void Set(IElement element, byte[] data) => DataByElement.AddOrUpdate(element, data);

        public static SKImage? Get(IElement element) =>
            DataByElement.TryGetValue(element, out var data) ? Images.GetValue(data, static key => new Holder(SKImage.FromEncodedData(key))).Image : null;

        private sealed record Holder(SKImage? Image);
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

            case "image":
                RenderImage(canvas, element, state, context, viewport);
                break;

            case "text":
                var x = SvgShapeBuilder.ReadX(element, "x", viewport);
                var y = SvgShapeBuilder.ReadY(element, "y", viewport);
                RenderTextContent(canvas, element, state, context, viewport, ref x, y);
                break;

            default:
                // Unsupported element: descend in case it groups further shapes, but
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
        canvas.Concat(in viewBoxMatrix);

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
                    canvas.Concat(in viewBoxMatrix);

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
        using var combined = new SKPathBuilder();

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

        return combined.Detach();
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
        // Shaped like HTML text (kerning, ligatures, joining scripts, font fallback), in visual
        // order with `direction` as the paragraph direction.
        var font = new RenderFont(state.FontFamily, state.FontSize, state.FontWeight, state.IsItalic, 0f, Language: state.Language);
        using var measurePaint = SkiaTextShaping.CreateTextPaint(font);
        var width = SkiaTextShaping.MeasureParagraph(measurePaint, text, font, state.IsRightToLeft);
        var ascent = -measurePaint.Font.Metrics.Ascent;
        var descent = measurePaint.Font.Metrics.Descent;
        var bounds = new SKRect(cursorX, y - ascent, cursorX + width, y + descent);

        // `start` and `end` follow the direction: right-to-left text starts at its right edge
        var anchorOffset = state.TextAnchor switch
        {
            SvgTextAnchor.Middle => -width / 2f,
            SvgTextAnchor.End => state.IsRightToLeft ? 0f : -width,
            _ => state.IsRightToLeft ? -width : 0f,
        };

        var drawX = cursorX + anchorOffset;

        using var fillPaint = state.CreateFillPaint(bounds, context, viewport);

        if (fillPaint is not null)
        {
            using var fillTextPaint = SkiaTextShaping.CreateTextPaint(font, fillPaint);
            SkiaTextShaping.DrawParagraph(canvas, fillTextPaint, text, drawX, y, font, state.IsRightToLeft);
        }

        using var strokePaint = state.CreateStrokePaint(bounds, context, viewport);

        if (strokePaint is not null)
        {
            using var strokeTextPaint = SkiaTextShaping.CreateTextPaint(font, strokePaint);
            SkiaTextShaping.DrawParagraph(canvas, strokeTextPaint, text, drawX, y, font, state.IsRightToLeft);
        }

        cursorX += state.IsRightToLeft ? -width : width;
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
