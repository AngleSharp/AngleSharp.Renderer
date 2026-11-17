using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

namespace AngleSharp.Renderer;

/// <summary>
/// Rendering with <see cref="RenderOptions"/>: the full page, a clip or an element, at a device
/// scale factor, without the page's white background, as PNG, JPEG or WebP.
/// </summary>
/// <remarks>
/// A capture beyond the viewport is laid out in the viewport (so viewport units, media queries and
/// fixed boxes are as they are there) at the top of the page, and painted on a surface reaching
/// down as far as the capture does, as browser automation tools capture beyond the viewport. Where
/// the capture is comes from a layout of the page taken before: the harness's, if the document has
/// one for this viewport, else one taken without creating a harness, since a harness would turn
/// on the interactive state (selection, scrolling) for every later render of the document.
/// </remarks>
public sealed partial class HtmlRenderer
{
    /// <summary>
    /// Renders the given document to an image, with the render device of its browsing context
    /// (or the fallback viewport, <see cref="FallbackViewportWidth"/> × <see cref="FallbackViewportHeight"/>).
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="options">What to render, and how.</param>
    /// <returns>The encoded image.</returns>
    public RenderedImage RenderToImage(IDocument document, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        return RenderToImage(document, ResolveRenderDevice(document), options);
    }

    /// <summary>
    /// Renders the given document to an image.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="renderDevice">The render device used for viewport and typography defaults.</param>
    /// <param name="options">What to render, and how.</param>
    /// <returns>The encoded image.</returns>
    public RenderedImage RenderToImage(IDocument document, IRenderDevice renderDevice, RenderOptions options)
    {
        var (displayList, viewport) = BuildCapture(document, renderDevice, options, element: null);
        return Encode(displayList, viewport, options);
    }

    /// <summary>
    /// Renders the border box of the given element to an image, with the render device of its
    /// browsing context. <see cref="RenderOptions.Clip"/> is relative to the border box, and
    /// <see cref="RenderOptions.FullPage"/> plays no role.
    /// </summary>
    /// <param name="element">The element to render.</param>
    /// <param name="options">What to render, and how.</param>
    /// <returns>The encoded image.</returns>
    /// <exception cref="InvalidOperationException">The element generates no box.</exception>
    public RenderedImage RenderToImage(IElement element, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(element);
        var document = element.Owner ?? throw new ArgumentException("The element is not in a document.", nameof(element));
        var (displayList, viewport) = BuildCapture(document, ResolveRenderDevice(document), options, element);
        return Encode(displayList, viewport, options);
    }

    /// <summary>
    /// Renders the given document to raw pixels (premultiplied RGBA), with the render device of
    /// its browsing context. <see cref="RenderOptions.Format"/> and <see cref="RenderOptions.Quality"/>
    /// play no role.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="options">What to render, and how.</param>
    /// <returns>The pixels.</returns>
    /// <exception cref="NotSupportedException">The backend renders no pixels (it is no <see cref="IPixelRenderBackend"/>).</exception>
    public RenderedPixels RenderToPixels(IDocument document, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        return RenderToPixels(document, ResolveRenderDevice(document), options);
    }

    /// <summary>
    /// Renders the given document to raw pixels (premultiplied RGBA). <see cref="RenderOptions.Format"/>
    /// and <see cref="RenderOptions.Quality"/> play no role.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="renderDevice">The render device used for viewport and typography defaults.</param>
    /// <param name="options">What to render, and how.</param>
    /// <returns>The pixels.</returns>
    /// <exception cref="NotSupportedException">The backend renders no pixels (it is no <see cref="IPixelRenderBackend"/>).</exception>
    public RenderedPixels RenderToPixels(IDocument document, IRenderDevice renderDevice, RenderOptions options)
    {
        var pixelBackend = GetPixelBackend();
        var (displayList, viewport) = BuildCapture(document, renderDevice, options, element: null);
        return pixelBackend.RenderToPixels(displayList, viewport);
    }

    /// <summary>
    /// The width of the viewport a document is rendered in when its browsing context has no
    /// render device (as the default viewport of browser automation tools).
    /// </summary>
    public const int FallbackViewportWidth = 800;

    /// <summary>
    /// The height of the viewport a document is rendered in when its browsing context has no
    /// render device.
    /// </summary>
    public const int FallbackViewportHeight = 600;

    /// <summary>
    /// The render device of the document's browsing context, or one with the fallback viewport
    /// (AngleSharp.Css's own default device has none). Only rendering falls back: the harness
    /// needs a registered device, as AngleSharp.Css reads the context's device for
    /// <c>window.innerWidth</c> and the media queries of computed styles.
    /// </summary>
    internal static IRenderDevice ResolveRenderDevice(IDocument document) =>
        document.Context?.GetService<IRenderDevice>()
        ?? new DefaultRenderDevice { ViewPortWidth = FallbackViewportWidth, ViewPortHeight = FallbackViewportHeight };

    private IPixelRenderBackend GetPixelBackend() =>
        _backend as IPixelRenderBackend
        ?? throw new NotSupportedException($"The render backend {_backend.GetType().Name} renders no pixels; it only renders PNG images.");

