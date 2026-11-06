namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Css;
using AngleSharp.Dom.Events;
using AngleSharp.Renderer;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

/// <summary>Options for a script-created animation.</summary>
[DomName("KeyframeAnimationOptions")]
public sealed class KeyframeAnimationOptions
{
    /// <summary>Gets or sets the active duration in milliseconds.</summary>
    [DomName("duration")]
    public double Duration { get; set; }

    /// <summary>Gets or sets the start delay in milliseconds.</summary>
    [DomName("delay")]
    public double Delay { get; set; }

    /// <summary>Gets or sets the end delay in milliseconds.</summary>
    [DomName("endDelay")]
    public double EndDelay { get; set; }

    /// <summary>Gets or sets the iteration count.</summary>
    [DomName("iterations")]
    public double Iterations { get; set; } = 1d;

    /// <summary>Gets or sets the playback direction.</summary>
    [DomName("direction")]
    public string Direction { get; set; } = "normal";

    /// <summary>Gets or sets the timing function.</summary>
    [DomName("easing")]
    public string Easing { get; set; } = "linear";

    /// <summary>Gets or sets the fill mode.</summary>
    [DomName("fill")]
    public string Fill { get; set; } = "none";

    /// <summary>Gets or sets the animation id.</summary>
    [DomName("id")]
    public string Id { get; set; } = string.Empty;
}

/// <summary>The document timeline used by script-created animations.</summary>
[DomName("DocumentTimeline")]
public sealed class AnimationTimeline
{
    private readonly IDocument _document;

    internal AnimationTimeline(IDocument document) => _document = document;

    /// <summary>Gets the current timeline time in milliseconds.</summary>
    [DomName("currentTime")]
    public double? CurrentTime => _document.Context.TryGetDomHarness(out var harness) && harness is not null
        ? harness.CurrentTime.TotalMilliseconds
        : null;
}

/// <summary>A keyframe effect attached to an element.</summary>
[DomName("KeyframeEffect")]
[DomExposed("Window")]
public sealed class KeyframeEffect
{
    private readonly AnimationKeyframe[] _keyframes;

    internal KeyframeEffect(IElement target, AnimationKeyframe[] keyframes, KeyframeAnimationOptions timing)
    {
        Target = target;
        _keyframes = keyframes;
        Timing = timing;
    }

    /// <summary>The effect target.</summary>
    [DomName("target")]
    public IElement Target { get; }

    /// <summary>Returns normalized keyframes.</summary>
    [DomName("getKeyframes")]
    public object[] GetKeyframes() => _keyframes.Select(frame => frame.ToScriptObject()).ToArray();

    /// <summary>Returns the effect timing parameters.</summary>
    [DomName("getTiming")]
    public KeyframeAnimationOptions GetTiming() => new()
    {
        Duration = Timing.Duration,
        Delay = Timing.Delay,
        EndDelay = Timing.EndDelay,
        Iterations = Timing.Iterations,
        Direction = Timing.Direction,
        Easing = Timing.Easing,
        Fill = Timing.Fill,
        Id = Timing.Id,
    };

    internal KeyframeAnimationOptions Timing { get; }

    internal IReadOnlyList<AnimationKeyframe> Frames => _keyframes;
}

/// <summary>A normalized keyframe.</summary>
[DomName("Keyframe")]
public sealed class AnimationKeyframe
{
    internal AnimationKeyframe(double? offset, string easing, IReadOnlyDictionary<string, string> properties)
    {
        Offset = offset;
        Easing = easing;
        Properties = properties;
    }

    /// <summary>Gets the keyframe's normalized offset.</summary>
    [DomName("offset")]
    public double? Offset { get; }

    /// <summary>Gets the keyframe easing.</summary>
    [DomName("easing")]
    public string Easing { get; }

    /// <summary>Gets the keyframe property values.</summary>
    [DomName("properties")]
    public IReadOnlyDictionary<string, string> Properties { get; }

    internal object ToScriptObject()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["offset"] = Offset,
            ["easing"] = Easing,
        };

        foreach (var (property, value) in Properties)
        {
            result[property] = value;
        }

        return result;
    }
}

/// <summary>A script-created Web Animations API animation.</summary>
[DomName("Animation")]
[DomExposed("Window")]
public sealed class Animation : EventTarget
{
    private readonly InteractiveHtmlRendererState _state;
    private readonly TaskCompletionSource<Animation> _ready = NewPromise();
    private TaskCompletionSource<Animation> _finished = NewPromise();
    private double? _holdTime;
    private double? _startTime;
    private double _playbackRate = 1d;
    private bool _isPaused;
    private bool _isCancelled;
    private bool _isFinished;
    private DomEventHandler? _onfinish;
    private DomEventHandler? _oncancel;

