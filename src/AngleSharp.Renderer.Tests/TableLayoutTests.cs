namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Table layout asserted on the display list: the table box, <c>border-spacing</c>, column
/// widths, row heights and the collapsing border model. Cells set <c>padding: 0</c> wherever exact
/// geometry is asserted, so the results do not depend on the user-agent cell padding.
/// </summary>
public sealed class TableLayoutTests
{
    private static readonly RenderColor Red = new(255, 0, 0);
    private static readonly RenderColor Green = new(0, 255, 0);
    private static readonly RenderColor Blue = new(0, 0, 255);
    private static readonly RenderColor Yellow = new(255, 255, 0);

    [Fact]
    public async Task BorderSpacing_SeparatesCellsFromEachOtherAndFromThePaddingEdge()
    {
        var displayList = await LayoutAsync("""
            <table style="border-spacing:10px 5px; border:3px solid #0000ff; padding:4px; background-color:#ffff00">
              <tr>
                <td style="padding:0; width:20px; height:10px; background-color:#ff0000"></td>
                <td style="padding:0; width:30px; height:10px; background-color:#00ff00"></td>
              </tr>
            </table>
            """);

        var table = SingleFill(displayList, Yellow);
        var red = SingleFill(displayList, Red);
        var green = SingleFill(displayList, Green);

        Assert.Equal(new RenderRect(0f, 0f, 94f, 34f), table.Rect);
        Assert.Equal(new RenderRect(17f, 12f, 20f, 10f), red.Rect);
        Assert.Equal(new RenderRect(47f, 12f, 30f, 10f), green.Rect);
    }

    [Theory]
    [InlineData("", 200f)]
    [InlineData("box-sizing:content-box;", 220f)]
    public async Task TableWidth_IsABorderBoxWidthByDefault(string boxSizing, float expectedWidth)
    {
        var displayList = await LayoutAsync($$"""
            <table style="{{boxSizing}} width:200px; border:10px solid #0000ff; border-spacing:0; background-color:#ffff00">
              <tr><td style="padding:0; height:10px"></td></tr>
            </table>
            """);

        Assert.Equal(expectedWidth, SingleFill(displayList, Yellow).Rect.Width, precision: 2);
    }

    [Fact]
    public async Task Table_DoesNotInheritTextIndent()
    {
        var displayList = await LayoutAsync("""
            <div style="text-indent:40px">
              <table style="border-spacing:0"><tr><td style="padding:0; background-color:#ff0000">x</td></tr></table>
            </div>
            """);

        Assert.Equal(SingleFill(displayList, Red).Rect.X, SingleText(displayList, "x").X, precision: 2);
    }

    [Fact]
    public async Task AutoWidthTable_ShrinksToFitAndCentersWithAutoMargins()
    {
        var displayList = await LayoutAsync("""
            <table style="margin:0 auto; border-spacing:0; background-color:#ffff00">
              <tr><td style="padding:0; width:40px; height:10px"></td></tr>
            </table>
            """);

        Assert.Equal(new RenderRect(180f, 0f, 40f, 10f), SingleFill(displayList, Yellow).Rect);
    }

    [Fact]
    public async Task TableInsideAFloat_ContributesItsColumnsToTheFloatsWidth()
    {
        var displayList = await LayoutAsync("""
            <div style="float:left; background-color:#ff0000">
              <table style="border-spacing:0">
                <tr>
                  <td style="padding:0; width:50px; height:10px"></td>
                  <td style="padding:0; width:30px; height:10px"></td>
                </tr>
              </table>
            </div>
            """);

        Assert.Equal(80f, SingleFill(displayList, Red).Rect.Width);
    }

