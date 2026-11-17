using System.Runtime.InteropServices;

using AngleSharp.Renderer.Rendering;

using SkiaSharp;

namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Encodes rasterized pixels (premultiplied RGBA) as an image file.
/// </summary>
internal static class SkiaImageEncoder
{
    public static RenderedImage Encode(RenderedPixels pixels, RenderImageFormat format, int quality)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(pixels.Data, GCHandleType.Pinned);

        try
        {
            using var source = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), pixels.RowBytes)
                ?? throw new InvalidOperationException("Unable to read the rendered pixels.");

            if (format == RenderImageFormat.Jpeg)
            {
                // JPEG has no alpha: what is transparent shows the white a browser would show
                using var surface = SKSurface.Create(info)
                    ?? throw new InvalidOperationException("Unable to create a Skia surface.");
                surface.Canvas.Clear(SKColors.White);
                surface.Canvas.DrawImage(source, 0f, 0f);
                using var opaque = surface.Snapshot();
                return Encode(opaque, SKEncodedImageFormat.Jpeg, quality, "image/jpeg");
            }

            return format == RenderImageFormat.Webp
                ? Encode(source, SKEncodedImageFormat.Webp, quality, "image/webp")
                : Encode(source, SKEncodedImageFormat.Png, 100, "image/png");
        }
        finally
        {
            handle.Free();
        }
    }

    private static RenderedImage Encode(SKImage image, SKEncodedImageFormat format, int quality, string mimeType)
    {
        using var data = image.Encode(format, quality)
            ?? throw new InvalidOperationException($"Unable to encode the rendered image as {mimeType}.");
        return new RenderedImage(data.ToArray(), image.Width, image.Height, mimeType);
    }
}
