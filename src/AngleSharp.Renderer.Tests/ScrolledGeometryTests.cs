using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// Geometry in the viewport follows the page's scroll position: the page moves, fixed boxes stay,
/// and sticky boxes stick, together with their content.
/// </summary>
public sealed class ScrolledGeometryTests
{
    private const string Page = """
        <html>
          <head>
            <style>
              html, body { margin: 0; padding: 0; }
              #header { position: sticky; top: 0; height: 40px; }
              #link { display: block; margin-left: 10px; width: 50px; height: 20px; }
              #content { height: 2000px; }
              #box { position: absolute; top: 500px; left: 0; width: 40px; height: 40px; }
              #fixed { position: fixed; bottom: 10px; right: 10px; width: 30px; height: 30px; }
              #inner { display: block; width: 10px; height: 10px; }
            </style>
          </head>
          <body>
            <div id="intro" style="height: 100px">Intro</div>
            <div id="header"><a id="link" href="#">Home</a></div>
            <div id="content"></div>
            <div id="box"></div>
            <div id="fixed"><span id="inner"></span></div>
          </body>
        </html>
        """;

    [Theory]
    [InlineData("position: fixed; bottom: 10px; right: 10px", 400d - 10d - 60d, 300d - 10d - 40d)]
    [InlineData("position: absolute; bottom: 0; left: 0", 0d, 1000d - 40d)]
    public async Task BoxesOfAnAutoHeightArePlacedByTheirBottom(string style, double left, double top)
    {
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; } #outer { position: relative; height: 1000px; } b { display: block; height: 20px; }</style></head>
              <body><div id="outer"><div id="toast" style="{{style}}; width: 60px"><b></b><b></b></div></div></body>
            </html>
            """);

        var rect = document.GetElementById("toast")!.GetBoundingClientRect();

        Assert.Equal(40d, rect.Height, 3);
        Assert.Equal(left, rect.Left, 3);
        Assert.Equal(top, rect.Top, 3);
    }

    [Theory]
    [InlineData("display: block")]
    [InlineData("display: flex; flex-direction: column")]
    public async Task ScrollContainersMoveTheirContent(string display)
    {
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; } #list { {{display}}; overflow-y: auto; height: 100px; margin-top: 20px; } #list > div { height: 30px; flex: none; }</style></head>
              <body><div id="list">{{string.Concat(Enumerable.Range(0, 10).Select(i => $"<div id=\"item{i}\">Item {i}</div>"))}}</div></body>
            </html>
            """);
        var list = document.GetElementById("list")!;

        list.SetScrollTop(60);

        Assert.Equal(60d, list.GetScrollTop());
        Assert.Equal(300, list.GetScrollHeight());
        Assert.Equal(20d, document.GetElementById("item2")!.GetBoundingClientRect().Top, 3);
        Assert.Equal(20d, list.GetBoundingClientRect().Top, 3);

        list.SetScrollTop(1000);
        Assert.Equal(200d, list.GetScrollTop());

        document.GetElementById("item5")!.ScrollIntoView();
        Assert.Equal(150d, list.GetScrollTop());
        Assert.Equal(20d, document.GetElementById("item5")!.GetBoundingClientRect().Top, 3);

        var hit = document.Context.GetDomHarness().PrepareFrame().HitTestMap.HitTest(5, 25);
        Assert.Equal("item5", hit!.Value.Element.Id);
    }

    [Fact]
    public async Task NestedScrollContainersOnlyAddTheirBoxToTheScrollableOverflow()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; } #outer { overflow: auto; height: 100px; } #inner { overflow: auto; height: 150px; }</style></head>
              <body><div id="outer"><div id="inner"><div style="height: 1000px"></div></div><div style="position: fixed; top: 0; height: 500px"></div></div></body>
            </html>
            """);

        Assert.Equal(150, document.GetElementById("outer")!.GetScrollHeight());
        Assert.Equal(1000, document.GetElementById("inner")!.GetScrollHeight());
    }

    [Fact]
    public async Task ScrollContainersClipAndScrollTheirContentBehindThem()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; } #list { overflow-y: auto; height: 100px; } #red { position: relative; z-index: -1; top: 150px; height: 30px; background: rgb(255, 0, 0); }</style></head>
              <body><div id="list"><div id="red"></div><div style="height: 300px"></div></div></body>
            </html>
            """);
        var harness = document.Context.GetDomHarness();

        var before = harness.PrepareFrame().Rasterize();
        document.GetElementById("list")!.SetScrollTop(150);
        var after = harness.PrepareFrame().Rasterize();

        // RGBA: clipped at (5, 155) before, scrolled to the top of the list after
        Assert.Equal((255, 255), (before.Data[(155 * before.RowBytes) + 20], before.Data[(155 * before.RowBytes) + 21]));
        Assert.Equal((255, 0), (after.Data[(5 * after.RowBytes) + 20], after.Data[(5 * after.RowBytes) + 21]));
    }

    [Fact]
    public async Task ScrollContainersPaintTheirContentScrolled()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; } #list { overflow-y: auto; height: 100px; } #list > div { height: 30px; } #red { background: rgb(255, 0, 0); }</style></head>
              <body><div id="list"><div></div><div></div><div></div><div></div><div></div><div id="red"></div><div></div></div></body>
            </html>
            """);
        var harness = document.Context.GetDomHarness();

        var before = harness.PrepareFrame().Rasterize();
        document.GetElementById("list")!.SetScrollTop(150);
        var after = harness.PrepareFrame().Rasterize();

        // RGBA at (5, 45): white before, the red item (at 150, the content scrolls by at most 110) after
        var pixel = (45 * after.RowBytes) + (5 * 4);
        Assert.Equal((255, 255), (before.Data[pixel], before.Data[pixel + 1]));
        Assert.Equal((255, 0), (after.Data[pixel], after.Data[pixel + 1]));
    }

    [Fact]
    public async Task ThePageMovesWithTheScrollPosition()
    {
        var document = await ParseAsync(Page);
        document.DocumentElement!.SetScrollTop(300);

        Assert.Equal(200d, document.GetElementById("box")!.GetBoundingClientRect().Top, 3);
        Assert.Equal(-300d, document.GetElementById("intro")!.GetBoundingClientRect().Top, 3);
    }

    [Fact]
    public async Task FixedBoxesStayWhereTheyAre()
    {
        var document = await ParseAsync(Page);
        var top = document.GetElementById("inner")!.GetBoundingClientRect().Top;

        document.DocumentElement!.SetScrollTop(300);

        Assert.Equal(260d, document.GetElementById("fixed")!.GetBoundingClientRect().Top, 3);
        Assert.Equal(top, document.GetElementById("inner")!.GetBoundingClientRect().Top, 3);
    }

    [Fact]
    public async Task StickyBoxesStickWithTheirContent()
    {
        var document = await ParseAsync(Page);
        Assert.Equal(100d, document.GetElementById("link")!.GetBoundingClientRect().Top, 3);

        document.DocumentElement!.SetScrollTop(300);

        Assert.Equal(0d, document.GetElementById("header")!.GetBoundingClientRect().Top, 3);
        var link = document.GetElementById("link")!.GetBoundingClientRect();
        Assert.Equal(0d, link.Top, 3);
        Assert.Equal(10d, link.Left, 3);
    }

    [Fact]
    public async Task ContentOfAStuckStickyBoxIsHitWhereItIsPainted()
    {
        var document = await ParseAsync(Page);
        document.DocumentElement!.SetScrollTop(300);

        var frame = document.Context.GetDomHarness().PrepareFrame();
        var hit = frame.HitTestMap.HitTest(20, 10);

        Assert.NotNull(hit);
        Assert.Equal("link", hit!.Value.Element.Id);
    }

    [Fact]
    public async Task CaretPositionsAreLookedUpInTheViewport()
    {
        var document = await ParseAsync(Page);
        document.DocumentElement!.SetScrollTop(300);

        var caret = document.CaretPositionFromPoint(20, 220);

        Assert.NotNull(caret);
        Assert.Equal("box", (caret!.OffsetNode as IElement)?.Id);
    }

    [Fact]
    public async Task TheLayoutOfAScrolledFrameGivesTheGeometryOfAFullLayout()
    {
        var document = await ParseAsync(Page);
        var harness = document.Context.GetDomHarness();
        document.DocumentElement!.SetScrollTop(300);
        string[] ids = ["intro", "header", "link", "content", "box", "fixed", "inner"];

        harness.PrepareFrame();
        var fromFrame = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();
        harness.PaintToPng();
        var fromCapture = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();

        Assert.Equal(fromCapture, fromFrame);
    }

    [Fact]
    public async Task ChangesAfterAScrolledFrameGiveTheGeometryOfAFullLayout()
    {
        var document = await ParseAsync(Page);
        var harness = document.Context.GetDomHarness();
        document.DocumentElement!.SetScrollTop(300);
        harness.PrepareFrame();

        document.GetElementById("box")!.SetAttribute("style", "top: 650px; width: 80px");
        var incremental = Describe(document.GetElementById("box")!);
        harness.PaintToPng();

        Assert.Equal("0.000,350.000,80.000,40.000", incremental);
        Assert.Equal(Describe(document.GetElementById("box")!), incremental);
    }

    private static string Describe(IElement element)
    {
        var rect = element.GetBoundingClientRect();
        return FormattableString.Invariant($"{rect.Left:F3},{rect.Top:F3},{rect.Width:F3},{rect.Height:F3}");
    }

    private static async Task<IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice
            {
                ViewPortWidth = 400,
                ViewPortHeight = 300,
                DeviceWidth = 400,
                DeviceHeight = 300,
                FontSize = 16,
            }));
        return await context.OpenAsync(request => request.Content(html));
    }
}
