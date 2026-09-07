namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;
using System.Linq;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Builds an <see cref="SKImageFilter"/> from a &lt;filter&gt; element's primitive chain. Supports
/// the common `feGaussianBlur`/`feOffset`/`feMerge`/`feColorMatrix`/`feDropShadow` primitives,
/// each composed via SkiaSharp's own filter graph (<see cref="SKImageFilter"/> is itself a DAG of
/// inputs, so this only has to translate primitives one at a time, not build a compositor).
/// An unsupported primitive (feFlood, feComposite, feTurbulence, feDisplacementMap, feTile,
/// feImage, feComponentTransfer, feConvolveMatrix, feDiffuseLighting, feSpecularLighting,
/// feMorphology) passes its resolved input through unchanged rather than being dropped, so later
/// primitives in the same chain still have something to work with.
/// </summary>
internal static class SvgFilterBuilder
{
    public static SKImageFilter? Build(IElement filterElement)
    {
        var results = new Dictionary<string, SKImageFilter?>(StringComparer.Ordinal);
        SKImageFilter? last = null;
        var hasPrimitive = false;

        foreach (var primitive in filterElement.Children)
        {
            var tag = primitive.LocalName.ToLowerInvariant();

            if (tag == "femerge")
            {
                last = BuildMerge(primitive, results, last);
            }
            else
            {
                var input = ResolveInput(primitive.GetAttribute("in"), results, last);

                last = tag switch
                {
                    "fegaussianblur" => BuildGaussianBlur(primitive, input),
                    "feoffset" => BuildOffset(primitive, input),
                    "fecolormatrix" => BuildColorMatrix(primitive, input),
                    "fedropshadow" => BuildDropShadow(primitive, input),
                    _ => input,
                };
            }

            hasPrimitive = true;

            var resultName = primitive.GetAttribute("result");

            if (!string.IsNullOrWhiteSpace(resultName))
            {
                results[resultName] = last;
            }
        }

        return hasPrimitive ? last : null;
    }

    private static SKImageFilter? ResolveInput(string? inName, Dictionary<string, SKImageFilter?> results, SKImageFilter? last)
    {
        if (string.IsNullOrWhiteSpace(inName))
        {
            return last;
        }

        // SourceAlpha (an alpha-only copy of the element's own content) is approximated as
        // SourceGraphic - a documented simplification, not a distinction this builder makes.
        if (inName is "SourceGraphic" or "SourceAlpha")
        {
            return null;
        }

        return results.TryGetValue(inName, out var named) ? named : last;
    }

    private static SKImageFilter BuildGaussianBlur(IElement primitive, SKImageFilter? input)
    {
        var (sigmaX, sigmaY) = ParseStdDeviation(primitive.GetAttribute("stdDeviation"));
        return SKImageFilter.CreateBlur(sigmaX, sigmaY, input);
    }

    private static SKImageFilter BuildOffset(IElement primitive, SKImageFilter? input)
    {
        var dx = ParseFloat(primitive.GetAttribute("dx"), 0f);
        var dy = ParseFloat(primitive.GetAttribute("dy"), 0f);
        return SKImageFilter.CreateOffset(dx, dy, input);
    }

    private static SKImageFilter BuildMerge(IElement primitive, Dictionary<string, SKImageFilter?> results, SKImageFilter? last)
    {
        var nodes = primitive.Children
            .Where(child => string.Equals(child.LocalName, "feMergeNode", StringComparison.OrdinalIgnoreCase))
            .Select(node => ResolveInput(node.GetAttribute("in"), results, last))
            .ToArray();

        return nodes.Length > 0 ? SKImageFilter.CreateMerge(nodes) : (last ?? SKImageFilter.CreateOffset(0f, 0f));
    }

    private static SKImageFilter BuildColorMatrix(IElement primitive, SKImageFilter? input)
    {
        var type = primitive.GetAttribute("type");
        var values = primitive.GetAttribute("values");

        var matrix = type?.ToLowerInvariant() switch
        {
            "saturate" => CreateSaturateMatrix(ParseFloat(values, 1f)),
            "luminancetoalpha" => LuminanceToAlphaMatrix,
            "matrix" when TryParseMatrixValues(values, out var explicitMatrix) => explicitMatrix,
            _ => IdentityMatrix,
        };

        using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
        return SKImageFilter.CreateColorFilter(colorFilter, input);
    }

    private static SKImageFilter BuildDropShadow(IElement primitive, SKImageFilter? input)
    {
        var dx = ParseFloat(primitive.GetAttribute("dx"), 2f);
        var dy = ParseFloat(primitive.GetAttribute("dy"), 2f);
        var (sigmaX, sigmaY) = ParseStdDeviation(primitive.GetAttribute("stdDeviation"), defaultValue: 2f);

        var floodColorRaw = primitive.GetAttribute("flood-color");
        var color = SvgColorParsing.TryParsePaint(string.IsNullOrWhiteSpace(floodColorRaw) ? "black" : floodColorRaw, out var parsed) ? parsed : SKColors.Black;

        if (float.TryParse(primitive.GetAttribute("flood-opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var floodOpacity))
        {
            color = color.WithAlpha((byte)Math.Round(color.Alpha * Math.Clamp(floodOpacity, 0f, 1f)));
        }

        return SKImageFilter.CreateDropShadow(dx, dy, sigmaX, sigmaY, color, input);
    }

    private static (float SigmaX, float SigmaY) ParseStdDeviation(string? value, float defaultValue = 2f)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (defaultValue, defaultValue);
        }

        var parts = value.Trim().Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var x = parts.Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px) ? px : defaultValue;
        var y = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py) ? py : x;

        return (Math.Max(0f, x), Math.Max(0f, y));
    }

    private static float ParseFloat(string? value, float defaultValue) =>
        !string.IsNullOrWhiteSpace(value) && float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;

    private static bool TryParseMatrixValues(string? value, out float[] matrix)
    {
        matrix = IdentityMatrix;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = value.Trim().Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length != 20)
        {
            return false;
        }

        var parsed = new float[20];

        for (var i = 0; i < 20; i++)
        {
            if (!float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        matrix = parsed;
        return true;
    }

    private static float[] CreateSaturateMatrix(float s)
    {
        // The SVG spec's saturate color matrix (a standard luminance-preserving desaturation).
        return
        [
            0.213f + (0.787f * s), 0.715f - (0.715f * s), 0.072f - (0.072f * s), 0f, 0f,
            0.213f - (0.213f * s), 0.715f + (0.285f * s), 0.072f - (0.072f * s), 0f, 0f,
            0.213f - (0.213f * s), 0.715f - (0.715f * s), 0.072f + (0.928f * s), 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
        ];
    }

    private static readonly float[] IdentityMatrix =
    [
        1f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    private static readonly float[] LuminanceToAlphaMatrix =
    [
        0f, 0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f, 0f,
        0.2125f, 0.7154f, 0.0721f, 0f, 0f,
    ];
}
