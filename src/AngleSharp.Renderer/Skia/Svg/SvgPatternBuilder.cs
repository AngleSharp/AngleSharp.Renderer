namespace AngleSharp.Renderer.Skia.Svg;

using System.Linq;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Builds an <see cref="SKShader"/> from a &lt;pattern&gt; element by rendering its content once
/// into a tile bitmap (reusing <see cref="SvgElementRenderer.Render"/>, the same entry point the
/// root &lt;svg&gt; uses) and repeating that tile with <see cref="SKShader.CreateImage(SKImage,
/// SKShaderTileMode, SKShaderTileMode, SKMatrix)"/>.
/// </summary>
internal static class SvgPatternBuilder
{
    private const float TileOversampleFactor = 2f;
    private const int MaxTileDimension = 1024;

    public static SKShader? Build(IElement patternElement, SKRect boundingBox, SvgRenderContext context, SvgViewport viewport)
    {
        if (!context.ActivePatterns.Add(patternElement))
        {
            // A pattern whose own content (directly or transitively) references itself again would
            // recurse forever; treat the reference as unresolved instead.
            return null;
        }

        try
        {
            var contentSource = ResolveContentSource(patternElement, context.ElementsById);

            if (contentSource is null || !contentSource.Children.Any())
            {
                return null;
            }

            var isObjectBoundingBox = !string.Equals(
                GetInheritedAttribute(patternElement, "patternUnits", context.ElementsById),
                "userSpaceOnUse",
                StringComparison.OrdinalIgnoreCase);

            var x = ResolveCoordinate(GetInheritedAttribute(patternElement, "x", context.ElementsById), 0f, isObjectBoundingBox, boundingBox.Left, boundingBox.Width, viewport.Width);
            var y = ResolveCoordinate(GetInheritedAttribute(patternElement, "y", context.ElementsById), 0f, isObjectBoundingBox, boundingBox.Top, boundingBox.Height, viewport.Height);
            var width = ResolveCoordinate(GetInheritedAttribute(patternElement, "width", context.ElementsById), 0f, isObjectBoundingBox, 0f, boundingBox.Width, viewport.Width);
            var height = ResolveCoordinate(GetInheritedAttribute(patternElement, "height", context.ElementsById), 0f, isObjectBoundingBox, 0f, boundingBox.Height, viewport.Height);

            if (width <= 0f || height <= 0f)
            {
                return null;
            }

            var scale = TileOversampleFactor;
            var maxNatural = Math.Max(width, height);

            if (maxNatural * scale > MaxTileDimension)
            {
                scale = Math.Max(1f, MaxTileDimension / maxNatural);
            }

            var tilePixelWidth = Math.Max(1, (int)Math.Round(width * scale));
            var tilePixelHeight = Math.Max(1, (int)Math.Round(height * scale));

            var contentUnitsIsObjectBoundingBox = string.Equals(
                GetInheritedAttribute(patternElement, "patternContentUnits", context.ElementsById),
                "objectBoundingBox",
                StringComparison.OrdinalIgnoreCase);

            var viewBox = SvgViewBoxMapping.ParseViewBox(GetInheritedAttribute(patternElement, "viewBox", context.ElementsById));

            using var tileBitmap = new SKBitmap(tilePixelWidth, tilePixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            tileBitmap.Erase(SKColors.Transparent);

            using (var tileCanvas = new SKCanvas(tileBitmap))
            {
                tileCanvas.Scale(scale, scale);

                SvgViewport contentViewport;

                if (viewBox is { } box)
                {
                    var (align, meet) = SvgViewBoxMapping.ParsePreserveAspectRatio(GetInheritedAttribute(patternElement, "preserveAspectRatio", context.ElementsById));
                    var viewBoxMatrix = SvgViewBoxMapping.ComputeMatrix(viewBox, width, height, align, meet);
                    tileCanvas.Concat(ref viewBoxMatrix);
                    contentViewport = new SvgViewport(box.Width, box.Height);
                }
                else if (contentUnitsIsObjectBoundingBox)
                {
                    tileCanvas.Scale(width, height);
                    contentViewport = new SvgViewport(1f, 1f);
                }
                else
                {
                    contentViewport = new SvgViewport(width, height);
                }

                SvgElementRenderer.Render(tileCanvas, contentSource, SvgPaintState.Initial, context, contentViewport);
                tileCanvas.Flush();
            }

            using var tileImage = SKImage.FromBitmap(tileBitmap);

            var patternTransform = SvgTransformParser.Parse(GetInheritedAttribute(patternElement, "patternTransform", context.ElementsById));
            var tileToUserSpace = new SKMatrix(width / tilePixelWidth, 0f, x, 0f, height / tilePixelHeight, y, 0f, 0f, 1f);
            var localMatrix = tileToUserSpace.PostConcat(patternTransform);

            return SKShader.CreateImage(tileImage, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, localMatrix);
        }
        finally
        {
            context.ActivePatterns.Remove(patternElement);
        }
    }

    private static float ResolveCoordinate(string? raw, float defaultValue, bool isObjectBoundingBox, float boxOrigin, float boxSize, float viewportReference)
    {
        if (isObjectBoundingBox)
        {
            var fraction = ParseFraction(raw, defaultValue);
            return boxOrigin + (fraction * boxSize);
        }

        return SvgLength.Parse(raw, viewportReference, defaultValue);
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

    private static IElement? ResolveContentSource(IElement start, IReadOnlyDictionary<string, IElement> elementsById)
    {
        var visited = new HashSet<IElement>();
        var current = start;

        while (current is not null && visited.Add(current))
        {
            if (current.Children.Any())
            {
                return current;
            }

            current = SvgUrlReference.GetHref(current) is { } href && href.StartsWith('#') && elementsById.TryGetValue(href[1..], out var next)
                ? next
                : null;
        }

        return null;
    }

    private static string? GetInheritedAttribute(IElement start, string attribute, IReadOnlyDictionary<string, IElement> elementsById)
    {
        var visited = new HashSet<IElement>();
        var current = start;

        while (current is not null && visited.Add(current))
        {
            var value = current.GetAttribute(attribute);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            current = SvgUrlReference.GetHref(current) is { } href && href.StartsWith('#') && elementsById.TryGetValue(href[1..], out var next)
                ? next
                : null;
        }

        return null;
    }
}
