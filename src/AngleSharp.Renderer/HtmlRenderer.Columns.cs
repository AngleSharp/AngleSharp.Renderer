namespace AngleSharp.Renderer;

using System.Globalization;
using System.Linq;
using System.Threading;

using AngleSharp.Css.RenderTree;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Multi-column layout (<c>column-count</c>, <c>column-width</c>, <c>column-gap</c>,
/// <c>column-rule</c>).
/// </summary>
/// <remarks>
/// The content is laid out once, as a single column of the used column width, into a scratch
/// display list. Every line box and block bottom produced on the way is a candidate break point;
/// the column height is the smallest one that fits the content into the column count without
/// breaking a line. Each column then replays the scratch list, translated into place and clipped
/// to its own slice, so no layout code needs to know about fragmentation.
/// </remarks>
public sealed partial class HtmlRenderer
{
    private static readonly AsyncLocal<ColumnBreakCapture?> s_columnBreakCapture = new();

    /// <summary>
    /// Where content may be split into columns: <see cref="Ends"/> are the bottoms of line boxes
    /// and blocks (a column may end there), <see cref="Starts"/> the tops of line boxes (the next
    /// column starts at the first one below the break, so margins at a break are truncated).
    /// </summary>
    private sealed class ColumnBreakCapture
    {
        public List<float> Ends { get; } = [];

        public List<float> Starts { get; } = [];
    }

    /// <summary>
    /// Notes the bottom of a line box or block, where a multi-column container may end a column.
    /// </summary>
    private static void RecordColumnBreakCandidate(float y) => s_columnBreakCapture.Value?.Ends.Add(y);

    /// <summary>
    /// Notes the top of a line box, where a multi-column container may start a column.
    /// </summary>
    private static void RecordColumnContentStart(float y) => s_columnBreakCapture.Value?.Starts.Add(y);

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
    /// Lays out a multi-column container's children into balanced columns and returns the height
    /// of the columns.
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
        var scratch = new DisplayList { Fonts = context.Fonts };
        var capture = new ColumnBreakCapture();
        var previousCapture = s_columnBreakCapture.Value;
        s_columnBreakCapture.Value = capture;
        var cursorY = contentY;

        try
        {
            var previousBlockMarginBottom = 0f;
            var suppressNextBlockTopMargin = false;
            var floats = new FloatArea();

            LayoutFlowChildren(
                children,
                contentX,
                contentY,
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
                specifiedContentHeight,
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

        var totalHeight = Math.Max(0f, cursorY - contentY);
        var candidates = capture.Ends
            .Select(y => y - contentY)
            .Where(offset => offset > 0.5f && offset < totalHeight - 0.5f)
            .Append(totalHeight)
            .Distinct()
            .OrderBy(offset => offset)
            .ToList();
        var starts = capture.Starts
            .Select(y => y - contentY)
            .Distinct()
            .OrderBy(offset => offset)
            .ToList();

        // A container with an authored height fills its columns one after another (extra columns
        // overflow to the side); otherwise the column height is balanced.
        var segments = float.IsNaN(specifiedContentHeight)
            ? BalanceColumns(candidates, starts, totalHeight, columns.Count)
            : FillColumns(candidates, starts, totalHeight, Math.Max(1f, specifiedContentHeight), out _);
        var columnHeight = float.IsNaN(specifiedContentHeight)
            ? segments.Max(segment => segment.End - segment.Start)
            : specifiedContentHeight;

        for (var index = 0; index < segments.Count; index++)
        {
            var (start, end) = segments[index];
            var columnX = contentX + (index * (columns.Width + columns.Gap));

            // Content that overflows a column horizontally may spill into half of the gap.
            var clip = new RenderRect(columnX - (columns.Gap / 2f), contentY, columns.Width + columns.Gap, end - start);
            displayList.PushClip(clip, RenderCornerRadii.Zero);
            displayList.PushTransform(RenderTransform2D.Translate(columnX - contentX, -start));
            displayList.InsertRange(displayList.Commands.Count, scratch.Commands);
            displayList.PopTransform();
            displayList.PopClip();
        }

        PaintColumnRules(styleMap, segments.Count, columns, contentX, contentY, columnHeight, displayList);
        return columnHeight;
    }

    /// <summary>
    /// Finds the smallest column height that fits the content into <paramref name="columnCount"/>
    /// columns, breaking only at <paramref name="candidates"/>.
    /// </summary>
    private static List<(float Start, float End)> BalanceColumns(List<float> candidates, List<float> starts, float totalHeight, int columnCount)
    {
        if (totalHeight <= 0f)
        {
            return [(0f, 0f)];
        }

        var height = totalHeight / columnCount;

        // Each round grows the height by the smallest amount that lets some column take one more
        // piece, so this ends after at most as many rounds as there are candidates.
        for (var round = 0; round <= candidates.Count; round++)
        {
            var segments = FillColumns(candidates, starts, totalHeight, height, out var smallestIncrease);

            if (segments.Count <= columnCount || float.IsInfinity(smallestIncrease))
            {
                return segments;
            }

            height += Math.Max(0.5f, smallestIncrease);
        }

        return FillColumns(candidates, starts, totalHeight, totalHeight, out _);
    }

    /// <summary>
    /// Fills columns of <paramref name="height"/> greedily. <paramref name="smallestIncrease"/> is
    /// how much taller the columns would need to be for any of them to take one more piece.
    /// </summary>
    private static List<(float Start, float End)> FillColumns(List<float> candidates, List<float> starts, float totalHeight, float height, out float smallestIncrease)
    {
        var segments = new List<(float Start, float End)>();
        smallestIncrease = float.PositiveInfinity;
        var start = 0f;

        while (start < totalHeight - 0.01f)
        {
            var limit = start + height + 0.01f;
            var end = start;
            var next = float.NaN;

            foreach (var candidate in candidates)
            {
                if (candidate <= start + 0.01f)
                {
                    continue;
                }

                if (candidate <= limit)
                {
                    end = candidate;
                }
                else
                {
                    next = candidate;
                    break;
                }
            }

            if (end <= start)
            {
                // A single piece taller than the column still has to go somewhere.
                end = float.IsNaN(next) ? totalHeight : next;
                next = float.NaN;
            }

            if (!float.IsNaN(next))
            {
                smallestIncrease = Math.Min(smallestIncrease, next - start - height);
            }

            segments.Add((start, end));

            // The next column starts with the next line box, skipping the space (margins) between.
            start = end;

            foreach (var candidateStart in starts)
            {
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