    [Fact]
    public async Task Cells_HaveNoBordersTheStylesheetDoesNotGiveThem()
    {
        var displayList = await LayoutAsync("<table><tr><td>A</td><td>B</td></tr></table>");

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(new RenderColor(0, 0, 0)));
    }

    [Fact]
    public async Task CellContent_IsLaidOutAsFlowContent()
    {
        var displayList = await LayoutAsync("""<table><tr><td>Plain <span style="color:#ff0000">Red</span><div>Below</div></td></tr></table>""");

        var plain = SingleText(displayList, "Plain");
        var red = SingleText(displayList, "Red");
        var below = SingleText(displayList, "Below");

        Assert.Equal(plain.Y, red.Y);
        Assert.True(red.X > plain.X);
        Assert.True(below.Y > plain.Y);
    }

    [Fact]
    public async Task BaselineAlignedCells_ShareTheirFirstBaseline()
    {
        var displayList = await LayoutAsync("""
            <table>
              <tr>
                <td style="vertical-align:baseline; font-size:32px">Big</td>
                <td style="vertical-align:baseline">small</td>
              </tr>
            </table>
            """);

        Assert.Equal(SingleText(displayList, "Big").Y, SingleText(displayList, "small").Y, precision: 2);
    }

    [Fact]
    public async Task FixedTableLayout_SharesTheWidthRegardlessOfContent()
    {
        var displayList = await LayoutAsync("""
            <table style="table-layout:fixed; width:200px; border-spacing:0">
              <tr>
                <td style="padding:0; height:10px; background-color:#ff0000">Averyveryverylongword</td>
                <td style="padding:0; height:10px; background-color:#00ff00">x</td>
              </tr>
            </table>
            """);

        Assert.Equal(100f, SingleFill(displayList, Red).Rect.Width);
        Assert.Equal(100f, SingleFill(displayList, Green).Rect.Width);
    }

    [Fact]
    public async Task AutoTableLayout_GivesWiderContentTheWiderColumn()
    {
        var displayList = await LayoutAsync("""
            <table style="width:300px; border-spacing:0">
              <tr>
                <td style="padding:0; height:10px; background-color:#ff0000">A rather long piece of text</td>
                <td style="padding:0; height:10px; background-color:#00ff00">x</td>
              </tr>
            </table>
            """);

        var red = SingleFill(displayList, Red);
        var green = SingleFill(displayList, Green);

        Assert.Equal(300f, red.Rect.Width + green.Rect.Width, precision: 2);
        Assert.True(red.Rect.Width > 4f * green.Rect.Width);
    }

    [Fact]
    public async Task CollapsedBorders_TheWiderBorderWinsTheSharedLine()
    {
        var displayList = await LayoutAsync("""
            <table style="border-collapse:collapse">
              <tr>
                <td style="padding:0; width:20px; height:10px; border:1px solid #ff0000"></td>
                <td style="padding:0; width:20px; height:10px; border:3px solid #0000ff"></td>
              </tr>
            </table>
            """);

        var fills = displayList.Commands.OfType<FillRectCommand>().ToList();

        // The red cell keeps half of its 1px outer line and half of the 3px shared line, so the
        // shared line is centred 0.5 + 0.5 + 20 + 1.5 = 22.5px from the table's left edge.
        Assert.Contains(fills, fill => fill.Color.Equals(Blue) && fill.Rect.X == 21f && fill.Rect.Width == 3f);
        Assert.DoesNotContain(fills, fill => fill.Color.Equals(Red) && fill.Rect.Height > fill.Rect.Width && fill.Rect.X > 1f);
    }

    [Fact]
    public async Task Captions_SitAboveOrBelowTheTable()
    {
        var top = await LayoutAsync("<table><caption>Title</caption><tr><td>Cell</td></tr></table>");
        var bottom = await LayoutAsync("""<table><caption style="caption-side:bottom">Title</caption><tr><td>Cell</td></tr></table>""");

        Assert.True(SingleText(top, "Title").Y < SingleText(top, "Cell").Y);
        Assert.True(SingleText(bottom, "Title").Y > SingleText(bottom, "Cell").Y);
    }

    [Fact]
    public async Task RowBackground_PaintsBehindItsCells()
    {
        var displayList = await LayoutAsync("""
            <table style="border-spacing:0">
              <tr style="background-color:#00ff00"><td style="padding:0; width:20px; height:10px"></td></tr>
            </table>
            """);

        Assert.Equal(new RenderRect(0f, 0f, 20f, 10f), SingleFill(displayList, Green).Rect);
    }

    [Fact]
    public async Task HeaderAndFooterGroups_FrameTheBodyWhateverTheirSourceOrder()
    {
        var displayList = await LayoutAsync("""
            <table>
              <tfoot><tr><td>Foot</td></tr></tfoot>
              <tbody><tr><td>Body</td></tr></tbody>
              <thead><tr><td>Head</td></tr></thead>
            </table>
            """);

        var head = SingleText(displayList, "Head").Y;
        var body = SingleText(displayList, "Body").Y;
        var foot = SingleText(displayList, "Foot").Y;

        Assert.True(head < body && body < foot, $"Expected head ({head}) above body ({body}) above foot ({foot}).");
    }

    [Fact]
    public async Task InlineTable_SitsOnTheLineBetweenItsNeighbours()
    {
        var displayList = await LayoutAsync("""
            <div>Before <table style="display:inline-table; border-spacing:0"><tr><td style="padding:0">Cell</td></tr></table> After</div>
            """);

        var before = SingleText(displayList, "Before");
        var cell = SingleText(displayList, "Cell");
        var after = SingleText(displayList, "After");

        Assert.Equal(before.Y, after.Y, precision: 2);
        Assert.Equal(before.Y, cell.Y, precision: 2);
        Assert.True(before.X < cell.X && cell.X < after.X);
    }

    [Fact]
    public async Task EmptyZeroWidthCell_StillHasItsHeight()
    {
        var displayList = await LayoutAsync("""
            <table style="border-spacing:2px; background-color:#ffff00"><tr><td style="padding:0; height:10px"></td></tr></table>
            """);

        Assert.Equal(new RenderRect(0f, 0f, 4f, 14f), SingleFill(displayList, Yellow).Rect);
    }

    [Fact]
    public async Task EmptyCellsHide_PaintsNeitherTheEmptyCellNorTheRowBehindIt()
    {
        var displayList = await LayoutAsync("""
            <table style="border-spacing:0; empty-cells:hide">
              <tr style="background-color:#0000ff">
                <td style="padding:0; width:20px; height:10px; background-color:#ff0000"> </td>
                <td style="padding:0; width:20px; height:10px">x</td>
              </tr>
            </table>
            """);

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
        Assert.Equal(20f, SingleFill(displayList, Blue).Rect.X);
    }

    [Fact]
    public async Task EmptyCellsHide_DoesNotApplyToCollapsedBorders()
    {
        var displayList = await LayoutAsync("""
            <table style="border-collapse:collapse; empty-cells:hide">
              <tr><td style="padding:0; width:20px; height:10px; background-color:#ff0000"></td></tr>
            </table>
            """);

        Assert.Single(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
    }

    [Fact]
    public async Task VisibilityCollapse_RemovesARowAndItsSpacing()
    {
        var displayList = await LayoutAsync("""
            <table style="border-spacing:5px; background-color:#ffff00">
              <tr><td style="padding:0; width:10px; height:10px"></td></tr>
              <tr style="visibility:collapse"><td style="padding:0; width:10px; height:30px; background-color:#ff0000"></td></tr>
              <tr><td style="padding:0; width:10px; height:10px; background-color:#00ff00"></td></tr>
            </table>
            """);

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
        Assert.Equal(20f, SingleFill(displayList, Green).Rect.Y);
        Assert.Equal(35f, SingleFill(displayList, Yellow).Rect.Height);
    }

    [Fact]
    public async Task VisibilityCollapse_RemovesAColumnAndItsSpacing()
    {
        var displayList = await LayoutAsync("""
            <table style="border-spacing:5px; background-color:#ffff00">
              <colgroup><col><col style="visibility:collapse"><col></colgroup>
              <tr>
                <td style="padding:0; width:10px; height:10px"></td>
                <td style="padding:0; width:30px; height:10px; background-color:#ff0000"></td>
                <td style="padding:0; width:10px; height:10px; background-color:#00ff00"></td>
              </tr>
            </table>
            """);

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
        Assert.Equal(20f, SingleFill(displayList, Green).Rect.X);
        Assert.Equal(35f, SingleFill(displayList, Yellow).Rect.Width);
    }

    [Fact]
    public async Task PercentageRowHeights_ShareTheTablesHeight()
    {
        var displayList = await LayoutAsync("""
            <table style="height:100px; border-spacing:0">
              <tr style="height:75%"><td style="padding:0; width:10px; background-color:#ff0000"></td></tr>
              <tr style="height:25%"><td style="padding:0; width:10px; background-color:#00ff00"></td></tr>
            </table>
            """);

        Assert.Equal(new RenderRect(0f, 0f, 10f, 75f), SingleFill(displayList, Red).Rect);
        Assert.Equal(new RenderRect(0f, 75f, 10f, 25f), SingleFill(displayList, Green).Rect);
    }

    [Fact]
    public async Task PercentageCellHeights_ShareTheTablesHeight()
    {
        var displayList = await LayoutAsync("""
            <table style="height:100px; border-spacing:0">
              <tr><td style="padding:0; width:10px; height:40%; background-color:#ff0000"></td></tr>
              <tr><td style="padding:0; width:10px; height:60%; background-color:#00ff00"></td></tr>
            </table>
            """);

        var red = SingleFill(displayList, Red).Rect;
        var green = SingleFill(displayList, Green).Rect;

        Assert.Equal(40f, red.Height, precision: 3);
        Assert.Equal(40f, green.Y, precision: 3);
        Assert.Equal(60f, green.Height, precision: 3);
    }

    [Fact]
    public async Task PercentageHeights_CountForNothingWithoutATableHeight()
    {
        var displayList = await LayoutAsync("""
            <table style="border-spacing:0; background-color:#ffff00">
              <tr style="height:50%"><td style="padding:0; width:200px; height:50%; background-color:#ff0000"></td></tr>
              <tr><td style="padding:0; width:200px; height:10px"></td></tr>
            </table>
            """);

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red) && fill.Rect.Height > 0f);
        Assert.Equal(10f, SingleFill(displayList, Yellow).Rect.Height);
    }

    [Theory]
    [InlineData("border-box", 100f, 60f)]
    [InlineData("content-box", 130f, 90f)]
    public async Task BoxSizing_AppliesToTheTableBox(string boxSizing, float expectedWidth, float expectedHeight)
    {
        var displayList = await LayoutAsync($"""
            <table style="box-sizing:{boxSizing}; width:100px; height:60px; padding:10px; border:5px solid #0000ff; border-spacing:0; background-color:#ffff00">
              <tr><td style="padding:0"></td></tr>
            </table>
            """);

        var table = SingleFill(displayList, Yellow).Rect;

        Assert.Equal(expectedWidth, table.Width);
        Assert.Equal(expectedHeight, table.Height);
    }

    [Theory]
    [InlineData("border-box", 50f)]
    [InlineData("content-box", 70f)]
    public async Task BoxSizing_AppliesToCellWidths(string boxSizing, float expectedWidth)
    {
        var displayList = await LayoutAsync($"""
            <table style="border-spacing:0">
              <tr><td style="box-sizing:{boxSizing}; width:50px; height:10px; padding:0 10px; background-color:#ff0000"></td></tr>
            </table>
            """);

        Assert.Equal(expectedWidth, SingleFill(displayList, Red).Rect.Width);
    }

    [Fact]
    public async Task TableMargins_CollapseWithTheirSiblings()
    {
        var displayList = await LayoutAsync("""
            <div style="height:10px; margin-bottom:20px"></div>
            <table style="margin-top:30px; border-spacing:0; background-color:#ffff00"><tr><td style="padding:0; width:10px; height:10px"></td></tr></table>
            """);

        Assert.Equal(40f, SingleFill(displayList, Yellow).Rect.Y);
    }

    private static async Task<DisplayList> LayoutAsync(string bodyHtml)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content($"<html><body style=\"margin:0\">{bodyHtml}</body></html>"));

        return new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 400,
            ViewPortHeight = 300,
            FontSize = 16f,
        });
    }

    private static FillRectCommand SingleFill(DisplayList displayList, RenderColor color) =>
        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(fill => fill.Color.Equals(color)));

    private static DrawTextCommand SingleText(DisplayList displayList, string text) =>
        Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(command => command.Text == text));
}
