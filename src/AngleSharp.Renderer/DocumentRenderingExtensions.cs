using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer;

/// <summary>
/// Provides convenience extension methods for document rendering.
/// </summary>
public static class DocumentRenderingExtensions
{
    /// <summary>
    /// Renders a document to PNG bytes with the default renderer.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="options">Optional rendering settings.</param>
    /// <returns>The rendered PNG image.</returns>
    public static RenderedImage RenderToPng(this IDocument document, HtmlRenderOptions? options = null)
    {
        var renderer = new HtmlRenderer();
        return renderer.RenderToPng(document, options);
    }
}