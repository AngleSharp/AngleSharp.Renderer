namespace AngleSharp.Renderer.Controls;

using AngleSharp.Dom.Events;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// A base for pickers: keeps the value the input had when it opened, sets values firing `input`,
/// fires `change` on closing, and paints text in the font of the input.
/// </summary>
public abstract class InputPicker : IInputPicker
{
    private readonly string _initialValue;

    /// <summary>
    /// Creates a picker for the input of the context.
    /// </summary>
    /// <param name="context">The context of the picker.</param>
    protected InputPicker(InputPickerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        _initialValue = context.Input.Value ?? string.Empty;
    }

    /// <summary>Gets the context the picker was created with.</summary>
    public InputPickerContext Context { get; }

    /// <inheritdoc />
    public IHtmlInputElement Input => Context.Input;

    /// <inheritdoc />
    public RenderRect Bounds { get; protected set; }

    /// <inheritdoc />
    public bool IsOpen { get; private set; } = true;

    /// <summary>Gets the colors of the picker.</summary>
    protected InputPickerPalette Palette => Context.Palette;

    /// <summary>Gets the font size of the picker's text.</summary>
    protected float FontSize => Context.Font.FontSize;

    /// <inheritdoc />
    public virtual bool Contains(double x, double y) =>
        IsOpen && x >= Bounds.X && x < Bounds.X + Bounds.Width && y >= Bounds.Y && y < Bounds.Y + Bounds.Height;

    /// <inheritdoc />
    public DisplayList Render()
    {
        var displayList = new DisplayList { Fonts = Context.Font.Faces ?? FontFaceSet.Empty };

        if (IsOpen)
        {
            Paint(displayList);
        }

        return displayList;
    }

    /// <summary>
    /// Paints the picker in viewport coordinates.
    /// </summary>
    /// <param name="displayList">The list to paint into.</param>
    protected abstract void Paint(DisplayList displayList);

    /// <inheritdoc />
    public virtual InputPickerResult PointerDown(double x, double y) => InputPickerResult.None;

    /// <inheritdoc />
    public virtual InputPickerResult PointerMove(double x, double y) => InputPickerResult.None;

    /// <inheritdoc />
    public virtual InputPickerResult PointerUp(double x, double y) => InputPickerResult.None;

    /// <inheritdoc />
    public virtual InputPickerResult Wheel(double x, double y, double deltaY) => InputPickerResult.None;

    /// <inheritdoc />
    public virtual InputPickerResult KeyDown(string key)
    {
        switch (key)
        {
            case "Escape":
                Close(cancel: true);
                return InputPickerResult.Close;
            case "Enter":
                Close();
                return InputPickerResult.Close;
            default:
                return InputPickerResult.None;
        }
    }

    /// <inheritdoc />
    public void Close(bool cancel = false)
    {
        if (!IsOpen)
        {
            return;
        }

        if (cancel)
        {
            SetValue(_initialValue);
        }

        IsOpen = false;

        if (!string.Equals(Input.Value ?? string.Empty, _initialValue, StringComparison.Ordinal))
        {
            Input.Dispatch(new Event("change", bubbles: true, cancelable: false));
        }
    }

    /// <summary>
    /// Sets the value of the input as chosen by the user: fires `input` if it changed.
    /// </summary>
    /// <param name="value">The value, in the format of the input's type.</param>
    /// <returns>True if the value changed.</returns>
    protected bool SetValue(string value)
    {
        if (string.Equals(Input.Value ?? string.Empty, value, StringComparison.Ordinal))
        {
            return false;
        }

        Input.Value = value;
        Input.Dispatch(new Event("input", bubbles: true, cancelable: false));
        return true;
    }

    /// <summary>
    /// Chooses a value and closes the picker.
    /// </summary>
    /// <param name="value">The value, in the format of the input's type.</param>
    /// <returns><see cref="InputPickerResult.Close"/>.</returns>
    protected InputPickerResult Choose(string value)
    {
        SetValue(value);
        Close();
        return InputPickerResult.Close;
    }

    /// <summary>
    /// Measures text in the font of the picker.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="fontWeight">The weight, or that of the input's font if not given.</param>
    /// <returns>The width in CSS pixels.</returns>
    protected float MeasureText(string text, float? fontWeight = null) =>
        string.IsNullOrEmpty(text) ? 0f : Context.TextMeasurer.MeasureWidth(text, Context.Font with { FontWeight = fontWeight ?? Context.Font.FontWeight });

    /// <summary>
    /// Paints text in the font of the picker, centered in a box.
    /// </summary>
    /// <param name="displayList">The list to paint into.</param>
    /// <param name="text">The text.</param>
    /// <param name="box">The box to center the text in.</param>
    /// <param name="color">The color.</param>
    /// <param name="fontWeight">The weight, or that of the input's font if not given.</param>
    protected void DrawCenteredText(DisplayList displayList, string text, RenderRect box, RenderColor color, float? fontWeight = null)
    {
        var width = MeasureText(text, fontWeight);
        DrawText(displayList, text, box.X + ((box.Width - width) / 2f), GetBaseline(box), color, fontWeight);
    }

    /// <summary>
    /// Paints text in the font of the picker.
    /// </summary>
    /// <param name="displayList">The list to paint into.</param>
    /// <param name="text">The text.</param>
    /// <param name="x">Where it starts.</param>
    /// <param name="baseline">Its baseline.</param>
    /// <param name="color">The color.</param>
    /// <param name="fontWeight">The weight, or that of the input's font if not given.</param>
    protected void DrawText(DisplayList displayList, string text, float x, float baseline, RenderColor color, float? fontWeight = null)
    {
        ArgumentNullException.ThrowIfNull(displayList);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var font = Context.Font;
        displayList.DrawText(text, x, baseline, color, font.FontSize, font.FontFamily, fontWeight ?? font.FontWeight, font.IsItalic, fontStretch: font.FontStretch, language: font.Language);
    }

    /// <summary>
    /// Gets the baseline of a line of text centered in a box.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <returns>The baseline.</returns>
    protected float GetBaseline(RenderRect box) => HtmlRenderer.ResolveControlTextBaseline(FontSize, box.Y, box.Height);

    /// <summary>
    /// Gets if a box contains a position.
    /// </summary>
    /// <param name="box">The box.</param>
    /// <param name="x">The horizontal position.</param>
    /// <param name="y">The vertical position.</param>
    /// <returns>True if it is inside.</returns>
    protected static bool Hit(RenderRect box, double x, double y) =>
        x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;

    /// <summary>
    /// Gets the corner radii of a rounded rectangle.
    /// </summary>
    /// <param name="radius">The radius of every corner.</param>
    /// <returns>The radii.</returns>
    protected static RenderCornerRadii Round(float radius) =>
        new(radius, radius, radius, radius, radius, radius, radius, radius);

    /// <summary>
    /// Paints the frame of a picker: a rounded, bordered rectangle of the background color.
    /// </summary>
    /// <param name="displayList">The list to paint into.</param>
    protected void PaintFrame(DisplayList displayList)
    {
        ArgumentNullException.ThrowIfNull(displayList);
        displayList.FillRect(Bounds, Palette.Background, Round(4f));
        displayList.StrokeRoundedRect(Bounds, Palette.Border, 1f, Round(4f));
    }
}
