namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;
using System.Linq;

using SkiaSharp;

internal enum SvgPreserveAspectRatioAlign
{
    None,
    XMinYMin,
    XMidYMin,
    XMaxYMin,
    XMinYMid,
    XMidYMid,
    XMaxYMid,
    XMinYMax,
    XMidYMax,
    XMaxYMax,
}

/// <summary>
/// Computes the matrix that maps a `viewBox` onto a `width`x`height` viewport, honouring
/// `preserveAspectRatio`. Shared by the root &lt;svg&gt; (<see cref="Skia.SvgRasterizer"/>), a
/// nested &lt;svg&gt;, and a &lt;symbol&gt; referenced through &lt;use&gt; - all three establish a
/// viewport the same way.
/// </summary>
internal static class SvgViewBoxMapping
{
    public static (float MinX, float MinY, float Width, float Height)? ParseViewBox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 4)
        {
            return null;
        }

        var numbers = new float[4];

        for (var i = 0; i < 4; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        if (numbers[2] <= 0f || numbers[3] <= 0f)
        {
            return null;
        }

        return (numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    public static (SvgPreserveAspectRatioAlign Align, bool Meet) ParsePreserveAspectRatio(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (SvgPreserveAspectRatioAlign.XMidYMid, true);
        }

        var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var alignToken = tokens.FirstOrDefault(t => !t.Equals("defer", StringComparison.OrdinalIgnoreCase)) ?? "xMidYMid";

        var align = alignToken.ToLowerInvariant() switch
        {
            "none" => SvgPreserveAspectRatioAlign.None,
            "xminymin" => SvgPreserveAspectRatioAlign.XMinYMin,
            "xmidymin" => SvgPreserveAspectRatioAlign.XMidYMin,
            "xmaxymin" => SvgPreserveAspectRatioAlign.XMaxYMin,
            "xminymid" => SvgPreserveAspectRatioAlign.XMinYMid,
            "xmaxymid" => SvgPreserveAspectRatioAlign.XMaxYMid,
            "xminymax" => SvgPreserveAspectRatioAlign.XMinYMax,
            "xmidymax" => SvgPreserveAspectRatioAlign.XMidYMax,
            "xmaxymax" => SvgPreserveAspectRatioAlign.XMaxYMax,
            _ => SvgPreserveAspectRatioAlign.XMidYMid,
        };

        var meet = !tokens.Any(t => t.Equals("slice", StringComparison.OrdinalIgnoreCase));

        return (align, meet);
    }

    public static SKMatrix ComputeMatrix((float MinX, float MinY, float Width, float Height)? viewBox, float width, float height, SvgPreserveAspectRatioAlign align, bool meet)
    {
        if (viewBox is not { } box || box.Width <= 0f || box.Height <= 0f)
        {
            return SKMatrix.CreateIdentity();
        }

        float sx, sy;

        if (align == SvgPreserveAspectRatioAlign.None)
        {
            sx = width / box.Width;
            sy = height / box.Height;
        }
        else
        {
            var uniformScale = meet
                ? Math.Min(width / box.Width, height / box.Height)
                : Math.Max(width / box.Width, height / box.Height);
            sx = uniformScale;
            sy = uniformScale;
        }

        var scaledWidth = box.Width * sx;
        var scaledHeight = box.Height * sy;

        var alignTranslateX = align switch
        {
            SvgPreserveAspectRatioAlign.XMidYMid or SvgPreserveAspectRatioAlign.XMidYMin or SvgPreserveAspectRatioAlign.XMidYMax => (width - scaledWidth) / 2f,
            SvgPreserveAspectRatioAlign.XMaxYMid or SvgPreserveAspectRatioAlign.XMaxYMin or SvgPreserveAspectRatioAlign.XMaxYMax => width - scaledWidth,
            _ => 0f,
        };

        var alignTranslateY = align switch
        {
            SvgPreserveAspectRatioAlign.XMinYMid or SvgPreserveAspectRatioAlign.XMidYMid or SvgPreserveAspectRatioAlign.XMaxYMid => (height - scaledHeight) / 2f,
            SvgPreserveAspectRatioAlign.XMinYMax or SvgPreserveAspectRatioAlign.XMidYMax or SvgPreserveAspectRatioAlign.XMaxYMax => height - scaledHeight,
            _ => 0f,
        };

        // x' = sx*(x-minX) + translateX; y' = sy*(y-minY) + translateY, encoded directly rather
        // than composed from separate scale/translate matrices so the axis order can't be gotten
        // backwards (see SvgTransformParser's PostConcat ordering note for why that matters).
        return new SKMatrix(
            sx, 0f, alignTranslateX - (box.MinX * sx),
            0f, sy, alignTranslateY - (box.MinY * sy),
            0f, 0f, 1f);
    }
}
