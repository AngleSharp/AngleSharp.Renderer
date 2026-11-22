namespace AngleSharp.Renderer.Controls;

using AngleSharp.Html.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// What a host has to do after forwarding something to an <see cref="IInputPicker"/>.
/// </summary>
public enum InputPickerResult
{
    /// <summary>Nothing changed.</summary>
    None,

    /// <summary>The picker looks different: show its <see cref="IInputPicker.Render"/> again.</summary>
    Repaint,

    /// <summary>The picker is done (its value was chosen or it was dismissed) and is to be hidden.</summary>
    Close,
}

/// <summary>
/// The picker of an input (a calendar, a clock, a color picker, ...). Like the list of a dropdown
/// (<see cref="SelectPopup"/>) it isn't part of the layout: a host shows <see cref="Render"/> on top
/// of a frame (<see cref="RenderedFrame.WithOverlay"/>) and forwards the pointer and the keyboard,
/// in viewport coordinates, while it is open. Choosing sets the value of the input and fires
/// `input`; closing fires `change` if the value is not the one it had when the picker opened.
/// </summary>
public interface IInputPicker
{
    /// <summary>Gets the input the picker is for.</summary>
    IHtmlInputElement Input { get; }

    /// <summary>Gets where the picker is in the viewport.</summary>
    RenderRect Bounds { get; }

    /// <summary>Gets if the picker is still open.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Gets if the position in the viewport is on the picker, e.g., to close it on a press
    /// anywhere else.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>True if the position is on the picker.</returns>
    bool Contains(double x, double y);

    /// <summary>
    /// Paints the picker as it currently is, in viewport coordinates.
    /// </summary>
    /// <returns>What to show on top of a frame.</returns>
    DisplayList Render();

    /// <summary>
    /// Forwards a press of the primary button.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>What the host has to do.</returns>
    InputPickerResult PointerDown(double x, double y);

    /// <summary>
    /// Forwards a move of the pointer, e.g. dragging the hue of a color.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>What the host has to do.</returns>
    InputPickerResult PointerMove(double x, double y);

    /// <summary>
    /// Forwards the release of the primary button.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>What the host has to do.</returns>
    InputPickerResult PointerUp(double x, double y);

    /// <summary>
    /// Forwards a turn of the wheel.
    /// </summary>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <param name="deltaY">How far to scroll down in CSS pixels (negative: up).</param>
    /// <returns>What the host has to do.</returns>
    InputPickerResult Wheel(double x, double y, double deltaY);

    /// <summary>
    /// Forwards a key press.
    /// </summary>
    /// <param name="key">The key as named by `KeyboardEvent.key`, e.g. "ArrowLeft", "Enter" or "Escape".</param>
    /// <returns>What the host has to do.</returns>
    InputPickerResult KeyDown(string key);

    /// <summary>
    /// Closes the picker, e.g. on a press outside of it: keeps the chosen value, or (cancel) brings
    /// back the one it had when the picker opened.
    /// </summary>
    /// <param name="cancel">True to discard what was chosen.</param>
    void Close(bool cancel = false);
}
