namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// How the edge and middle parts of a `border-image` fill their areas (`border-image-repeat`).
/// </summary>
public enum RenderBorderImageRepeat
{
    /// <summary>Stretches the part to fill the area.</summary>
    Stretch,

    /// <summary>Tiles the part, centered, clipping the tiles at both ends.</summary>
    Repeat,

    /// <summary>Tiles the part, scaled so a whole number of tiles fills the area.</summary>
    Round,

    /// <summary>Tiles as many whole parts as fit, spreading the leftover space between them.</summary>
    Space,
}

/// <summary>
/// One `border-image-slice` offset: image pixels, or a fraction (0..1) of the image's size.
/// </summary>
public readonly record struct RenderBorderImageSlice(float Value, bool IsPercentage);

/// <summary>
/// A resolved `border-image`: the source is cut into nine parts by the slice offsets, and the
/// corners, edges and (with <paramref name="Fill"/>) the middle are drawn into the border image
/// area, whose edge widths are already resolved to pixels.
/// </summary>
/// <param name="Source">The image or gradient to slice.</param>
/// <param name="SliceTop">The top slice offset.</param>
/// <param name="SliceRight">The right slice offset.</param>
/// <param name="SliceBottom">The bottom slice offset.</param>
/// <param name="SliceLeft">The left slice offset.</param>
/// <param name="Fill">Whether the middle part is drawn too (`fill`).</param>
/// <param name="WidthTop">The top edge width in pixels.</param>
/// <param name="WidthRight">The right edge width in pixels.</param>
/// <param name="WidthBottom">The bottom edge width in pixels.</param>
/// <param name="WidthLeft">The left edge width in pixels.</param>
/// <param name="RepeatX">How the top, bottom and middle parts fill horizontally.</param>
/// <param name="RepeatY">How the left, right and middle parts fill vertically.</param>
public sealed record RenderBorderImage(
    RenderPaint Source,
    RenderBorderImageSlice SliceTop,
    RenderBorderImageSlice SliceRight,
    RenderBorderImageSlice SliceBottom,
    RenderBorderImageSlice SliceLeft,
    bool Fill,
    float WidthTop,
    float WidthRight,
    float WidthBottom,
    float WidthLeft,
    RenderBorderImageRepeat RepeatX,
    RenderBorderImageRepeat RepeatY);
