namespace AngleSharp.Renderer.Controls;

using AngleSharp.Html.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// What an <see cref="IInputControl"/> paints with: the element, the display list and the content
/// box to paint into (in the coordinates of the display list), and the text of the element as
/// styled, which is measured and painted exactly as the renderer paints text.
/// </summary>
public sealed class InputControlPaintContext
{
    private readonly Func<string, float> _measure;
    private readonly Action<string, float, float> _draw;

    internal InputControlPaintContext(
        IHtmlInputElement input,
        DisplayList displayList,
        RenderRect contentBox,
        RenderFont font,
        RenderColor textColor,
        RenderColor accentColor,
        float textBaseline,
        Func<string, float> measure,
        Action<string, float, float> draw)
    {
        Input = input;
        DisplayList = displayList;
        ContentBox = contentBox;
        Font = font;
        TextColor = textColor;
        AccentColor = accentColor;
        TextBaseline = textBaseline;
        _measure = measure;
        _draw = draw;
    }

    /// <summary>Gets the input element.</summary>
    public IHtmlInputElement Input { get; }

    /// <summary>Gets the type of the input, in lowercase.</summary>
    public string Type => Input.Type?.Trim().ToLowerInvariant() ?? "text";

    /// <summary>Gets the current value of the input (not its `value` attribute).</summary>
    public string Value => Input.Value ?? string.Empty;

    /// <summary>Gets the display list to paint into.</summary>
    public DisplayList DisplayList { get; }

    /// <summary>Gets the content box of the input, inside of its border and padding.</summary>
    public RenderRect ContentBox { get; }

    /// <summary>Gets the font of the input's text.</summary>
    public RenderFont Font { get; }

    /// <summary>Gets the color of the input's text.</summary>
    public RenderColor TextColor { get; }

    /// <summary>Gets the `accent-color` of the input, or the default accent if it is `auto`.</summary>
    public RenderColor AccentColor { get; }

    /// <summary>Gets the baseline of a line of text centered in the content box.</summary>
    public float TextBaseline { get; }

    /// <summary>
    /// Measures text in the style of the input.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Its width in CSS pixels.</returns>
    public float MeasureText(string text) => string.IsNullOrEmpty(text) ? 0f : _measure(text);

    /// <summary>
    /// Paints text in the style of the input (its color, font, `letter-spacing`, `text-shadow`,
    /// ...) without decorations.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="x">Where it starts.</param>
    /// <param name="baseline">Its baseline, e.g. <see cref="TextBaseline"/>.</param>
    public void DrawText(string text, float x, float baseline)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _draw(text, x, baseline);
        }
    }
}
