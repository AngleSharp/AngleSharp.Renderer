using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Text;
using System.Globalization;

namespace AngleSharp.Renderer;

/// <summary>
/// Which kind of value an interpolatable CSS property carries, so it can be lerped and
/// re-serialized correctly.
/// </summary>
internal enum CssInterpolationValueKind
{
    Color,
    LengthPixels,
    Number,

    /// <summary>
    /// A list of functions or components (`transform`, `filter`, `translate`, ...), interpolated by
    /// structure (<see cref="CssFunctionListInterpolation"/>).
    /// </summary>
    FunctionList,
}

/// <summary>
/// A parsed CSS easing function - either `cubic-bezier(x1, y1, x2, y2)` (which every named keyword
/// - `ease`/`ease-in`/`ease-in-out`/`ease-out`/`linear` - normalizes to via AngleSharp.Css's own
/// computed style, confirmed empirically) or `steps(count, start|end)`. Shared by `transition` and
/// `animation`, since both use the identical CSS timing-function grammar and evaluation.
/// </summary>
internal readonly record struct CssTimingFunction(bool IsSteps, double X1, double Y1, double X2, double Y2, int StepCount, bool JumpAtStart)
{
    public static readonly CssTimingFunction Linear = CubicBezier(0d, 0d, 1d, 1d);

    public static CssTimingFunction CubicBezier(double x1, double y1, double x2, double y2) =>
        new(false, x1, y1, x2, y2, 0, false);

    public static CssTimingFunction Steps(int count, bool jumpAtStart) =>
        new(true, 0, 0, 0, 0, Math.Max(1, count), jumpAtStart);

    /// <summary>
    /// Evaluates this easing function at <paramref name="t"/> (0..1, the linear fraction of the
    /// duration elapsed) and returns the eased progress (also 0..1).
    /// </summary>
    public double Evaluate(double t)
    {
        t = Math.Clamp(t, 0d, 1d);

        if (IsSteps)
        {
            var step = JumpAtStart ? Math.Floor(t * StepCount) + 1d : Math.Floor(t * StepCount);
            return Math.Clamp(step / StepCount, 0d, 1d);
        }

        return SolveCubicBezier(X1, Y1, X2, Y2, t);
    }

    // The standard "UnitBezier" technique browsers use: the curve's own parameter (call it u) is
    // not the same as elapsed time t - t is the *x* coordinate of the curve, and the eased progress
    // is the *y* coordinate of the point on the curve whose x equals t. Bisection search finds the
    // u whose x(u) matches t closely enough, then evaluates y(u) - simpler to get right than
    // Newton-Raphson and fast enough here, since this runs once per interpolated property per
    // paint, not once per frame in a tight interactive render loop.
    private static double SolveCubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0d)
        {
            return 0d;
        }

        if (x >= 1d)
        {
            return 1d;
        }

        var lo = 0d;
        var hi = 1d;
        var u = x;

        for (var i = 0; i < 30; i++)
        {
            u = (lo + hi) / 2d;
            var currentX = BezierComponent(u, x1, x2);

            if (Math.Abs(currentX - x) < 1e-7d)
            {
                break;
            }

            if (currentX < x)
            {
                lo = u;
            }
            else
            {
                hi = u;
            }
        }

        return BezierComponent(u, y1, y2);
    }

    // The cubic Bezier value at parameter u for control points (0,0), (p1,_), (p2,_), (1,1) - the
    // endpoints are fixed at 0 and 1 for both x(u) and y(u) per the CSS timing-function definition,
    // so only the two interior control coordinates are needed.
    private static double BezierComponent(double u, double p1, double p2)
    {
        var v = 1d - u;
        return (3d * v * v * u * p1) + (3d * v * u * u * p2) + (u * u * u);
    }
}

