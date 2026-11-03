namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// A text measurer that knows the metrics a font states: its character widths, which size text
/// fields, and its line spacing, which is what `line-height: normal` means.
/// </summary>
internal interface IFontMetricsMeasurer
{
    /// <summary>
    /// The average and maximum character width of the font in pixels.
    /// </summary>
    (float Average, float Maximum) MeasureCharacterWidths(RenderFont font);

    /// <summary>
    /// The height of the font's ascent and descent in pixels, which is what a caret spans.
    /// </summary>
    float MeasureContentHeight(RenderFont font);

    /// <summary>
    /// The ascent and descent of the font in pixels, each a whole number as in Blink.
    /// </summary>
    (float Ascent, float Descent) MeasureFontHeights(RenderFont font);

    /// <summary>
    /// The distance between the baselines of two lines of the font in pixels.
    /// </summary>
    float MeasureLineSpacing(RenderFont font);
}
