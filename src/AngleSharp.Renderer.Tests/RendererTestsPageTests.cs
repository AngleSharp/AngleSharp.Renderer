using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// Layouts of the renderer test pages (anglesharp-tests.anglevisions.com/renderer/): reversed
/// flex lines, implicit grid tracks, subgrids and boxes sized by their content between offsets.
/// </summary>
public sealed class RendererTestsPageTests
{
    [Fact]
    public async Task RowReverse_PlacesTheFirstItemAtTheEnd_AfterOrdering()
    {
        var document = await ParseAsync("""
            <div id="order" style="display:flex; flex-direction:row-reverse; width:400px;">
              <div id="a" style="width:80px; height:30px;">A</div>
              <div id="b" style="width:80px; height:30px;">B</div>
              <div id="first" style="width:80px; height:30px; order:-1;">first</div>
            </div>
            """);

        Assert.Equal(320d, Left(document, "#first"));
        Assert.Equal(240d, Left(document, "#a"));
        Assert.Equal(160d, Left(document, "#b"));
    }

    [Fact]
    public async Task ImplicitColumns_AreSizedByGridAutoColumns()
    {
        var document = await ParseAsync("""
            <div style="display:grid; width:400px; grid-template-columns:200px; grid-auto-columns:50px; grid-auto-rows:25px;">
              <div>near</div>
              <div id="far" style="grid-column:3; grid-row:2;">far</div>
            </div>
            """);

        var far = document.QuerySelector("#far")!.GetBoundingClientRect();
        Assert.Equal(250d, far.Left);
        Assert.Equal(25d, far.Top);
        Assert.Equal(50d, far.Width);
    }

    [Fact]
    public async Task SubgridColumns_AdoptTheTracksOfTheParent()
    {
        var document = await ParseAsync("""
            <div style="display:grid; width:400px; grid-template-columns:50px 100px 150px 100px;">
              <div>0</div>
              <div style="grid-column:2 / 5; display:grid; grid-template-columns:subgrid;">
                <div id="a">a</div><div id="b">b</div><div id="c">c</div>
              </div>
            </div>
            """);

        Assert.Equal(100d, document.QuerySelector("#a")!.GetBoundingClientRect().Width);
        Assert.Equal(150d, document.QuerySelector("#b")!.GetBoundingClientRect().Width);
        Assert.Equal(300d, Left(document, "#c"));
    }

    [Fact]
    public async Task SubgridColumns_KeepTheirLinesWithAGapOfTheirOwn()
    {
        var document = await ParseAsync("""
            <div style="display:grid; width:400px; grid-template-columns:repeat(3, 100px); column-gap:20px;">
              <div style="grid-column:1 / -1; display:grid; grid-template-columns:subgrid; column-gap:0;">
                <div>a</div><div id="b">b</div><div>c</div>
              </div>
            </div>
            """);

        var b = document.QuerySelector("#b")!.GetBoundingClientRect();
        Assert.Equal(110d, b.Left);
        Assert.Equal(120d, b.Width);
    }

    [Fact]
    public async Task SubgridRows_AreSizedByTheItemsOfAllSubgrids()
    {
        var document = await ParseAsync("""
            <div style="display:grid; width:300px; grid-template-columns:repeat(2, 1fr); font:12px/20px sans-serif;">
              <article style="grid-row:span 3; display:grid; grid-template-rows:subgrid;">
                <h3 style="margin:0; font-size:12px;">Short</h3><p style="margin:0;">One</p><footer id="f1">footer</footer>
              </article>
              <article style="grid-row:span 3; display:grid; grid-template-rows:subgrid;">
                <h3 style="margin:0; font-size:12px; height:60px;">Tall</h3><p style="margin:0;">One</p><footer id="f2">footer</footer>
              </article>
            </div>
            """);

        Assert.Equal(Top(document, "#f2"), Top(document, "#f1"));
        Assert.Equal(80d, Top(document, "#f1"));
    }

    [Fact]
    public async Task FixedBoxOfFitContentHeight_IsCenteredBetweenItsOffsets()
    {
        var document = await ParseAsync("""
            <div id="pop" style="position:fixed; inset:0; width:fit-content; height:fit-content; margin:auto;">
              <div style="width:100px; height:40px;"></div>
            </div>
            """);

        var rect = document.QuerySelector("#pop")!.GetBoundingClientRect();
        Assert.Equal(40d, rect.Height);
        Assert.Equal(60d, rect.Top);
        Assert.Equal(70d, rect.Left);
    }

    [Fact]
    public async Task ScrollWidth_IncludesTextCutOffByAnEllipsis()
    {
        var document = await ParseAsync("<div id=\"box\" style=\"width:100px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font:16px sans-serif\">A very long label that does not fit into its box</div>");

        var box = document.QuerySelector("#box")!;
        Assert.Equal(100, box.GetClientWidth());
        Assert.True(box.GetScrollWidth() > 200, $"scrollWidth {box.GetScrollWidth()}");
    }

    [Fact]
    public async Task GetImageData_ReadsWhatWasDrawn()
    {
        var document = await ParseAsync("<canvas id=c width=101 height=1></canvas>");
        var canvas = (AngleSharp.Html.Dom.IHtmlCanvasElement)document.QuerySelector("#c")!;
        var context = new Canvas2DRenderingContext(canvas);
        var gradient = context.CreateLinearGradient(0, 0, 100, 0);
        gradient.AddColorStop(0, "rgb(255, 0, 0)");
        gradient.AddColorStop(1, "rgb(0, 0, 255)");
        context.FillStyle = gradient;
        context.FillRect(0, 0, 101, 1);

        var data = context.GetImageData(50, 0, 1, 1).Data;

        Assert.InRange((int)data[0], 125, 131);
        Assert.InRange((int)data[2], 125, 131);
        Assert.Equal(255, data[3]);

        context.PutImageData(new AngleSharp.Media.Dom.ImageData(1, 1), 50, 0);
        Assert.Equal(0, context.GetImageData(50, 0, 1, 1).Data[3]);
    }

    private static double Left(IDocument document, string selector) =>
        document.QuerySelector(selector)!.GetBoundingClientRect().Left;

    private static double Top(IDocument document, string selector) =>
        document.QuerySelector(selector)!.GetBoundingClientRect().Top;

    private static async Task<IDocument> ParseAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice
            {
                ViewPortWidth = 240,
                ViewPortHeight = 160,
                DeviceWidth = 240,
                DeviceHeight = 160,
                FontSize = 16,
            }));
        return await context.OpenAsync(request => request.Content("<!doctype html><html><head><style>html, body { margin: 0; }</style></head><body>" + body + "</body></html>"));
    }
}