    internal Animation(InteractiveHtmlRendererState state, KeyframeEffect effect)
    {
        _state = state;
        Effect = effect;
    }

    /// <summary>The effect controlled by this animation.</summary>
    [DomName("effect")]
    public KeyframeEffect Effect { get; }

    /// <summary>Gets or sets the current time in milliseconds.</summary>
    [DomName("currentTime")]
    public double? CurrentTime
    {
        get => _holdTime ?? (_startTime is { } start ? (TimelineNow - start) * _playbackRate : null);
        set
        {
            _holdTime = value;
            if (_startTime is not null && _playbackRate != 0d)
            {
                _startTime = TimelineNow - (value ?? 0d) / _playbackRate;
            }
            else if (_playbackRate == 0d)
            {
                _startTime = null;
            }

            Update();
            Invalidate();
        }
    }

    /// <summary>Gets or sets the playback rate.</summary>
    [DomName("playbackRate")]
    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            var time = CurrentTime;
            _playbackRate = value;
            if (value == 0d)
            {
                _holdTime = time;
                _startTime = null;
            }
            else if (time is not null && _startTime is not null)
            {
                _startTime = TimelineNow - time.Value / value;
            }

            Invalidate();
        }
    }

    /// <summary>Gets the animation's playback state.</summary>
    [DomName("playState")]
    public string PlayState => _isCancelled ? "idle" : _isPaused ? "paused" : _isFinished ? "finished" : _startTime is null ? "idle" : "running";

    /// <summary>Gets or sets the animation id.</summary>
    [DomName("id")]
    public string Id
    {
        get => Effect.Timing.Id;
        set => Effect.Timing.Id = value ?? string.Empty;
    }

    /// <summary>Gets the promise resolved when this animation becomes ready.</summary>
    [DomName("ready")]
    public Task<Animation> Ready => _ready.Task;

    /// <summary>Gets the promise resolved when this animation finishes.</summary>
    [DomName("finished")]
    public Task<Animation> Finished => _finished.Task;

    /// <summary>Gets or sets the finish event handler.</summary>
    [DomName("onfinish")]
    public DomEventHandler? OnFinish
    {
        get => _onfinish;
        set => SetEventHandler("finish", ref _onfinish, value);
    }

    /// <summary>Gets or sets the cancel event handler.</summary>
    [DomName("oncancel")]
    public DomEventHandler? OnCancel
    {
        get => _oncancel;
        set => SetEventHandler("cancel", ref _oncancel, value);
    }

    /// <summary>Starts or resumes playback.</summary>
    [DomName("play")]
    public void Play()
    {
        if (_isFinished || _isCancelled)
        {
            _holdTime = _playbackRate < 0d ? EffectEndTime : 0d;
            _finished = NewPromise();
        }

        if (_holdTime is { } hold)
        {
            _startTime = _playbackRate == 0d ? TimelineNow : TimelineNow - hold / _playbackRate;
            _holdTime = null;
        }
        else if (_startTime is null)
        {
            _startTime = TimelineNow;
        }

        _isPaused = false;
        _isCancelled = false;
        _isFinished = false;
        _ready.TrySetResult(this);
        Update();
        Invalidate();
    }

    /// <summary>Pauses playback.</summary>
    [DomName("pause")]
    public void Pause()
    {
        if (_startTime is null && !_isFinished)
        {
            Play();
        }

        _holdTime = CurrentTime ?? 0d;
        _startTime = null;
        _isPaused = true;
        _ready.TrySetResult(this);
        Invalidate();
    }

    /// <summary>Cancels playback and rejects the current finished promise.</summary>
    [DomName("cancel")]
    public void Cancel()
    {
        if (_isCancelled)
        {
            return;
        }

        _startTime = null;
        _holdTime = null;
        _isPaused = false;
        _isFinished = false;
        _isCancelled = true;
        _state.RemoveAnimation(this);
        _finished.TrySetException(new DomException(DomError.Abort));
        Dispatch(new Event("cancel"));
        Invalidate();
    }

    /// <summary>Moves playback to its end and dispatches a finish event.</summary>
    [DomName("finish")]
    public void Finish()
    {
        if (_playbackRate == 0d)
        {
            throw new DomException(DomError.InvalidState);
        }

        _holdTime = _playbackRate > 0d ? EffectEndTime : 0d;
        _startTime = null;
        Complete();
        Invalidate();
    }

    /// <summary>Reverses the playback direction.</summary>
    [DomName("reverse")]
    public void Reverse()
    {
        if (CurrentTime is null)
        {
            _holdTime = EffectEndTime;
        }

        PlaybackRate = -_playbackRate;
        Play();
    }

    internal bool IsContributing => !_isCancelled && (_startTime is not null || _isPaused || _isFinished);

    internal bool IsRunning => !_isCancelled && !_isPaused && !_isFinished && _startTime is not null;

    internal IEnumerable<string> AnimatedProperties => Effect.Frames.SelectMany(frame => frame.Properties.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    internal string? GetAnimatedValue(string property, double timelineNow, AngleSharp.Css.Dom.ICssStyleDeclaration? style)
    {
        if (!IsContributing || !Effect.Frames.Any(frame => frame.Properties.ContainsKey(property)))
        {
            return null;
        }

        var localTime = _holdTime ?? (_startTime is { } start ? (timelineNow - start) * _playbackRate : 0d);
        var timing = Effect.Timing;
        var activeDuration = ActiveDuration;
        var activeTime = localTime - timing.Delay;
        var before = activeTime < 0d;
        var after = activeTime >= activeDuration;

        if ((before && timing.Fill is not ("backwards" or "both")) ||
            (after && timing.Fill is not ("forwards" or "both")))
        {
            return null;
        }

        activeTime = Math.Clamp(activeTime, 0d, activeDuration);
        var duration = Math.Max(0d, timing.Duration);
        var iteration = duration > 0d ? Math.Floor(activeTime / duration) : 0d;
        var progress = duration > 0d && !after ? (activeTime - iteration * duration) / duration : 1d;

        if (timing.Iterations <= 0d)
        {
            progress = 1d;
        }
        else if (after && timing.Iterations > 0d && !double.IsPositiveInfinity(timing.Iterations))
        {
            iteration = Math.Max(0d, Math.Ceiling(timing.Iterations) - 1d);
            progress = timing.Iterations % 1d is var fraction && fraction > 0d ? fraction : 1d;
        }

        if (timing.Direction.Equals("reverse", StringComparison.OrdinalIgnoreCase) ||
            (timing.Direction.Equals("alternate", StringComparison.OrdinalIgnoreCase) && iteration % 2d == 1d) ||
            (timing.Direction.Equals("alternate-reverse", StringComparison.OrdinalIgnoreCase) && iteration % 2d == 0d))
        {
            progress = 1d - progress;
        }

        progress = ParseEasing(timing.Easing).Evaluate(progress);
        return SampleProperty(property, progress, style, Effect.Frames);
    }

    internal void OnTimeAdvanced() => Update();

    private double TimelineNow => _state.CurrentTime.TotalMilliseconds;

    private double ActiveDuration => Effect.Timing.Duration <= 0d || Effect.Timing.Iterations <= 0d
        ? 0d
        : Effect.Timing.Duration * Effect.Timing.Iterations;

    private double EffectEndTime => Math.Max(0d, Effect.Timing.Delay + ActiveDuration + Effect.Timing.EndDelay);

    private void Update()
    {
        if (_startTime is null || _isPaused || _isCancelled)
        {
            return;
        }

        var time = CurrentTime ?? 0d;
        if ((_playbackRate > 0d && time >= EffectEndTime) ||
            (_playbackRate < 0d && time <= 0d))
        {
            _holdTime = _playbackRate > 0d ? EffectEndTime : 0d;
            _startTime = null;
            Complete();
        }
    }

    private void Complete()
    {
        if (_isFinished)
        {
            return;
        }

        _isFinished = true;
        _isPaused = false;
        _finished.TrySetResult(this);
        Dispatch(new Event("finish"));
    }

    private void Invalidate() => _state.InvalidateAnimation();

    private void SetEventHandler(string type, ref DomEventHandler? slot, DomEventHandler? value)
    {
        if (slot is not null)
        {
            RemoveEventListener(type, slot);
        }

        slot = value;
        if (value is not null)
        {
            AddEventListener(type, value);
        }
    }

    private static TaskCompletionSource<Animation> NewPromise() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? SampleProperty(string property, double progress, AngleSharp.Css.Dom.ICssStyleDeclaration? style, IReadOnlyList<AnimationKeyframe> frames)
    {
        var stops = new List<(double Offset, string Value, string Easing)>();
        foreach (var frame in frames)
        {
            if (frame.Offset is { } offset && frame.Properties.TryGetValue(property, out var value))
            {
                stops.Add((offset, value, frame.Easing));
            }
        }

        if (stops.Count == 0)
        {
            return null;
        }

        if (stops[0].Offset > 0d && style is not null)
        {
            stops.Insert(0, (0d, style.GetPropertyValue(property), "linear"));
        }

        if (stops[^1].Offset < 1d && style is not null)
        {
            stops.Add((1d, style.GetPropertyValue(property), "linear"));
        }

        if (progress <= stops[0].Offset)
        {
            return stops[0].Value;
        }

        if (progress >= stops[^1].Offset)
        {
            return stops[^1].Value;
        }

        for (var index = 1; index < stops.Count; index++)
        {
            var upper = stops[index];
            if (progress > upper.Offset)
            {
                continue;
            }

            var lower = stops[index - 1];
            var span = upper.Offset - lower.Offset;
            var part = span > 0d ? (progress - lower.Offset) / span : 1d;
            part = ParseEasing(lower.Easing).Evaluate(part);

            if (CssValueInterpolation.InterpolatableProperties.ContainsKey(property))
            {
                return CssValueInterpolation.Interpolate(property, lower.Value, upper.Value, part);
            }

            return part < 0.5d ? lower.Value : upper.Value;
        }

        return stops[^1].Value;
    }

    private static CssTimingFunction ParseEasing(string easing)
    {
        try
        {
            return CssValueInterpolation.ParseTimingFunction(easing);
        }
        catch
        {
            return CssTimingFunction.Linear;
        }
    }
}

