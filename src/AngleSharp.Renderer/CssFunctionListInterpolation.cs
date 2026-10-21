using AngleSharp.Renderer.Rendering;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AngleSharp.Renderer;

/// <summary>
/// Interpolates values made of function lists and plain components - `transform`, the individual
/// `translate`/`rotate`/`scale` properties, `filter` and `backdrop-filter` - by structure, as CSS
/// Transforms/Filter Effects define it: lists whose functions match pair by pair interpolate their
/// arguments (numbers with the same unit, a unitless 0 against any unit, colors channel-wise);
/// `none` and a shorter list are padded with the identity function of each counterpart. Transform
/// lists that still do not match are interpolated as decomposed 2D matrices; any other mismatch is
/// discrete and switches at the halfway point.
/// </summary>
internal static class CssFunctionListInterpolation
{
    public static string Interpolate(string property, string from, string to, double t)
    {
        var isTransform = property.Equals("transform", StringComparison.OrdinalIgnoreCase);
        var fromItems = Parse(from);
        var toItems = Parse(to);

        if (fromItems is not null && toItems is not null)
        {
            var (paddedFrom, paddedTo) = Pad(fromItems, toItems, property);

            if (paddedFrom is not null && paddedTo is not null &&
                !(isTransform && (ContainsFunction(paddedFrom, "matrix") || ContainsFunction(paddedTo, "matrix"))) &&
                TryInterpolateList(paddedFrom, paddedTo, t, out var interpolated))
            {
                return Serialize(interpolated);
            }
        }

        if (isTransform && TryInterpolateMatrices(from, to, t, out var matrix))
        {
            return matrix;
        }

        return t < 0.5d ? from : to;
    }

