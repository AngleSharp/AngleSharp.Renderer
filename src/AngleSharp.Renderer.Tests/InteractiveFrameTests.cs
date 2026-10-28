namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

/// <summary>
/// Interactive frames (<see cref="IDomHarness.PrepareFrame"/>): raw pixels, overscan, and the
/// hit-test map laid out along with them.
/// </summary>
public sealed class InteractiveFrameTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    [Fact]
    public async Task Frame_RasterizesTheViewportIntoPixels()
    {
        var (_, harness) = await OpenAsync("""<div style="width: 50px; height: 50px; background: #00ff00"></div>""");

        var pixels = harness.PrepareFrame().Rasterize();

        Assert.Equal(300, pixels.Width);
        Assert.Equal(200, pixels.Height);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.Data[..4]);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixels.Data[(299 * 4)..(300 * 4)]);
    }

    [Fact]
    public async Task Frame_PaintsOverscanOncePageIsKnownToScrollAsAWhole()
    {
        var (document, harness) = await OpenAsync("""<div style="height: 1000px"></div><div id="end" style="height: 20px; background: #0000ff"></div>""");
        harness.SetScrollTop(document.DocumentElement, 900, 1020);

        var first = harness.PrepareFrame(100);
        var second = harness.PrepareFrame(100);
        var pixels = second.Rasterize();

        Assert.Equal(0, first.OverscanTop);
        Assert.True(second.IsScrollShiftable);
        Assert.Equal(100, second.OverscanTop);
        Assert.Equal(400, second.Height);
        Assert.Equal(1020, second.ContentHeight, precision: 0);
        // the blue box at 1000 is 100px below the scrolled viewport's top, i.e., at row 200
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Data[(200 * 300 * 4)..(200 * 300 * 4 + 4)]);
        Assert.Equal("end", second.HitTestMap.HitTest(10, 105)?.Element.Id);
    }

    [Fact]
    public async Task Frame_RepositionsWithoutLayout()
    {
        var (_, harness) = await OpenAsync("""<div style="height: 500px"></div><div id="box" style="height: 20px; background: #0000ff"></div><div style="height: 1000px"></div>""");
        var frame = harness.PrepareFrame();

        var moved = frame.Reposition(400);
        var pixels = moved.Rasterize();

        Assert.Equal(400, moved.ScrollTop);
        Assert.Equal("box", moved.HitTestMap.HitTest(10, 110)?.Element.Id);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Data[(110 * 300 * 4)..(110 * 300 * 4 + 4)]);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixels.Data[(90 * 300 * 4)..(90 * 300 * 4 + 4)]);
    }

    [Fact]
    public async Task Frame_RepositionsAroundFixedBoxes()
    {
        var (_, harness) = await OpenAsync("""
            <div id="bar" style="position: fixed; top: 0; left: 0; width: 300px; height: 20px; background: #ff0000"></div>
            <div style="height: 500px"></div><div id="box" style="height: 20px; background: #0000ff"></div><div style="height: 1000px"></div>
            """);
        var frame = harness.PrepareFrame();

        var moved = frame.Reposition(400);
        var pixels = moved.Rasterize();

        Assert.True(frame.CanReposition);
        Assert.Equal("bar", moved.HitTestMap.HitTest(10, 10)?.Element.Id);
        Assert.Equal("box", moved.HitTestMap.HitTest(10, 110)?.Element.Id);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.Data[(10 * 300 * 4)..(10 * 300 * 4 + 4)]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Data[(110 * 300 * 4)..(110 * 300 * 4 + 4)]);
    }

    [Fact]
    public async Task Frame_WithStickyBoxCannotReposition()
    {
        var (_, harness) = await OpenAsync("""<div style="position: sticky; top: 0; height: 10px"></div><div style="height: 1000px"></div>""");

        Assert.False(harness.PrepareFrame().CanReposition);
    }

    [Fact]
    public async Task Frame_WithFixedBoxIsNotShiftable()
    {
        var (_, harness) = await OpenAsync("""<div style="position: fixed; top: 0; width: 10px; height: 10px"></div><div style="height: 1000px"></div>""");

        harness.PrepareFrame(100);
        var frame = harness.PrepareFrame(100);

        Assert.False(frame.IsScrollShiftable);
        Assert.Equal(0, frame.OverscanTop);
    }

    [Fact]
    public async Task HitTestMap_FollowsPaintingOrderAndClips()
    {
        var (_, harness) = await OpenAsync("""
            <div id="below" style="position: absolute; z-index: 2; left: 0; top: 0; width: 100px; height: 100px"></div>
            <div id="above" style="position: absolute; z-index: 3; left: 50px; top: 50px; width: 100px; height: 100px; cursor: pointer"></div>
            <div id="clip" style="position: absolute; left: 200px; top: 0; width: 50px; height: 50px; overflow: hidden">
                <div id="inner" style="width: 100px; height: 100px"></div>
            </div>
            <div id="scroller" style="position: absolute; left: 0; top: 150px; width: 50px; height: 30px; overflow-y: auto">
                <div id="item" style="height: 100px"></div>
            </div>
            """);

        var map = harness.PrepareFrame().HitTestMap;

        Assert.Equal("below", map.HitTest(20, 20)?.Element.Id);
        Assert.Equal("above", map.HitTest(70, 70)?.Element.Id);
        Assert.Equal("pointer", map.HitTest(70, 70)?.Cursor);
        Assert.Equal("auto", map.HitTest(20, 20)?.Cursor);
        Assert.Equal("inner", map.HitTest(220, 20)?.Element.Id);
        Assert.NotEqual("inner", map.HitTest(220, 70)?.Element.Id);
        Assert.Equal("item", map.HitTest(10, 160)?.Element.Id);
        Assert.True(map.HitTest(10, 160)?.IsInScrollContainer);
        Assert.False(map.HitTest(20, 20)?.IsInScrollContainer);
    }

    [Fact]
    public async Task SetHoveredElement_AppliesHoverWithoutHitTesting()
    {
        var (document, harness) = await OpenAsync("""<div id="outer"><div id="inner" style="height: 20px"></div></div>""");
        var inner = document.GetElementById("inner")!;

        harness.SetHoveredElement(inner);

        Assert.Same(inner, harness.HoveredElement);
        Assert.True(inner.Matches(":hover"));
        Assert.True(document.GetElementById("outer")!.Matches(":hover"));
    }

    [Fact]
    public async Task Frame_ReportsRunningAnimations()
    {
        var (_, harness) = await OpenAsync("""
            <style>@keyframes spin { from { opacity: 0 } to { opacity: 1 } }</style>
            <div style="height: 20px; animation: spin 1s linear"></div>
            """);

        Assert.True(harness.PrepareFrame().IsAnimating);

        harness.AdvanceTime(TimeSpan.FromSeconds(2));

        Assert.False(harness.PrepareFrame().IsAnimating);
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string body)
    {
        var configuration = Configuration.Default.WithCss().WithRenderDevice(Device);
        var context = BrowsingContext.New(configuration);
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head><style>html, body { margin: 0; padding: 0; }</style></head>
            <body>{{body}}</body></html>
            """));
        return (document, document.Context.GetDomHarness());
    }
}
