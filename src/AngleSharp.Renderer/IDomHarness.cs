using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer;

/// <summary>
/// Represents an interactive harness bound to a browsing context.
/// </summary>
public interface IDomHarness
{
    /// <summary>
    /// Raised whenever interaction state changes require repainting.
    /// </summary>
    event EventHandler? PaintInvalidated;

    /// <summary>
    /// Gets the browsing context associated with this harness.
    /// </summary>
    IBrowsingContext Context { get; }

    /// <summary>
    /// Gets the render device associated with this harness.
    /// </summary>
    IRenderDevice RenderDevice { get; }

    /// <summary>
    /// Gets the currently hovered element (derived from the mouse cursor position).
    /// </summary>
    IElement? HoveredElement { get; }

    /// <summary>
    /// Gets or sets the current mouse cursor position in viewport coordinates.
    /// </summary>
    (double X, double Y) MousePosition { get; set; }

    /// <summary>
    /// Gets the horizontal scroll offset for the given element.
    /// </summary>
    double GetScrollLeft(IElement element, double maxLeft);

    /// <summary>
    /// Sets the horizontal scroll offset for the given element.
    /// </summary>
    void SetScrollLeft(IElement element, double value, double maxLeft);

    /// <summary>
    /// Gets the vertical scroll offset for the given element.
    /// </summary>
    double GetScrollTop(IElement element, double maxTop);

    /// <summary>
    /// Sets the vertical scroll offset for the given element.
    /// </summary>
    void SetScrollTop(IElement element, double value, double maxTop);

    /// <summary>
    /// Renders the active document to PNG using the harness-bound render device.
    /// </summary>
    RenderedImage PaintToPng();
}
