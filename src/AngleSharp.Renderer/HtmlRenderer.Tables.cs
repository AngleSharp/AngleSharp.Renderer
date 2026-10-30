namespace AngleSharp.Renderer;

using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Tables (<c>display: table</c>/<c>inline-table</c> and the table-internal display types).
/// </summary>
/// <remarks>
/// The structure (row groups, rows, cells, columns, captions) comes from the computed
/// <c>display</c>, never from tag names; only <c>colspan</c>/<c>rowspan</c>/<c>span</c> are read
/// from attributes, as element semantics. Column widths follow the automatic table layout (each
/// column between its min-content and max-content width, the table itself shrink-to-fit unless it
/// has a width) or <c>table-layout: fixed</c>. Cells are laid out as ordinary block containers,
/// stretched to the rows they span, with their content shifted by <c>vertical-align</c>.
/// </remarks>
public sealed partial class HtmlRenderer
{
    /// <summary>
    /// Where a cell's content sits within the box the cell occupies.
    /// </summary>
    private enum CellVerticalAlign
    {
        Baseline,
        Top,
        Middle,
        Bottom,
    }

    private sealed record TableRowInfo(ElementRenderNode Node, ElementRenderNode? Group);

    private sealed record TableColumnInfo(ElementRenderNode? Node, ElementRenderNode? Group);

    private sealed class TableCellInfo(ElementRenderNode node, Dictionary<string, string> style, int row, int column, int rowSpan, int columnSpan)
    {
        public ElementRenderNode Node { get; } = node;

        public Dictionary<string, string> Style { get; } = style;

        public int Row { get; } = row;

        public int Column { get; } = column;

        public int RowSpan { get; } = rowSpan;

        public int ColumnSpan { get; } = columnSpan;

        /// <summary>Collapsing border model: the full width of the grid line along each edge.</summary>
        public EdgeSizes LineWidths { get; set; }

        /// <summary>Collapsing border model: the color of the border that won each edge.</summary>
        public EdgeBorderColor LineColors { get; set; }

        /// <summary>Collapsing border model: no cell to the right paints this cell's right line.</summary>
        public bool PaintsRightLine { get; set; }

        /// <summary>Collapsing border model: no cell below paints this cell's bottom line.</summary>
        public bool PaintsBottomLine { get; set; }

        /// <summary>
        /// A percentage <c>height</c>, taken out of <see cref="Style"/> because it refers to the
        /// table's height rather than to anything <see cref="LayoutElement"/> knows; NaN if none.
        /// </summary>
        public float PercentHeight { get; set; } = float.NaN;
    }

    private sealed class TableStructure
    {
        public required List<TableRowInfo> Rows { get; init; }

        public required List<TableCellInfo> Cells { get; init; }

        public required List<TableColumnInfo> Columns { get; init; }

        public required List<ElementRenderNode> TopCaptions { get; init; }

        public required List<ElementRenderNode> BottomCaptions { get; init; }

        public required int ColumnCount { get; init; }

        public required bool CollapseBorders { get; init; }
    }

    /// <summary>
    /// The detached elements standing in for anonymous table boxes, so hit testing can skip them.
    /// </summary>
    private static readonly ConditionalWeakTable<IElement, object> s_anonymousTableBoxes = new();

    private static bool IsAnonymousTableBox(IElement element) => s_anonymousTableBoxes.TryGetValue(element, out _);

    private static string GetTableRole(IRenderNode node) =>
        node is ElementRenderNode element ? element.ComputedStyle.GetDisplay()?.Trim().ToLowerInvariant() ?? string.Empty : string.Empty;

    private static bool IsRowGroupRole(string role) => role is "table-row-group" or "table-header-group" or "table-footer-group";

    /// <summary>The children a table box takes as they are (CSS 2.1 §17.2.1, "proper table child").</summary>
    private static bool IsProperTableChildRole(string role) =>
        role is "table-row" or "table-caption" or "table-column" or "table-column-group" || IsRowGroupRole(role);

    /// <summary>
    /// Nodes that never generate a box of their own: collapsible white space and
    /// <c>display: none</c> elements. They neither start nor break a run of table parts.
    /// </summary>
    private static bool IsTableNeutral(IRenderNode node) => node switch
    {
        TextRenderNode text => NormalizeWhitespace(text.Ref.Data).Length == 0,
        ElementRenderNode => !GeneratesBoxes(node),
        _ => false,
    };

    /// <summary>
    /// Generates the anonymous table objects of CSS 2.1 §17.2.1 in the render tree, so that the
    /// table layout only ever sees well-formed tables: content of a table or row group that is
    /// not a row is wrapped in an anonymous row, content of a row that is not a cell in an
    /// anonymous cell, consecutive cells outside a row in an anonymous row, and consecutive rows,
    /// row groups, captions and columns outside a table in an anonymous table. White space between
    /// table parts is dropped. An anonymous box is a detached element whose computed style holds
    /// its <c>display</c> plus the inherited properties of its parent, so everything else is its
    /// initial value. Runs once per render, before anything reads the tree; a second pass would
    /// find nothing left to wrap.
    /// </summary>
    private static void BuildAnonymousTableBoxes(IRenderNode node)
    {
        if (node is ElementRenderNode element && element.Children is List<IRenderNode> children &&
            element.Ref is not IPseudoElement && GeneratesBoxes(element) && !IsReplacedElement(element.Ref))
        {
            var role = GetTableRole(element);

            switch (role)
            {
                case "table" or "inline-table":
                    WrapMissingChildren(element, children, child => !IsProperTableChildRole(GetTableRole(child)), "table-row");
                    break;
                case "table-row-group" or "table-header-group" or "table-footer-group":
                    WrapMissingChildren(element, children, child => GetTableRole(child) != "table-row", "table-row");
                    break;
                case "table-row":
                    WrapMissingChildren(element, children, child => GetTableRole(child) != "table-cell", "table-cell");
                    break;
                case "table-column" or "table-column-group":
                    break;
                default:
                    WrapMissingParents(element, children, child => GetTableRole(child) == "table-cell", "table-row");
                    WrapMissingParents(element, children, child => IsProperTableChildRole(GetTableRole(child)), role is "inline" ? "inline-table" : "table");
                    break;
            }
        }

        foreach (var child in node.Children)
        {
            BuildAnonymousTableBoxes(child);
        }
    }

    /// <summary>
    /// Wraps every run of children matching <paramref name="isMisplaced"/> into an anonymous box.
    /// A run that would generate nothing (white space, <c>display: none</c>) is not wrapped and
    /// its white space is dropped.
    /// </summary>
    private static void WrapMissingChildren(ElementRenderNode parent, List<IRenderNode> children, Func<IRenderNode, bool> isMisplaced, string display)
    {
        var result = new List<IRenderNode>(children.Count);
        var run = new List<IRenderNode>();

        void Flush()
        {
            if (run.All(IsTableNeutral))
            {
                result.AddRange(run.Where(node => node is not TextRenderNode));
            }
            else
            {
                result.Add(CreateAnonymousTableBox(parent, display, run));
            }

            run = [];
        }

        foreach (var child in children)
        {
            if (isMisplaced(child))
            {
                run.Add(child);
                continue;
            }

            Flush();
            result.Add(child);
        }

        Flush();
        children.Clear();
        children.AddRange(result);
    }

