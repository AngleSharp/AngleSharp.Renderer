namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;
using System.Linq;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Builds an <see cref="SKImageFilter"/> from a &lt;filter&gt; element's primitive chain. Supports
/// the common `feGaussianBlur`/`feOffset`/`feMerge`/`feColorMatrix`/`feDropShadow` primitives plus
/// `feFlood`/`feComposite`/`feMorphology`/`feComponentTransfer` (linear sub-functions only), each
/// composed via SkiaSharp's own filter graph (<see cref="SKImageFilter"/> is itself a DAG of
/// inputs, so this only has to translate primitives one at a time, not build a compositor).
/// An unsupported primitive (feTurbulence, feDisplacementMap, feTile, feImage, feConvolveMatrix,
/// feDiffuseLighting, feSpecularLighting, or a feComponentTransfer sub-function using `table`/
/// `discrete`/`gamma` rather than `linear`) passes its resolved input through unchanged rather
/// than being dropped, so later primitives in the same chain still have something to work with.
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
            else if (tag == "fecomposite")
            {
                last = BuildComposite(primitive, results, last);
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
                    "feflood" => BuildFlood(primitive, input),
                    "femorphology" => BuildMorphology(primitive, input),
                    "fecomponenttransfer" => BuildComponentTransfer(primitive, input),
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

    /// <summary>
    /// `feFlood` fills its output with a solid `flood-color`/`flood-opacity` - since this
    /// architecture has no "paint an infinite flood, then let the filter region clip it" primitive
    /// to build on, it is approximated as a color remap of whatever the resolved input already
    /// covers (`SKColorFilter.CreateBlendMode(color, SKBlendMode.Src)`, replacing every covered
    /// pixel's color outright while leaving its own alpha/shape as-is) rather than a true
    /// region-filling flood - close enough for the common case of flooding a shape that already
    /// has its own opaque coverage (e.g. `feFlood` feeding into `feComposite`/`feBlend` against
    /// `SourceGraphic`), though not a fully spec-accurate infinite flood.
    /// </summary>
    private static SKImageFilter BuildFlood(IElement primitive, SKImageFilter? input)
    {
        var floodColorRaw = primitive.GetAttribute("flood-color");
        var color = SvgColorParsing.TryParsePaint(string.IsNullOrWhiteSpace(floodColorRaw) ? "black" : floodColorRaw, out var parsed) ? parsed : SKColors.Black;

        if (float.TryParse(primitive.GetAttribute("flood-opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var floodOpacity))
        {
            color = color.WithAlpha((byte)Math.Round(color.Alpha * Math.Clamp(floodOpacity, 0f, 1f)));
        }

        using var colorFilter = SKColorFilter.CreateBlendMode(color, SKBlendMode.Src);
        return SKImageFilter.CreateColorFilter(colorFilter, input);
    }

    /// <summary>
    /// `feComposite` combines two inputs (`in` over/under `in2`, per the Porter-Duff `operator`,
    /// or the `arithmetic` operator's own `k1..k4` formula) - the one primitive in this builder
    /// that genuinely needs a *second* named input, so it bypasses the single-`in`-resolution
    /// path <see cref="Build"/> uses for every other primitive, mirroring how `feMerge` already
    /// needed to for the same reason.
    /// </summary>
    private static SKImageFilter BuildComposite(IElement primitive, Dictionary<string, SKImageFilter?> results, SKImageFilter? last)
    {
        var input = ResolveInput(primitive.GetAttribute("in"), results, last);
        var input2 = ResolveInput(primitive.GetAttribute("in2"), results, last);
        var op = primitive.GetAttribute("operator")?.Trim().ToLowerInvariant();

        if (op == "arithmetic")
        {
            // SVG's own formula is `result = k1*i1*i2 + k2*i1 + k3*i2 + k4`, i1 = `in`
            // (foreground), i2 = `in2` (background) - matching CreateArithmetic's own
            // `k1*foreground*background + k2*foreground + k3*background + k4` exactly.
            var k1 = ParseFloat(primitive.GetAttribute("k1"), 0f);
            var k2 = ParseFloat(primitive.GetAttribute("k2"), 0f);
            var k3 = ParseFloat(primitive.GetAttribute("k3"), 0f);
            var k4 = ParseFloat(primitive.GetAttribute("k4"), 0f);
            return SKImageFilter.CreateArithmetic(k1, k2, k3, k4, false, input2, input, null);
        }

        // `in` (Src) composites *over*/*in*/*out*/*atop*/*xor* `in2` (Dst) - Skia's own
        // background/foreground blend-mode convention maps directly onto Dst/Src.
        var blendMode = op switch
        {
            "in" => SKBlendMode.SrcIn,
            "out" => SKBlendMode.SrcOut,
            "atop" => SKBlendMode.SrcATop,
            "xor" => SKBlendMode.Xor,
            _ => SKBlendMode.SrcOver,
        };

        return SKImageFilter.CreateBlendMode(blendMode, input2, input, null);
    }

    /// <summary>
    /// `feMorphology` grows (`dilate`, the default) or shrinks (`erode`) the input's own opaque
    /// region by `radius` - both map directly onto Skia's own identically-named filters.
    /// </summary>
    private static SKImageFilter BuildMorphology(IElement primitive, SKImageFilter? input)
    {
        var (radiusX, radiusY) = ParseStdDeviation(primitive.GetAttribute("radius"), defaultValue: 0f);
        var isErode = string.Equals(primitive.GetAttribute("operator")?.Trim(), "erode", StringComparison.OrdinalIgnoreCase);

        return isErode
            ? SKImageFilter.CreateErode(radiusX, radiusY, input)
            : SKImageFilter.CreateDilate(radiusX, radiusY, input);
    }

    /// <summary>
    /// `feComponentTransfer` remaps each color channel independently through its own
    /// `feFuncR`/`feFuncG`/`feFuncB`/`feFuncA` sub-function - only `type="linear"` (`slope`/
    /// `intercept`, a per-channel scale-and-offset) is implemented, since it maps directly onto a
    /// single diagonal entry (and its own offset column) of the same color-matrix machinery
    /// `feColorMatrix` already established above; `table`/`discrete`/`gamma` would each need their
    /// own genuinely different per-pixel lookup/curve evaluation this renderer does not implement,
    /// so a sub-function using one of those is simply left as the identity transform for that
    /// channel (matching this builder's general "leave unsupported constructs as a no-op rather
    /// than breaking the chain" policy).
    /// </summary>
    private static SKImageFilter BuildComponentTransfer(IElement primitive, SKImageFilter? input)
    {
        var matrix = (float[])IdentityMatrix.Clone();

        foreach (var functionNode in primitive.Children)
        {
            var channelIndex = functionNode.LocalName.ToLowerInvariant() switch
            {
                "fefuncr" => 0,
                "fefuncg" => 1,
                "fefuncb" => 2,
                "fefunca" => 3,
                _ => -1,
            };

            if (channelIndex < 0 || !string.Equals(functionNode.GetAttribute("type")?.Trim(), "linear", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slope = ParseFloat(functionNode.GetAttribute("slope"), 1f);
            var intercept = ParseFloat(functionNode.GetAttribute("intercept"), 0f);

            matrix[(channelIndex * 5) + channelIndex] = slope;
            // The matrix's own offset column already operates in 0-255 space (see IdentityMatrix/
            // LuminanceToAlphaMatrix, whose own offsets are always 0) - `intercept` is SVG's
            // 0-1 fraction of full range, so it is scaled up to match.
            matrix[(channelIndex * 5) + 4] = intercept * 255f;
        }

        using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
        return SKImageFilter.CreateColorFilter(colorFilter, input);
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