/// <summary>Provides Web Animations API methods on elements.</summary>
[DomExposed("Element")]
public static class ElementWebAnimationsExtensions
{
    /// <summary>Creates and starts a keyframe animation.</summary>
    [DomName("animate")]
    public static Animation Animate(this IElement element, object keyframes, object? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(keyframes);
        var state = GetState(element.Owner ?? throw new DomException(DomError.InvalidState));
        var timing = ParseOptions(options);
        var frames = ParseKeyframes(keyframes);
        var effect = new KeyframeEffect(element, frames, timing);
        return state.CreateAnimation(effect);
    }

    /// <summary>Returns the script-created animations on this element.</summary>
    [DomName("getAnimations")]
    public static Animation[] GetAnimations(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Owner is { } document && document.Context.TryGetDomHarness(out var harness) && harness is InteractiveHtmlRendererState state
            ? state.GetAnimations(element)
            : [];
    }

    internal static AnimationTimeline GetTimeline(IDocument document) => new(document);

    private static InteractiveHtmlRendererState GetState(IDocument document) =>
        document.Context.GetDomHarness() as InteractiveHtmlRendererState
            ?? throw new InvalidOperationException("The browsing context is not connected to the interactive renderer.");

    private static KeyframeAnimationOptions ParseOptions(object? options)
    {
        if (options is null || options is double or float or int or long or decimal)
        {
            return new KeyframeAnimationOptions
            {
                Duration = options is null ? 0d : Convert.ToDouble(options, CultureInfo.InvariantCulture),
            };
        }

        return new KeyframeAnimationOptions
        {
            Duration = ReadNumber(options, "duration", 0d),
            Delay = ReadNumber(options, "delay", 0d),
            EndDelay = ReadNumber(options, "endDelay", 0d),
            Iterations = ReadNumber(options, "iterations", 1d),
            Direction = ReadString(options, "direction", "normal").ToLowerInvariant(),
            Easing = ReadString(options, "easing", "linear"),
            Fill = ReadString(options, "fill", "none").ToLowerInvariant(),
            Id = ReadString(options, "id", string.Empty),
        };
    }

