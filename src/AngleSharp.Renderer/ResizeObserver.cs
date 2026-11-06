namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Events;
using AngleSharp.Dom.Geometry;
using AngleSharp.Renderer.Rendering;
using System.Linq;

/// <summary>
/// The callback receiving the changes of size a resize observer found.
/// </summary>
/// <param name="entries">The changes.</param>
/// <param name="observer">The observer.</param>
[DomName("ResizeObserverCallback")]
public delegate void ResizeObserverCallback(ResizeObserverEntry[] entries, ResizeObserver observer);

/// <summary>
/// Represents the options of observing an element.
/// </summary>
[DomName("ResizeObserverOptions")]
public sealed class ResizeObserverOptions
{
    /// <summary>
    /// Gets or sets the box to observe: content-box (by default), border-box or
    /// device-pixel-content-box.
    /// </summary>
    [DomName("box")]
    public string? Box { get; set; }
}

/// <summary>
/// Represents the size of a box along the directions of its writing mode.
/// </summary>
[DomName("ResizeObserverSize")]
public sealed class ResizeObserverSize
{
    internal ResizeObserverSize(double inlineSize, double blockSize)
    {
        InlineSize = inlineSize;
        BlockSize = blockSize;
    }

    /// <summary>
    /// Gets the size along the lines of text: the width, unless the writing mode is vertical.
    /// </summary>
    [DomName("inlineSize")]
    public double InlineSize { get; }

    /// <summary>
    /// Gets the size across the lines of text: the height, unless the writing mode is vertical.
    /// </summary>
    [DomName("blockSize")]
    public double BlockSize { get; }
}

/// <summary>
/// Represents an element whose size changed.
/// </summary>
[DomName("ResizeObserverEntry")]
public sealed class ResizeObserverEntry
{
    internal ResizeObserverEntry(IElement target, IDomRect contentRect, ResizeObserverSize[] borderBoxSize, ResizeObserverSize[] contentBoxSize, ResizeObserverSize[] devicePixelContentBoxSize)
    {
        Target = target;
        ContentRect = contentRect;
        BorderBoxSize = borderBoxSize;
        ContentBoxSize = contentBoxSize;
        DevicePixelContentBoxSize = devicePixelContentBoxSize;
    }

    /// <summary>
    /// Gets the observed element.
    /// </summary>
    [DomName("target")]
    public IElement Target { get; }

    /// <summary>
    /// Gets the content box, where the position is the padding at the top left.
    /// </summary>
    [DomName("contentRect")]
    public IDomRect ContentRect { get; }

    /// <summary>
    /// Gets the size of the border box.
    /// </summary>
    [DomName("borderBoxSize")]
    public ResizeObserverSize[] BorderBoxSize { get; }

    /// <summary>
    /// Gets the size of the content box.
    /// </summary>
    [DomName("contentBoxSize")]
    public ResizeObserverSize[] ContentBoxSize { get; }

    /// <summary>
    /// Gets the size of the content box in device pixels.
    /// </summary>
    [DomName("devicePixelContentBoxSize")]
    public ResizeObserverSize[] DevicePixelContentBoxSize { get; }
}

/// <summary>
/// Represents the ResizeObserver of the standard, which tells when the size of an element changes.
/// As in browsers, the callback is invoked in a task of the event loop; without an event loop it is
/// invoked right away.
/// </summary>
[DomName("ResizeObserver")]
[DomExposed("Window")]
public sealed class ResizeObserver
{
    private const string ContentBox = "content-box";
    private const string BorderBox = "border-box";
    private const string DevicePixelContentBox = "device-pixel-content-box";
    private const string LoopMessage = "ResizeObserver loop completed with undelivered notifications.";
    private const double DefaultResolution = 96d;

    private readonly ResizeObserverCallback _callback;
    private readonly LayoutObserverScheduler _scheduler;
    private readonly List<Observation> _observations = [];

    /// <summary>
    /// Creates a new observer.
    /// </summary>
    /// <param name="window">The window of the observer.</param>
    /// <param name="callback">The callback receiving the changes.</param>
    [DomConstructor]
    public ResizeObserver(IWindow window, ResizeObserverCallback callback)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(callback);

