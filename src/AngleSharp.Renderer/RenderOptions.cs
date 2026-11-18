namespace AngleSharp.Renderer;

using AngleSharp.Renderer.Rendering;

/// <summary>
/// The encodings an image can be rendered to.
/// </summary>
public enum RenderImageFormat
{
    /// <summary>
    /// PNG: lossless, with transparency.
    /// </summary>
    Png,

    /// <summary>
    /// JPEG: lossy, without transparency (transparent areas become white).
    /// </summary>
    Jpeg,

    /// <summary>
    /// WebP: lossy, with transparency.
    /// </summary>
    Webp,
}

/// <summary>
/// Options for rendering a document or an element to an image (see
/// <see cref="HtmlRenderer.RenderToImage(AngleSharp.Dom.IDocument, RenderOptions)"/>).
/// </summary>
/// <remarks>
/// The viewport is the one of the render device: media queries and viewport units are evaluated
/// against it, and a capture of the full page or of a clip is laid out in it as well. Only what is
/// painted extends beyond it, as with the <c>fullPage</c> and <c>clip</c> screenshots of
/// browser automation tools.
/// </remarks>
public sealed class RenderOptions
{
    /// <summary>
    /// Gets the default options: the viewport at its scroll position, as PNG at one device pixel
    /// per CSS pixel.
    /// </summary>
    public static RenderOptions Default { get; } = new();

    /// <summary>
    /// Gets whether the whole page is rendered, from its top to the end of its content, instead of
    /// the viewport at its scroll position. The image is as wide as the viewport.
    /// </summary>
    public bool FullPage { get; init; }

    /// <summary>
    /// Gets the part of the page to render, in CSS pixels from the top left of the page (not of
    /// the viewport, so the scroll position plays no role). It is limited to the page and to the
    /// width of the viewport. For an element, it is relative to the element's border box.
    /// </summary>
    public RenderRect? Clip { get; init; }

    /// <summary>
    /// Gets the number of device pixels per CSS pixel, e.g. 2 for the screens of phones. The
    /// layout stays the same; text, borders and vector images are painted at the higher
    /// resolution, raster images are scaled up.
    /// </summary>
    public float DeviceScaleFactor { get; init; } = 1f;

    /// <summary>
    /// Gets whether the white background of the page is left out, so that the image is
    /// transparent where the page paints no background of its own.
    /// </summary>
    public bool OmitBackground { get; init; }

    /// <summary>
    /// Gets the encoding of the image.
    /// </summary>
    public RenderImageFormat Format { get; init; } = RenderImageFormat.Png;

    /// <summary>
    /// Gets the quality of a lossy encoding (JPEG, WebP), from 0 to 100.
    /// </summary>
    public int Quality { get; init; } = 90;

    internal void Validate()
    {
        if (!float.IsFinite(DeviceScaleFactor) || DeviceScaleFactor <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(DeviceScaleFactor), DeviceScaleFactor, "The device scale factor must be a positive number.");
        }

        if (Quality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Quality), Quality, "The quality must be between 0 and 100.");
        }

        if (Clip is { } clip && (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) || !float.IsFinite(clip.Width) || !float.IsFinite(clip.Height) || clip.Width <= 0f || clip.Height <= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(Clip), clip, "The clip must have a positive size.");
        }

        if (!Enum.IsDefined(Format))
        {
            throw new ArgumentOutOfRangeException(nameof(Format), Format, "Unknown image format.");
        }
    }
}
