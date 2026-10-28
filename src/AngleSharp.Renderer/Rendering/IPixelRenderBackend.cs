namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Represents a backend that can rasterize a display list into raw pixels, which spares
/// interactive hosts encoding and decoding an image for every frame.
/// </summary>
public interface IPixelRenderBackend : IRenderBackend
{
    /// <summary>
    /// Rasterizes the display list into premultiplied RGBA pixels.
    /// </summary>
    /// <param name="displayList">The list of draw commands.</param>
    /// <param name="viewport">The target viewport.</param>
    /// <returns>The resulting pixels.</returns>
    RenderedPixels RenderToPixels(DisplayList displayList, RenderViewport viewport);
}
