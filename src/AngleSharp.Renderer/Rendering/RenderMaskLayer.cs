namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// One layer of a CSS mask (`mask-image` and its companion properties).
/// </summary>
/// <param name="Paint">The mask image (an image or a gradient paint).</param>
/// <param name="Area">The area the layer masks (`mask-clip`); outside of it the layer hides everything.</param>
/// <param name="PaintArea">The area the image is sized and positioned in (`mask-origin`).</param>
/// <param name="UseLuminance">Whether the mask uses the image's luminance (`mask-mode: luminance`) rather than its alpha.</param>
public sealed record RenderMaskLayer(RenderPaint Paint, RenderRect Area, RenderRect PaintArea, bool UseLuminance);
