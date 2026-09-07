namespace AngleSharp.Renderer.Skia;

using System.Globalization;
using System.Text;

using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Renderer.Skia.Svg;

using SkiaSharp;

/// <summary>
/// Rasterizes SVG content into PNG bytes by walking the DOM AngleSharp already parsed and painting
/// it directly with SkiaSharp (<see cref="SvgElementRenderer"/>) - no second SVG parser is
/// involved, so an inline &lt;svg&gt; the host document already parsed is never re-parsed.
/// </summary>
internal static class SvgRasterizer
{
    // Rasterizing at natural size only would look sharp at 1:1 but blur once the renderer scales
    // the resulting PNG up to a larger CSS box (SkiaRenderBackend.DrawImage always scales at draw
    // time, the same way it does for ordinary raster images). Oversampling trades memory for
    // headroom against that common case.
    private const float OversampleFactor = 3f;
    private const int MaxRasterDimension = 4096;
    private const int SniffLength = 512;
    private const float DefaultNaturalSize = 300f;

    /// <summary>
    /// Determines whether the given bytes look like SVG markup rather than an encoded raster image.
    /// </summary>
    public static bool IsSvg(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, SniffLength));
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');

        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            var svgIndex = trimmed.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            return svgIndex >= 0;
        }

        return trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses standalone SVG markup (an `&lt;img&gt;` source or `data:` URI payload) with
    /// AngleSharp's own HTML/foreign-content parser and rasterizes the resulting element tree.
    /// </summary>
    public static bool TryRasterizeMarkup(byte[] svgBytes, out byte[] pngBytes, out int naturalWidth, out int naturalHeight)
    {
        pngBytes = [];
        naturalWidth = 0;
        naturalHeight = 0;

        var text = Encoding.UTF8.GetString(svgBytes).TrimStart('﻿', ' ', '\t', '\r', '\n');
        var xmlDeclarationEnd = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);

        if (xmlDeclarationEnd < 0)
        {
            return false;
        }

        var svgMarkup = text[xmlDeclarationEnd..];
        var wrapped = $"<!doctype html><html><body>{svgMarkup}</body></html>";

        try
        {
            var context = BrowsingContext.New(Configuration.Default);
            var document = context.OpenAsync(request => request.Content(wrapped)).GetAwaiter().GetResult();
            var svgRoot = document.QuerySelector("svg");

            if (svgRoot is null)
            {
                return false;
            }

            return TryRasterizeElement(svgRoot, out pngBytes, out naturalWidth, out naturalHeight);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rasterizes an SVG element that was already parsed as part of a larger document (an inline
    /// &lt;svg&gt; in the host HTML) - the DOM is walked directly, nothing is re-parsed.
    /// </summary>
    public static bool TryRasterizeElement(IElement svgRoot, out byte[] pngBytes, out int naturalWidth, out int naturalHeight)
    {
        pngBytes = [];
        naturalWidth = 0;
        naturalHeight = 0;

        try
        {
            var (viewBoxMatrix, width, height, contentViewport) = ResolveViewport(svgRoot);

            if (width <= 0f || height <= 0f)
            {
                return false;
            }

            naturalWidth = (int)Math.Max(1, Math.Round(width));
            naturalHeight = (int)Math.Max(1, Math.Round(height));

            var scale = OversampleFactor;
            var maxNatural = Math.Max(width, height);

            if (maxNatural * scale > MaxRasterDimension)
            {
                scale = Math.Max(1f, MaxRasterDimension / maxNatural);
            }

            var pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
            var pixelHeight = Math.Max(1, (int)Math.Round(height * scale));

            using var bitmap = new SKBitmap(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            bitmap.Erase(SKColors.Transparent);

            var renderContext = SvgRenderContext.Build(svgRoot);

            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Scale(scale, scale);
                canvas.Concat(ref viewBoxMatrix);
                SvgElementRenderer.Render(canvas, svgRoot, SvgPaintState.Initial, renderContext, contentViewport);
                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);

            if (data is null)
            {
                return false;
            }

            pngBytes = data.ToArray();
            return pngBytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static (SKMatrix ViewBoxMatrix, float Width, float Height, SvgViewport ContentViewport) ResolveViewport(IElement svgRoot)
    {
        var viewBox = SvgViewBoxMapping.ParseViewBox(svgRoot.GetAttribute("viewBox"));
        var explicitWidth = ParseLength(svgRoot.GetAttribute("width"));
        var explicitHeight = ParseLength(svgRoot.GetAttribute("height"));

        float width;
        float height;

        if (explicitWidth is { } specifiedWidth && explicitHeight is { } specifiedHeight)
        {
            width = specifiedWidth;
            height = specifiedHeight;
        }
        else if (explicitWidth is { } widthOnly)
        {
            width = widthOnly;
            height = viewBox is { } vbForHeight && vbForHeight.Width > 0f ? widthOnly * (vbForHeight.Height / vbForHeight.Width) : widthOnly;
        }
        else if (explicitHeight is { } heightOnly)
        {
            height = heightOnly;
            width = viewBox is { } vbForWidth && vbForWidth.Height > 0f ? heightOnly * (vbForWidth.Width / vbForWidth.Height) : heightOnly;
        }
        else if (viewBox is { } vbOnly)
        {
            width = vbOnly.Width;
            height = vbOnly.Height;
        }
        else
        {
            width = DefaultNaturalSize;
            height = DefaultNaturalSize / 2f;
        }

        var (align, meet) = SvgViewBoxMapping.ParsePreserveAspectRatio(svgRoot.GetAttribute("preserveAspectRatio"));
        var matrix = SvgViewBoxMapping.ComputeMatrix(viewBox, width, height, align, meet);
        var contentViewport = viewBox is { } box ? new SvgViewport(box.Width, box.Height) : new SvgViewport(width, height);

        return (matrix, width, height, contentViewport);
    }

    private static float? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.EndsWith('%'))
        {
            return null;
        }

        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2];
        }

        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0f
            ? number
            : null;
    }
}
