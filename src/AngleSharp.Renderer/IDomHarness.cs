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
    /// Sets the hovered element directly, e.g., found in a frame's <see cref="RenderedFrame.HitTestMap"/>,
    /// which spares the layout setting <see cref="MousePosition"/> runs to hit test.
    /// </summary>
    /// <param name="element">The element below the pointer, if any.</param>
    void SetHoveredElement(IElement? element);

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
    /// Advances the virtual clock CSS `transition`s and `animation`s are measured against. There is
    /// no real-time timer anywhere in this renderer - a caller drives animation forward explicitly
    /// by calling this before each <see cref="PaintToPng"/>, the same way it already drives mouse
    /// position and scroll offsets.
    /// </summary>
    void AdvanceTime(TimeSpan delta);

    /// <summary>
    /// Gets the current position of the virtual clock <see cref="AdvanceTime"/> advances - the same
    /// clock CSS `transition`/`animation` are measured against, and what a focused text input's
    /// caret blink is measured against too.
    /// </summary>
    TimeSpan CurrentTime { get; }

    /// <summary>
    /// Gets the interpolated value currently in effect for <paramref name="property"/> on
    /// <paramref name="element"/> if a CSS `transition` is actively running for it, or null if none
    /// is active - in which case the property's ordinary computed value applies, unaffected.
    /// </summary>
    string? GetTransitioningValue(IElement element, string property);

    /// <summary>
    /// Gets the animated value currently in effect for <paramref name="property"/> on
    /// <paramref name="element"/> if a CSS `animation` is actively running for it against the
    /// current virtual clock time, or null if none is active - in which case the property's
    /// ordinary computed value applies, unaffected. Unlike <see cref="GetTransitioningValue"/>, an
    /// animation starts the first moment this is called for a given (element, animation-name) pair
    /// rather than needing some other triggering state change.
    /// </summary>
    string? GetAnimatedValue(IElement element, string property);

    /// <summary>
    /// Renders the active document to PNG using the harness-bound render device.
    /// </summary>
    RenderedImage PaintToPng();

    /// <summary>
    /// Lays out the active document into a frame at the current scroll position, which comes
    /// with its hit-test map and scroll extent. Rasterizing it (<see cref="RenderedFrame.Rasterize"/>)
    /// does not touch the DOM, so it may happen on another thread.
    /// </summary>
    /// <param name="overscan">
    /// The rows to paint above and below the viewport, such that the frame can be scrolled
    /// until the next one is ready. Only used for pages without fixed or sticky boxes.
    /// </param>
    /// <returns>The prepared frame.</returns>
    RenderedFrame PrepareFrame(int overscan = 0);

    /// <summary>
    /// Brings the last frame (<see cref="PrepareFrame"/>) up to date with the changes of the
    /// active document since, without laying out all of it: when they were confined to boxes
    /// out of the flow, those are laid out again and replace their commands (e.g. a menu changing
    /// its items), and when the boxes were not rendered then and are not now (e.g. a script
    /// showing, measuring and hiding a dropdown again), the frame stays as it is.
    /// </summary>
    /// <returns>The frame showing the document now, or null if it needs <see cref="PrepareFrame"/>.</returns>
    RenderedFrame? UpdateFrame() => null;

    /// <summary>
    /// Gets or sets if the text of the document can be selected, which is the case by default.
    /// Without it, layouts do not record where their text comes from, nothing can be selected
    /// with the pointer, and no selection is painted.
    /// </summary>
    bool IsTextSelectionEnabled { get => false; set { } }

    /// <summary>
    /// Gets if the pointer is selecting text, i.e., since <see cref="BeginTextSelection"/>.
    /// </summary>
    bool IsSelectingText => false;

    /// <summary>
    /// Starts selecting text as a press of the primary pointer button does, which a host calls
    /// unless `mousedown` was canceled. Fires `selectstart`, and does not select when canceled.
    /// </summary>
    /// <param name="x">The horizontal position in viewport coordinates.</param>
    /// <param name="y">The vertical position in viewport coordinates.</param>
    /// <param name="clickCount">The number of successive presses: 1 places the caret, 2 selects a word, 3 a paragraph.</param>
    /// <param name="extend">If the selection is to extend to the position (Shift held).</param>
    /// <returns>True if text is being selected now, false if there is no selectable text there.</returns>
    bool BeginTextSelection(double x, double y, int clickCount = 1, bool extend = false) => false;

    /// <summary>
    /// Extends the selection to the position, while the pointer moves with its button held.
    /// </summary>
    /// <param name="x">The horizontal position in viewport coordinates.</param>
    /// <param name="y">The vertical position in viewport coordinates.</param>
    void ExtendTextSelection(double x, double y) { }

    /// <summary>
    /// Ends selecting text, as releasing the pointer button does. The selection stays.
    /// </summary>
    void EndTextSelection() { }

    /// <summary>
    /// Selects all text of the active document, e.g., for Ctrl+A.
    /// </summary>
    void SelectAllText() { }

    /// <summary>
    /// Removes the selection of the active document.
    /// </summary>
    void ClearTextSelection() { }

    /// <summary>
    /// Gets the selected text as it is shown, e.g., for copying it to the clipboard.
    /// </summary>
    string SelectedText => string.Empty;
}
