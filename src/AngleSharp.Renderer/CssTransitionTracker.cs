using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System.Linq;

namespace AngleSharp.Renderer;

internal readonly record struct CssTransitionSpec(double DurationMs, double DelayMs, CssTimingFunction TimingFunction);

internal sealed record CssTransitionRun(string From, string To, double StartMs, CssTransitionSpec Spec);

/// <summary>
/// Tracks in-flight CSS `transition`s for an interactive document and computes each one's current
/// interpolated value against a caller-pumped virtual clock (<see cref="AdvanceTime"/>) - the
/// mechanism that lets <see cref="InteractiveHtmlRendererState"/> paint a `:hover` transition
/// mid-flight instead of only ever the resting or fully-hovered extremes. There is no real-time
/// timer anywhere in this renderer; a caller drives the clock explicitly, exactly the way it
/// already drives mouse position and scroll offsets for the same interactive harness. Value
/// interpolation itself (which properties, how to lerp/re-serialize each) is shared with
/// `animation` via <see cref="CssValueInterpolation"/>.
/// </summary>
internal sealed class CssTransitionTracker
{
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
        return CssValueInterpolation.Interpolate(property, run.From, run.To, eased);
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

            foreach (var (property, kind) in CssValueInterpolation.InterpolatableProperties)
            {
                var key = (element, property);

                if (_activeRuns.ContainsKey(key))
                {
                    continue;
                }

                var naturalValue = CssValueInterpolation.ReadNaturalValue(style, property, kind);

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

            foreach (var (property, kind) in CssValueInterpolation.InterpolatableProperties)
            {
                if (!specsByProperty.TryGetValue(property, out var spec))
                {
                    continue;
                }

                var naturalValue = CssValueInterpolation.ReadNaturalValue(style, property, kind);

                if (naturalValue is null)
                {
                    continue;
                }

                var key = (element, property);
                var currentValue = GetTransitioningValue(element, property)
                    ?? (_lastKnownValues.TryGetValue(key, out var last) ? last : naturalValue);

                if (CssValueInterpolation.ValuesAreEquivalent(kind, currentValue, naturalValue))
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

    /// <summary>
    /// Reads `transition-property`/`-duration`/`-delay`/`-timing-function` from an element's own
    /// computed style and builds one <see cref="CssTransitionSpec"/> per named property -
    /// `transition-property: all` (or an absent/unparseable list) expands to every property
    /// <see cref="CssValueInterpolation"/> knows how to interpolate rather than literally every CSS
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
            ? [.. CssValueInterpolation.InterpolatableProperties.Keys]
            : propertyList;

        var result = new Dictionary<string, CssTransitionSpec>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < expandedProperties.Length; i++)
        {
            var property = expandedProperties[i].Trim();

            if (!CssValueInterpolation.InterpolatableProperties.ContainsKey(property))
            {
                continue;
            }

            var duration = durationList.Length > 0 ? CssValueInterpolation.ParseTimeValue(durationList[i % durationList.Length]) : 0d;
            var delay = delayList.Length > 0 ? CssValueInterpolation.ParseTimeValue(delayList[i % delayList.Length]) : 0d;
            var timingFunction = timingList.Length > 0 ? CssValueInterpolation.ParseTimingFunction(timingList[i % timingList.Length]) : CssTimingFunction.Linear;

            if (duration > 0d)
            {
                result[property] = new CssTransitionSpec(duration, delay, timingFunction);
            }
        }

        return result;
    }
}
