namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Contains rasterized pixels: rows of premultiplied RGBA (8 bits per channel) without padding.
/// </summary>
public sealed class RenderedPixels
{
    /// <summary>
    /// Creates a new pixel buffer result.
    /// </summary>
    public RenderedPixels(byte[] data, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length != width * height * 4)
        {
            throw new ArgumentException("The buffer does not match the dimensions.", nameof(data));
        }

        Data = data;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Gets the pixels, row by row from the top.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// Gets the width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the number of bytes per row.
    /// </summary>
    public int RowBytes => Width * 4;
}
