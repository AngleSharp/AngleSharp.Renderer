namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Browser;
using AngleSharp.Dom.Geometry;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

/// <summary>
/// The callback receiving the changes an intersection observer found.
/// </summary>
/// <param name="entries">The changes.</param>
/// <param name="observer">The observer.</param>
[DomName("IntersectionObserverCallback")]
public delegate void IntersectionObserverCallback(IntersectionObserverEntry[] entries, IntersectionObserver observer);

/// <summary>
/// Represents the thresholds given to an intersection observer: one or several ratios.
/// </summary>
public sealed class IntersectionObserverThreshold
{
    private IntersectionObserverThreshold(double[] values)
    {
        Values = values;
    }

    /// <summary>
    /// Gets the given ratios.
    /// </summary>
    public IReadOnlyList<double> Values { get; }

    /// <summary>
    /// Creates the thresholds from a single ratio.
    /// </summary>
    public static implicit operator IntersectionObserverThreshold(double value) => new([value]);

    /// <summary>
    /// Creates the thresholds from several ratios.
    /// </summary>
    public static implicit operator IntersectionObserverThreshold(double[] values) => new(values ?? []);
}

/// <summary>
/// Represents the options of an intersection observer.
/// </summary>
[DomName("IntersectionObserverInit")]
public sealed class IntersectionObserverOptions
{
    /// <summary>
    /// Gets or sets the element or document whose box targets are compared with; by default the
    /// viewport.
    /// </summary>
    [DomName("root")]
    public INode? Root { get; set; }

    /// <summary>
    /// Gets or sets what the box of the root grows (or shrinks) by, like a margin in CSS, in
    /// pixels or percentages.
    /// </summary>
    [DomName("rootMargin")]
    public string? RootMargin { get; set; }

    /// <summary>
    /// Gets or sets the ratios of visibility to report the crossing of.
    /// </summary>
    [DomName("threshold")]
    public IntersectionObserverThreshold? Threshold { get; set; }
}

/// <summary>
/// Represents a change of the intersection of a target with the root.
/// </summary>
[DomName("IntersectionObserverEntry")]
public sealed class IntersectionObserverEntry
{
    internal IntersectionObserverEntry(double time, IDomRect? rootBounds, IDomRect boundingClientRect, IDomRect intersectionRect, bool isIntersecting, double intersectionRatio, IElement target)
    {
        Time = time;
        RootBounds = rootBounds;
        BoundingClientRect = boundingClientRect;
        IntersectionRect = intersectionRect;
        IsIntersecting = isIntersecting;
        IntersectionRatio = intersectionRatio;
        Target = target;
    }

    /// <summary>
    /// Gets when the change was found in milliseconds, since the document started observing.
    /// </summary>
    [DomName("time")]
    public double Time { get; }

    /// <summary>
    /// Gets the box of the root with its margin.
    /// </summary>
    [DomName("rootBounds")]
    public IDomRect? RootBounds { get; }

    /// <summary>
    /// Gets the box of the target.
    /// </summary>
    [DomName("boundingClientRect")]
    public IDomRect BoundingClientRect { get; }

    /// <summary>
    /// Gets the part of the target's box that is visible in the root.
    /// </summary>
    [DomName("intersectionRect")]
    public IDomRect IntersectionRect { get; }

    /// <summary>
    /// Gets if the target touches the root, which may also be with no area at all.
    /// </summary>
    [DomName("isIntersecting")]
    public bool IsIntersecting { get; }

    /// <summary>
    /// Gets how much of the target is visible, from 0 to 1.
    /// </summary>
    [DomName("intersectionRatio")]
    public double IntersectionRatio { get; }

    /// <summary>
    /// Gets the observed element.
    /// </summary>
    [DomName("target")]
    public IElement Target { get; }
}

/// <summary>
/// Represents the IntersectionObserver of the standard, which tells when elements come into view
/// (or into the box of another element). As in browsers, the callback is invoked in a task of the
/// event loop, with the first one telling the state of each target; without an event loop it is
/// invoked right away.
/// </summary>
[DomName("IntersectionObserver")]
[DomExposed("Window")]
public sealed class IntersectionObserver
{
    private static readonly char[] Spaces = [' ', '\t', '\r', '\n', '\f'];

    private readonly IntersectionObserverCallback _callback;
    private readonly IntersectionObserverScheduler _scheduler;
    private readonly (double Value, bool IsPercentage)[] _margin;
    private readonly List<IntersectionObserverEntry> _queue = [];
    private readonly Dictionary<IElement, Registration> _targets = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Creates a new observer.
    /// </summary>
    /// <param name="window">The window of the observer.</param>
    /// <param name="callback">The callback receiving the changes.</param>
    /// <param name="options">The options of the observer.</param>
    [DomConstructor]
    public IntersectionObserver(IWindow window, IntersectionObserverCallback callback, IntersectionObserverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(callback);

        _callback = callback;
        _scheduler = IntersectionObserverScheduler.For(window.Document);
        _margin = ParseMargin(options?.RootMargin);
        Root = options?.Root;
        Thresholds = ParseThresholds(options?.Threshold);

        if (Root is not null and not IElement and not IDocument)
        {
            throw new DomException(DomError.TypeMismatch);
        }
    }

