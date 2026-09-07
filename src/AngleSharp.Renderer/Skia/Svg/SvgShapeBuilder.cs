namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Builds an <see cref="SKPath"/> for a basic SVG shape element, resolving `x`/`y`/`width`/
/// `height`/`cx`/`cy`/`r`/`rx`/`ry` against the current viewport so percentage lengths work.
/// Shared by shape rendering, clip-path content, and bounding-box computation for `mask`/gradient
/// object-bounding-box units, so all three treat geometry the same way.
/// </summary>
internal static class SvgShapeBuilder
{
    public static SKPath? Build(IElement element, SvgViewport viewport) => element.LocalName.ToLowerInvariant() switch
    {
        "rect" => BuildRect(element, viewport),
        "circle" => BuildCircle(element, viewport),
        "ellipse" => BuildEllipse(element, viewport),
        "line" => BuildLine(element, viewport),
        "polyline" => BuildPoly(element, close: false),
        "polygon" => BuildPoly(element, close: true),
        "path" when element.GetAttribute("d") is { Length: > 0 } d => SvgPathDataParser.Parse(d),
        _ => null,
    };

    public static SKPath BuildRect(IElement element, SvgViewport viewport)
    {
        var x = ReadX(element, "x", viewport);
        var y = ReadY(element, "y", viewport);
        var width = ReadOptionalX(element, "width", viewport) ?? 0f;
        var height = ReadOptionalY(element, "height", viewport) ?? 0f;
        var rx = ReadOptionalX(element, "rx", viewport);
        var ry = ReadOptionalY(element, "ry", viewport) ?? rx;
        rx ??= ry;

        var path = new SKPath();

        if (width <= 0f || height <= 0f)
        {
            return path;
        }

        if (rx is > 0f && ry is > 0f)
        {
            path.AddRoundRect(new SKRect(x, y, x + width, y + height), Math.Min(rx.Value, width / 2f), Math.Min(ry.Value, height / 2f));
        }
        else
        {
            path.AddRect(new SKRect(x, y, x + width, y + height));
        }

        return path;
    }

    public static SKPath BuildCircle(IElement element, SvgViewport viewport)
    {
        var cx = ReadX(element, "cx", viewport);
        var cy = ReadY(element, "cy", viewport);
        var r = SvgLength.Parse(element.GetAttribute("r"), viewport.DiagonalReference, 0f);

        var path = new SKPath();

        if (r > 0f)
        {
            path.AddCircle(cx, cy, r);
        }

        return path;
    }

    public static SKPath BuildEllipse(IElement element, SvgViewport viewport)
    {
        var cx = ReadX(element, "cx", viewport);
        var cy = ReadY(element, "cy", viewport);
        var rx = ReadX(element, "rx", viewport);
        var ry = ReadY(element, "ry", viewport);

        var path = new SKPath();

        if (rx > 0f && ry > 0f)
        {
            path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
        }

        return path;
    }

    public static SKPath BuildLine(IElement element, SvgViewport viewport)
    {
        var path = new SKPath();
        path.MoveTo(ReadX(element, "x1", viewport), ReadY(element, "y1", viewport));
        path.LineTo(ReadX(element, "x2", viewport), ReadY(element, "y2", viewport));
        return path;
    }

    private static SKPath BuildPoly(IElement element, bool close)
    {
        var path = new SKPath();
        var points = ParsePoints(element.GetAttribute("points"));

        if (points.Count == 0)
        {
            return path;
        }

        path.MoveTo(points[0]);

        for (var i = 1; i < points.Count; i++)
        {
            path.LineTo(points[i]);
        }

        if (close)
        {
            path.Close();
        }

        return path;
    }

    private static List<SKPoint> ParsePoints(string? value)
    {
        var points = new List<SKPoint>();

        if (string.IsNullOrWhiteSpace(value))
        {
            return points;
        }

        var tokens = value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<float>(tokens.Length);

        foreach (var token in tokens)
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number);
            }
        }

        for (var i = 0; i + 1 < numbers.Count; i += 2)
        {
            points.Add(new SKPoint(numbers[i], numbers[i + 1]));
        }

        return points;
    }

    public static float ReadX(IElement element, string attribute, SvgViewport viewport) => SvgLength.Parse(element.GetAttribute(attribute), viewport.Width, 0f);

    public static float ReadY(IElement element, string attribute, SvgViewport viewport) => SvgLength.Parse(element.GetAttribute(attribute), viewport.Height, 0f);

    public static float? ReadOptionalX(IElement element, string attribute, SvgViewport viewport) => SvgLength.ParseOptional(element.GetAttribute(attribute), viewport.Width);

    public static float? ReadOptionalY(IElement element, string attribute, SvgViewport viewport) => SvgLength.ParseOptional(element.GetAttribute(attribute), viewport.Height);
}
