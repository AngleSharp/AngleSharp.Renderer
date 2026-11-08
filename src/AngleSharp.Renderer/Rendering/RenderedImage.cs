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
    /// Gets the image payload.
    /// </summary>
    public byte[] Data => _data ?? VectorImage!.Png;

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
}