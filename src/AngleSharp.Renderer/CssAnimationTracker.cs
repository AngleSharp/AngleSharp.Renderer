using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System.Globalization;
using System.Linq;

namespace AngleSharp.Renderer;

/// <summary>
/// `animation-direction`, mirroring <see cref="AngleSharp.Css.Dom.AnimationDirection"/>'s keyword
/// set - not reused directly because that enum lives on the (internal-heavy) declaration types this
/// renderer otherwise avoids depending on; a small local copy keeps this tracker self-contained.
/// </summary>
internal enum CssAnimationDirection
{
    Normal,
    Reverse,
    Alternate,
    AlternateReverse,
}

internal enum CssAnimationFillMode
{
    None,
    Forwards,
    Backwards,
    Both,
}

internal readonly record struct CssAnimationSpec(
    string Name,
    double DurationMs,
    double DelayMs,
    CssTimingFunction TimingFunction,
    double IterationCount,
    CssAnimationDirection Direction,
    CssAnimationFillMode FillMode,
    bool IsPaused);

internal enum CssAnimationPhase
{
    Idle,
    Before,
    Active,
    After,
}

/// <summary>
/// An event of a CSS animation, to be dispatched once the frame that found it is done.
/// </summary>
internal readonly record struct CssAnimationEventRecord(IElement Target, string Type, string AnimationName, double ElapsedSeconds);

internal sealed record CssKeyframeStop(double Position, IReadOnlyDictionary<string, string> PropertyValues);

/// <summary>
/// Tracks CSS `animation`s the same way <see cref="CssTransitionTracker"/> tracks `transition`s -
/// against the same caller-pumped virtual clock (<see cref="InteractiveHtmlRendererState.AdvanceTime"/>)
/// and the same fixed, explicit property whitelist (<see cref="CssValueInterpolation"/>) - but with
/// a different trigger model: a `transition` starts only when some other state change (e.g. `:hover`)
/// makes its target property's *natural* value change, whereas an `animation` simply starts running
/// the first moment this tracker observes it on an element (there is no "before" state to compare
/// against - an animation's own `@keyframes` supply both endpoints directly), and keeps running -
/// looping per `animation-iteration-count` - for as long as the element keeps declaring that
/// `animation-name`, independent of `:hover` or anything else.
/// </summary>
internal sealed class CssAnimationTracker
{
    private readonly Dictionary<(IElement Element, string Name), double> _startTimesMs = [];

    // Total virtual-clock time this (element, animation-name) pair has spent paused since it
    // started, so it can be subtracted back out of the raw clock to get an "effective" clock that
    // stays frozen for as long as the animation is paused and resumes in sync with the real clock
    // once it isn't - the same amount of clock time always elapsing while paused as gets added
    // here is what keeps the effective clock frozen across any number of paused observations.
    private readonly Dictionary<(IElement Element, string Name), double> _pausedAccumulatedMs = [];
    private readonly Dictionary<(IElement Element, string Name), double> _lastObservedClockMs = [];

    // While a frame is painted, the animations of an element and the keyframes of a name stay the
    // same, but they are asked for once per interpolatable property: each time, the element's
    // cascade ran again and every rule of every style sheet was searched for the keyframes.
    private Dictionary<IElement, List<CssAnimationSpec>>? _frameSpecs;
    private readonly Dictionary<(IElement Element, string Name), (CssAnimationPhase Phase, double Iteration)> _phases = [];
    private readonly Dictionary<IElement, HashSet<string>> _trackedNames = [];
    private readonly HashSet<IDocument> _paintedDocuments = [];
    private readonly HashSet<(IElement Element, string Name)> _pausedNames = [];
    private List<CssAnimationEventRecord> _pendingEvents = [];
    private bool _isFirstFrame = true;
    private Dictionary<(IDocument Document, string Name), IReadOnlyList<CssKeyframeStop>?>? _frameKeyframes;

