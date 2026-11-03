namespace AngleSharp.Renderer.Skia;

using AngleSharp.Renderer.Rendering;

using SkiaSharp;

/// <summary>
/// Measures text with the same typefaces and paint settings <see cref="SkiaRenderBackend"/> paints with.
/// </summary>
public sealed class SkiaTextMeasurer : ITextMeasurer, IFontMetricsMeasurer
{
    // Layout measures once per word, so the paints are cached. They are kept per thread because
    // SKPaint is not safe to share across threads, and typeface resolution behind them is already
    // process wide.
    // A document only ever uses a handful of distinct fonts; the cap is there so a pathological
    // document cannot grow the cache without bound.
    private const int MaxCachedPaints = 256;

    [ThreadStatic]
    private static Dictionary<RenderFont, SKPaint>? t_paints;

    // Shaping a word is far costlier than looking it up, and layout measures the same words over
    // and over (every trial layout of an inline-block measures its content again). A width only
    // depends on the text and the font, so it is cached per thread, cleared when it gets large.
    private const int MaxCachedWidths = 16384;

    [ThreadStatic]
    private static Dictionary<(string Text, RenderFont Font), float>? t_widths;

    [ThreadStatic]
    private static Dictionary<RenderFont, float>? t_xHeights;

    /// <inheritdoc />
    public float MeasureWidth(string text, RenderFont font)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        var widths = t_widths ??= [];

        if (widths.TryGetValue((text, font), out var width))
        {
            return width;
        }

        if (widths.Count >= MaxCachedWidths)
        {
            widths.Clear();
        }

        width = SkiaTextShaping.MeasureTextWidth(GetPaint(font), text, font.LetterSpacing, font);
        widths[(text, font)] = width;
        return width;
    }

    /// <inheritdoc />
    public float MeasureXHeight(RenderFont font)
    {
        var heights = t_xHeights ??= [];

        if (!heights.TryGetValue(font, out var height))
        {
            using var skFont = new SKFont(GetPaint(font).Typeface, font.FontSize);
            height = skFont.Metrics.XHeight;

            // not every font states its x-height
            if (!(height > 0f))
            {
                height = font.FontSize * 0.5f;
            }

            if (heights.Count >= MaxCachedPaints)
            {
                heights.Clear();
            }

            heights[font] = height;
        }

        return height;
    }

    (float Average, float Maximum) IFontMetricsMeasurer.MeasureCharacterWidths(RenderFont font)
    {
        var metrics = SkiaTextShaping.GetFontTableMetrics(GetPaint(font).Typeface);
        // not every font states its average
        return (metrics.AverageWidth > 0f ? metrics.AverageWidth * font.FontSize : MeasureWidth("0", font), metrics.MaximumWidth * font.FontSize);
    }

    float IFontMetricsMeasurer.MeasureContentHeight(RenderFont font)
    {
        var (ascent, descent) = ((IFontMetricsMeasurer)this).MeasureFontHeights(font);
        return ascent + descent;
    }

    (float Ascent, float Descent) IFontMetricsMeasurer.MeasureFontHeights(RenderFont font)
    {
        var metrics = SkiaTextShaping.GetFontTableMetrics(GetPaint(font).Typeface);

        // not every font states them
        if (metrics.Ascent <= 0f && metrics.Descent <= 0f)
        {
            using var skFont = new SKFont(GetPaint(font).Typeface, font.FontSize);
            return (MathF.Round(-skFont.Metrics.Ascent, MidpointRounding.AwayFromZero), MathF.Round(skFont.Metrics.Descent, MidpointRounding.AwayFromZero));
        }

        // each one is rounded on its own, as in Blink
        return (MathF.Round(metrics.Ascent * font.FontSize, MidpointRounding.AwayFromZero),
            MathF.Round(metrics.Descent * font.FontSize, MidpointRounding.AwayFromZero));
    }

    float IFontMetricsMeasurer.MeasureLineSpacing(RenderFont font)
    {
        var metrics = SkiaTextShaping.GetFontTableMetrics(GetPaint(font).Typeface);
        var content = ((IFontMetricsMeasurer)this).MeasureContentHeight(font);

        if (metrics.Ascent <= 0f && metrics.Descent <= 0f)
        {
            using var skFont = new SKFont(GetPaint(font).Typeface, font.FontSize);
            return content + skFont.Metrics.Leading;
        }

        return content + MathF.Round(metrics.LineGap * font.FontSize, MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    public IReadOnlyList<int> GetClusterBoundaries(string text, RenderFont font) =>
        string.IsNullOrEmpty(text) ? [0] : SkiaTextShaping.GetClusterBoundaries(GetPaint(font), text, font);

    private static SKPaint GetPaint(RenderFont font)
    {
        var paints = t_paints ??= [];

        if (!paints.TryGetValue(font, out var paint))
        {
            if (paints.Count >= MaxCachedPaints)
            {
                foreach (var cached in paints.Values)
                {
                    cached.Dispose();
                }

                paints.Clear();
            }

            paint = SkiaTextShaping.CreateTextPaint(font);
            paints[font] = paint;
        }

        return paint;
    }
}
