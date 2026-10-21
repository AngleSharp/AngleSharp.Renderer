namespace AngleSharp.Renderer;

using System.Globalization;
using System.Text;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Text properties beyond the basic font: <c>hyphens</c>, <c>tab-size</c>,
/// <c>font-variant-caps</c> and <c>font-stretch</c>.
/// </summary>
public sealed partial class HtmlRenderer
{
    private const char SoftHyphen = '­';

    /// <summary>The character painted at the end of a line broken at a soft hyphen.</summary>
    private const string HyphenText = "-";

    /// <summary>The scale of the synthesized small capitals relative to the font size.</summary>
    private const float SmallCapsScale = 0.7f;

    /// <summary>
    /// CSS <c>hyphens</c>. <c>auto</c> has no hyphenation dictionary behind it, so it breaks
    /// only at soft hyphens, exactly like <c>manual</c>.
    /// </summary>
    private enum HyphensMode
    {
        Manual,
        None,
        Auto,
    }

    private enum FontVariantCapsMode
    {
        Normal,
        SmallCaps,
        AllSmallCaps,
    }

    private static HyphensMode ParseHyphens(Dictionary<string, string> styleMap, HyphensMode inherited)
    {
        if (!styleMap.TryGetValue("hyphens", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "none" => HyphensMode.None,
            "auto" => HyphensMode.Auto,
            "manual" => HyphensMode.Manual,
            _ => inherited,
        };
    }

    /// <summary>
    /// CSS <c>tab-size</c> (inherited): a number of space advances, or a length when
    /// <paramref name="isLength"/> is set.
    /// </summary>
    private static float ParseTabSize(Dictionary<string, string> styleMap, float fontSize, float inherited, bool inheritedIsLength, out bool isLength)
    {
        isLength = inheritedIsLength;

        if (!styleMap.TryGetValue("tab-size", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        var trimmed = value.Trim();

        if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var count))
        {
            isLength = false;
            return Math.Max(0f, count);
        }

        if (trimmed.EndsWith("em", StringComparison.OrdinalIgnoreCase) && !trimmed.EndsWith("rem", StringComparison.OrdinalIgnoreCase) &&
            float.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ems))
        {
            isLength = true;
            return Math.Max(0f, ems * fontSize);
        }

        var length = ParseLengthValue(trimmed, float.NaN, allowAuto: false);

        if (float.IsNaN(length))
        {
            return inherited;
        }

        isLength = true;
        return Math.Max(0f, length);
    }

    /// <summary>
    /// The advance of one tab stop interval in pixels.
    /// </summary>
    private static float ResolveTabStopWidth(LayoutContext context, RenderTextStyle style) =>
        style.TabSizeIsLength
            ? style.TabSize
            : style.TabSize * (MeasureTextWidth(context, " ", style) + style.WordSpacing);

    /// <summary>
    /// Expands preserved tabs to spaces up to the next tab stop, for the paragraph path, which
    /// paints whole lines and therefore can only advance by whole spaces.
    /// </summary>
    private static string ExpandTabs(LayoutContext context, string text, RenderTextStyle style)
    {
        if (!text.Contains('\t'))
        {
            return text;
        }

        var spaceWidth = MeasureTextWidth(context, " ", style) + style.WordSpacing;
        var spacesPerTab = spaceWidth > 0f ? ResolveTabStopWidth(context, style) / spaceWidth : style.TabSize;
        var builder = new StringBuilder(text.Length);
        var column = 0f;

        foreach (var c in text)
        {
            if (c == '\n')
            {
                builder.Append(c);
                column = 0f;
                continue;
            }

            if (c != '\t')
            {
                builder.Append(c);
                column++;
                continue;
            }

            if (spacesPerTab <= 0f)
            {
                continue;
            }

            var next = (MathF.Floor((column + 0.001f) / spacesPerTab) + 1f) * spacesPerTab;
            var spaces = Math.Max(1, (int)MathF.Round(next - column));
            builder.Append(' ', spaces);
            column += spaces;
        }

        return builder.ToString();
    }

    /// <summary>
    /// <c>font-variant-caps</c> (inherited; also set by the <c>font-variant</c> shorthand).
    /// </summary>
    private static FontVariantCapsMode ParseFontVariantCaps(Dictionary<string, string> styleMap, FontVariantCapsMode inherited)
    {
        if (!styleMap.TryGetValue("font-variant-caps", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        var mode = FontVariantCapsMode.Normal;

        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToLowerInvariant())
            {
                // Without the font feature, petite caps and unicase fall back to small caps.
                case "small-caps":
                case "petite-caps":
                case "unicase":
                    mode = FontVariantCapsMode.SmallCaps;
                    break;
                case "all-small-caps":
                case "all-petite-caps":
                    mode = FontVariantCapsMode.AllSmallCaps;
                    break;
            }
        }

        return mode;
    }

