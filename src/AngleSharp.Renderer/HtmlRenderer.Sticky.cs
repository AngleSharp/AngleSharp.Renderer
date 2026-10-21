namespace AngleSharp.Renderer;

using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// <c>position: sticky</c>.
/// </summary>
/// <remarks>
/// A sticky box is laid out at its natural position and registered with the container laying out
/// its parent (every block, flex, grid and table container, and the page, opens a scope in
/// <see cref="LayoutContext.StickyScopes"/>). Once that container knows its content box,
/// <see cref="ResolveStickyBoxes"/> shifts each box by its <c>top</c>/<c>bottom</c>/<c>left</c>/
/// <c>right</c> against the viewport (layout already runs in scrolled coordinates), keeps its
/// margin box inside the container's content box, and moves its commands to the end of the
/// container's children under a translation, so it paints over the content it overlaps. The
/// viewport is the only scrollport: nested scroll containers do not scroll.
/// </remarks>
public sealed partial class HtmlRenderer
{
    [ThreadStatic]
    private static StickyProbe? s_stickyProbe;

    /// <summary>
    /// A sticky box waiting for its container: the display list it was emitted into, its first
    /// command (found again by identity, since later insertions shift indices) and command count,
    /// and its natural border box.
    /// </summary>
    private sealed record StickyBox(
        DisplayList DisplayList,
        RenderCommand FirstCommand,
        int Count,
        IElement Element,
        Dictionary<string, string> Style,
        ElementLayoutMetrics Metrics);

    /// <summary>Catches the metrics a sticky element records for itself while it is laid out.</summary>
    private sealed class StickyProbe(IElement element)
    {
        public IElement Element { get; } = element;

        public ElementLayoutMetrics? Metrics { get; set; }
    }

    private static bool ContainsBottomStickyBox(IEnumerable<IRenderNode> nodes, LayoutContext context)
    {
        foreach (var node in nodes)
        {
            if (node is not ElementRenderNode element)
            {
                continue;
            }

            var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);

            if ((IsStickyPositioned(styleMap) && !float.IsNaN(ParseLength(styleMap, "bottom", 0f, float.NaN, allowAuto: true))) ||
                ContainsBottomStickyBox(element.Children, context))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStickyElement(IRenderNode node, LayoutContext context) =>
        node is ElementRenderNode element && IsStickyPositioned(GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle));

    /// <summary>
    /// <see cref="LayoutElement"/> inside a sticky scope of its own (for the sticky boxes among its
    /// children), registering the element itself with its parent's scope when it is sticky.
    /// </summary>
    private static void LayoutStickyAwareElement(
        ElementRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        bool isFlexItem,
        bool isRowDirection,
        float? flexMainSize,
        float? flexCrossSize,
        float containingHeight,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight,
        float forcedBorderBoxWidth,
        bool isAtomicInline,
        float forcedBorderBoxHeight,
        float contentOffsetY)
    {
        var styleMap = GetOrCreateStyleMap(context, node.Ref, node.ComputedStyle);
        var isSticky = IsStickyPositioned(styleMap);
        var start = displayList.Commands.Count;
        var previousProbe = s_stickyProbe;
        var probe = isSticky ? new StickyProbe(node.Ref) : null;

        if (probe is not null)
        {
            s_stickyProbe = probe;
        }

        context.StickyScopes.Push([]);

        try
        {
            LayoutElement(node, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed, inheritedTextStyle, context, displayList, maxY, isFlexItem, isRowDirection, flexMainSize, flexCrossSize, containingHeight, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight, forcedBorderBoxWidth, isAtomicInline, forcedBorderBoxHeight, contentOffsetY);
        }
        finally
        {
            context.StickyScopes.Pop();
            s_stickyProbe = previousProbe;
        }

        if (probe?.Metrics is { } metrics && displayList.Commands.Count > start && context.StickyScopes.TryPeek(out var scope))
        {
            scope.Add(new StickyBox(displayList, displayList.Commands[start], displayList.Commands.Count - start, node.Ref, styleMap, metrics));
        }
    }

