namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

/// <summary>
/// A change of an element's state that the DOM does not see (hover, focus) only styles and lays
/// out again what a selector of that state refers to, and the frame is patched (see
/// <see cref="IDomHarness.UpdateFrame"/>) when that is confined to the element and its descendants.
/// </summary>
public sealed class StateInvalidationTests
{
    private const string Style = """
        html, body { margin: 0; padding: 0; font-family: sans-serif; }
        .card { margin: 10px; padding: 8px; width: 200px; }
        .title { color: rgb(0, 0, 0); }
        p { margin: 0; }
        """;

    [Theory]
    [InlineData(".card:hover .title { color: rgb(200, 0, 0); }")]
    [InlineData(".card:hover { background: rgb(0, 0, 200); }")]
    [InlineData(":is(.card:hover, .card:focus-within) .title { color: rgb(200, 0, 0); }")]
    [InlineData(".card:not(:hover) .title { color: rgb(0, 200, 0); }")]
    public async Task HoveringPatchesTheFrameToWhatALayoutShows(string rule)
    {
        var (document, harness) = await OpenAsync(rule, """<div class="card" id="card"><p class="title">Title</p></div><div class="card"><p>Other</p></div>""");
        var before = harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("card")!.FirstElementChild);
        var patched = harness.UpdateFrame();