    /// <summary>
    /// <c>font-stretch</c> as a percentage (inherited); keywords map to their CSS percentages.
    /// </summary>
    private static float ParseFontStretch(Dictionary<string, string> styleMap, float inherited)
    {
        if (!styleMap.TryGetValue("font-stretch", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        var trimmed = value.Trim().ToLowerInvariant();

        if (trimmed.EndsWith('%') &&
            float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage) &&
            percentage >= 0f)
        {
            return percentage;
        }

        return trimmed switch
        {
            "ultra-condensed" => 50f,
            "extra-condensed" => 62.5f,
            "condensed" => 75f,
            "semi-condensed" => 87.5f,
            "normal" => 100f,
            "semi-expanded" => 112.5f,
            "expanded" => 125f,
            "extra-expanded" => 150f,
            "ultra-expanded" => 200f,
            _ => inherited,
        };
    }

    /// <summary>
    /// Splits text into runs for synthesized small capitals: lowercase letters (every letter for
    /// <c>all-small-caps</c>) become uppercase at <see cref="SmallCapsScale"/> of the font size.
    /// Non-letters join the run before them, so words are not cut into pieces needlessly.
    /// </summary>
    private static List<(string Text, bool Reduced)> SplitSmallCaps(string text, FontVariantCapsMode mode)
    {
        var runs = new List<(string Text, bool Reduced)>();
        var current = new StringBuilder();
        var currentReduced = false;

        foreach (var rune in text.EnumerateRunes())
        {
            var reduced = Rune.IsLetter(rune)
                ? mode == FontVariantCapsMode.AllSmallCaps || Rune.IsLower(rune)
                : current.Length > 0 && currentReduced;

            if (current.Length > 0 && reduced != currentReduced)
            {
                runs.Add((current.ToString(), currentReduced));
                current.Clear();
            }

            currentReduced = reduced;
            current.Append(reduced ? Rune.ToUpperInvariant(rune).ToString() : rune.ToString());
        }

        if (current.Length > 0)
        {
            runs.Add((current.ToString(), currentReduced));
        }

        return runs;
    }

    private static RenderTextStyle WithSmallCapsSize(RenderTextStyle style) =>
        style with { FontSize = style.FontSize * SmallCapsScale, VariantCaps = FontVariantCapsMode.Normal };

    private static string StripSoftHyphens(string text) =>
        text.Contains(SoftHyphen) ? text.Replace(SoftHyphen.ToString(), string.Empty) : text;

    /// <summary>
    /// Paints one text run with every font and decoration property of <paramref name="style"/>,
    /// preceded by its <c>text-shadow</c> layers (when decorated). Soft hyphens are dropped and
    /// small capitals are painted as alternating full-size and reduced runs.
    /// </summary>
    private static void DrawStyledText(LayoutContext context, DisplayList displayList, string text, float x, float y, RenderTextStyle style, bool decorate = true)
    {
        if (style.IsHidden)
        {
            return;
        }

        text = StripSoftHyphens(text);

        // Vertical text: upright glyphs are placed one by one, sideways runs of `vertical-lr`
        // are flipped back (see HtmlRenderer.WritingModes.cs).
        if (TryDrawOrientedText(context, displayList, text, x, y, style, decorate))
        {
            return;
        }

        var flip = NeedsGlyphFlip(style) && !string.IsNullOrWhiteSpace(text);

        if (flip)
        {
            displayList.PushTransform(GlyphFlip(y, style.FontSize));
        }

        if (style.VariantCaps == FontVariantCapsMode.Normal)
        {
            DrawPlainText(displayList, text, x, y, style, decorate);
        }
        else
        {
            foreach (var (run, reduced) in SplitSmallCaps(text, style.VariantCaps))
            {
                var runStyle = reduced ? WithSmallCapsSize(style) : style with { VariantCaps = FontVariantCapsMode.Normal };
                DrawPlainText(displayList, run, x, y, runStyle, decorate);
                x += MeasureTextWidth(context, run, runStyle);
            }
        }

        if (flip)
        {
            displayList.PopTransform();
        }
    }

    private static void DrawPlainText(DisplayList displayList, string text, float x, float y, RenderTextStyle style, bool decorate)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (decorate)
        {
            PaintTextShadows(displayList, style.TextShadows, text, x, y, style);
        }

        displayList.DrawText(
            text,
            x,
            y,
            style.Color,
            style.FontSize,
            style.FontFamily,
            style.FontWeight,
            style.IsItalic,
            decorate && style.Underline,
            decorate && style.StrikeThrough,
            style.DecorationColor,
            style.DecorationStyle,
            style.LetterSpacing,
            style.FontStretch);
    }
}
