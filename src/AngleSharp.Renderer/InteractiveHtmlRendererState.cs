using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using System.Linq;
using System.Runtime.CompilerServices;

namespace AngleSharp.Renderer;

/// <summary>
/// Stores interactive renderer state (scroll positions, hover state, and render device) per browsing context.
/// </summary>
internal sealed partial class InteractiveHtmlRendererState : IDomHarness
{
    private readonly ConditionalWeakTable<IElement, ElementInteractionState> _elementStates = new();
    private readonly HtmlRenderer _renderer;
    private readonly List<IElement> _forcedHoverChain = [];
    private readonly CssTransitionTracker _transitionTracker = new();
    private readonly CssAnimationTracker _animationTracker = new();
    private double _clockMs;
    private IElement? _hoveredElement;
    private IDocument? _frameDocument;
    private bool _isFrameScrollShiftable;
    private (double X, double Y) _mousePosition;
    private IDocument? _metricsDocument;
    private MutationObserver? _metricsObserver;
    private HtmlRenderer.LayoutSnapshot? _metrics;
    // the last frame and its layout (see UpdateFrame)
    private RenderedFrame? _lastFrame;
    private HtmlRenderer.LayoutSnapshot? _lastFrameLayout;
    // the changes the observer delivered on its own, which the metrics have not seen yet
    private readonly List<IMutationRecord> _pendingMutations = [];
    private const int MaxPendingMutations = 256;
    private (int Width, int Height) _metricsViewport;

    private sealed class ElementInteractionState
    {
        public double ScrollLeft { get; set; }

        public double ScrollTop { get; set; }
    }

    public InteractiveHtmlRendererState(IBrowsingContext context, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(renderDevice);

        Context = context;
        RenderDevice = renderDevice;
        _renderer = new HtmlRenderer();
    }

    private readonly HashSet<InteractiveHtmlRendererState> _subframes = [];
    private readonly HashSet<IElement> _watchedFrames = new(ReferenceEqualityComparer.Instance);
    private bool _isPreparingFrame;
    private bool _hasChangedSinceFrame;

    public event EventHandler? PaintInvalidated;

    /// <summary>
    /// Raised when a frame was laid out or brought up to date, i.e., boxes may be somewhere else.
    /// </summary>
    internal event EventHandler? FrameUpdated;

    public IBrowsingContext Context { get; }

    /// <summary>
    /// Gets the render device used for interactive measurements.
    /// </summary>
    public IRenderDevice RenderDevice { get; }

    /// <summary>
    /// Gets the currently hovered element, if any.
    /// </summary>
    public IElement? HoveredElement => _hoveredElement;

    /// <summary>
    /// Gets or sets the current mouse cursor position.
    /// </summary>
    public (double X, double Y) MousePosition
    {
        get => _mousePosition;
        set
        {
            if (Math.Abs(_mousePosition.X - value.X) < double.Epsilon &&
                Math.Abs(_mousePosition.Y - value.Y) < double.Epsilon)
            {
                return;
            }

            _mousePosition = value;
            var hoverChanged = UpdateHoveredElementFromMousePosition();

            // the hover state is what styles (and so the layout) depend on
            if (!hoverChanged)
            {
                OnPaintInvalidated(keepsMetrics: true);
            }
        }
    }

