namespace AngleSharp.Renderer.Skia;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using SkiaSharp;

/// <summary>
/// An image of several frames (an animated GIF or WebP), which shows the frame its time has come
/// to: like in browsers, its animation starts when it is first painted and goes on with the clock,
/// for all places showing the same payload alike. Frames are decoded once they are due.
/// </summary>
internal sealed class AnimatedImage
{
    // browsers take a delay of 10 ms or less for 100 ms, as many GIFs were made for that
    private const int MinimumDuration = 11;
    private const int DefaultDuration = 100;

    private static readonly ConditionalWeakTable<byte[], Box> s_images = new();

    private readonly object _lock = new();
    private readonly byte[] _bytes;
    private readonly SKImageInfo _info;
    private readonly SKCodecFrameInfo[] _frames;
    // when each frame ends within a round, in milliseconds
    private readonly long[] _ends;
    private readonly SKImage?[] _decoded;
    private readonly int _repetitions;
    private readonly long _start = Stopwatch.GetTimestamp();

    private AnimatedImage(byte[] bytes, SKCodec codec)
    {
        _bytes = bytes;
        _info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
        _frames = codec.FrameInfo;
        _ends = new long[_frames.Length];
        _decoded = new SKImage?[_frames.Length];
        _repetitions = codec.RepetitionCount;
        var end = 0L;

        for (var i = 0; i < _frames.Length; i++)
        {
            end += _frames[i].Duration < MinimumDuration ? DefaultDuration : _frames[i].Duration;
            _ends[i] = end;
        }
    }

    private sealed record Box(AnimatedImage? Image);

    /// <summary>
    /// Gets the animation of the payload, or null if it is no image of several frames.
    /// </summary>
    public static AnimatedImage? For(byte[] bytes)
    {
        if (!MayBeAnimated(bytes))
        {
            return null;
        }

        return s_images.GetValue(bytes, static key =>
        {
            using var data = SKData.CreateCopy(key);
            using var codec = SKCodec.Create(data);
            return new Box(codec is { FrameCount: > 1, Info.Width: > 0, Info.Height: > 0 } ? new AnimatedImage(key, codec) : null);
        }).Image;
    }

    /// <summary>
    /// Gets the frame to show now, or null if it cannot be decoded.
    /// </summary>
    public SKImage? GetCurrentFrame() => GetFrame(GetPosition(out _));

    /// <summary>
    /// Gets how long the current frame is still shown, or null if the animation is over.
    /// </summary>
    public TimeSpan? GetTimeToNextFrame()
    {
        GetPosition(out var remaining);
        return remaining;
    }

    // which frame is shown now, and for how much longer (null when the last round is over)
    private int GetPosition(out TimeSpan? remaining)
    {
        var round = _ends[^1];
        var elapsed = (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        // a count of 0 plays once, -1 (the loop extension asking for 0) forever
        var rounds = elapsed / round;

        if (_repetitions >= 0 && rounds > _repetitions)
        {
            remaining = null;
            return _frames.Length - 1;
        }

        var time = elapsed % round;
        var index = Array.FindIndex(_ends, end => time < end);
        remaining = TimeSpan.FromMilliseconds(_ends[index] - time);
        return index;
    }

    private SKImage? GetFrame(int index)
    {
        lock (_lock)
        {
            if (_decoded[index] is { } decoded)
            {
                return decoded;
            }

            // a frame is drawn on top of the one it requires, which is decoded first
            var required = _frames[index].RequiredFrame;
            var prior = required >= 0 && required < index ? GetFrame(required) : null;

            using var data = SKData.CreateCopy(_bytes);
            using var codec = SKCodec.Create(data);
            using var bitmap = new SKBitmap(_info);

            if (codec is null)
            {
                return null;
            }

            if (prior is null || !prior.ReadPixels(_info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                bitmap.Erase(SKColors.Transparent);
                prior = null;
            }

            var options = prior is null ? new SKCodecOptions(index) : new SKCodecOptions(index, required);
            var result = codec.GetPixels(_info, bitmap.GetPixels(), options);

            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                return index > 0 ? GetFrame(index - 1) : null;
            }

            // a copy of the pixels, as the bitmap goes away
            return _decoded[index] = SKImage.FromBitmap(bitmap);
        }
    }

    // only GIFs and WebPs have several frames that Skia decodes
    private static bool MayBeAnimated(byte[] bytes) =>
        bytes.Length > 12 && (
            bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' ||
            bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P');
}