    public static bool AreEquivalent(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A component of a value: a function with arguments, a comma, or a single token.</summary>
    private sealed record Item(string Text, string? FunctionName = null, List<Item>? Arguments = null)
    {
        public bool IsComma => Text == "," && FunctionName is null;
    }

    private static List<Item>? Parse(string value)
    {
        var items = new List<Item>();
        var i = 0;

        while (i < value.Length)
        {
            var c = value[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == ',')
            {
                items.Add(new Item(","));
                i++;
                continue;
            }

            if (c == ')')
            {
                return null;
            }

            var start = i;

            while (i < value.Length && !char.IsWhiteSpace(value[i]) && value[i] is not (',' or '(' or ')'))
            {
                i++;
            }

            if (i < value.Length && value[i] == '(')
            {
                var name = value[start..i];
                var depth = 1;
                var argumentsStart = ++i;

                while (i < value.Length && depth > 0)
                {
                    depth += value[i] switch { '(' => 1, ')' => -1, _ => 0 };
                    i++;
                }

                if (depth != 0)
                {
                    return null;
                }

                if (IsColorFunction(name))
                {
                    items.Add(new Item(value[start..i]));
                    continue;
                }

                if (Parse(value[argumentsStart..(i - 1)]) is not { } arguments)
                {
                    return null;
                }

                items.Add(new Item(value[start..i], name.ToLowerInvariant(), arguments));
                continue;
            }

            items.Add(new Item(value[start..i]));
        }

        return items;
    }

    private static bool IsColorFunction(string name) =>
        name.ToLowerInvariant() is "rgb" or "rgba" or "hsl" or "hsla" or "hwb" or "lab" or "lch" or "oklab" or "oklch" or "color";

    private static bool ContainsFunction(List<Item> items, string name) =>
        items.Any(item => item.FunctionName == name);

    /// <summary>
    /// Replaces `none` with the identity of the other side and pads the shorter of two function
    /// lists with the identities of the longer one's extra functions.
    /// </summary>
    private static (List<Item>? From, List<Item>? To) Pad(List<Item> from, List<Item> to, string property)
    {
        if (property.ToLowerInvariant() is not ("transform" or "filter" or "backdrop-filter"))
        {
            return (from, to);
        }

        static bool IsNone(List<Item> items) => items.Count == 1 && items[0].FunctionName is null && items[0].Text.Equals("none", StringComparison.OrdinalIgnoreCase);

        if (IsNone(from))
        {
            from = [];
        }

        if (IsNone(to))
        {
            to = [];
        }

        if (from.Any(item => item.FunctionName is null) || to.Any(item => item.FunctionName is null))
        {
            return (null, null);
        }

        var paddedFrom = new List<Item>(from);
        var paddedTo = new List<Item>(to);

        for (var i = from.Count; i < to.Count; i++)
        {
            if (Identity(to[i]) is not { } identity)
            {
                return (null, null);
            }

            paddedFrom.Add(identity);
        }

        for (var i = to.Count; i < from.Count; i++)
        {
            if (Identity(from[i]) is not { } identity)
            {
                return (null, null);
            }

            paddedTo.Add(identity);
        }

        return (paddedFrom, paddedTo);
    }

    /// <summary>The identity counterpart of a transform or filter function, keeping its argument shape.</summary>
    private static Item? Identity(Item function)
    {
        var identity = function.FunctionName switch
        {
            "translate" or "translatex" or "translatey" or "translatez" or "translate3d" or
            "rotate" or "rotatex" or "rotatey" or "rotatez" or
            "skew" or "skewx" or "skewy" or "blur" or "grayscale" or "invert" or "sepia" or "hue-rotate" =>
                Rebuild(function, argument => IsNumber(argument, out _, out _) ? "0" : null),
            "scale" or "scalex" or "scaley" or "scalez" or "scale3d" or
            "brightness" or "contrast" or "opacity" or "saturate" =>
                Rebuild(function, argument => IsNumber(argument, out _, out _) ? "1" : null),
            "drop-shadow" =>
                Rebuild(function, argument => IsNumber(argument, out _, out _) ? "0" : IsColor(argument.Text) ? "transparent" : null),
            _ => null,
        };

        return identity is not null && Parse(identity) is [var item] ? item : null;
    }

    private static string? Rebuild(Item function, Func<Item, string?> mapArgument)
    {
        var arguments = new List<string>();

        foreach (var argument in function.Arguments ?? [])
        {
            if (argument.IsComma)
            {
                arguments.Add(",");
                continue;
            }

            if (mapArgument(argument) is not { } mapped)
            {
                return null;
            }

            arguments.Add(mapped);
        }

        return $"{function.FunctionName}({JoinArguments(arguments)})";
    }

    private static bool TryInterpolateList(List<Item> from, List<Item> to, double t, out List<Item> result)
    {
        result = [];

        if (from.Count != to.Count)
        {
            return false;
        }

        for (var i = 0; i < from.Count; i++)
        {
            if (!TryInterpolateItem(from[i], to[i], t, out var item))
            {
                return false;
            }

            result.Add(item);
        }

        return true;
    }

    private static bool TryInterpolateItem(Item from, Item to, double t, out Item result)
    {
        result = from;

        if (from.IsComma || to.IsComma)
        {
            return from.IsComma && to.IsComma;
        }

        if (from.FunctionName is not null || to.FunctionName is not null)
        {
            if (!string.Equals(from.FunctionName, to.FunctionName, StringComparison.Ordinal) ||
                !TryInterpolateList(from.Arguments!, to.Arguments!, t, out var arguments))
            {
                return false;
            }

            result = new Item(string.Empty, from.FunctionName, arguments);
            return true;
        }

        if (IsNumber(from, out var fromNumber, out var fromUnit) && IsNumber(to, out var toNumber, out var toUnit))
        {
            if (!string.Equals(fromUnit, toUnit, StringComparison.OrdinalIgnoreCase) &&
                !(fromUnit.Length == 0 && fromNumber == 0d) && !(toUnit.Length == 0 && toNumber == 0d))
            {
                return false;
            }

            var unit = fromUnit.Length > 0 ? fromUnit : toUnit;
            result = new Item(FormatNumber(fromNumber + ((toNumber - fromNumber) * t)) + unit);
            return true;
        }

        if (IsColor(from.Text) && IsColor(to.Text))
        {
            var fromColor = HtmlRenderer.ParseColor(from.Text, RenderColor.Transparent);
            var toColor = HtmlRenderer.ParseColor(to.Text, RenderColor.Transparent);
            result = new Item(CssValueInterpolation.InterpolateColor(fromColor, toColor, t));
            return true;
        }

        return string.Equals(from.Text, to.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumber(Item item, out double number, out string unit)
    {
        number = 0d;
        unit = string.Empty;

        if (item.FunctionName is not null || item.IsComma)
        {
            return false;
        }

        var text = item.Text;
        var end = 0;

        while (end < text.Length &&
               (char.IsDigit(text[end]) || text[end] is '.' or '-' or '+' ||
                (text[end] is 'e' or 'E' && end > 0 && end + 1 < text.Length && (char.IsDigit(text[end + 1]) || text[end + 1] is '-' or '+'))))
        {
            end++;
        }

        if (end == 0 || !double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        unit = text[end..];
        return unit.All(c => char.IsLetter(c) || c == '%');
    }

    private static bool IsColor(string text) =>
        !text.Equals("none", StringComparison.OrdinalIgnoreCase) &&
        HtmlRenderer.ParseColor(text, new RenderColor(1, 2, 3, 4)) != new RenderColor(1, 2, 3, 4);

    private static string FormatNumber(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Serialize(List<Item> items) =>
        JoinArguments(items.Select(item => item.FunctionName is null ? item.Text : $"{item.FunctionName}({Serialize(item.Arguments!)})"));

    private static string JoinArguments(IEnumerable<string> parts)
    {
        var builder = new StringBuilder();

        foreach (var part in parts)
        {
            if (part == ",")
            {
                builder.Append(", ");
                continue;
            }

            if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Interpolates two transform lists as 2D matrices (CSS Transforms, "Interpolation of
    /// Matrices"): each is decomposed into translation, rotation, skew and scale, the components
    /// interpolate (the rotation the short way round) and are recomposed. Lists with percentages
    /// have no box to resolve against here and stay discrete.
    /// </summary>
    private static bool TryInterpolateMatrices(string from, string to, double t, out string result)
    {
        result = string.Empty;

        if (HtmlRenderer.TryComputeTransformMatrix(from) is not { } fromMatrix ||
            HtmlRenderer.TryComputeTransformMatrix(to) is not { } toMatrix ||
            Decompose(fromMatrix) is not { } a || Decompose(toMatrix) is not { } b)
        {
            return false;
        }

        var fromAngle = a.Angle;
        var toAngle = b.Angle;

        if (Math.Abs(fromAngle - toAngle) > Math.PI)
        {
            if (fromAngle > toAngle)
            {
                fromAngle -= 2d * Math.PI;
            }
            else
            {
                toAngle -= 2d * Math.PI;
            }
        }

        double Lerp(double x, double y) => x + ((y - x) * t);

        var angle = Lerp(fromAngle, toAngle);
        var scaleX = Lerp(a.ScaleX, b.ScaleX);
        var scaleY = Lerp(a.ScaleY, b.ScaleY);
        var skew = Lerp(a.Skew, b.Skew);
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);

        // translate · rotate · skew · scale, in CSS's column-vector matrix(a, b, c, d, e, f) form.
        var m11 = cos * scaleX;
        var m12 = sin * scaleX;
        var m21 = ((cos * skew) - sin) * scaleY;
        var m22 = ((sin * skew) + cos) * scaleY;

        result = string.Create(CultureInfo.InvariantCulture,
            $"matrix({FormatNumber(m11)}, {FormatNumber(m12)}, {FormatNumber(m21)}, {FormatNumber(m22)}, {FormatNumber(Lerp(a.TranslateX, b.TranslateX))}, {FormatNumber(Lerp(a.TranslateY, b.TranslateY))})");
        return true;
    }

    private readonly record struct Decomposed2D(double TranslateX, double TranslateY, double ScaleX, double ScaleY, double Skew, double Angle);

    private static Decomposed2D? Decompose(RenderTransform2D matrix)
    {
        double a = matrix.A, b = matrix.B, c = matrix.C, d = matrix.D;

        if (Math.Abs((a * d) - (b * c)) < 1e-12)
        {
            return null;
        }

        var scaleX = Math.Sqrt((a * a) + (b * b));
        a /= scaleX;
        b /= scaleX;

        var skew = (a * c) + (b * d);
        c -= a * skew;
        d -= b * skew;

        var scaleY = Math.Sqrt((c * c) + (d * d));
        c /= scaleY;
        d /= scaleY;
        skew /= scaleY;

        // A reflection: flip one axis so the remaining rotation is proper.
        if ((a * d) - (b * c) < 0d)
        {
            a = -a;
            b = -b;
            skew = -skew;
            scaleX = -scaleX;
        }

        return new Decomposed2D(matrix.E, matrix.F, scaleX, scaleY, skew, Math.Atan2(b, a));
    }
}
