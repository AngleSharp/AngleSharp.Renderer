namespace AngleSharp.Media.Dom;

using System;
using AngleSharp.Attributes;
using AngleSharp.Dom;

/// <summary>
/// The pixels of a part of a canvas (HTML 4.12.5.1.16): RGBA, not premultiplied, row by row.
/// </summary>
[DomName("ImageData")]
public sealed class ImageData
{
    /// <summary>
    /// Creates transparent black image data of the given size.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    [DomConstructor]
    public ImageData(int width, int height)
        : this(width, height, new byte[checked(Math.Max(0, width) * Math.Max(0, height) * 4)])
    {
        if (width <= 0 || height <= 0)
        {
            throw new DomException(DomError.IndexSizeError);
        }
    }

    internal ImageData(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Data = data;
    }

    /// <summary>
    /// Gets the width in pixels.
    /// </summary>
    [DomName("width")]
    public int Width { get; }

    /// <summary>
    /// Gets the height in pixels.
    /// </summary>
    [DomName("height")]
    public int Height { get; }

    /// <summary>
    /// Gets the pixels (for scripts a copy, as WebIDL has no clamped array here).
    /// </summary>
    [DomName("data")]
    public ArraySegment<byte> Pixels => new(Data);

    /// <summary>
    /// Gets the pixels, RGBA, four bytes per pixel.
    /// </summary>
    public byte[] Data { get; }
}
