namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Builds an <see cref="SKShader"/> from a &lt;linearGradient&gt;/&lt;radialGradient&gt; element,
/// resolving `href`/`xlink:href` chains for inherited stops and attributes the way SVG paint
/// servers do.
/// </summary>
internal static class SvgGradientBuilder
{
    public static SKShader? Build(IElement gradientElement, SKRect boundingBox, IReadOnlyDictionary<string, IElement> elementsById)
    {
        var stops = ResolveStops(gradientElement, elementsById);

        if (stops.Count == 0)
        {
            return null;
        }

        if (stops.Count == 1)
        {
            // A single-stop gradient paints as a solid color; SKShader.CreateLinearGradient needs
            // at least two color/position pairs to be well-defined.
            return SKShader.CreateColor(stops[0].Color);
        }

        var colors = new SKColor[stops.Count];
        var positions = new float[stops.Count];

        for (var i = 0; i < stops.Count; i++)
        {
            colors[i] = stops[i].Color;
            positions[i] = stops[i].Offset;
        }

        var isObjectBoundingBox = !string.Equals(
            GetInheritedAttribute(gradientElement, "gradientUnits", elementsById),
            "userSpaceOnUse",
            StringComparison.OrdinalIgnoreCase);

        var tileMode = GetInheritedAttribute(gradientElement, "spreadMethod", elementsById)?.ToLowerInvariant() switch
        {
            "reflect" => SKShaderTileMode.Mirror,
            "repeat" => SKShaderTileMode.Repeat,
            _ => SKShaderTileMode.Clamp,
        };

        var gradientTransform = SvgTransformParser.Parse(GetInheritedAttribute(gradientElement, "gradientTransform", elementsById));

        var isRadial = string.Equals(gradientElement.LocalName, "radialGradient", StringComparison.OrdinalIgnoreCase);

        return isRadial
            ? BuildRadial(gradientElement, boundingBox, isObjectBoundingBox, gradientTransform, colors, positions, tileMode, elementsById)
            : BuildLinear(gradientElement, boundingBox, isObjectBoundingBox, gradientTransform, colors, positions, tileMode, elementsById);
    }

    private static SKShader BuildLinear(
        IElement element,
        SKRect boundingBox,
        bool isObjectBoundingBox,
        SKMatrix gradientTransform,
        SKColor[] colors,
        float[] positions,
        SKShaderTileMode tileMode,
        IReadOnlyDictionary<string, IElement> elementsById)
    {
        var x1 = ResolveCoordinate(GetInheritedAttribute(element, "x1", elementsById), 0f, isObjectBoundingBox, boundingBox.Left, boundingBox.Width);
        var y1 = ResolveCoordinate(GetInheritedAttribute(element, "y1", elementsById), 0f, isObjectBoundingBox, boundingBox.Top, boundingBox.Height);
        var x2 = ResolveCoordinate(GetInheritedAttribute(element, "x2", elementsById), 1f, isObjectBoundingBox, boundingBox.Left, boundingBox.Width);
        var y2 = ResolveCoordinate(GetInheritedAttribute(element, "y2", elementsById), 0f, isObjectBoundingBox, boundingBox.Top, boundingBox.Height);

        var p1 = gradientTransform.MapPoint(x1, y1);
        var p2 = gradientTransform.MapPoint(x2, y2);

        return SKShader.CreateLinearGradient(p1, p2, colors, positions, tileMode);
    }

