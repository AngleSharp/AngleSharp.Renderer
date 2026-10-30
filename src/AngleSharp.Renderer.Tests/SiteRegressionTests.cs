namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

/// <summary>
/// Reduced cases of layout bugs found on real sites, checked through the layout metrics.
/// </summary>
public sealed class SiteRegressionTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };

    [Fact]
    public async Task GridRow_FitsAnInlineItemLaidOutAsBlock()
    {
        // w3schools.com "All languages": <a> cards in a grid were measured as inline boxes, so
        // the rows were too short (or too tall) for the wrapped text of the blockified cards
        var document = await OpenAsync("""
            <div style="display: grid; grid-template-columns: 200px">
              <a id="item" style="padding: 10px"><h3 style="margin: 0; font-size: 20px; line-height: 24px">Title</h3>
                <p id="text" style="margin: 0; font-size: 16px; line-height: 20px">Some longer text that wraps across two or more lines here</p></a>
            </div>
            <div id="next" style="height: 10px"></div>
            """);

        var item = document.GetElementById("item")!.GetBoundingClientRect();
        var text = document.GetElementById("text")!.GetBoundingClientRect();
        var next = document.GetElementById("next")!.GetBoundingClientRect();

        Assert.True(text.Height >= 40, $"The text should wrap, but is {text.Height}px tall.");
        Assert.Equal(text.Bottom + 10, item.Bottom, precision: 0);
        Assert.Equal(item.Bottom, next.Top, precision: 0);
    }

    [Fact]
    public async Task InlineSvg_KeepsWithinMaxSizeAndRatio()
    {
        // w3schools.com language icons: a viewBox-only SVG with auto size and max-width/height 44px
        var document = await OpenAsync("""
            <span style="display: inline-flex; align-items: center; width: 44px; height: 44px">
              <svg id="icon" viewBox="0 0 64 32" style="display: block; width: auto; height: auto; max-width: 44px; max-height: 44px"><rect width="64" height="32"/></svg>
            </span>
            """);

        var icon = document.GetElementById("icon")!.GetBoundingClientRect();

        Assert.Equal(44, icon.Width, precision: 0);
        Assert.Equal(22, icon.Height, precision: 0);
    }

    [Fact]
    public async Task Image_MinWidthGrowsKeepingRatio()
    {
        var document = await OpenAsync("""
            <svg id="icon" viewBox="0 0 20 10" style="min-width: 40px"><rect width="20" height="10"/></svg>
            """);

        var icon = document.GetElementById("icon")!.GetBoundingClientRect();

        Assert.Equal(40, icon.Width, precision: 0);
        Assert.Equal(20, icon.Height, precision: 0);
    }

    private static async Task<IDocument> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head><style>html, body { margin: 0; padding: 0; font-family: sans-serif }</style></head>
            <body>{{body}}</body></html>
            """));
        context.GetDomHarness();
        return document;
    }
}
