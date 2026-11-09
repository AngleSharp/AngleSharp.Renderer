using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// A change confined to an out-of-flow box (or an in-flow block staying the same from outside)
/// only lays out that box again (see HtmlRenderer.LayoutSnapshot); the geometry read afterwards
/// has to be the one a full layout gives, also where the change is not confined and the full
/// layout is the one taken.
/// </summary>
public sealed class IncrementalLayoutTests
{
    private const string Page = """
        <html>
          <head>
            <style>
              html, body { margin: 0; padding: 0; font-family: sans-serif; }
              .card { position: relative; display: inline-block; padding: 4px; }
              .dropdown { position: absolute; top: 100%; left: 0; display: none; width: 80px; padding: 3px; }
              .reverse { top: auto; bottom: 100%; }
              .open + .next { margin-top: 30px; }
              .dropdown[class*="wide"] ~ .tail { font-size: 40px; }
              p { margin: 0; }
            </style>
          </head>
          <body>
            <p>Before</p>
            <span class="card" id="card">Card text <span class="dropdown" id="dropdown"><b id="inner">Dropdown</b> content</span><span class="tail" id="tail">tail</span></span>
            <div id="flow">Flow</div>
            <div class="next" id="next">Next</div>
            <div id="fixed" style="position: fixed; right: 10px; bottom: 10px; width: 30px; height: 10px;"><i id="label">x</i><b id="badge" style="position: absolute; top: 0; left: 0">1</b></div>
          </body>
        </html>
        """;

    [Fact]
    public async Task ShowingAHiddenDropdownGivesTheGeometryOfAFullLayout()
    {
        var (document, harness) = await ParseAsync(Page);
        var dropdown = document.GetElementById("dropdown")!;
        _ = dropdown.GetBoundingClientRect();

        dropdown.SetAttribute("style", "display: block; opacity: 0");

        AssertSameAsFullLayout(document, harness, "dropdown", "inner", "card", "flow");
        Assert.True(document.GetElementById("dropdown")!.GetBoundingClientRect().Height > 0);
    }

    [Fact]
    public async Task MeasuringDropdownsInARowGivesTheGeometryOfFullLayouts()
    {
        var (document, harness) = await ParseAsync(Page);
        var dropdown = document.GetElementById("dropdown")!;
        var rects = new List<string>();

        // what dev.to does for each of its dropdowns on scroll
        for (var i = 0; i < 3; i++)
        {
            dropdown.ClassList.Remove("reverse");
            dropdown.SetAttribute("style", "opacity: 0; display: block");
            rects.Add(Describe(dropdown));
            dropdown.ClassList.Add("reverse");
            rects.Add(Describe(dropdown));
            dropdown.RemoveAttribute("style");
            rects.Add(Describe(dropdown));
        }

        harness.PaintToPng();
        dropdown.ClassList.Remove("reverse");
        dropdown.SetAttribute("style", "opacity: 0; display: block");
        var full = Describe(dropdown);

        Assert.Equal(full, rects[0]);
        Assert.NotEqual(rects[0], rects[1]);
        Assert.Equal("0.000,0.000,0.000,0.000", rects[2]);
        Assert.Equal(rects[0], rects[3]);
    }

    [Fact]
    public async Task ChangingTheTextOfAnOutOfFlowBoxGivesTheGeometryOfAFullLayout()
    {
        var (document, harness) = await ParseAsync(Page);
        _ = document.GetElementById("label")!.GetBoundingClientRect();

        document.GetElementById("label")!.TextContent = "a much longer label";
        document.GetElementById("fixed")!.SetAttribute("style", "position: fixed; right: 10px; bottom: 10px; height: 10px;");

        AssertSameAsFullLayout(document, harness, "fixed", "label", "flow");
    }

