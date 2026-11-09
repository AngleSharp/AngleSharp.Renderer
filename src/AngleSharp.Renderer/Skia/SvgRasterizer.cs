namespace AngleSharp.Renderer.Skia;

using System.Globalization;
using System.Text;

using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Renderer.Skia.Svg;

using SkiaSharp;

/// <summary>
/// Records SVG content into a <see cref="SvgVectorImage"/> by walking the DOM AngleSharp already
/// parsed and painting it directly with SkiaSharp (<see cref="SvgElementRenderer"/>) - no second
/// SVG parser is involved, so an inline &lt;svg&gt; the host document already parsed is never
/// re-parsed.
/// </summary>
internal static class SvgRasterizer
{
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
    /// AngleSharp's own HTML/foreign-content parser and records the resulting element tree.
    /// </summary>
    public static bool TryRecordMarkup(byte[] svgBytes, out SvgVectorImage? vectorImage)
    {
        vectorImage = null;

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

            return TryRecordElement(svgRoot, out vectorImage);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Records an SVG element that was already parsed as part of a larger document (an inline
    /// &lt;svg&gt; in the host HTML) - the DOM is walked directly, nothing is re-parsed.
    /// </summary>
    public static bool TryRecordElement(IElement svgRoot, out SvgVectorImage? vectorImage, SvgPageStyle? pageStyle = null)
    {
        vectorImage = null;

        try
        {
            var (viewBoxMatrix, width, height, contentViewport) = ResolveViewport(svgRoot);

            if (width <= 0f || height <= 0f)
            {
                return false;
            }

            var renderContext = SvgRenderContext.Build(svgRoot, pageStyle);
            var initialState = pageStyle is null
                ? SvgPaintState.Initial
                : SvgPaintState.Initial.ResolveDeclarations(pageStyle.Inherited, contentViewport);

            using var recorder = new SKPictureRecorder();
            var recording = recorder.BeginRecording(new SKRect(0f, 0f, width, height));
            recording.Concat(ref viewBoxMatrix);
            SvgElementRenderer.Render(recording, svgRoot, initialState, renderContext, contentViewport);
            vectorImage = new SvgVectorImage(recorder.EndRecording(), width, height);
            return true;
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
