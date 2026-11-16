using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// An image of several frames (an animated GIF) shows the frame its time has come to, without a
/// layout: rasterizing the same frame again later shows the next one.
/// </summary>
public sealed class AnimatedImageTests
{
    [Fact]
    public async Task AnimatedGifShowsItsFramesInTurn()
    {
        // red for 100 ms, then blue for 2 s
        var gif = CreateGif(loop: false, (0, 10), (1, 200));
        var harness = await OpenAsync($"""<img src="data:image/gif;base64,{Convert.ToBase64String(gif)}" style="width:40px;height:40px">""");

        var frame = harness.PrepareFrame();
        var first = PixelAt(frame.Rasterize(), 20, 20);
        var remaining = frame.GetTimeToNextImageFrame();

        Assert.NotNull(remaining);
        Assert.InRange(remaining.Value, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        Assert.Equal(((byte)255, (byte)0), first);

        var second = first;
        while (second == first)
        {
            var timeToNext = frame.GetTimeToNextImageFrame();
            if (timeToNext is null)
            {
                second = PixelAt(frame.Rasterize(), 20, 20);
                break;
            }

            await Task.Delay(timeToNext.Value);
            second = PixelAt(frame.Rasterize(), 20, 20);
        }

        Assert.Equal(((byte)0, (byte)255), second);

        if (frame.GetTimeToNextImageFrame() is { } nextFrameDelay)
        {
            Assert.InRange(nextFrameDelay, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task GifPlayedOnceStopsAtItsLastFrame()
    {
        // without the loop extension it plays once: red for 50 ms, then blue
        var gif = CreateGif(loop: false, (0, 5), (1, 5));
        var harness = await OpenAsync($"""<img src="data:image/gif;base64,{Convert.ToBase64String(gif)}" style="width:40px;height:40px">""");
        var frame = harness.PrepareFrame();
        frame.Rasterize();

        await Task.Delay(400);

        Assert.Null(frame.GetTimeToNextImageFrame());
        Assert.Equal(((byte)0, (byte)255), PixelAt(frame.Rasterize(), 20, 20));
    }

    [Fact]
    public async Task StillImagesDoNotChange()
    {
        var gif = CreateGif(loop: true, (0, 10));
        var harness = await OpenAsync($"""<img src="data:image/gif;base64,{Convert.ToBase64String(gif)}" style="width:40px;height:40px">""");

        var frame = harness.PrepareFrame();

        Assert.Null(frame.GetTimeToNextImageFrame());
        Assert.Equal(((byte)255, (byte)0), PixelAt(frame.Rasterize(), 20, 20));
    }

    [Fact]
    public async Task AnimatedImagesOutOfViewDoNotChangeTheFrame()
    {
        var gif = CreateGif(loop: true, (0, 10), (1, 10));
        var harness = await OpenAsync($"""<div style="height:3000px"></div><img src="data:image/gif;base64,{Convert.ToBase64String(gif)}" style="width:40px;height:40px">""");

        var frame = harness.PrepareFrame();

        Assert.Null(frame.GetTimeToNextImageFrame());
    }

    private static (byte R, byte B) PixelAt(Rendering.RenderedPixels pixels, int x, int y)
    {
        var offset = ((y + 0) * pixels.RowBytes) + (x * 4);
        return (pixels.Data[offset], pixels.Data[offset + 2]);
    }

    /// <summary>
    /// A GIF of 1x1 pixel frames, each of a color of the palette (0 red, 1 blue) shown for the given
    /// hundredths of a second.
    /// </summary>
    private static byte[] CreateGif(bool loop, params (int Color, int Delay)[] frames)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        // 1x1, a global palette of 2 colors
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0]);
        bytes.AddRange([255, 0, 0, 0, 0, 255]);

        if (loop)
        {
            bytes.AddRange([0x21, 0xFF, 0x0B]);
            bytes.AddRange("NETSCAPE2.0"u8.ToArray());
            bytes.AddRange([3, 1, 0, 0, 0]);
        }

        foreach (var (color, delay) in frames)
        {
            bytes.AddRange([0x21, 0xF9, 4, 0, (byte)delay, 0, 0, 0]);
            bytes.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]);
            // LZW of 2 bit codes (3 bit wide): clear, the color, end
            bytes.AddRange([2, 2, color == 0 ? (byte)0x44 : (byte)0x4C, 1, 0]);
        }

        bytes.Add(0x3B);
        return [.. bytes];
    }

    private static async Task<IDomHarness> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice
            {
                ViewPortWidth = 200,
                ViewPortHeight = 200,
                DeviceWidth = 200,
                DeviceHeight = 200,
                FontSize = 16,
            }));
        var document = await context.OpenAsync(request => request.Content($"<html><body style=\"margin:0\">{body}</body></html>"));
        return document.Context.GetDomHarness();
    }
}