    private static SKShader BuildRadial(
        IElement element,
        SKRect boundingBox,
        bool isObjectBoundingBox,
        SKMatrix gradientTransform,
        SKColor[] colors,
        float[] positions,
        SKShaderTileMode tileMode,
        IReadOnlyDictionary<string, IElement> elementsById)
    {
        var cx = ResolveCoordinate(GetInheritedAttribute(element, "cx", elementsById), 0.5f, isObjectBoundingBox, boundingBox.Left, boundingBox.Width);
        var cy = ResolveCoordinate(GetInheritedAttribute(element, "cy", elementsById), 0.5f, isObjectBoundingBox, boundingBox.Top, boundingBox.Height);
        var r = ResolveCoordinate(GetInheritedAttribute(element, "r", elementsById), 0.5f, isObjectBoundingBox, 0f, Math.Max(boundingBox.Width, boundingBox.Height));

        var fxRaw = GetInheritedAttribute(element, "fx", elementsById);
        var fyRaw = GetInheritedAttribute(element, "fy", elementsById);
        var fx = fxRaw is null ? cx : ResolveCoordinate(fxRaw, 0.5f, isObjectBoundingBox, boundingBox.Left, boundingBox.Width);
        var fy = fyRaw is null ? cy : ResolveCoordinate(fyRaw, 0.5f, isObjectBoundingBox, boundingBox.Top, boundingBox.Height);

        var center = gradientTransform.MapPoint(cx, cy);
        var focal = gradientTransform.MapPoint(fx, fy);

        // Scale a unit radius through the transform so a non-uniform gradientTransform (or an
        // objectBoundingBox mapping onto a non-square box) still produces a roughly elliptical
        // falloff rather than only ever a uniformly-scaled circle.
        var edge = gradientTransform.MapPoint(cx + r, cy);
        var radius = Distance(center, edge);

        if (radius <= 0f)
        {
            return SKShader.CreateColor(colors[^1]);
        }

        return (focal.X == center.X && focal.Y == center.Y)
            ? SKShader.CreateRadialGradient(center, radius, colors, positions, tileMode)
            : SKShader.CreateTwoPointConicalGradient(focal, 0f, center, radius, colors, positions, tileMode);
    }

    private static float Distance(SKPoint a, SKPoint b) => (float)Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));

    private static float ResolveCoordinate(string? raw, float defaultFraction, bool isObjectBoundingBox, float boxOrigin, float boxSize)
    {
        var fraction = ParseFractionOrNumber(raw, defaultFraction);
        return isObjectBoundingBox ? boxOrigin + (fraction * boxSize) : fraction;
    }

    private static float ParseFractionOrNumber(string? raw, float defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        var trimmed = raw.Trim();

        if (trimmed.EndsWith('%') && float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100f;
        }

        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : defaultValue;
    }

    private static List<(float Offset, SKColor Color)> ResolveStops(IElement start, IReadOnlyDictionary<string, IElement> elementsById)
    {
        var visited = new HashSet<IElement>();
        var current = start;

        while (current is not null && visited.Add(current))
        {
            var stops = ReadStops(current);

            if (stops.Count > 0)
            {
                return stops;
            }

            current = SvgUrlReference.GetHref(current) is { } href && href.StartsWith('#') && elementsById.TryGetValue(href[1..], out var next)
                ? next
                : null;
        }

        return [];
    }

    private static List<(float Offset, SKColor Color)> ReadStops(IElement gradientElement)
    {
        var stops = new List<(float, SKColor)>();
        var previousOffset = 0f;

        foreach (var child in gradientElement.Children)
        {
            if (!string.Equals(child.LocalName, "stop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var offset = Math.Clamp(ParseFractionOrNumber(child.GetAttribute("offset"), previousOffset), 0f, 1f);
            offset = Math.Max(offset, previousOffset);
            previousOffset = offset;

            var declarations = ReadStopDeclarations(child);
            var colorValue = declarations.TryGetValue("stop-color", out var explicitColor) ? explicitColor : "black";
            SvgColorParsing.TryParsePaint(colorValue, out var color);

            var opacity = declarations.TryGetValue("stop-opacity", out var opacityValue) && float.TryParse(opacityValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedOpacity)
                ? Math.Clamp(parsedOpacity, 0f, 1f)
                : 1f;

            stops.Add((offset, color.WithAlpha((byte)Math.Round(color.Alpha * opacity))));
        }

        return stops;
    }

    private static Dictionary<string, string> ReadStopDeclarations(IElement stopElement)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (stopElement.GetAttribute("stop-color") is { Length: > 0 } stopColor)
        {
            declarations["stop-color"] = stopColor;
        }

        if (stopElement.GetAttribute("stop-opacity") is { Length: > 0 } stopOpacity)
        {
            declarations["stop-opacity"] = stopOpacity;
        }

        var style = stopElement.GetAttribute("style");

        if (string.IsNullOrWhiteSpace(style))
        {
            return declarations;
        }

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = declaration.IndexOf(':');

            if (colonIndex <= 0)
            {
                continue;
            }

            declarations[declaration[..colonIndex].Trim()] = declaration[(colonIndex + 1)..].Trim();
        }

        return declarations;
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