    [Fact]
    public async Task ChangingABlockWithABoxPlacedAgainstAnAncestorGivesTheGeometryOfAFullLayout()
    {
        var (document, harness) = await ParseAsync("""
            <!doctype html><html><body style="margin: 0">
              <div id="block" style="position: relative; width: 200px">
                <div id="part"><span id="text">text</span><div id="placed" style="position: absolute; right: 0; bottom: 0; width: 10px; height: 50%"></div></div>
                <div id="rest" style="height: 80px"></div>
              </div>
            </body></html>
            """);
        _ = document.GetElementById("placed")!.GetBoundingClientRect();

        document.GetElementById("text")!.TextContent = "a text that is a good bit longer than the first one, so that it wraps to a further line";

        AssertSameAsFullLayout(document, harness, "block", "placed", "rest");
    }

    [Fact]
    public async Task FillingAnEmptyBlockGivesTheGeometryOfAFullLayout()
    {
        var (document, harness) = await ParseAsync("""
            <!doctype html><html><body style="margin: 0">
              <div id="before" style="height: 10px"></div>
              <div id="empty" style="margin: 30px 0"></div>
              <div id="after" style="height: 10px"></div>
            </body></html>
            """);
        _ = document.GetElementById("after")!.GetBoundingClientRect();

        document.GetElementById("empty")!.TextContent = "now it has a line, which separates its margins";

        AssertSameAsFullLayout(document, harness, "before", "empty", "after");
    }

    [Fact]
    public async Task ChangingABlockInColumnsGivesTheGeometryOfAFullLayout()
    {
        var (document, harness) = await ParseAsync("""
            <!doctype html><html><body style="margin: 0">
              <div style="columns: 2; column-gap: 20px; width: 400px">
                <div id="first" style="height: 40px; break-inside: avoid">a</div>
                <div id="second" style="height: 40px; break-inside: avoid"><span id="text">b</span></div>
              </div>
            </body></html>
            """);
        _ = document.GetElementById("second")!.GetBoundingClientRect();

        document.GetElementById("text")!.TextContent = "c";

        AssertSameAsFullLayout(document, harness, "first", "second", "text");
    }

    [Fact]
    public async Task ChangingAClassASiblingSelectorRefersToGivesTheGeometryOfAFullLayout()
    {
        var (document, harness) = await ParseAsync(Page);
        var flow = document.GetElementById("flow")!;
        var before = flow.GetBoundingClientRect();
        _ = document.GetElementById("next")!.GetBoundingClientRect();

        flow.ClassList.Add("open");

        Assert.Equal(before.Top, flow.GetBoundingClientRect().Top);
        AssertSameAsFullLayout(document, harness, "next", "flow");
    }

    [Fact]
    public async Task MovingABoxIntoTheFlowGivesTheGeometryOfAFullLayout()
    {
        var (document, harness) = await ParseAsync(Page);
        var flowTop = document.GetElementById("flow")!.GetBoundingClientRect().Top;

        document.GetElementById("dropdown")!.SetAttribute("style", "display: block; position: static");

        Assert.True(document.GetElementById("flow")!.GetBoundingClientRect().Top > flowTop);
        AssertSameAsFullLayout(document, harness, "dropdown", "card", "flow", "next");
    }

    [Theory]
    [InlineData("wide")]
    [InlineData("other")]
    public async Task ChangingAClassAnAttributeSelectorOfASiblingSelectorRefersToGivesTheGeometryOfAFullLayout(string token)
    {
        var (document, harness) = await ParseAsync(Page);
        var dropdown = document.GetElementById("dropdown")!;
        dropdown.SetAttribute("style", "display: block");
        var tail = Describe(document.GetElementById("tail")!);

        dropdown.ClassList.Add(token);

        Assert.Equal(token == "wide", tail != Describe(document.GetElementById("tail")!));
        AssertSameAsFullLayout(document, harness, "dropdown", "tail", "card", "flow");
    }

