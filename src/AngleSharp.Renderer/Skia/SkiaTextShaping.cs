namespace AngleSharp.Renderer.Skia;

using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using AngleSharp.Renderer.Rendering;

using SkiaSharp;
using SkiaSharp.HarfBuzz;

/// <summary>
/// Shared text configuration for the Skia backend.
/// </summary>
/// <remarks>
/// Measuring and painting have to resolve the same typeface, apply the same paint settings and
/// shape the same way, otherwise layout is computed against glyphs that never reach the canvas.
/// Both paths go through this type (<see cref="ShapeText"/>) so they cannot drift apart.
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

    private static readonly IReadOnlyDictionary<string, string[]> PlatformFontAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["arial"] = ["Liberation Sans"],
            ["helvetica"] = ["Nimbus Sans", "Liberation Sans"],
            ["courier new"] = ["Liberation Mono", "Nimbus Mono PS"],
        };

    // CSS family names are case insensitive, but Skia's family lookup is not on every platform:
    // the Linux font manager matches case sensitively, so "arial" resolves on Windows and fails
    // on Linux. Indexing the installed families once gives the same answer everywhere.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> SystemFontFamilies =
        new(CreateSystemFontFamilyIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<SystemTypefaceKey, SKTypeface?> ResolvedSystemTypefaces = new();

    // What the host offers for a character no declared or bundled font has, per style.
    private static readonly ConcurrentDictionary<(int Codepoint, int Weight, int Width, SKFontStyleSlant Slant), SKTypeface?> CharacterFallbacks = new();

    // Decoding a font file is expensive and a face outlives a single render, so the typeface is
    // kept alongside the face itself rather than rebuilt per paint.
    private static readonly ConcurrentDictionary<byte[], SKTypeface?> EmbeddedTypefaces = new(ReferenceEqualityComparer.Instance);

    public static SKFontStyle CreateFontStyle(float fontWeight, bool isItalic, float fontStretch = 100f) =>
        new(fontWeight >= 600f ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            ToFontStyleWidth(fontStretch),
            isItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    /// <summary>
    /// Maps a <c>font-stretch</c> percentage to the nearest OpenType width class (CSS Fonts 4,
    /// 50% = ultra-condensed ... 200% = ultra-expanded). Only installed and <c>@font-face</c>
    /// families can honour it; the bundled fonts have a single width and nothing is synthesized.
    /// </summary>
    public static SKFontStyleWidth ToFontStyleWidth(float fontStretch)
    {
        ReadOnlySpan<float> classes = [50f, 62.5f, 75f, 87.5f, 100f, 112.5f, 125f, 150f, 200f];
        var best = 4;

        for (var index = 0; index < classes.Length; index++)
        {
            if (Math.Abs(classes[index] - fontStretch) < Math.Abs(classes[best] - fontStretch))
            {
                best = index;
            }
        }

        return (SKFontStyleWidth)(best + 1);
    }

    /// <summary>
    /// Creates the paint used for both measuring and drawing a text run. The caller assigns the
    /// color; everything that influences advance widths is set here.
    /// </summary>
    public static SKPaint CreateTextPaint(RenderFont font)
    {
        var typeface = CreateTypeface(
            font.FontFamily,
            CreateFontStyle(font.FontWeight, font.IsItalic, font.FontStretch),
            font.Faces ?? FontFaceSet.Empty,
            font.FontWeight);

        var paint = new SKPaint
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

        ApplySyntheticBold(paint, font, typeface);
        return paint;
    }

    /// <summary>
    /// Emboldens the glyphs by stroking their outlines along with filling them (which leaves the
    /// advances as they are), when the face is lighter than asked for. Skia's own fake bold is
    /// not used: on macOS it switches the glyphs from CoreText's rasterization, which renders
    /// them noticeably heavier, to plain outlines, so the "bold" text came out lighter than the
    /// regular one there.
    /// </summary>
    private static void ApplySyntheticBold(SKPaint paint, RenderFont font, SKTypeface typeface)
    {
        var synthesize = NeedsSyntheticBold(font, typeface);
        paint.Style = synthesize ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill;
        paint.StrokeWidth = synthesize ? font.FontSize * SyntheticBoldStrokeRatio : 0f;
        paint.StrokeJoin = SKStrokeJoin.Round;
    }

    // about the offset Firefox draws its synthetic bold with (1/16 em)
    private const float SyntheticBoldStrokeRatio = 1f / 16f;

    /// <summary>
    /// Bold is synthesized when the resolved face is lighter than asked for (CSS Fonts 4 §5.2,
    /// font-synthesis: weight), e.g. a variable web font whose weight axis cannot be set here, or
    /// a family that only comes in one weight.
    /// </summary>
    private static bool NeedsSyntheticBold(RenderFont font, SKTypeface typeface) =>
        font.FontWeight >= SyntheticBoldThreshold && typeface.FontWeight < SyntheticBoldThreshold;

    private const float SyntheticBoldThreshold = 600f;

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
            if (TryResolveFamily(family, fontStyle, faces, requestedWeight, out var typeface))
            {
                return typeface;
            }
        }

        return GetDefaultTypeface(isBold);
    }

    /// <summary>
    /// Resolves one entry of a <c>font-family</c> list, or reports that it is unavailable.
    /// </summary>
    private static bool TryResolveFamily(string family, SKFontStyle fontStyle, FontFaceSet faces, float requestedWeight, out SKTypeface typeface)
    {
        var normalized = family.Trim('\'', '"', ' ');
        var isBold = fontStyle.Weight >= (int)SKFontStyleWeight.Bold;
        var isItalic = fontStyle.Slant != SKFontStyleSlant.Upright;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            typeface = null!;
            return false;
        }

        // Generic names are keywords, so they always mean the bundled fonts and cannot be
        // taken over by an @font-face declaration.
        if (GenericFontMappings.TryGetValue(normalized, out var bundledFamilyKey) &&
            BundledFonts.Value.TryGetValue(bundledFamilyKey, out var bundledFamily))
        {
            typeface = isBold ? bundledFamily.Bold : bundledFamily.Regular;
            return true;
        }

        // A declared face takes precedence over an installed font of the same name.
        if (!faces.IsEmpty &&
            faces.TryMatch(normalized, requestedWeight, isItalic, out var face) &&
            TryResolveFaceTypeface(face, fontStyle, out typeface))
        {
            return true;
        }

        return TryResolveSystemTypeface(normalized, fontStyle, out typeface);
    }

    /// <summary>
    /// The typeface that draws a character the font's own typeface has no glyph for (CSS Fonts 4
    /// §5.1): the next entry of the <c>font-family</c> list that has it, then the bundled fonts,
    /// then whatever the host offers for it. Only the last step depends on the installed fonts,
    /// and only for characters none of the bundled fonts cover.
    /// </summary>
    private static SKTypeface ResolveFallbackTypeface(RenderFont font, SKTypeface primary, int codepoint)
    {
        var fontStyle = CreateFontStyle(font.FontWeight, font.IsItalic, font.FontStretch);
        var faces = font.Faces ?? FontFaceSet.Empty;

        foreach (var family in font.FontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryResolveFamily(family, fontStyle, faces, font.FontWeight, out var typeface) && HasGlyph(typeface, codepoint))
            {
                return typeface;
            }
        }

        var isBold = fontStyle.Weight >= (int)SKFontStyleWeight.Bold;

        foreach (var bundledFamily in BundledFonts.Value.Values)
        {
            var typeface = isBold ? bundledFamily.Bold : bundledFamily.Regular;

            if (HasGlyph(typeface, codepoint))
            {
                return typeface;
            }
        }

        var hostFallback = CharacterFallbacks.GetOrAdd((codepoint, fontStyle.Weight, fontStyle.Width, fontStyle.Slant), static key =>
        {
            var match = SKFontManager.Default.MatchCharacter(null, new SKFontStyle(key.Weight, key.Width, key.Slant), null, key.Codepoint);
            return match is not null && HasGlyph(match, key.Codepoint) ? match : null;
        });

        return hostFallback ?? primary;
    }

    private static bool HasGlyph(SKTypeface typeface, int codepoint) => typeface.GetGlyph(codepoint) != 0;

    /// <summary>
    /// Splits text into runs that each draw with one typeface: the paint's own where it has the
    /// glyphs, a fallback (<see cref="ResolveFallbackTypeface"/>) where it does not. Returns
    /// <see langword="null"/> when the paint's typeface covers the whole text, the common case.
    /// </summary>
    private static List<(string Text, SKTypeface Typeface)>? SplitFallbackRuns(SKPaint paint, string text, RenderFont? font)
    {
        var primary = paint.Typeface;

        if (font is null || primary is null || primary.ContainsGlyphs(text))
        {
            return null;
        }

        var runs = new List<(string Text, SKTypeface Typeface)>();
        var builder = new StringBuilder();
        SKTypeface? current = null;

        foreach (var rune in text.EnumerateRunes())
        {
            // Spaces and controls stay with the run they are in rather than splitting it.
            var typeface = current is not null && (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune)) ? current :
                HasGlyph(primary, rune.Value) || Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) ? primary :
                ResolveFallbackTypeface(font.Value, primary, rune.Value);

            if (current is not null && !ReferenceEquals(typeface, current))
            {
                runs.Add((builder.ToString(), current));
                builder.Clear();
            }

            current = typeface;
            builder.Append(rune.ToString());
        }

        if (current is not null)
        {
            runs.Add((builder.ToString(), current));
        }

        return runs;
    }

    /// <summary>
    /// A copy of <paramref name="paint"/> drawing with another typeface, slanted synthetically
    /// when the font asks for italics the typeface does not have.
    /// </summary>
    private static SKPaint CreateFallbackPaint(SKPaint paint, SKTypeface typeface, RenderFont font)
    {
        var fallbackPaint = paint.Clone();
        fallbackPaint.Typeface = typeface;
        fallbackPaint.TextSkewX = font.IsItalic && typeface.FontSlant == SKFontStyleSlant.Upright ? -0.25f : 0f;

        // a stroke paint (SVG `stroke`) outlines the glyphs and has to stay one
        if (paint.Style != SKPaintStyle.Stroke)
        {
            ApplySyntheticBold(fallbackPaint, font, typeface);
        }

        return fallbackPaint;
    }

    /// <summary>
    /// Draws text exactly as <see cref="MeasureTextWidth"/> measures it: shaped with HarfBuzz
    /// (see <see cref="ShapeText"/>) and painted as positioned glyphs, one text blob per run.
    /// </summary>
    public static void DrawText(SKCanvas canvas, SKPaint paint, string text, float x, float y, float letterSpacing, RenderFont? font = null)
    {
        var shaped = ShapeText(paint, text, letterSpacing, font);

        foreach (var run in shaped.Runs)
        {
            if (run.Glyphs.Length == 0)
            {
                continue;
            }

            var runPaint = ReferenceEquals(run.Typeface, paint.Typeface) || font is null ? paint : CreateFallbackPaint(paint, run.Typeface, font.Value);

            try
            {
                using var skFont = runPaint.ToFont();
                using var builder = new SKTextBlobBuilder();
                var buffer = builder.AllocatePositionedRun(skFont, run.Glyphs.Length);
                buffer.SetGlyphs(run.Glyphs);
                buffer.SetPositions(run.Positions);
                using var blob = builder.Build();

                if (blob is not null)
                {
                    canvas.DrawText(blob, x, y, runPaint);
                }
            }
            finally
            {
                if (!ReferenceEquals(runPaint, paint))
                {
                    runPaint.Dispose();
                }
            }
        }
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
            canonicalFamily = PlatformFontAliases.TryGetValue(family, out var aliases)
                ? aliases.FirstOrDefault(alias => SystemFontFamilies.Value.ContainsKey(alias))
                : null;

            if (canonicalFamily is null)
            {
                typeface = null!;
                return false;
            }
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
    /// Measures a line of text that no layout has split into bidi runs (SVG text, Canvas 2D), as
    /// <see cref="DrawParagraph"/> paints it.
    /// </summary>
    public static float MeasureParagraph(SKPaint paint, string text, RenderFont font, bool isRightToLeft)
    {
        if (!BidiAlgorithm.RequiresReordering(text, isRightToLeft))
        {
            return MeasureTextWidth(paint, text, font.LetterSpacing, font with { IsRightToLeft = false });
        }

        var width = 0f;

        foreach (var (run, runIsRightToLeft) in BidiAlgorithm.GetVisualRuns(text, isRightToLeft))
        {
            width += MeasureTextWidth(paint, run, font.LetterSpacing, font with { IsRightToLeft = runIsRightToLeft });
        }

        return width;
    }

    /// <summary>
    /// Draws a line of text that no layout has split into bidi runs: its runs in visual order
    /// (UAX #9, with <paramref name="isRightToLeft"/> as the paragraph direction), left to right
    /// from <paramref name="x"/>, each shaped in its own direction.
    /// </summary>
    public static void DrawParagraph(SKCanvas canvas, SKPaint paint, string text, float x, float y, RenderFont font, bool isRightToLeft)
    {
        if (!BidiAlgorithm.RequiresReordering(text, isRightToLeft))
        {
            DrawText(canvas, paint, text, x, y, font.LetterSpacing, font with { IsRightToLeft = false });
            return;
        }

        foreach (var (run, runIsRightToLeft) in BidiAlgorithm.GetVisualRuns(text, isRightToLeft))
        {
            var runFont = font with { IsRightToLeft = runIsRightToLeft };
            DrawText(canvas, paint, run, x, y, font.LetterSpacing, runFont);
            x += MeasureTextWidth(paint, run, font.LetterSpacing, runFont);
        }
    }

    /// <summary>
    /// Measures the advance width of <paramref name="text"/> as <see cref="DrawText"/> paints it.
    /// </summary>
    public static float MeasureTextWidth(SKPaint paint, string text, float letterSpacing, RenderFont? font = null) =>
        ShapeText(paint, text, letterSpacing, font).Width;

    /// <summary>
    /// The boundaries (UTF-16 offsets, ascending, starting with 0) of the clusters HarfBuzz forms
    /// for <paramref name="text"/>: the units text may be cut into without breaking a glyph.
    /// </summary>
    public static IReadOnlyList<int> GetClusterBoundaries(SKPaint paint, string text, RenderFont? font = null)
    {
        var boundaries = new SortedSet<int> { 0 };

        foreach (var run in ShapeText(paint, text, font?.LetterSpacing ?? 0f, font).Runs)
        {
            foreach (var cluster in run.Clusters)
            {
                boundaries.Add((int)cluster);
            }
        }

        boundaries.Add(text.Length);
        return [.. boundaries];
    }

    /// <summary>
    /// The glyphs of one typeface, positioned relative to the origin of the whole text, in
    /// visual order.
    /// </summary>
    private sealed record GlyphRun(SKTypeface Typeface, ushort[] Glyphs, SKPoint[] Positions, uint[] Clusters);

    private sealed record ShapedText(List<GlyphRun> Runs, float Width);

    /// <summary>
    /// Shapes text with HarfBuzz: split into runs of one typeface (<see cref="SplitFallbackRuns"/>)
    /// and one script, each shaped in the run direction of <paramref name="font"/> (or its
    /// script's, for right-to-left scripts) with the font's OpenType features, the whole text as context. Runs are placed
    /// in visual order, so the first logical run is the rightmost one in right-to-left text.
    /// Letter spacing goes after every cluster but the last and turns optional ligatures off.
    /// </summary>
    private static ShapedText ShapeText(SKPaint paint, string text, float letterSpacing, RenderFont? font)
    {
        var runs = new List<GlyphRun>();

        if (string.IsNullOrEmpty(text) || paint.Typeface is not { } primary)
        {
            return new ShapedText(runs, 0f);
        }

        var isRightToLeft = font?.IsRightToLeft ?? false;
        var features = ResolveFeatures(letterSpacing, font?.FontFeatures);
        var segments = new List<(int Start, int Length, SKTypeface Typeface, HarfBuzzSharp.Script Script)>();
        var offset = 0;

        // Context from across an element boundary is handed to HarfBuzz around the text, so
        // joining continues across it without being shaped (or measured) here.
        var contextBefore = font?.ContextBefore ?? string.Empty;
        var shapedText = contextBefore.Length == 0 && string.IsNullOrEmpty(font?.ContextAfter) ? text : contextBefore + text + font?.ContextAfter;

        foreach (var (runText, typeface) in SplitFallbackRuns(paint, text, font) ?? [(text, primary)])
        {
            foreach (var (start, length, script) in SplitScripts(text, offset, runText.Length))
            {
                segments.Add((start, length, typeface, script));
            }

            offset += runText.Length;
        }

        if (isRightToLeft)
        {
            segments.Reverse();
        }

        var penX = 0f;
        var spacingCount = 0;

        foreach (var (start, length, typeface, script) in segments)
        {
            var hbFont = GetHarfBuzzFont(typeface);
            var scale = paint.TextSize / hbFont.UnitsPerEm;

            // Strong right-to-left characters always resolve to an odd bidi level (UAX #9 I1/I2),
            // so a right-to-left script is shaped right to left even where the caller has not
            // resolved the levels yet (layout measures words before reordering lines, SVG text
            // never does): HarfBuzz shapes e.g. Arabic differently when forced left to right.
            // Everything else (neutrals, or Latin under an override) takes the run direction.
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(shapedText, contextBefore.Length + start, length);
            buffer.Direction = isRightToLeft || script.HorizontalDirection == HarfBuzzSharp.Direction.RightToLeft
                ? HarfBuzzSharp.Direction.RightToLeft
                : HarfBuzzSharp.Direction.LeftToRight;
            buffer.Language = GetLanguage(font?.Language);
            buffer.GuessSegmentProperties();
            hbFont.Font.Shape(buffer, features);

            var infos = buffer.GetGlyphInfoSpan();
            var positions = buffer.GetGlyphPositionSpan();
            var glyphs = new ushort[infos.Length];
            var points = new SKPoint[infos.Length];
            var clusters = new uint[infos.Length];

            for (var index = 0; index < infos.Length; index++)
            {
                glyphs[index] = (ushort)infos[index].Codepoint;
                clusters[index] = infos[index].Cluster - (uint)contextBefore.Length;
                points[index] = new SKPoint(penX + (positions[index].XOffset * scale), -positions[index].YOffset * scale);
                penX += positions[index].XAdvance * scale;

                // spacing follows the last glyph of every cluster
                if (letterSpacing != 0f && (index + 1 == infos.Length || infos[index + 1].Cluster != infos[index].Cluster))
                {
                    penX += letterSpacing;
                    spacingCount++;
                }
            }

            runs.Add(new GlyphRun(typeface, glyphs, points, clusters));
        }

        // no spacing after the last cluster
        return new ShapedText(runs, spacingCount > 0 ? penX - letterSpacing : penX);
    }

    // Without a `lang` the language is fixed rather than HarfBuzz's default (the host locale), so
    // results do not depend on the machine.
    private static readonly HarfBuzzSharp.Language DefaultLanguage = new("und");

    private static readonly ConcurrentDictionary<string, HarfBuzzSharp.Language> Languages = new(StringComparer.OrdinalIgnoreCase);

    private static HarfBuzzSharp.Language GetLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? DefaultLanguage : Languages.GetOrAdd(language, static tag => new HarfBuzzSharp.Language(tag));

    private static readonly HarfBuzzSharp.Feature[] NoLigatures = ParseFeatures("liga=0,clig=0,dlig=0,hlig=0");

    /// <summary>
    /// The features to shape with: optional ligatures off under letter spacing (CSS Text 3
    /// §8.2), then those of the font's <c>font-variant-*</c>/<c>font-kerning</c>/
    /// <c>font-feature-settings</c>, which come later and so take precedence.
    /// </summary>
    private static HarfBuzzSharp.Feature[] ResolveFeatures(float letterSpacing, string? fontFeatures)
    {
        var declared = string.IsNullOrEmpty(fontFeatures) ? [] : ParsedFeatures.GetOrAdd(fontFeatures, ParseFeatures);
        return letterSpacing != 0f ? [.. NoLigatures, .. declared] : declared;
    }

    private static readonly ConcurrentDictionary<string, HarfBuzzSharp.Feature[]> ParsedFeatures = new(StringComparer.Ordinal);

    private static HarfBuzzSharp.Feature[] ParseFeatures(string features)
    {
        var parsed = new List<HarfBuzzSharp.Feature>();

        foreach (var entry in features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (HarfBuzzSharp.Feature.TryParse(entry, out var feature))
            {
                parsed.Add(feature);
            }
        }

        return [.. parsed];
    }

    /// <summary>
    /// Splits a range of <paramref name="text"/> into runs of one script, as HarfBuzz shapes one
    /// script at a time. Characters of no particular script (spaces, punctuation, digits,
    /// combining marks) stay with the run they are in, or join the first run when they lead.
    /// </summary>
    private static List<(int Start, int Length, HarfBuzzSharp.Script Script)> SplitScripts(string text, int start, int length)
    {
        var result = new List<(int Start, int Length, HarfBuzzSharp.Script Script)>();
        var runStart = start;
        var runScript = HarfBuzzSharp.Script.Invalid;
        var end = start + length;
        var index = start;

        while (index < end)
        {
            var codepoint = char.IsSurrogatePair(text, index) ? char.ConvertToUtf32(text, index) : text[index];
            var script = HarfBuzzSharp.UnicodeFunctions.Default.GetScript((uint)codepoint);

            if (script != HarfBuzzSharp.Script.Common && script != HarfBuzzSharp.Script.Inherited && script != HarfBuzzSharp.Script.Unknown)
            {
                if (runScript != HarfBuzzSharp.Script.Invalid && script != runScript)
                {
                    result.Add((runStart, index - runStart, runScript));
                    runStart = index;
                }

                runScript = script;
            }

            index += codepoint > 0xFFFF ? 2 : 1;
        }

        result.Add((runStart, end - runStart, runScript));
        return result;
    }

    /// <summary>A HarfBuzz font for a typeface, kept with the blob and face behind it.</summary>
    private sealed class HarfBuzzFont(HarfBuzzSharp.Blob blob, HarfBuzzSharp.Face face, HarfBuzzSharp.Font font, int unitsPerEm)
    {
        public HarfBuzzSharp.Blob Blob { get; } = blob;

        public HarfBuzzSharp.Face Face { get; } = face;

        public HarfBuzzSharp.Font Font { get; } = font;

        public int UnitsPerEm { get; } = unitsPerEm;
    }

    // Typefaces are themselves cached for the life of the process (bundled, installed and
    // @font-face ones alike), so their HarfBuzz fonts are too. Shaping with a font is thread safe.
    private static readonly ConcurrentDictionary<SKTypeface, HarfBuzzFont> HarfBuzzFonts = new(ReferenceEqualityComparer.Instance);

    private static HarfBuzzFont GetHarfBuzzFont(SKTypeface typeface) =>
        HarfBuzzFonts.GetOrAdd(typeface, static typeface =>
        {
            // the blob takes over the stream (it may point straight into its memory)
            var blob = typeface.OpenStream(out var index).ToHarfBuzzBlob();
            var face = new HarfBuzzSharp.Face(blob, index);
            var unitsPerEm = Math.Max(1, face.UnitsPerEm);
            var font = new HarfBuzzSharp.Font(face);

            // positions in font units, scaled to the font size by the caller
            font.SetScale(unitsPerEm, unitsPerEm);
            font.SetFunctionsOpenType();
            return new HarfBuzzFont(blob, face, font, unitsPerEm);
        });

    private static IReadOnlyDictionary<string, BundledFontFamily> CreateBundledFonts()
    {
        var sansRegular = LoadBundledTypeface("DejaVuSans.ttf");
        var sansBold = LoadBundledTypeface("DejaVuSans-Bold.ttf");
        var serifRegular = LoadBundledTypeface("DejaVuSerif.ttf");
        var serifBold = LoadBundledTypeface("DejaVuSerif-Bold.ttf");
        var monoRegular = LoadBundledTypeface("DejaVuSansMono.ttf");
        var monoBold = LoadBundledTypeface("DejaVuSansMono-Bold.ttf");
        var devanagari = LoadBundledTypeface("NotoSansDevanagari-Regular.ttf");

        return new Dictionary<string, BundledFontFamily>(StringComparer.OrdinalIgnoreCase)
        {
            ["sans-serif"] = new BundledFontFamily(sansRegular, sansBold),
            ["serif"] = new BundledFontFamily(serifRegular, serifBold),
            ["monospace"] = new BundledFontFamily(monoRegular, monoBold),

            // Only a fallback for the scripts the DejaVu fonts lack, never a generic family; its
            // bold is synthesized.
            ["devanagari"] = new BundledFontFamily(devanagari, devanagari),
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
