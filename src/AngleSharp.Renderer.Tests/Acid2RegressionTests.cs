namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Reduced versions of what the Acid2 test (acid2.acidtests.org) showed to differ from its
/// reference rendering, compared with the boxes Chromium lays out.
/// </summary>
public sealed class Acid2RegressionTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };

    // a 1x1 yellow PNG, its base64 percent-encoded as Acid2 has it
    private const string YellowPixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mP4%2F58BAAT%2FAf9jgNErAAAAAElFTkSuQmCC";

    [Fact]
    public async Task MinHeight_WinsOverMaxHeight()
    {
        // the scalp: max-height is applied first, then min-height (CSS 2.1 §10.7)
        var document = await OpenAsync("""
            <div id="fixed" style="position: fixed; top: 10px; left: 10px; width: 10px; height: 8px; min-height: 12px; max-height: 7px"></div>
            <div id="flow" style="height: 8px; min-height: 12px; max-height: 7px"></div>
            """);

        Assert.Equal(12f, Rect(document, "fixed").Height, precision: 1);
        Assert.Equal(12f, Rect(document, "flow").Height, precision: 1);
    }

    [Fact]
    public async Task MarginsOfAnEmptyBlock_CollapseWithAllAdjoiningOnes()
    {
        // 48, 75 and 75 with the -72 of the empty child collapse to 75 - 72, not to 48 or 75
        var document = await OpenAsync("""
            <div style="height: 12px; margin-bottom: 48px"></div>
            <div id="empty" style="margin: 75px"><div style="margin: 0 0 -72px"></div></div>
            <div id="next" style="margin-top: 60px; height: 10px"></div>
            """);

        Assert.Equal(15f, Rect(document, "empty").Y, precision: 1);
        Assert.Equal(15f, Rect(document, "next").Y, precision: 1);
    }

    [Fact]
    public async Task ClearedBlock_IsAtTheBottomOfTheFloat()
    {
        var document = await OpenAsync("""
            <div style="height: 12px; margin-bottom: 48px"></div>
            <div style="float: left; margin: -24px 0 -12px; width: 20px; height: 48px"></div>
            <div style="margin: 75px"><div style="margin: 0 0 -72px"></div></div>
            <div id="cleared" style="margin-top: 60px; clear: both; height: 10px"></div>
            """);

        // the float is at 12 + 48 - 24 and ends 12px above its border box
        Assert.Equal(72f, Rect(document, "cleared").Y, precision: 1);
    }

    [Fact]
    public async Task FloatAfterAnAbsoluteBox_IsBelowTheMarginOfTheBlockBefore()
    {
        var document = await OpenAsync("""
            <div style="height: 12px; margin-bottom: 48px"></div>
            <div style="position: absolute; top: 0; left: 200px"><div style="height: 10px"></div></div>
            <div id="float" style="float: left; margin-top: -24px; width: 20px; height: 20px"></div>
            """);

        Assert.Equal(36f, Rect(document, "float").Y, precision: 1);
    }

    [Fact]
    public async Task RelativeBox_MovesByItsBottomWithoutATop()
    {
        var document = await OpenAsync("""
            <div style="height: 10px"></div>
            <div id="moved" style="position: relative; bottom: -12px; right: 5px; height: 10px"></div>
            """);

        Assert.Equal(22f, Rect(document, "moved").Y, precision: 1);
        Assert.Equal(-5f, Rect(document, "moved").X, precision: 1);
    }

    [Fact]
    public async Task InlineContentAfterABlock_IsBelowItsBottomMargin()
    {
        var document = await OpenAsync("""
            <div><div style="height: 10px; margin-bottom: 20px"></div>text <span id="span">x</span></div>
            <div id="text"><div style="height: 10px; margin-bottom: 20px"></div>just text</div>
            <div id="positioned" style="position: relative"><div style="height: 10px; margin-bottom: 20px"></div><span id="absolute" style="position: absolute">a</span></div>
            """);

        Assert.Equal(30f, Rect(document, "span").Y, precision: 1);
        Assert.True(Rect(document, "text").Height > 30f);
        Assert.Equal(Rect(document, "positioned").Y + 30f, Rect(document, "absolute").Y, precision: 1);
    }

    [Fact]
    public async Task ObjectWithAnImage_IsReplacedByIt()
    {
        var document = await OpenAsync($$"""
            <object id="unknown" data="data:application/x-unknown,ERROR"><object id="image" data="{{YellowPixel}}">ERROR</object></object>
            """);

        // the image takes the place of its fallback content, which the unknown type shows
        Assert.Equal(1f, Rect(document, "image").Width, precision: 1);
        Assert.Equal(1f, Rect(document, "image").Height, precision: 1);
        Assert.DoesNotContain(new HtmlRenderer().BuildDisplayList(document, Device).Commands, command => command.GetType().Name.Contains("Text"));
    }

    [Fact]
    public async Task FixedBackground_IsPositionedInTheViewport()
    {
        var document = await OpenAsync($$"""
            <div style="height: 300px"></div>
            <div style="margin-left: 100px; width: 50px; height: 50px; background: url({{YellowPixel}}) no-repeat fixed"></div>
            """);
        var commands = new HtmlRenderer().BuildDisplayList(document, Device).Commands.ToList();
        var anchor = commands.FindIndex(command => command is PushViewportAnchorCommand);

        // clipped to the box, which moves with the page, while the image stays in the viewport
        Assert.True(anchor > 0);
        Assert.IsType<PushClipCommand>(commands[anchor - 1]);
        var image = Assert.IsType<FillRectCommand>(commands[anchor + 1]);
        Assert.Equal(new RenderRect(0f, 0f, 800f, 600f), image.Rect);
        Assert.IsType<PopViewportAnchorCommand>(commands[anchor + 2]);
    }

    [Fact]
    public async Task AtomicInlineOverlappingAFloat_PaintsAboveIt()
    {
        // the eyes: the line of a block of no height paints above the float after it (Appendix E)
        var document = await OpenAsync("""
            <div style="height: 0; line-height: 20px"><span style="display: inline-block; width: 30px; height: 20px; background: rgb(0, 0, 255)"></span></div>
            <div style="float: left; width: 50px; height: 20px; background: rgb(255, 0, 0)"></div>
            """);
        var fills = new HtmlRenderer().BuildDisplayList(document, Device).Commands.OfType<FillRectCommand>().ToList();
        var inline = fills.FindIndex(fill => fill.Paint is RenderColorPaint { Color: { R: 0, B: 255 } });
        var floated = fills.FindIndex(fill => fill.Paint is RenderColorPaint { Color: { R: 255, G: 0 } });

        Assert.True(floated >= 0 && inline > floated);
    }

    private static RenderRect Rect(IDocument document, string id)
    {
        var rect = document.GetElementById(id)!.GetBoundingClientRect();
        return new RenderRect((float)rect.Left, (float)rect.Top, (float)rect.Width, (float)rect.Height);
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
