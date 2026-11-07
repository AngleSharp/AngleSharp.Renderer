using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System.Linq;

namespace AngleSharp.Renderer;

internal readonly record struct CssTransitionSpec(double DurationMs, double DelayMs, CssTimingFunction TimingFunction);

internal sealed record CssTransitionRun(string From, string To, double StartMs, CssTransitionSpec Spec)
{
    /// <summary>
    /// Gets or sets if the delay of the run is over (and transitionstart was told).
    /// </summary>
    public bool HasStarted { get; set; }

    /// <summary>
    /// Gets if the property is not interpolated here: the run only has the events, while the
    /// value is the one it changes to right away.
    /// </summary>
    public bool IsEventOnly { get; init; }
}

/// <summary>
/// An event of a CSS transition, to be dispatched once the frame that found it is done.
/// </summary>
internal readonly record struct CssTransitionEventRecord(IElement Target, string Type, string PropertyName, double ElapsedSeconds);

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
    private readonly HashSet<IElement> _observedElements = new(ReferenceEqualityComparer.Instance);
    private List<CssTransitionEventRecord> _pendingEvents = [];
    private double _clockMs;

    // Properties that are transitioned by `all` as well, without being interpolated here: their
    // transitions have the events (a page waiting for transitionend of a max-height), while the
    // value changes right away.
    private static readonly string[] EventOnlyProperties =
    [
        "max-height", "max-width", "min-height", "min-width", "box-shadow", "text-shadow", "visibility",
        "outline-color", "outline-width", "outline-offset", "fill", "stroke", "text-decoration-color",
        "letter-spacing", "word-spacing", "line-height", "clip-path", "gap", "row-gap", "column-gap",
        "background-position", "background-size", "border-radius", "inset",
    ];

    // Properties whose values are not animated by CSS (only discretely, with allow-discrete).
    private static readonly HashSet<string> DiscreteProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "display", "position", "float", "clear", "overflow", "overflow-x", "overflow-y", "content-visibility", "overlay",
    };

    // Shorthands whose longhands are interpolated here.
    private static readonly Dictionary<string, string[]> Shorthands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["margin"] = ["margin-top", "margin-right", "margin-bottom", "margin-left"],
        ["padding"] = ["padding-top", "padding-right", "padding-bottom", "padding-left"],
        ["border-color"] = ["border-top-color", "border-right-color", "border-bottom-color", "border-left-color"],
        ["border-width"] = ["border-top-width", "border-right-width", "border-bottom-width", "border-left-width"],
        ["border"] = ["border-top-color", "border-right-color", "border-bottom-color", "border-left-color", "border-top-width", "border-right-width", "border-bottom-width", "border-left-width"],
        ["inset"] = ["top", "right", "bottom", "left"],
        ["background"] = ["background-color"],
    };

    /// <summary>
    /// Advances the virtual clock every active transition is measured against.
    /// </summary>
    public void AdvanceTime(TimeSpan delta) => _clockMs += delta.TotalMilliseconds;

    /// <summary>
    /// Gets if a transition has not reached its end yet. Runs are only pruned when they are
    /// asked for, so this looks at the clock instead (an element removed mid-transition is never
    /// asked for again).
    /// </summary>
    internal bool HasRuns => _activeRuns.Count > 0;

    public bool HasRunningTransitions
    {
        get
        {
            foreach (var run in _activeRuns.Values)
            {
                if (_clockMs - run.StartMs - run.Spec.DelayMs < run.Spec.DurationMs)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Gets the interpolated value currently in effect for <paramref name="property"/> on
    /// <paramref name="element"/> if a transition is actively running for it (mid-delay returns the
    /// starting value; a completed run returns null so the natural value shows), or
    /// null if none is active - the common case, and a signal to the caller to use whatever value
    /// it would otherwise have used.
    /// </summary>
    public string? GetTransitioningValue(IElement element, string property)
    {
        var key = (element, property);

        if (!_activeRuns.TryGetValue(key, out var run) || run.IsEventOnly)
        {
            return null;
        }

        var elapsed = _clockMs - run.StartMs - run.Spec.DelayMs;

        // a run that is over is removed once its end is told (see TakeEvents)
        if (elapsed >= run.Spec.DurationMs)
        {
            return null;
        }

        if (elapsed <= 0d)
        {
            return run.From;
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
    /// called before the state change; <see cref="NotifyElementsMayHaveChanged(IEnumerable{IElement})"/> is called after.
    /// </summary>
    public void CaptureNaturalValuesBeforeChange(IEnumerable<IElement> elements) =>
        CaptureNaturalValuesBeforeChange(elements, static element => element.ComputeCurrentStyle());

    /// <summary>
    /// Like <see cref="CaptureNaturalValuesBeforeChange(IEnumerable{IElement})"/>, with the computed
    /// styles of the elements given, e.g. the ones of the last layout.
    /// </summary>
    public void CaptureNaturalValuesBeforeChange(IEnumerable<IElement> elements, Func<IElement, ICssStyleDeclaration> getStyle)
    {
        foreach (var element in elements)
        {
            var style = getStyle(element);

            foreach (var (property, kind) in CssValueInterpolation.InterpolatableProperties)
            {
                var key = (element, property);

                if (_activeRuns.ContainsKey(key))
                {
                    continue;
                }

                var naturalValue = CssValueInterpolation.ReadNaturalValue(style, property, kind, element);

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
    public void NotifyElementsMayHaveChanged(IEnumerable<IElement> elements) =>
        NotifyElementsMayHaveChanged(elements, static element => element.ComputeCurrentStyle());

    /// <summary>
    /// Like <see cref="NotifyElementsMayHaveChanged(IEnumerable{IElement})"/>, with the computed
    /// styles of the elements (after the change) given.
    /// </summary>
    public void NotifyElementsMayHaveChanged(IEnumerable<IElement> elements, Func<IElement, ICssStyleDeclaration> getStyle)
    {
        foreach (var element in elements)
        {
            var style = getStyle(element);
            var specsByProperty = ParseTransitionSpecs(style);

            foreach (var (property, kind) in CssValueInterpolation.InterpolatableProperties)
            {
                if (!specsByProperty.TryGetValue(property, out var spec))
                {
                    continue;
                }

                var naturalValue = CssValueInterpolation.ReadNaturalValue(style, property, kind, element);

                if (naturalValue is null)
                {
                    continue;
                }

                var key = (element, property);
                var currentValue = GetTransitioningValue(element, property)
                    ?? (_lastKnownValues.TryGetValue(key, out var last) ? last : naturalValue);

                if (CssValueInterpolation.ValuesAreEquivalent(kind, currentValue, naturalValue))
                {
                    CancelRun(key);
                }
                else
                {
                    StartRun(key, currentValue, naturalValue, spec, isEventOnly: false);
                }

                _lastKnownValues[key] = naturalValue;
            }
        }
    }

    /// <summary>
    /// Starts collecting the elements laid out by a frame, which have their values kept (see
    /// <see cref="EndFrame"/>).
    /// </summary>
    public void BeginFrame() => _observedElements.Clear();

    /// <summary>
    /// Forgets the values of the elements the frame did not lay out (removed, or not rendered),
    /// which start without a transition once they are again, like a new element.
    /// </summary>
    public void EndFrame()
    {
        if (_lastKnownValues.Count == 0)
        {
            return;
        }

        foreach (var key in _lastKnownValues.Keys.ToArray())
        {
            if (!_observedElements.Contains(key.Element) && !_activeRuns.ContainsKey(key))
            {
                _lastKnownValues.Remove(key);
            }
        }
    }

    /// <summary>
    /// Follows the transitioned properties of an element whose style was computed for painting,
    /// starting a transition for each one having another value than the last time it was seen, or
    /// in the given style before (the one of the last frame), e.g., after a script changed a
    /// class of the element. An element seen the first time has no transition.
    /// </summary>
    public void Observe(IElement element, ICssStyleDeclaration style, ICssStyleDeclaration? previousStyle)
    {
        _observedElements.Add(element);

        foreach (var (property, spec) in ParseTransitionSpecs(style, withEventOnlyProperties: true))
        {
            var isInterpolated = CssValueInterpolation.InterpolatableProperties.TryGetValue(property, out var kind);
            var naturalValue = ReadValue(style, property, isInterpolated, kind, element);

            if (naturalValue is null)
            {
                continue;
            }

            var key = (element, property);
            var hasRun = _activeRuns.TryGetValue(key, out var run) && !IsOver(run);

            // e.g. started by :hover already
            if (hasRun && AreEquivalent(isInterpolated, kind, run!.To, naturalValue))
            {
                _lastKnownValues[key] = naturalValue;
                continue;
            }

            // a transform is read from the cascade of the element as it is now (see ReadNaturalValue)
            var before = _lastKnownValues.TryGetValue(key, out var last) ? last
                : previousStyle is not null && !property.Equals("transform", StringComparison.OrdinalIgnoreCase) ? ReadValue(previousStyle, property, isInterpolated, kind, element)
                : null;
            _lastKnownValues[key] = naturalValue;

            if (before is null || AreEquivalent(isInterpolated, kind, before, naturalValue))
            {
                continue;
            }

            var from = (hasRun && isInterpolated ? GetTransitioningValue(element, property) : null) ?? before;
            StartRun(key, from, naturalValue, spec, isEventOnly: !isInterpolated);
        }
    }

    /// <summary>
    /// Takes the events of transitions since the last time (see <see cref="Observe"/>), in the
    /// order they happened, with the ones of runs whose delay or duration is over by now. The
    /// runs that are over are removed.
    /// </summary>
    public List<CssTransitionEventRecord> TakeEvents()
    {
        if (_activeRuns.Count > 0)
        {
            foreach (var (key, run) in _activeRuns.ToArray())
            {
                if (!key.Element.IsConnected())
                {
                    _activeRuns.Remove(key);
                    continue;
                }

                var elapsed = _clockMs - run.StartMs - run.Spec.DelayMs;

                if (!run.HasStarted && elapsed >= 0d)
                {
                    run.HasStarted = true;
                    _pendingEvents.Add(new(key.Element, "transitionstart", key.Property, Math.Max(0d, -run.Spec.DelayMs) / 1000d));
                }

                if (elapsed >= run.Spec.DurationMs)
                {
                    _activeRuns.Remove(key);
                    _pendingEvents.Add(new(key.Element, "transitionend", key.Property, run.Spec.DurationMs / 1000d));
                }
            }
        }

        var events = _pendingEvents;
        _pendingEvents = [];
        return events;
    }

    private bool IsOver(CssTransitionRun run) => _clockMs - run.StartMs - run.Spec.DelayMs >= run.Spec.DurationMs;

    private void StartRun((IElement Element, string Property) key, string from, string to, CssTransitionSpec spec, bool isEventOnly)
    {
        CancelRun(key);
        var run = new CssTransitionRun(from, to, _clockMs, spec) { IsEventOnly = isEventOnly, HasStarted = spec.DelayMs <= 0d };
        _activeRuns[key] = run;
        _pendingEvents.Add(new(key.Element, "transitionrun", key.Property, Math.Max(0d, -spec.DelayMs) / 1000d));

        if (run.HasStarted)
        {
            _pendingEvents.Add(new(key.Element, "transitionstart", key.Property, Math.Max(0d, -spec.DelayMs) / 1000d));
        }
    }

    // a run replaced or removed before it is over is cancelled
    private void CancelRun((IElement Element, string Property) key)
    {
        if (_activeRuns.Remove(key, out var run) && !IsOver(run))
        {
            var elapsed = Math.Max(0d, _clockMs - run.StartMs - run.Spec.DelayMs);
            _pendingEvents.Add(new(key.Element, "transitioncancel", key.Property, elapsed / 1000d));
        }
    }

    private static string? ReadValue(ICssStyleDeclaration style, string property, bool isInterpolated, CssInterpolationValueKind kind, IElement element)
    {
        if (isInterpolated)
        {
            return CssValueInterpolation.ReadNaturalValue(style, property, kind, element);
        }

        var value = style.GetPropertyValue(property);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool AreEquivalent(bool isInterpolated, CssInterpolationValueKind kind, string a, string b) =>
        isInterpolated ? CssValueInterpolation.ValuesAreEquivalent(kind, a, b) : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets if the style has a transition, i.e., a transition-duration of more than 0.
    /// </summary>
    internal static bool HasTransition(ICssStyleDeclaration style)
    {
        var durations = style.GetPropertyValue("transition-duration");

        if (string.IsNullOrWhiteSpace(durations) || durations is "0s" or "0ms")
        {
            return false;
        }

        foreach (var duration in HtmlRenderer.SplitTopLevelCommaList(durations))
        {
            if (CssValueInterpolation.ParseTimeValue(duration) > 0d)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads `transition-property`/`-duration`/`-delay`/`-timing-function` from an element's own
    /// computed style and builds one <see cref="CssTransitionSpec"/> per named property -
    /// `transition-property: all` (or an absent/unparseable list) expands to every property
    /// <see cref="CssValueInterpolation"/> knows how to interpolate rather than literally every CSS
    /// property (and, <paramref name="withEventOnlyProperties"/>, to the ones only having the
    /// events here, as does any other property named that is not discrete); a shorthand names its
    /// longhands. Per spec, the duration/delay/timing-function lists are consumed cyclically
    /// against the (possibly longer) property list when they have fewer entries, and a later
    /// entry for the same property wins.
    /// </summary>
    private static Dictionary<string, CssTransitionSpec> ParseTransitionSpecs(ICssStyleDeclaration style, bool withEventOnlyProperties = false)
    {
        var propertyList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-property"));
        var durationList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-duration"));
        var delayList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-delay"));
        var timingList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("transition-timing-function"));

        if (propertyList.Length == 0)
        {
            propertyList = ["all"];
        }

        var result = new Dictionary<string, CssTransitionSpec>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < propertyList.Length; i++)
        {
            var duration = durationList.Length > 0 ? CssValueInterpolation.ParseTimeValue(durationList[i % durationList.Length]) : 0d;

            if (duration <= 0d)
            {
                continue;
            }

            var delay = delayList.Length > 0 ? CssValueInterpolation.ParseTimeValue(delayList[i % delayList.Length]) : 0d;
            var timingFunction = timingList.Length > 0 ? CssValueInterpolation.ParseTimingFunction(timingList[i % timingList.Length]) : CssTimingFunction.Linear;
            var spec = new CssTransitionSpec(duration, delay, timingFunction);
            var name = propertyList[i].Trim();

            if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var property in CssValueInterpolation.InterpolatableProperties.Keys)
                {
                    result[property] = spec;
                }

                if (withEventOnlyProperties)
                {
                    foreach (var property in EventOnlyProperties)
                    {
                        result[property] = spec;
                    }
                }
            }
            else if (CssValueInterpolation.InterpolatableProperties.ContainsKey(name))
            {
                result[name] = spec;
            }
            else if (Shorthands.TryGetValue(name, out var longhands))
            {
                foreach (var property in longhands)
                {
                    result[property] = spec;
                }
            }
            else if (withEventOnlyProperties && name.Length > 0 && !name.Equals("none", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("--", StringComparison.Ordinal) && !DiscreteProperties.Contains(name))
            {
                result[name] = spec;
            }
        }

        return result;
    }
}