    /// <summary>
    /// Wraps every run of consecutive children matching <paramref name="isOrphan"/> into an
    /// anonymous box. White space and <c>display: none</c> elements between them belong to the run
    /// (the white space is dropped); those before the first and after the last stay outside.
    /// </summary>
    private static void WrapMissingParents(ElementRenderNode parent, List<IRenderNode> children, Func<IRenderNode, bool> isOrphan, string display)
    {
        if (!children.Any(isOrphan))
        {
            return;
        }

        var result = new List<IRenderNode>(children.Count);
        var index = 0;

        while (index < children.Count)
        {
            if (!isOrphan(children[index]))
            {
                result.Add(children[index++]);
                continue;
            }

            var run = new List<IRenderNode>();
            var end = index;

            for (var next = index; next < children.Count && (isOrphan(children[next]) || IsTableNeutral(children[next])); next++)
            {
                if (isOrphan(children[next]))
                {
                    end = next;
                }
            }

            for (var member = index; member <= end; member++)
            {
                if (children[member] is not TextRenderNode)
                {
                    run.Add(children[member]);
                }
            }

            result.Add(CreateAnonymousTableBox(parent, display, run));
            index = end + 1;
        }

        children.Clear();
        children.AddRange(result);
    }

    private static ElementRenderNode CreateAnonymousTableBox(ElementRenderNode parent, string display, List<IRenderNode> children)
    {
        var document = parent.Ref.Owner!;
        var element = document.CreateElement("div");
        var style = new CssParser(new CssParserOptions(), document.Context).ParseDeclaration(string.Empty)!;

        foreach (var property in parent.ComputedStyle)
        {
            if (property.CanBeInherited && !string.IsNullOrEmpty(property.Value))
            {
                style.SetProperty(property.Name, property.Value);
            }
        }

        style.SetProperty("display", display);
        s_anonymousTableBoxes.AddOrUpdate(element, display);
        return new ElementRenderNode(element, parent, children, style, style);
    }

    private static bool HasDisplay(ElementRenderNode node, string display) =>
        string.Equals(node.ComputedStyle.GetDisplay()?.Trim(), display, StringComparison.OrdinalIgnoreCase);

    private static bool IsTableDisplay(Dictionary<string, string> styleMap) =>
        GetDisplay(styleMap)?.Trim().ToLowerInvariant() is "table" or "inline-table";

    /// <summary>
    /// The cell grid of a table, built once per render. In the collapsing border model this also
    /// resolves the shared grid lines and rewrites the style maps of the table and its cells (see
    /// <see cref="ResolveCollapsedBorders"/>), which is why it must never run twice for a table.
    /// </summary>
    private static TableStructure GetTableStructure(ElementRenderNode table, LayoutContext context)
    {
        if (context.TableStructures.TryGetValue(table.Ref, out var cached))
        {
            return cached;
        }

        var tableStyle = GetOrCreateStyleMap(context, table.Ref, table.ComputedStyle);
        var headerRows = new List<TableRowInfo>();
        var bodyRows = new List<TableRowInfo>();
        var footerRows = new List<TableRowInfo>();
        var columns = new List<TableColumnInfo>();
        var topCaptions = new List<ElementRenderNode>();
        var bottomCaptions = new List<ElementRenderNode>();

        foreach (var child in table.Children.OfType<ElementRenderNode>())
        {
            switch (child.ComputedStyle.GetDisplay()?.Trim().ToLowerInvariant())
            {
                case "table-row":
                    bodyRows.Add(new TableRowInfo(child, null));
                    break;
                case "table-row-group":
                    bodyRows.AddRange(CollectGroupRows(child));
                    break;
                // Header rows come first and footer rows last, wherever their groups appear.
                case "table-header-group":
                    headerRows.AddRange(CollectGroupRows(child));
                    break;
                case "table-footer-group":
                    footerRows.AddRange(CollectGroupRows(child));
                    break;
                case "table-column":
                    AddTableColumns(columns, child, null);
                    break;
                case "table-column-group":
                {
                    var groupColumns = child.Children.OfType<ElementRenderNode>().Where(column => HasDisplay(column, "table-column")).ToList();

                    if (groupColumns.Count == 0)
                    {
                        AddTableColumns(columns, null, child);
                    }

                    foreach (var column in groupColumns)
                    {
                        AddTableColumns(columns, column, child);
                    }

                    break;
                }
                case "table-caption":
                {
                    var captionStyle = GetOrCreateStyleMap(context, child.Ref, child.ComputedStyle);
                    var isBottom = captionStyle.TryGetValue("caption-side", out var side) &&
                                   string.Equals(side.Trim(), "bottom", StringComparison.OrdinalIgnoreCase);
                    (isBottom ? bottomCaptions : topCaptions).Add(child);
                    break;
                }
            }
        }

        var rows = headerRows.Concat(bodyRows).Concat(footerRows).ToList();
        var cells = new List<TableCellInfo>();
        var occupied = new List<List<bool>>();
        var columnCount = columns.Count;

        bool IsOccupied(int row, int column) => row < occupied.Count && column < occupied[row].Count && occupied[row][column];

        void Occupy(int row, int column)
        {
            while (occupied.Count <= row)
            {
                occupied.Add([]);
            }

            while (occupied[row].Count <= column)
            {
                occupied[row].Add(false);
            }

            occupied[row][column] = true;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var columnIndex = 0;

            foreach (var cellNode in rows[rowIndex].Node.Children.OfType<ElementRenderNode>().Where(cell => HasDisplay(cell, "table-cell")))
            {
                while (IsOccupied(rowIndex, columnIndex))
                {
                    columnIndex++;
                }

                var columnSpan = ReadSpanAttribute(cellNode.Ref, "colspan");
                var rowSpan = Math.Min(ReadSpanAttribute(cellNode.Ref, "rowspan"), rows.Count - rowIndex);

                for (var spannedRow = rowIndex; spannedRow < rowIndex + rowSpan; spannedRow++)
                {
                    for (var spannedColumn = columnIndex; spannedColumn < columnIndex + columnSpan; spannedColumn++)
                    {
                        Occupy(spannedRow, spannedColumn);
                    }
                }

                var cell = new TableCellInfo(cellNode, GetOrCreateStyleMap(context, cellNode.Ref, cellNode.ComputedStyle), rowIndex, columnIndex, rowSpan, columnSpan);

                if (cell.Style.TryGetValue("height", out var cellHeight) && TryParsePercentage(cellHeight, out var percentHeight))
                {
                    cell.PercentHeight = percentHeight;
                    cell.Style["height"] = "auto";
                    (cell.Style as StyleMap)?.ClearRawLength("height");
                }

                cells.Add(cell);
                columnIndex += columnSpan;
                columnCount = Math.Max(columnCount, columnIndex);
            }
        }

        var collapse = tableStyle.TryGetValue("border-collapse", out var borderCollapse) &&
                       string.Equals(borderCollapse.Trim(), "collapse", StringComparison.OrdinalIgnoreCase);
        var structure = new TableStructure
        {
            Rows = rows,
            Cells = cells,
            Columns = columns,
            TopCaptions = topCaptions,
            BottomCaptions = bottomCaptions,
            ColumnCount = columnCount,
            CollapseBorders = collapse,
        };

        if (collapse)
        {
            ResolveCollapsedBorders(table, tableStyle, structure);
        }

        context.TableStructures[table.Ref] = structure;
        return structure;

        IEnumerable<TableRowInfo> CollectGroupRows(ElementRenderNode group) =>
            group.Children.OfType<ElementRenderNode>().Where(row => HasDisplay(row, "table-row")).Select(row => new TableRowInfo(row, group));
    }

