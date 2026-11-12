namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

/// <summary>
/// Frames of the viewport only (<see cref="DomHarnessExtensions.PrepareViewportFrame"/>): they
/// look like a frame of the whole page, which the next frame is.
/// </summary>
public sealed class ViewportFrameTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    private const string TallPage = """
        <div style="height: 150px; background: #00ff00">Top</div>
        <p>Some text, wrapping over more than one line in the narrow viewport of the test</p>
        <div style="height: 2000px; background: linear-gradient(#ff0000, #0000ff)"></div>
        <div id="end" style="height: 20px"></div>
        """;

    [Fact]
    public async Task ViewportFrame_OfATallPageStylesAndLaysOutWhatIsInView()
    {
        var (document, harness) = await OpenAsync(TallPage + string.Concat(Enumerable.Repeat("<section><h2>More</h2><p>Text</p></section>", 50)));

        var frame = harness.PrepareViewportFrame();

        Assert.True(frame.IsPartial);
        Assert.True(frame.IsAnimating);
        Assert.True(frame.ContentHeight < 2000);
        Assert.True(frame.Metrics.Elements < document.All.Length / 2);
        Assert.Null(frame.HitTestMap.HitTest(10, 190 + 2000));
    }

    [Fact]
    public async Task ViewportFrame_DoesNotBuildStyleMapsForOffscreenSiblings()
    {
        var offscreen = string.Concat(Enumerable.Repeat("""<div style="height: 20px">Offscreen</div>""", 300));
        var (_, harness) = await OpenAsync("""<div style="height: 1000px">Above the fold</div>""" + offscreen);

        var frame = harness.PrepareViewportFrame();

        Assert.True(frame.IsPartial);
        Assert.True(frame.Metrics.StyleMaps < 20, $"Created {frame.Metrics.StyleMaps} style maps for content outside the viewport.");
    }

    [Fact]
    public async Task ViewportFrame_AutoHeightColumnWithDefiniteItemHeightsDoesNotTrialLayoutItems()
    {
        var items = string.Concat(Enumerable.Repeat("""<div style="height: 40px">A fixed-height flex item</div>""", 30));
        var (_, harness) = await OpenAsync("""<main style="display: flex; flex-direction: column">""" + items + "</main>");

        var partial = harness.PrepareViewportFrame();
        var whole = harness.PrepareFrame();

        Assert.Equal(0, partial.Metrics.TrialLayouts);
        AssertSamePixels(whole.Rasterize(), partial.Rasterize());
    }

    [Fact]
    public async Task ViewportFrame_WrappingFlexRowLaysOutOnlyTheLinesInView()
    {
        var cards = string.Concat(Enumerable.Range(0, 120).Select(index => $"""<div style="width: 80px; background: #{index * 2:x2}ff00"><p>Card {index}</p><span>with text</span></div>"""));
        var (_, harness) = await OpenAsync("""<div style="display: flex; flex-wrap: wrap; gap: 8px">""" + cards + "</div>" + TallPage);

        var partial = harness.PrepareViewportFrame();
        var whole = harness.PrepareFrame();

        Assert.True(partial.IsPartial);
        Assert.True(partial.Metrics.TrialLayouts * 4 < whole.Metrics.TrialLayouts, $"{partial.Metrics.TrialLayouts} trial layouts in view, {whole.Metrics.TrialLayouts} in all.");
        AssertSamePixels(whole.Rasterize(), partial.Rasterize());
    }

    [Theory]
    [InlineData("height: 40px")]
    [InlineData("padding: 4px")]
    public async Task ViewportFrame_ColumnLaysOutOnlyTheItemsInView(string itemStyle)
    {
        var items = string.Concat(Enumerable.Range(0, 200).Select(index => $"""<div style="{itemStyle}; background: #00{index:x2}ff">Item <span>{index}</span></div>"""));
        var (_, harness) = await OpenAsync("""<main style="display: flex; flex-direction: column; gap: 4px">""" + items + "</main>" + TallPage);

        var partial = harness.PrepareViewportFrame();
        var whole = harness.PrepareFrame();

        Assert.True(partial.IsPartial);
        Assert.True(partial.Metrics.Boxes * 4 < whole.Metrics.Boxes, $"{partial.Metrics.Boxes} boxes laid out in view, {whole.Metrics.Boxes} in all.");
        AssertSamePixels(whole.Rasterize(), partial.Rasterize());
    }

    [Fact]
    public async Task ViewportFrame_LooksLikeAFrameOfTheWholePage()
    {
        var (_, harness) = await OpenAsync(TallPage);

        var partial = harness.PrepareViewportFrame().Rasterize();
        var whole = harness.PrepareFrame();

        Assert.False(whole.IsPartial);
        Assert.True(whole.ContentHeight > 2000);
        AssertSamePixels(whole.Rasterize(), partial);
    }

    [Theory]
    [InlineData("""<div style="display: table-cell; background: #00ff00">A</div><div style="display: table-cell">B</div>""")]
    [InlineData("""<div style="writing-mode: vertical-rl; height: 150px">Vertical text</div>""")]
    [InlineData("""<style>p::before { content: attr(title) ": "; color: #ff0000 }</style><p title="Before">Text</p>""")]
    [InlineData("""<style>ol { counter-reset: item } li { list-style: none } li::before { counter-increment: item; content: counter(item) ". " }</style><ol><li>One</li><li>Two</li></ol>""")]
    [InlineData("""<style>.unused::before { content: counter(item) }</style><q>Quoted</q>""")]
    [InlineData("""<div style="display: flex; gap: 8px"><div style="flex: 1; background: #00ff00">A</div><div style="flex: 2">B</div></div>""")]
    [InlineData("""<svg width="100" height="50"><rect width="100" height="50" style="fill: var(--fill, #0000ff)" /></svg>""")]
    public async Task ViewportFrame_OfContentLooksLikeAFrameOfTheWholePage(string content)
    {
        var (_, harness) = await OpenAsync(content + TallPage);

        var partial = harness.PrepareViewportFrame().Rasterize();
        var whole = harness.PrepareFrame().Rasterize();

        AssertSamePixels(whole, partial);
    }

    [Fact]
    public async Task ViewportFrame_OfAPageUsingCountersStylesAllOfIt()
    {
        var (_, harness) = await OpenAsync("""<style>li::before { counter-increment: item; content: counter(item) }</style><ol><li>One</li></ol>""" + TallPage);

        var partial = harness.PrepareViewportFrame();
        var whole = harness.PrepareFrame();

        Assert.True(partial.IsPartial);
        Assert.Equal(whole.Metrics.Elements, partial.Metrics.Elements);
    }

    [Fact]
    public async Task ViewportFrame_AnswersNoGeometryReads()
    {
        var (document, harness) = await OpenAsync(TallPage);

        harness.PrepareViewportFrame();
        var end = document.GetElementById("end")!.GetBoundingClientRect();

        Assert.True(end.Top > 2000);
        Assert.Equal(20, end.Height, 1);
    }

    [Fact]
    public async Task ViewportFrame_IsNotPatchedButLaidOutAgain()
    {
        var (document, harness) = await OpenAsync(TallPage);

        harness.PrepareViewportFrame();
        document.GetElementById("end")!.SetAttribute("style", "height: 40px");

        Assert.Null(harness.UpdateFrame());
        Assert.False(harness.PrepareFrame().IsPartial);
    }

    [Fact]
    public async Task ViewportFrame_OfAPageInViewIsComplete()
    {
        var (_, harness) = await OpenAsync("""<div style="height: 50px; background: #00ff00"></div>""");

        var partial = harness.PrepareViewportFrame();
        var whole = harness.PrepareFrame();

        Assert.Equal(whole.ContentHeight, partial.ContentHeight);
        AssertSamePixels(whole.Rasterize(), partial.Rasterize());
    }

    private static void AssertSamePixels(AngleSharp.Renderer.Rendering.RenderedPixels expected, AngleSharp.Renderer.Rendering.RenderedPixels actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var index = expected.Data.AsSpan().CommonPrefixLength(actual.Data);

        if (index < expected.Data.Length)
        {
            var (x, y) = (index / 4 % expected.Width, index / 4 / expected.Width);
            Assert.Fail($"The pixels differ first at ({x}, {y}): {expected.Data[index]} instead of {actual.Data[index]}");
        }
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