    /// <summary>
    /// Shifts the sticky boxes registered in a container's scope and moves them to the end of the
    /// container's children. Boxes registered against another display list (a trial layout or a
    /// multi-column scratch list) are left alone.
    /// </summary>
    private static void ResolveStickyBoxes(DisplayList displayList, List<StickyBox> boxes, RenderRect containingBlock, LayoutContext context)
    {
        foreach (var sticky in boxes)
        {
            if (!ReferenceEquals(sticky.DisplayList, displayList))
            {
                continue;
            }

            var commands = displayList.Commands;
            var start = -1;

            for (var index = 0; index < commands.Count; index++)
            {
                if (ReferenceEquals(commands[index], sticky.FirstCommand))
                {
                    start = index;
                    break;
                }
            }

            if (start < 0)
            {
                continue;
            }

            var count = Math.Min(sticky.Count, commands.Count - start);
            var (offsetX, offsetY) = ComputeStickyOffset(sticky, containingBlock, context);

            // A sticky box is positioned, so it paints over the in-flow content after it.
            displayList.MoveRangeToEnd(start, count);

            if (offsetX == 0f && offsetY == 0f)
            {
                continue;
            }

            displayList.InsertRange(displayList.Commands.Count - count, [new PushTransformCommand(RenderTransform2D.Translate(offsetX, offsetY))]);
            displayList.PopTransform();

            var metrics = sticky.Metrics;
            RecordLayoutMetrics(
                sticky.Element,
                metrics.BorderBoxX + offsetX,
                metrics.BorderBoxY + offsetY,
                metrics.BorderBoxWidth,
                metrics.BorderBoxHeight,
                metrics.BorderLeft,
                metrics.BorderRight,
                metrics.BorderTop,
                metrics.BorderBottom,
                metrics.PaddingLeft,
                metrics.PaddingRight,
                metrics.PaddingTop,
                metrics.PaddingBottom);
        }
    }

    /// <summary>
    /// How far a sticky box moves from its natural position: far enough to stay
    /// <c>top</c>/<c>bottom</c>/<c>left</c>/<c>right</c> inside the viewport (<c>top</c> wins over
    /// <c>bottom</c>, <c>left</c> over <c>right</c>), but never so far that its margin box leaves
    /// the containing block.
    /// </summary>
    private static (float X, float Y) ComputeStickyOffset(StickyBox sticky, RenderRect containingBlock, LayoutContext context)
    {
        var style = sticky.Style;
        var box = sticky.Metrics;
        var viewportLeft = context.Padding;
        var viewportTop = context.Padding;
        var viewportRight = context.Width - context.Padding;
        var viewportBottom = context.Height - context.Padding;
        var viewportWidth = viewportRight - viewportLeft;
        var viewportHeight = viewportBottom - viewportTop;

        var offsetY = Stick(
            box.BorderBoxY,
            box.BorderBoxHeight,
            ParseLength(style, "top", viewportHeight, float.NaN, allowAuto: true),
            ParseLength(style, "bottom", viewportHeight, float.NaN, allowAuto: true),
            viewportTop,
            viewportBottom,
            containingBlock.Y,
            containingBlock.Y + containingBlock.Height,
            ParseLength(style, "margin-top", containingBlock.Width, 0f, allowAuto: false),
            ParseLength(style, "margin-bottom", containingBlock.Width, 0f, allowAuto: false));
        var offsetX = Stick(
            box.BorderBoxX,
            box.BorderBoxWidth,
            ParseLength(style, "left", viewportWidth, float.NaN, allowAuto: true),
            ParseLength(style, "right", viewportWidth, float.NaN, allowAuto: true),
            viewportLeft,
            viewportRight,
            containingBlock.X,
            containingBlock.X + containingBlock.Width,
            ParseLength(style, "margin-left", containingBlock.Width, 0f, allowAuto: false),
            ParseLength(style, "margin-right", containingBlock.Width, 0f, allowAuto: false));

        return (offsetX, offsetY);

        static float Stick(float position, float size, float startInset, float endInset, float viewportStart, float viewportEnd, float blockStart, float blockEnd, float marginStart, float marginEnd)
        {
            var offset = 0f;

            if (!float.IsNaN(endInset) && position + size > viewportEnd - endInset)
            {
                offset = viewportEnd - endInset - (position + size);
            }

            if (!float.IsNaN(startInset) && position + offset < viewportStart + startInset)
            {
                offset = viewportStart + startInset - position;
            }

            return offset > 0f
                ? Math.Min(offset, Math.Max(0f, blockEnd - (position + size + marginEnd)))
                : Math.Max(offset, Math.Min(0f, blockStart - (position - marginStart)));
        }
    }
}
