using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// An audio element shows the controls of Chrome when it has the `controls` attribute, else
/// nothing; a host finds their parts to make them interactive.
/// </summary>
public sealed class MediaControlsTests
{
    [Fact]
    public async Task AudioWithoutControlsIsNotRendered()
    {
        var harness = await OpenAsync("""<audio src="a.wav">Fallback text</audio><div id="after" style="height:10px"></div>""");
        var document = harness.Context.Active!;

        Assert.Equal(0d, document.GetElementById("after")!.GetBoundingClientRect().Top);
        Assert.False(document.QuerySelector("audio")!.HasMediaControls());
    }

    [Fact]
    public async Task AudioWithControlsHasTheSizeOfChrome()
    {
        var harness = await OpenAsync("""<audio id="audio" controls>Fallback text</audio>""");
        var rect = harness.Context.Active!.GetElementById("audio")!.GetBoundingClientRect();

        Assert.Equal(300d, rect.Width);
        Assert.Equal(54d, rect.Height);
    }

    [Fact]
    public async Task AudioWithControlsCanBeSized()
    {
        var harness = await OpenAsync("""<audio id="audio" controls style="display:block;width:500px;height:40px"></audio>""");
        var rect = harness.Context.Active!.GetElementById("audio")!.GetBoundingClientRect();

        Assert.Equal(500d, rect.Width);
        Assert.Equal(40d, rect.Height);
    }

    [Fact]
    public async Task PausedControlsShowAPlayButtonAndDoNotChange()
    {
        var harness = await OpenAsync("""<audio controls style="display:block"></audio>""");
        var frame = harness.PrepareFrame();
        var pixels = frame.Rasterize();

        // the panel, the triangle of the play button and the timeline at its start
        Assert.Equal((241, 243, 244), PixelAt(pixels, 150, 4));
        Assert.True(PixelAt(pixels, 27, 27).R < 100);
        Assert.Null(frame.GetTimeToNextImageFrame());
    }

    [Fact]
    public async Task PartsOfTheControlsAreFound()
    {
        var harness = await OpenAsync("""<audio controls style="display:block"></audio>""");
        harness.PrepareFrame();
        var audio = harness.Context.Active!.QuerySelector("audio")!;

        Assert.True(audio.HasMediaControls());
        Assert.Equal(MediaControlPart.PlayPause, audio.GetMediaControlAt(28, 27));
        Assert.Equal(MediaControlPart.Mute, audio.GetMediaControlAt(272, 27));
        Assert.Equal(MediaControlPart.Timeline, audio.GetMediaControlAt(200, 27));
        Assert.Equal(MediaControlPart.None, audio.GetMediaControlAt(200, 3));
        Assert.Equal(MediaControlPart.None, audio.GetMediaControlAt(400, 27));
        Assert.Equal(0d, audio.GetMediaTimelinePosition(0));
        Assert.Equal(1d, audio.GetMediaTimelinePosition(299));
        Assert.InRange(audio.GetMediaTimelinePosition(200)!.Value, 0.1, 0.9);
    }

    [Fact]
    public async Task NarrowControlsLeaveOutTheTimeline()
    {
        var harness = await OpenAsync("""<audio controls style="display:block;width:120px"></audio>""");
        harness.PrepareFrame();
        var audio = harness.Context.Active!.QuerySelector("audio")!;

        Assert.Null(audio.GetMediaTimelinePosition(60));
        Assert.Equal(MediaControlPart.PlayPause, audio.GetMediaControlAt(28, 27));
    }

    private static (int R, int G, int B) PixelAt(Rendering.RenderedPixels pixels, int x, int y)
    {
        var offset = (y * pixels.RowBytes) + (x * 4);
        return (pixels.Data[offset], pixels.Data[offset + 1], pixels.Data[offset + 2]);
    }

    private static async Task<IDomHarness> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice
            {
                ViewPortWidth = 400,
                ViewPortHeight = 200,
                DeviceWidth = 400,
                DeviceHeight = 200,
                FontSize = 16,
            }));
        var document = await context.OpenAsync(request => request.Content($"<html><body style=\"margin:0\">{body}</body></html>"));
        return document.Context.GetDomHarness();
    }
}
