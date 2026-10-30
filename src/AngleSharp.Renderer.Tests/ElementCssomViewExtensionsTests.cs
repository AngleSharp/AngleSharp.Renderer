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

    [Theory]
    // found on www.florian-rappl.de: an empty, collapsed panel overflowing onto the navigation took its clicks
    [InlineData("<div style='height:0'><ul id='under' style='height:100px; margin:0'></ul></div><nav id='top' style='position:relative; z-index:4; height:50px'><a id='hit' href='#' style='display:block; height:50px'>Link</a></nav>", 30, "hit")]
    [InlineData("<div id='hit' style='position:absolute; top:0; width:100px; height:100px; z-index:2'></div><div style='position:absolute; top:0; width:100px; height:100px; z-index:1'><span style='display:block; height:80px'>deep</span></div>", 30, "hit")]
    [InlineData("<div style='position:relative; height:100px'><div id='hit' style='position:absolute; left:0; top:0; right:0; bottom:0'></div></div><div style='margin-top:-100px; height:100px'><p style='margin:0'>in flow</p></div>", 10, "hit")]
    [InlineData("<div style='height:100px; overflow:hidden'><div style='height:100px'></div><div id='clipped' style='height:100px'></div></div><div id='hit' style='height:100px'></div>", 150, "hit")]
    [InlineData("<div id='hit' style='height:100px'><div style='height:100px; pointer-events:none'><span style='display:block; height:100px'>pass</span></div></div>", 50, "hit")]
    public async Task DomHarness_HitTestsTheTopMostPaintedElement(string body, double y, string expected)
    {
        var context = BrowsingContext.New(CreateConfiguration(160, 400));
        var document = await context.OpenAsync(request => request.Content($"<html><head><style>html, body {{ margin: 0; padding: 0; }}</style></head><body>{body}</body></html>"));
        var harness = context.GetDomHarness();

        harness.MousePosition = (20d, y);

        Assert.Equal(expected, harness.HoveredElement?.Id);
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

    [Theory]
    [InlineData("1.5px", 1d)]
    [InlineData("0.5px", 1d)]
    [InlineData("2.7px", 2d)]
    [InlineData("0.1875em", 3d)]
    [InlineData("thin", 1d)]
    public async Task BorderWidths_AreSnappedToDevicePixels(string width, double expected)
    {
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; } #c { width: 20px; height: 20px; font-size: 16px; border: {{width}} solid black; }</style></head>
              <body><div id="c"></div></body>
            </html>
            """);

        var rect = document.QuerySelector("#c")!.GetBoundingClientRect();

        Assert.Equal(20d + (2 * expected), rect.Height, 2);
    }

    [Theory]
    [InlineData("input", 39d)]
    [InlineData("select", 39d)]
    [InlineData("textarea", 63d)]
    public async Task FormControl_KeepsItsPaddingAndBorder_UnderBorderBox(string control, double expectedHeight)
    {
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; } * { box-sizing: border-box; } #c { font-size: 16px; line-height: 1.5; padding: 6.5px 40px; border: 1.5px solid black; }</style></head>
              <body><{{control}} id="c"></{{control}}></body>
            </html>
            """);

        var rect = document.QuerySelector("#c")!.GetBoundingClientRect();

        Assert.Equal(expectedHeight, rect.Height, 1);
    }

    [Fact]
    public async Task FormControl_WithContentBox_IsNotEnlarged()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; } #c { font-size: 16px; line-height: 1.5; padding: 6.5px 40px; border: 1.5px solid black; }</style></head>
              <body><input id="c"></body>
            </html>
            """);

        var rect = document.QuerySelector("#c")!.GetBoundingClientRect();

        // the 1.5px border is snapped to 1px, as in browsers
        Assert.Equal(39d, rect.Height, 1);
        Assert.Equal(232d, rect.Width, 1);
    }

    [Fact]
    public async Task Geometry_FollowsMutationsMadeRightBefore()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; } .wide { width: 100px; }</style></head>
              <body><div id="box" style="width:40px; height:20px;"></div><div id="after" style="height:10px"></div></body>
            </html>
            """);

        var box = document.QuerySelector("#box")!;
        var after = document.QuerySelector("#after")!;

        Assert.Equal(40d, box.GetBoundingClientRect().Width);
        Assert.Equal(20d, after.GetBoundingClientRect().Top);

        box.SetAttribute("style", "width:70px; height:50px;");
        Assert.Equal(70d, box.GetBoundingClientRect().Width);
        Assert.Equal(50d, after.GetBoundingClientRect().Top);

        box.Remove();
        Assert.Equal(0d, after.GetBoundingClientRect().Top);
    }

    [Fact]
    public async Task Window_ScrollsTheDocumentElement()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; }</style></head>
              <body><div style="height:1000px"></div></body>
            </html>
            """);

        var window = document.DefaultView!;

        Assert.Equal(0d, window.GetScrollY());

        window.ScrollTo(0, 300);
        Assert.Equal(300d, window.GetScrollY());
        Assert.Equal(300d, document.DocumentElement.GetScrollTop());

        window.ScrollBy(new ScrollToOptions { Top = 50 });
        Assert.Equal(350d, window.GetScrollY());

        window.ScrollTo(new ScrollToOptions { Top = 5000 });
        Assert.Equal(1000d - 160d, window.GetScrollY());
    }

    [Fact]
    public async Task AutoHeightColumn_PlacesItemsByTheirContentHeight()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; } body { display: flex; flex-direction: column; min-height: 100px; }</style></head>
              <body>
                <header id="header" style="height:50px"></header>
                <main id="main" style="flex-grow:1; margin-top:10px"><div style="height:300px"></div><p style="margin:0">Text</p><div style="height:20px"></div></main>
                <footer id="footer" style="height:40px"></footer>
              </body>
            </html>
            """);

        var main = document.QuerySelector("#main")!.GetBoundingClientRect();
        var footer = document.QuerySelector("#footer")!.GetBoundingClientRect();

        Assert.Equal(60d, main.Top);
        Assert.True(main.Height > 320d);
        Assert.Equal(main.Top + main.Height, footer.Top);
        Assert.Equal(40d, footer.Height);
    }

    [Theory]
    [InlineData("<div style='height:20px'></div>")]
    [InlineData("<p style='margin:0'>Short text</p>")]
    public async Task AutoHeightColumn_GrowsItemsIntoItsMinHeight(string mainContent)
    {
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; } body { display: flex; flex-direction: column; min-height: 300px; }</style></head>
              <body>
                <header style="height:50px"></header>
                <main id="main" style="flex-grow:1">{{mainContent}}</main>
                <footer id="footer" style="height:40px"></footer>
              </body>
            </html>
            """);

        var main = document.QuerySelector("#main")!.GetBoundingClientRect();
        var footer = document.QuerySelector("#footer")!.GetBoundingClientRect();

        Assert.Equal(50d, main.Top);
        Assert.Equal(210d, main.Height, 2);
        Assert.Equal(260d, footer.Top, 2);
        Assert.Equal(300d, document.Body!.GetBoundingClientRect().Height, 2);
    }

    [Theory]
    [InlineData("<div style='height:20px'></div>")]
    [InlineData("<div style='height:500px'></div>")]
    public async Task AutoHeightColumn_InMultipleColumns_GrowsLikeAMeasuredOne(string mainContent)
    {
        // in multiple columns the deferred items are measured up to the min-height beforehand
        string Page(string wrap) => $$"""
            <html>
              <head><style>html, body { margin: 0; }</style></head>
              <body>
                <div style="columns:2; column-gap:0">
                  <div id="column" style="display:flex; flex-direction:column; flex-wrap:{{wrap}}; min-height:300px">
                    <header style="height:50px"></header>
                    <main style="flex-grow:1">{{mainContent}}</main>
                    <footer style="height:40px"></footer>
                  </div>
                </div>
              </body>
            </html>
            """;

        var deferred = await ParseAsync(Page("nowrap"));
        var measured = await ParseAsync(Page("wrap"));
        var deferredRects = deferred.QuerySelectorAll("#column, #column *").Select(m => m.GetBoundingClientRect()).ToList();
        var measuredRects = measured.QuerySelectorAll("#column, #column *").Select(m => m.GetBoundingClientRect()).ToList();

        Assert.True(deferredRects[2].Height >= 210d - 0.01);

        for (var i = 0; i < measuredRects.Count; i++)
        {
            Assert.Equal(measuredRects[i].Top, deferredRects[i].Top, 2);
            Assert.Equal(measuredRects[i].Height, deferredRects[i].Height, 2);
        }
    }

    [Fact]
    public async Task AutoHeightColumn_JustifiesItemsWithinItsMinHeight()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; }</style></head>
              <body>
                <div style="display:flex; flex-direction:column; justify-content:center; min-height:200px">
                  <div id="item" style="height:40px"></div>
                </div>
              </body>
            </html>
            """);

        Assert.Equal(80d, document.QuerySelector("#item")!.GetBoundingClientRect().Top, 2);
    }

    [Fact]
    public async Task AutoHeightColumn_DoesNotGrowItemsPastTheirContent()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; } body { display: flex; flex-direction: column; min-height: 100px; }</style></head>
              <body>
                <main id="main" style="flex-grow:1"><div style="height:400px"></div></main>
                <footer id="footer" style="flex-grow:1; height:40px"></footer>
              </body>
            </html>
            """);

        Assert.Equal(400d, document.QuerySelector("#main")!.GetBoundingClientRect().Height, 2);
        Assert.Equal(400d, document.QuerySelector("#footer")!.GetBoundingClientRect().Top, 2);
    }

    [Fact]
    public async Task AutoHeightColumn_ShrinksItemsToItsMaxHeight()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; }</style></head>
              <body>
                <div id="column" style="display:flex; flex-direction:column; max-height:100px">
                  <div id="a" style="height:80px; overflow:hidden"></div>
                  <div id="b" style="height:80px; overflow:hidden"></div>
                </div>
              </body>
            </html>
            """);

        Assert.Equal(50d, document.QuerySelector("#a")!.GetBoundingClientRect().Height, 2);
        Assert.Equal(50d, document.QuerySelector("#b")!.GetBoundingClientRect().Top, 2);
        Assert.Equal(100d, document.QuerySelector("#column")!.GetBoundingClientRect().Height, 2);
    }

    [Theory]
    [InlineData("<div style='height:120px'></div><p>Some text that wraps across more than a single line of the item.</p>")]
    [InlineData("<div style='display:flex'><div style='width:50px; height:30px'></div><div style='flex:1'><p>Row item text</p></div></div>")]
    [InlineData("<div style='display:grid; grid-template-columns:1fr 1fr; gap:4px'><div style='height:10px'></div><div><p>Grid text</p></div></div>")]
    [InlineData("<div style='height:80px'><div style='height:50%'></div></div><div><div style='height:50%'>auto parent</div></div>")]
    [InlineData("<div style='display:flex; flex-direction:column'><div style='flex:1'><div style='height:100%'>stretched</div></div></div>")]
    [InlineData("<div style='position:relative'><div style='position:absolute; top:10%; height:50%'></div><p>abs</p></div>")]
    [InlineData("<div style='min-height:40px; max-height:200px; padding:5px; border:2px solid'><p>Clamped</p></div>")]
    public async Task AutoHeightColumn_LaysOutItemsLikeAMeasuredOne(string itemContent)
    {
        // a column of a wrapping container is a single line as well, but measured by trial layouts
        string Page(string wrap) => $$"""
            <html>
              <head><style>html, body { margin: 0; } p { margin: 6px 0; }</style></head>
              <body>
                <div id="column" style="display:flex; flex-direction:column; flex-wrap:{{wrap}}; min-height:50px; row-gap:3px">
                  <div style="height:20px"></div>
                  <div style="flex-grow:1; margin:7px 0 2px">{{itemContent}}</div>
                  <section style="padding:4px">{{itemContent}}</section>
                  <div style="height:15px"></div>
                </div>
              </body>
            </html>
            """;

        var deferred = await ParseAsync(Page("nowrap"));
        var measured = await ParseAsync(Page("wrap"));
        var deferredRects = deferred.QuerySelectorAll("#column, #column *").Select(m => m.GetBoundingClientRect()).ToList();
        var measuredRects = measured.QuerySelectorAll("#column, #column *").Select(m => m.GetBoundingClientRect()).ToList();

        Assert.Equal(measuredRects.Count, deferredRects.Count);

        for (var i = 0; i < measuredRects.Count; i++)
        {
            Assert.Equal(measuredRects[i].Left, deferredRects[i].Left, 2);
            Assert.Equal(measuredRects[i].Top, deferredRects[i].Top, 2);
            Assert.Equal(measuredRects[i].Width, deferredRects[i].Width, 2);
            Assert.Equal(measuredRects[i].Height, deferredRects[i].Height, 2);
        }
    }

    [Theory]
    [InlineData("margin: 0 auto; width: 100px", 70d, 100d)]
    [InlineData("margin: 0 0 0 auto; width: 100px", 140d, 100d)]
    [InlineData("margin: 0 10px", 10d, 220d)]
    [InlineData("margin: 0 auto", 0d, 0d)]
    public async Task GridItemMargins_PlaceTheItemInItsArea(string style, double left, double width)
    {
        // dev.to centers its sign-in card as a grid item with `margin: 0 auto; width: 640px`
        var document = await ParseAsync($$"""
            <html><head><style>html, body { margin: 0 }</style></head><body>
              <div style="display: grid"><div id="item" style="height: 10px; {{style}}"></div></div>
            </body></html>
            """);

        var rect = document.QuerySelector("#item")!.GetBoundingClientRect();

        Assert.Equal(left, rect.Left, 2);
        Assert.Equal(width, rect.Width, 2);
    }

    [Fact]
    public async Task GridItemVerticalMargins_TakeSpaceInTheRow()
    {
        var document = await ParseAsync("""
            <html><head><style>html, body { margin: 0 }</style></head><body>
              <div style="display: grid"><div style="height: 10px; margin: 5px 0 7px"></div><div id="next" style="height: 10px"></div></div>
              <p id="after" style="margin: 0">x</p>
            </body></html>
            """);

        Assert.Equal(22d, document.QuerySelector("#next")!.GetBoundingClientRect().Top, 2);
        Assert.Equal(32d, document.QuerySelector("#after")!.GetBoundingClientRect().Top, 2);
    }

    [Theory]
    [InlineData("grid-gap: 12px")]
    [InlineData("grid-row-gap: 12px")]
    [InlineData("gap: 12px")]
    public async Task LegacyGridGaps_SpaceTheRows(string gap)
    {
        var document = await ParseAsync($$"""
            <html><head><style>html, body { margin: 0 }</style></head><body>
              <div style="display: grid; {{gap}}"><div style="height: 10px"></div><div id="second" style="height: 10px"></div></div>
            </body></html>
            """);

        Assert.Equal(22d, document.QuerySelector("#second")!.GetBoundingClientRect().Top, 2);
    }

    [Theory]
    [InlineData("top: 50%", 50d)]
    [InlineData("bottom: 0", 90d)]
    [InlineData("top: 25%; height: 50%", 25d)]
    public async Task AbsoluteChild_OfAnAutoHeightBlock_UsesItsFinalHeight(string offsets, double top)
    {
        // dev.to's "OR" label is `top: 50%` in a `position: relative` box of an automatic height
        var document = await ParseAsync($$"""
            <html><head><style>html, body { margin: 0 }</style></head><body>
              <div style="position: relative; width: 200px">
                <div id="label" style="position: absolute; left: 0; width: 10px; height: 10px; {{offsets}}"></div>
                <div style="height: 100px"></div>
              </div>
            </body></html>
            """);

        Assert.Equal(top, document.QuerySelector("#label")!.GetBoundingClientRect().Top, 2);
    }

    [Theory]
    [InlineData("display: inline-block")]
    [InlineData("float: left")]
    public async Task ShrinkToFitWidth_IgnoresWhiteSpaceAtTheLineEdges(string style)
    {
        var document = await ParseAsync($$"""
            <html><body>
              <div><span id="spaced" style="{{style}}">
                 Forgot password?
              </span></div>
              <div style="clear: both"><span id="plain" style="{{style}}">Forgot password?</span></div>
            </body></html>
            """);

        var spaced = document.QuerySelector("#spaced")!.GetBoundingClientRect();
        var plain = document.QuerySelector("#plain")!.GetBoundingClientRect();

        Assert.Equal(plain.Width, spaced.Width, 2);
    }

    [Fact]
    public async Task ShrinkToFitWidth_KeepsOneSpaceBetweenElements()
    {
        var document = await ParseAsync("""
            <html><body>
              <div><span id="joined" style="display: inline-block"><span>a</span> <span>b</span></span></div>
              <div><span id="spaced" style="display: inline-block"><span>a </span> <span> b</span></span></div>
            </body></html>
            """);

        var joined = document.QuerySelector("#joined")!.GetBoundingClientRect();
        var spaced = document.QuerySelector("#spaced")!.GetBoundingClientRect();

        Assert.Equal(joined.Width, spaced.Width, 2);
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
