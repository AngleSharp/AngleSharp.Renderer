namespace AngleSharp.Renderer.Rendering;

using AngleSharp.Renderer.Skia;

/// <summary>
/// Contains rendered image data.
/// </summary>
public sealed class RenderedImage
{
    private readonly byte[]? _data;

    /// <summary>
    /// Creates a new rendered image result.
    /// </summary>
    public RenderedImage(byte[] data, int width, int height, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        _data = data;
        Width = width;
        Height = height;
        MimeType = mimeType;
    }

    /// <summary>
    /// Creates an image of an SVG, whose payload is only rendered when it is asked for.
    /// </summary>
    internal RenderedImage(SvgVectorImage vectorImage, int width, int height)
    {
        VectorImage = vectorImage;
        Width = width;
        Height = height;
        MimeType = "image/png";
    }

    /// <summary>
    /// Creates the image of a canvas, whose bitmap is the one it has when the image is painted.
    /// </summary>
    internal RenderedImage(AngleSharp.Html.Dom.IHtmlCanvasElement canvas, int width, int height)
    {
        Canvas = canvas;
        Width = width;
        Height = height;
        MimeType = "image/png";
    }

    /// <summary>
    /// Gets the image payload.
    /// </summary>
    public byte[] Data => _data ?? VectorImage?.Png ?? EncodeCanvas();

    /// <summary>
    /// Gets the image width.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the image height.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the MIME type.
    /// </summary>
    public string MimeType { get; }

    /// <summary>
    /// Gets the vectors of an SVG, which a backend that can should paint instead of the payload.
    /// </summary>
    internal SvgVectorImage? VectorImage { get; }

    /// <summary>
    /// Gets the canvas whose bitmap this is, which a backend that can should paint as it is now.
    /// </summary>
    internal AngleSharp.Html.Dom.IHtmlCanvasElement? Canvas { get; }

    /// <summary>
    /// Gets the bitmap of the canvas as it is now, or null if nothing was drawn into it.
    /// </summary>
    internal SkiaSharp.SKImage? GetCanvasSnapshot() =>
        Canvas is { } canvas ? CanvasRenderingService.GetContext(canvas)?.GetSnapshot() : null;

    private byte[] EncodeCanvas()
    {
        var snapshot = GetCanvasSnapshot();

        if (snapshot is null)
        {
            using var empty = new SkiaSharp.SKBitmap(Math.Max(1, Width), Math.Max(1, Height));
            empty.Erase(SkiaSharp.SKColors.Transparent);
            using var emptyData = empty.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return emptyData.ToArray();
        }

        using var data = snapshot.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}