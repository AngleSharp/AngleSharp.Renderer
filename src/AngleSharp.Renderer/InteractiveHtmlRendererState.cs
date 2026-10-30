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
internal sealed class InteractiveHtmlRendererState : IDomHarness
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
    private IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics>? _metrics;
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

    public event EventHandler? PaintInvalidated;

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

            if (!hoverChanged)
            {
                OnPaintInvalidated();
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
        OnPaintInvalidated();
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
        OnPaintInvalidated();
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
        OnPaintInvalidated();
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

        using (_animationTracker.BeginFrame())
        {
            var frame = _renderer.PrepareFrame(targetDocument, RenderDevice, scrollTop, _isFrameScrollShiftable ? overscan : 0);

            if (frame.OverscanTop > 0 && !frame.IsScrollShiftable)
            {
                frame = _renderer.PrepareFrame(targetDocument, RenderDevice, scrollTop, 0);
            }

            _isFrameScrollShiftable = frame.IsScrollShiftable;
            frame.IsAnimating = _animationTracker.IsAnimating || _transitionTracker.HasRunningTransitions;
            return frame;
        }
    }

    /// <summary>
    /// Gets the layout metrics of the given document, laid out again only when something may
    /// have changed them. Script reads geometry (getBoundingClientRect, offsetTop, scrollHeight)
    /// over and over, often for many elements in a row, and every capture is a full layout.
    /// </summary>
    /// <remarks>
    /// The metrics are dropped by a mutation of the document (taken synchronously, so a script
    /// changing a class and reading geometry right after sees the change), by anything that
    /// invalidates the paint here (scrolling, hovering, time), by a frame being prepared (which
    /// happens after resources such as style sheets, images, or fonts arrived), and by a
    /// changed viewport.
    /// </remarks>
    internal IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> GetLayoutMetrics(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var viewport = (RenderDevice.ViewPortWidth, RenderDevice.ViewPortHeight);

        if (!ReferenceEquals(_metricsDocument, document))
        {
            _metricsObserver?.Disconnect();
            _metricsObserver = new MutationObserver((_, _) => _metrics = null);
            _metricsObserver.Connect(document, childList: true, subtree: true, attributes: true, characterData: true);
            _metricsDocument = document;
            _metrics = null;
        }
        else if (_metricsObserver!.Flush().ToArray().Length > 0)
        {
            _metrics = null;
        }

        var metrics = _metrics;

        if (metrics is null || _metricsViewport != viewport)
        {
            metrics = HtmlRenderer.CaptureLayoutMetrics(document, RenderDevice);
            _metrics = metrics;
            _metricsViewport = viewport;
        }

        return metrics;
    }

    private void OnPaintInvalidated()
    {
        _metrics = null;
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
