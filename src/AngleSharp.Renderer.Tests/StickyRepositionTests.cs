using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// A frame with sticky boxes is moved to another scroll position without a layout: the sticky
/// boxes stick there as a layout would make them, also for hit testing.
/// </summary>
public sealed class StickyRepositionTests
{
    private const string Page = """
        <html><head><style>
          html, body { margin: 0 }
          #header { position: sticky; top: 0; height: 40px; background: rgb(0, 0, 255) }
          .section { height: 300px; background: rgb(240, 240, 240); border-top: 4px solid rgb(0, 128, 0) }
          .card { position: sticky; top: 50px; height: 60px; margin: 20px; background: rgb(255, 0, 0) }
          #bottom { position: sticky; bottom: 0; height: 30px; background: rgb(255, 200, 0) }
        </style></head><body>
          <div id="header">header</div>
          <div class="section"><div class="card" id="card1">one</div></div>
          <div class="section"><div class="card" id="card2">two</div></div>
          <div class="section"><div class="card" id="card3">three</div></div>
          <div class="section"></div>
          <div id="bottom">bottom</div>
          <div style="height: 600px"></div>
        </body></html>
        """;

    [Theory]
    [InlineData(0, 120)]
    [InlineData(0, 450)]
    [InlineData(200, 700)]
    [InlineData(900, 60)]
    public async Task RepositionedFramesShowStickyBoxesAsALayoutDoes(int from, int to)
    {
        var (document, harness) = await OpenAsync();
        var root = document.DocumentElement!;
        root.SetScrollTop(from);
        var frame = harness.PrepareFrame();

        Assert.True(frame.CanReposition);
        var moved = frame.Reposition(to);
        root.SetScrollTop(to);
        var laidOut = harness.PrepareFrame();

        Assert.Equal(to, laidOut.ScrollTop);
        Assert.Equal(0, Differences(moved.Rasterize(), laidOut.Rasterize()));

        for (var y = 5; y < 300; y += 10)
        {
            Assert.Equal(laidOut.HitTestMap.HitTest(100, y)?.Element, moved.HitTestMap.HitTest(100, y)?.Element);
        }
    }

    [Fact]
    public async Task RepositioningTwiceStartsFromTheLayout()
    {
        var (document, harness) = await OpenAsync();
        var frame = harness.PrepareFrame();

        var moved = frame.Reposition(300).Reposition(500);
        document.DocumentElement!.SetScrollTop(500);
        var laidOut = harness.PrepareFrame();

        Assert.Equal(0, Differences(moved.Rasterize(), laidOut.Rasterize()));
    }

    private static int Differences(RenderedPixels a, RenderedPixels b)
    {
        var count = 0;

        for (var i = 0; i < Math.Min(a.Data.Length, b.Data.Length); i += 4)
        {
            if (Math.Abs(a.Data[i] - b.Data[i]) > 2 || Math.Abs(a.Data[i + 1] - b.Data[i + 1]) > 2 || Math.Abs(a.Data[i + 2] - b.Data[i + 2]) > 2)
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync()
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 300, DeviceWidth = 300, DeviceHeight = 300, FontSize = 16 }));
        var document = await context.OpenAsync(request => request.Content(Page));
        return (document, document.Context.GetDomHarness());
    }
}
