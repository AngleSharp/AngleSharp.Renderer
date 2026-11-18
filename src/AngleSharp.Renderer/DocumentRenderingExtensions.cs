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
    /// <remarks>The render device is the one of the document's browsing context, or <see cref="HtmlRenderer.FallbackViewportWidth"/> × <see cref="HtmlRenderer.FallbackViewportHeight"/> without one.</remarks>
    public static RenderedImage RenderToPng(this IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new HtmlRenderer().RenderToPng(document);
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

    /// <summary>
    /// Renders a document to an image with the default renderer and the render device of its
    /// browsing context.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="options">What to render, and how; the viewport as PNG by default.</param>
    /// <returns>The encoded image.</returns>
    public static RenderedImage RenderToImage(this IDocument document, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new HtmlRenderer().RenderToImage(document, options ?? RenderOptions.Default);
    }

    /// <summary>
    /// Renders the border box of an element to an image with the default renderer and the render
    /// device of its browsing context. <see cref="RenderOptions.Clip"/> is relative to the border
    /// box.
    /// </summary>
    /// <param name="element">The element to render.</param>
    /// <param name="options">What to render, and how; the whole border box as PNG by default.</param>
    /// <returns>The encoded image.</returns>
    public static RenderedImage RenderToImage(this IElement element, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        return new HtmlRenderer().RenderToImage(element, options ?? RenderOptions.Default);
    }
}
