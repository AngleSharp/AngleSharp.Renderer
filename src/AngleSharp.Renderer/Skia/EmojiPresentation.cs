namespace AngleSharp.Renderer.Skia;


/// <summary>
/// Which characters are drawn as emoji by default (UTS #51), which browsers prefer a color font
/// for over any other font having a glyph for them.
/// </summary>
internal static class EmojiPresentation
{
    public const int TextSelector = 0xFE0E;
    public const int EmojiSelector = 0xFE0F;
    public const int ZeroWidthJoiner = 0x200D;
    private const int Keycap = 0x20E3;

    // Emoji_Presentation=Yes, sorted as (first, last)
    private static readonly (int First, int Last)[] Ranges =
    [
        (0x231A, 0x231B), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
        (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE),
        (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
        (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C),
        (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A), (0x1F1E6, 0x1F1FF), (0x1F201, 0x1F201), (0x1F21A, 0x1F21A), (0x1F22F, 0x1F22F), (0x1F232, 0x1F236),
        (0x1F238, 0x1F23A), (0x1F250, 0x1F251), (0x1F300, 0x1F320), (0x1F32D, 0x1F335), (0x1F337, 0x1F37C), (0x1F37E, 0x1F393),
        (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0), (0x1F3F4, 0x1F3F4), (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440),
        (0x1F442, 0x1F4FC), (0x1F4FF, 0x1F53D), (0x1F54B, 0x1F54E), (0x1F550, 0x1F567), (0x1F57A, 0x1F57A), (0x1F595, 0x1F596),
        (0x1F5A4, 0x1F5A4), (0x1F5FB, 0x1F64F), (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7),
        (0x1F6DC, 0x1F6DF), (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0), (0x1F90C, 0x1F93A),
        (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FAFF),
    ];

    /// <summary>
    /// If the text has a character that may be drawn as an emoji, which only ever costs the
    /// comparison with the first emoji for the text of most pages.
    /// </summary>
    public static bool MayContainEmoji(string text)
    {
        foreach (var character in text)
        {
            if (character >= Ranges[0].First)
            {
                return true;
            }

            if (character == '#' || character == '*' || char.IsAsciiDigit(character) || character == '©' || character == '®' || character == '‼' || character == '⁉' || character == '™' || character == 'ℹ')
            {
                // only an emoji with a selector following
                if (text.Contains((char)EmojiSelector))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// If the character is drawn as an emoji, given the character following it (0 if none).
    /// </summary>
    public static bool IsEmoji(int codepoint, int next) =>
        next == EmojiSelector ? codepoint != EmojiSelector : next != TextSelector && IsEmojiPresentation(codepoint);

    /// <summary>
    /// If the character continues the emoji before it, so stays in its font.
    /// </summary>
    public static bool ContinuesEmoji(int codepoint, int previous) =>
        previous == ZeroWidthJoiner ||
        codepoint is EmojiSelector or ZeroWidthJoiner or Keycap ||
        codepoint is >= 0x1F3FB and <= 0x1F3FF || // skin tones
        codepoint is >= 0xE0020 and <= 0xE007F; // tag sequences (subdivision flags)

    private static bool IsEmojiPresentation(int codepoint)
    {
        var low = 0;
        var high = Ranges.Length - 1;

        while (low <= high)
        {
            var middle = (low + high) / 2;

            if (codepoint < Ranges[middle].First)
            {
                high = middle - 1;
            }
            else if (codepoint > Ranges[middle].Last)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
