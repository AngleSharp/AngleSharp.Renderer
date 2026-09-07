namespace AngleSharp.Renderer.Skia;

using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using AngleSharp.Renderer.Rendering;

using SkiaSharp;

/// <summary>
/// Shared text configuration for the Skia backend.
/// </summary>
/// <remarks>
/// Measuring and painting have to resolve the same typeface and apply the same paint settings,
/// otherwise layout is computed against a font that never reaches the canvas. Both paths go
/// through this type so they cannot drift apart.
/// </remarks>
internal static class SkiaTextShaping
{
    private const string FontResourcePrefix = "AngleSharp.Renderer.Resources.Fonts.";

    private static readonly Lazy<IReadOnlyDictionary<string, BundledFontFamily>> BundledFonts =
        new(CreateBundledFonts, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly IReadOnlyDictionary<string, string> GenericFontMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["serif"] = "serif",
            ["sans-serif"] = "sans-serif",
            ["monospace"] = "monospace",
            ["cursive"] = "sans-serif",
            ["fantasy"] = "serif",
            ["dejavu serif"] = "serif",
            ["dejavu sans"] = "sans-serif",
            ["dejavu sans mono"] = "monospace",
            ["system-ui"] = "sans-serif",
            ["ui-sans-serif"] = "sans-serif",
            ["ui-serif"] = "serif",
            ["ui-monospace"] = "monospace",
        };

    // CSS family names are case insensitive, but Skia's family lookup is not on every platform:
    // the Linux font manager matches case sensitively, so "arial" resolves on Windows and fails
    // on Linux. Indexing the installed families once gives the same answer everywhere.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> SystemFontFamilies =
        new(CreateSystemFontFamilyIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<SystemTypefaceKey, SKTypeface?> ResolvedSystemTypefaces = new();

    // Decoding a font file is expensive and a face outlives a single render, so the typeface is
    // kept alongside the face itself rather than rebuilt per paint.
    private static readonly ConcurrentDictionary<byte[], SKTypeface?> EmbeddedTypefaces = new(ReferenceEqualityComparer.Instance);

    public static SKFontStyle CreateFontStyle(float fontWeight, bool isItalic) =>
        new(fontWeight >= 600f ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            isItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    /// <summary>
    /// Creates the paint used for both measuring and drawing a text run. The caller assigns the
    /// color; everything that influences advance widths is set here.
    /// </summary>
    public static SKPaint CreateTextPaint(RenderFont font)
    {
        var typeface = CreateTypeface(
            font.FontFamily,
            CreateFontStyle(font.FontWeight, font.IsItalic),
            font.Faces ?? FontFaceSet.Empty,
            font.FontWeight);

        return new SKPaint
        {
            IsAntialias = true,
            SubpixelText = false,
            LcdRenderText = false,
            HintingLevel = SKPaintHinting.Normal,
            TextSize = font.FontSize,
            Typeface = typeface,
            // Only slant synthetically when the resolved face is upright. A real italic face is
            // already slanted, and skewing it again doubles the angle.
            TextSkewX = font.IsItalic && typeface.FontSlant == SKFontStyleSlant.Upright ? -0.25f : 0f,
        };
    }

    /// <summary>
    /// Resolves a CSS font-family list to a typeface, honouring the declared fallback order.
    /// </summary>
    public static SKTypeface CreateTypeface(string fontFamily, SKFontStyle fontStyle) =>
        CreateTypeface(fontFamily, fontStyle, FontFaceSet.Empty, fontStyle.Weight);

    /// <summary>
    /// Resolves a CSS font-family list to a typeface, honouring the declared fallback order and
    /// any <c>@font-face</c> declarations in scope.
    /// </summary>
    public static SKTypeface CreateTypeface(string fontFamily, SKFontStyle fontStyle, FontFaceSet faces, float requestedWeight)
    {
        var isBold = fontStyle.Weight >= (int)SKFontStyleWeight.Bold;
        var isItalic = fontStyle.Slant != SKFontStyleSlant.Upright;

        var families = fontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var family in families)
        {
            var normalized = family.Trim('\'', '"', ' ');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            // Generic names are keywords, so they always mean the bundled fonts and cannot be
            // taken over by an @font-face declaration.
            if (GenericFontMappings.TryGetValue(normalized, out var bundledFamilyKey) &&
                BundledFonts.Value.TryGetValue(bundledFamilyKey, out var bundledFamily))
            {
                return isBold ? bundledFamily.Bold : bundledFamily.Regular;
            }

            // A declared face takes precedence over an installed font of the same name.
            if (!faces.IsEmpty &&
                faces.TryMatch(normalized, requestedWeight, isItalic, out var face) &&
                TryResolveFaceTypeface(face, fontStyle, out var faceTypeface))
            {
                return faceTypeface;
            }

            if (TryResolveSystemTypeface(normalized, fontStyle, out var typeface))
            {
                return typeface;
            }
        }

        return GetDefaultTypeface(isBold);
    }

    /// <summary>
    /// Walks the sources of a face in declaration order and returns the first usable one.
    /// </summary>
    private static bool TryResolveFaceTypeface(FontFace face, SKFontStyle fontStyle, out SKTypeface typeface)
    {
        foreach (var source in face.Sources)
        {
            if (source.LocalFamily is not null)
            {
                if (TryResolveSystemTypeface(source.LocalFamily, fontStyle, out typeface))
                {
                    return true;
                }

                continue;
            }

            if (source.Data is not null && TryDecodeTypeface(source.Data) is { } decoded)
            {
                typeface = decoded;
                return true;
            }
        }

        typeface = null!;
        return false;
    }

    private static SKTypeface? TryDecodeTypeface(byte[] fontData) =>
        EmbeddedTypefaces.GetOrAdd(fontData, static bytes =>
        {
            using var data = SKData.CreateCopy(bytes);
            return SKTypeface.FromData(data);
        });

    /// <summary>
    /// Resolves an installed family, or reports that it is unavailable so the caller can move on
    /// to the next entry of the fallback list.
    /// </summary>
    /// <remarks>
    /// <see cref="SKTypeface.FromFamilyName(string, SKFontStyle)"/> cannot be used for this: it
    /// substitutes the platform default for an unknown family instead of returning
    /// <see langword="null"/>, which silently swallows the rest of the fallback list and makes the
    /// result depend on whichever font the host happens to default to.
    /// </remarks>
    private static bool TryResolveSystemTypeface(string family, SKFontStyle fontStyle, out SKTypeface typeface)
    {
        if (!SystemFontFamilies.Value.TryGetValue(family, out var canonicalFamily))
        {
            typeface = null!;
            return false;
        }

        var key = new SystemTypefaceKey(canonicalFamily, fontStyle.Weight, fontStyle.Width, fontStyle.Slant);
        var resolved = ResolvedSystemTypefaces.GetOrAdd(key, static k =>
        {
            var match = SKFontManager.Default.MatchFamily(k.Family, new SKFontStyle(k.Weight, k.Width, k.Slant));

            // Some platforms substitute rather than return null, so confirm what came back really
            // is the requested family before accepting it.
            return match is not null && string.Equals(match.FamilyName, k.Family, StringComparison.OrdinalIgnoreCase)
                ? match
                : null;
        });

        typeface = resolved!;
        return resolved is not null;
    }

    private static SKTypeface GetDefaultTypeface(bool isBold)
    {
        if (BundledFonts.Value.TryGetValue("sans-serif", out var defaultFamily))
        {
            return isBold ? defaultFamily.Bold : defaultFamily.Regular;
        }

        return SKTypeface.Default;
    }

    private static IReadOnlyDictionary<string, string> CreateSystemFontFamilyIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in SKFontManager.Default.FontFamilies)
        {
            if (!string.IsNullOrWhiteSpace(family))
            {
                index[family] = family;
            }
        }

