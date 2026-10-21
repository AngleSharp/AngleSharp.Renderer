namespace AngleSharp.Renderer;

using System.Globalization;
using System.Linq;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX #9) for one line of text: explicit embeddings,
/// overrides and isolates (rules X1-X10), the weak, neutral and implicit rules (W1-W7, N1-N2,
/// I1-I2), trailing whitespace (L1), reordering (L2) and mirroring (L4).
/// </summary>
/// <remarks>
/// .NET exposes no Bidi_Class data, so <see cref="GetClass"/> derives it from script ranges and
/// general categories, which is exact for the common scripts (Latin, Hebrew, Arabic, Syriac,
/// digits, punctuation). The weak and neutral rules are applied per level run rather than per
/// isolating run sequence, so the text on both sides of an isolate does not influence each
/// other's neutrals.
/// </remarks>
internal static class BidiAlgorithm
{
    public enum BidiClass : byte
    {
        L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON,
        LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
    }

    public const char LeftToRightEmbedding = '‪';
    public const char RightToLeftEmbedding = '‫';
    public const char PopDirectionalFormatting = '‬';
    public const char LeftToRightOverride = '‭';
    public const char RightToLeftOverride = '‮';
    public const char LeftToRightIsolate = '⁦';
    public const char RightToLeftIsolate = '⁧';
    public const char PopDirectionalIsolate = '⁩';

    /// <summary>
    /// A zero-width placeholder (Bidi_Class BN) for content that takes part in the ordering
    /// without affecting it, such as the edges of an inline box.
    /// </summary>
    public const char Placeholder = '⁠';

    /// <summary>The object replacement character, used for atomic inlines (Bidi_Class ON).</summary>
    public const char ObjectReplacement = '￼';

    /// <summary>
    /// Whether <paramref name="text"/> needs the algorithm at all: in a left-to-right paragraph,
    /// text without right-to-left characters, Arabic digits or explicit formatting stays in order.
    /// </summary>
    public static bool RequiresReordering(string text, bool isRightToLeft)
    {
        if (isRightToLeft)
        {
            return true;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (GetClass(rune.Value) is BidiClass.R or BidiClass.AL or BidiClass.AN or BidiClass.RLE or BidiClass.RLO or BidiClass.RLI or BidiClass.FSI or BidiClass.LRE or BidiClass.LRO or BidiClass.LRI)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The embedding level of every UTF-16 code unit of one line.
    /// </summary>
    public static byte[] GetLevels(string text, bool isRightToLeft)
    {
        var length = text.Length;
        var paragraphLevel = (byte)(isRightToLeft ? 1 : 0);
        var classes = new BidiClass[length];
        var levels = new byte[length];

        for (var index = 0; index < length; index++)
        {
            if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1]))
            {
                classes[index] = classes[index - 1];
                continue;
            }

            var codePoint = char.IsHighSurrogate(text[index]) && index + 1 < length && char.IsLowSurrogate(text[index + 1])
                ? char.ConvertToUtf32(text[index], text[index + 1])
                : text[index];
            classes[index] = GetClass(codePoint);
        }

        var originalClasses = (BidiClass[])classes.Clone();
        ResolveExplicitLevels(classes, levels, paragraphLevel);

        // X9/X10: removed characters (BN) are skipped by the remaining rules; the rest form level
        // runs, each with the direction of the higher neighboring level at its edges.
        var indices = new List<int>(length);

        for (var index = 0; index < length; index++)
        {
            if (classes[index] != BidiClass.BN)
            {
                indices.Add(index);
            }
        }

        var start = 0;

        while (start < indices.Count)
        {
            var level = levels[indices[start]];
            var end = start;

            while (end + 1 < indices.Count && levels[indices[end + 1]] == level)
            {
                end++;
            }

            var previousLevel = start > 0 ? levels[indices[start - 1]] : paragraphLevel;
            var nextLevel = end + 1 < indices.Count ? levels[indices[end + 1]] : paragraphLevel;
            var sos = (Math.Max(previousLevel, level) & 1) == 1 ? BidiClass.R : BidiClass.L;
            var eos = (Math.Max(nextLevel, level) & 1) == 1 ? BidiClass.R : BidiClass.L;
            var run = indices.GetRange(start, end - start + 1);

            ResolveWeakTypes(classes, run, sos);
            ResolveNeutralTypes(classes, run, sos, eos, level);
            ResolveImplicitLevels(classes, levels, run);

            start = end + 1;
        }

        // Removed characters take the level of what precedes them, so they stay in place.
        for (var index = 0; index < length; index++)
        {
            if (classes[index] == BidiClass.BN)
            {
                levels[index] = index > 0 ? levels[index - 1] : paragraphLevel;
            }
        }