    [Fact]
    public async Task AFrameStaysAsItIsWhenAHiddenBoxIsShownMeasuredAndHiddenAgain()
    {
        var (document, harness) = await ParseAsync(Page);
        var dropdown = document.GetElementById("dropdown")!;
        var frame = harness.PrepareFrame();
        Assert.Same(frame, harness.UpdateFrame());

        dropdown.SetAttribute("style", "display: block");
        _ = dropdown.GetBoundingClientRect();
        dropdown.ClassList.Add("reverse");
        _ = dropdown.GetBoundingClientRect();
        dropdown.RemoveAttribute("style");

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Theory]
    [InlineData("flow", "class", "open")]
    [InlineData("tail", "style", "font-size: 30px")]
    public async Task AFrameNeedsALayoutAfterOtherChanges(string id, string name, string value)
    {
        var (document, harness) = await ParseAsync(Page);
        harness.PrepareFrame();

        document.GetElementById(id)!.SetAttribute(name, value);

        Assert.Null(harness.UpdateFrame());
    }

    [Theory]
    [InlineData("position: fixed; right: 20px; bottom: 30px; width: 80px; height: 40px; background: rgb(0, 128, 0)", false)]
    [InlineData("display: none", false)]
    [InlineData("position: fixed; right: 20px; bottom: 30px; width: 80px; height: 40px; background: rgb(0, 128, 0)", true)]
    public async Task APatchedFrameShowsWhatALaidOutFrameShows(string style, bool measureFirst)
    {
        var (document, harness) = await ParseAsync(Page);
        var box = document.GetElementById("fixed")!;
        harness.PrepareFrame();

        box.SetAttribute("style", style);
        document.GetElementById("label")!.TextContent = "changed";

        if (measureFirst)
        {
            _ = box.GetBoundingClientRect();
        }

        var patched = harness.UpdateFrame();
        Assert.NotNull(patched);
        Assert.True(patched!.Metrics.IsIncremental);
        Assert.True(patched.Metrics.Layout > TimeSpan.Zero);
        var expected = harness.PrepareFrame();
        Assert.False(expected.Metrics.IsIncremental);

        Assert.Equal(expected.Rasterize().Data, patched!.Rasterize().Data);
        Assert.Equal(expected.HitTestMap.HitTest(330, 250)?.Element, patched.HitTestMap.HitTest(330, 250)?.Element);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingStyleSheetsGivesTheGeometryOfAFullLayout(bool addsSheetToOutOfFlowBox)
    {
        var (document, harness) = await ParseAsync(Page);
        var flow = document.GetElementById("flow")!;
        var top = flow.GetBoundingClientRect().Top;

        if (addsSheetToOutOfFlowBox)
        {
            var sheet = document.CreateElement("style");
            sheet.TextContent = "#flow { margin-top: 50px }";
            document.GetElementById("fixed")!.AppendChild(sheet);
        }
        else
        {
            document.Head!.QuerySelector("style")!.TextContent += "#flow { margin-top: 50px }";
        }

        Assert.True(flow.GetBoundingClientRect().Top > top);
        AssertSameAsFullLayout(document, harness, "flow", "next", "fixed");
    }

    [Theory]
    [InlineData("show")]
    [InlineData("show, measure, hide, show")]
    [InlineData("inner, outer")]
    [InlineData("outer, inner")]
    [InlineData("show, inner, hide")]
    public async Task APatchedFrameShowsWhatALaidOutFrameShowsAfter(string changes)
    {
        var (document, harness) = await ParseAsync(Page);
        var dropdown = document.GetElementById("dropdown")!;
        harness.PrepareFrame();

        foreach (var change in changes.Split(", "))
        {
            switch (change)
            {
                case "show":
                    dropdown.SetAttribute("style", "display: block; background: rgb(0, 0, 255)");
                    break;
                case "hide":
                    dropdown.RemoveAttribute("style");
                    break;
                case "measure":
                    _ = dropdown.GetBoundingClientRect();
                    break;
                case "inner":
                    document.GetElementById("badge")!.TextContent = "42";
                    _ = document.GetElementById("badge")!.GetBoundingClientRect();
                    break;
                case "outer":
                    document.GetElementById("fixed")!.SetAttribute("style", "position: fixed; right: 10px; bottom: 10px; width: 60px; height: 20px; background: rgb(0, 128, 0)");
                    _ = document.GetElementById("fixed")!.GetBoundingClientRect();
                    break;
            }
        }

        var patched = harness.UpdateFrame();
        Assert.NotNull(patched);
        var expected = harness.PrepareFrame();

        Assert.Equal(expected.Rasterize().Data, patched!.Rasterize().Data);
    }

    [Theory]
    [InlineData(1, "block")]
    [InlineData(2, "block")]
    [InlineData(3, "block")]
    [InlineData(1, "flex")]
    [InlineData(3, "flex")]
    [InlineData(1, "grid")]
    [InlineData(3, "grid")]
    public async Task AShownBoxPaintsInTheOrderOfItsZIndex(int zIndex, string display)
    {
        var (document, harness) = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; } #box { position: relative; display: {{display}}; height: 80px; } #box > div { position: absolute; top: 0; left: 0; width: 50px; height: 50px; }</style></head>
              <body><div id="box"><div style="z-index: 2; background: rgb(255, 0, 0)"></div><div id="shown" style="display: none"></div><div style="background: rgb(0, 128, 0); left: 25px"></div></div></body>
            </html>
            """);
        harness.PrepareFrame();

        document.GetElementById("shown")!.SetAttribute("style", $"z-index: {zIndex}; background: rgb(0, 0, 255); top: 10px");

        var patched = harness.UpdateFrame();
        Assert.NotNull(patched);
        Assert.Equal(harness.PrepareFrame().Rasterize().Data, patched!.Rasterize().Data);
    }

    [Fact]
    public async Task APatchedBoxInAScrolledContainerIsScrolledWithIt()
    {
        var (document, harness) = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; } #list { position: relative; overflow-y: auto; height: 100px; } #box { position: absolute; top: 80px; left: 0; width: 50px; height: 30px; background: rgb(255, 0, 0); }</style></head>
              <body><div id="list"><div style="height: 300px"></div><div id="box"></div></div></body>
            </html>
            """);
        document.GetElementById("list")!.SetScrollTop(60);
        harness.PrepareFrame();

