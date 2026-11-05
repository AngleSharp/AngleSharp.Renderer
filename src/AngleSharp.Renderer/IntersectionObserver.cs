namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Geometry;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;
using System.Globalization;
using System.Linq;

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
    private readonly LayoutObserverScheduler _scheduler;
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
        _scheduler = LayoutObserverScheduler.For(window.Document);
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
        var box = ElementCssomViewExtensions.GetRenderedBox(target);
        var isNested = Root is null && target.Owner?.Context.Parent is not null;
        var isSameOrigin = !isNested || IsSameOriginAsTop(target.Owner!);
        // a margin isn't for a page to learn anything about what is around it
        var rootBox = isNested ? GetTopViewport(target.Owner!, isSameOrigin) : GetRootBox(target);
        var rootBounds = rootBox is { } bounds && isSameOrigin ? ToDomRect(bounds) : null;

        if (box is not { } targetBox || rootBox is not { } root || !IsInsideRoot(target))
        {
            return new IntersectionObserverEntry(time, rootBounds, box is { } some ? ToDomRect(some) : new DomRect(), new DomRect(), false, 0d, target);
        }

        var clip = ElementCssomViewExtensions.GetClipTo(target, Root as IElement);
        var visible = clip is { } inner ? Intersect(targetBox, inner) : targetBox;
        var offset = (X: 0f, Y: 0f);

        if (isNested)
        {
            // through the frames around it, in which the rest of the way is what counts
            if (GetViewport(target.Owner!) is { } viewport)
            {
                visible = Intersect(visible, viewport);
            }

            for (var document = target.Owner!; visible.Width >= 0 && visible.Height >= 0 && GetFrame(document) is { } frame; document = frame.Owner!)
            {
                if (ElementCssomViewExtensions.GetContentBox(frame) is not { } content)
                {
                    return new IntersectionObserverEntry(time, rootBounds, ToDomRect(targetBox), new DomRect(), false, 0d, target);
                }

                visible = Intersect(new RenderRect(visible.X + content.X, visible.Y + content.Y, visible.Width, visible.Height), content);
                offset = (offset.X + content.X, offset.Y + content.Y);

                if (ElementCssomViewExtensions.GetClipTo(frame, null) is { } around)
                {
                    visible = Intersect(visible, around);
                }
            }
        }

        visible = Intersect(visible, root);

        if (visible.Width < 0 || visible.Height < 0)
        {
            return new IntersectionObserverEntry(time, rootBounds, ToDomRect(targetBox), new DomRect(), false, 0d, target);
        }

        var area = (double)targetBox.Width * targetBox.Height;
        var ratio = area > 0 ? Math.Min(1d, visible.Width * (double)visible.Height / area) : 1d;
        var rect = new RenderRect(visible.X - offset.X, visible.Y - offset.Y, visible.Width, visible.Height);
        return new IntersectionObserverEntry(time, rootBounds, ToDomRect(targetBox), ToDomRect(rect), true, ratio, target);
    }

    /// <summary>
    /// Gets the iframe showing the document, if any.
    /// </summary>
    internal static IElement? GetFrame(IDocument document) =>
        document.Context?.Parent?.Active?.QuerySelectorAll("iframe")
            .OfType<AngleSharp.Html.Dom.IHtmlInlineFrameElement>()
            .FirstOrDefault(frame => ReferenceEquals(frame.ContentDocument, document));

    private static IDocument GetTop(IDocument document)
    {
        var current = document;

        while (GetFrame(current) is { Owner: { } owner })
        {
            current = owner;
        }

        return current;
    }

    // A document without an address of its own (about:srcdoc) has the origin of what is around it,
    // and one in a sandboxed frame an origin of its own that equals nothing.
    private static bool IsSameOriginAsTop(IDocument document)
    {
        var current = document;
        var origin = (string?)null;

        while (GetFrame(current) is { Owner: { } owner } frame)
        {
            var sandbox = frame.GetAttribute("sandbox");

            if (sandbox is not null && !sandbox.Split(Spaces, StringSplitOptions.RemoveEmptyEntries).Contains("allow-same-origin", StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            origin ??= HasOwnOrigin(current) ? current.Origin : null;
            current = owner;
        }

        return origin is null || string.Equals(origin, current.Origin, StringComparison.Ordinal);
    }

    private static bool HasOwnOrigin(IDocument document) =>
        !document.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

    private static RenderRect? GetViewport(IDocument document)
    {
        var device = document.Context.GetDomHarness().RenderDevice;
        return new RenderRect(0f, 0f, device.ViewPortWidth, device.ViewPortHeight);
    }

    private RenderRect? GetTopViewport(IDocument document, bool withMargin)
    {
        if (GetViewport(GetTop(document)) is not { } box)
        {
            return null;
        }

        return withMargin ? Grow(box) : box;
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

        return Grow(box);
    }

    private RenderRect Grow(RenderRect box)
    {
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
