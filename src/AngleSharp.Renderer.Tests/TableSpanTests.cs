namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Geometry of <c>colspan</c> and <c>rowspan</c>, asserted on the display list so the checks do
/// not depend on how any particular platform rasterizes the result.
/// </summary>
public sealed class TableSpanTests
{
    private const string TableCss =
        "html, body { margin: 0; padding: 0; } table { border-collapse: collapse; width: 180px; } " +
        "td { padding: 6px; border: 1px solid #222; background-color: #eef7ff; }";

    [Fact]
    public async Task ColspanCell_CoversEveryColumnItSpans()
    {
        var cells = await LayoutCellsAsync("""
            <table>
              <tr><td colspan="2">Header</td></tr>
              <tr><td>Left</td><td>Right</td></tr>
            </table>
            """);

        var header = cells["Header"];
        var left = cells["Left"];
        var right = cells["Right"];

        Assert.Equal(left.Width + right.Width, header.Width, tolerance: 0.5f);
        Assert.Equal(left.X, header.X, tolerance: 0.5f);
        Assert.Equal(right.X + right.Width, header.X + header.Width, tolerance: 0.5f);
    }

    [Fact]
    public async Task RowspanCell_CoversEveryRowItSpans()
    {
        var cells = await LayoutCellsAsync("""
            <table>
              <tr><td rowspan="2">Left</td><td>Right</td></tr>
              <tr><td>Bottom</td></tr>
            </table>
            """);

        var left = cells["Left"];
        var right = cells["Right"];
        var bottom = cells["Bottom"];

        Assert.Equal(right.Height + bottom.Height, left.Height, tolerance: 0.5f);
        Assert.Equal(right.Y, left.Y, tolerance: 0.5f);
        Assert.Equal(bottom.Y + bottom.Height, left.Y + left.Height, tolerance: 0.5f);
    }

    [Fact]
    public async Task TallRowspanCell_IsSharedAcrossItsRowsRatherThanRepeated()
    {
        // The cell is taller than either row needs on its own. Its height has to be satisfied by
        // the spanned rows together; giving each row the full height would double the table.
        const string Long = "one two three four five six seven eight nine ten eleven twelve";

        // The text wraps, so it arrives as several draw commands; the cell is identified by being
        // the tallest background rather than by its text.
        var reference = await TallestCellAsync($"""
            <table>
              <tr><td>{Long}</td><td>a</td></tr>
            </table>
            """);

        var spanned = await TallestCellAsync($"""
            <table>
              <tr><td rowspan="2">{Long}</td><td>a</td></tr>
              <tr><td>b</td></tr>
            </table>
            """);

        Assert.True(spanned > 40f, "The sample text was expected to wrap to several lines.");
        Assert.True(spanned <= reference + 0.5f,
            $"The spanning cell grew to {spanned:F1}px against a required {reference:F1}px, " +
            "so its height was applied to every spanned row instead of being shared between them.");
    }

    [Fact]
    public async Task SpannedSlot_IsNotPaintedAsItsOwnCell()
    {
        var document = await ParseAsync("""
            <table>
              <tr><td colspan="2">Header</td></tr>
              <tr><td rowspan="2">Left</td><td>Right</td></tr>
              <tr><td>Bottom</td></tr>
            </table>
            """);

        // Four declared cells, so exactly four cell backgrounds. A phantom cell in a covered slot
        // would show up as a fifth.
        Assert.Equal(4, CellBackgrounds(new HtmlRenderer().BuildDisplayList(document, Device())).Count());
    }

    [Fact]
    public async Task SpannedSlot_HoldsNoSeparateCell()
    {
        var cells = await LayoutCellsAsync("""
            <table>
              <tr><td colspan="2">Header</td></tr>
              <tr><td rowspan="2">Left</td><td>Right</td></tr>
              <tr><td>Bottom</td></tr>
            </table>
            """);

        Assert.Equal(["Bottom", "Header", "Left", "Right"], cells.Keys.Order(StringComparer.Ordinal));

        // Nothing is painted in the slot the rowspan covers.
        var left = cells["Left"];
        var bottom = cells["Bottom"];

        Assert.True(bottom.X >= left.X + left.Width - 0.5f,
            "The cell after a rowspan must start beyond the spanning cell, not inside it.");
    }

    [Fact]
    public async Task EmptyRow_UnderARowspan_DoesNotThrow()
    {
        // The occupancy carried into the next row used to index past the end of a shorter row.
        var cells = await LayoutCellsAsync("""
            <table>
              <tr><td rowspan="3">Tall</td><td>Side</td></tr>
              <tr></tr>
              <tr><td>Last</td></tr>
            </table>
            """);

        Assert.Contains("Tall", cells.Keys);
        Assert.Contains("Last", cells.Keys);
    }

