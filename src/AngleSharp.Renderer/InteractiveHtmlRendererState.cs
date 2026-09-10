using AngleSharp.Css;
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
    private (double X, double Y) _mousePosition;

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
                PaintInvalidated?.Invoke(this, EventArgs.Empty);
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
        PaintInvalidated?.Invoke(this, EventArgs.Empty);
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
        PaintInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private bool UpdateHoveredElementFromMousePosition()
    {
        var targetDocument = Context.Active;
        IElement? nextHovered = null;

        if (targetDocument is not null)
        {
            var metrics = HtmlRenderer.CaptureLayoutMetrics(targetDocument, RenderDevice);
            nextHovered = FindTopMostElementAt(metrics, _mousePosition.X, _mousePosition.Y);
        }

        if (ReferenceEquals(_hoveredElement, nextHovered))
        {
            return false;
        }

        _hoveredElement = nextHovered;
        UpdateForcedHoverChain(nextHovered);
        PaintInvalidated?.Invoke(this, EventArgs.Empty);
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

    private static IElement? FindTopMostElementAt(IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, double x, double y)
    {
        return metrics
            .Where(pair => Contains(pair.Value, x, y))
            .OrderByDescending(pair => GetDepth(pair.Key))
            .ThenBy(pair => Math.Max(0f, pair.Value.BorderBoxWidth) * Math.Max(0f, pair.Value.BorderBoxHeight))
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private static bool Contains(HtmlRenderer.ElementLayoutMetrics metrics, double x, double y)
    {
        var left = metrics.BorderBoxX;
        var top = metrics.BorderBoxY;
        var right = metrics.BorderBoxX + metrics.BorderBoxWidth;
        var bottom = metrics.BorderBoxY + metrics.BorderBoxHeight;

        return x >= left && x <= right && y >= top && y <= bottom;
    }

    private static int GetDepth(IElement element)
    {
        var depth = 0;
        var current = element.ParentElement;

        while (current is not null)
        {
            depth++;
            current = current.ParentElement;
        }

        return depth;
    }

    public void AdvanceTime(TimeSpan delta)
    {
        _clockMs += delta.TotalMilliseconds;
        _transitionTracker.AdvanceTime(delta);
        PaintInvalidated?.Invoke(this, EventArgs.Empty);
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

    public RenderedImage PaintToPng()
    {
        var targetDocument = Context.Active;
        if (targetDocument is null)
        {
            throw new InvalidOperationException("No active document is available for painting.");
        }

        return _renderer.RenderToPng(targetDocument, RenderDevice);
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
