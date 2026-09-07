namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Describes a single `box-shadow` layer.
/// </summary>
/// <param name="OffsetX">Horizontal offset in pixels; positive moves the shadow right.</param>
/// <param name="OffsetY">Vertical offset in pixels; positive moves the shadow down.</param>
/// <param name="BlurRadius">The Gaussian blur radius in pixels (`0` for a hard-edged shadow).</param>
/// <param name="SpreadRadius">
/// How far the shadow shape grows (positive) or shrinks (negative) from the border box before
/// blurring, in pixels.
/// </param>
/// <param name="Color">The shadow's color.</param>
/// <param name="Inset">Whether the shadow is painted inside the border box (`inset`) rather than outside it.</param>
public sealed record RenderBoxShadow(
    float OffsetX,
    float OffsetY,
    float BlurRadius,
    float SpreadRadius,
    RenderColor Color,
    bool Inset);

/// <summary>
/// Describes a single `text-shadow` layer. Unlike <see cref="RenderBoxShadow"/>, `text-shadow`
/// has no spread and no `inset` variant.
/// </summary>
/// <param name="OffsetX">Horizontal offset in pixels; positive moves the shadow right.</param>
/// <param name="OffsetY">Vertical offset in pixels; positive moves the shadow down.</param>
/// <param name="BlurRadius">The Gaussian blur radius in pixels (`0` for a hard-edged shadow).</param>
/// <param name="Color">The shadow's color.</param>
public sealed record RenderTextShadow(
    float OffsetX,
    float OffsetY,
    float BlurRadius,
    RenderColor Color);
