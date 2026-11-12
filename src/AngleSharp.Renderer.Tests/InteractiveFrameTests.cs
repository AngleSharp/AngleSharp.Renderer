namespace AngleSharp.Renderer.Tests;

using System.Runtime.ExceptionServices;

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

    [Theory]
    [InlineData("auto")]
    [InlineData("scroll")]
    [InlineData("hidden")]
    public async Task Frame_ContentHeightLeavesOutWhatABoxClips(string overflow)
    {
        var (document, harness) = await OpenAsync($"""<div style="height: 100px; overflow: {overflow}"><div style="height: 5000px"></div></div>""");

        var frame = harness.PrepareFrame();

        Assert.Equal(200, frame.ContentHeight, precision: 0);
        Assert.Equal(document.DocumentElement.GetScrollHeight(), frame.ContentHeight, precision: 0);
    }

    [Fact]
    public async Task Frame_ContentHeightCountsAClippingBoxItself()
    {
        var (_, harness) = await OpenAsync("""<div style="height: 1000px; overflow: auto"><div style="height: 5000px"></div></div>""");

        Assert.Equal(1000, harness.PrepareFrame().ContentHeight, precision: 0);
    }

    [Fact]
    public async Task Frame_ContentHeightCountsAPositionedBoxEscapingAClip()
    {
        // the containing block of the absolute box is the page, so the auto box does not clip it
        var (_, harness) = await OpenAsync("""<div style="height: 100px; overflow: auto"><div style="position: absolute; top: 700px; height: 300px; width: 10px"></div></div>""");

        Assert.Equal(1000, harness.PrepareFrame().ContentHeight, precision: 0);
    }

    [Fact]
    public async Task ScrollHeight_OfThePageCountsAPositionedBoxEscapingAClipLikeTheFrame()
    {
        var (document, harness) = await OpenAsync("""<div style="height: 100px; overflow: auto"><div style="position: absolute; top: 700px; height: 300px; width: 10px"></div></div>""");

        Assert.Equal(1000, document.DocumentElement.GetScrollHeight());
        Assert.Equal(document.DocumentElement.GetScrollHeight(), harness.PrepareFrame().ContentHeight, precision: 0);
    }

    [Fact]
    public async Task ScrollHeight_OfABoxLeavesOutAPositionedBoxEscapingIt()
    {
        var (document, _) = await OpenAsync("""<div id="box" style="height: 100px; overflow: auto"><div style="position: absolute; top: 700px; height: 300px; width: 10px"></div></div>""");

        Assert.Equal(100, document.GetElementById("box")!.GetScrollHeight());
    }

    [Fact]
    public async Task ScrollHeight_OfABoxCountsAPositionedBoxItContains()
    {
        var (document, _) = await OpenAsync("""<div id="box" style="position: relative; height: 100px; overflow: auto"><div style="position: absolute; top: 700px; height: 300px; width: 10px"></div></div>""");

        Assert.Equal(1000, document.GetElementById("box")!.GetScrollHeight());
    }

    [Fact]
    public async Task ScrollHeight_OfABoxLeavesOutWhatABoxInsideClips()
    {
        var (document, _) = await OpenAsync("""<div id="box" style="height: 100px; overflow: auto"><div style="height: 50px; overflow: hidden"><div style="height: 5000px"></div></div></div>""");

        Assert.Equal(100, document.GetElementById("box")!.GetScrollHeight());
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

    [Fact]
    public async Task HitTestMap_FindsTransformedBoxesWhereTheyArePainted()
    {
        var (_, harness) = await OpenAsync("""
            <div id="moved" style="width: 40px; height: 40px; transform: translate(50px, 50px)"><div id="inner" style="width: 10px; height: 10px; transform: translate(20px, 0)"></div></div>
            <div style="height: 60px"></div>
            <div id="scaled" style="width: 40px; height: 40px; transform: scale(3); transform-origin: 0 0"></div>
            """);

        var frame = harness.PrepareFrame(0);

        Assert.Equal("moved", frame.HitTestMap.HitTest(60, 80)?.Element.Id);
        Assert.Equal("inner", frame.HitTestMap.HitTest(75, 55)?.Element.Id);
        Assert.NotEqual("moved", frame.HitTestMap.HitTest(10, 10)?.Element.Id);
        Assert.Equal("scaled", frame.HitTestMap.HitTest(100, 150)?.Element.Id);
    }

    [Fact]
    public async Task Frame_TellsWhatItsLayoutTook()
    {
        var (_, harness) = await OpenAsync("""<div style="display: flex"><p style="flex: 1">A text</p><input placeholder="Search"></div><div style="width: 10px; height: 10px"></div>""");

        var metrics = harness.PrepareFrame().Metrics;

        Assert.False(metrics.IsIncremental);
        Assert.True(metrics.Style > TimeSpan.Zero);
        Assert.True(metrics.Layout > TimeSpan.Zero);
        Assert.Equal(metrics.Setup + metrics.Style + metrics.Layout + metrics.HitTesting + metrics.Subframes, metrics.Total);
        // html, head, style, body, the flex container with its two items, and the box
        Assert.Equal(8, metrics.Elements);
        Assert.True(metrics.Boxes >= 6);
        Assert.True(metrics.TrialLayouts > 0);
        Assert.True(metrics.StyleMaps >= metrics.Boxes);
        Assert.True(metrics.TextMeasurements > 0);
        Assert.True(metrics.Commands > 0);
        Assert.True(metrics.HitTestEntries > 0);
        Assert.True(metrics.AllocatedBytes > 0);
    }

    [Fact]
    public async Task Frame_MovedToAnotherScrollPositionNeededNoLayout()
    {
        var (_, harness) = await OpenAsync("""<div style="height: 1000px"></div>""");

        var frame = harness.PrepareFrame(100).Reposition(50);

        Assert.Same(Rendering.RenderMetrics.None, frame.Metrics);
        Assert.Equal(TimeSpan.Zero, frame.Metrics.Total);
    }

    [Fact]
    public async Task Frame_HitsAnInlineBoxOnSeveralLinesOnlyOnItsLines()
    {
        var (document, harness) = await OpenAsync("""<div id="box" style="width: 200px">Some words ahead of <a id="wrapped" href="#">a link over lines</a> <a id="next" href="#">next</a></div>""");

        var frame = harness.PrepareFrame();
        var wrapped = document.GetElementById("wrapped")!;
        var next = document.GetElementById("next")!.GetBoundingClientRect();
        var rects = wrapped.GetClientRects();

        Assert.Equal(2, rects.Length);
        var (first, second) = (rects.Item(0)!, rects.Item(1)!);
        Assert.True(second.Top >= first.Bottom - 1);
        Assert.True(next.Left > second.Right);
        // left of where the link starts on its first line and right of where it ends on the second
        Assert.Equal("box", frame.HitTestMap.HitTest(first.Left / 2, (first.Top + first.Bottom) / 2)?.Element.Id);
        Assert.Equal("next", frame.HitTestMap.HitTest((next.Left + next.Right) / 2, (next.Top + next.Bottom) / 2)?.Element.Id);
        Assert.Equal("wrapped", frame.HitTestMap.HitTest((first.Left + first.Right) / 2, (first.Top + first.Bottom) / 2)?.Element.Id);
        Assert.Equal("wrapped", frame.HitTestMap.HitTest((second.Left + second.Right) / 2, (second.Top + second.Bottom) / 2)?.Element.Id);
        Assert.Equal("box", document.ElementFromPoint(first.Left / 2, (first.Top + first.Bottom) / 2)?.Id);
    }

    [Theory]
    [InlineData("<table><tr><td>", "</td></tr></table>")]
    [InlineData("<div style='display: inline-block'>", "</div>")]
    public async Task NestedTrialLayouts_GrowLinearlyWithTheDepth(string open, string close)
    {
        // Every level measures its content with a trial layout; were each level to lay out the
        // levels below it again, doubling the depth would multiply the trials by about 2^6.
        var shallow = await CountTrialLayoutsAsync(open, close, depth: 6);
        var deep = await CountTrialLayoutsAsync(open, close, depth: 12);

        Assert.True(deep <= 3 * shallow, $"{shallow} trial layouts at depth 6, {deep} at depth 12");
    }

    private const int NestedLayoutStackSize = 16 * 1024 * 1024;

    private static async Task<int> CountTrialLayoutsAsync(string open, string close, int depth)
    {
        var body = string.Concat(Enumerable.Repeat(open, depth)) + "leaf" + string.Concat(Enumerable.Repeat(close, depth));
        var (_, harness) = await OpenAsync(body);
        var trials = 0;
        Exception? error = null;
        // Laid out only a few times, the layout stays in the first JIT tier, whose frames are
        // large: 12 levels need more stack than test threads have on some platforms (macOS).
        var thread = new Thread(() =>
        {
            try
            {
                trials = harness.PrepareFrame().Metrics.TrialLayouts;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, NestedLayoutStackSize);

        thread.Start();
        thread.Join();

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return trials;
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
