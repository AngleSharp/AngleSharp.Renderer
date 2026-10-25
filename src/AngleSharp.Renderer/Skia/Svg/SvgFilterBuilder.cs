namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;
using System.Linq;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// The image filter a &lt;filter&gt; element applies to one element, and the filter region its
/// result is clipped to.
/// </summary>
internal readonly record struct SvgFilter(SKImageFilter? ImageFilter, SKRect Region);

/// <summary>
/// Builds an <see cref="SKImageFilter"/> from a &lt;filter&gt; element's primitive chain, composed
/// via SkiaSharp's own filter graph (<see cref="SKImageFilter"/> is itself a DAG of inputs, so this
/// only translates primitives one at a time). Every primitive is supported: `feBlend`,
/// `feColorMatrix`, `feComponentTransfer`, `feComposite`, `feConvolveMatrix`,
/// `feDiffuseLighting`/`feSpecularLighting` (distant, point and spot lights), `feDisplacementMap`,
/// `feDropShadow`, `feFlood`, `feGaussianBlur`, `feImage` (data URIs and `#id` element references),
/// `feMerge`, `feMorphology`, `feOffset`, `feTile` and `feTurbulence`. The filter region
/// (`x`/`y`/`width`/`height`, `filterUnits`) and primitive subregions (`primitiveUnits`) are crop
/// rectangles; `SourceGraphic` and `SourceAlpha` are inputs. Each primitive works in its
/// `color-interpolation-filters` space (linearRGB by default): inputs are converted into it and
/// the final result back to sRGB, with flood and lighting colors linearized to match. Not modeled:
/// the `BackgroundImage`/`FillPaint`/`StrokePaint` inputs (treated as `SourceGraphic`) and
/// `kernelUnitLength` (kernels run per raster pixel, which oversampling makes finer than a user unit).
/// </summary>
internal static class SvgFilterBuilder
{
    public static SvgFilter? Build(IElement filterElement, IElement target, SvgRenderContext context, SvgViewport viewport)
    {
        var bounds = SvgGeometry.ComputeBounds(target, viewport, context);
        var region = ResolveFilterRegion(filterElement, bounds, viewport);
        var primitiveUnitsAreBoundingBox = string.Equals(filterElement.GetAttribute("primitiveUnits")?.Trim(), "objectBoundingBox", StringComparison.OrdinalIgnoreCase);
        var results = new Dictionary<string, FilterResult>(StringComparer.Ordinal);
        FilterResult? last = null;
        var source = new FilterResult(null, region, Linear: false);

        FilterResult Input(string? name)
        {
            switch (name?.Trim())
            {
                case null or "":
                    return last ?? source;
                case "SourceGraphic" or "BackgroundImage" or "BackgroundAlpha" or "FillPaint" or "StrokePaint":
                    return source;
                case "SourceAlpha":
                    return source with { Filter = SKImageFilter.CreateColorFilter(SKColorFilter.CreateColorMatrix(SourceAlphaMatrix)) };
                default:
                    return results.TryGetValue(name.Trim(), out var named) ? named : last ?? source;
            }
        }

        foreach (var primitive in filterElement.Children)
        {
            var tag = primitive.LocalName.ToLowerInvariant();

            if (!tag.StartsWith("fe", StringComparison.Ordinal))
            {
                continue;
            }

            var linear = UsesLinearRgb(primitive, filterElement);
            var input = Input(primitive.GetAttribute("in"));
            var inputFilter = ConvertSpace(input, linear);
            SKImageFilter? SecondInput() => ConvertSpace(Input(primitive.GetAttribute("in2")), linear);
            var subregion = ResolveSubregion(primitive, region, bounds, viewport, primitiveUnitsAreBoundingBox);
            var crop = subregion;

            var result = tag switch
            {
                // Geometric primitives move pixels without mixing colors, so their output stays in
                // the input's color space; feImage paints its image as it is (sRGB).
                "feoffset" => input with { Filter = BuildOffset(primitive, input.Filter, crop), Subregion = subregion },
                "fetile" => input with { Filter = SKImageFilter.CreateTile(input.Subregion, subregion, input.Filter), Subregion = subregion },
                "fedisplacementmap" => input with { Filter = BuildDisplacementMap(primitive, input.Filter, SecondInput(), crop), Subregion = subregion },
                "feimage" => new FilterResult(BuildImage(primitive, subregion, context, viewport), subregion, Linear: false),
                _ => new FilterResult(tag switch
                {
                    "feblend" => BuildBlend(primitive, inputFilter, SecondInput(), crop),
                    "fecolormatrix" => BuildColorMatrix(primitive, inputFilter, crop),
                    "fecomponenttransfer" => BuildComponentTransfer(primitive, inputFilter, crop),
                    "fecomposite" => BuildComposite(primitive, inputFilter, SecondInput(), crop),
                    "feconvolvematrix" => BuildConvolveMatrix(primitive, inputFilter, crop),
                    "fediffuselighting" => BuildLighting(primitive, inputFilter, crop, specular: false, linear),
                    "fespecularlighting" => BuildLighting(primitive, inputFilter, crop, specular: true, linear),
                    "fedropshadow" => BuildDropShadow(primitive, inputFilter, crop, linear),
                    "feflood" => BuildFlood(primitive, crop, linear),
                    "fegaussianblur" => BuildGaussianBlur(primitive, inputFilter, crop),
                    "femerge" => BuildMerge(primitive, name => ConvertSpace(Input(name), linear), crop),
                    "femorphology" => BuildMorphology(primitive, inputFilter, crop),
                    "feturbulence" => BuildTurbulence(primitive, crop),
                    _ => inputFilter,
                }, subregion, linear),
            };

            last = result;
            var resultName = primitive.GetAttribute("result");

            if (!string.IsNullOrWhiteSpace(resultName))
            {
                results[resultName.Trim()] = result;
            }
        }

        return last is { } final ? new SvgFilter(ConvertSpace(final, linear: false), region) : null;
    }

