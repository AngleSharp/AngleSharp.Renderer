namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;

using SkiaSharp;

/// <summary>
/// Parses SVG/CSS paint values (colors, "none", "currentColor" and a common set of named colors)
/// into <see cref="SKColor"/>. Scoped to the presentation-attribute subset SVG rendering needs -
/// not a general CSS color parser.
/// </summary>
internal static class SvgColorParsing
{
    /// <summary>
    /// Attempts to parse a paint value. Returns <c>false</c> for "none" (explicitly unpainted) or
    /// an unrecognized value.
    /// </summary>
    public static bool TryParsePaint(string? value, out SKColor color)
    {
        color = SKColors.Black;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // currentColor would need the CSS `color` property resolved from an ancestor; without a
        // wider style cascade to resolve it against, black is a reasonable fallback.
        if (string.Equals(trimmed, "currentColor", StringComparison.OrdinalIgnoreCase))
        {
            color = SKColors.Black;
            return true;
        }

        if (trimmed.StartsWith('#'))
        {
            return TryParseHex(trimmed, out color);
        }

        if (trimmed.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgbFunction(trimmed, out color);
        }

        return TryParseNamedColor(trimmed, out color);
    }

    private static bool TryParseHex(string value, out SKColor color)
    {
        color = SKColors.Black;
        var hex = value[1..];

        static bool TryHexByte(string s, out byte result) =>
            byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);

        static byte Expand(char c) => (byte)(Uri.FromHex(c) * 16 + Uri.FromHex(c));

        try
        {
            switch (hex.Length)
            {
                case 3:
                    color = new SKColor(Expand(hex[0]), Expand(hex[1]), Expand(hex[2]));
                    return true;
                case 4:
                    color = new SKColor(Expand(hex[0]), Expand(hex[1]), Expand(hex[2]), Expand(hex[3]));
                    return true;
                case 6 when TryHexByte(hex[..2], out var r) && TryHexByte(hex[2..4], out var g) && TryHexByte(hex[4..6], out var b):
                    color = new SKColor(r, g, b);
                    return true;
                case 8 when TryHexByte(hex[..2], out var r2) && TryHexByte(hex[2..4], out var g2) && TryHexByte(hex[4..6], out var b2) && TryHexByte(hex[6..8], out var a2):
                    color = new SKColor(r2, g2, b2, a2);
                    return true;
                default:
                    return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseRgbFunction(string value, out SKColor color)
    {
        color = SKColors.Black;

        var openParen = value.IndexOf('(');
        var closeParen = value.LastIndexOf(')');

        if (openParen < 0 || closeParen <= openParen)
        {
            return false;
        }

        var content = value[(openParen + 1)..closeParen];
        var separators = content.Contains(',') ? new[] { ',' } : new[] { ' ' };
        var parts = content.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 3)
        {
            return false;
        }

        if (!TryParseColorChannel(parts[0], out var r) ||
            !TryParseColorChannel(parts[1], out var g) ||
            !TryParseColorChannel(parts[2], out var b))
        {
            return false;
        }

        byte a = 255;

        if (parts.Length >= 4 && float.TryParse(parts[3].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
        {
            a = (byte)Math.Clamp(Math.Round(alpha * 255f), 0, 255);
        }

        color = new SKColor(r, g, b, a);
        return true;
    }

    private static bool TryParseColorChannel(string token, out byte value)
    {
        value = 0;

        if (token.EndsWith('%'))
        {
            if (!float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return false;
            }

            value = (byte)Math.Clamp(Math.Round(percent * 255f / 100f), 0, 255);
            return true;
        }

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        value = (byte)Math.Clamp(Math.Round(raw), 0, 255);
        return true;
    }

    private static bool TryParseNamedColor(string name, out SKColor color)
    {
        var found = name.ToLowerInvariant() switch
        {
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => new SKColor(0, 128, 0),
            "blue" => SKColors.Blue,
            "yellow" => SKColors.Yellow,
            "orange" => new SKColor(255, 165, 0),
            "purple" => new SKColor(128, 0, 128),
            "gray" or "grey" => new SKColor(128, 128, 128),
            "silver" => new SKColor(192, 192, 192),
            "cyan" or "aqua" => SKColors.Cyan,
            "magenta" or "fuchsia" => SKColors.Magenta,
            "lime" => new SKColor(0, 255, 0),
            "navy" => new SKColor(0, 0, 128),
            "teal" => new SKColor(0, 128, 128),
            "olive" => new SKColor(128, 128, 0),
            "maroon" => new SKColor(128, 0, 0),
            "pink" => new SKColor(255, 192, 203),
            "brown" => new SKColor(165, 42, 42),
            "gold" => new SKColor(255, 215, 0),
            "indigo" => new SKColor(75, 0, 130),
            "violet" => new SKColor(238, 130, 238),
            "coral" => new SKColor(255, 127, 80),
            "salmon" => new SKColor(250, 128, 114),
            "khaki" => new SKColor(240, 230, 140),
            "crimson" => new SKColor(220, 20, 60),
            "turquoise" => new SKColor(64, 224, 208),
            "beige" => new SKColor(245, 245, 220),
            "ivory" => new SKColor(255, 255, 240),
            "lavender" => new SKColor(230, 230, 250),
            "chocolate" => new SKColor(210, 105, 30),
            "tomato" => new SKColor(255, 99, 71),
            "orchid" => new SKColor(218, 112, 214),
            "plum" => new SKColor(221, 160, 221),
            "skyblue" => new SKColor(135, 206, 235),
            "steelblue" => new SKColor(70, 130, 180),
            "royalblue" => new SKColor(65, 105, 225),
            "forestgreen" => new SKColor(34, 139, 34),
            "seagreen" => new SKColor(46, 139, 87),
            "darkgreen" => new SKColor(0, 100, 0),
            "darkred" => new SKColor(139, 0, 0),
            "darkblue" => new SKColor(0, 0, 139),
            "lightblue" => new SKColor(173, 216, 230),
            "lightgreen" => new SKColor(144, 238, 144),
            "lightgray" or "lightgrey" => new SKColor(211, 211, 211),
            "darkgray" or "darkgrey" => new SKColor(169, 169, 169),
            _ => (SKColor?)null,
        };

        if (found is null)
        {
            color = SKColors.Black;
            return false;
        }

        color = found.Value;
        return true;
    }
}
