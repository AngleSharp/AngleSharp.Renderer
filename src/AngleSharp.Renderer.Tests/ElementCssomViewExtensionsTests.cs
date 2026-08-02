using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

public sealed class ElementCssomViewExtensionsTests
{
  [Fact]
  public void GetInteractiveHtmlRendererState_ReturnsSingleInstancePerContext()
  {
    var context = BrowsingContext.New(CreateConfiguration(120, 80));

    var first = context.GetDomHarness();
    var second = context.GetDomHarness();

    Assert.Same(first, second);
  }

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
        Assert.Equal(2, element.GetClientLeft());
        Assert.Equal(2, element.GetClientTop());
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

    [Fact]
    public async Task OffsetParent_IsNull_ForFixedPositionedElements()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="child" style="position:fixed; left:12px; top:8px; width:20px; height:10px;"></div>
              </body>
            </html>
            """);

        var child = document.QuerySelector("#child");
        Assert.NotNull(child);

        Assert.Null(child!.GetOffsetParent());
    }

    [Fact]
    public async Task ScrollMetrics_IncludeOverflowingDescendants()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="viewport" style="position:relative; width:40px; height:20px; border:1px solid black;">
                  <div style="position:absolute; left:80px; top:60px; width:30px; height:40px;"></div>
                </div>
              </body>
            </html>
            """);

        var viewport = document.QuerySelector("#viewport");
        Assert.NotNull(viewport);

        Assert.Equal(40, viewport!.GetClientWidth());
        Assert.Equal(20, viewport.GetClientHeight());
        Assert.True(viewport.GetScrollWidth() > viewport.GetClientWidth());
        Assert.True(viewport.GetScrollHeight() > viewport.GetClientHeight());
    }

    [Fact]
    public async Task ScrollPositions_AreMutable_AndClamped()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="viewport" style="position:relative; width:40px; height:20px; border:1px solid black;">
                  <div style="position:absolute; left:80px; top:60px; width:30px; height:40px;"></div>
                </div>
              </body>
            </html>
            """);

        var viewport = document.QuerySelector("#viewport");
        Assert.NotNull(viewport);

        var maxLeft = viewport!.GetScrollWidth() - viewport.GetClientWidth();
        var maxTop = viewport.GetScrollHeight() - viewport.GetClientHeight();
        Assert.True(maxLeft > 0);
        Assert.True(maxTop > 0);

        viewport.SetScrollLeft(500);
        viewport.SetScrollTop(500);
        Assert.Equal(maxLeft, viewport.GetScrollLeft());
        Assert.Equal(maxTop, viewport.GetScrollTop());

        viewport.ScrollTo(-10, -5);
        Assert.Equal(0d, viewport.GetScrollLeft());
        Assert.Equal(0d, viewport.GetScrollTop());

        viewport.ScrollBy(maxLeft + 20, maxTop + 20);
        Assert.Equal(maxLeft, viewport.GetScrollLeft());
        Assert.Equal(maxTop, viewport.GetScrollTop());

        viewport.Scroll(new ScrollToOptions { Left = 5, Top = 7 });
        Assert.Equal(5d, viewport.GetScrollLeft());
        Assert.Equal(7d, viewport.GetScrollTop());

        viewport.ScrollBy(new ScrollToOptions { Left = 3, Top = 4 });
        Assert.Equal(8d, viewport.GetScrollLeft());
        Assert.Equal(11d, viewport.GetScrollTop());

        viewport.Scroll(2, 3);
        Assert.Equal(2d, viewport.GetScrollLeft());
        Assert.Equal(3d, viewport.GetScrollTop());
    }

    [Fact]
    public async Task ScrollIntoView_ScrollsScrollableAncestor()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="viewport" style="position:relative; width:40px; height:20px; border:1px solid black;">
                  <div id="target" style="position:absolute; left:80px; top:60px; width:30px; height:40px;"></div>
                </div>
              </body>
            </html>
            """);

        var viewport = document.QuerySelector("#viewport");
        var target = document.QuerySelector("#target");

        Assert.NotNull(viewport);
        Assert.NotNull(target);

        target!.ScrollIntoView();

        Assert.True(viewport!.GetScrollLeft() > 0d);
        Assert.True(viewport.GetScrollTop() > 0d);
    }

    [Fact]
    public async Task ScrollIntoView_RespectsBooleanAndOptionsVariants()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="viewport" style="position:relative; width:40px; height:20px; border:1px solid black;">
                  <div id="target" style="position:absolute; left:80px; top:60px; width:30px; height:40px;"></div>
                </div>
              </body>
            </html>
            """);

        var viewport = document.QuerySelector("#viewport");
        var target = document.QuerySelector("#target");

        Assert.NotNull(viewport);
        Assert.NotNull(target);

        target!.ScrollIntoView(false);
        var bottomAlignedTop = viewport!.GetScrollTop();

        viewport.ScrollTo(0, 0);
        target.ScrollIntoView(new ScrollIntoViewOptions
        {
          Block = ScrollLogicalPosition.Center,
          Inline = ScrollLogicalPosition.Center,
        });

        Assert.True(bottomAlignedTop >= viewport.GetScrollTop());
        Assert.True(viewport.GetScrollLeft() > 0d);
        Assert.True(viewport.GetScrollTop() > 0d);
    }

    [Fact]
    public async Task ScrollPositions_AreIsolatedPerBrowsingContext()
    {
        const string html = """
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="viewport" style="position:relative; width:40px; height:20px; border:1px solid black;">
                  <div style="position:absolute; left:80px; top:60px; width:30px; height:40px;"></div>
                </div>
              </body>
            </html>
            """;

        var contextA = BrowsingContext.New(CreateConfiguration(120, 80));
        var contextB = BrowsingContext.New(CreateConfiguration(120, 80));

        var documentA = await contextA.OpenAsync(request => request.Content(html));
        var documentB = await contextB.OpenAsync(request => request.Content(html));

        var viewportA = documentA.QuerySelector("#viewport");
        var viewportB = documentB.QuerySelector("#viewport");

        Assert.NotNull(viewportA);
        Assert.NotNull(viewportB);

        viewportA!.SetScrollLeft(25);
        viewportA.SetScrollTop(15);

        Assert.True(viewportA.GetScrollLeft() > 0d);
        Assert.True(viewportA.GetScrollTop() > 0d);
        Assert.Equal(0d, viewportB!.GetScrollLeft());
        Assert.Equal(0d, viewportB.GetScrollTop());
    }

    [Fact]
    public async Task DomHarness_RaisesPaintInvalidated_OnInteractionChanges()
    {
        var context = BrowsingContext.New(CreateConfiguration(160, 100));
        var document = await context.OpenAsync(request => request.Content("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div id="viewport" style="position:relative; width:40px; height:20px; border:1px solid black;">
                  <div style="position:absolute; left:80px; top:60px; width:30px; height:40px;"></div>
                </div>
              </body>
            </html>
            """));

        var harness = context.GetDomHarness();
        var viewport = document.QuerySelector("#viewport");
        Assert.NotNull(viewport);

        var invalidatedCount = 0;
        harness.PaintInvalidated += (_, _) => invalidatedCount++;

        viewport!.SetScrollLeft(20);
        viewport.SetScrollTop(10);
        harness.MousePosition = (20d, 20d);

        Assert.Same(viewport, harness.HoveredElement);

        Assert.True(invalidatedCount >= 3);
    }

    [Fact]
    public async Task DomHarness_PaintsOnAssignedRenderDevice()
    {
        var context = BrowsingContext.New(CreateConfiguration(210, 130));
        _ = await context.OpenAsync(request => request.Content("<html><body><div style='width:40px;height:20px;background:#f00'></div></body></html>"));

        var harness = context.GetDomHarness();
        var image = harness.PaintToPng();

        Assert.Equal(210, image.Width);
        Assert.Equal(130, image.Height);
    }

    [Fact]
    public async Task CaretPositionFromPoint_ReturnsCaretInTextNode()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <p id="text" style="font-size:20px; margin:0;">hello</p>
              </body>
            </html>
            """);

        var caret = document.CaretPositionFromPoint(22d, 10d);

        Assert.NotNull(caret);
        Assert.IsAssignableFrom<IText>(caret!.OffsetNode);
        Assert.InRange(caret.Offset, 1, 3);

        var rect = caret.GetClientRect();
        Assert.True(rect.Height > 0d);
    }

    [Fact]
    public async Task CaretPositionFromPoint_ReturnsNull_OutsideRenderedContent()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <p>hello</p>
              </body>
            </html>
            """);

        var caret = document.CaretPositionFromPoint(-100d, -100d);

        Assert.Null(caret);
    }

    private static async Task<IDocument> ParseAsync(string html)
    {
      var context = BrowsingContext.New(CreateConfiguration(240, 160));
        return await context.OpenAsync(request => request.Content(html));
    }

    private static IConfiguration CreateConfiguration(int width, int height)
    {
      return Configuration.Default
        .WithCss()
        .WithRenderDevice(new DefaultRenderDevice
        {
          ViewPortWidth = width,
          ViewPortHeight = height,
          DeviceWidth = width,
          DeviceHeight = height,
          FontSize = 16,
        });
    }
}
