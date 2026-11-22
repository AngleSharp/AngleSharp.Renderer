namespace AngleSharp.Dom;

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Controls;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

/// <summary>
/// Where and when a picker opens.
/// </summary>
public sealed class InputPickerOptions
{
    /// <summary>Gets or sets where the viewport of the input's document is horizontally, e.g. for an input in an iframe.</summary>
    public double OriginX { get; set; }

    /// <summary>Gets or sets where the viewport of the input's document is vertically.</summary>
    public double OriginY { get; set; }

    /// <summary>Gets or sets the device of the page the picker opens in, if it isn't that of the input's document.</summary>
    public IRenderDevice? Page { get; set; }

    /// <summary>Gets or sets the current local time (e.g. for marking today), else the clock's.</summary>
    public DateTime? Now { get; set; }
}

/// <summary>
/// What a host needs to open the picker of an input (a calendar, a clock, a color picker, ...):
/// the picker comes from the input's <see cref="IInputControl"/>, see
/// <see cref="InputControlConfigurationExtensions"/> for using others.
/// </summary>
public static class InputPickerExtensions
{
    /// <summary>
    /// Gets if the element is an input whose control has a picker it can open now, i.e. it is
    /// neither disabled nor read-only.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>True if <see cref="OpenInputPicker"/> may return a picker.</returns>
    public static bool HasInputPicker(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element is IHtmlInputElement input && !input.IsDisabled && !input.IsReadOnly &&
            InputControlRegistry.Resolve(input) is not null;
    }

    /// <summary>
    /// Creates the picker of an input where it is in the viewport, as laid out last.
    /// </summary>
    /// <param name="element">The input element.</param>
    /// <param name="options">Where and when it opens.</param>
    /// <returns>The picker, or null if the input has none (or is not shown).</returns>
    public static IInputPicker? OpenInputPicker(this IElement element, InputPickerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!element.HasInputPicker() || element is not IHtmlInputElement input ||
            InputControlRegistry.Resolve(input) is not { } control ||
            input.Owner is not { } document ||
            ElementCssomViewExtensions.GetViewportMetrics(input) is not { } metrics ||
            metrics.BorderBoxWidth <= 0f)
        {
            return null;
        }

        options ??= new InputPickerOptions();
        var style = input.ComputeCurrentStyle();
        var device = options.Page ?? document.Context.GetDomHarness().RenderDevice;
        var fontSize = DocumentCssomViewExtensions.ParseLengthOrDefault(style?.GetPropertyValue("font-size"), (float)device.FontSize);
        var font = DocumentCssomViewExtensions.CreateFont(style, document, fontSize) with { LetterSpacing = 0f };
        var palette = SelectPopup.UsesDarkColorScheme(style, device, document) ? InputPickerPalette.Dark : InputPickerPalette.Light;
        var accent = style?.GetPropertyValue("accent-color")?.Trim();

        if (!string.IsNullOrEmpty(accent) && !string.Equals(accent, "auto", StringComparison.OrdinalIgnoreCase) &&
            HtmlRenderer.TryParseColor(accent, out var accentColor))
        {
            palette = palette with { Accent = accentColor, AccentText = GetContrastingText(accentColor) };
        }

        var anchor = new RenderRect(metrics.BorderBoxX + (float)options.OriginX, metrics.BorderBoxY + (float)options.OriginY, metrics.BorderBoxWidth, metrics.BorderBoxHeight);
        var context = new InputPickerContext(input, anchor, (float)device.ViewPortWidth, (float)device.ViewPortHeight, font, palette, new SkiaTextMeasurer(), options.Now ?? DateTime.Now);
        return control.CreatePicker(context);
    }

    // black or white, whichever reads better on the color
    private static RenderColor GetContrastingText(RenderColor color) =>
        (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B) > 150d ? RenderColor.Black : RenderColor.White;
}