    /// <summary>A primitive's output: its filter, subregion, and the color space its colors are in.</summary>
    private readonly record struct FilterResult(SKImageFilter? Filter, SKRect Subregion, bool Linear);

    /// <summary>
    /// `color-interpolation-filters` of a primitive (attribute or `style`, inherited from the
    /// &lt;filter&gt;): `linearRGB`, the initial value, or `sRGB` (also used for `auto`).
    /// </summary>
    private static bool UsesLinearRgb(IElement primitive, IElement filterElement)
    {
        var value = ReadPresentation(primitive, "color-interpolation-filters")?.Trim();

        if (string.IsNullOrEmpty(value) || value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            value = ReadPresentation(filterElement, "color-interpolation-filters")?.Trim();
        }

        return string.IsNullOrEmpty(value) || value.Equals("linearRGB", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A result's filter, with its colors converted into the requested color space.</summary>
    private static SKImageFilter? ConvertSpace(FilterResult result, bool linear)
    {
        if (result.Linear == linear)
        {
            return result.Filter;
        }

        // Lookup tables act on unpremultiplied colors, as the transfer functions require; alpha is kept.
        var table = linear ? SrgbToLinearTable : LinearToSrgbTable;
        using var conversion = SKColorFilter.CreateTable(IdentityTable, table, table, table);
        return SKImageFilter.CreateColorFilter(conversion, result.Filter);
    }

    private static readonly byte[] IdentityTable = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private static readonly byte[] SrgbToLinearTable = Enumerable.Range(0, 256).Select(i => (byte)Math.Round(SrgbToLinear(i / 255d) * 255d)).ToArray();

    private static readonly byte[] LinearToSrgbTable = Enumerable.Range(0, 256).Select(i => (byte)Math.Round(LinearToSrgb(i / 255d) * 255d)).ToArray();

    private static double SrgbToLinear(double c) => c <= 0.04045d ? c / 12.92d : Math.Pow((c + 0.055d) / 1.055d, 2.4d);

    private static double LinearToSrgb(double l) => l <= 0.0031308d ? l * 12.92d : (1.055d * Math.Pow(l, 1d / 2.4d)) - 0.055d;

    /// <summary>An sRGB color expressed in linear RGB, for a primitive operating in linearRGB.</summary>
    private static SKColor ToSpace(SKColor color, bool linear)
    {
        if (!linear)
        {
            return color;
        }

        return new SKColor(SrgbToLinearTable[color.Red], SrgbToLinearTable[color.Green], SrgbToLinearTable[color.Blue], color.Alpha);
    }

    /// <summary>
    /// The filter region: `x`/`y`/`width`/`height` (default -10%/-10%/120%/120%) as fractions of
    /// the element's bounding box (`filterUnits="objectBoundingBox"`, the default) or as user-space
    /// lengths (`userSpaceOnUse`, percentages of the viewport). Without a bounding box the region
    /// falls back to the viewport.
    /// </summary>
    private static SKRect ResolveFilterRegion(IElement filterElement, SKRect? bounds, SvgViewport viewport)
    {
        var userSpace = string.Equals(filterElement.GetAttribute("filterUnits")?.Trim(), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);

        if (userSpace)
        {
            var x = SvgLength.Parse(filterElement.GetAttribute("x"), viewport.Width, -0.1f * viewport.Width);
            var y = SvgLength.Parse(filterElement.GetAttribute("y"), viewport.Height, -0.1f * viewport.Height);
            var width = SvgLength.Parse(filterElement.GetAttribute("width"), viewport.Width, 1.2f * viewport.Width);
            var height = SvgLength.Parse(filterElement.GetAttribute("height"), viewport.Height, 1.2f * viewport.Height);
            return SKRect.Create(x, y, width, height);
        }

        if (bounds is not { } box)
        {
            return SKRect.Create(0f, 0f, viewport.Width, viewport.Height);
        }

        return SKRect.Create(
            box.Left + (ParseFraction(filterElement.GetAttribute("x"), -0.1f) * box.Width),
            box.Top + (ParseFraction(filterElement.GetAttribute("y"), -0.1f) * box.Height),
            ParseFraction(filterElement.GetAttribute("width"), 1.2f) * box.Width,
            ParseFraction(filterElement.GetAttribute("height"), 1.2f) * box.Height);
    }

    /// <summary>
    /// A primitive's subregion: its own `x`/`y`/`width`/`height` where given (user-space lengths,
    /// or bounding-box fractions under `primitiveUnits="objectBoundingBox"`), the filter region's
    /// edges otherwise.
    /// </summary>
    private static SKRect ResolveSubregion(IElement primitive, SKRect region, SKRect? bounds, SvgViewport viewport, bool boundingBoxUnits)
    {
        float Resolve(string name, float fallback, float reference, float origin, float boxSize)
        {
            var raw = primitive.GetAttribute(name);

            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            return boundingBoxUnits && bounds is not null
                ? origin + (ParseFraction(raw, 0f) * boxSize)
                : SvgLength.Parse(raw, reference, fallback);
        }

        var box = bounds ?? region;
        var x = Resolve("x", region.Left, viewport.Width, box.Left, box.Width);
        var y = Resolve("y", region.Top, viewport.Height, box.Top, box.Height);
        var width = Resolve("width", region.Right - x, viewport.Width, 0f, box.Width);
        var height = Resolve("height", region.Bottom - y, viewport.Height, 0f, box.Height);
        return SKRect.Intersect(SKRect.Create(x, y, width, height), region);
    }

    private static SKImageFilter BuildGaussianBlur(IElement primitive, SKImageFilter? input, SKRect crop)
    {
        var (sigmaX, sigmaY) = ParsePair(primitive.GetAttribute("stdDeviation"), 0f);
        return SKImageFilter.CreateBlur(sigmaX, sigmaY, input, crop);
    }

    private static SKImageFilter BuildOffset(IElement primitive, SKImageFilter? input, SKRect crop) =>
        SKImageFilter.CreateOffset(ParseFloat(primitive.GetAttribute("dx"), 0f), ParseFloat(primitive.GetAttribute("dy"), 0f), input, crop);

    private static SKImageFilter BuildMerge(IElement primitive, Func<string?, SKImageFilter?> input, SKRect crop)
    {
        var nodes = primitive.Children
            .Where(child => string.Equals(child.LocalName, "feMergeNode", StringComparison.OrdinalIgnoreCase))
            .Select(node => input(node.GetAttribute("in")))
            .ToArray();

        return nodes.Length > 0 ? SKImageFilter.CreateMerge(nodes, crop) : Transparent(crop);
    }

    private static SKImageFilter BuildColorMatrix(IElement primitive, SKImageFilter? input, SKRect crop)
    {
        var type = primitive.GetAttribute("type")?.Trim().ToLowerInvariant();
        var values = primitive.GetAttribute("values");

        var matrix = type switch
        {
            "saturate" => CreateSaturateMatrix(ParseFloat(values, 1f)),
            "huerotate" => CreateHueRotateMatrix(ParseFloat(values, 0f)),
            "luminancetoalpha" => LuminanceToAlphaMatrix,
            _ when TryParseNumbers(values, 20, out var explicitMatrix) => explicitMatrix,
            _ => IdentityMatrix,
        };

        using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
        return SKImageFilter.CreateColorFilter(colorFilter, input, crop);
    }

    private static SKImageFilter BuildDropShadow(IElement primitive, SKImageFilter? input, SKRect crop, bool linear)
    {
        var dx = ParseFloat(primitive.GetAttribute("dx"), 2f);
        var dy = ParseFloat(primitive.GetAttribute("dy"), 2f);
        var (sigmaX, sigmaY) = ParsePair(primitive.GetAttribute("stdDeviation"), 2f);
        return SKImageFilter.CreateDropShadow(dx, dy, sigmaX, sigmaY, ToSpace(ParseFloodColor(primitive), linear), input, crop);
    }

    /// <summary>`feFlood` fills its whole subregion with `flood-color`/`flood-opacity`.</summary>
    private static SKImageFilter BuildFlood(IElement primitive, SKRect crop, bool linear)
    {
        using var paint = new SKPaint { Color = ToSpace(ParseFloodColor(primitive), linear) };
        return SKImageFilter.CreatePaint(paint, crop);
    }

    private static SKImageFilter Transparent(SKRect crop)
    {
        using var paint = new SKPaint { Color = SKColors.Transparent };
        return SKImageFilter.CreatePaint(paint, crop);
    }

    private static SKColor ParseFloodColor(IElement primitive)
    {
        var raw = ReadPresentation(primitive, "flood-color");
        var color = SvgColorParsing.TryParsePaint(string.IsNullOrWhiteSpace(raw) ? "black" : raw, out var parsed) ? parsed : SKColors.Black;

        if (float.TryParse(ReadPresentation(primitive, "flood-opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
        {
            color = color.WithAlpha((byte)Math.Round(color.Alpha * Math.Clamp(opacity, 0f, 1f)));
        }

        return color;
    }

    /// <summary>
    /// `feComposite`: `in` over/in/out/atop/xor `in2`, or `arithmetic`'s
    /// `k1*i1*i2 + k2*i1 + k3*i2 + k4` (i1 = `in`, the foreground; i2 = `in2`, the background).
    /// </summary>
    private static SKImageFilter BuildComposite(IElement primitive, SKImageFilter? input, SKImageFilter? input2, SKRect crop)
    {
        var op = primitive.GetAttribute("operator")?.Trim().ToLowerInvariant();

        if (op == "arithmetic")
        {
            return SKImageFilter.CreateArithmetic(
                ParseFloat(primitive.GetAttribute("k1"), 0f),
                ParseFloat(primitive.GetAttribute("k2"), 0f),
                ParseFloat(primitive.GetAttribute("k3"), 0f),
                ParseFloat(primitive.GetAttribute("k4"), 0f),
                false,
                input2,
                input,
                crop);
        }

        var blendMode = op switch
        {
            "in" => SKBlendMode.SrcIn,
            "out" => SKBlendMode.SrcOut,
            "atop" => SKBlendMode.SrcATop,
            "xor" => SKBlendMode.Xor,
            "lighter" => SKBlendMode.Plus,
            _ => SKBlendMode.SrcOver,
        };

        return SKImageFilter.CreateBlendMode(blendMode, input2, input, crop);
    }

    /// <summary>`feBlend`: `in` blended onto `in2` with any CSS blend mode.</summary>
    private static SKImageFilter BuildBlend(IElement primitive, SKImageFilter? input, SKImageFilter? input2, SKRect crop)
    {
        var mode = primitive.GetAttribute("mode")?.Trim().ToLowerInvariant() switch
        {
            "multiply" => SKBlendMode.Multiply,
            "screen" => SKBlendMode.Screen,
            "darken" => SKBlendMode.Darken,
            "lighten" => SKBlendMode.Lighten,
            "overlay" => SKBlendMode.Overlay,
            "color-dodge" => SKBlendMode.ColorDodge,
            "color-burn" => SKBlendMode.ColorBurn,
            "hard-light" => SKBlendMode.HardLight,
            "soft-light" => SKBlendMode.SoftLight,
            "difference" => SKBlendMode.Difference,
            "exclusion" => SKBlendMode.Exclusion,
            "hue" => SKBlendMode.Hue,
            "saturation" => SKBlendMode.Saturation,
            "color" => SKBlendMode.Color,
            "luminosity" => SKBlendMode.Luminosity,
            _ => SKBlendMode.SrcOver,
        };

        return SKImageFilter.CreateBlendMode(mode, input2, input, crop);
    }

    /// <summary>
    /// `feMorphology` grows (`dilate`, the default) or shrinks (`erode`) the input's own opaque
    /// region by `radius`.
    /// </summary>
    private static SKImageFilter BuildMorphology(IElement primitive, SKImageFilter? input, SKRect crop)
    {
        var (radiusX, radiusY) = ParsePair(primitive.GetAttribute("radius"), 0f);
        var isErode = string.Equals(primitive.GetAttribute("operator")?.Trim(), "erode", StringComparison.OrdinalIgnoreCase);

        return isErode
            ? SKImageFilter.CreateErode(radiusX, radiusY, input, crop)
            : SKImageFilter.CreateDilate(radiusX, radiusY, input, crop);
    }

    /// <summary>
    /// `feComponentTransfer` remaps each channel through its `feFuncR`/`G`/`B`/`A` function -
    /// `identity`, `table`, `discrete`, `linear` or `gamma` - as a 256-entry lookup table on
    /// unpremultiplied values.
    /// </summary>
    private static SKImageFilter BuildComponentTransfer(IElement primitive, SKImageFilter? input, SKRect crop)
    {
        var tables = new byte[4][];

        for (var channel = 0; channel < 4; channel++)
        {
            tables[channel] = CreateTransferTable(null);
        }

        foreach (var function in primitive.Children)
        {
            var channel = function.LocalName.ToLowerInvariant() switch
            {
                "fefuncr" => 0,
                "fefuncg" => 1,
                "fefuncb" => 2,
                "fefunca" => 3,
                _ => -1,
            };

            if (channel >= 0)
            {
                tables[channel] = CreateTransferTable(function);
            }
        }

        using var colorFilter = SKColorFilter.CreateTable(tables[3], tables[0], tables[1], tables[2]);
        return SKImageFilter.CreateColorFilter(colorFilter, input, crop);
    }

    private static byte[] CreateTransferTable(IElement? function)
    {
        var type = function?.GetAttribute("type")?.Trim().ToLowerInvariant();
        var values = TryParseNumbers(function?.GetAttribute("tableValues"), -1, out var parsed) ? parsed : [];
        var slope = ParseFloat(function?.GetAttribute("slope"), 1f);
        var intercept = ParseFloat(function?.GetAttribute("intercept"), 0f);
        var amplitude = ParseFloat(function?.GetAttribute("amplitude"), 1f);
        var exponent = ParseFloat(function?.GetAttribute("exponent"), 1f);
        var offset = ParseFloat(function?.GetAttribute("offset"), 0f);
        var table = new byte[256];

        for (var i = 0; i < 256; i++)
        {
            var c = i / 255d;
            var value = type switch
            {
                "table" when values.Length > 1 => Table(c),
                "table" when values.Length == 1 => values[0],
                "discrete" when values.Length > 0 => values[Math.Min(values.Length - 1, (int)Math.Floor(c * values.Length))],
                "linear" => (slope * c) + intercept,
                "gamma" => (amplitude * Math.Pow(c, exponent)) + offset,
                _ => c,
            };

            table[i] = (byte)Math.Round(Math.Clamp(value, 0d, 1d) * 255d);
        }

        return table;

        double Table(double c)
        {
            var n = values.Length - 1;
            var k = Math.Min(n - 1, (int)Math.Floor(c * n));
            return values[k] + ((c - (k / (double)n)) * n * (values[k + 1] - values[k]));
        }
    }

    /// <summary>
    /// `feConvolveMatrix`. SVG applies the kernel rotated by 180 degrees relative to Skia, so it
    /// is reversed (and the target mirrored); `divisor` (default: the kernel sum, or 1) becomes
    /// the gain.
    /// </summary>
    private static SKImageFilter? BuildConvolveMatrix(IElement primitive, SKImageFilter? input, SKRect crop)
    {
        var (orderX, orderY) = ParsePair(primitive.GetAttribute("order"), 3f);
        var width = Math.Max(1, (int)orderX);
        var height = Math.Max(1, (int)orderY);

        if (!TryParseNumbers(primitive.GetAttribute("kernelMatrix"), width * height, out var kernel))
        {
            return input;
        }

        Array.Reverse(kernel);
        var sum = kernel.Sum();
        var divisor = ParseFloat(primitive.GetAttribute("divisor"), sum == 0f ? 1f : sum);
        var targetX = (int)ParseFloat(primitive.GetAttribute("targetX"), width / 2);
        var targetY = (int)ParseFloat(primitive.GetAttribute("targetY"), height / 2);
        var tileMode = primitive.GetAttribute("edgeMode")?.Trim().ToLowerInvariant() switch
        {
            "wrap" => SKShaderTileMode.Repeat,
            "none" => SKShaderTileMode.Decal,
            _ => SKShaderTileMode.Clamp,
        };
        var preserveAlpha = string.Equals(primitive.GetAttribute("preserveAlpha")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        return SKImageFilter.CreateMatrixConvolution(
            new SKSizeI(width, height),
            kernel,
            divisor == 0f ? 1f : 1f / divisor,
            ParseFloat(primitive.GetAttribute("bias"), 0f),
            new SKPointI(width - 1 - targetX, height - 1 - targetY),
            tileMode,
            !preserveAlpha,
            input,
            crop);
    }

    /// <summary>
    /// `feDiffuseLighting`/`feSpecularLighting` with the first `feDistantLight`, `fePointLight` or
    /// `feSpotLight` child; the input's alpha is the bump map.
    /// </summary>
    private static SKImageFilter BuildLighting(IElement primitive, SKImageFilter? input, SKRect crop, bool specular, bool linear)
    {
        var light = primitive.Children.FirstOrDefault(child => child.LocalName.ToLowerInvariant() is "fedistantlight" or "fepointlight" or "fespotlight");

        if (light is null)
        {
            return Transparent(crop);
        }

        var colorRaw = ReadPresentation(primitive, "lighting-color");
        var color = ToSpace(SvgColorParsing.TryParsePaint(string.IsNullOrWhiteSpace(colorRaw) ? "white" : colorRaw, out var parsed) ? parsed : SKColors.White, linear);
        var surfaceScale = ParseFloat(primitive.GetAttribute("surfaceScale"), 1f);
        var constant = ParseFloat(primitive.GetAttribute(specular ? "specularConstant" : "diffuseConstant"), 1f);
        var shininess = ParseFloat(primitive.GetAttribute("specularExponent"), 1f);

        SKPoint3 Point(string prefix) => new(
            ParseFloat(light.GetAttribute(prefix.Length == 0 ? "x" : prefix + "X"), 0f),
            ParseFloat(light.GetAttribute(prefix.Length == 0 ? "y" : prefix + "Y"), 0f),
            ParseFloat(light.GetAttribute(prefix.Length == 0 ? "z" : prefix + "Z"), 0f));

        switch (light.LocalName.ToLowerInvariant())
        {
            case "fedistantlight":
            {
                var azimuth = ParseFloat(light.GetAttribute("azimuth"), 0f) * MathF.PI / 180f;
                var elevation = ParseFloat(light.GetAttribute("elevation"), 0f) * MathF.PI / 180f;
                var direction = new SKPoint3(MathF.Cos(azimuth) * MathF.Cos(elevation), MathF.Sin(azimuth) * MathF.Cos(elevation), MathF.Sin(elevation));
                return specular
                    ? SKImageFilter.CreateDistantLitSpecular(direction, color, surfaceScale, constant, shininess, input, crop)
                    : SKImageFilter.CreateDistantLitDiffuse(direction, color, surfaceScale, constant, input, crop);
            }

            case "fepointlight":
                return specular
                    ? SKImageFilter.CreatePointLitSpecular(Point(string.Empty), color, surfaceScale, constant, shininess, input, crop)
                    : SKImageFilter.CreatePointLitDiffuse(Point(string.Empty), color, surfaceScale, constant, input, crop);

            default:
            {
                var falloff = ParseFloat(light.GetAttribute("specularExponent"), 1f);
                var cutoff = ParseFloat(light.GetAttribute("limitingConeAngle"), 90f);
                return specular
                    ? SKImageFilter.CreateSpotLitSpecular(Point(string.Empty), Point("pointsAt"), falloff, cutoff, color, surfaceScale, constant, shininess, input, crop)
                    : SKImageFilter.CreateSpotLitDiffuse(Point(string.Empty), Point("pointsAt"), falloff, cutoff, color, surfaceScale, constant, input, crop);
            }
        }
    }

    /// <summary>`feDisplacementMap`: `in` shifted by `scale` times the selected channels of `in2`.</summary>
    private static SKImageFilter BuildDisplacementMap(IElement primitive, SKImageFilter? input, SKImageFilter? displacement, SKRect crop)
    {
        static SKColorChannel Channel(string? value) => value?.Trim().ToUpperInvariant() switch
        {
            "R" => SKColorChannel.R,
            "G" => SKColorChannel.G,
            "B" => SKColorChannel.B,
            _ => SKColorChannel.A,
        };

        return SKImageFilter.CreateDisplacementMapEffect(
            Channel(primitive.GetAttribute("xChannelSelector")),
            Channel(primitive.GetAttribute("yChannelSelector")),
            ParseFloat(primitive.GetAttribute("scale"), 0f),
            displacement,
            input,
            crop);
    }

    /// <summary>`feTurbulence`: Perlin `turbulence` (the default) or `fractalNoise` over the subregion.</summary>
    private static SKImageFilter BuildTurbulence(IElement primitive, SKRect crop)
    {
        var (frequencyX, frequencyY) = ParsePair(primitive.GetAttribute("baseFrequency"), 0f);
        var octaves = Math.Max(1, (int)ParseFloat(primitive.GetAttribute("numOctaves"), 1f));
        var seed = ParseFloat(primitive.GetAttribute("seed"), 0f);
        var fractal = string.Equals(primitive.GetAttribute("type")?.Trim(), "fractalNoise", StringComparison.OrdinalIgnoreCase);
        using var shader = fractal
            ? SKShader.CreatePerlinNoiseFractalNoise(frequencyX, frequencyY, octaves, seed)
            : SKShader.CreatePerlinNoiseTurbulence(frequencyX, frequencyY, octaves, seed);
        using var paint = new SKPaint { Shader = shader };
        return SKImageFilter.CreatePaint(paint, crop);
    }

    /// <summary>
    /// `feImage`: a `data:` image fitted into the subregion per `preserveAspectRatio`, or an
    /// `#id` reference to an element of the same SVG, rendered in user space. Anything else (an
    /// external URL) is transparent.
    /// </summary>
    private static SKImageFilter BuildImage(IElement primitive, SKRect subregion, SvgRenderContext context, SvgViewport viewport)
    {
        var crop = subregion;
        var href = (primitive.GetAttribute("href") ?? primitive.GetAttribute("xlink:href"))?.Trim();

        if (href is { Length: > 1 } && href[0] == '#' && context.ElementsById.TryGetValue(href[1..], out var referenced))
        {
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(SKRect.Create(-viewport.Width, -viewport.Height, viewport.Width * 3f, viewport.Height * 3f));
            SvgElementRenderer.RenderReferencedElement(canvas, referenced, context, viewport);
            using var picture = recorder.EndRecording();
            return SKImageFilter.CreateMerge([SKImageFilter.CreatePicture(picture)], crop);
        }

        if (href is not null && TryDecodeDataImage(href) is { } image)
        {
            using (image)
            {
                var source = SKRect.Create(0f, 0f, image.Width, image.Height);
                var (align, meet) = SvgViewBoxMapping.ParsePreserveAspectRatio(primitive.GetAttribute("preserveAspectRatio"));
                var fit = SvgViewBoxMapping.ComputeMatrix((0f, 0f, image.Width, image.Height), subregion.Width, subregion.Height, align, meet);
                var destination = fit.MapRect(source);
                destination.Offset(subregion.Left, subregion.Top);
                return SKImageFilter.CreateImage(image, source, destination, SKFilterQuality.High);
            }
        }

        return Transparent(crop);
    }

    private static SKImage? TryDecodeDataImage(string href)
    {
        var comma = href.IndexOf(',');

        if (!href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0 || !href[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return SKImage.FromEncodedData(Convert.FromBase64String(href[(comma + 1)..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A property given as a presentation attribute or in the primitive's `style`.</summary>
    private static string? ReadPresentation(IElement primitive, string property)
    {
        var style = primitive.GetAttribute("style");

        if (!string.IsNullOrWhiteSpace(style))
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = declaration.IndexOf(':');

                if (colon > 0 && declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                {
                    return declaration[(colon + 1)..].Trim();
                }
            }
        }

        return primitive.GetAttribute(property);
    }

    private static (float X, float Y) ParsePair(string? value, float defaultValue)
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

    private static float ParseFraction(string? raw, float defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        var trimmed = raw.Trim();

        return trimmed.EndsWith('%') && float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            ? percent / 100f
            : ParseFloat(trimmed, defaultValue);
    }

    /// <summary>Parses whitespace/comma separated numbers; <paramref name="count"/> -1 accepts any count.</summary>
    private static bool TryParseNumbers(string? value, int count, out float[] numbers)
    {
        numbers = [];

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = value.Trim().Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (count >= 0 && tokens.Length != count)
        {
            return false;
        }

        var parsed = new float[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
        {
            if (!float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        numbers = parsed;
        return true;
    }

    private static float[] CreateSaturateMatrix(float s) =>
    [
        0.213f + (0.787f * s), 0.715f - (0.715f * s), 0.072f - (0.072f * s), 0f, 0f,
        0.213f - (0.213f * s), 0.715f + (0.285f * s), 0.072f - (0.072f * s), 0f, 0f,
        0.213f - (0.213f * s), 0.715f - (0.715f * s), 0.072f + (0.928f * s), 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    /// <summary>The SVG `hueRotate` matrix.</summary>
    private static float[] CreateHueRotateMatrix(float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);

        return
        [
            0.213f + (cos * 0.787f) - (sin * 0.213f), 0.715f - (cos * 0.715f) - (sin * 0.715f), 0.072f - (cos * 0.072f) + (sin * 0.928f), 0f, 0f,
            0.213f - (cos * 0.213f) + (sin * 0.143f), 0.715f + (cos * 0.285f) + (sin * 0.140f), 0.072f - (cos * 0.072f) - (sin * 0.283f), 0f, 0f,
            0.213f - (cos * 0.213f) - (sin * 0.787f), 0.715f - (cos * 0.715f) + (sin * 0.715f), 0.072f + (cos * 0.928f) + (sin * 0.072f), 0f, 0f,
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

    /// <summary>`SourceAlpha`: black, with the source graphic's alpha.</summary>
    private static readonly float[] SourceAlphaMatrix =
    [
        0f, 0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f, 0f,
        0f, 0f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];
}
