namespace AngleSharp.Renderer.Controls;

using AngleSharp.Html.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Which box an <see cref="InputControlSize"/> sizes.
/// </summary>
public enum InputControlSizeBox
{
    /// <summary>
    /// The content box: padding and border are added (e.g. a text field, whose line keeps its
    /// height whatever its padding is).
    /// </summary>
    ContentBox,

    /// <summary>
    /// The border box: padding and border are taken from it (e.g. the swatch of a color input,
    /// which is as large whatever its padding is).
    /// </summary>
    BorderBox,
}

/// <summary>
/// The size of an input when its `width` or `height` is `auto`, which is what its control
/// paints in (see <see cref="IInputControl.GetIntrinsicSize"/>).
/// </summary>
/// <param name="Width">The width in CSS pixels, or null for the built-in width.</param>
/// <param name="Height">The height in CSS pixels, or null for the built-in height.</param>
/// <param name="Box">The box the width and height are of.</param>
public readonly record struct InputControlSize(float? Width, float? Height, InputControlSizeBox Box = InputControlSizeBox.ContentBox);

/// <summary>
/// What an <see cref="IInputControl"/> sizes an input with: the element, and its text as styled.
/// </summary>
public sealed class InputControlSizeContext
{
    private readonly Func<string, float> _measure;

    internal InputControlSizeContext(IHtmlInputElement input, RenderFont font, float lineHeight, Func<string, float> measure)
    {
        Input = input;
        Font = font;
        LineHeight = lineHeight;
        _measure = measure;
    }

    /// <summary>Gets the input element.</summary>
    public IHtmlInputElement Input { get; }

    /// <summary>Gets the type of the input, in lowercase.</summary>
    public string Type => Input.Type?.Trim().ToLowerInvariant() ?? "text";

    /// <summary>Gets the current value of the input (not its `value` attribute).</summary>
    public string Value => Input.Value ?? string.Empty;

    /// <summary>Gets the font of the input's text.</summary>
    public RenderFont Font { get; }

    /// <summary>Gets the height of a line of the input's text (its `line-height`).</summary>
    public float LineHeight { get; }

    /// <summary>
    /// Measures text in the style of the input, as <see cref="InputControlPaintContext.MeasureText"/> does.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Its width in CSS pixels.</returns>
    public float MeasureText(string text) => string.IsNullOrEmpty(text) ? 0f : _measure(text);
}
