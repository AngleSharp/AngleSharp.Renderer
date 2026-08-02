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
        PaintInvalidated?.Invoke(this, EventArgs.Empty);
        return true;
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
