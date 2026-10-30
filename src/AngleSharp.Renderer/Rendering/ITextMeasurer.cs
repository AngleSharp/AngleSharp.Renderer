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

    /// <summary>
    /// Measures the x-height of the font (the height of its lowercase letters) in pixels, which
    /// `vertical-align: middle` centers on. The default assumes half the font size.
    /// </summary>
    /// <param name="font">The font the text is rendered with.</param>
    /// <returns>The x-height in pixels.</returns>
    float MeasureXHeight(RenderFont font) => font.FontSize * 0.5f;

    /// <summary>
    /// The offsets at which the text may be cut without breaking a glyph cluster (e.g. to
    /// truncate it with an ellipsis): ascending UTF-16 offsets, starting with 0 and ending with
    /// the length of the text. The default treats every grapheme cluster as one.
    /// </summary>
    /// <param name="text">The text to split.</param>
    /// <param name="font">The font the text is rendered with.</param>
    /// <returns>The cluster boundaries.</returns>
    IReadOnlyList<int> GetClusterBoundaries(string text, RenderFont font)
    {
        var boundaries = new List<int> { 0 };
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            boundaries.Add(enumerator.ElementIndex + ((string)enumerator.Current).Length);
        }

        return boundaries;
    }
}