        // L1: trailing whitespace (and isolate/embedding formatting characters) go back to the
        // paragraph level, as do segment separators and the whitespace before them.
        var resetting = true;

        for (var index = length - 1; index >= 0; index--)
        {
            var original = originalClasses[index];

            if (original is BidiClass.S or BidiClass.B)
            {
                levels[index] = paragraphLevel;
                resetting = true;
            }
            else if (resetting && original is BidiClass.WS or BidiClass.BN or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI or BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF)
            {
                levels[index] = paragraphLevel;
            }
            else
            {
                resetting = false;
            }
        }

        return levels;
    }

    /// <summary>
    /// The logical indices of a line in visual order (L2): from the highest level down to the
    /// lowest odd level, every maximal run at or above that level is reversed.
    /// </summary>
    public static int[] GetVisualOrder(byte[] levels)
    {
        var order = new int[levels.Length];

        for (var index = 0; index < order.Length; index++)
        {
            order[index] = index;
        }

        if (levels.Length == 0)
        {
            return order;
        }

        var highest = levels.Max();
        var lowestOdd = levels.Where(static level => (level & 1) == 1).DefaultIfEmpty((byte)(highest + 1)).Min();

        for (var level = highest; level >= lowestOdd && level > 0; level--)
        {
            var index = 0;

            while (index < order.Length)
            {
                if (levels[order[index]] < level)
                {
                    index++;
                    continue;
                }

                var end = index;

                while (end + 1 < order.Length && levels[order[end + 1]] >= level)
                {
                    end++;
                }

                Array.Reverse(order, index, end - index + 1);
                index = end + 1;
            }
        }

        return order;
    }

    /// <summary>The mirrored glyph of a Bidi_Mirrored character in right-to-left text (L4).</summary>
    public static char Mirror(char value) => value switch
    {
        '(' => ')',
        ')' => '(',
        '[' => ']',
        ']' => '[',
        '{' => '}',
        '}' => '{',
        '<' => '>',
        '>' => '<',
        '«' => '»',
        '»' => '«',
        '‹' => '›',
        '›' => '‹',
        '≤' => '≥',
        '≥' => '≤',
        '⟨' => '⟩',
        '⟩' => '⟨',
        _ => value,
    };

    /// <summary>
    /// Whether a character is an explicit formatting character or the placeholder, none of
    /// which is ever painted.
    /// </summary>
    public static bool IsFormattingCharacter(char value) =>
        value is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or Placeholder or '‎' or '‏' or '؜';

    private static void ResolveExplicitLevels(BidiClass[] classes, byte[] levels, byte paragraphLevel)
    {
        // X1-X8, with overflow handling simplified to ignoring pushes beyond depth 125.
        var stack = new Stack<(byte Level, BidiClass Override, bool Isolate)>();
        stack.Push((paragraphLevel, BidiClass.ON, false));

        for (var index = 0; index < classes.Length; index++)
        {
            var (level, overrideClass, _) = stack.Peek();
            var current = classes[index];

            switch (current)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                {
                    var isRightToLeft = current is BidiClass.RLE or BidiClass.RLO;
                    var next = (byte)(isRightToLeft ? (level + 1) | 1 : (level + 2) & ~1);
                    levels[index] = level;
                    classes[index] = BidiClass.BN;

                    if (next <= 125)
                    {
                        var pushedOverride = current switch
                        {
                            BidiClass.RLO => BidiClass.R,
                            BidiClass.LRO => BidiClass.L,
                            _ => BidiClass.ON,
                        };
                        stack.Push((next, pushedOverride, false));
                    }

                    break;
                }
                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                {
                    // The isolate initiator itself belongs to the outer level and acts as a neutral.
                    levels[index] = level;
                    classes[index] = overrideClass == BidiClass.ON ? BidiClass.ON : overrideClass;
                    var next = (byte)(current == BidiClass.RLI ? (level + 1) | 1 : (level + 2) & ~1);

                    if (next <= 125)
                    {
                        stack.Push((next, BidiClass.ON, true));
                    }

                    break;
                }
                case BidiClass.PDI:
                {
                    if (stack.Any(static entry => entry.Isolate))
                    {
                        while (!stack.Peek().Isolate)
                        {
                            stack.Pop();
                        }

                        stack.Pop();
                    }

                    var (outerLevel, outerOverride, _) = stack.Peek();
                    levels[index] = outerLevel;
                    classes[index] = outerOverride == BidiClass.ON ? BidiClass.ON : outerOverride;
                    break;
                }
                case BidiClass.PDF:
                {
                    if (stack.Count > 1 && !stack.Peek().Isolate)
                    {
                        stack.Pop();
                    }

                    levels[index] = stack.Peek().Level;
                    classes[index] = BidiClass.BN;
                    break;
                }
                case BidiClass.B:
                    levels[index] = paragraphLevel;
                    break;
                case BidiClass.BN:
                    levels[index] = level;
                    break;
                default:
                    levels[index] = level;

                    if (overrideClass != BidiClass.ON)
                    {
                        classes[index] = overrideClass;
                    }

                    break;
            }
        }
    }

    private static void ResolveWeakTypes(BidiClass[] classes, List<int> run, BidiClass sos)
    {
        // W1: a nonspacing mark takes the type of what precedes it.
        var previous = sos;

        foreach (var index in run)
        {
            if (classes[index] == BidiClass.NSM)
            {
                classes[index] = previous is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON : previous;
            }

            previous = classes[index];
        }

        // W2: European digits after Arabic letters are Arabic digits. W3: AL becomes R.
        var lastStrong = sos;

        foreach (var index in run)
        {
            switch (classes[index])
            {
                case BidiClass.EN when lastStrong == BidiClass.AL:
                    classes[index] = BidiClass.AN;
                    break;
                case BidiClass.L or BidiClass.R or BidiClass.AL:
                    lastStrong = classes[index];
                    break;
            }
        }

        foreach (var index in run)
        {
            if (classes[index] == BidiClass.AL)
            {
                classes[index] = BidiClass.R;
            }
        }

        // W4: a single separator between two numbers of the same kind joins them.
        for (var position = 1; position < run.Count - 1; position++)
        {
            var before = classes[run[position - 1]];
            var after = classes[run[position + 1]];
            var current = classes[run[position]];

            if (current == BidiClass.ES && before == BidiClass.EN && after == BidiClass.EN)
            {
                classes[run[position]] = BidiClass.EN;
            }
            else if (current == BidiClass.CS && before == after && before is BidiClass.EN or BidiClass.AN)
            {
                classes[run[position]] = before;
            }
        }

        // W5: terminators next to European digits become European digits.
        for (var position = 0; position < run.Count; position++)
        {
            if (classes[run[position]] != BidiClass.ET)
            {
                continue;
            }

            var end = position;

            while (end + 1 < run.Count && classes[run[end + 1]] == BidiClass.ET)
            {
                end++;
            }

            var touchesNumber = (position > 0 && classes[run[position - 1]] == BidiClass.EN) ||
                                (end + 1 < run.Count && classes[run[end + 1]] == BidiClass.EN);

            if (touchesNumber)
            {
                for (var fill = position; fill <= end; fill++)
                {
                    classes[run[fill]] = BidiClass.EN;
                }
            }

            position = end;
        }

        // W6: remaining separators and terminators are neutral.
        foreach (var index in run)
        {
            if (classes[index] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
            {
                classes[index] = BidiClass.ON;
            }
        }

        // W7: European digits after left-to-right text are left-to-right.
        lastStrong = sos;

        foreach (var index in run)
        {
            switch (classes[index])
            {
                case BidiClass.EN when lastStrong == BidiClass.L:
                    classes[index] = BidiClass.L;
                    break;
                case BidiClass.L or BidiClass.R:
                    lastStrong = classes[index];
                    break;
            }
        }
    }

    private static bool IsNeutral(BidiClass value) =>
        value is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON or BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI;

    private static void ResolveNeutralTypes(BidiClass[] classes, List<int> run, BidiClass sos, BidiClass eos, byte level)
    {
        // N1: neutrals between two strong types of the same direction take that direction
        // (numbers count as right-to-left). N2: the rest take the embedding direction.
        static BidiClass Strength(BidiClass value) => value is BidiClass.EN or BidiClass.AN ? BidiClass.R : value;

        var embedding = (level & 1) == 1 ? BidiClass.R : BidiClass.L;

        for (var position = 0; position < run.Count; position++)
        {
            if (!IsNeutral(classes[run[position]]))
            {
                continue;
            }

            var end = position;

            while (end + 1 < run.Count && IsNeutral(classes[run[end + 1]]))
            {
                end++;
            }

            var before = position > 0 ? Strength(classes[run[position - 1]]) : sos;
            var after = end + 1 < run.Count ? Strength(classes[run[end + 1]]) : eos;
            var resolved = before == after ? before : embedding;

            for (var fill = position; fill <= end; fill++)
            {
                classes[run[fill]] = resolved;
            }

            position = end;
        }
    }

    private static void ResolveImplicitLevels(BidiClass[] classes, byte[] levels, List<int> run)
    {
        foreach (var index in run)
        {
            var level = levels[index];
            var value = classes[index];

            if ((level & 1) == 0)
            {
                // I1
                levels[index] = value switch
                {
                    BidiClass.R => (byte)(level + 1),
                    BidiClass.AN or BidiClass.EN => (byte)(level + 2),
                    _ => level,
                };
            }
            else if (value is BidiClass.L or BidiClass.EN or BidiClass.AN)
            {
                // I2
                levels[index] = (byte)(level + 1);
            }
        }
    }

    /// <summary>
    /// The Bidi_Class of a code point, derived from script ranges and general categories.
    /// </summary>
    public static BidiClass GetClass(int codePoint)
    {
        switch (codePoint)
        {
            case 0x202A: return BidiClass.LRE;
            case 0x202B: return BidiClass.RLE;
            case 0x202C: return BidiClass.PDF;
            case 0x202D: return BidiClass.LRO;
            case 0x202E: return BidiClass.RLO;
            case 0x2066: return BidiClass.LRI;
            case 0x2067: return BidiClass.RLI;
            case 0x2068: return BidiClass.FSI;
            case 0x2069: return BidiClass.PDI;
            case 0x200E: return BidiClass.L;
            case 0x200F: return BidiClass.R;
            case 0x061C: return BidiClass.AL;
            case 0x2060: return BidiClass.BN;
            case 0x000A or 0x000D or 0x001C or 0x001D or 0x001E or 0x0085 or 0x2029: return BidiClass.B;
            case 0x0009 or 0x000B or 0x001F: return BidiClass.S;
            case 0x0020 or 0x000C or 0x2028 or 0x205F or 0x3000 or 0x1680: return BidiClass.WS;
            case >= 0x2000 and <= 0x200A: return BidiClass.WS;
            case >= 0x0030 and <= 0x0039: return BidiClass.EN;
            case >= 0x06F0 and <= 0x06F9: return BidiClass.EN;
            case 0x00B2 or 0x00B3 or 0x00B9: return BidiClass.EN;
            case >= 0x2070 and <= 0x2079: return BidiClass.EN;
            case >= 0x2080 and <= 0x2089: return BidiClass.EN;
            case >= 0xFF10 and <= 0xFF19: return BidiClass.EN;
            case >= 0x0660 and <= 0x0669: return BidiClass.AN;
            case 0x066B or 0x066C or 0x06DD: return BidiClass.AN;
            case >= 0x0600 and <= 0x0605: return BidiClass.AN;
            case 0x002B or 0x002D or 0x2212 or 0xFB29 or 0xFE62 or 0xFE63 or 0xFF0B or 0xFF0D: return BidiClass.ES;
            case 0x0023 or 0x0024 or 0x0025 or 0x00A2 or 0x00A3 or 0x00A4 or 0x00A5 or 0x00B0 or 0x00B1 or 0x2030 or 0x2031 or 0x2032 or 0x2033 or 0x2034 or 0x066A: return BidiClass.ET;
            case 0x002C or 0x002E or 0x002F or 0x003A or 0x00A0 or 0x060C or 0x202F or 0x2044 or 0xFE50 or 0xFE52 or 0xFE55 or 0xFF0C or 0xFF0E or 0xFF0F or 0xFF1A: return BidiClass.CS;
        }

        if (codePoint is (>= 0x0590 and <= 0x05FF) or (>= 0x07C0 and <= 0x085F) or (>= 0xFB1D and <= 0xFB4F) or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EC6F) or (>= 0x1ED00 and <= 0x1EDFF))
        {
            return IsMark(codePoint) ? BidiClass.NSM : BidiClass.R;
        }

        if (codePoint is (>= 0x0600 and <= 0x07BF) or (>= 0x0860 and <= 0x08FF) or (>= 0xFB50 and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF) or (>= 0x1EC70 and <= 0x1ECFF) or (>= 0x1EE00 and <= 0x1EEFF))
        {
            return IsMark(codePoint) ? BidiClass.NSM : BidiClass.AL;
        }

        if (codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return BidiClass.L;
        }

        return CharUnicodeInfo.GetUnicodeCategory(codePoint) switch
        {
            UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark => BidiClass.NSM,
            UnicodeCategory.Format or UnicodeCategory.Control => BidiClass.BN,
            UnicodeCategory.CurrencySymbol => BidiClass.ET,
            UnicodeCategory.SpaceSeparator => BidiClass.WS,
            UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol => BidiClass.ON,
            _ => BidiClass.L,
        };
    }

    private static bool IsMark(int codePoint) =>
        CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
}
