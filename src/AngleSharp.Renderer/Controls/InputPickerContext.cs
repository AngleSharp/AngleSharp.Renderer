namespace AngleSharp.Renderer.Controls;

using AngleSharp.Html.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// The colors of a picker, those of the color scheme its input uses.
/// </summary>
/// <param name="Background">The background of the picker.</param>
/// <param name="Text">The text.</param>
/// <param name="MutedText">Text of what can't be chosen, or of what is outside the shown period.</param>
/// <param name="Border">The border of the picker and of its parts.</param>
/// <param name="Hover">The background of a part to choose, e.g. a button.</param>
/// <param name="Accent">The background of what is chosen.</param>
/// <param name="AccentText">Text on <paramref name="Accent"/>.</param>
public sealed record InputPickerPalette(
    RenderColor Background,
    RenderColor Text,
    RenderColor MutedText,
    RenderColor Border,
    RenderColor Hover,
    RenderColor Accent,
    RenderColor AccentText)
{
    /// <summary>Gets the colors of the light color scheme.</summary>
    public static InputPickerPalette Light { get; } = new(
        RenderColor.White,
        RenderColor.Black,
        new RenderColor(118, 118, 118),
        new RenderColor(118, 118, 118),
        new RenderColor(233, 233, 237),
        new RenderColor(0, 117, 255),
        RenderColor.White);

    /// <summary>Gets the colors of the dark color scheme.</summary>
    public static InputPickerPalette Dark { get; } = new(
        new RenderColor(59, 59, 59),
        RenderColor.White,
        new RenderColor(170, 170, 170),
        new RenderColor(133, 133, 133),
        new RenderColor(82, 82, 94),
        new RenderColor(153, 200, 255),
        RenderColor.Black);
}

/// <summary>
/// What an <see cref="IInputControl"/> creates its picker with: the input, where it is in the
/// viewport, and how a picker of it looks.
/// </summary>
public sealed class InputPickerContext
{
    /// <summary>
    /// Creates a context, e.g. for testing a picker.
    /// </summary>
    /// <param name="input">The input element.</param>
    /// <param name="anchor">The border box of the input in the viewport.</param>
    /// <param name="viewportWidth">The width of the viewport the picker opens in.</param>
    /// <param name="viewportHeight">The height of the viewport the picker opens in.</param>
    /// <param name="font">The font of the picker's text.</param>
    /// <param name="palette">The colors of the picker.</param>
    /// <param name="textMeasurer">Measures the picker's text, as it is painted.</param>
    /// <param name="now">The current local time, e.g. for marking today.</param>
    public InputPickerContext(
        IHtmlInputElement input,
        RenderRect anchor,
        float viewportWidth,
        float viewportHeight,
        RenderFont font,
        InputPickerPalette palette,
        ITextMeasurer textMeasurer,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(textMeasurer);
        Input = input;
        Anchor = anchor;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        Font = font;
        Palette = palette;
        TextMeasurer = textMeasurer;
        Now = now;
    }

    /// <summary>Gets the input element.</summary>
    public IHtmlInputElement Input { get; }

    /// <summary>Gets the type of the input, in lowercase.</summary>
    public string Type => Input.Type?.Trim().ToLowerInvariant() ?? "text";

    /// <summary>Gets the border box of the input in the viewport.</summary>
    public RenderRect Anchor { get; }

    /// <summary>Gets the width of the viewport the picker opens in.</summary>
    public float ViewportWidth { get; }

    /// <summary>Gets the height of the viewport the picker opens in.</summary>
    public float ViewportHeight { get; }

    /// <summary>Gets the font of the picker's text (that of the input).</summary>
    public RenderFont Font { get; }

    /// <summary>Gets the colors of the picker.</summary>
    public InputPickerPalette Palette { get; }

    /// <summary>Gets what measures the picker's text, as it is painted.</summary>
    public ITextMeasurer TextMeasurer { get; }

    /// <summary>Gets the current local time.</summary>
    public DateTime Now { get; }

    /// <summary>
    /// Gets where a picker of the given size goes: below the input if it fits there, else on the
    /// side having more room, and moved into the viewport horizontally.
    /// </summary>
    /// <param name="width">The width of the picker.</param>
    /// <param name="height">The height of the picker.</param>
    /// <returns>Its bounds in the viewport.</returns>
    public RenderRect Place(float width, float height)
    {
        var below = ViewportHeight - (Anchor.Y + Anchor.Height);
        var above = Anchor.Y;
        var top = height <= below || below >= above ? Anchor.Y + Anchor.Height : Anchor.Y - height;
        var left = Math.Clamp(Anchor.X, 0f, Math.Max(0f, ViewportWidth - width));
        return new RenderRect(left, top, width, height);
    }
}
