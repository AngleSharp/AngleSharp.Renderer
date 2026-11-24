namespace AngleSharp.Renderer.Controls;

/// <summary>
/// How an input of one type (e.g. `color` or `date`) is shown: what is painted inside of its box,
/// and the picker it opens for choosing a value. The box itself (border, padding, background,
/// size) comes from the style sheets as for any element; a control only paints its content.
/// </summary>
/// <remarks>
/// The controls in use come from the <see cref="IInputControlProvider"/> of the browsing context
/// (see <see cref="InputControlConfigurationExtensions.WithInputControl"/>), else from
/// <see cref="InputControlRegistry.Default"/>. Painting happens during layout, so it must only
/// depend on the state of the element (its value, attributes and focus) and the given context.
/// </remarks>
public interface IInputControl
{
    /// <summary>
    /// Gets the size of the input when its `width` or `height` is `auto`. Called during layout,
    /// so, as painting, it must only depend on the state of the element.
    /// </summary>
    /// <param name="context">The element and how its text looks.</param>
    /// <returns>The size, or null (also for either side) for the built-in size of the type.</returns>
    InputControlSize? GetIntrinsicSize(InputControlSizeContext context) => null;

    /// <summary>
    /// Paints the content of the control into its content box.
    /// </summary>
    /// <param name="context">The element, where to paint it and how its text looks.</param>
    void Paint(InputControlPaintContext context);

    /// <summary>
    /// Creates the picker of the control, e.g. a calendar or a color picker, which a host shows on
    /// top of a frame and forwards the pointer and the keyboard to.
    /// </summary>
    /// <param name="context">The element, where it is in the viewport and how a picker looks.</param>
    /// <returns>The picker, or null if the control has none.</returns>
    IInputPicker? CreatePicker(InputPickerContext context);
}