    [Fact]
    public async Task CollapsedBorders_DoNotCrossASpanningCell()
    {
        var document = await ParseAsync("""
            <table>
              <tr><td colspan="2">Header</td></tr>
              <tr><td>Left</td><td>Right</td></tr>
            </table>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, Device());
        var cells = CellRects(displayList);
        var header = cells["Header"];

        // A vertical rule inside the header would be a border painted across the span.
        var crossing = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => IsBorderPaint(command))
            .Where(command => command.Rect.Width <= 2f)
            .Where(command => command.Rect.X > header.X + 0.5f && command.Rect.X < header.X + header.Width - 1.5f)
            .Where(command => command.Rect.Y < header.Y + header.Height - 0.5f && command.Rect.Y + command.Rect.Height > header.Y + 0.5f)
            .ToArray();

        Assert.True(crossing.Length == 0,
            $"{crossing.Length} vertical border(s) painted across the spanning header cell.");
    }

    private static bool IsBorderPaint(FillRectCommand command) =>
        command.Paint is RenderColorPaint { Color: { R: 0, G: 0, B: 0, A: 255 } };

    private static DefaultRenderDevice Device() =>
        new() { ViewPortWidth = 220, ViewPortHeight = 200 };

    private static async Task<float> TallestCellAsync(string tableHtml) =>
        CellBackgrounds(new HtmlRenderer().BuildDisplayList(await ParseAsync(tableHtml), Device()))
            .Max(rect => rect.Height);

    private static async Task<Dictionary<string, RenderRect>> LayoutCellsAsync(string tableHtml) =>
        CellRects(new HtmlRenderer().BuildDisplayList(await ParseAsync(tableHtml), Device()));

    [Fact]
    public async Task CellContent_IsCenteredByDefault()
    {
        // The UA stylesheet gives cells vertical-align: middle, so the default has to match an
        // explicit middle rather than pin the text to the top.
        Assert.Equal(
            await TextBaselineAsync(AlignedTable("middle"), "Left"),
            await TextBaselineAsync(AlignedTable(null), "Left"),
            tolerance: 0.01f);
    }

    [Fact]
    public async Task VerticalAlign_PutsMiddleHalfwayBetweenTopAndBottom()
    {
        // Stated as a relation between the three keywords, so it holds whatever the line height
        // works out to. The reported Y is a baseline, which is not the centre of the line box.
        var top = await TextBaselineAsync(AlignedTable("top"), "Left");
        var middle = await TextBaselineAsync(AlignedTable("middle"), "Left");
        var bottom = await TextBaselineAsync(AlignedTable("bottom"), "Left");

        Assert.True(top < middle && middle < bottom,
            $"Expected top ({top:F1}) above middle ({middle:F1}) above bottom ({bottom:F1}).");
        Assert.Equal(bottom - middle, middle - top, tolerance: 0.5f);
    }

    [Fact]
    public async Task VerticalAlign_Top_MatchesANonSpanningCell()
    {
        // Aligned to the top, a cell spanning two rows has to offset its text exactly like any
        // other top-aligned cell: by its border and padding alone.
        var cells = await LayoutCellsAsync(AlignedTable("top", alignEveryCell: true));
        var left = await TextBaselineAsync(AlignedTable("top", alignEveryCell: true), "Left");
        var right = await TextBaselineAsync(AlignedTable("top", alignEveryCell: true), "Right");

        Assert.Equal(right - cells["Right"].Y, left - cells["Left"].Y, tolerance: 0.01f);
    }

    [Fact]
    public async Task VerticalAlign_Bottom_MatchesANonSpanningCell()
    {
        var cells = await LayoutCellsAsync(AlignedTable("bottom", alignEveryCell: true));
        var left = await TextBaselineAsync(AlignedTable("bottom", alignEveryCell: true), "Left");
        var right = await TextBaselineAsync(AlignedTable("bottom", alignEveryCell: true), "Right");

        var leftCell = cells["Left"];
        var rightCell = cells["Right"];

        Assert.Equal(rightCell.Y + rightCell.Height - right, leftCell.Y + leftCell.Height - left, tolerance: 0.01f);
    }

    private static string AlignedTable(string? verticalAlign, bool alignEveryCell = false)
    {
        var style = verticalAlign is null ? string.Empty : $" style=\"vertical-align:{verticalAlign}\"";
        var otherStyle = alignEveryCell ? style : string.Empty;

        return $"""
            <table>
              <tr><td rowspan="2"{style}>Left</td><td{otherStyle}>Right</td></tr>
              <tr><td{otherStyle}>Bottom</td></tr>
            </table>
            """;
    }

    private static async Task<float> TextBaselineAsync(string tableHtml, string text)
    {
        var displayList = new HtmlRenderer().BuildDisplayList(await ParseAsync(tableHtml), Device());

        return displayList.Commands.OfType<DrawTextCommand>().First(command => command.Text == text).Y;
    }

    private static IEnumerable<RenderRect> CellBackgrounds(DisplayList displayList) =>
        displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Paint is RenderColorPaint { Color: { R: 238, G: 247, B: 255, A: 255 } })
            .Select(command => command.Rect);

    private static Dictionary<string, RenderRect> CellRects(DisplayList displayList)
    {
        var backgrounds = CellBackgrounds(displayList).ToList();
        var cells = new Dictionary<string, RenderRect>(StringComparer.Ordinal);

        foreach (var text in displayList.Commands.OfType<DrawTextCommand>())
        {
            // The page background contains every cell, so the enclosing rectangle of least area is
            // the one that actually belongs to this text.
            var match = backgrounds
                .Where(rect => text.X >= rect.X && text.X <= rect.X + rect.Width &&
                               text.Y >= rect.Y && text.Y <= rect.Y + rect.Height)
                .OrderBy(rect => rect.Width * rect.Height)
                .ToArray();

            if (match.Length > 0)
            {
                cells[text.Text] = match[0];
            }
        }

        return cells;
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string tableHtml)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());

        return await context.OpenAsync(request => request.Content($$"""
            <html><head><style>{{TableCss}}</style></head><body>{{tableHtml}}</body></html>
            """));
    }
}
