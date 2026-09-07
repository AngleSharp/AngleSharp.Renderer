namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Measures text the way the backend will eventually paint it.
/// </summary>
/// <remarks>
/// Layout and rasterization have to agree on advance widths. If they do not, line breaking,
/// text alignment and table column widths are computed against a font that is never drawn.
/// </remarks>
public interface ITextMeasurer
{
    /// <summary>
    /// Measures the advance width of the given text in pixels.
    /// </summary>
    /// <param name="text">The text to measure.</param>
    /// <param name="font">The font the text is rendered with.</param>
    /// <returns>The advance width in pixels.</returns>
    float MeasureWidth(string text, RenderFont font);
}