    /// <summary>
    /// Gets the horizontal scroll position for an element, clamped to the supplied maximum.
    /// </summary>
    public double GetScrollLeft(IElement element, double maxLeft)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = _elementStates.GetValue(element, static _ => new ElementInteractionState());
        state.ScrollLeft = Clamp(state.ScrollLeft, 0d, maxLeft);
        return state.ScrollLeft;
    }

    /// <summary>
    /// Sets the horizontal scroll position for an element with clamping.
    /// </summary>
    public void SetScrollLeft(IElement element, double value, double maxLeft)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = _elementStates.GetValue(element, static _ => new ElementInteractionState());
        var next = Clamp(value, 0d, maxLeft);
        if (Math.Abs(state.ScrollLeft - next) < double.Epsilon)
        {
            return;
        }

        state.ScrollLeft = next;
        OnPaintInvalidated(keepsMetrics: IsPage(element));
    }

    /// <summary>
    /// Gets the vertical scroll position for an element, clamped to the supplied maximum.
    /// </summary>
    public double GetScrollTop(IElement element, double maxTop)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = _elementStates.GetValue(element, static _ => new ElementInteractionState());
        state.ScrollTop = Clamp(state.ScrollTop, 0d, maxTop);
        return state.ScrollTop;
    }

    /// <summary>
    /// Sets the vertical scroll position for an element with clamping.
    /// </summary>
    public void SetScrollTop(IElement element, double value, double maxTop)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = _elementStates.GetValue(element, static _ => new ElementInteractionState());
        var next = Clamp(value, 0d, maxTop);
        if (Math.Abs(state.ScrollTop - next) < double.Epsilon)
        {
            return;
        }

        state.ScrollTop = next;
        OnPaintInvalidated(keepsMetrics: IsPage(element));
    }

    public void SetHoveredElement(IElement? element)
    {
        if (ReferenceEquals(_hoveredElement, element))
        {
            return;
        }

        _hoveredElement = element;
        UpdateForcedHoverChain(element);
        OnPaintInvalidated();
    }

    private bool UpdateHoveredElementFromMousePosition()
    {
        var targetDocument = Context.Active;
        IElement? nextHovered = null;

        if (targetDocument is not null)
        {
            var metrics = GetLayoutMetrics(targetDocument);
            nextHovered = HitTesting.FindTopMostElementAt(metrics, _mousePosition.X, _mousePosition.Y);
        }

        if (ReferenceEquals(_hoveredElement, nextHovered))
        {
            return false;
        }

        _hoveredElement = nextHovered;
        UpdateForcedHoverChain(nextHovered);
        OnPaintInvalidated();
        return true;
    }

    // AngleSharp.Css's `SetPseudoClass` forces `:hover` for exactly the element it is called on -
    // it deliberately does not propagate to ancestors (see AngleSharp.Css's own
    // PseudoClassForcingTests.ForcingIsExplicitAndDoesNotPropagateToAncestors), unlike how a real
    // pointer device makes every ancestor of the physically-hovered element match `:hover` too
    // (`.card:hover .title` relies on this). So this renderer has to walk the ancestor chain
    // itself and force each element individually, then clear the previous chain when the hovered
    // element changes - there is no document-wide "clear every forced :hover" API to lean on
    // instead, so this class tracks exactly which elements it forced.
    private void UpdateForcedHoverChain(IElement? nextHovered)
    {
        var previousChain = _forcedHoverChain.ToList();
        var newChain = new List<IElement>();
        var probe = nextHovered;

        while (probe is not null)
        {
            newChain.Add(probe);
            probe = probe.ParentElement;
        }

        var affectedElements = previousChain.Union(newChain).ToList();

        // Snapshot each affected element's *current* (pre-change) natural value before the
        // pseudo-class mutation below - once :hover is forced/removed, there is no way to ask
        // AngleSharp.Css what the value used to be, so a freshly-starting transition (nothing
        // already in flight for that element/property) would otherwise have no "from" value to
        // animate away from and would jump straight to the new state instead.
        _transitionTracker.CaptureNaturalValuesBeforeChange(affectedElements);

        foreach (var element in _forcedHoverChain)
        {
            element.RemovePseudoClass("hover");
        }

        _forcedHoverChain.Clear();

        var current = nextHovered;

        while (current is not null)
        {
            current.SetPseudoClass("hover");
            _forcedHoverChain.Add(current);
            current = current.ParentElement;
        }

        // Every element that either lost or gained :hover may now have a different natural
        // computed style than a moment ago - re-evaluate them all for CSS `transition`s that
        // should start animating toward that new style (the union, since an element leaving
        // :hover needs its own "fade back to resting" transition considered too, not just the
        // newly-hovered chain's "fade in").
        _transitionTracker.NotifyElementsMayHaveChanged(affectedElements);
    }


    public void AdvanceTime(TimeSpan delta)
    {
        _clockMs += delta.TotalMilliseconds;
        _transitionTracker.AdvanceTime(delta);

        foreach (var subframe in _subframes)
        {
            subframe.AdvanceTime(delta);
        }

        OnPaintInvalidated();
    }

    /// <summary>
    /// Makes what changes the document of an iframe in the page change the page.
    /// </summary>
    internal void AttachSubframe(InteractiveHtmlRendererState subframe)
    {
        if (_subframes.Add(subframe))
        {
            subframe.PaintInvalidated += (_, _) => OnPaintInvalidated();
        }
    }

    private bool HasChangedSinceFrame()
    {
        if (_metricsObserver is { } observer)
        {
            KeepMutations(observer.Flush());
        }

        return _hasChangedSinceFrame || !ReferenceEquals(Context.Active, _frameDocument) || _subframes.Any(static subframe => subframe.HasChangedSinceFrame());
    }

    /// <summary>
    /// Has the page painted again once an iframe without a document yet got one.
    /// </summary>
    internal void WatchFrameLoad(IElement element)
    {
        if (_watchedFrames.Add(element))
        {
            element.AddEventListener("load", (_, _) => OnPaintInvalidated());
        }
    }

    /// <summary>
    /// Sets the size of the viewport of the document of an iframe.
    /// </summary>
    internal void SetViewport(int width, int height)
    {
        if (RenderDevice is DefaultRenderDevice device && (device.ViewPortWidth != width || device.ViewPortHeight != height))
        {
            device.ViewPortWidth = width;
            device.ViewPortHeight = height;
            device.DeviceWidth = width;
            device.DeviceHeight = height;
        }
    }

    public TimeSpan CurrentTime => TimeSpan.FromMilliseconds(_clockMs);

    public string? GetTransitioningValue(IElement element, string property)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        return _transitionTracker.GetTransitioningValue(element, property);
    }

    public string? GetAnimatedValue(IElement element, string property)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        return _animationTracker.GetAnimatedValue(element, property, _clockMs);
    }

    /// <summary>
    /// Gets the animated value of a property, given the element's computed style.
    /// </summary>
    internal string? GetAnimatedValue(IElement element, string property, ICssStyleDeclaration computedStyle) =>
        _animationTracker.GetAnimatedValue(element, property, _clockMs, computedStyle);

    public RenderedImage PaintToPng()
    {
        var targetDocument = Context.Active;
        if (targetDocument is null)
        {
            throw new InvalidOperationException("No active document is available for painting.");
        }

        _metrics = null;

        using (_animationTracker.BeginFrame())
        {
            return _renderer.RenderToPng(targetDocument, RenderDevice);
        }
    }

    public RenderedFrame PrepareFrame(int overscan = 0)
    {
        var targetDocument = Context.Active
            ?? throw new InvalidOperationException("No active document is available for painting.");

        // Overscan is only right for pages scrolling as a whole: boxes attached to the viewport
        // would be painted relative to the surface instead. Whether a page has any is only known
        // once it is laid out, so the previous frame decides and a wrong guess is laid out again.
        if (!ReferenceEquals(_frameDocument, targetDocument))
        {
            _frameDocument = targetDocument;
            _isFrameScrollShiftable = false;
        }

        var scrollTop = targetDocument.DocumentElement is { } root ? GetScrollTop(root, double.MaxValue) : 0d;
        _metrics = null;
        _isPreparingFrame = true;
        _hasChangedSinceFrame = false;

        try
        {
        using (_animationTracker.BeginFrame())
        {
            var frame = _renderer.PrepareFrame(targetDocument, RenderDevice, scrollTop, _isFrameScrollShiftable ? overscan : 0, out var snapshot);

            if (frame.OverscanTop > 0 && !frame.IsScrollShiftable)
            {
                frame = _renderer.PrepareFrame(targetDocument, RenderDevice, scrollTop, 0, out snapshot);
            }

            // the frame's layout answers the geometry reads until something changes it
            WatchMutations(targetDocument);
            _metricsObserver!.Flush();
            _pendingMutations.Clear();
            _metrics = snapshot;
            _metricsViewport = (RenderDevice.ViewPortWidth, RenderDevice.ViewPortHeight);
            _lastFrame = frame;
            _lastFrameLayout = snapshot;
            _isFrameScrollShiftable = frame.IsScrollShiftable;
            frame.IsAnimating = _animationTracker.IsAnimating || _transitionTracker.HasRunningTransitions || snapshot.Subframes.Values.Any(static entry => entry.Frame?.IsAnimating == true);
            FrameUpdated?.Invoke(this, EventArgs.Empty);
            return ShowSelection(frame);
        }
        }
        finally
        {
            _isPreparingFrame = false;
        }
    }

    /// <summary>
    /// Gets the layout metrics of the given document, laid out again only when something may
    /// have changed them. Script reads geometry (getBoundingClientRect, offsetTop, scrollHeight)
    /// over and over, often for many elements in a row, and every capture is a full layout.
    /// </summary>
    /// <remarks>
    /// The metrics follow the mutations of the document (taken synchronously, so a script
    /// changing a class and reading geometry right after sees the change): a change confined to
    /// an out-of-flow box lays out only that box again (see <see cref="HtmlRenderer.LayoutSnapshot"/>),
    /// anything else all of the page. They are dropped by anything that invalidates the paint
    /// here (hovering, time; not scrolling or moving the pointer) and by a changed viewport. A
    /// frame being prepared (which happens after resources such as style sheets, images, or
    /// fonts arrived) replaces them with its own layout.
    /// </remarks>
    internal IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> GetLayoutMetrics(IDocument document) =>
        GetLayoutSnapshot(document).Metrics;

    /// <summary>
    /// Gets the layout of the given document (see <see cref="GetLayoutMetrics"/>), which also
    /// maps its boxes into the viewport.
    /// </summary>
    internal HtmlRenderer.LayoutSnapshot GetLayoutSnapshot(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var viewport = (RenderDevice.ViewPortWidth, RenderDevice.ViewPortHeight);

        WatchMutations(document);
        KeepMutations(_metricsObserver!.Flush());
        var metrics = _metrics;

        if (_pendingMutations.Count > 0 && metrics is not null && (_metricsViewport != viewport || !metrics.TryUpdate(_pendingMutations)))
        {
            metrics = null;
        }

        if (metrics is null || _metricsViewport != viewport)
        {
            metrics = _renderer.CaptureLayout(document, RenderDevice);
            _metricsViewport = viewport;
        }

        // the changes the layout made itself (see PrepareDocumentForRendering) are in already
        _metricsObserver.Flush();
        _pendingMutations.Clear();
        _metrics = metrics;
        return metrics;
    }

    public RenderedFrame? UpdateFrame()
    {
        var frame = UpdateLastFrame();

        if (frame is not null)
        {
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        }

        return frame;
    }

    private RenderedFrame? UpdateLastFrame()
    {
        var document = Context.Active;

        // what an iframe shows is in the frame, and only a new one has it change
        if (_subframes.Any(static subframe => subframe.HasChangedSinceFrame()))
        {
            return null;
        }

        if (document is null || _lastFrame is null || !ReferenceEquals(document, _metricsDocument) || _metricsObserver is null ||
            _metricsViewport != (RenderDevice.ViewPortWidth, RenderDevice.ViewPortHeight))
        {
            return null;
        }

        KeepMutations(_metricsObserver.Flush());

        // only the frame's own layout (kept up to date since) knows where its boxes are painted
        if (_metrics is not { } metrics || !ReferenceEquals(metrics, _lastFrameLayout))
        {
            return null;
        }

        if (_pendingMutations.Count > 0 && !metrics.TryUpdate(_pendingMutations))
        {
            _metrics = null;
            _pendingMutations.Clear();
            return null;
        }

        _pendingMutations.Clear();
        _lastFrame = _renderer.UpdateFrame(document, _lastFrame, metrics);
        return _lastFrame is { } updated ? ShowSelection(updated) : null;
    }

    private void WatchMutations(IDocument document)
    {
        if (!ReferenceEquals(_metricsDocument, document))
        {
            _metricsObserver?.Disconnect();
            _metricsObserver = new MutationObserver((records, _) => KeepMutations(records));
            _metricsObserver.Connect(document, childList: true, subtree: true, attributes: true, characterData: true, attributeOldValue: true);
            _metricsDocument = document;
            _metrics = null;
        }
    }

    // Many changes before the next read are cheaper laid out all at once, and kept around
    // without one they would only pile up.
    private void KeepMutations(IEnumerable<IMutationRecord> records)
    {
        // the document of an iframe has nobody else watching it
        if (Context.Parent is not null && !_isPreparingFrame && records.Any())
        {
            _hasChangedSinceFrame = true;
            OnPaintInvalidated(keepsMetrics: true);
        }

        if (_metrics is not null)
        {
            _pendingMutations.AddRange(records);

            if (_pendingMutations.Count > MaxPendingMutations)
            {
                _metrics = null;
                _pendingMutations.Clear();
            }
        }
    }

    // the page scrolls in the viewport, other scroll containers move their content in the layout
    private static bool IsPage(IElement element) => ReferenceEquals(element, element.Owner?.DocumentElement);

    // the metrics are laid out unscrolled and only depend on the hovered element, not on where
    // the pointer is (see GetLayoutMetrics), so scrolling the page and moving the pointer keep them
    private void OnPaintInvalidated(bool keepsMetrics = false)
    {
        if (!keepsMetrics)
        {
            _metrics = null;
        }

        PaintInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}
