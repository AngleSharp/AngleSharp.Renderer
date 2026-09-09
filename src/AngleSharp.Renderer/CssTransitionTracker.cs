using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Text;
using System.Globalization;
using System.Linq;

namespace AngleSharp.Renderer;

/// <summary>
/// Which kind of value a transitionable CSS property carries, so <see cref="CssTransitionTracker"/>
/// knows how to interpolate and re-serialize it.
/// </summary>
internal enum CssTransitionValueKind
{
    Color,
    LengthPixels,
    Number,
}

/// <summary>
/// A parsed CSS easing function - either `cubic-bezier(x1, y1, x2, y2)` (which every named keyword
/// - `ease`/`ease-in`/`ease-in-out`/`ease-out`/`linear` - normalizes to via AngleSharp.Css's own
/// computed style, confirmed empirically) or `steps(count, start|end)`.
/// </summary>
internal readonly record struct CssTimingFunction(bool IsSteps, double X1, double Y1, double X2, double Y2, int StepCount, bool JumpAtStart)
{
    public static readonly CssTimingFunction Linear = CubicBezier(0d, 0d, 1d, 1d);

    public static CssTimingFunction CubicBezier(double x1, double y1, double x2, double y2) =>
        new(false, x1, y1, x2, y2, 0, false);

    public static CssTimingFunction Steps(int count, bool jumpAtStart) =>
        new(true, 0, 0, 0, 0, System.Math.Max(1, count), jumpAtStart);

    /// <summary>
    /// Evaluates this easing function at <paramref name="t"/> (0..1, the linear fraction of the
    /// transition's duration elapsed) and returns the eased progress (also 0..1).
    /// </summary>
    public double Evaluate(double t)
    {
        t = System.Math.Clamp(t, 0d, 1d);

        if (IsSteps)
        {
            var step = JumpAtStart ? System.Math.Floor(t * StepCount) + 1d : System.Math.Floor(t * StepCount);
            return System.Math.Clamp(step / StepCount, 0d, 1d);
        }

        return SolveCubicBezier(X1, Y1, X2, Y2, t);
    }

    // The standard "UnitBezier" technique browsers use: the curve's own parameter (call it u) is
    // not the same as elapsed time t - t is the *x* coordinate of the curve, and the eased progress
    // is the *y* coordinate of the point on the curve whose x equals t. Bisection search finds the
    // u whose x(u) matches t closely enough, then evaluates y(u) - simpler to get right than
    // Newton-Raphson and fast enough here, since this runs once per transitioning property per
    // paint, not once per animation frame in a tight interactive render loop.
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

            if (System.Math.Abs(currentX - x) < 0.0001d)
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

internal readonly record struct CssTransitionSpec(double DurationMs, double DelayMs, CssTimingFunction TimingFunction);

internal sealed record CssTransitionRun(string From, string To, double StartMs, CssTransitionSpec Spec);

/// <summary>
/// Tracks in-flight CSS `transition`s for an interactive document and computes each one's current
/// interpolated value against a caller-pumped virtual clock (<see cref="AdvanceTime"/>) - the
/// mechanism that lets <see cref="InteractiveHtmlRendererState"/> paint a `:hover` transition
/// mid-flight instead of only ever the resting or fully-hovered extremes. There is no real-time
/// timer anywhere in this renderer; a caller drives the clock explicitly, exactly the way it
/// already drives mouse position and scroll offsets for the same interactive harness.
///
/// Only a fixed, explicit whitelist of properties (<see cref="TransitionableProperties"/>) is
/// interpolated - colors (channel-wise lerp) and numeric lengths/opacity (plain numeric lerp) cover
/// the overwhelming majority of real `:hover` transitions without requiring a general CSS-value
/// interpolation engine (which would need to understand every property's own grammar, including
/// ones this renderer does not otherwise resolve, like `transform`/`filter` component-wise
/// interpolation - a deliberate, documented scope cut, not an oversight). `transition-property: all`
/// expands to this same whitelist, not literally every CSS property, for the same reason.
/// </summary>
internal sealed class CssTransitionTracker
{
    private static readonly Dictionary<string, CssTransitionValueKind> TransitionableProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["background-color"] = CssTransitionValueKind.Color,
        ["color"] = CssTransitionValueKind.Color,
        ["border-top-color"] = CssTransitionValueKind.Color,
        ["border-right-color"] = CssTransitionValueKind.Color,
        ["border-bottom-color"] = CssTransitionValueKind.Color,
        ["border-left-color"] = CssTransitionValueKind.Color,
        ["opacity"] = CssTransitionValueKind.Number,
        ["width"] = CssTransitionValueKind.LengthPixels,
        ["height"] = CssTransitionValueKind.LengthPixels,
        ["font-size"] = CssTransitionValueKind.LengthPixels,
        ["margin-top"] = CssTransitionValueKind.LengthPixels,
        ["margin-right"] = CssTransitionValueKind.LengthPixels,
        ["margin-bottom"] = CssTransitionValueKind.LengthPixels,
        ["margin-left"] = CssTransitionValueKind.LengthPixels,
        ["padding-top"] = CssTransitionValueKind.LengthPixels,
        ["padding-right"] = CssTransitionValueKind.LengthPixels,
        ["padding-bottom"] = CssTransitionValueKind.LengthPixels,
        ["padding-left"] = CssTransitionValueKind.LengthPixels,
        ["border-top-width"] = CssTransitionValueKind.LengthPixels,
        ["border-right-width"] = CssTransitionValueKind.LengthPixels,
        ["border-bottom-width"] = CssTransitionValueKind.LengthPixels,
        ["border-left-width"] = CssTransitionValueKind.LengthPixels,
        ["top"] = CssTransitionValueKind.LengthPixels,
        ["left"] = CssTransitionValueKind.LengthPixels,
        ["right"] = CssTransitionValueKind.LengthPixels,
        ["bottom"] = CssTransitionValueKind.LengthPixels,
    };

    private readonly Dictionary<(IElement Element, string Property), CssTransitionRun> _activeRuns = [];
    private readonly Dictionary<(IElement Element, string Property), string> _lastKnownValues = [];
    private double _clockMs;

    /// <summary>
    /// Advances the virtual clock every active transition is measured against.
    /// </summary>
    public void AdvanceTime(TimeSpan delta) => _clockMs += delta.TotalMilliseconds;

    /// <summary>
    /// Gets the interpolated value currently in effect for <paramref name="property"/> on
    /// <paramref name="element"/> if a transition is actively running for it (mid-delay returns the
    /// starting value; a completed run is pruned and returns null so the natural value shows), or
    /// null if none is active - the common case, and a signal to the caller to use whatever value
    /// it would otherwise have used.
    /// </summary>
    public string? GetTransitioningValue(IElement element, string property)
    {
        var key = (element, property);

        if (!_activeRuns.TryGetValue(key, out var run))
        {
            return null;
        }

        var elapsed = _clockMs - run.StartMs - run.Spec.DelayMs;

        if (elapsed <= 0d)
        {
            return run.From;
        }

        if (elapsed >= run.Spec.DurationMs)
        {
            _activeRuns.Remove(key);
            return null;
        }

        var t = run.Spec.DurationMs > 0d ? elapsed / run.Spec.DurationMs : 1d;
        var eased = run.Spec.TimingFunction.Evaluate(t);
        return Interpolate(property, run.From, run.To, eased);
    }

    /// <summary>
    /// Records each of the given elements' *current* natural value for every transitionable
    /// property, before some state change (e.g. a forced pseudo-class) is about to happen - once
    /// that change is applied there is no way to ask AngleSharp.Css what the value used to be, so
    /// this is the only chance to capture a "from" baseline for a property that is not already
    /// mid-transition (one that is skips the snapshot: its live interpolated value, read via
    /// <see cref="GetTransitioningValue"/>, is already the correct "from" for a reversal). Must be
    /// called before the state change; <see cref="NotifyElementsMayHaveChanged"/> is called after.
    /// </summary>
    public void CaptureNaturalValuesBeforeChange(IEnumerable<IElement> elements)
    {
        foreach (var element in elements)
        {
            var style = element.ComputeCurrentStyle();

            foreach (var (property, kind) in TransitionableProperties)
            {
                var key = (element, property);

                if (_activeRuns.ContainsKey(key))
                {
                    continue;
                }

                var naturalValue = ReadNaturalValue(style, property, kind);

                if (naturalValue is not null)
                {
                    _lastKnownValues[key] = naturalValue;
                }
            }
        }
    }

    /// <summary>
    /// Re-evaluates every transitionable property this tracker knows about for each of the given
    /// elements against their current (post-state-change) natural computed style, starting a new
    /// transition run for any that changed - or, if a transition was already running and reversed
    /// mid-flight, restarting it from the value currently on screen rather than jumping back to the
    /// old resting value. Called whenever something that can affect computed style changes for a
    /// set of elements (today: the forced `:hover` pseudo-class chain in
    /// <see cref="InteractiveHtmlRendererState"/>).
    /// </summary>
    public void NotifyElementsMayHaveChanged(IEnumerable<IElement> elements)
    {
        foreach (var element in elements)
        {
            var style = element.ComputeCurrentStyle();
            var specsByProperty = ParseTransitionSpecs(style);

            foreach (var (property, kind) in TransitionableProperties)
            {
                if (!specsByProperty.TryGetValue(property, out var spec))
                {
                    continue;
                }

                var naturalValue = ReadNaturalValue(style, property, kind);

                if (naturalValue is null)
                {
                    continue;
                }

                var key = (element, property);
                var currentValue = GetTransitioningValue(element, property)
                    ?? (_lastKnownValues.TryGetValue(key, out var last) ? last : naturalValue);

                if (ValuesAreEquivalent(kind, currentValue, naturalValue))
                {
                    _activeRuns.Remove(key);
                }
                else
                {
                    _activeRuns[key] = new CssTransitionRun(currentValue, naturalValue, _clockMs, spec);
                }

                _lastKnownValues[key] = naturalValue;
            }
        }
    }

    private static string? ReadNaturalValue(ICssStyleDeclaration style, string property, CssTransitionValueKind kind)
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
            CssTransitionValueKind.Color => raw,
            CssTransitionValueKind.Number => float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? raw.Trim() : null,
            CssTransitionValueKind.LengthPixels => float.IsNaN(HtmlRenderer.ParseLengthValue(raw, float.NaN, allowAuto: false)) ? null : raw,
            _ => null,
        };
    }

    private static bool ValuesAreEquivalent(CssTransitionValueKind kind, string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return kind switch
        {
            CssTransitionValueKind.Color => HtmlRenderer.ParseColor(a, RenderColor.Transparent) == HtmlRenderer.ParseColor(b, RenderColor.Transparent),
            CssTransitionValueKind.Number or CssTransitionValueKind.LengthPixels =>
                System.Math.Abs(HtmlRenderer.ParseLengthValue(a, 0f, allowAuto: false) - HtmlRenderer.ParseLengthValue(b, 0f, allowAuto: false)) < 0.01f,
            _ => false,
        };
    }

    private static string Interpolate(string property, string from, string to, double t)
    {
        var kind = TransitionableProperties.TryGetValue(property, out var value) ? value : CssTransitionValueKind.Number;

        if (kind == CssTransitionValueKind.Color)
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

        return kind == CssTransitionValueKind.Number
            ? interpolated.ToString("0.####", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{interpolated.ToString("0.####", CultureInfo.InvariantCulture)}px");
    }

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)System.Math.Round(from + ((to - from) * t), MidpointRounding.AwayFromZero);

    /// <summary>
    /// Reads `transition-property`/`-duration`/`-delay`/`-timing-function` from an element's own
    /// computed style and builds one <see cref="CssTransitionSpec"/> per named property -
    /// `transition-property: all` (or an absent/unparseable list) expands to every property this
    /// tracker knows how to interpolate (see the class remarks) rather than literally every CSS
    /// property. Per spec, the duration/delay/timing-function lists are consumed cyclically against
    /// the (possibly longer) property list when they have fewer entries.
    /// </summary>
    private static Dictionary<string, CssTransitionSpec> ParseTransitionSpecs(ICssStyleDeclaration style)
    {
        var propertyList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-property"));
        var durationList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-duration"));
        var delayList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-delay"));
        var timingList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-timing-function"));

        var expandedProperties = propertyList.Length == 0 || propertyList.Any(p => p.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            ? [.. TransitionableProperties.Keys]
            : propertyList;

        var result = new Dictionary<string, CssTransitionSpec>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < expandedProperties.Length; i++)
        {
            var property = expandedProperties[i].Trim();

            if (!TransitionableProperties.ContainsKey(property))
            {
                continue;
            }

            var duration = durationList.Length > 0 ? ParseTimeValue(durationList[i % durationList.Length]) : 0d;
            var delay = delayList.Length > 0 ? ParseTimeValue(delayList[i % delayList.Length]) : 0d;
            var timingFunction = timingList.Length > 0 ? ParseTimingFunction(timingList[i % timingList.Length]) : CssTimingFunction.Linear;

            if (duration > 0d)
            {
                result[property] = new CssTransitionSpec(duration, delay, timingFunction);
            }
        }

        return result;
    }

    private static double ParseTimeValue(string value)
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

    private static CssTimingFunction ParseTimingFunction(string value)
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
