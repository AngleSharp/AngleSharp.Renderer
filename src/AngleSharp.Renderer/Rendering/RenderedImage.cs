namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Contains rendered image data.
/// </summary>
public sealed class RenderedImage
{
    /// <summary>
    /// Creates a new rendered image result.
    /// </summary>
    public RenderedImage(byte[] data, int width, int height, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        Data = data;
        Width = width;
        Height = height;
        MimeType = mimeType;
    }

    /// <summary>
    /// Gets the image payload.
    /// </summary>
    public byte[] Data { get; }

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
}