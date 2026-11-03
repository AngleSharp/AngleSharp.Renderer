namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Reduced versions of what the pages in <c>cases/</c> showed to differ from a browser. The pages
/// themselves are only rendered by <see cref="CaseRenderingTests"/>; these pin the behavior, in the
/// bundled fonts so that they hold on any host.
/// </summary>
public sealed class CaseRegressionTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };

    private static readonly RenderColor Red = new(255, 0, 0);
    private static readonly RenderColor Green = new(0, 255, 0);
    private static readonly RenderColor Blue = new(0, 0, 255);

    [Theory]
    [InlineData("min-content")]
    [InlineData("max-content")]
    public async Task FlexItem_ContentSizeKeywordAsWidth_IsTheBaseSize(string keyword)
    {
        // test10: the reference is the same box sized by the block layout, outside of the container
        var document = await OpenAsync($$"""
            <div style="display: flex; align-items: flex-start">
              <div id="item" style="width: {{keyword}}; padding: 12px; border: 2px solid; box-sizing: border-box">MIN-CONTENT: supercalifragilistic</div>
            </div>
            <div id="block" style="width: {{keyword}}; padding: 12px; border: 2px solid; box-sizing: border-box">MIN-CONTENT: supercalifragilistic</div>
            """);

        var item = Rect(document, "item");
        var block = Rect(document, "block");

        Assert.Equal(block.Width, item.Width, precision: 1);
        Assert.Equal(block.Height, item.Height, precision: 1);
    }

    [Fact]
    public async Task FlexItem_MinContentWidth_WrapsAtTheLongestWord()
    {
        var document = await OpenAsync("""
            <div style="display: flex; align-items: flex-start">
              <div id="item" style="width: min-content">aaaa supercalifragilistic</div>
              <div id="word">supercalifragilistic</div>
            </div>
            <div id="line" style="width: 500px">aaaa supercalifragilistic</div>
            """);

        Assert.Equal(Rect(document, "word").Width, Rect(document, "item").Width, precision: 1);
        Assert.Equal(2 * Rect(document, "line").Height, Rect(document, "item").Height, precision: 1);
    }

    [Theory]
    [InlineData(1000, "max")]
    [InlineData(60, "min")]
    [InlineData(200, "available")]
    public async Task FlexItem_FitContentWidth_IsClampedByItsContent(int containerWidth, string expected)
    {
        const string Text = "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj kkkk llll mmmm nnnn oooo pppp supercalifragilistic";
        var document = await OpenAsync($$"""
            <div style="display: flex; align-items: flex-start; width: {{containerWidth}}px">
              <div id="item" style="width: fit-content; flex-shrink: 0">{{Text}}</div>
            </div>
            <div style="display: inline-block; width: min-content" id="min">{{Text}}</div>
            <div style="display: inline-block; width: max-content" id="max">{{Text}}</div>
            """);

        var width = expected switch
        {
            "max" => Rect(document, "max").Width,
            "min" => Rect(document, "min").Width,
            _ => containerWidth,
        };

        Assert.Equal(width, Rect(document, "item").Width, precision: 1);
    }

    [Theory]
    [InlineData("flex-basis: min-content")]
    [InlineData("flex: 0 0 auto; width: 1px; min-width: min-content")]
    [InlineData("flex: 0 0 auto; width: 500px; max-width: min-content")]
    public async Task FlexItem_ContentSizeKeywordsAsBasisAndLimits_AreResolved(string style)
    {
        var document = await OpenAsync($$"""
            <div style="display: flex; width: 700px">
              <div id="item" style="{{style}}">aaaa supercalifragilistic</div>
            </div>
            <div id="block" style="width: min-content">aaaa supercalifragilistic</div>
            """);

        Assert.Equal(Rect(document, "block").Width, Rect(document, "item").Width, precision: 1);
    }

    [Theory]
    [InlineData("stretch")]
    [InlineData("flex-start")]
    public async Task ColumnFlexItem_ContentSizeKeywordAsWidth_IsNotStretchedAndSetsTheHeight(string alignItems)
    {
        var document = await OpenAsync($$"""
            <div style="display: flex; flex-direction: column; align-items: {{alignItems}}; width: 500px">
              <div id="item" style="width: min-content">aaaa supercalifragilistic</div>
            </div>
            <div id="block" style="width: min-content">aaaa supercalifragilistic</div>
            """);

        Assert.Equal(Rect(document, "block").Width, Rect(document, "item").Width, precision: 1);
        Assert.Equal(Rect(document, "block").Height, Rect(document, "item").Height, precision: 1);
    }

    [Theory]
    [InlineData("min-content")]
    [InlineData("max-content")]
    public async Task GridItem_ContentSizeKeywordAsWidth_IsNotStretchedAndMayOverflow(string keyword)
    {
        var document = await OpenAsync($$"""
            <div style="display: grid; grid-template-columns: 100px">
              <div id="item" style="width: {{keyword}}">aaaa supercalifragilistic</div>
            </div>
            <div id="block" style="width: {{keyword}}">aaaa supercalifragilistic</div>
            """);

        Assert.Equal(Rect(document, "block").Width, Rect(document, "item").Width, precision: 1);
    }

    [Fact]
    public async Task PreWrap_KeepsIndentationAndRepeatedSpaces()
    {
        // test11: only `pre` kept them; `pre-wrap` went through word wrapping
        var displayList = await LayoutAsync("""
            <p style="margin: 0; white-space: pre-wrap; font-family: monospace">First line
                Second    line</p>
            """);

        Assert.Contains(displayList.Commands.OfType<DrawTextCommand>(), text => text.Text == "First line");
        Assert.Contains(displayList.Commands.OfType<DrawTextCommand>(), text => text.Text == "    Second    line");
    }

    [Theory]
    [InlineData("pre-wrap")]
    [InlineData("break-spaces")]
    public async Task PreservedSpaces_StillWrapAtTheEndOfTheLine(string whiteSpace)
    {
        var displayList = await LayoutAsync($$"""
            <p style="margin: 0; width: 100px; white-space: {{whiteSpace}}; font-family: monospace">  aaaa  bbbb  cccc  dddd  eeee</p>
            """);

        var lines = displayList.Commands.OfType<DrawTextCommand>().Select(text => text.Text).ToList();

        Assert.True(lines.Count > 1);
        Assert.StartsWith("  aaaa", lines[0]);
        Assert.Equal("aaaa  bbbb  cccc  dddd  eeee".Replace(" ", string.Empty), string.Concat(lines).Replace(" ", string.Empty));
    }

    [Fact]
    public async Task PreWrap_ALineOfOnlySpacesIsStillALine()
    {
        var document = await OpenAsync("""
            <p id="spaces" style="margin: 0; white-space: pre-wrap">a
               
            b</p>
            <p id="empty" style="margin: 0; white-space: pre-wrap">a

            b</p>
            """);

        Assert.Equal(Rect(document, "empty").Height, Rect(document, "spaces").Height, precision: 1);
    }

    [Fact]
    public async Task PreWrap_KeepsSpacesInTheInlineFormattingContextToo()
    {
        var document = await OpenAsync("""
            <div style="width: 500px; font-family: monospace">
              <span id="collapsed">a b</span><br>
              <p style="margin: 0; white-space: pre-wrap"><span id="kept">a    b</span></p>
            </div>
            """);

        Assert.True(Rect(document, "kept").Width > Rect(document, "collapsed").Width);
    }

    [Fact]
    public async Task TextDecoration_AllLinesAreCarriedToTheText()
    {
        var displayList = await LayoutAsync("""
            <p style="text-decoration-line: underline overline line-through">text</p>
            <p style="text-decoration-line: overline">over</p>
            <p>plain</p>
            """);

        var text = displayList.Commands.OfType<DrawTextCommand>().Single(command => command.Text == "text");
        var over = displayList.Commands.OfType<DrawTextCommand>().Single(command => command.Text == "over");
        var plain = displayList.Commands.OfType<DrawTextCommand>().Single(command => command.Text == "plain");

        Assert.True(text.Underline && text.Overline && text.StrikeThrough);
        Assert.True(over.Overline && !over.Underline && !over.StrikeThrough);
        Assert.False(plain.Underline || plain.Overline || plain.StrikeThrough);
    }

    [Theory]
    [InlineData("2px", 2f)]
    [InlineData("0.5em", 10f)]
    [InlineData("10%", 2f)]
    public async Task TextDecorationThickness_IsCarriedToTheText(string thickness, float expected)
    {
        var displayList = await LayoutAsync($$"""
            <p style="font-size: 20px; text-decoration-line: underline; text-decoration-thickness: {{thickness}}">text</p>
            """);

        Assert.Equal(expected, displayList.Commands.OfType<DrawTextCommand>().Single().DecorationThickness, precision: 3);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("from-font")]
    public async Task TextDecorationThickness_WithoutALengthIsLeftToTheFont(string thickness)
    {
        var displayList = await LayoutAsync($$"""
            <p style="text-decoration-line: underline; text-decoration-thickness: {{thickness}}">text</p>
            """);

        Assert.True(float.IsNaN(displayList.Commands.OfType<DrawTextCommand>().Single().DecorationThickness));
    }

    [Fact]
    public async Task TextDecoration_OfADescendantIsPaintedAsSetOnTheDecoratingBox()
    {
        // test11: the nested text kept the lines, but took its own color for them
        var displayList = await LayoutAsync("""
            <p style="color: #0000ff; text-decoration-line: underline; text-decoration-style: wavy; text-decoration-color: #ff0000; text-decoration-thickness: 2px">outer <span>inner</span> <span style="color: #00ff00">green</span></p>
            """);

        foreach (var text in displayList.Commands.OfType<DrawTextCommand>())
        {
            Assert.True(text.Underline);
            Assert.Equal(Red, text.DecorationColor);
            Assert.Equal(RenderTextDecorationStyle.Wavy, text.DecorationStyle);
            Assert.Equal(2f, text.DecorationThickness);
        }

        Assert.Contains(displayList.Commands.OfType<DrawTextCommand>(), text => text.Color == Green);
    }

    [Fact]
    public async Task TextDecoration_WithoutAColorTakesTheColorOfTheDecoratingBox()
    {
        var displayList = await LayoutAsync("""
            <p style="color: #ff0000; text-decoration-line: underline">outer <span style="color: #00ff00">inner</span></p>
            """);

        var inner = displayList.Commands.OfType<DrawTextCommand>().Single(text => text.Text.Contains("inner"));

        Assert.Equal(Green, inner.Color);
        Assert.Equal(Red, inner.DecorationColor);
    }

    [Fact]
    public async Task TextDecoration_OwnLinesDoNotKeepTheStyleOfTheBoxAround()
    {
        var displayList = await LayoutAsync("""
            <p style="text-decoration-line: underline; text-decoration-style: wavy; text-decoration-color: #ff0000">outer <span style="color: #00ff00; text-decoration-line: line-through">inner</span></p>
            """);

        var inner = displayList.Commands.OfType<DrawTextCommand>().Single(text => text.Text.Contains("inner"));

        Assert.Equal(RenderTextDecorationStyle.Solid, inner.DecorationStyle);
        Assert.Equal(Green, inner.DecorationColor);
    }

    [Fact]
    public async Task CollapsedBorders_OfTheSameWidth_TheTopAndLeftCellWin()
    {
        // test13: CSS 2.1 17.6.2.1; the line was painted in the color of the cell after it
        var displayList = await LayoutAsync("""
            <table style="border-collapse: collapse">
              <tr>
                <td style="padding: 0; width: 20px; height: 20px; border: 2px solid #ff0000"></td>
                <td style="padding: 0; width: 20px; height: 20px; border: 2px solid #00ff00"></td>
              </tr>
              <tr>
                <td style="padding: 0; width: 20px; height: 20px; border: 2px solid #0000ff"></td>
                <td style="padding: 0; width: 20px; height: 20px; border: 2px solid #0000ff"></td>
              </tr>
            </table>
            """);

        var lines = displayList.Commands.OfType<FillRectCommand>().Where(fill => fill.Paint is RenderColorPaint).ToList();
        var between = lines.Where(line => line.Rect.Height == 2f && Math.Abs(line.Rect.Y) > 1f && line.Rect.Y < 30f).ToList();
        var column = lines.Where(line => line.Rect.Width == 2f && Math.Abs(line.Rect.X) > 1f && line.Rect.X < 30f && line.Rect.Y < 10f).ToList();

        Assert.NotEmpty(between);
        Assert.DoesNotContain(between, line => ((RenderColorPaint)line.Paint).Color == Blue);
        Assert.NotEmpty(column);
        Assert.All(column, line => Assert.Equal(Red, ((RenderColorPaint)line.Paint).Color));
    }

    [Fact]
    public async Task CollapsedBorders_HorizontalLinesCrossTheVerticalOnes()
    {
        var displayList = await LayoutAsync("""
            <table style="border-collapse: collapse">
              <tr><td colspan="2" style="padding: 0; height: 20px; border: 2px solid #ff0000"></td></tr>
              <tr>
                <td style="padding: 0; width: 20px; height: 20px; border: 2px solid #00ff00"></td>
                <td style="padding: 0; width: 20px; height: 20px; border: 2px solid #00ff00"></td>
              </tr>
            </table>
            """);

        var lines = displayList.Commands.OfType<FillRectCommand>().Where(fill => fill.Paint is RenderColorPaint { Color.A: > 0 } && fill.Rect.Width < Device.ViewPortWidth).Select(fill => fill.Rect).ToList();
        var firstHorizontal = lines.FindIndex(rect => rect.Width > rect.Height);
        var lastVertical = lines.FindLastIndex(rect => rect.Height > rect.Width);

        Assert.True(lastVertical < firstHorizontal);
    }

    [Fact]
    public async Task Border_WithDifferentColors_MeetsOnTheDiagonals()
    {
        // test14: the left edge was painted over the whole corner
        var displayList = await LayoutAsync("""
            <div style="width: 100px; height: 50px; border-style: solid; border-width: 4px 6px; border-color: #ff0000 #00ff00"></div>
            """);

        var regions = displayList.Commands.OfType<PushClipShapeCommand>().Select(command => Assert.IsType<RenderClipPolygon>(command.Shape)).ToList();

        Assert.Equal(4, regions.Count);
        Assert.Equal(4, displayList.Commands.OfType<FillRingCommand>().Count());

        // the region of the top edge: its sides run through the outer and inner corners
        var top = regions[0].Points;
        Assert.Equal(4f / 6f, (0f - top[0].Y) / (0f - top[0].X), precision: 3);
        Assert.Equal(4f / 6f, top[3].Y / top[3].X, precision: 3);
        Assert.True(top[0].X < 0f && top[0].Y < 0f && top[3].X > 6f && top[3].Y > 4f);
    }

    [Theory]
    [InlineData("border: 4px solid #ff0000")]
    [InlineData("border-left: 4px solid #ff0000; border-top: 0 solid #00ff00")]
    public async Task Border_WithoutCornersBetweenColors_StaysPlainEdges(string border)
    {
        var displayList = await LayoutAsync($$"""<div style="width: 100px; height: 50px; {{border}}"></div>""");

        Assert.Empty(displayList.Commands.OfType<PushClipShapeCommand>());
    }

    [Fact]
    public async Task Border_WithATransparentEdge_LeavesATriangle()
    {
        var displayList = await LayoutAsync("""
            <div style="width: 0; height: 0; border: 10px solid transparent; border-top-color: #ff0000"></div>
            """);

        var region = Assert.IsType<RenderClipPolygon>(Assert.Single(displayList.Commands.OfType<PushClipShapeCommand>()).Shape);

        // the sides of the region meet in the middle of the box, on the diagonals of its corners
        Assert.Equal(1f, region.Points[0].Y / region.Points[0].X, precision: 3);
        Assert.Equal(1f, Assert.Single(displayList.Commands.OfType<FillRingCommand>()).Inner.Width + 1f, precision: 3);
    }

    [Theory]
    [InlineData("a b", "A| |B", "r|f|r")]
    [InlineData("a1b", "A|1|B", "r|f|r")]
    [InlineData("a.b", "A|.|B", "r|f|r")]
    [InlineData("Ab", "A|B", "f|r")]
    [InlineData("12 ab", "12 |AB", "f|r")]
    public async Task SmallCaps_OnlyLowercaseLettersAreReduced(string text, string runs, string sizes)
    {
        await AssertCapsRunsAsync("small-caps", text, runs, sizes);
    }

    [Theory]
    [InlineData("petite-caps")]
    public async Task PetiteCaps_FallBackToSmallCaps(string caps)
    {
        await AssertCapsRunsAsync(caps, "a1", "A|1", "r|f");
    }

    [Theory]
    [InlineData("all-small-caps")]
    [InlineData("all-petite-caps")]
    public async Task AllSmallCaps_ReduceEverything(string caps)
    {
        await AssertCapsRunsAsync(caps, "aB 1.c", "A|B 1.|C", "r|r|r");
    }

    [Fact]
    public async Task Unicase_ReducesEverythingButLowercaseLetters()
    {
        await AssertCapsRunsAsync("unicase", "aB 1c", "a|B 1|c", "f|r|f");
    }

    [Theory]
    [InlineData("\u00DF", "SS")]
    [InlineData("\uFB01", "FI")]
    [InlineData("\uFB03", "FFI")]
    public async Task SmallCaps_UseTheFullUppercaseMapping(string text, string expected)
    {
        await AssertCapsRunsAsync("small-caps", text, expected, "r");
    }

    [Fact]
    public async Task SmallCaps_KeepACombiningMarkWithItsLetter()
    {
        await AssertCapsRunsAsync("small-caps", "a\u0301b", "A\u0301B", "r");
    }

    [Fact]
    public async Task SmallCaps_MeasureAsTheyArePainted()
    {
        var document = await OpenAsync("""
            <div style="font-size: 20px"><span id="caps" style="font-variant-caps: small-caps">a1 b</span></div>
            <div style="font-size: 14px"><span id="a">A</span></div>
            <div style="font-size: 20px"><span id="middle" style="white-space: pre">1 </span></div>
            <div style="font-size: 14px"><span id="b">B</span></div>
            """);

        Assert.Equal(
            Rect(document, "a").Width + Rect(document, "middle").Width + Rect(document, "b").Width,
            Rect(document, "caps").Width,
            precision: 1);
    }

    [Fact]
    public async Task SmallCaps_HaveLetterSpacingWhereTheSizeChanges()
    {
        var displayList = await LayoutAsync("""
            <p style="margin: 0; font-size: 20px; font-variant-caps: small-caps; letter-spacing: 3px">ab1</p>
            <p style="margin: 0; font-size: 14px; letter-spacing: 3px"><span id="reduced">AB</span></p>
            """);

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();
        var document = await OpenAsync("""<span id="reduced" style="font-size: 14px; letter-spacing: 3px">AB</span>""");

        Assert.Equal(Rect(document, "reduced").Width, texts.Single(text => text.Text == "1").X, precision: 1);
    }

    [Theory]
    [InlineData("underline")]
    [InlineData("overline")]
    [InlineData("line-through")]
    public async Task SmallCaps_DecorationCrossesTheirSpaces(string line)
    {
        var displayList = await LayoutAsync($$"""
            <p style="margin: 0; font-size: 20px; font-variant-caps: small-caps; text-decoration-line: {{line}}">a b</p>
            """);

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();

        Assert.Equal(["A", " ", "B"], texts.Select(text => text.Text));
        Assert.All(texts, text => Assert.True(text.Underline || text.Overline || text.StrikeThrough));

        // one line along the text, at the place and in the thickness of the full size
        Assert.All(texts, text => Assert.Equal(20f, text.DecorationFontSize));
    }

    [Fact]
    public async Task SmallCaps_UndecoratedSpacesPaintNothing()
    {
        var displayList = await LayoutAsync("""<p style="margin: 0; font-variant-caps: small-caps">a b</p>""");

        Assert.Equal(["A", "B"], displayList.Commands.OfType<DrawTextCommand>().Select(text => text.Text));
    }

    private static async Task AssertCapsRunsAsync(string caps, string text, string runs, string sizes)
    {
        var displayList = await LayoutAsync($$"""
            <p style="margin: 0; font-size: 20px; white-space: pre; text-decoration-line: underline; font-variant-caps: {{caps}}">{{text}}</p>
            """);

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();

        Assert.Equal(runs.Split('|'), texts.Select(command => command.Text));
        Assert.Equal(sizes.Split('|'), texts.Select(command => Math.Abs(command.FontSize - 14f) < 0.01f ? "r" : "f"));
    }

    private static readonly RenderColor BoxColor = new(255, 221, 136);

    [Fact]
    public async Task InlineBox_InARightToLeftLine_IsPaintedAroundItsText()
    {
        // the box took x = 0 from the pieces reordering had copied
        var displayList = await LayoutAsync("""
            <div style="direction: rtl; width: 300px"><span style="background: #ffdd88">مرحبا بالعالم</span></div>
            """);

        var box = SingleBox(displayList);
        var text = displayList.Commands.OfType<DrawTextCommand>().Single();

        Assert.Equal(text.X, box.X, precision: 2);
        Assert.Equal(300f, box.X + box.Width, precision: 1);
    }

    [Fact]
    public async Task InlineBox_WithReorderedText_StaysInItsContainer()
    {
        var displayList = await LayoutAsync("""
            <div style="padding-left: 40px; width: 300px"><span style="background: #ffdd88">abc مرحبا def</span></div>
            """);

        Assert.Equal(40f, SingleBox(displayList).X, precision: 2);
    }

    [Theory]
    [InlineData("padding-left: 20px; padding-right: 5px", 20f, 300f)]
    [InlineData("margin-left: 30px; margin-right: 12px", 0f, 288f)]
    [InlineData("border-left: 4px solid #00ff00; border-right: 8px solid #00ff00", 4f, 300f)]
    public async Task InlineBox_InARightToLeftLine_KeepsItsLeftAndRightEdges(string style, float left, float boxRight)
    {
        var displayList = await LayoutAsync($$"""
            <div style="direction: rtl; width: 300px"><span style="background: #ffdd88; {{style}}">مرحبا</span></div>
            """);

        var box = SingleBox(displayList);
        var text = displayList.Commands.OfType<DrawTextCommand>().Single();

        Assert.Equal(left, text.X - box.X, precision: 2);
        Assert.Equal(boxRight, box.X + box.Width, precision: 1);
    }

    [Fact]
    public async Task WrappedInlineBox_InARightToLeftLine_StartsAtTheRightWithItsRightEdge()
    {
        const string Word = "مرحبا";
        var displayList = await LayoutAsync($$"""
            <div style="direction: rtl; width: 100px"><span style="background: #ffdd88; padding-left: 20px; padding-right: 5px">{{Word}} {{Word}} {{Word}} {{Word}}</span></div>
            """);

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();
        var boxes = displayList.Commands.OfType<FillRectCommand>().Where(fill => fill.Paint is RenderColorPaint paint && paint.Color == BoxColor).Select(fill => fill.Rect).ToList();

        Assert.True(boxes.Count >= 2);

        // the first line has only the right padding, at the right; the last only the left one
        Assert.Equal(100f, boxes[0].X + boxes[0].Width, precision: 1);
        Assert.Equal(texts[0].X, boxes[0].X, precision: 2);
        Assert.Equal(20f, texts[^1].X - boxes[^1].X, precision: 2);
    }

    [Fact]
    public async Task InlineBox_DirectionWithoutEmbedding_KeepsItsLeftEdgeOnTheLeft()
    {
        // `direction` alone reorders nothing, so the box starts at the left here
        var displayList = await LayoutAsync("""
            <div style="width: 300px"><span style="direction: rtl; background: #ffdd88; padding-left: 20px; padding-right: 5px">abc</span></div>
            """);

        Assert.Equal(20f, displayList.Commands.OfType<DrawTextCommand>().Single().X - SingleBox(displayList).X, precision: 2);
    }

    private static RenderRect SingleBox(DisplayList displayList) =>
        displayList.Commands.OfType<FillRectCommand>().Single(fill => fill.Paint is RenderColorPaint paint && paint.Color == BoxColor).Rect;

    private static RenderRect Rect(IDocument document, string id)
    {
        var rect = document.GetElementById(id)!.GetBoundingClientRect();
        return new RenderRect((float)rect.Left, (float)rect.Top, (float)rect.Width, (float)rect.Height);
    }

    private static async Task<DisplayList> LayoutAsync(string body)
    {
        var document = await OpenAsync(body);
        return new HtmlRenderer().BuildDisplayList(document, Device);
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
