namespace AngleSharp.Renderer.Rendering;

using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Media;
using SkiaSharp;

/// <summary>
/// Buffers image responses while AngleSharp's request processor still owns them. The processor
/// disposes a response (closing its content stream and clearing its headers) right after
/// processing it, so an <c>&lt;img&gt;</c>'s <c>CurrentDownload</c> can no longer be read when the
/// document is painted later; the renderer looks the bytes up here by the response instead.
/// </summary>
internal sealed class ImageResourceService : IResourceService<IImageInfo>
{
    private static readonly ConditionalWeakTable<IResponse, CapturedImage> s_captured = new();

    public static ImageResourceService Instance { get; } = new();

    private ImageResourceService()
    {
    }

    public bool SupportsType(string mimeType) =>
        mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

    public async Task<IImageInfo> CreateAsync(IResponse response, CancellationToken cancel)
    {
        using var buffer = new MemoryStream();

        if (response.Content is not null)
        {
            await response.Content.CopyToAsync(buffer, cancel).ConfigureAwait(false);
        }

        var bytes = buffer.ToArray();
        var mimeType = response.Headers?.TryGetValue("Content-Type", out var contentType) == true && !string.IsNullOrWhiteSpace(contentType)
            ? contentType
            : "image/unknown";
        s_captured.AddOrUpdate(response, new CapturedImage(bytes, mimeType));

        var width = 0;
        var height = 0;

        using (var data = SKData.CreateCopy(bytes))
        using (var codec = SKCodec.Create(data))
        {
            if (codec is not null)
            {
                width = codec.Info.Width;
                height = codec.Info.Height;
            }
        }

        return new ImageInfo { Source = response.Address, Width = width, Height = height };
    }

    /// <summary>
    /// The bytes and content type of a response this service processed.
    /// </summary>
    public static bool TryGetCaptured(IResponse response, out byte[] bytes, out string mimeType)
    {
        if (s_captured.TryGetValue(response, out var captured))
        {
            bytes = captured.Bytes;
            mimeType = captured.MimeType;
            return true;
        }

        bytes = [];
        mimeType = string.Empty;
        return false;
    }

    private sealed record CapturedImage(byte[] Bytes, string MimeType);

    private sealed class ImageInfo : IImageInfo
    {
        public required Url Source { get; set; }

        public int Width { get; init; }

        public int Height { get; init; }
    }
}