    private RenderedImage Encode(DisplayList displayList, RenderViewport viewport, RenderOptions options) =>
        options.Format == RenderImageFormat.Png
            ? _backend.RenderToPng(displayList, viewport)
            : SkiaImageEncoder.Encode(GetPixelBackend().RenderToPixels(displayList, viewport), options.Format, options.Quality);

    private (DisplayList DisplayList, RenderViewport Viewport) BuildCapture(IDocument document, IRenderDevice renderDevice, RenderOptions options, IElement? element)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (element is null && options.FullPage && options.Clip is not null)
        {
            throw new ArgumentException("A capture is either of the full page or of a clip.", nameof(options));
        }

        var context = CreateLayoutContext(document, renderDevice, _textMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        var area = element is not null || options.FullPage || options.Clip is not null
            ? ResolveCaptureArea(document, renderDevice, viewport, options, element)
            : (RenderRect?)null;

        // the viewport is shown where it is scrolled to, a capture of the page from its top
        var scrollOffsetY = area is null ? ResolveRootScrollOffsetY(document) : 0f;
        var surfaceExtraHeight = area is { } below ? Math.Max(0f, MathF.Ceiling(below.Y + below.Height) - viewport.Height) : 0f;
        var displayList = PaintMediaTimelines(WithSelectionPainted(document, context, BuildDisplayList(
            document,
            viewport,
            context,
            renderDevice,
            scrollOffsetY,
            measureFullExtent: surfaceExtraHeight > 0f,
            surfaceExtraHeight: surfaceExtraHeight,
            omitBackground: options.OmitBackground)));

        var rect = area ?? new RenderRect(0f, 0f, viewport.Width, viewport.Height);
        var scale = options.DeviceScaleFactor;
        var output = new RenderViewport(ToDevicePixels(rect.Width * scale), ToDevicePixels(rect.Height * scale));

        if (scale != 1f || rect.X != 0f || rect.Y != 0f)
        {
            // device pixels from CSS pixels: the capture's top left at the origin, scaled
            displayList.InsertRange(0, [new PushTransformCommand(new RenderTransform2D(scale, 0f, 0f, scale, -rect.X * scale, -rect.Y * scale))]);
            displayList.PopTransform();
        }

        return (displayList, output);
    }

    // a size that is a whole number but for float noise (e.g. 199.99998) stays that number
    private static int ToDevicePixels(float size) => Math.Max(1, (int)MathF.Ceiling(size - 0.001f));

    private static RenderRect ResolveCaptureArea(IDocument document, IRenderDevice renderDevice, RenderViewport viewport, RenderOptions options, IElement? element)
    {
        var (metrics, styleMaps) = GetPageLayout(document, renderDevice);
        var pageHeight = (float)viewport.Height;

        if (document.DocumentElement is { } root && metrics.TryGetValue(root, out var rootMetrics))
        {
            pageHeight = Math.Max(pageHeight, ElementCssomViewExtensions.GetScrollExtents(root, rootMetrics, metrics, styleMaps, 0d, 0d).Height);
        }

        var page = new RenderRect(0f, 0f, viewport.Width, pageHeight);
        RenderRect area;

        if (element is not null)
        {
            if (!metrics.TryGetValue(element, out var box) || box.BorderBoxWidth <= 0f || box.BorderBoxHeight <= 0f)
            {
                throw new InvalidOperationException("The element generates no box to render.");
            }

            area = options.Clip is { } relative
                ? new RenderRect(box.BorderBoxX + relative.X, box.BorderBoxY + relative.Y, relative.Width, relative.Height)
                : new RenderRect(box.BorderBoxX, box.BorderBoxY, box.BorderBoxWidth, box.BorderBoxHeight);
        }
        else
        {
            area = options.Clip ?? page;
        }

        var left = Math.Max(area.X, page.X);
        var top = Math.Max(area.Y, page.Y);
        var right = Math.Min(area.X + area.Width, page.X + page.Width);
        var bottom = Math.Min(area.Y + area.Height, page.Y + page.Height);

        if (right <= left || bottom <= top)
        {
            throw new ArgumentException("The capture is outside of the page.", nameof(options));
        }

        return new RenderRect(left, top, right - left, bottom - top);
    }

    private static (IReadOnlyDictionary<IElement, ElementLayoutMetrics> Metrics, IReadOnlyDictionary<IElement, Dictionary<string, string>> StyleMaps) GetPageLayout(IDocument document, IRenderDevice renderDevice)
    {
        if (document.Context.TryGetDomHarness(out var harness) &&
            harness is InteractiveHtmlRendererState state &&
            state.RenderDevice.ViewPortWidth == renderDevice.ViewPortWidth &&
            state.RenderDevice.ViewPortHeight == renderDevice.ViewPortHeight)
        {
            var current = state.GetLayoutSnapshot(document);
            return (current.Metrics, current.StyleMaps);
        }

        var snapshot = LayoutSnapshot.Capture(document, renderDevice);
        return (snapshot.Metrics, snapshot.StyleMaps);
    }
}