    /// <summary>
    /// Caches the animations of elements and the keyframes they refer to until the returned
    /// scope is disposed, i.e. for the duration of painting one frame.
    /// </summary>
    public IDisposable BeginFrame(IDocument? document = null)
    {
        IsAnimating = false;
        _frameSpecs = [];
        _frameKeyframes = [];
        _isFirstFrame = document is null || !_paintedDocuments.Contains(document);
        ForgetDisconnectedElements();
        return new FrameScope(this, document);
    }

    /// <summary>
    /// Gets if an animation observed in the last frame (see <see cref="BeginFrame"/>) is still
    /// playing, i.e., is not paused and has not ended (it may still be in its delay).
    /// </summary>
    public bool IsAnimating { get; private set; }

    /// <summary>
    /// Gets if an animation seen so far has not ended yet and is not paused, i.e. if time
    /// passing changes it or gives it events.
    /// </summary>
    public bool HasRunningAnimations
    {
        get
        {
            foreach (var (key, state) in _phases)
            {
                if (state.Phase is CssAnimationPhase.Before or CssAnimationPhase.Active && !IsPaused(key))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private bool IsPaused((IElement Element, string Name) key) =>
        _pausedNames.Contains(key);

    private sealed class FrameScope(CssAnimationTracker tracker, IDocument? document) : IDisposable
    {
        public void Dispose()
        {
            tracker._frameSpecs = null;
            tracker._frameKeyframes = null;
            tracker._isFirstFrame = false;

            if (document is not null)
            {
                tracker._paintedDocuments.Add(document);
            }
        }
    }

    /// <summary>
    /// Takes the events of animations found while painting (see <see cref="Observe"/>), in the
    /// order they happened.
    /// </summary>
    public List<CssAnimationEventRecord> TakeEvents()
    {
        var events = _pendingEvents;
        _pendingEvents = [];
        return events;
    }

    /// <summary>
    /// Follows the animations of an element while a frame is painted, recording the events of
    /// the ones starting, repeating, ending or being cancelled since the last time it was seen.
    /// </summary>
    /// <param name="element">The element whose style was computed.</param>
    /// <param name="computedStyle">The element's computed style.</param>
    /// <param name="isAnimated">If the style names any animation.</param>
    /// <param name="clockMs">The virtual clock.</param>
    public void Observe(IElement element, ICssStyleDeclaration computedStyle, bool isAnimated, double clockMs)
    {
        _trackedNames.TryGetValue(element, out var tracked);

        if (!isAnimated && tracked is null)
        {
            return;
        }

        var specs = isAnimated ? GetAnimationSpecs(element, computedStyle) : [];
        HashSet<string>? current = null;

        foreach (var spec in specs)
        {
            // an animation of keyframes that don't exist does not run, so it has no events
            if (GetKeyframeStops(element, spec.Name) is not { Count: > 0 })
            {
                continue;
            }

            (current ??= new(StringComparer.Ordinal)).Add(spec.Name);
            ObserveSpec(element, spec, clockMs);
        }

        if (tracked is not null)
        {
            foreach (var name in tracked.ToArray())
            {
                if (current is null || !current.Contains(name))
                {
                    Forget(element, name, cancelled: true);
                }
            }
        }

        if (current is not null)
        {
            if (!_trackedNames.TryGetValue(element, out tracked))
            {
                tracked = new(StringComparer.Ordinal);
                _trackedNames[element] = tracked;
            }

            tracked.UnionWith(current);
        }
        else
        {
            _trackedNames.Remove(element);
        }
    }

    private void ObserveSpec(IElement element, CssAnimationSpec spec, double clockMs)
    {
        var key = (element, spec.Name);
        var startMs = GetStartTimeMs(key, clockMs);
        var effectiveClockMs = ResolveEffectiveClockMs(element, spec, clockMs);
        var elapsedMs = effectiveClockMs - startMs - spec.DelayMs;
        var activeDurationMs = spec.DurationMs * spec.IterationCount;
        var isInfinite = double.IsPositiveInfinity(activeDurationMs);
        var phase = elapsedMs < 0d ? CssAnimationPhase.Before
            : isInfinite || elapsedMs < activeDurationMs ? CssAnimationPhase.Active
            : CssAnimationPhase.After;
        var iteration = phase switch
        {
            CssAnimationPhase.Active => spec.DurationMs > 0d ? Math.Floor(elapsedMs / spec.DurationMs) : 0d,
            CssAnimationPhase.After => Math.Max(0d, Math.Ceiling(spec.IterationCount) - 1d),
            _ => 0d,
        };

        if (!spec.IsPaused && phase != CssAnimationPhase.After)
        {
            IsAnimating = true;
        }

        if (spec.IsPaused)
        {
            _pausedNames.Add(key);
        }
        else
        {
            _pausedNames.Remove(key);
        }

        _phases.TryGetValue(key, out var previous);
        _phases[key] = (phase, iteration);

        // the times the events tell, as CSS Animations 1 says (seconds, without the delay)
        var startElapsed = Math.Max(Math.Min(-spec.DelayMs, activeDurationMs), 0d) / 1000d;
        var endElapsed = isInfinite ? 0d : activeDurationMs / 1000d;

        switch (previous.Phase, phase)
        {
            case (CssAnimationPhase.Idle or CssAnimationPhase.Before, CssAnimationPhase.Active):
                Record(element, "animationstart", spec.Name, startElapsed);
                break;
            case (CssAnimationPhase.Idle or CssAnimationPhase.Before, CssAnimationPhase.After):
                Record(element, "animationstart", spec.Name, startElapsed);
                Record(element, "animationend", spec.Name, endElapsed);
                break;
            case (CssAnimationPhase.Active, CssAnimationPhase.Active) when iteration != previous.Iteration:
                Record(element, "animationiteration", spec.Name, iteration * spec.DurationMs / 1000d);
                break;
            case (CssAnimationPhase.Active, CssAnimationPhase.After):
                Record(element, "animationend", spec.Name, endElapsed);
                break;
        }
    }

    private void Record(IElement element, string type, string name, double elapsedSeconds) =>
        _pendingEvents.Add(new CssAnimationEventRecord(element, type, name, elapsedSeconds));

    // An animation stops once its element no longer names it: naming it again starts it anew.
    private void Forget(IElement element, string name, bool cancelled)
    {
        var key = (element, name);

        if (cancelled && _phases.TryGetValue(key, out var state) && state.Phase is CssAnimationPhase.Before or CssAnimationPhase.Active)
        {
            Record(element, "animationcancel", name, 0d);
        }

        _phases.Remove(key);
        _pausedNames.Remove(key);
        _startTimesMs.Remove(key);
        _pausedAccumulatedMs.Remove(key);
        _lastObservedClockMs.Remove(key);
    }

    private void ForgetDisconnectedElements()
    {
        if (_trackedNames.Count == 0)
        {
            return;
        }

        foreach (var (element, names) in _trackedNames.ToArray())
        {
            if (!element.IsConnected())
            {
                foreach (var name in names)
                {
                    Forget(element, name, cancelled: false);
                }

                _trackedNames.Remove(element);
            }
        }
    }

    // An animation found in the first frame of a document runs since the document got ready, i.e.
    // since the clock started, however long ago that is; one found later starts when it is found.
    private double GetStartTimeMs((IElement Element, string Name) key, double clockMs)
    {
        if (!_startTimesMs.TryGetValue(key, out var startMs))
        {
            startMs = _isFirstFrame ? 0d : clockMs;
            _startTimesMs[key] = startMs;
        }

        return startMs;
    }

    /// <summary>
    /// Gets the animated value currently in effect for <paramref name="property"/> on
    /// <paramref name="element"/>, or null if no running `animation` on this element currently
    /// overrides it (no `animation-name`, the named `@keyframes` rule does not exist, the property
    /// is not declared in the bracketing keyframes, the animation has not started yet and
    /// `animation-fill-mode` does not include `backwards`, or it already ended and
    /// `animation-fill-mode` does not include `forwards`) - a signal to the caller to use whatever
    /// value it would otherwise have used.
    /// </summary>
    public string? GetAnimatedValue(IElement element, string property, double clockMs, ICssStyleDeclaration? computedStyle = null)
    {
        var specs = GetAnimationSpecs(element, computedStyle);

        if (specs.Count == 0)
        {
            return null;
        }

        // Multiple simultaneous animations can target the same property - the one declared last in
        // `animation-name` wins, per spec, so this simply lets a later match overwrite an earlier
        // one rather than stopping at the first.
        string? result = null;

        foreach (var spec in specs)
        {
            var value = GetAnimatedValueForSpec(element, spec, property, clockMs);

            if (value is not null)
            {
                result = value;
            }
        }

        return result;
    }

    // the computed style, when the caller has it, saves running the element's cascade again
    private List<CssAnimationSpec> GetAnimationSpecs(IElement element, ICssStyleDeclaration? computedStyle)
    {
        if (_frameSpecs is null)
        {
            return ParseAnimationSpecs(computedStyle ?? element.ComputeCurrentStyle());
        }

        if (!_frameSpecs.TryGetValue(element, out var specs))
        {
            specs = ParseAnimationSpecs(computedStyle ?? element.ComputeCurrentStyle());
            _frameSpecs[element] = specs;
        }

        return specs;
    }

    private IReadOnlyList<CssKeyframeStop>? GetKeyframeStops(IElement element, string name)
    {
        if (_frameKeyframes is null || element.Owner is not { } document)
        {
            return FindKeyframeStops(element, name);
        }

        if (!_frameKeyframes.TryGetValue((document, name), out var stops))
        {
            stops = FindKeyframeStops(element, name);
            _frameKeyframes[(document, name)] = stops;
        }

        return stops;
    }

    private string? GetAnimatedValueForSpec(IElement element, CssAnimationSpec spec, string property, double clockMs)
    {
        var keyframes = GetKeyframeStops(element, spec.Name);

        if (keyframes is not { Count: > 0 })
        {
            return null;
        }

        // Pinned to virtual-clock zero when this (element, animation-name) pair is observed in the
        // first frame of its document - an animation begins playing at document-ready in a real
        // browser regardless of when a viewer first looks at it, whether or not a caller advanced
        // the time before painting for the first time - and otherwise to the clock when the
        // element starts naming the animation (e.g. a class added by script).
        var startMs = GetStartTimeMs((element, spec.Name), clockMs);
        var effectiveClockMs = ResolveEffectiveClockMs(element, spec, clockMs);
        var elapsedMs = effectiveClockMs - startMs - spec.DelayMs;
        var totalDurationMs = spec.DurationMs * spec.IterationCount;

        if (!spec.IsPaused && (elapsedMs < totalDurationMs || double.IsPositiveInfinity(totalDurationMs)))
        {
            IsAnimating = true;
        }

        if (elapsedMs < 0d)
        {
            if (spec.FillMode is not (CssAnimationFillMode.Backwards or CssAnimationFillMode.Both))
            {
                return null;
            }

            elapsedMs = 0d;
        }
        else if (elapsedMs >= totalDurationMs && !double.IsPositiveInfinity(totalDurationMs))
        {
            if (spec.FillMode is not (CssAnimationFillMode.Forwards or CssAnimationFillMode.Both))
            {
                return null;
            }

            // Clamp to a hair before the end so the iteration/direction math below resolves to the
            // very last instant of the final iteration, rather than rolling over into a
            // (non-existent) next one.
            elapsedMs = Math.Max(0d, totalDurationMs - 0.001d);
        }

        var iterationIndex = spec.DurationMs > 0d ? Math.Floor(elapsedMs / spec.DurationMs) : 0d;
        var withinIterationMs = spec.DurationMs > 0d ? elapsedMs - (iterationIndex * spec.DurationMs) : 0d;
        var rawProgress = spec.DurationMs > 0d ? withinIterationMs / spec.DurationMs : 1d;
        var iterationRunsBackward = spec.Direction switch
        {
            CssAnimationDirection.Reverse => true,
            CssAnimationDirection.Alternate => iterationIndex % 2d == 1d,
            CssAnimationDirection.AlternateReverse => iterationIndex % 2d == 0d,
            _ => false,
        };
        var progress = iterationRunsBackward ? 1d - rawProgress : rawProgress;
        var eased = spec.TimingFunction.Evaluate(progress);

        return InterpolateAtKeyframes(keyframes, property, eased);
    }

    /// <summary>
    /// `animation-play-state: paused` freezes an animation exactly where it currently is - not by
    /// stopping the shared virtual clock (other animations/transitions on the same or other
    /// elements must keep advancing normally), but by tracking how much clock time this specific
    /// (element, animation-name) pair has spent paused and subtracting that back out of the raw
    /// clock before the ordinary elapsed-time math runs. Whether the gap since the last observation
    /// counts as paused time is decided by *this* call's play-state (not the previous one) - the
    /// only state this tracker ever samples is at call time, so a pause taking effect anywhere
    /// between two observations is attributed to the whole gap once it is next observed as paused;
    /// once next observed as running again, no further time is added and the effective clock
    /// continues advancing in lockstep with the real one from wherever it was left off.
    /// </summary>
    private double ResolveEffectiveClockMs(IElement element, CssAnimationSpec spec, double clockMs)
    {
        var key = (element, spec.Name);

        if (_lastObservedClockMs.TryGetValue(key, out var lastClockMs))
        {
            if (spec.IsPaused)
            {
                _pausedAccumulatedMs[key] = _pausedAccumulatedMs.GetValueOrDefault(key) + (clockMs - lastClockMs);
            }
        }
        else
        {
            _pausedAccumulatedMs[key] = 0d;
        }

        _lastObservedClockMs[key] = clockMs;

        return clockMs - _pausedAccumulatedMs.GetValueOrDefault(key);
    }

    private static string? InterpolateAtKeyframes(IReadOnlyList<CssKeyframeStop> stops, string property, double position)
    {
        if (position <= stops[0].Position)
        {
            return stops[0].PropertyValues.TryGetValue(property, out var value) ? value : null;
        }

        if (position >= stops[^1].Position)
        {
            return stops[^1].PropertyValues.TryGetValue(property, out var value) ? value : null;
        }

        var upperIndex = 0;

        while (upperIndex < stops.Count && stops[upperIndex].Position < position)
        {
            upperIndex++;
        }

        var lower = stops[upperIndex - 1];
        var upper = stops[upperIndex];
        var hasLower = lower.PropertyValues.TryGetValue(property, out var lowerValue);
        var hasUpper = upper.PropertyValues.TryGetValue(property, out var upperValue);

        if (hasLower && hasUpper)
        {
            var span = upper.Position - lower.Position;
            var subT = span > 0d ? (position - lower.Position) / span : 1d;
            return CssValueInterpolation.Interpolate(property, lowerValue!, upperValue!, subT);
        }

        // Only one of the two bracketing keyframes declares this property - a defensible
        // simplification (rather than searching further afield for the nearest declaring
        // keyframe on either side, which the spec technically requires): use whichever edge
        // actually declared a value instead of no override at all.
        return hasLower ? lowerValue : hasUpper ? upperValue : null;
    }

    private static IReadOnlyList<CssKeyframeStop>? FindKeyframeStops(IElement element, string name)
    {
        var keyframesRule = element.Owner?.StyleSheets
            .OfType<ICssStyleSheet>()
            .SelectMany(sheet => sheet.Rules)
            .OfType<ICssKeyframesRule>()
            .FirstOrDefault(rule => string.Equals(rule.Name, name, StringComparison.Ordinal));

        if (keyframesRule is null)
        {
            return null;
        }

        var stops = new List<CssKeyframeStop>();

        foreach (var keyframe in keyframesRule.Rules.OfType<ICssKeyframeRule>())
        {
            var propertyValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in CssValueInterpolation.InterpolatableProperties.Keys)
            {
                var kind = CssValueInterpolation.KindOf(property);
                var value = CssValueInterpolation.ReadNaturalValue(keyframe.Style, property, kind);

                if (value is not null)
                {
                    propertyValues[property] = value;
                }
            }

            foreach (var position in ParseKeyframeSelector(keyframe.KeyText))
            {
                stops.Add(new CssKeyframeStop(position, propertyValues));
            }
        }

        return stops.Count > 0 ? [.. stops.OrderBy(s => s.Position)] : null;
    }

    private static IEnumerable<double> ParseKeyframeSelector(string keyText)
    {
        foreach (var token in keyText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                yield return 0d;
            }
            else if (token.Equals("to", StringComparison.OrdinalIgnoreCase))
            {
                yield return 1d;
            }
            else if (token.EndsWith('%') &&
                double.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
            {
                yield return Math.Clamp(percentage / 100d, 0d, 1d);
            }
        }
    }

    /// <summary>
    /// Reads `animation-name`/`-duration`/`-delay`/`-timing-function`/`-iteration-count`/
    /// `-direction`/`-fill-mode` from an element's own computed style and builds one
    /// <see cref="CssAnimationSpec"/> per named animation, cycling the shorter lists against a
    /// longer `animation-name` list per spec. AngleSharp.Css used to report the literal string
    /// `"initial"` (not each property's own actual initial value) for any of these longhands the
    /// `animation` shorthand did not explicitly set - fixed upstream, confirmed empirically each
    /// now resolves to its real initial value (`normal`/`none`/`0s`/`running`) directly, so
    /// <see cref="ResolveListValue"/> only has to handle the "list shorter than `animation-name`"
    /// cycling case per spec, not a literal-`"initial"` substitution too.
    /// </summary>
    private static List<CssAnimationSpec> ParseAnimationSpecs(ICssStyleDeclaration style)
    {
        var nameList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-name"))
            .Where(n => !n.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (nameList.Length == 0)
        {
            return [];
        }

        var durationList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-duration"));
        var delayList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-delay"));
        var timingList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-timing-function"));
        var iterationList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-iteration-count"));
        var directionList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-direction"));
        var fillModeList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-fill-mode"));
        var playStateList = HtmlRenderer.SplitTopLevelCommaList(style.GetPropertyValue("animation-play-state"));

        var result = new List<CssAnimationSpec>(nameList.Length);

        for (var i = 0; i < nameList.Length; i++)
        {
            var name = nameList[i].Trim();
            var duration = ResolveListValue(durationList, i, "0s") is { } durationText ? CssValueInterpolation.ParseTimeValue(durationText) : 0d;
            var delay = ResolveListValue(delayList, i, "0s") is { } delayText ? CssValueInterpolation.ParseTimeValue(delayText) : 0d;
            var timingFunction = ResolveListValue(timingList, i, "linear") is { } timingText ? CssValueInterpolation.ParseTimingFunction(timingText) : CssTimingFunction.Linear;
            var iterationCount = ParseIterationCount(ResolveListValue(iterationList, i, "1"));
            var direction = ParseDirection(ResolveListValue(directionList, i, "normal"));
            var fillMode = ParseFillMode(ResolveListValue(fillModeList, i, "none"));
            var isPaused = string.Equals(ResolveListValue(playStateList, i, "running")?.Trim(), "paused", StringComparison.OrdinalIgnoreCase);

            result.Add(new CssAnimationSpec(name, duration, delay, timingFunction, iterationCount, direction, fillMode, isPaused));
        }

        return result;
    }

    // Cycles a shorter list against a longer one per spec; an empty list (the property was never
    // set at all) falls back to the caller-supplied initial value.
    private static string? ResolveListValue(string[] list, int index, string initialValue) =>
        list.Length == 0 ? initialValue : list[index % list.Length].Trim();

    private static double ParseIterationCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 1d;
        }

        var trimmed = value.Trim();

        if (trimmed.Equals("infinite", StringComparison.OrdinalIgnoreCase))
        {
            return double.PositiveInfinity;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var count) && count >= 0d
            ? count
            : 1d;
    }

    private static CssAnimationDirection ParseDirection(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "reverse" => CssAnimationDirection.Reverse,
        "alternate" => CssAnimationDirection.Alternate,
        "alternate-reverse" => CssAnimationDirection.AlternateReverse,
        _ => CssAnimationDirection.Normal,
    };

    private static CssAnimationFillMode ParseFillMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "forwards" => CssAnimationFillMode.Forwards,
        "backwards" => CssAnimationFillMode.Backwards,
        "both" => CssAnimationFillMode.Both,
        _ => CssAnimationFillMode.None,
    };
}