/// <summary>
/// Shared value-interpolation machinery for CSS `transition` (<see cref="CssTransitionTracker"/>)
/// and `animation` (<see cref="CssAnimationTracker"/>) - both animate an explicit whitelist of
/// properties: colors (channel-wise), numeric lengths and `opacity` (plain numeric lerp), and the
/// function lists `transform`/`translate`/`rotate`/`scale`/`filter`/`backdrop-filter` (by structure,
/// <see cref="CssFunctionListInterpolation"/>). `transition-property: all` (and an `animation`
/// keyframe declaring any other property) is limited to this same whitelist.
/// </summary>
internal static class CssValueInterpolation
{
    public static readonly IReadOnlyDictionary<string, CssInterpolationValueKind> InterpolatableProperties = new Dictionary<string, CssInterpolationValueKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["background-color"] = CssInterpolationValueKind.Color,
        ["color"] = CssInterpolationValueKind.Color,
        ["border-top-color"] = CssInterpolationValueKind.Color,
        ["border-right-color"] = CssInterpolationValueKind.Color,
        ["border-bottom-color"] = CssInterpolationValueKind.Color,
        ["border-left-color"] = CssInterpolationValueKind.Color,
        ["opacity"] = CssInterpolationValueKind.Number,
        ["width"] = CssInterpolationValueKind.LengthPixels,
        ["height"] = CssInterpolationValueKind.LengthPixels,
        ["font-size"] = CssInterpolationValueKind.LengthPixels,
        ["margin-top"] = CssInterpolationValueKind.LengthPixels,
        ["margin-right"] = CssInterpolationValueKind.LengthPixels,
        ["margin-bottom"] = CssInterpolationValueKind.LengthPixels,
        ["margin-left"] = CssInterpolationValueKind.LengthPixels,
        ["padding-top"] = CssInterpolationValueKind.LengthPixels,
        ["padding-right"] = CssInterpolationValueKind.LengthPixels,
        ["padding-bottom"] = CssInterpolationValueKind.LengthPixels,
        ["padding-left"] = CssInterpolationValueKind.LengthPixels,
        ["border-top-width"] = CssInterpolationValueKind.LengthPixels,
        ["border-right-width"] = CssInterpolationValueKind.LengthPixels,
        ["border-bottom-width"] = CssInterpolationValueKind.LengthPixels,
        ["border-left-width"] = CssInterpolationValueKind.LengthPixels,
        ["top"] = CssInterpolationValueKind.LengthPixels,
        ["left"] = CssInterpolationValueKind.LengthPixels,
        ["right"] = CssInterpolationValueKind.LengthPixels,
        ["bottom"] = CssInterpolationValueKind.LengthPixels,
        ["transform"] = CssInterpolationValueKind.FunctionList,
        ["translate"] = CssInterpolationValueKind.FunctionList,
        ["rotate"] = CssInterpolationValueKind.FunctionList,
        ["scale"] = CssInterpolationValueKind.FunctionList,
        ["filter"] = CssInterpolationValueKind.FunctionList,
        ["backdrop-filter"] = CssInterpolationValueKind.FunctionList,
    };

    public static CssInterpolationValueKind KindOf(string property) =>
        InterpolatableProperties.TryGetValue(property, out var kind) ? kind : CssInterpolationValueKind.Number;

    /// <summary>
    /// The value of <paramref name="property"/> in <paramref name="style"/> in a form
    /// <see cref="Interpolate"/> understands, or <see langword="null"/> when it has none. With an
    /// <paramref name="element"/> (an element's own current value, as opposed to a keyframe's
    /// declaration), an unset function list counts as its initial `none`, so it can transition
    /// from and to nothing.
    /// </summary>
    public static string? ReadNaturalValue(ICssStyleDeclaration style, string property, CssInterpolationValueKind kind, IElement? element = null)
    {
        var raw = property.Equals("opacity", StringComparison.OrdinalIgnoreCase)
            ? style.GetOpacity()
            : property.Equals("transform", StringComparison.OrdinalIgnoreCase) && element is not null
                ? HtmlRenderer.ReadAuthoredTransform(element)
                : style.GetPropertyValue(property);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return kind == CssInterpolationValueKind.FunctionList && element is not null ? "none" : null;
        }

        return kind switch
        {
            CssInterpolationValueKind.Color => raw,
            CssInterpolationValueKind.Number => float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? raw.Trim() : null,
            CssInterpolationValueKind.LengthPixels => TryParsePercentage(raw, out _) || !float.IsNaN(HtmlRenderer.ParseLengthValue(raw, float.NaN, allowAuto: false)) ? raw : null,
            CssInterpolationValueKind.FunctionList => raw.Trim(),
            _ => null,
        };
    }

    /// <summary>
    /// The natural value of an element's authored `transform` (see <see cref="ReadNaturalValue"/>),
    /// e.g. the one of its style map.
    /// </summary>
    public static string ReadNaturalTransform(string? authored) =>
        string.IsNullOrWhiteSpace(authored) ? "none" : authored.Trim();

    public static bool ValuesAreEquivalent(CssInterpolationValueKind kind, string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return kind switch
        {
            CssInterpolationValueKind.Color => HtmlRenderer.ParseColor(a, RenderColor.Transparent) == HtmlRenderer.ParseColor(b, RenderColor.Transparent),
            CssInterpolationValueKind.LengthPixels when TryParsePercentage(a, out var pa) | TryParsePercentage(b, out var pb) =>
                TryParsePercentage(a, out _) && TryParsePercentage(b, out _) && Math.Abs(pa - pb) < 0.01f,
            CssInterpolationValueKind.Number or CssInterpolationValueKind.LengthPixels =>
                Math.Abs(HtmlRenderer.ParseLengthValue(a, 0f, allowAuto: false) - HtmlRenderer.ParseLengthValue(b, 0f, allowAuto: false)) < 0.01f,
            CssInterpolationValueKind.FunctionList => CssFunctionListInterpolation.AreEquivalent(a, b),
            _ => false,
        };
    }

    public static string Interpolate(string property, string from, string to, double t)
    {
        var kind = KindOf(property);

        if (kind == CssInterpolationValueKind.Color)
        {
            return InterpolateColor(HtmlRenderer.ParseColor(from, RenderColor.Transparent), HtmlRenderer.ParseColor(to, RenderColor.Transparent), t);
        }

        if (kind == CssInterpolationValueKind.FunctionList)
        {
            return CssFunctionListInterpolation.Interpolate(property, from, to, t);
        }

        // percentages (e.g. an overlay shrinking from 100% to 90%) stay percentages, mixed with
        // a length in a calc()
        var fromIsPercentage = TryParsePercentage(from, out var fromPercentage);
        var toIsPercentage = TryParsePercentage(to, out var toPercentage);

        if (kind == CssInterpolationValueKind.LengthPixels && (fromIsPercentage || toIsPercentage))
        {
            var fromLength = fromIsPercentage ? 0f : HtmlRenderer.ParseLengthValue(from, 0f, allowAuto: false);
            var toLength = toIsPercentage ? 0f : HtmlRenderer.ParseLengthValue(to, 0f, allowAuto: false);
            var percentage = (fromIsPercentage ? fromPercentage : 0f) + (((toIsPercentage ? toPercentage : 0f) - (fromIsPercentage ? fromPercentage : 0f)) * t);
            var length = fromLength + ((toLength - fromLength) * t);
            var percentageText = percentage.ToString("0.####", CultureInfo.InvariantCulture);
            return fromIsPercentage && toIsPercentage
                ? $"{percentageText}%"
                : string.Create(CultureInfo.InvariantCulture, $"calc({percentageText}% + {length.ToString("0.####", CultureInfo.InvariantCulture)}px)");
        }

        var fromNumber = HtmlRenderer.ParseLengthValue(from, 0f, allowAuto: false);
        var toNumber = HtmlRenderer.ParseLengthValue(to, 0f, allowAuto: false);
        var interpolated = fromNumber + ((toNumber - fromNumber) * t);

        return kind == CssInterpolationValueKind.Number
            ? interpolated.ToString("0.####", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{interpolated.ToString("0.####", CultureInfo.InvariantCulture)}px");
    }

    /// <summary>
    /// The value of a custom property between two of its values. Only a registered one has a type
    /// (CSS Properties and Values API 1, 2.6): its numbers of the same unit (e.g. 0deg and 180deg,
    /// or 5% and 95%) and its colors are interpolated; anything else, like the value of a property
    /// that is not registered, flips halfway.
    /// </summary>
    public static string InterpolateCustom(AngleSharp.Css.Dom.ICssPropertyRegistration? registration, string from, string to, double t)
    {
        if (registration is { IsUniversal: false, IsList: false })
        {
            var syntax = registration.Syntax;

            if (syntax.Contains("<color>", StringComparison.Ordinal) && !TrySplitDimension(from, out _, out _) && !TrySplitDimension(to, out _, out _))
            {
                return InterpolateColor(HtmlRenderer.ParseColor(from, RenderColor.Transparent), HtmlRenderer.ParseColor(to, RenderColor.Transparent), t);
            }

            if (TrySplitDimension(from, out var a, out var unit) && TrySplitDimension(to, out var b, out var otherUnit) &&
                (unit.Equals(otherUnit, StringComparison.OrdinalIgnoreCase) || a == 0d || b == 0d))
            {
                var value = a + ((b - a) * t);
                var text = syntax.Contains("<integer>", StringComparison.Ordinal) && unit.Length == 0
                    ? Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
                    : value.ToString("0.####", CultureInfo.InvariantCulture);
                return text + (unit.Length > 0 ? unit : otherUnit);
            }
        }

        return t < 0.5d ? from : to;
    }

    // a number with its unit, if any, e.g. 37.5deg, 5% or 2
    private static bool TrySplitDimension(string value, out double number, out string unit)
    {
        var text = value.Trim();
        var end = 0;

        while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or '-' or '+' || (end > 0 && text[end] is 'e' or 'E' && end + 1 < text.Length && (char.IsDigit(text[end + 1]) || text[end + 1] is '-' or '+'))))
        {
            end++;
        }

        unit = text[end..];

        if (!double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
            !(unit.Length == 0 || unit == "%" || System.Linq.Enumerable.All(unit, char.IsLetter)))
        {
            return false;
        }

        // an angle in deg, however written (e.g. as computed or as declared)
        switch (unit.ToLowerInvariant())
        {
            case "rad": number *= 180d / Math.PI; unit = "deg"; break;
            case "grad": number *= 0.9d; unit = "deg"; break;
            case "turn": number *= 360d; unit = "deg"; break;
        }

        return true;
    }

    /// <summary>
    /// Gets if two values of a custom property are the same, e.g. 180deg and 0.5turn.
    /// </summary>
    public static bool CustomValuesAreEquivalent(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal))
        {
            return true;
        }
        else if (TrySplitDimension(a, out var x, out var unit) && TrySplitDimension(b, out var y, out var otherUnit))
        {
            return unit.Equals(otherUnit, StringComparison.OrdinalIgnoreCase) && Math.Abs(x - y) < 1e-6;
        }

        // e.g. rgb(0, 0, 255) as declared and rgba(0, 0, 255, 1) as computed
        return TryParseColor(a, out var first) && TryParseColor(b, out var second) && first == second;
    }

    // a color is what parses the same with two different fallbacks
    private static bool TryParseColor(string value, out RenderColor color)
    {
        color = HtmlRenderer.ParseColor(value, RenderColor.Transparent);
        return color == HtmlRenderer.ParseColor(value, new RenderColor(1, 2, 3));
    }

    private static bool TryParsePercentage(string value, out float percentage)
    {
        var text = value.Trim();
        percentage = 0f;
        return text.EndsWith('%') && float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out percentage);
    }

    public static string InterpolateColor(RenderColor from, RenderColor to, double t)
    {
        var r = Lerp(from.R, to.R, t);
        var g = Lerp(from.G, to.G, t);
        var b = Lerp(from.B, to.B, t);
        var a = Lerp(from.A, to.A, t) / 255d;
        return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {a.ToString("0.###", CultureInfo.InvariantCulture)})");
    }

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)Math.Round(from + ((to - from) * t), MidpointRounding.AwayFromZero);

    public static double ParseTimeValue(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return ms;
        }

        if (trimmed.EndsWith('s') &&
            double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return s * 1000d;
        }

        return 0d;
    }

    public static CssTimingFunction ParseTimingFunction(string value)
    {
        var source = new StringSource(value.Trim());
        var parsed = TimingFunctionParser.ParseTimingFunction(source);

        return parsed switch
        {
            CssCubicBezierValue cubic => CssTimingFunction.CubicBezier(cubic.X1.AsDouble(), cubic.Y1.AsDouble(), cubic.X2.AsDouble(), cubic.Y2.AsDouble()),
            CssStepsValue steps => CssTimingFunction.Steps(steps.Intervals.AsInt32(), steps.IsStart),
            _ => CssTimingFunction.Linear,
        };
    }
}