        _callback = callback;
        _scheduler = LayoutObserverScheduler.For(window.Document);
    }

    /// <summary>
    /// Starts observing the element. Observing it again with another box starts over.
    /// </summary>
    /// <param name="target">The element to observe.</param>
    /// <param name="options">The options, if any.</param>
    [DomName("observe")]
    public void Observe(IElement target, ResizeObserverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var box = options?.Box ?? ContentBox;

        if (box is not (ContentBox or BorderBox or DevicePixelContentBox))
        {
            throw new ArgumentException($"The box '{box}' is not one that can be observed.", nameof(options));
        }

        var existing = _observations.FindIndex(m => ReferenceEquals(m.Target, target));

        if (existing >= 0)
        {
            if (_observations[existing].Box == box)
            {
                return;
            }

            _observations.RemoveAt(existing);
        }

        _observations.Add(new Observation(target, box));
        _scheduler.Add(this);
        _scheduler.Schedule();
    }

    /// <summary>
    /// Stops observing the element.
    /// </summary>
    /// <param name="target">The element to stop observing.</param>
    [DomName("unobserve")]
    public void Unobserve(IElement target)
    {
        ArgumentNullException.ThrowIfNull(target);

        _observations.RemoveAll(m => ReferenceEquals(m.Target, target));

        if (_observations.Count == 0)
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
        _observations.Clear();
        _scheduler.Remove(this);
    }

    /// <summary>
    /// Tells the observers of a document what changed (Resize Observer 3.4). What a callback changes
    /// is only told in the same go if it is deeper in the tree than what was told last, which is what
    /// makes this end, however the callbacks behave.
    /// </summary>
    /// <returns>True if there is something left to be told next time.</returns>
    internal static bool Deliver(IDocument document, ResizeObserver[] observers)
    {
        var depth = 0;

        while (observers.Any(m => m.HasChanges(depth)))
        {
            var shallowest = int.MaxValue;

            foreach (var observer in observers)
            {
                shallowest = Math.Min(shallowest, observer.DeliverChanges(depth));
            }

            depth = shallowest;
        }

        if (!observers.Any(m => m.HasChanges(0)))
        {
            return false;
        }

        // A browser reports that, and pages expect it: they could be waiting for a size
        var error = new ErrorEvent();
        error.Init(EventNames.Error, false, true);
        error.Init(string.Empty, 0, 0, new InvalidOperationException(LoopMessage));
        document.DefaultView?.Dispatch(error);
        return true;
    }

    private bool HasChanges(int depth) =>
        _observations.ToArray().Any(m => GetDepth(m.Target) > depth && !m.LastSize.Equals(GetSize(m)));

    private int DeliverChanges(int depth)
    {
        var shallowest = int.MaxValue;
        var entries = new List<ResizeObserverEntry>();

        foreach (var observation in _observations.ToArray())
        {
            var targetDepth = GetDepth(observation.Target);

            if (targetDepth > depth && !observation.LastSize.Equals(GetSize(observation)))
            {
                observation.LastSize = GetSize(observation);
                entries.Add(CreateEntry(observation.Target));
                shallowest = Math.Min(shallowest, targetDepth);
            }
        }

        if (entries.Count > 0)
        {
            _callback.Invoke(entries.ToArray(), this);
        }

        return shallowest;
    }

    // what is asked for in the writing mode of the box, as that is what scripts get to see
    private static (double Inline, double Block) GetSize(Observation observation)
    {
        var sizes = ElementCssomViewExtensions.GetObservedSizes(observation.Target);

        return observation.Box switch
        {
            BorderBox => Along(sizes.BorderWidth, sizes.BorderHeight, sizes.IsVertical),
            DevicePixelContentBox => ToDevicePixels(observation.Target, sizes.Content, sizes.IsVertical),
            _ => Along(sizes.Content.Width, sizes.Content.Height, sizes.IsVertical),
        };
    }

    private static (double Inline, double Block) Along(double width, double height, bool isVertical) =>
        isVertical ? (height, width) : (width, height);

    private static (double Inline, double Block) ToDevicePixels(IElement target, RenderRect content, bool isVertical)
    {
        var resolution = target.Owner?.Context.GetDomHarness().RenderDevice.Resolution ?? 0;
        var ratio = resolution > 0 ? resolution / DefaultResolution : 1d;
        return Along(Math.Round(content.Width * ratio), Math.Round(content.Height * ratio), isVertical);
    }

    private static ResizeObserverEntry CreateEntry(IElement target)
    {
        var sizes = ElementCssomViewExtensions.GetObservedSizes(target);
        var content = Along(sizes.Content.Width, sizes.Content.Height, sizes.IsVertical);
        var border = Along(sizes.BorderWidth, sizes.BorderHeight, sizes.IsVertical);
        var pixels = ToDevicePixels(target, sizes.Content, sizes.IsVertical);
        return new ResizeObserverEntry(
            target,
            new DomRect(sizes.Content.X, sizes.Content.Y, sizes.Content.Width, sizes.Content.Height),
            [new ResizeObserverSize(border.Inline, border.Block)],
            [new ResizeObserverSize(content.Inline, content.Block)],
            [new ResizeObserverSize(pixels.Inline, pixels.Block)]);
    }

    // the root element is the first level, and what is in a shadow tree is below its host
    private static int GetDepth(IElement element)
    {
        var depth = 1;
        var current = element;

        while (current is not null)
        {
            var parent = current.ParentElement ?? (current.Parent as IShadowRoot)?.Host;

            if (parent is not null)
            {
                depth++;
            }

            current = parent;
        }

        return depth;
    }

    private sealed class Observation(IElement target, string box)
    {
        public IElement Target { get; } = target;

        public string Box { get; } = box;

        public (double Inline, double Block) LastSize { get; set; }
    }
}