    /// <summary>
    /// Gets the element or document whose box targets are compared with, if any.
    /// </summary>
    [DomName("root")]
    public INode? Root { get; }

    /// <summary>
    /// Gets the margin of the root.
    /// </summary>
    [DomName("rootMargin")]
    public string RootMargin => string.Join(' ', _margin.Select(static m => FormattableString.Invariant($"{m.Value}{(m.IsPercentage ? "%" : "px")}")));

    /// <summary>
    /// Gets the ratios of visibility to report the crossing of, in ascending order.
    /// </summary>
    [DomName("thresholds")]
    public double[] Thresholds { get; }

    /// <summary>
    /// Starts observing the element.
    /// </summary>
    /// <param name="target">The element to observe.</param>
    [DomName("observe")]
    public void Observe(IElement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_targets.TryAdd(target, new Registration()))
        {
            _scheduler.Add(this);
            _scheduler.Schedule();
        }
    }

    /// <summary>
    /// Stops observing the element.
    /// </summary>
    /// <param name="target">The element to stop observing.</param>
    [DomName("unobserve")]
    public void Unobserve(IElement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_targets.Remove(target) && _targets.Count == 0)
        {
            _scheduler.Remove(this);
        }
    }

    /// <summary>
    /// Stops observing everything.
    /// </summary>
    [DomName("disconnect")]
    public void Disconnect()
    {
        _targets.Clear();
        _queue.Clear();
        _scheduler.Remove(this);
    }

    /// <summary>
    /// Gets the changes the callback has not been told about yet, which it won't be anymore.
    /// </summary>
    /// <returns>The changes.</returns>
    [DomName("takeRecords")]
    public IntersectionObserverEntry[] TakeRecords()
    {
        var records = _queue.ToArray();
        _queue.Clear();
        return records;
    }

    /// <summary>
    /// Compares the targets with the root, and tells the callback of what changed.
    /// </summary>
    internal void Update(double time)
    {
        foreach (var (target, registration) in _targets.ToArray())
        {
            var entry = Measure(target, time);
            var ratio = entry.IntersectionRatio;
            var index = Array.FindIndex(Thresholds, m => m > ratio);
            var thresholdIndex = index < 0 ? Thresholds.Length : index;

            if (thresholdIndex != registration.ThresholdIndex || entry.IsIntersecting != registration.IsIntersecting)
            {
                registration.ThresholdIndex = thresholdIndex;
                registration.IsIntersecting = entry.IsIntersecting;
                _queue.Add(entry);
            }
        }

        if (_queue.Count > 0)
        {
            _callback.Invoke(TakeRecords(), this);
        }
    }

    private IntersectionObserverEntry Measure(IElement target, double time)
    {
        var rootBox = GetRootBox(target);
        var rootBounds = rootBox is { } bounds ? ToDomRect(bounds) : null;
        var box = ElementCssomViewExtensions.GetRenderedBox(target);

        if (box is not { } targetBox || rootBox is not { } root || !IsInsideRoot(target))
        {
            return new IntersectionObserverEntry(time, rootBounds, box is { } some ? ToDomRect(some) : new DomRect(), new DomRect(), false, 0d, target);
        }

        var clip = ElementCssomViewExtensions.GetClipTo(target, Root as IElement);
        var visible = Intersect(targetBox, clip is { } inner ? Intersect(inner, root) : root);
        var isIntersecting = visible.Width >= 0 && visible.Height >= 0;

        if (!isIntersecting)
        {
            return new IntersectionObserverEntry(time, rootBounds, ToDomRect(targetBox), new DomRect(), false, 0d, target);
        }

        var area = (double)targetBox.Width * targetBox.Height;
        var ratio = area > 0 ? Math.Min(1d, visible.Width * (double)visible.Height / area) : 1d;
        return new IntersectionObserverEntry(time, rootBounds, ToDomRect(targetBox), ToDomRect(visible), true, ratio, target);
    }

    private bool IsInsideRoot(IElement target)
    {
        if (Root is not IElement root)
        {
            return Root is null || ReferenceEquals(Root, target.Owner);
        }

        for (var current = target.ParentElement; current is not null; current = current.ParentElement)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }
        }

        return false;
    }

    private RenderRect? GetRootBox(IElement target)
    {
        RenderRect box;

        if (Root is IElement element)
        {
            if (ElementCssomViewExtensions.GetPaddingBox(element) is not { } padding)
            {
                return null;
            }

            box = padding;
        }
        else
        {
            var device = (Root as IDocument ?? target.Owner)?.Context.GetDomHarness().RenderDevice;
            box = new RenderRect(0f, 0f, device?.ViewPortWidth ?? 0, device?.ViewPortHeight ?? 0);
        }

        var top = Resolve(_margin[0], box.Height);
        var right = Resolve(_margin[1], box.Width);
        var bottom = Resolve(_margin[2], box.Height);
        var left = Resolve(_margin[3], box.Width);
        return new RenderRect(box.X - left, box.Y - top, box.Width + left + right, box.Height + top + bottom);
    }

    private static float Resolve((double Value, bool IsPercentage) margin, float size) =>
        (float)(margin.IsPercentage ? margin.Value * size / 100d : margin.Value);

    private static RenderRect Intersect(RenderRect a, RenderRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new RenderRect(left, top, right - left, bottom - top);
    }

    private static DomRect ToDomRect(RenderRect box) => new(box.X, box.Y, box.Width, box.Height);

    private static double[] ParseThresholds(IntersectionObserverThreshold? threshold)
    {
        var values = threshold?.Values ?? [0d];

        if (values.Any(static m => double.IsNaN(m) || m < 0d || m > 1d))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "The ratios of an intersection observer are from 0 to 1.");
        }

        return values.Count == 0 ? [0d] : values.Distinct().Order().ToArray();
    }

    // as for a margin in CSS: one to four values, for the top, right, bottom and left
    private static (double Value, bool IsPercentage)[] ParseMargin(string? margin)
    {
        var parts = (margin ?? string.Empty).Split(Spaces, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length > 4)
        {
            throw new DomException(DomError.Syntax);
        }

        var values = parts.Length == 0 ? [(0d, false)] : parts.Select(ParseLength).ToArray();
        var top = values[0];
        var right = values.Length > 1 ? values[1] : top;
        var bottom = values.Length > 2 ? values[2] : top;
        var left = values.Length > 3 ? values[3] : right;
        return [top, right, bottom, left];
    }

    private static (double Value, bool IsPercentage) ParseLength(string text)
    {
        var isPercentage = text.EndsWith('%');
        var number = isPercentage ? text[..^1] : text.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? text[..^2] : text == "0" ? text : null;

        if (number is null || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new DomException(DomError.Syntax);
        }

        return (value, isPercentage);
    }

    private sealed class Registration
    {
        public int ThresholdIndex { get; set; } = -1;

        public bool IsIntersecting { get; set; }
    }
}

