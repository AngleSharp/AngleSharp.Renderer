namespace AngleSharp.Renderer.Controls;

using AngleSharp.Renderer.Rendering;

/// <summary>
/// The built-in control of `color` inputs: a swatch of the value filling the content box, whose
/// picker is a <see cref="ColorPicker"/>. Derive from it to change what is shown.
/// </summary>
public class ColorInputControl : IInputControl
{
    /// <inheritdoc />
    /// <remarks>50 by 27 pixels as a whole, as in browsers, whatever its font, border and padding are.</remarks>
    public virtual InputControlSize? GetIntrinsicSize(InputControlSizeContext context) =>
        new InputControlSize(50f, 27f, InputControlSizeBox.BorderBox);

    /// <inheritdoc />
    public virtual void Paint(InputControlPaintContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // the swatch ignores `background-color`, as in browsers; only the frame around it (border,
        // padding, size) is up to the style sheets
        var color = HtmlRenderer.ParseColor(context.Value, RenderColor.Black);
        context.DisplayList.FillRect(context.ContentBox, color);
    }

    /// <inheritdoc />
    public virtual IInputPicker? CreatePicker(InputPickerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ColorPicker(context);
    }
}
