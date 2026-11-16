using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// An animation or transition changing only the transform or the opacity of a box brings the last
/// frame to the time now without a layout, painting the box under its new values; the frame looks
/// like a full layout at that time would.
/// </summary>
public sealed class CompositedAnimationTests
{
    [Theory]
    [InlineData("@keyframes spin { to { transform: rotate(360deg) } } #box { animation: spin 2s linear infinite }")]
    [InlineData("@keyframes fade { from { opacity: 0.2 } to { opacity: 0.9 } } #box { animation: fade 2s linear infinite alternate }")]
    [InlineData("@keyframes slide { from { transform: translateX(5px) } to { transform: translateX(80px) } } #box { animation: slide 2s ease-in-out infinite }")]
    [InlineData("#box { filter: blur(1px); transition: filter 2s linear } #box.on { filter: blur(6px) }")]
    [InlineData("@keyframes spin { to { transform: rotate(360deg) } } #box::after { content: ''; position: absolute; inset: 10px; background: rgb(0, 0, 255); animation: spin 2s linear infinite }")]
    public async Task AnimationFramesNeedNoLayoutAndLookLikeOne(string css)
    {
        var (document, harness) = await OpenAsync(css);
        var last = harness.PrepareFrame();

        if (css.Contains(".on"))
        {
            document.GetElementById("box")!.ClassList.Add("on");
            last = harness.UpdateFrame() ?? harness.PrepareFrame();
        }

        Assert.True(last.IsAnimating, "first frame animating");

        for (var tick = 1; tick <= 5; tick++)
        {
            harness.AdvanceTime(TimeSpan.FromMilliseconds(130));
            var patched = harness.UpdateFrame();

            Assert.NotNull(patched);
            Assert.True(patched!.IsAnimating, $"tick {tick} animating");
            Assert.True(patched.Metrics.IsIncremental, $"tick {tick} incremental, same frame: {ReferenceEquals(patched, last)}");
            var pixels = patched.Rasterize();
            last = harness.PrepareFrame();
            var full = last.Rasterize();
            Assert.True(Differences(pixels, full) == 0, $"tick {tick}: {Differences(pixels, full)} pixels differ from a layout");
        }
    }

    [Fact]
    public async Task AnimationFramesPaintTheBoxOnTheWay()
    {
        var (document, harness) = await OpenAsync("@keyframes slide { from { transform: translateX(0px) } to { transform: translateX(100px) } } #box { animation: slide 1s linear forwards; transform: translateX(1px) }");
        var before = harness.PrepareFrame();
        var y = (int)document.GetElementById("box")!.GetBoundingClientRect().Top + 30;
        var start = Color(before.Rasterize(), 15, y);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        var halfway = harness.UpdateFrame()!.Rasterize();

        Assert.Equal(((byte)255, (byte)0, (byte)0), start);
        Assert.Equal(((byte)255, (byte)255, (byte)255), Color(halfway, 15, y));
        Assert.Equal(((byte)255, (byte)0, (byte)0), Color(halfway, 15 + 50, y));
    }

    [Theory]
    [InlineData("@keyframes spin { to { transform: rotate(360deg) } }", "rotate(90deg)")]
    [InlineData("@keyframes spin { from { transform: rotate(40deg) } }", "rotate(30deg)")]
    [InlineData("@keyframes spin { 50% { transform: rotate(80deg) } }", "rotate(40deg)")]
    public async Task KeyframesMissingAtAnEndAnimateFromTheElementsOwnValue(string keyframes, string expected)
    {
        var (document, harness) = await OpenAsync(keyframes + " #box { animation: spin 2s linear infinite }");
        harness.PrepareFrame();

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));

        Assert.Equal(expected, harness.GetAnimatedValue(document.GetElementById("box")!, "transform"));
    }

    [Fact]
    public async Task AnAnimationOfTheLayoutLaysItOutAgain()
    {
        var (_, harness) = await OpenAsync("@keyframes grow { from { width: 40px } to { width: 120px } } #box { animation: grow 2s linear infinite }");
        harness.PrepareFrame();

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));

        Assert.Null(harness.UpdateFrame());
    }

    [Fact]
    public async Task AFinishedAnimationEndsTheFramesToCome()
    {
        var (_, harness) = await OpenAsync("@keyframes spin { to { transform: rotate(90deg) } } #box { animation: spin 300ms linear; transform: rotate(10deg) }");
        harness.PrepareFrame();

        harness.AdvanceTime(TimeSpan.FromMilliseconds(400));
        var frame = harness.UpdateFrame();
        var full = harness.PrepareFrame();

        Assert.NotNull(frame);
        Assert.False(frame!.IsAnimating);
        Assert.Equal(0, Differences(frame.Rasterize(), full.Rasterize()));
    }

    [Fact]
    public async Task ATransitionOfTheTransformNeedsNoLayout()
    {
        var (document, harness) = await OpenAsync("#box { transform: translateX(2px); transition: transform 1s linear } #box.moved { transform: translateX(60px) }");
        harness.PrepareFrame();

        document.GetElementById("box")!.ClassList.Add("moved");
        harness.UpdateFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(400));
        var frame = harness.UpdateFrame();
        var full = harness.PrepareFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.Metrics.IsIncremental);
        Assert.Equal(0, Differences(frame.Rasterize(), full.Rasterize()));
    }

    private static int Differences(RenderedPixels a, RenderedPixels b)
    {
        var count = 0;

        for (var i = 0; i < a.Data.Length; i += 4)
        {
            if (Math.Abs(a.Data[i] - b.Data[i]) > 2 || Math.Abs(a.Data[i + 1] - b.Data[i + 1]) > 2 || Math.Abs(a.Data[i + 2] - b.Data[i + 2]) > 2)
            {
                count++;
            }
        }

        return count;
    }

    private static (byte R, byte G, byte B) Color(RenderedPixels pixels, int x, int y)
    {
        var offset = (y * pixels.RowBytes) + (x * 4);
        return (pixels.Data[offset], pixels.Data[offset + 1], pixels.Data[offset + 2]);
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string css)
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, DeviceWidth = 300, DeviceHeight = 200, FontSize = 16 }));
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head><style>
              html, body { margin: 0 }
              #box { position: relative; margin: 10px; width: 60px; height: 60px; background: rgb(255, 0, 0) }
              {{css}}
            </style></head><body><p>text before</p><div id="box"></div><p>text after</p></body></html>
            """));
        return (document, document.Context.GetDomHarness());
    }
}
