namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Represents a backend that can rasterize a display list.
/// </summary>
public interface IRenderBackend
{
    /// <summary>
    /// Rasterizes the display list into a PNG image.
    /// </summary>
    /// <param name="displayList">The list of draw commands.</param>
    /// <param name="viewport">The target viewport.</param>
    /// <returns>The resulting image bytes and metadata.</returns>
    RenderedImage RenderToPng(DisplayList displayList, RenderViewport viewport);
}