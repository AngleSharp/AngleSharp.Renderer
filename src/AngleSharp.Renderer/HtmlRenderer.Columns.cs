namespace AngleSharp.Renderer;

using System.Globalization;
using System.Linq;
using System.Threading;

using AngleSharp.Css.RenderTree;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Multi-column layout (<c>column-count</c>, <c>column-width</c>, <c>column-gap</c>,
/// <c>column-rule</c>, <c>column-span</c>, <c>column-fill</c>) and its fragmentation
/// (<c>break-before</c>/<c>-after</c>/<c>-inside</c>, <c>orphans</c>, <c>widows</c>).
/// </summary>
/// <remarks>
/// Each column set (the content between <c>column-span: all</c> children) is laid out once, as a
/// single column of the used column width, into a scratch display list. Every line box and block
/// bottom produced on the way is a candidate break point, refined by the break properties; the
/// column height is the smallest one that fits the content into the column count without
/// breaking a line. Each column then replays the scratch list, translated into place and clipped
/// to its own slice, so no layout code needs to know about fragmentation.
/// </remarks>
public sealed partial class HtmlRenderer
{
    private static readonly AsyncLocal<ColumnBreakCapture?> s_columnBreakCapture = new();

    /// <summary>
    /// What a multi-column container may break at: <see cref="Ends"/> are the bottoms of line
    /// boxes and blocks (a column may end there), <see cref="Starts"/> the tops of line boxes (the
    /// next column starts at the first one below the break, so margins at a break are truncated).
    /// The other lists refine those candidates: <c>break-before</c>/<c>break-after</c> between
    /// blocks, <c>break-inside: avoid</c> ranges, the lines of each paragraph (for
    /// <c>orphans</c>/<c>widows</c>), and the ranges no column may cut through (line boxes and
    /// replaced blocks).
    /// </summary>
    private sealed class ColumnBreakCapture
    {
        public List<float> Ends { get; } = [];

        public List<float> Starts { get; } = [];

        public List<(float Y, ColumnBreakKind Kind)> Between { get; } = [];

        public List<(float Top, float Bottom)> AvoidInside { get; } = [];

        public List<(float Top, float Bottom)> Monolithic { get; } = [];

        public List<(float Bottom, int Paragraph)> Lines { get; } = [];

        public List<(int Orphans, int Widows)> Paragraphs { get; } = [];

        public int CurrentParagraph { get; set; } = -1;
    }

    private enum ColumnBreakKind
    {
        Allowed,
        Avoid,
        Forced,
    }

    private readonly record struct ColumnBreak(float Offset, ColumnBreakKind Kind);

    /// <summary>
    /// Notes the bottom of a line box or block, where a multi-column container may end a column.
    /// </summary>
    private static void RecordColumnBreakCandidate(float y) => s_columnBreakCapture.Value?.Ends.Add(y);

    /// <summary>
    /// Notes a line box: a column may start at its top and end at its bottom, but never cut it.
    /// </summary>
    private static void RecordColumnLine(float top, float bottom)
    {
        if (s_columnBreakCapture.Value is not { } capture)
        {
            return;
        }

        capture.Starts.Add(top);
        capture.Ends.Add(bottom);
        capture.Monolithic.Add((top, bottom));
        capture.Lines.Add((bottom, capture.CurrentParagraph));
    }

    /// <summary>
    /// Starts a paragraph (a run of lines of one block container) whose breaks follow the
    /// container's <c>orphans</c>/<c>widows</c>; returns the paragraph to restore afterwards.
    /// </summary>
    private static int BeginColumnParagraph(IRenderNode firstChild)
    {
        if (s_columnBreakCapture.Value is not { } capture)
        {
            return -1;
        }

        var previous = capture.CurrentParagraph;
        var container = firstChild switch
        {
            ElementRenderNode element => element.Parent,
            TextRenderNode text => text.Parent as ElementRenderNode,
            _ => null,
        };

        capture.Paragraphs.Add((ReadLineCount(container, "orphans"), ReadLineCount(container, "widows")));
        capture.CurrentParagraph = capture.Paragraphs.Count - 1;
        return previous;

        static int ReadLineCount(ElementRenderNode? node, string propertyName) =>
            node is not null &&
            int.TryParse(node.ComputedStyle.GetPropertyValue(propertyName)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
            count > 0
                ? count
                : 2;
    }

    private static void EndColumnParagraph(int previous)
    {
        if (s_columnBreakCapture.Value is { } capture)
        {
            capture.CurrentParagraph = previous;
        }
    }

    /// <summary>
    /// Notes the fragmentation properties of a block laid out from <paramref name="top"/> (its
    /// margin edge, which is where the previous block ended) to <paramref name="bottom"/>.
    /// </summary>
    private static void RecordColumnBlockBreaks(IRenderNode child, float top, float bottom)
    {
        if (s_columnBreakCapture.Value is not { } capture || child is not ElementRenderNode element)
        {
            return;
        }

        var style = element.ComputedStyle;

        if (ParseBreakBetween(style.GetPropertyValue("break-before")) is { } before)
        {
            capture.Between.Add((top, before));
        }

        if (ParseBreakBetween(style.GetPropertyValue("break-after")) is { } after)
        {
            capture.Between.Add((bottom, after));
        }

        if (style.GetPropertyValue("break-inside")?.Trim().ToLowerInvariant() is "avoid" or "avoid-column")
        {
            capture.AvoidInside.Add((top, bottom));
        }

        if (IsReplacedElement(element.Ref))
        {
            capture.Monolithic.Add((top, bottom));
        }
    }

    /// <summary>
    /// <c>break-before</c>/<c>break-after</c> as they apply inside a multi-column container: every
    /// forced break value (page breaks included, as there are no pages) starts a new column.
    /// </summary>
    private static ColumnBreakKind? ParseBreakBetween(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "column" or "page" or "always" or "left" or "right" or "recto" or "verso" => ColumnBreakKind.Forced,
        "avoid" or "avoid-column" => ColumnBreakKind.Avoid,
        _ => null,
    };

    /// <summary>
    /// <c>column-span: all</c> on an in-flow child; floats and absolutely positioned boxes never
    /// span.
    /// </summary>
    private static bool IsColumnSpanner(IRenderNode node, LayoutContext context)
    {
        if (node is not ElementRenderNode element || !GeneratesBoxes(element) ||
            !string.Equals(element.ComputedStyle.GetPropertyValue("column-span")?.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);
        return styleMap.GetValueOrDefault("float")?.Trim().ToLowerInvariant() is null or "" or "none" &&
               styleMap.GetValueOrDefault("position")?.Trim().ToLowerInvariant() is not ("absolute" or "fixed");
    }

    private readonly record struct ColumnLayout(int Count, float Width, float Gap);

    /// <summary>
    /// Resolves the used column count and width of a multi-column container, per the CSS
    /// multi-column pseudo-algorithm. Returns false when the box is not a multi-column container
    /// or ends up with a single column.
    /// </summary>
    private static bool TryResolveColumnLayout(Dictionary<string, string> styleMap, float availableWidth, float fontSize, out ColumnLayout layout)
    {
        layout = default;

        var count = styleMap.TryGetValue("column-count", out var countValue) &&
                    int.TryParse(countValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) &&
                    parsedCount > 0
            ? parsedCount
            : 0;
        var width = styleMap.TryGetValue("column-width", out var widthValue)
            ? ParseLengthValue(widthValue, float.NaN, allowAuto: true)
            : float.NaN;

        if (count == 0 && (float.IsNaN(width) || width <= 0f))
        {
            return false;
        }

        // `column-gap: normal` is 1em for multi-column containers.
        var gap = fontSize;

        if (styleMap.TryGetValue("column-gap", out var gapValue) && !string.Equals(gapValue.Trim(), "normal", StringComparison.OrdinalIgnoreCase))
        {
            var parsedGap = ParseLengthValue(gapValue, float.NaN, allowAuto: false);
            gap = float.IsNaN(parsedGap) ? gap : Math.Max(0f, parsedGap);
        }

        int columns;

        if (!float.IsNaN(width) && width > 0f)
        {
            var fitting = Math.Max(1, (int)Math.Floor((availableWidth + gap) / (width + gap)));
            columns = count > 0 ? Math.Min(count, fitting) : fitting;
        }
        else
        {
            columns = count;
        }

        if (columns <= 1)
        {
            return false;
        }

        var columnWidth = Math.Max(0f, (availableWidth - ((columns - 1) * gap)) / columns);
        layout = new ColumnLayout(columns, columnWidth, gap);
        return true;
    }

    /// <summary>
    /// Lays out a multi-column container's children into columns and returns the height of its
    /// content. Every <c>column-span: all</c> child ends a column set: the content before it is
    /// balanced on its own, and the spanner is laid out across all columns below it.
    /// </summary>
    private static float LayoutMultiColumnChildren(
        IReadOnlyList<IRenderNode> children,
        ColumnLayout columns,
        Dictionary<string, string> styleMap,
        float contentX,
        float contentY,
        float specifiedContentHeight,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight)
    {
        var cursorY = contentY;
        var set = new List<IRenderNode>();
        var fullWidth = (columns.Count * columns.Width) + ((columns.Count - 1) * columns.Gap);

        foreach (var child in children)
        {
            if (!IsColumnSpanner(child, context))
            {
                set.Add(child);
                continue;
            }

            // Column sets followed by a spanner are always balanced.
            cursorY += LayoutColumnSet(set, columns, styleMap, contentX, cursorY, float.NaN, float.NaN, specifiedContentHeight, ref textIndentConsumed, textStyle, context, displayList, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight);
            set = [];

            var previousMarginBottom = 0f;
            var suppressTopMargin = false;

            LayoutNode(
                child,
                contentX,
                contentY,
                fullWidth,
                ref cursorY,
                ref previousMarginBottom,
                ref suppressTopMargin,
                new FloatArea(),
                ref textIndentConsumed,
                textStyle,
                context,
                displayList,
                float.MaxValue,
                containingHeight: specifiedContentHeight,
                positionedContainingX: positionedContainingX,
                positionedContainingY: positionedContainingY,
                positionedContainingWidth: positionedContainingWidth,
                positionedContainingHeight: positionedContainingHeight);

            cursorY += previousMarginBottom;
        }

        // With a definite height the last set takes what is left of it: `column-fill: auto` fills
        // its columns one after another, `balance` (the initial value) balances them within it.
        // Columns that do not fit overflow to the side.
        var remainingHeight = float.IsNaN(specifiedContentHeight) ? float.NaN : Math.Max(1f, specifiedContentHeight - (cursorY - contentY));
        var fillsSequentially = !float.IsNaN(remainingHeight) &&
                                string.Equals(styleMap.GetValueOrDefault("column-fill")?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

        cursorY += LayoutColumnSet(
            set,
            columns,
            styleMap,
            contentX,
            cursorY,
            fillsSequentially ? remainingHeight : float.NaN,
            fillsSequentially ? float.NaN : remainingHeight,
            specifiedContentHeight,
            ref textIndentConsumed,
            textStyle,
            context,
            displayList,
            positionedContainingX,
            positionedContainingY,
            positionedContainingWidth,
            positionedContainingHeight);

        return float.IsNaN(specifiedContentHeight) ? cursorY - contentY : specifiedContentHeight;
    }

    /// <summary>
    /// Lays out one column set at <paramref name="top"/> and returns its height. Columns are
    /// <paramref name="fillHeight"/> tall and filled one after another when that is given, else
    /// balanced (never taller than <paramref name="maxHeight"/>, when given).
    /// </summary>
    private static float LayoutColumnSet(
        IReadOnlyList<IRenderNode> children,
        ColumnLayout columns,
        Dictionary<string, string> styleMap,
        float contentX,
        float top,
        float fillHeight,
        float maxHeight,
        float containingHeight,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight)
    {
        if (children.Count == 0)
        {
            return 0f;
        }

        var scratch = new DisplayList { Fonts = context.Fonts };
        var capture = new ColumnBreakCapture();
        var previousCapture = s_columnBreakCapture.Value;
        s_columnBreakCapture.Value = capture;
        var cursorY = top;

        try
        {
            var previousBlockMarginBottom = 0f;
            var suppressNextBlockTopMargin = false;
            var floats = new FloatArea();

            LayoutFlowChildren(
                children,
                contentX,
                top,
                columns.Width,
                ref cursorY,
                ref previousBlockMarginBottom,
                ref suppressNextBlockTopMargin,
                floats,
                ref textIndentConsumed,
                textStyle,
                context,
                scratch,
                float.MaxValue,
                containingHeight,
                positionedContainingX,
                positionedContainingY,
                positionedContainingWidth,
                positionedContainingHeight,
                []);
        }
        finally
        {
            s_columnBreakCapture.Value = previousCapture;
        }

        var totalHeight = Math.Max(0f, cursorY - top);
        var breaks = CollectColumnBreaks(capture, top, totalHeight);
        var starts = capture.Starts
            .Select(y => y - top)
            .Distinct()
            .OrderBy(offset => offset)
            .ToList();
        var monolithic = capture.Monolithic.Select(range => (range.Top - top, range.Bottom - top)).ToList();

        List<(float Start, float End)> segments;
        float columnHeight;

        if (!float.IsNaN(fillHeight))
        {
            segments = FillColumns(breaks, starts, monolithic, totalHeight, fillHeight, out _);
            columnHeight = fillHeight;
        }
        else
        {
            segments = BalanceColumns(breaks, starts, monolithic, totalHeight, columns.Count);
            columnHeight = segments.Max(segment => segment.End - segment.Start);

            if (!float.IsNaN(maxHeight) && columnHeight > maxHeight + 0.01f)
            {
                segments = FillColumns(breaks, starts, monolithic, totalHeight, maxHeight, out _);
                columnHeight = maxHeight;
            }
        }

        for (var index = 0; index < segments.Count; index++)
        {
            var (start, end) = segments[index];
            var columnX = contentX + (index * (columns.Width + columns.Gap));

            // Content that overflows a column horizontally may spill into half of the gap.
            var clip = new RenderRect(columnX - (columns.Gap / 2f), top, columns.Width + columns.Gap, end - start);
            displayList.PushClip(clip, RenderCornerRadii.Zero);
            displayList.PushTransform(RenderTransform2D.Translate(columnX - contentX, -start));
            displayList.InsertRange(displayList.Commands.Count, scratch.Commands);
            displayList.PopTransform();
            displayList.PopClip();
        }

        PaintColumnRules(styleMap, segments.Count, columns, contentX, top, columnHeight, textStyle.IsHidden ? new DisplayList() : displayList);
        return columnHeight;
    }

    /// <summary>
    /// The places a column set may break at, as offsets from its top, in order and ending with
    /// its total height. Forced breaks (<c>break-before</c>/<c>break-after</c>) always break;
    /// breaks to avoid (<c>break-*: avoid</c>, inside a <c>break-inside: avoid</c> block, or
    /// leaving fewer lines of a paragraph than <c>orphans</c> before or <c>widows</c> after them)
    /// are only taken when no other break fits.
    /// </summary>
    private static List<ColumnBreak> CollectColumnBreaks(ColumnBreakCapture capture, float top, float totalHeight)
    {
        var entries = new List<(float Offset, ColumnBreakKind Kind, bool IsCandidate)>();

        bool InRange(float offset) => offset > 0.5f && offset < totalHeight - 0.5f;

        foreach (var end in capture.Ends)
        {
            if (InRange(end - top))
            {
                entries.Add((end - top, ColumnBreakKind.Allowed, true));
            }
        }

        // A forced break is a break even where nothing else ends (inside a parent's padding).
        foreach (var (y, kind) in capture.Between)
        {
            if (InRange(y - top))
            {
                entries.Add((y - top, kind, kind == ColumnBreakKind.Forced));
            }
        }

        foreach (var paragraph in capture.Lines.Where(line => line.Paragraph >= 0).GroupBy(line => line.Paragraph))
        {
            var lines = paragraph.ToList();
            var (orphans, widows) = capture.Paragraphs[paragraph.Key];

            for (var index = 0; index < lines.Count - 1; index++)
            {
                var before = index + 1;
                var after = lines.Count - before;

                if (before < orphans || after < widows)
                {
                    entries.Add((lines[index].Bottom - top, ColumnBreakKind.Avoid, false));
                }
            }
        }

        var breaks = new List<ColumnBreak>();

        foreach (var group in GroupNearby(entries.OrderBy(entry => entry.Offset).ToList()))
        {
            if (!group.Any(entry => entry.IsCandidate))
            {
                continue;
            }

            var offset = group[0].Offset;
            var kind = group.Any(entry => entry.Kind == ColumnBreakKind.Forced)
                ? ColumnBreakKind.Forced
                : group.Any(entry => entry.Kind == ColumnBreakKind.Avoid) ||
                  capture.AvoidInside.Any(range => offset > range.Top - top + 0.5f && offset < range.Bottom - top - 0.5f)
                    ? ColumnBreakKind.Avoid
                    : ColumnBreakKind.Allowed;
            breaks.Add(new ColumnBreak(offset, kind));
        }

        breaks.Add(new ColumnBreak(totalHeight, ColumnBreakKind.Allowed));
        return breaks;

        static IEnumerable<List<(float Offset, ColumnBreakKind Kind, bool IsCandidate)>> GroupNearby(List<(float Offset, ColumnBreakKind Kind, bool IsCandidate)> sorted)
        {
            var group = new List<(float Offset, ColumnBreakKind Kind, bool IsCandidate)>();

            foreach (var entry in sorted)
            {
                if (group.Count > 0 && entry.Offset - group[0].Offset > 0.01f)
                {
                    yield return group;
                    group = [];
                }

                group.Add(entry);
            }

            if (group.Count > 0)
            {
                yield return group;
            }
        }
    }

    /// <summary>
    /// Finds the smallest column height that fits the content into <paramref name="columnCount"/>
    /// columns, breaking only at <paramref name="breaks"/> (or, failing those, inside a block).
    /// </summary>
    private static List<(float Start, float End)> BalanceColumns(List<ColumnBreak> breaks, List<float> starts, List<(float Top, float Bottom)> monolithic, float totalHeight, int columnCount)
    {
        if (totalHeight <= 0f)
        {
            return [(0f, 0f)];
        }

        var height = totalHeight / columnCount;

        // Each round grows the height by the smallest amount that lets some column take one more
        // piece, so this ends after at most as many rounds as there are breaks.
        for (var round = 0; round <= breaks.Count; round++)
        {
            var segments = FillColumns(breaks, starts, monolithic, totalHeight, height, out var smallestIncrease);

            if (segments.Count <= columnCount || float.IsInfinity(smallestIncrease))
            {
                return segments;
            }

            height += Math.Max(0.5f, smallestIncrease);
        }

        return FillColumns(breaks, starts, monolithic, totalHeight, totalHeight, out _);
    }

    /// <summary>
    /// Fills columns of <paramref name="height"/> greedily. <paramref name="smallestIncrease"/> is
    /// how much taller the columns would need to be for any of them to take one more piece.
    /// </summary>
    private static List<(float Start, float End)> FillColumns(List<ColumnBreak> breaks, List<float> starts, List<(float Top, float Bottom)> monolithic, float totalHeight, float height, out float smallestIncrease)
    {
        var segments = new List<(float Start, float End)>();
        smallestIncrease = float.PositiveInfinity;
        var start = 0f;

        while (start < totalHeight - 0.01f)
        {
            var limit = start + height + 0.01f;
            var end = start;
            var avoidedEnd = start;
            var next = float.NaN;

            foreach (var candidate in breaks)
            {
                if (candidate.Offset <= start + 0.01f)
                {
                    continue;
                }

                if (candidate.Offset > limit)
                {
                    next = candidate.Offset;
                    break;
                }

                if (candidate.Kind == ColumnBreakKind.Avoid)
                {
                    avoidedEnd = candidate.Offset;
                    continue;
                }

                end = candidate.Offset;

                if (candidate.Kind == ColumnBreakKind.Forced)
                {
                    next = float.NaN;
                    break;
                }
            }

            var cutInsideBlock = false;

            if (end <= start && avoidedEnd > start)
            {
                end = avoidedEnd;
            }
            else if (end <= start)
            {
                // Nothing fits: a block is split at the column's end, or above the line or
                // replaced box that would be cut there. A piece that cannot be split overflows.
                var cut = start + height;
                var blocker = monolithic.Where(range => range.Top < cut - 0.01f && range.Bottom > cut + 0.01f).Select(range => range.Top).DefaultIfEmpty(float.NaN).Min();

                if (!float.IsNaN(blocker))
                {
                    cut = blocker;
                }

                if (cut > start + 0.5f && cut < totalHeight - 0.01f)
                {
                    end = cut;
                    cutInsideBlock = true;
                }
                else
                {
                    end = float.IsNaN(next) ? totalHeight : next;
                }

                next = float.NaN;
            }

            if (!float.IsNaN(next))
            {
                smallestIncrease = Math.Min(smallestIncrease, next - start - height);
            }

            segments.Add((start, end));

            // The next column starts with the next line box, skipping the space (margins) between;
            // the rest of a split block continues right at the cut.
            start = end;

            foreach (var candidateStart in starts)
            {
                if (cutInsideBlock)
                {
                    break;
                }

                if (candidateStart >= end - 0.01f)
                {
                    start = Math.Min(Math.Max(end, candidateStart), totalHeight);
                    break;
                }
            }
        }

        if (segments.Count == 0)
        {
            segments.Add((0f, totalHeight));
        }

        return segments;
    }

    private static void PaintColumnRules(Dictionary<string, string> styleMap, int columnCount, ColumnLayout columns, float contentX, float contentY, float columnHeight, DisplayList displayList)
    {
        if (columnCount <= 1 || columnHeight <= 0f ||
            !styleMap.TryGetValue("column-rule-style", out var ruleStyle) ||
            !IsPaintedBorderStyle(ParseBorderStyleToken(ruleStyle)))
        {
            return;
        }

        var ruleWidth = styleMap.TryGetValue("column-rule-width", out var ruleWidthValue)
            ? ParseLengthValue(ruleWidthValue, 3f, allowAuto: false)
            : 3f;

        if (float.IsNaN(ruleWidth) || ruleWidth <= 0f)
        {
            return;
        }

        var color = ParseColor(styleMap.TryGetValue("column-rule-color", out var ruleColor) ? ruleColor : null, RenderColor.Black);

        for (var index = 0; index < columnCount - 1; index++)
        {
            var gapCenter = contentX + ((index + 1) * columns.Width) + (index * columns.Gap) + (columns.Gap / 2f);
            displayList.FillRect(new RenderRect(gapCenter - (ruleWidth / 2f), contentY, ruleWidth, columnHeight), color);
        }
    }
}