    private static bool TryParsePercentage(string value, out float percentage)
    {
        var trimmed = value.Trim();
        percentage = 0f;
        return trimmed.EndsWith('%') &&
               float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out percentage);
    }

    private static bool IsCollapsed(ElementRenderNode? node, LayoutContext context) =>
        node is not null &&
        GetOrCreateStyleMap(context, node.Ref, node.ComputedStyle).TryGetValue("visibility", out var visibility) &&
        string.Equals(visibility.Trim(), "collapse", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A cell without content: no element children (generated content included) and no text
    /// beyond collapsible white space.
    /// </summary>
    private static bool IsEmptyCell(TableCellInfo cell) =>
        cell.Node.Children.All(child => child is TextRenderNode text && NormalizeWhitespace(text.Ref.Data).Length == 0);

    private static void AddTableColumns(List<TableColumnInfo> columns, ElementRenderNode? column, ElementRenderNode? group)
    {
        var span = ReadSpanAttribute((column ?? group)!.Ref, "span");

        for (var index = 0; index < span; index++)
        {
            columns.Add(new TableColumnInfo(column, group));
        }
    }

    private static int ReadSpanAttribute(IElement element, string name) =>
        element is not IPseudoElement &&
        int.TryParse(element.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var span) && span > 0
            ? Math.Min(span, 1000)
            : 1;

    /// <summary>
    /// The collapsing border model: every grid line takes the widest border meeting there
    /// (<c>hidden</c> suppresses it), the cell's own or its neighbor's, and the table's along the
    /// outer edges. For layout each cell keeps half of each of its lines and the table half of
    /// its outer lines, written straight into their style maps; the lines themselves are painted
    /// by <see cref="PaintCollapsedBorders"/>, so those borders are made transparent.
    /// </summary>
    private static void ResolveCollapsedBorders(ElementRenderNode table, Dictionary<string, string> tableStyle, TableStructure structure)
    {
        var rowCount = structure.Rows.Count;
        var columnCount = structure.ColumnCount;
        var grid = new TableCellInfo?[rowCount, columnCount];
        var boxes = new Dictionary<TableCellInfo, BoxStyle>();

        foreach (var cell in structure.Cells)
        {
            boxes[cell] = ResolveBoxStyle(cell.Style, cell.Node.Ref);

            for (var row = cell.Row; row < cell.Row + cell.RowSpan; row++)
            {
                for (var column = cell.Column; column < cell.Column + cell.ColumnSpan; column++)
                {
                    grid[row, column] = cell;
                }
            }
        }

        var tableBox = ResolveBoxStyle(tableStyle, table.Ref);
        var outer = new float[4];

        foreach (var cell in structure.Cells)
        {
            var box = boxes[cell];
            var top = new List<(float, BorderStyleKind, RenderColor)> { (box.BorderWidth.Top, box.BorderStyle.Top, box.BorderColor.Top) };
            var right = new List<(float, BorderStyleKind, RenderColor)> { (box.BorderWidth.Right, box.BorderStyle.Right, box.BorderColor.Right) };
            var bottom = new List<(float, BorderStyleKind, RenderColor)> { (box.BorderWidth.Bottom, box.BorderStyle.Bottom, box.BorderColor.Bottom) };
            var left = new List<(float, BorderStyleKind, RenderColor)> { (box.BorderWidth.Left, box.BorderStyle.Left, box.BorderColor.Left) };
            var lastRow = cell.Row + cell.RowSpan;
            var lastColumn = cell.Column + cell.ColumnSpan;

            for (var column = cell.Column; column < lastColumn; column++)
            {
                if (cell.Row == 0)
                {
                    top.Add((tableBox.BorderWidth.Top, tableBox.BorderStyle.Top, tableBox.BorderColor.Top));
                }
                else if (grid[cell.Row - 1, column] is { } above)
                {
                    top.Add((boxes[above].BorderWidth.Bottom, boxes[above].BorderStyle.Bottom, boxes[above].BorderColor.Bottom));
                }

                if (lastRow == rowCount)
                {
                    bottom.Add((tableBox.BorderWidth.Bottom, tableBox.BorderStyle.Bottom, tableBox.BorderColor.Bottom));
                }
                else if (grid[lastRow, column] is { } below)
                {
                    bottom.Add((boxes[below].BorderWidth.Top, boxes[below].BorderStyle.Top, boxes[below].BorderColor.Top));
                }
                else
                {
                    cell.PaintsBottomLine = true;
                }
            }

            for (var row = cell.Row; row < lastRow; row++)
            {
                if (cell.Column == 0)
                {
                    left.Add((tableBox.BorderWidth.Left, tableBox.BorderStyle.Left, tableBox.BorderColor.Left));
                }
                else if (grid[row, cell.Column - 1] is { } before)
                {
                    left.Add((boxes[before].BorderWidth.Right, boxes[before].BorderStyle.Right, boxes[before].BorderColor.Right));
                }

                if (lastColumn == columnCount)
                {
                    right.Add((tableBox.BorderWidth.Right, tableBox.BorderStyle.Right, tableBox.BorderColor.Right));
                }
                else if (grid[row, lastColumn] is { } after)
                {
                    right.Add((boxes[after].BorderWidth.Left, boxes[after].BorderStyle.Left, boxes[after].BorderColor.Left));
                }
                else
                {
                    cell.PaintsRightLine = true;
                }
            }

            var (topWidth, topColor) = ResolveCollapsedLine(top);
            var (rightWidth, rightColor) = ResolveCollapsedLine(right);
            var (bottomWidth, bottomColor) = ResolveCollapsedLine(bottom);
            var (leftWidth, leftColor) = ResolveCollapsedLine(left);
            cell.LineWidths = new EdgeSizes(topWidth, rightWidth, bottomWidth, leftWidth);
            cell.LineColors = new EdgeBorderColor(topColor, rightColor, bottomColor, leftColor);
            cell.PaintsBottomLine |= lastRow == rowCount;
            cell.PaintsRightLine |= lastColumn == columnCount;

            OverrideCollapsedBorder(cell.Style, "top", topWidth / 2f);
            OverrideCollapsedBorder(cell.Style, "right", rightWidth / 2f);
            OverrideCollapsedBorder(cell.Style, "bottom", bottomWidth / 2f);
            OverrideCollapsedBorder(cell.Style, "left", leftWidth / 2f);

            outer[0] = cell.Row == 0 ? Math.Max(outer[0], topWidth) : outer[0];
            outer[1] = lastColumn == columnCount ? Math.Max(outer[1], rightWidth) : outer[1];
            outer[2] = lastRow == rowCount ? Math.Max(outer[2], bottomWidth) : outer[2];
            outer[3] = cell.Column == 0 ? Math.Max(outer[3], leftWidth) : outer[3];
        }

        if (structure.Cells.Count == 0)
        {
            return;
        }

        // A collapsed table has no padding, and its border is part of the outer grid lines.
        OverrideCollapsedBorder(tableStyle, "top", outer[0] / 2f);
        OverrideCollapsedBorder(tableStyle, "right", outer[1] / 2f);
        OverrideCollapsedBorder(tableStyle, "bottom", outer[2] / 2f);
        OverrideCollapsedBorder(tableStyle, "left", outer[3] / 2f);

        foreach (var side in new[] { "top", "right", "bottom", "left" })
        {
            OverrideStyleLength(tableStyle, $"padding-{side}", 0f);
        }
    }

    private static (float Width, RenderColor Color) ResolveCollapsedLine(List<(float Width, BorderStyleKind Style, RenderColor Color)> candidates)
    {
        var width = 0f;
        var color = RenderColor.Transparent;

        foreach (var candidate in candidates)
        {
            if (candidate.Style == BorderStyleKind.Hidden)
            {
                return (0f, RenderColor.Transparent);
            }

            if (candidate.Width > width)
            {
                width = candidate.Width;
                color = candidate.Color;
            }
        }

        return (width, color);
    }

    /// <summary>
    /// Marks a style map whose border widths are halves of collapsed grid lines, which were
    /// snapped to device pixels as whole lines already (see SnapBorderWidth).
    /// </summary>
    private const string CollapsedBordersMarker = "-anglesharp-collapsed-borders";

    private static void OverrideCollapsedBorder(Dictionary<string, string> styleMap, string side, float width)
    {
        styleMap[CollapsedBordersMarker] = "1";
        OverrideStyleLength(styleMap, $"border-{side}-width", width);
        styleMap[$"border-{side}-style"] = width > 0f ? "solid" : "none";
        styleMap[$"border-{side}-color"] = "transparent";
    }

    private static void OverrideStyleLength(Dictionary<string, string> styleMap, string propertyName, float pixels)
    {
        styleMap[propertyName] = pixels.ToString("0.###", CultureInfo.InvariantCulture) + "px";
        (styleMap as StyleMap)?.ClearRawLength(propertyName);
    }

    /// <summary>
    /// <c>border-spacing</c> (one value for both axes, or horizontal then vertical). It only
    /// applies in the separated border model.
    /// </summary>
    private static (float Horizontal, float Vertical) ParseBorderSpacing(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("border-spacing", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return (0f, 0f);
        }

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var horizontal = Math.Max(0f, ParseLengthValue(tokens[0], 0f, allowAuto: false));
        var vertical = tokens.Length > 1 ? Math.Max(0f, ParseLengthValue(tokens[1], 0f, allowAuto: false)) : horizontal;
        return (horizontal, vertical);
    }

    /// <summary>
    /// Reads the box-level meaning of <c>vertical-align</c>, which is what the property means on a
    /// table cell. On inline content the same property shifts the text instead, which is what
    /// <see cref="ParseVerticalAlign"/> handles. Unset is the initial value, <c>baseline</c>.
    /// </summary>
    private static CellVerticalAlign ParseCellVerticalAlign(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("vertical-align", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return CellVerticalAlign.Baseline;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "top" or "text-top" => CellVerticalAlign.Top,
            "bottom" or "text-bottom" => CellVerticalAlign.Bottom,
            "middle" => CellVerticalAlign.Middle,
            _ => CellVerticalAlign.Baseline,
        };
    }

    private static RenderTextStyle ResolveRowTextStyle(TableRowInfo row, RenderTextStyle tableTextStyle, LayoutContext context)
    {
        var style = tableTextStyle;

        if (row.Group is { } group)
        {
            style = ResolveTextStyle(GetOrCreateStyleMap(context, group.Ref, group.ComputedStyle), style);
        }

        style = ResolveTextStyle(GetOrCreateStyleMap(context, row.Node.Ref, row.Node.ComputedStyle), style);
        return style with { VerticalAlignOffset = 0f };
    }

    /// <summary>
    /// The min-content and max-content border-box widths of a cell. An authored width raises the
    /// minimum and replaces the maximum, but never below the content's minimum.
    /// </summary>
    private static IntrinsicWidths ComputeCellIntrinsicWidths(TableCellInfo cell, RenderTextStyle rowTextStyle, float percentageBase, LayoutContext context)
    {
        var style = cell.Style;
        var textStyle = ResolveTextStyle(style, rowTextStyle) with { VerticalAlignOffset = 0f };
        var box = ResolveBoxStyle(style, cell.Node.Ref);
        var extras = box.BorderWidth.Left + box.BorderWidth.Right +
                     ParseLength(style, "padding-left", percentageBase, box.Padding.Left, allowAuto: false) +
                     ParseLength(style, "padding-right", percentageBase, box.Padding.Right, allowAuto: false);
        var content = ComputeContentIntrinsicWidths(cell.Node, textStyle, percentageBase, context);
        var min = content.Min + extras;
        var max = Math.Max(min, content.Max + extras);
        var authored = ResolveAuthoredDimension(style, "width", percentageBase, float.NaN, extras);

        if (!float.IsNaN(authored))
        {
            min = Math.Max(min, authored + extras);
            max = Math.Max(min, authored + extras);
        }

        return new IntrinsicWidths(min, max);
    }

    private static float ResolveTableColumnWidth(TableColumnInfo column, float percentageBase, LayoutContext context)
    {
        foreach (var node in new[] { column.Node, column.Group })
        {
            if (node is null)
            {
                continue;
            }

            var width = ParseLength(GetOrCreateStyleMap(context, node.Ref, node.ComputedStyle), "width", percentageBase, float.NaN, allowAuto: true);

            if (!float.IsNaN(width))
            {
                return width;
            }
        }

        return float.NaN;
    }

    /// <summary>
    /// The min-content and max-content width of every column. Cells spanning several columns are
    /// handled after the narrower ones and spread whatever they still need over their columns,
    /// in proportion to the columns' max-content widths.
    /// </summary>
    private static (float[] Min, float[] Max) ComputeTableColumnWidths(TableStructure structure, RenderTextStyle tableTextStyle, float percentageBase, float horizontalSpacing, LayoutContext context)
    {
        var count = structure.ColumnCount;
        var min = new float[count];
        var max = new float[count];
        var rowTextStyles = structure.Rows.Select(row => ResolveRowTextStyle(row, tableTextStyle, context)).ToArray();

        foreach (var cell in structure.Cells.OrderBy(cell => cell.ColumnSpan))
        {
            var widths = ComputeCellIntrinsicWidths(cell, rowTextStyles[cell.Row], percentageBase, context);

            if (cell.ColumnSpan == 1)
            {
                min[cell.Column] = Math.Max(min[cell.Column], widths.Min);
                max[cell.Column] = Math.Max(max[cell.Column], widths.Max);
                continue;
            }

            var spacing = (cell.ColumnSpan - 1) * horizontalSpacing;
            GrowSpannedColumns(min, max, cell.Column, cell.ColumnSpan, widths.Min - spacing);
            GrowSpannedColumns(max, max, cell.Column, cell.ColumnSpan, widths.Max - spacing);
        }

        for (var index = 0; index < Math.Min(count, structure.Columns.Count); index++)
        {
            var columnWidth = ResolveTableColumnWidth(structure.Columns[index], percentageBase, context);

            if (!float.IsNaN(columnWidth))
            {
                min[index] = Math.Max(min[index], columnWidth);
                max[index] = Math.Max(min[index], columnWidth);
            }
        }

        for (var index = 0; index < count; index++)
        {
            max[index] = Math.Max(max[index], min[index]);
        }

        return (min, max);
    }

    private static void GrowSpannedColumns(float[] target, float[] weights, int start, int span, float required)
    {
        var current = 0f;
        var weightSum = 0f;

        for (var index = start; index < start + span; index++)
        {
            current += target[index];
            weightSum += weights[index];
        }

        if (required <= current)
        {
            return;
        }

        var excess = required - current;
        var shares = new float[span];

        for (var offset = 0; offset < span; offset++)
        {
            shares[offset] = weightSum > 0f ? excess * weights[start + offset] / weightSum : excess / span;
        }

        for (var offset = 0; offset < span; offset++)
        {
            target[start + offset] += shares[offset];
        }
    }

    /// <summary>
    /// Shares out the space for the columns: between min-content and max-content every column
    /// gets the same fraction of its range; beyond max-content the extra goes in proportion to
    /// the max-content widths.
    /// </summary>
    private static float[] DistributeColumnWidths(float[] min, float[] max, float space)
    {
        var count = min.Length;
        var widths = new float[count];
        var minSum = min.Sum();
        var maxSum = max.Sum();

        for (var index = 0; index < count; index++)
        {
            if (space >= maxSum)
            {
                var extra = space - maxSum;
                widths[index] = max[index] + (maxSum > 0f ? extra * max[index] / maxSum : extra / count);
            }
            else if (space > minSum && maxSum > minSum)
            {
                widths[index] = min[index] + ((max[index] - min[index]) * (space - minSum) / (maxSum - minSum));
            }
            else
            {
                widths[index] = min[index];
            }
        }

        return widths;
    }

    /// <summary>
    /// <c>table-layout: fixed</c>: column widths come from the columns and the first row's cells
    /// only; the remaining columns share what is left equally. Content never widens a column.
    /// </summary>
    private static float[] ResolveFixedColumnWidths(TableStructure structure, float space, float percentageBase, float horizontalSpacing, LayoutContext context)
    {
        var count = structure.ColumnCount;
        var widths = Enumerable.Repeat(float.NaN, count).ToArray();

        for (var index = 0; index < Math.Min(count, structure.Columns.Count); index++)
        {
            widths[index] = ResolveTableColumnWidth(structure.Columns[index], percentageBase, context);
        }

        foreach (var cell in structure.Cells.Where(cell => cell.Row == 0))
        {
            var box = ResolveBoxStyle(cell.Style, cell.Node.Ref);
            var extras = box.BorderWidth.Left + box.BorderWidth.Right +
                         ParseLength(cell.Style, "padding-left", percentageBase, box.Padding.Left, allowAuto: false) +
                         ParseLength(cell.Style, "padding-right", percentageBase, box.Padding.Right, allowAuto: false);
            var authored = ResolveAuthoredDimension(cell.Style, "width", percentageBase, float.NaN, extras);

            if (float.IsNaN(authored))
            {
                continue;
            }

            var perColumn = Math.Max(0f, (authored + extras - ((cell.ColumnSpan - 1) * horizontalSpacing)) / cell.ColumnSpan);

            for (var column = cell.Column; column < cell.Column + cell.ColumnSpan; column++)
            {
                if (float.IsNaN(widths[column]))
                {
                    widths[column] = perColumn;
                }
            }
        }

        var specified = widths.Where(width => !float.IsNaN(width)).Sum();
        var unspecifiedCount = widths.Count(float.IsNaN);
        var remaining = space - specified;

        for (var index = 0; index < count; index++)
        {
            if (float.IsNaN(widths[index]))
            {
                widths[index] = Math.Max(0f, remaining / unspecifiedCount);
            }
            else if (unspecifiedCount == 0 && remaining > 0f)
            {
                widths[index] += specified > 0f ? remaining * widths[index] / specified : remaining / count;
            }
        }

        return widths;
    }

    /// <summary>
    /// The intrinsic widths of a table's content box: its columns plus the spacing between them,
    /// and at least as wide as its widest caption needs.
    /// </summary>
    private static IntrinsicWidths ComputeTableIntrinsicWidths(ElementRenderNode table, Dictionary<string, string> styleMap, RenderTextStyle textStyle, float percentageBase, LayoutContext context)
    {
        var structure = GetTableStructure(table, context);
        var horizontalSpacing = structure.CollapseBorders ? 0f : ParseBorderSpacing(styleMap).Horizontal;
        var (min, max) = ComputeTableColumnWidths(structure, textStyle, percentageBase, horizontalSpacing, context);
        var spacing = structure.ColumnCount > 0 ? (structure.ColumnCount + 1) * horizontalSpacing : 0f;
        var tableMin = min.Sum() + spacing;

        foreach (var caption in structure.TopCaptions.Concat(structure.BottomCaptions))
        {
            tableMin = Math.Max(tableMin, ComputeOuterIntrinsicWidths(caption, textStyle, percentageBase, context).Min);
        }

        return new IntrinsicWidths(tableMin, Math.Max(tableMin, max.Sum() + spacing));
    }

    private static void LayoutTable(
        ElementRenderNode tableNode,
        float containingX,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight,
        float forcedBorderBoxWidth,
        float containingHeight = float.NaN)
    {
        var structure = GetTableStructure(tableNode, context);
        var styleMap = GetOrCreateStyleMap(context, tableNode.Ref, tableNode.ComputedStyle);
        var textStyle = ResolveTextStyle(styleMap, inheritedTextStyle) with { VerticalAlignOffset = 0f };

        var clearance = floats.GetClearance(GetClear(styleMap));

        if (!float.IsNaN(clearance) && clearance > cursorY)
        {
            cursorY = clearance;
            previousBlockMarginBottom = 0f;
        }

        // A table establishes a block formatting context, so it sits beside floats as a whole.
        var (flowLeft, flowRight) = floats.GetBand(cursorY, textStyle.FontSize * textStyle.LineHeightMultiplier, containingX, containingX + containingWidth);
        var flowWidth = Math.Max(0f, flowRight - flowLeft);
        var box = ResolveBoxStyle(styleMap, tableNode.Ref);
        var marginTop = ParseLength(styleMap, "margin-top", flowWidth, box.Margin.Top, allowAuto: false);
        var marginBottom = ParseLength(styleMap, "margin-bottom", flowWidth, box.Margin.Bottom, allowAuto: false);
        var marginLeft = ParseLength(styleMap, "margin-left", flowWidth, box.Margin.Left, allowAuto: true);
        var marginRight = ParseLength(styleMap, "margin-right", flowWidth, box.Margin.Right, allowAuto: true);

        if (!float.IsNaN(forcedBorderBoxWidth))
        {
            // An inline-table is placed by its line; auto margins are zero there.
            marginLeft = float.IsNaN(marginLeft) ? 0f : marginLeft;
            marginRight = float.IsNaN(marginRight) ? 0f : marginRight;
        }

        if (suppressNextBlockTopMargin)
        {
            marginTop = 0f;
            suppressNextBlockTopMargin = false;
        }

        var border = box.BorderWidth;
        var padding = new EdgeSizes(
            ParseLength(styleMap, "padding-top", flowWidth, box.Padding.Top, allowAuto: false),
            ParseLength(styleMap, "padding-right", flowWidth, box.Padding.Right, allowAuto: false),
            ParseLength(styleMap, "padding-bottom", flowWidth, box.Padding.Bottom, allowAuto: false),
            ParseLength(styleMap, "padding-left", flowWidth, box.Padding.Left, allowAuto: false));
        var (horizontalSpacing, verticalSpacing) = structure.CollapseBorders ? (0f, 0f) : ParseBorderSpacing(styleMap);
        var columnCount = structure.ColumnCount;
        var rowCount = structure.Rows.Count;
        var horizontalSpacingTotal = columnCount > 0 ? (columnCount + 1) * horizontalSpacing : 0f;

        // `visibility: collapse` removes rows and columns (with their spacing) once the column
        // widths are known, as if they had been visible.
        var collapsedRows = structure.Rows.Select(row => IsCollapsed(row.Node, context) || IsCollapsed(row.Group, context)).ToArray();
        var collapsedColumns = Enumerable.Range(0, columnCount)
            .Select(column => column < structure.Columns.Count && (IsCollapsed(structure.Columns[column].Node, context) || IsCollapsed(structure.Columns[column].Group, context)))
            .ToArray();
        var visibleRowCount = collapsedRows.Count(collapsed => !collapsed);
        var visibleColumnCount = collapsedColumns.Count(collapsed => !collapsed);
        var verticalSpacingTotal = visibleRowCount > 0 ? (visibleRowCount + 1) * verticalSpacing : 0f;
        var horizontalExtras = border.Left + border.Right + padding.Left + padding.Right;
        var verticalExtras = border.Top + border.Bottom + padding.Top + padding.Bottom;
        var knownMargins = (float.IsNaN(marginLeft) ? 0f : marginLeft) + (float.IsNaN(marginRight) ? 0f : marginRight);
        var available = Math.Max(0f, flowWidth - knownMargins - horizontalExtras);

        var (columnMin, columnMax) = ComputeTableColumnWidths(structure, textStyle, available, horizontalSpacing, context);
        var minContentWidth = columnMin.Sum() + horizontalSpacingTotal;
        var maxContentWidth = columnMax.Sum() + horizontalSpacingTotal;

        foreach (var caption in structure.TopCaptions.Concat(structure.BottomCaptions))
        {
            minContentWidth = Math.Max(minContentWidth, ComputeOuterIntrinsicWidths(caption, textStyle, available, context).Min);
        }

        var authoredWidth = !float.IsNaN(forcedBorderBoxWidth)
            ? forcedBorderBoxWidth - horizontalExtras
            : ResolveAuthoredDimension(styleMap, "width", flowWidth, float.NaN, horizontalExtras);

        // An auto-width table is shrink-to-fit.
        var contentWidth = float.IsNaN(authoredWidth)
            ? Math.Min(Math.Max(minContentWidth, available), maxContentWidth)
            : authoredWidth;

        if (float.IsNaN(forcedBorderBoxWidth))
        {
            contentWidth = ClampToMinMaxWidth(styleMap, contentWidth, flowWidth, horizontalExtras);
        }

        float[] columnWidths;

        if (!float.IsNaN(authoredWidth) && styleMap.TryGetValue("table-layout", out var tableLayout) &&
            string.Equals(tableLayout.Trim(), "fixed", StringComparison.OrdinalIgnoreCase))
        {
            columnWidths = ResolveFixedColumnWidths(structure, Math.Max(0f, contentWidth - horizontalSpacingTotal), available, horizontalSpacing, context);
        }
        else
        {
            // A table is never narrower than its content needs.
            contentWidth = Math.Max(contentWidth, minContentWidth);
            columnWidths = DistributeColumnWidths(columnMin, columnMax, Math.Max(0f, contentWidth - horizontalSpacingTotal));
        }

        contentWidth = Math.Max(contentWidth, columnWidths.Sum() + horizontalSpacingTotal);

        if (visibleColumnCount < columnCount)
        {
            var removedWidth = (columnCount - visibleColumnCount) * horizontalSpacing + (visibleColumnCount == 0 ? horizontalSpacing : 0f);

            for (var column = 0; column < columnCount; column++)
            {
                if (collapsedColumns[column])
                {
                    removedWidth += columnWidths[column];
                    columnWidths[column] = 0f;
                }
            }

            contentWidth = Math.Max(0f, contentWidth - removedWidth);
        }

        var borderBoxWidth = contentWidth + horizontalExtras;
        var borderBoxX = float.IsNaN(marginLeft) && float.IsNaN(marginRight)
            ? flowLeft + Math.Max(0f, (flowWidth - borderBoxWidth) / 2f)
            : float.IsNaN(marginLeft)
                ? flowLeft + flowWidth - marginRight - borderBoxWidth
                : flowLeft + marginLeft;

        var captionCursorY = cursorY + CollapseMargins(previousBlockMarginBottom, marginTop);
        LayoutTableCaptions(structure.TopCaptions, borderBoxX, borderBoxWidth, ref captionCursorY, textStyle, context, displayList, maxY, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight);
        var borderBoxY = captionCursorY;
        var contentLeft = borderBoxX + border.Left + padding.Left;
        var contentTop = borderBoxY + border.Top + padding.Top;

        var columnX = new float[columnCount];
        var nextColumnX = contentLeft + horizontalSpacing;

        for (var column = 0; column < columnCount; column++)
        {
            columnX[column] = nextColumnX;

            if (!collapsedColumns[column])
            {
                nextColumnX += columnWidths[column] + horizontalSpacing;
            }
        }

        float SpanWidth(TableCellInfo cell)
        {
            var width = 0f;
            var visible = 0;

            for (var column = cell.Column; column < cell.Column + cell.ColumnSpan; column++)
            {
                if (!collapsedColumns[column])
                {
                    width += columnWidths[column];
                    visible++;
                }
            }

            return width + (Math.Max(0, visible - 1) * horizontalSpacing);
        }

        // Rows: as tall as their tallest single-row cell (or their authored height), with the
        // baseline-aligned cells sharing one baseline; cells spanning rows then grow the rows
        // they cover by whatever they still lack, evenly.
        var rowTextStyles = structure.Rows.Select(row => ResolveRowTextStyle(row, textStyle, context)).ToArray();
        var rowHeights = new float[rowCount];
        var rowBaselines = new float[rowCount];
        var rowDescents = new float[rowCount];
        var cellCount = structure.Cells.Count;
        var cellHeights = new float[cellCount];
        var cellBaselines = new float[cellCount];

        // A percentage table height refers to the containing block's height (and is `auto` while
        // that is not definite); percentage row and cell heights refer to the table's height, and
        // count for nothing while that is not definite.
        var definiteTableHeight = ResolveAuthoredDimension(styleMap, "height", containingHeight, float.NaN, verticalExtras);
        var percentageHeightBase = float.IsNaN(definiteTableHeight) ? 0f : definiteTableHeight;

        for (var row = 0; row < rowCount; row++)
        {
            var rowNode = structure.Rows[row].Node;
            var authoredRowHeight = ParseLength(GetOrCreateStyleMap(context, rowNode.Ref, rowNode.ComputedStyle), "height", percentageHeightBase, float.NaN, allowAuto: true);
            rowHeights[row] = float.IsNaN(authoredRowHeight) ? 0f : authoredRowHeight;
        }

        for (var index = 0; index < cellCount; index++)
        {
            var cell = structure.Cells[index];
            var width = SpanWidth(cell);
            var trial = TrialLayout(cell.Node, width, rowTextStyles[cell.Row], context, width, isAtomicInline: false);

            if (trial.Metrics is not { } metrics)
            {
                continue;
            }

            cellHeights[index] = metrics.BorderBoxHeight;

            if (!float.IsNaN(cell.PercentHeight) && percentageHeightBase > 0f)
            {
                var verticalCellExtras = metrics.BorderTop + metrics.BorderBottom + metrics.PaddingTop + metrics.PaddingBottom;
                cellHeights[index] = Math.Max(cellHeights[index], (cell.PercentHeight / 100f * percentageHeightBase) + verticalCellExtras);
            }

            // A cell without lines has its baseline at the bottom of its content box.
            var baselineY = trial.FirstBaselineY ?? (metrics.BorderBoxY + metrics.BorderBoxHeight - metrics.PaddingBottom - metrics.BorderBottom);
            cellBaselines[index] = baselineY - metrics.BorderBoxY;

            if (cell.RowSpan != 1)
            {
                continue;
            }

            if (ParseCellVerticalAlign(cell.Style) == CellVerticalAlign.Baseline)
            {
                rowBaselines[cell.Row] = Math.Max(rowBaselines[cell.Row], cellBaselines[index]);
                rowDescents[cell.Row] = Math.Max(rowDescents[cell.Row], cellHeights[index] - cellBaselines[index]);
            }
            else
            {
                rowHeights[cell.Row] = Math.Max(rowHeights[cell.Row], cellHeights[index]);
            }
        }

        for (var row = 0; row < rowCount; row++)
        {
            rowHeights[row] = collapsedRows[row] ? 0f : Math.Max(rowHeights[row], rowBaselines[row] + rowDescents[row]);
        }

        for (var index = 0; index < cellCount; index++)
        {
            var cell = structure.Cells[index];

            if (cell.RowSpan <= 1)
            {
                continue;
            }

            var visibleRows = 0;
            var spanned = 0f;

            for (var row = cell.Row; row < cell.Row + cell.RowSpan; row++)
            {
                if (!collapsedRows[row])
                {
                    spanned += rowHeights[row];
                    visibleRows++;
                }
            }

            spanned += Math.Max(0, visibleRows - 1) * verticalSpacing;

            if (visibleRows > 0 && cellHeights[index] > spanned)
            {
                var deficitPerRow = (cellHeights[index] - spanned) / visibleRows;

                for (var row = cell.Row; row < cell.Row + cell.RowSpan; row++)
                {
                    if (!collapsedRows[row])
                    {
                        rowHeights[row] += deficitPerRow;
                    }
                }
            }
        }

        // A taller authored table height goes to the rows, in proportion to their heights.
        var contentHeight = rowHeights.Sum() + verticalSpacingTotal;

        if (!float.IsNaN(definiteTableHeight) && definiteTableHeight > contentHeight)
        {
            var extra = definiteTableHeight - contentHeight;
            var rowSum = rowHeights.Sum();

            for (var row = 0; row < rowCount; row++)
            {
                if (!collapsedRows[row])
                {
                    rowHeights[row] += rowSum > 0f ? extra * rowHeights[row] / rowSum : extra / visibleRowCount;
                }
            }

            contentHeight = definiteTableHeight;
        }

        var rowY = new float[rowCount];
        var nextRowY = contentTop + verticalSpacing;

        for (var row = 0; row < rowCount; row++)
        {
            rowY[row] = nextRowY;

            if (!collapsedRows[row])
            {
                nextRowY += rowHeights[row] + verticalSpacing;
            }
        }

        float SpanHeight(TableCellInfo cell)
        {
            var height = 0f;
            var visible = 0;

            for (var row = cell.Row; row < cell.Row + cell.RowSpan; row++)
            {
                if (!collapsedRows[row])
                {
                    height += rowHeights[row];
                    visible++;
                }
            }

            return height + (Math.Max(0, visible - 1) * verticalSpacing);
        }

        var borderBoxHeight = contentHeight + verticalExtras;
        box = box with { BorderRadius = ResolveBorderRadii(styleMap, borderBoxWidth, borderBoxHeight) };
        var decorations = textStyle.IsHidden ? new DisplayList() : displayList;
        var effects = OpenElementEffectScopes(displayList, decorations, styleMap, tableNode.Ref, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, border, padding, box.BorderRadius, textStyle.FontSize);
        PaintBoxBackground(decorations, styleMap, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, border, padding);
        PaintBoxShadows(decorations, box.BoxShadows, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);
        PaintBoxBorder(decorations, styleMap, tableNode.Ref, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        PaintOutline(decorations, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        RecordLayoutMetrics(tableNode.Ref, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, border.Left, border.Right, border.Top, border.Bottom, padding.Left, padding.Right, padding.Top, padding.Bottom);

        var shownCells = new HashSet<TableCellInfo>();

        for (var index = 0; index < cellCount; index++)
        {
            var cell = structure.Cells[index];

            // A cell that lives only in collapsed rows or columns is gone, and `empty-cells: hide`
            // paints neither the box nor the backgrounds behind an empty cell (separated borders only).
            var collapsedAway = Enumerable.Range(cell.Row, cell.RowSpan).All(row => collapsedRows[row]) ||
                                Enumerable.Range(cell.Column, cell.ColumnSpan).All(column => collapsedColumns[column]) ||
                                IsCollapsed(cell.Node, context);
            var hiddenEmpty = !structure.CollapseBorders &&
                              cell.Style.TryGetValue("empty-cells", out var emptyCells) &&
                              string.Equals(emptyCells.Trim(), "hide", StringComparison.OrdinalIgnoreCase) &&
                              IsEmptyCell(cell);

            if (collapsedAway || hiddenEmpty)
            {
                continue;
            }

            // A hidden cell keeps its place, but its collapsed grid lines are not painted.
            if (!ResolveTextStyle(cell.Style, rowTextStyles[cell.Row]).IsHidden)
            {
                shownCells.Add(cell);
            }

            var cellX = columnX[cell.Column];
            var cellY = rowY[cell.Row];
            var width = SpanWidth(cell);
            var height = SpanHeight(cell);
            PaintTablePartBackgrounds(displayList, structure, cell, new RenderRect(cellX, cellY, width, height), context);

            var contentOffsetY = ParseCellVerticalAlign(cell.Style) switch
            {
                CellVerticalAlign.Top => 0f,
                CellVerticalAlign.Middle => (height - cellHeights[index]) / 2f,
                CellVerticalAlign.Bottom => height - cellHeights[index],
                _ => cell.RowSpan == 1 ? rowBaselines[cell.Row] - cellBaselines[index] : 0f,
            };

            var cellCursorY = cellY;
            var cellPreviousMarginBottom = 0f;
            var cellSuppressTopMargin = false;
            var cellTextIndentConsumed = false;

            LayoutNode(
                cell.Node,
                cellX,
                cellY,
                width,
                ref cellCursorY,
                ref cellPreviousMarginBottom,
                ref cellSuppressTopMargin,
                new FloatArea(),
                ref cellTextIndentConsumed,
                rowTextStyles[cell.Row],
                context,
                displayList,
                maxY,
                positionedContainingX: positionedContainingX,
                positionedContainingY: positionedContainingY,
                positionedContainingWidth: positionedContainingWidth,
                positionedContainingHeight: positionedContainingHeight,
                forcedBorderBoxWidth: width,
                forcedBorderBoxHeight: height,
                contentOffsetY: Math.Max(0f, contentOffsetY));
        }

        if (structure.CollapseBorders)
        {
            PaintCollapsedBorders(displayList, shownCells, columnX, rowY, SpanWidth, SpanHeight);
        }

        ResolveStickyBoxes(displayList, context.StickyScopes.Peek(), new RenderRect(contentLeft, contentTop, contentWidth, contentHeight), context);
        CloseElementEffectScopes(displayList, effects);

        var tableBottom = borderBoxY + borderBoxHeight;
        LayoutTableCaptions(structure.BottomCaptions, borderBoxX, borderBoxWidth, ref tableBottom, textStyle, context, displayList, maxY, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight);
        cursorY = tableBottom;
        previousBlockMarginBottom = marginBottom + context.ParagraphSpacing;
    }

    private static void LayoutTableCaptions(
        List<ElementRenderNode> captions,
        float x,
        float width,
        ref float cursorY,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight)
    {
        foreach (var caption in captions)
        {
            var previousMarginBottom = 0f;
            var suppressTopMargin = false;
            var textIndentConsumed = false;

            LayoutNode(
                caption,
                x,
                cursorY,
                width,
                ref cursorY,
                ref previousMarginBottom,
                ref suppressTopMargin,
                new FloatArea(),
                ref textIndentConsumed,
                textStyle,
                context,
                displayList,
                maxY,
                positionedContainingX: positionedContainingX,
                positionedContainingY: positionedContainingY,
                positionedContainingWidth: positionedContainingWidth,
                positionedContainingHeight: positionedContainingHeight);

            cursorY += previousMarginBottom;
        }
    }

    /// <summary>
    /// The backgrounds of the column group, column, row group and row a cell belongs to, painted
    /// across the cell (and so never into the spacing between cells), in that order.
    /// </summary>
    private static void PaintTablePartBackgrounds(DisplayList displayList, TableStructure structure, TableCellInfo cell, RenderRect rect, LayoutContext context)
    {
        var column = cell.Column < structure.Columns.Count ? structure.Columns[cell.Column] : null;
        var row = structure.Rows[cell.Row];

        foreach (var part in new[] { column?.Group, column?.Node, row.Group, row.Node })
        {
            if (part is null)
            {
                continue;
            }

            var partStyle = GetOrCreateStyleMap(context, part.Ref, part.ComputedStyle);

            if (ResolveIsHidden(partStyle, inherited: false))
            {
                continue;
            }

            var partBox = ResolveBoxStyle(partStyle, part.Ref);

            if (partBox.BackgroundColor.A > 0)
            {
                displayList.FillRect(rect, partBox.BackgroundColor);
            }

            if (partBox.BackgroundLayers is { Count: > 0 } layers)
            {
                for (var index = layers.Count - 1; index >= 0; index--)
                {
                    PaintBackground(displayList, layers[index].Paint, rect.X, rect.Y, rect.Width, rect.Height);
                }
            }
        }
    }

    /// <summary>
    /// The grid lines of the collapsing border model, centred on the cell edges and painted over
    /// the cells. Each line is painted once: a cell paints its top and left lines, plus its right
    /// and bottom ones where no cell follows.
    /// </summary>
    private static void PaintCollapsedBorders(DisplayList displayList, IEnumerable<TableCellInfo> cells, float[] columnX, float[] rowY, Func<TableCellInfo, float> spanWidth, Func<TableCellInfo, float> spanHeight)
    {
        foreach (var cell in cells)
        {
            var x = columnX[cell.Column];
            var y = rowY[cell.Row];
            var width = spanWidth(cell);
            var height = spanHeight(cell);
            var lines = cell.LineWidths;
            var colors = cell.LineColors;
            var left = x - (lines.Left / 2f);
            var top = y - (lines.Top / 2f);
            var fullWidth = width + ((lines.Left + lines.Right) / 2f);
            var fullHeight = height + ((lines.Top + lines.Bottom) / 2f);

            PaintLine(new RenderRect(left, top, fullWidth, lines.Top), colors.Top);
            PaintLine(new RenderRect(left, top, lines.Left, fullHeight), colors.Left);

            if (cell.PaintsRightLine)
            {
                PaintLine(new RenderRect(x + width - (lines.Right / 2f), top, lines.Right, fullHeight), colors.Right);
            }

            if (cell.PaintsBottomLine)
            {
                PaintLine(new RenderRect(left, y + height - (lines.Bottom / 2f), fullWidth, lines.Bottom), colors.Bottom);
            }
        }

        void PaintLine(RenderRect rect, RenderColor color)
        {
            if (rect.Width > 0f && rect.Height > 0f && color.A > 0)
            {
                displayList.FillRect(rect, color);
            }
        }
    }
}
