namespace AngleSharp.Dom;

using AngleSharp.Css;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Provides convenience extension methods for document rendering.
/// </summary>
public static class DocumentRenderingExtensions
{
    /// <summary>
    /// Renders a document to PNG bytes with the default renderer.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <returns>The rendered PNG image.</returns>
    public static RenderedImage RenderToPng(this IDocument document)
    {
        var renderDevice = document.Context.GetService<IRenderDevice>();
        return document.RenderToPng(renderDevice!);
    }

    /// <summary>
    /// Renders a document to PNG bytes with the default renderer.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="renderDevice">The render device used for rendering.</param>
    /// <returns>The rendered PNG image.</returns>
    public static RenderedImage RenderToPng(this IDocument document, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(renderDevice);

        var renderer = new HtmlRenderer();
        return renderer.RenderToPng(document, renderDevice);
    }
}