        Assert.NotNull(patched);
        Assert.NotSame(before, patched);
        Assert.True(patched!.Metrics.IsIncremental);
        Assert.Equal(harness.PrepareFrame().Rasterize().Data, patched.Rasterize().Data);
        Assert.NotEqual(before.Rasterize().Data, patched.Rasterize().Data);
    }

    [Theory]
    [InlineData(".card:hover { background-color: rgb(0, 0, 200); border-color: rgb(200, 0, 0); }")]
    [InlineData(".card:hover { outline: 3px solid rgb(0, 200, 0); box-shadow: 2px 2px 4px rgb(0, 0, 0); }")]
    [InlineData(".card:hover .title { color: rgb(200, 0, 0); }")]
    [InlineData(".card:hover .title { text-decoration: underline; }")]
    [InlineData(".card:hover::before { background-color: rgb(0, 0, 200); }")]
    public async Task APaintOnlyChangeRepaintsTheFrameWithoutLayingItOut(string rule)
    {
        var (document, harness) = await OpenAsync(
            ".card { border: 2px solid rgb(0, 0, 0); } .card::before { content: 'x'; display: block; background-color: rgb(0, 200, 0); } " + rule,
            """<div class="card" id="card"><p class="title">Title</p></div><div class="card"><p>Other</p></div>""");
        var before = harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("card")!.FirstElementChild);
        var patched = harness.UpdateFrame();

        Assert.NotNull(patched);
        Assert.True(patched!.Metrics.IsIncremental);
        // the boxes stayed where they were: the frame's map of them, too
        Assert.Same(before.HitTestMap, patched.HitTestMap);
        Assert.Equal(harness.PrepareFrame().Rasterize().Data, patched.Rasterize().Data);
        Assert.NotEqual(before.Rasterize().Data, patched.Rasterize().Data);
    }

    [Fact]
    public async Task ACursorChangeMapsTheBoxesAgain()
    {
        var (document, harness) = await OpenAsync(".card:hover { cursor: pointer; background-color: rgb(0, 0, 200); }", """<div class="card" id="card"><p class="title">Title</p></div>""");
        var before = harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("card"));
        var patched = harness.UpdateFrame();

        Assert.NotNull(patched);
        Assert.NotSame(before.HitTestMap, patched!.HitTestMap);
        Assert.Equal("pointer", patched.HitTestMap.HitTest(20, 20)?.Cursor);
    }

    [Theory]
    [InlineData(":where(.link):hover { color: rgb(200, 0, 0); }")]
    [InlineData(":is(a.link, span.link):hover { color: rgb(200, 0, 0); }")]
    [InlineData("[data-a=\"1\"] :where(.link):where([data-b]):hover { color: rgb(200, 0, 0); }")]
    [InlineData(".suggester .card:hover .title { color: rgb(200, 0, 0); }")]
    [InlineData(".suggester > div:hover p { color: rgb(200, 0, 0); }")]
    public async Task HoveringWhatTheArgumentsOfASelectorRuleOutKeepsTheFrame(string rule)
    {
        var (document, harness) = await OpenAsync(rule, """<div class="card" id="card" data-a="1"><p class="title">Title</p></div>""");
        var frame = harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("card")!.FirstElementChild);

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Fact]
    public async Task HoveringWhatIsNotRenderedKeepsTheFrame()
    {
        var (document, harness) = await OpenAsync("a:hover { color: rgb(200, 0, 0); background: rgb(0, 0, 200); }", """<div style="display: none"><ul><li><a id="link" href="#">Hidden</a></li></ul></div><p>Shown</p>""");
        var frame = harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("link"));

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Fact]
    public async Task HoveringWhatNoSelectorRefersToKeepsTheFrame()
    {
        var (document, harness) = await OpenAsync(".other:hover { color: red; }", """<div class="card" id="card"><p class="title">Title</p></div>""");
        var frame = harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("card"));

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Theory]
    [InlineData("summary:has(a:hover) { color: rgb(200, 0, 0); }")]
    [InlineData(":is(.list summary):hover:has(a:hover) { color: rgb(200, 0, 0); }")]
    public async Task HoveringWhatARelationalSelectorLooksForOutsideOfItsSubjectKeepsTheFrame(string rule)
    {
        var (document, harness) = await OpenAsync(rule, """<div class="card"><a id="link" href="#">Link</a></div><details><summary>S</summary></details>""");
        var frame = harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("link"));

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Theory]
    [InlineData("summary:has(a:hover) { color: rgb(200, 0, 0); }")]
    [InlineData("details:not(:has(a:hover)) { color: rgb(200, 0, 0); }")]
    [InlineData("summary:has(+ a:hover) { color: rgb(200, 0, 0); }")]
    public async Task HoveringWhatARelationalSelectorLooksForNeedsALayout(string rule)
    {
        var (document, harness) = await OpenAsync(rule, """<details open><summary><a id="link" href="#">Link</a></summary><a href="#">Next</a></details>""");
        harness.PrepareFrame();
        var link = rule.Contains('+') ? document.QuerySelectorAll("a")[1] : document.GetElementById("link");

        harness.SetHoveredElement(link);

        Assert.Null(harness.UpdateFrame());
    }

    [Theory]
    [InlineData(".card:hover + .card { color: rgb(200, 0, 0); }")]
    [InlineData("body:has(.card:hover) .card { color: rgb(200, 0, 0); }")]
    public async Task HoveringWhatStylesOtherElementsNeedsALayout(string rule)
    {
        var (document, harness) = await OpenAsync(rule, """<div class="card" id="card"><p class="title">Title</p></div><div class="card"><p>Other</p></div>""");
        harness.PrepareFrame();

        harness.SetHoveredElement(document.GetElementById("card"));

        Assert.Null(harness.UpdateFrame());
    }

    [Theory]
    [InlineData("<style>p:hover { color: rgb(200, 0, 0); }</style><p id=\"inner\">Shadow</p>")]
    [InlineData("<style>:host(:hover) p { color: rgb(200, 0, 0); }</style><p id=\"inner\">Shadow</p>")]
    public async Task HoveringInAShadowTreeWhoseRulesReferToTheStateIsLaidOutAgain(string shadowTree)
    {
        var (document, harness) = await OpenAsync(string.Empty, $"""<div class="card" id="host"><template shadowrootmode="open">{shadowTree}</template></div>""");
        harness.PrepareFrame();
        var host = document.GetElementById("host")!;

        harness.SetHoveredElement(host.ShadowRoot!.GetElementById("inner") ?? host);

        Assert.Null(harness.UpdateFrame());
    }

    [Fact]
    public async Task AFocusChangeToldToTheHarnessPatchesTheFrame()
    {
        var (document, harness) = await OpenAsync("input:focus { background: rgb(0, 0, 200); } .card:focus-within { background: rgb(0, 200, 0); }", """<div class="card"><input id="field"></div>""");
        var before = harness.PrepareFrame();
        var field = (IHtmlInputElement)document.GetElementById("field")!;

        field.DoFocus();
        harness.NotifyStateChanged(field);
        var patched = harness.UpdateFrame();

        Assert.NotNull(patched);
        Assert.Equal(harness.PrepareFrame().Rasterize().Data, patched!.Rasterize().Data);
        Assert.NotEqual(before.Rasterize().Data, patched.Rasterize().Data);
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string rule, string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 300, FontSize = 16 }));
        var document = await context.OpenAsync(request => request.Content($"<html><head><style>{Style} {rule}</style></head><body>{body}</body></html>"));
        return (document, document.Context.GetDomHarness());
    }
}