        document.GetElementById("box")!.SetAttribute("style", "background: rgb(0, 0, 255)");

        var incremental = Describe(document.GetElementById("box")!);
        var patched = harness.UpdateFrame();
        Assert.NotNull(patched);
        Assert.Equal(harness.PrepareFrame().Rasterize().Data, patched!.Rasterize().Data);
        Assert.Equal("0.000,20.000,50.000,30.000", incremental);
    }

    private const string FlowPage = """
        <html>
          <head>
            <style>
              html, body { margin: 0; padding: 0; font-family: sans-serif; }
              p { margin: 8px 0; }
              #box { height: 100px; }
              .hot { color: rgb(200, 0, 0); }
              .tall { height: 40px; }
            </style>
          </head>
          <body>
            <p id="intro">Intro</p>
            <div id="outer"><p id="first">First paragraph</p><p id="second">Second <span id="word">word</span></p></div>
            <div id="box"><p id="boxed">Boxed</p><div id="hidden" style="display: none">Hidden</div></div>
            <div id="floats"><div id="float" style="float: left; width: 50px; height: 50px"></div>Beside</div>
            <p id="after">After</p>
          </body>
        </html>
        """;

    public static TheoryData<string, bool, Action<IDocument>> InFlowChanges => new()
    {
        // the same box from outside: laid out on its own
        { "the text of a word", true, document => document.GetElementById("word")!.TextContent = "text" },
        { "a class changing the color", true, document => document.GetElementById("second")!.ClassName = "hot" },
        { "a box growing in a box of a fixed height", true, document => document.GetElementById("boxed")!.ClassName = "tall" },
        { "a box shown in a box of a fixed height", true, document => document.GetElementById("hidden")!.RemoveAttribute("style") },
        { "text added to a paragraph", true, document => document.GetElementById("second")!.AppendChild(document.CreateTextNode("!")) },
        // moving the boxes after it: a full layout
        { "a box growing", false, document => document.GetElementById("second")!.ClassName = "tall" },
        { "a box shown", false, document => document.GetElementById("box")!.SetAttribute("style", "height: auto") },
        { "a margin the parent took", false, document => document.GetElementById("first")!.SetAttribute("style", "margin-top: 30px") },
        { "a counter counted through the document", false, document => document.GetElementById("second")!.SetAttribute("style", "counter-increment: item") },
        { "a float beside the boxes after it", false, document => document.GetElementById("float")!.SetAttribute("style", "float: left; width: 50px; height: 80px") },
    };

    [Theory]
    [MemberData(nameof(InFlowChanges))]
    public async Task InFlowChangesGiveTheGeometryAndFrameOfAFullLayout(string change, bool isPatched, Action<IDocument> apply)
    {
        string[] ids = ["intro", "outer", "first", "second", "word", "box", "boxed", "hidden", "floats", "float", "after"];
        var (document, harness) = await ParseAsync(FlowPage);
        harness.PrepareFrame();

        apply(document);

        var incremental = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();
        var patched = harness.UpdateFrame();
        Assert.True(isPatched == patched is not null, change);
        var expected = harness.PrepareFrame();
        Assert.Equal(ids.Select(id => Describe(document.GetElementById(id)!)), incremental);

        if (patched is not null)
        {
            Assert.Equal(expected.Rasterize().Data, patched.Rasterize().Data);
        }
    }

    [Theory]
    [InlineData("display: flex", "the text of a label", true)]
    [InlineData("display: flex", "a shorter label", true)]
    [InlineData("display: grid; grid-template-columns: auto 1fr", "the text of a label", true)]
    [InlineData("display: grid; grid-template-columns: auto 1fr", "a shorter label", true)]
    [InlineData("display: flex; align-items: baseline", "a shorter label", true)]
    public async Task ChangesInFlexAndGridItemsGiveTheGeometryAndFrameOfAFullLayout(string display, string change, bool isPatched)
    {
        string[] ids = ["row", "item", "label", "main", "text", "after"];
        var (document, harness) = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; font-family: sans-serif; } p { margin: 0; } #row { {{display}}; height: 60px; } #main { flex: 1; }</style></head>
              <body><div id="row"><div id="item"><p id="label">Label text</p></div><div id="main"><p id="text">Main text</p></div></div><p id="after">After</p></body>
            </html>
            """);
        harness.PrepareFrame();

        document.GetElementById(change == "a shorter label" ? "label" : "text")!.TextContent = change == "a shorter label" ? "Label" : "Main words";

        var incremental = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();
        var patched = harness.UpdateFrame();
        Assert.Equal(isPatched, patched is not null);
        var expected = harness.PrepareFrame();
        Assert.Equal(ids.Select(id => Describe(document.GetElementById(id)!)), incremental);

        if (patched is not null)
        {
            Assert.Equal(expected.Rasterize().Data, patched.Rasterize().Data);
        }
    }

    [Theory]
    [InlineData("<i>plain</i>", "text", true)]
    [InlineData("<b class=\"badge\">1</b>", "text", false)]
    [InlineData("<b class=\"alert\">!</b>", "other", false)]
    [InlineData("<i>plain</i>", "other", true)]
    public async Task ContentChangesGiveTheGeometryAndFrameOfAFullLayoutWithRelationalSelectors(string html, string parentId, bool isPatched)
    {
        string[] ids = ["card", "text", "other", "after"];
        var (document, harness) = await ParseAsync("""
            <html>
              <head><style>
                html, body { margin: 0; font-family: sans-serif; } p { margin: 0; }
                .card:has(.badge) { padding: 20px; }
                body:has(.alert) #after { color: rgb(255, 0, 0); }
              </style></head>
              <body><div class="card" id="card"><p id="text">Text</p></div><p id="other">Other</p><p id="after">After</p></body>
            </html>
            """);
        harness.PrepareFrame();

        document.GetElementById(parentId)!.InnerHtml += html;

        var incremental = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();
        var patched = harness.UpdateFrame();
        Assert.Equal(isPatched, patched is not null);
        var expected = harness.PrepareFrame();
        Assert.Equal(ids.Select(id => Describe(document.GetElementById(id)!)), incremental);

        if (patched is not null)
        {
            Assert.Equal(expected.Rasterize().Data, patched.Rasterize().Data);
        }
    }

    private const string ItemsPage = """
        <!doctype html>
        <html>
          <head>
            <style>
              html, body { margin: 0; font: 16px sans-serif; }
              .row { display: flex; gap: 4px; }
              .fixed { width: 120px; height: 40px; flex: none; overflow: hidden; }
              .auto { flex: none; }
              .grid { display: grid; grid-template-columns: 100px 100px; grid-auto-rows: 40px; }
              .fit { display: flex; width: fit-content; }
              .fit div { width: 100%; }
              .col { float: left; width: 150px; }
              .col::after { content: ""; display: block; clear: both; }
              .sticky { position: sticky; top: 0; }
              p { margin: 0; }
            </style>
          </head>
          <body>
            <div class="row" id="row"><div class="fixed" id="fixed"><span id="fixed-text">Fixed</span></div><div class="auto" id="auto">Auto</div><div class="auto" id="next">Next</div></div>
            <div class="row" id="stretched"><div class="auto" id="tall" style="width: 60px">Tall</div><div class="auto" id="beside" style="width: 60px; background: red">Beside</div></div>
            <div class="grid" id="grid"><div id="cell"><span id="cell-text">Cell</span></div><div id="cell2">Cell</div></div>
            <div class="row"><div class="fit" id="fit"><div id="fit-content"><ul style="margin: 0; padding: 0; display: flex"><li id="fit-item" style="display: block">Item</li></ul></div></div><div class="auto" id="after-fit">After</div></div>
            <p id="after">After</p>
            <div id="columns"><div class="col" id="col"><p id="col-text">Column</p></div><div class="col"><p>Column</p></div></div>
            <p id="below" style="clear: both">Below</p>
            <div class="sticky" id="sticky"><p id="sticky-text">Sticky</p></div>
            <div style="height: 2000px"></div>
          </body>
        </html>
        """;

    private static readonly string[] s_itemsPageIds =
    [
        "row", "fixed", "fixed-text", "auto", "next", "stretched", "tall", "beside", "grid", "cell", "cell2", "cell-text", "fit", "fit-content", "fit-item", "after-fit",
        "after", "columns", "col", "col-text", "below", "sticky", "sticky-text",
    ];

    [Theory]
    [InlineData("fixed-text", "Changed")]
    [InlineData("cell-text", "Changed")]
    [InlineData("col-text", "Changed")]
    [InlineData("sticky-text", "Changed")]
    public async Task ChangingTheContentOfAnItemLaysOutJustThatItem(string id, string text)
    {
        var (document, harness) = await ParseAsync(ItemsPage);
        var full = harness.PrepareFrame();

        document.GetElementById(id)!.TextContent = text;

        var patched = harness.UpdateFrame();
        Assert.NotNull(patched);
        Assert.True(patched!.Metrics.IsIncremental);
        Assert.True(patched.Metrics.StyleMaps < full.Metrics.StyleMaps / 3, $"{patched.Metrics.StyleMaps} of {full.Metrics.StyleMaps} elements styled");
        AssertPatchedLikeFullLayout(document, harness, patched);
    }

    [Theory]
    // the next item moves
    [InlineData("auto", "text", "A much longer text than before")]
    // the line gets higher, and the item beside it stretches along
    [InlineData("tall", "text", "A text that goes over several lines")]
    // the box around the item, which is as wide as its content, grows
    [InlineData("fit-item", "style", "display: block; padding-left: 7px")]
    [InlineData("fit-item", "text", "A longer item")]
    [InlineData("fixed", "style", "order: 5")]
    [InlineData("fixed", "style", "width: 200px")]
    [InlineData("fixed", "style", "margin-left: 9px")]
    [InlineData("fixed", "style", "position: absolute")]
    [InlineData("fixed", "style", "display: none")]
    [InlineData("cell", "style", "grid-column: 2")]
    [InlineData("cell", "style", "align-self: center")]
    [InlineData("cell", "style", "margin-top: 3px")]
    [InlineData("cell", "style", "display: none")]
    [InlineData("col", "style", "float: right")]
    public async Task ChangingWhatTheContainerReadsOfAnItemGivesTheLayoutOfAFullOne(string id, string kind, string value)
    {
        var (document, harness) = await ParseAsync(ItemsPage);
        harness.PrepareFrame();
        var element = document.GetElementById(id)!;

        if (kind == "text")
        {
            element.TextContent = value;
        }
        else
        {
            element.SetAttribute("style", value);
        }

        AssertPatchedLikeFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task ChangingAnItemAgainAfterItsPatchGivesTheLayoutOfAFullOne()
    {
        var (document, harness) = await ParseAsync(ItemsPage);
        harness.PrepareFrame();

        foreach (var (id, text) in new[] { ("fixed-text", "One"), ("cell-text", "Two"), ("fixed-text", "Three"), ("col-text", "Four"), ("auto", "A much longer text"), ("fixed-text", "Five") })
        {
            document.GetElementById(id)!.TextContent = text;
            var incremental = s_itemsPageIds.Select(other => Describe(document.GetElementById(other)!)).ToList();
            var patched = harness.UpdateFrame();

            if (patched is not null)
            {
                Assert.Equal(s_itemsPageIds.Select(other => Describe(document.GetElementById(other)!)), incremental);
            }
        }

        AssertPatchedLikeFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task ChangingAnItemOfABoxMeasuredAsInlineContentGivesTheLayoutOfAFullOne()
    {
        // GitHub's marquee: a grid as wide as its content, with text in nested inline grids
        var (document, harness) = await ParseAsync("""
            <!doctype html>
            <html>
              <head><style>html, body { margin: 0; font: 16px sans-serif; } ul { margin: 0; padding: 0; list-style: none; display: grid; grid-auto-flow: column; width: max-content; } li, div { display: grid; }</style></head>
              <body>
                <ul id="track"><li id="block"><div id="stat"><span id="value" style="display: inline-grid"><span id="text">12345</span></span></div></li><li id="other">Other</li></ul>
                <p id="after">After</p>
              </body>
            </html>
            """);
        harness.PrepareFrame();

        document.GetElementById("text")!.TextContent = "A much longer value than before";

        string[] ids = ["track", "block", "stat", "value", "text", "other", "after"];
        var incremental = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();
        var patched = harness.UpdateFrame();

        if (patched is not null)
        {
            incremental = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();
        }

        var expected = harness.PrepareFrame();
        Assert.Equal(ids.Select(id => Describe(document.GetElementById(id)!)), incremental);

        if (patched is not null)
        {
            Assert.Equal(expected.Rasterize().Data, patched.Rasterize().Data);
        }
    }

    [Theory]
    // a state not depending on the value: as long as nothing else changed, the field is patched
    [InlineData("input:checked + p { color: red; }", true)]
    // a state depending on the value, which changes the paragraph next to the field
    [InlineData("input:valid + p { margin-top: 30px; }", false)]
    // the placeholder goes with the first character
    [InlineData("input:placeholder-shown + p { margin-top: 30px; }", false)]
    public async Task TypingIntoATextFieldOnlyLaysOutMoreIfAStateOfItsValueIsUsed(string rule, bool isPatched)
    {
        var (document, harness) = await ParseAsync($$"""
            <!doctype html>
            <html>
              <head><style>html, body { margin: 0; font: 16px sans-serif; } p { margin: 0; } {{rule}}</style></head>
              <body><div style="display: flex"><input id="field" placeholder="Search"><p id="next">Next</p></div><p id="after">After</p></body>
            </html>
            """);
        var input = (AngleSharp.Html.Dom.IHtmlInputElement)document.GetElementById("field")!;
        harness.PrepareFrame();

        input.SetAttribute("value", "typed");
        input.Value = "typed";

        var patched = harness.UpdateFrame();
        Assert.Equal(isPatched, patched is not null);
        var incremental = Describe(document.GetElementById("next")!);
        var expected = harness.PrepareFrame();
        Assert.Equal(Describe(document.GetElementById("next")!), incremental);

        if (patched is not null)
        {
            Assert.Equal(expected.Rasterize().Data, patched.Rasterize().Data);
        }
    }

    [Fact]
    public async Task TypingMoreIntoATextFieldLeavesThePlaceholderSelectorsAlone()
    {
        var (document, harness) = await ParseAsync("""
            <!doctype html>
            <html>
              <head><style>html, body { margin: 0; font: 16px sans-serif; } p { margin: 0; } input:placeholder-shown + p { margin-top: 30px; }</style></head>
              <body><div style="display: flex"><input id="field" placeholder="Search" value="a"><p id="next">Next</p></div><p id="after">After</p></body>
            </html>
            """);
        var input = (AngleSharp.Html.Dom.IHtmlInputElement)document.GetElementById("field")!;
        harness.PrepareFrame();

        input.SetAttribute("value", "ab");
        input.Value = "ab";

        var patched = harness.UpdateFrame();
        Assert.NotNull(patched);
        var incremental = Describe(document.GetElementById("next")!);
        var expected = harness.PrepareFrame();
        Assert.Equal(Describe(document.GetElementById("next")!), incremental);
        Assert.Equal(expected.Rasterize().Data, patched!.Rasterize().Data);
    }

    [Fact]
    public async Task ABlockAfterAFloatThatEndedAboveItIsLaidOutOnItsOwn()
    {
        var (document, harness) = await ParseAsync("""
            <!doctype html>
            <html>
              <head><style>html, body { margin: 0; font: 16px sans-serif; } p { margin: 0; }</style></head>
              <body>
                <div style="float: left; width: 50px; height: 20px"></div>
                <div style="clear: both; height: 0"></div>
                <p id="first">First</p>
                <p id="second">Second</p>
              </body>
            </html>
            """);
        harness.PrepareFrame();

        document.GetElementById("first")!.TextContent = "Changed";

        var patched = harness.UpdateFrame();
        Assert.NotNull(patched);
        var expected = harness.PrepareFrame();
        Assert.Equal(expected.Rasterize().Data, patched!.Rasterize().Data);
    }

    [Fact]
    public async Task ABlockBesideAFloatIsNotLaidOutOnItsOwn()
    {
        var (document, harness) = await ParseAsync("""
            <!doctype html>
            <html>
              <head><style>html, body { margin: 0; font: 16px sans-serif; } p { margin: 0; }</style></head>
              <body>
                <div style="float: left; width: 50px; height: 200px; background: red"></div>
                <p id="first">First</p>
                <p id="second">Second</p>
              </body>
            </html>
            """);
        harness.PrepareFrame();

        document.GetElementById("first")!.TextContent = "A text going around the float";

        AssertSameAsFullLayout(document, harness, "first", "second");
    }

    private static void AssertPatchedLikeFullLayout(IDocument document, IDomHarness harness, AngleSharp.Renderer.Rendering.RenderedFrame? patched)
    {
        if (patched is null)
        {
            return;
        }

        var incremental = s_itemsPageIds.Select(id => document.GetElementById(id) is { } element ? Describe(element) : id).ToList();
        var expected = harness.PrepareFrame();
        Assert.False(expected.Metrics.IsIncremental);
        Assert.Equal(s_itemsPageIds.Select(id => document.GetElementById(id) is { } element ? Describe(element) : id), incremental);
        Assert.Equal(expected.Rasterize().Data, patched.Rasterize().Data);
    }

    private static void AssertSameAsFullLayout(IDocument document, IDomHarness harness, params string[] ids)
    {
        var incremental = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();

        // anything invalidating the paint drops the metrics
        harness.PaintToPng();
        var full = ids.Select(id => Describe(document.GetElementById(id)!)).ToList();

        Assert.Equal(full, incremental);
    }

    // Two full layouts already differ in the last bits of a float (e.g. 21.6 and 21.599998 for
    // the same line, depending on what else is on it), so the geometry is compared in thousandths.
    private static string Describe(IElement element)
    {
        var rect = element.GetBoundingClientRect();
        return FormattableString.Invariant($"{rect.Left:F3},{rect.Top:F3},{rect.Width:F3},{rect.Height:F3}");
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> ParseAsync(string html)
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
        var document = await context.OpenAsync(request => request.Content(html));
        return (document, context.GetDomHarness());
    }
}
