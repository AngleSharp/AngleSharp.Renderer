using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.Values;
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

            if (Math.Abs(currentX - x) < 0.0001d)
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
/// and `animation` (<see cref="CssAnimationTracker"/>) - both only ever animate a fixed, explicit
/// whitelist of properties (colors, channel-wise; numeric lengths/`opacity`, plain numeric lerp),
/// covering the overwhelming majority of real-world usage without a general CSS-value
/// interpolation engine that would need to understand every property's own grammar, including ones
/// this renderer does not otherwise resolve component-wise (`transform`/`filter`) - a deliberate,
/// documented scope cut, not an oversight. `transition-property: all` (and an `animation` keyframe
/// declaring any other property) is limited to this same whitelist for the same reason.
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
    };

    public static CssInterpolationValueKind KindOf(string property) =>
        InterpolatableProperties.TryGetValue(property, out var kind) ? kind : CssInterpolationValueKind.Number;

    public static string? ReadNaturalValue(ICssStyleDeclaration style, string property, CssInterpolationValueKind kind)
    {
        var raw = property.Equals("opacity", StringComparison.OrdinalIgnoreCase)
            ? style.GetOpacity()
            : style.GetPropertyValue(property);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return kind switch
        {
            CssInterpolationValueKind.Color => raw,
            CssInterpolationValueKind.Number => float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? raw.Trim() : null,
            CssInterpolationValueKind.LengthPixels => float.IsNaN(HtmlRenderer.ParseLengthValue(raw, float.NaN, allowAuto: false)) ? null : raw,
            _ => null,
        };
    }

    public static bool ValuesAreEquivalent(CssInterpolationValueKind kind, string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return kind switch
        {
            CssInterpolationValueKind.Color => HtmlRenderer.ParseColor(a, RenderColor.Transparent) == HtmlRenderer.ParseColor(b, RenderColor.Transparent),
            CssInterpolationValueKind.Number or CssInterpolationValueKind.LengthPixels =>
                Math.Abs(HtmlRenderer.ParseLengthValue(a, 0f, allowAuto: false) - HtmlRenderer.ParseLengthValue(b, 0f, allowAuto: false)) < 0.01f,
            _ => false,
        };
    }

    public static string Interpolate(string property, string from, string to, double t)
    {
        var kind = KindOf(property);

        if (kind == CssInterpolationValueKind.Color)
        {
            var fromColor = HtmlRenderer.ParseColor(from, RenderColor.Transparent);
            var toColor = HtmlRenderer.ParseColor(to, RenderColor.Transparent);
            var r = Lerp(fromColor.R, toColor.R, t);
            var g = Lerp(fromColor.G, toColor.G, t);
            var b = Lerp(fromColor.B, toColor.B, t);
            var a = Lerp(fromColor.A, toColor.A, t) / 255d;
            return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {a.ToString("0.###", CultureInfo.InvariantCulture)})");
        }

        var fromNumber = HtmlRenderer.ParseLengthValue(from, 0f, allowAuto: false);
        var toNumber = HtmlRenderer.ParseLengthValue(to, 0f, allowAuto: false);
        var interpolated = fromNumber + ((toNumber - fromNumber) * t);

        return kind == CssInterpolationValueKind.Number
            ? interpolated.ToString("0.####", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{interpolated.ToString("0.####", CultureInfo.InvariantCulture)}px");
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
