using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

public sealed class ElementCssomViewExtensionsTests
{
    [Fact]
    public async Task GetBoundingClientRect_AndClientMetrics_AreDerivedFromBorderBox()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="box" style="width:40px; height:20px; padding:5px; border:2px solid black;"></div>
              </body>
            </html>
            """);

        var element = document.QuerySelector("#box");
        Assert.NotNull(element);

        var rect = element!.GetBoundingClientRect();
        var rects = element.GetClientRects();

        Assert.Equal(54d, rect.Width);
        Assert.Equal(34d, rect.Height);
        Assert.Equal(1, rects.Length);
        Assert.Equal(50, element.GetClientWidth());
        Assert.Equal(30, element.GetClientHeight());
        Assert.Equal(54, element.GetOffsetWidth());
        Assert.Equal(34, element.GetOffsetHeight());
        Assert.Equal(50, element.GetScrollWidth());
        Assert.Equal(30, element.GetScrollHeight());
    }

    [Fact]
    public async Task GetClientRects_ReturnsEmpty_WhenElementHasNoLayoutBox()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="hidden" style="display:none; width:40px; height:20px;"></div>
              </body>
            </html>
            """);

        var element = document.QuerySelector("#hidden");
        Assert.NotNull(element);

        var rect = element!.GetBoundingClientRect();
        var rects = element.GetClientRects();

        Assert.Equal(0, rects.Length);
        Assert.Equal(0d, rect.Width);
        Assert.Equal(0d, rect.Height);
        Assert.Equal(0, element.GetOffsetWidth());
        Assert.Equal(0, element.GetOffsetHeight());
    }

    [Fact]
    public async Task OffsetParent_AndOffsets_AreResolvedFromPositionedAncestor()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="parent" style="position:relative; border:2px solid black; padding:10px; width:120px; height:80px;">
                  <div id="child" style="width:20px; height:10px;"></div>
                </div>
              </body>
            </html>
            """);

        var parent = document.QuerySelector("#parent");
        var child = document.QuerySelector("#child");

        Assert.NotNull(parent);
        Assert.NotNull(child);

        Assert.Same(parent, child!.GetOffsetParent());
        Assert.Equal(10, child.GetOffsetLeft());
        Assert.Equal(10, child.GetOffsetTop());
    }

    private static async Task<IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        return await context.OpenAsync(request => request.Content(html));
    }
}
