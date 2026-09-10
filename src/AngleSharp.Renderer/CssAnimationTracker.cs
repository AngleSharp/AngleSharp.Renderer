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
    CssAnimationFillMode FillMode);

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

    /// <summary>
    /// Gets the animated value currently in effect for <paramref name="property"/> on
    /// <paramref name="element"/>, or null if no running `animation` on this element currently
    /// overrides it (no `animation-name`, the named `@keyframes` rule does not exist, the property
    /// is not declared in the bracketing keyframes, the animation has not started yet and
    /// `animation-fill-mode` does not include `backwards`, or it already ended and
    /// `animation-fill-mode` does not include `forwards`) - a signal to the caller to use whatever
    /// value it would otherwise have used.
    /// </summary>
    public string? GetAnimatedValue(IElement element, string property, double clockMs)
    {
        var style = element.ComputeCurrentStyle();
        var specs = ParseAnimationSpecs(style);

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

    private string? GetAnimatedValueForSpec(IElement element, CssAnimationSpec spec, string property, double clockMs)
    {
        var keyframes = FindKeyframeStops(element, spec.Name);

        if (keyframes is not { Count: > 0 })
        {
            return null;
        }

        // Pinned to virtual-clock zero the first time this (element, animation-name) pair is
        // observed, not to whatever the clock happens to read at that moment - an animation begins
        // playing at document-ready (clock zero) in a real browser regardless of when a viewer
        // first looks at it, and the same has to hold here regardless of whether a caller happens
        // to call AdvanceTime one or several times before ever painting for the first time.
        if (!_startTimesMs.ContainsKey((element, spec.Name)))
        {
            _startTimesMs[(element, spec.Name)] = 0d;
        }

        var startMs = _startTimesMs[(element, spec.Name)];
        var elapsedMs = clockMs - startMs - spec.DelayMs;
        var totalDurationMs = spec.DurationMs * spec.IterationCount;

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

            result.Add(new CssAnimationSpec(name, duration, delay, timingFunction, iterationCount, direction, fillMode));
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