        return index;
    }

    /// <summary>
    /// Measures the advance width, mirroring how the backend lays glyphs out when letter
    /// spacing is in play.
    /// </summary>
    public static float MeasureTextWidth(SKPaint paint, string text, float letterSpacing)
    {
        if (letterSpacing <= 0f)
        {
            return paint.MeasureText(text);
        }

        var width = 0f;

        foreach (var character in text)
        {
            width += paint.MeasureText(character.ToString()) + letterSpacing;
        }

        return width > 0f ? width - letterSpacing : 0f;
    }

    private static IReadOnlyDictionary<string, BundledFontFamily> CreateBundledFonts()
    {
        var sansRegular = LoadBundledTypeface("DejaVuSans.ttf");
        var sansBold = LoadBundledTypeface("DejaVuSans-Bold.ttf");
        var serifRegular = LoadBundledTypeface("DejaVuSerif.ttf");
        var serifBold = LoadBundledTypeface("DejaVuSerif-Bold.ttf");
        var monoRegular = LoadBundledTypeface("DejaVuSansMono.ttf");
        var monoBold = LoadBundledTypeface("DejaVuSansMono-Bold.ttf");

        return new Dictionary<string, BundledFontFamily>(StringComparer.OrdinalIgnoreCase)
        {
            ["sans-serif"] = new BundledFontFamily(sansRegular, sansBold),
            ["serif"] = new BundledFontFamily(serifRegular, serifBold),
            ["monospace"] = new BundledFontFamily(monoRegular, monoBold),
        };
    }

    private static SKTypeface LoadBundledTypeface(string fileName)
    {
        var assembly = typeof(SkiaTextShaping).Assembly;
        var resourceName = string.Concat(FontResourcePrefix, fileName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled font resource not found: {resourceName}");
        using var data = SKData.Create(stream)
            ?? throw new InvalidOperationException($"Unable to read bundled font resource: {resourceName}");

        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException($"Unable to load bundled font resource: {resourceName}");
    }

    private readonly record struct BundledFontFamily(SKTypeface Regular, SKTypeface Bold);

    private readonly record struct SystemTypefaceKey(string Family, int Weight, int Width, SKFontStyleSlant Slant);
}