/// <summary>
/// Runs the observers of a document: after anything that may have moved a box, once per turn of the
/// event loop, as scrolling alone may not even need a layout.
/// </summary>
internal sealed class IntersectionObserverScheduler
{
    private static readonly ConditionalWeakTable<IDocument, IntersectionObserverScheduler> Schedulers = [];

    private readonly IDocument _document;
    private readonly List<IntersectionObserver> _observers = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly EventHandler _onChanged;
    private InteractiveHtmlRendererState? _state;
    private int _isScheduled;

    private IntersectionObserverScheduler(IDocument document)
    {
        _document = document;
        _onChanged = (_, _) => Schedule();
    }

    public static IntersectionObserverScheduler For(IDocument document) =>
        Schedulers.GetValue(document, static key => new IntersectionObserverScheduler(key));

    public void Add(IntersectionObserver observer)
    {
        if (!_observers.Contains(observer))
        {
            _observers.Add(observer);
        }

        if (_state is null && _document.Context.GetDomHarness() is InteractiveHtmlRendererState state)
        {
            _state = state;
            state.PaintInvalidated += _onChanged;
            state.FrameUpdated += _onChanged;
        }
    }

    public void Remove(IntersectionObserver observer)
    {
        _observers.Remove(observer);

        if (_observers.Count == 0)
        {
            Detach();
        }
    }

    public void Schedule()
    {
        if (Interlocked.Exchange(ref _isScheduled, 1) == 0)
        {
            if (_document.Context.GetService<IEventLoop>() is { } loop)
            {
                loop.Enqueue(_ => Run(), TaskPriority.Normal);
            }
            else
            {
                Run();
            }
        }
    }

    private void Run()
    {
        Volatile.Write(ref _isScheduled, 0);

        // the document is gone, and with it everything it observed
        if (!ReferenceEquals(_document.Context.Active, _document))
        {
            Detach();
            return;
        }

        var time = _clock.Elapsed.TotalMilliseconds;

        foreach (var observer in _observers.ToArray())
        {
            observer.Update(time);
        }
    }

    private void Detach()
    {
        if (_state is { } state)
        {
            state.PaintInvalidated -= _onChanged;
            state.FrameUpdated -= _onChanged;
            _state = null;
        }
    }
}