    private static AnimationKeyframe[] ParseKeyframes(object input)
    {
        var rows = new List<Dictionary<string, object?>>();

        if (TryReadDictionary(input, out var objectForm))
        {
            var count = objectForm.Values.OfType<IEnumerable>().Where(value => value is not string).Select(value => value.Cast<object?>().Count()).DefaultIfEmpty(1).Max();
            for (var index = 0; index < count; index++)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, value) in objectForm)
                {
                    if (value is IEnumerable sequence && value is not string)
                    {
                        var values = sequence.Cast<object?>().ToArray();
                        row[name] = values.Length == 0 ? null : values[Math.Min(index, values.Length - 1)];
                    }
                    else
                    {
                        row[name] = value;
                    }
                }
                rows.Add(row);
            }
        }
        else if (input is IEnumerable sequence && input is not string)
        {
            foreach (var item in sequence)
            {
                if (item is not null && TryReadDictionary(item, out var row))
                {
                    rows.Add(row);
                }
            }
        }

        if (rows.Count == 0)
        {
            throw new DomException(DomError.TypeMismatch);
        }

        var rawOffsets = rows.Select(row => row.TryGetValue("offset", out var value) && value is not null
            ? ToNullableDouble(value)
            : null).ToArray();
        var offsets = DistributeOffsets(rawOffsets);
        var result = new AnimationKeyframe[rows.Count];

        for (var index = 0; index < rows.Count; index++)
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in rows[index])
            {
                if (name.Equals("offset", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("easing", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("composite", StringComparison.OrdinalIgnoreCase) ||
                    value is null)
                {
                    continue;
                }

                var property = ToCssProperty(name);
                properties[property] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            var easing = rows[index].TryGetValue("easing", out var easingValue) && easingValue is not null
                ? Convert.ToString(easingValue, CultureInfo.InvariantCulture) ?? "linear"
                : "linear";
            result[index] = new AnimationKeyframe(offsets[index], easing, properties);
        }

        return result;
    }

    private static double?[] DistributeOffsets(double?[] offsets)
    {
        if (offsets.Length == 1)
        {
            return [offsets[0] ?? 1d];
        }

        if (offsets[0] is null)
        {
            offsets[0] = 0d;
        }

        if (offsets[^1] is null)
        {
            offsets[^1] = 1d;
        }

        var index = 0;
        while (index < offsets.Length)
        {
            if (offsets[index] is not null)
            {
                index++;
                continue;
            }

            var start = index - 1;
            var end = index;
            while (end < offsets.Length && offsets[end] is null)
            {
                end++;
            }

            var from = offsets[start] ?? 0d;
            var to = offsets[end] ?? 1d;
            for (var fill = index; fill < end; fill++)
            {
                offsets[fill] = from + (to - from) * (fill - start) / (end - start);
            }
            index = end + 1;
        }

        return offsets;
    }

    private static string ToCssProperty(string property)
    {
        if (property.StartsWith("--", StringComparison.Ordinal))
        {
            return property;
        }

        var result = new System.Text.StringBuilder(property.Length + 4);
        foreach (var character in property)
        {
            if (char.IsUpper(character))
            {
                result.Append('-');
                result.Append(char.ToLowerInvariant(character));
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }

    private static double ReadNumber(object source, string name, double fallback)
    {
        var value = ReadValue(source, name);
        if (value is null)
        {
            return fallback;
        }
        if (value is string text && text.Equals("infinite", StringComparison.OrdinalIgnoreCase))
        {
            return double.PositiveInfinity;
        }
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;
    }

    private static string ReadString(object source, string name, string fallback) =>
        Convert.ToString(ReadValue(source, name), CultureInfo.InvariantCulture) ?? fallback;

    private static double? ToNullableDouble(object value) =>
        double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static object? ReadValue(object source, string name)
    {
        if (TryReadDictionary(source, out var dictionary))
        {
            return dictionary.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        }

        var property = source.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        return property?.GetValue(source);
    }

    private static bool TryReadDictionary(object source, out Dictionary<string, object?> result)
    {
        result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source is IDictionary dictionary)
        {
            foreach (DictionaryEntry item in dictionary)
            {
                if (item.Key is string key)
                {
                    result[key] = item.Value;
                }
            }
            return result.Count > 0;
        }

        var type = source.GetType();
        if (type.IsPrimitive || source is string || source is IEnumerable)
        {
            return false;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanRead && property.GetIndexParameters().Length == 0)
            {
                result[property.Name] = property.GetValue(source);
            }
        }
        return result.Count > 0;
    }
}

/// <summary>Provides the Web Animations timeline and document animation query.</summary>
[DomExposed("Document")]
public static class DocumentWebAnimationsExtensions
{
    /// <summary>Gets the document timeline.</summary>
    [DomName("timeline")]
    [DomAccessor(Accessors.Getter)]
    public static AnimationTimeline GetTimeline(this IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ElementWebAnimationsExtensions.GetTimeline(document);
    }

    /// <summary>Returns script-created animations in this document.</summary>
    [DomName("getAnimations")]
    public static Animation[] GetAnimations(this IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Context.TryGetDomHarness(out var harness) && harness is InteractiveHtmlRendererState state
            ? state.GetAnimations()
            : [];
    }
}
