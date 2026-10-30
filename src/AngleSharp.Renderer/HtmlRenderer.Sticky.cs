namespace AngleSharp.Renderer;

using System.Linq;

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

    /// <summary>
    /// A sticky box inside a writing-mode system, whose offset waits until the system is placed:
    /// its commands are already wrapped in <paramref name="Placeholder"/>, and
    /// <paramref name="ToOuter"/> maps its coordinates to those of the systems placed so far.
    /// </summary>
    private sealed record DeferredStickyBox(
        DisplayList DisplayList,
        PushTransformCommand Placeholder,
        StickyBox Sticky,
        RenderRect ContainingBlock,
        RenderTransform2D ToOuter);

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
        float contentOffsetY,
        bool mainAxisMarginsPlaced)
    {
        var styleMap = GetOrCreateStyleMap(context, node.Ref, node.ComputedStyle);
        var isSticky = IsStickyPositioned(styleMap);
        var start = displayList.Commands.Count;
        var previousProbe = s_stickyProbe;
        var probe = isSticky ? new StickyProbe(node.Ref) : null;
        var isViewportAnchored = string.Equals(GetPosition(styleMap), "fixed", StringComparison.Ordinal);

        if (probe is not null)
        {
            s_stickyProbe = probe;
        }

        if (isViewportAnchored)
        {
            displayList.Add(new PushViewportAnchorCommand());
        }

        context.StickyScopes.Push([]);

        try
        {
            LayoutElement(node, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed, inheritedTextStyle, context, displayList, maxY, isFlexItem, isRowDirection, flexMainSize, flexCrossSize, containingHeight, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight, forcedBorderBoxWidth, isAtomicInline, forcedBorderBoxHeight, contentOffsetY, mainAxisMarginsPlaced);
        }
        finally
        {
            context.StickyScopes.Pop();
            s_stickyProbe = previousProbe;

            if (isViewportAnchored)
            {
                displayList.Add(new PopViewportAnchorCommand());
            }
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

            // A sticky box is positioned, so it paints over the in-flow content after it.
            displayList.MoveRangeToEnd(start, count);

            // Inside a writing-mode system the viewport is only known once the system is placed.
            if (context.StickyFlows.TryPeek(out var flow))
            {
                var placeholder = new PushTransformCommand(RenderTransform2D.Identity);
                displayList.InsertRange(displayList.Commands.Count - count, [placeholder]);
                displayList.PopTransform();
                flow.Add(new DeferredStickyBox(displayList, placeholder, sticky, containingBlock, RenderTransform2D.Identity));
                continue;
            }

            var (offsetX, offsetY) = ComputeStickyOffset(sticky, containingBlock, GetStickyViewport(context));

            if (offsetX == 0f && offsetY == 0f)
            {
                continue;
            }

            displayList.InsertRange(displayList.Commands.Count - count, [new PushTransformCommand(RenderTransform2D.Translate(offsetX, offsetY))]);
            displayList.PopTransform();
            RecordStickyMetrics(sticky, offsetX, offsetY);
        }
    }

    /// <summary>
    /// Places the sticky boxes of a writing-mode system once the transform from its coordinates
    /// to the enclosing ones is known: inside another system they wait for that one, else they
    /// stick against the viewport as seen in their own coordinates, where their (logical)
    /// insets apply.
    /// </summary>
    private static void ResolveFlowStickyBoxes(List<DeferredStickyBox> stickyBoxes, RenderTransform2D toOuter, LayoutContext context)
    {
        if (stickyBoxes.Count == 0)
        {
            return;
        }

        if (context.StickyFlows.TryPeek(out var outerFlow))
        {
            outerFlow.AddRange(stickyBoxes.Select(deferred => deferred with { ToOuter = RenderTransform2D.Multiply(toOuter, deferred.ToOuter) }));
            return;
        }

        var viewport = GetStickyViewport(context);

        foreach (var deferred in stickyBoxes)
        {
            var toPage = RenderTransform2D.Multiply(toOuter, deferred.ToOuter);
            var localViewport = MapRect(Invert(toPage), viewport);
            var (offsetX, offsetY) = ComputeStickyOffset(deferred.Sticky, deferred.ContainingBlock, localViewport);

            if ((offsetX != 0f || offsetY != 0f) &&
                deferred.DisplayList.ReplaceCommand(deferred.Placeholder, new PushTransformCommand(RenderTransform2D.Translate(offsetX, offsetY))))
            {
                RecordStickyMetrics(deferred.Sticky, offsetX, offsetY);
            }
        }

        static RenderTransform2D Invert(RenderTransform2D m)
        {
            var determinant = (m.A * m.D) - (m.B * m.C);
            return new RenderTransform2D(
                m.D / determinant,
                -m.B / determinant,
                -m.C / determinant,
                m.A / determinant,
                ((m.C * m.F) - (m.D * m.E)) / determinant,
                ((m.B * m.E) - (m.A * m.F)) / determinant);
        }

        static RenderRect MapRect(RenderTransform2D m, RenderRect rect)
        {
            var corners = new[] { (rect.X, rect.Y), (rect.X + rect.Width, rect.Y), (rect.X, rect.Y + rect.Height), (rect.X + rect.Width, rect.Y + rect.Height) }
                .Select(corner => ((m.A * corner.Item1) + (m.C * corner.Item2) + m.E, (m.B * corner.Item1) + (m.D * corner.Item2) + m.F))
                .ToArray();
            var left = corners.Min(corner => corner.Item1);
            var top = corners.Min(corner => corner.Item2);
            return new RenderRect(left, top, corners.Max(corner => corner.Item1) - left, corners.Max(corner => corner.Item2) - top);
        }
    }

    /// <summary>The viewport sticky boxes stay inside, in page coordinates.</summary>
    private static RenderRect GetStickyViewport(LayoutContext context) =>
        new(context.Padding, context.Padding, context.Width - (2f * context.Padding), context.Height - (2f * context.Padding));

    private static void RecordStickyMetrics(StickyBox sticky, float offsetX, float offsetY)
    {
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

    /// <summary>
    /// How far a sticky box moves from its natural position: far enough to stay
    /// <c>top</c>/<c>bottom</c>/<c>left</c>/<c>right</c> inside the viewport (<c>top</c> wins over
    /// <c>bottom</c>, <c>left</c> over <c>right</c>), but never so far that its margin box leaves
    /// the containing block.
    /// </summary>
    private static (float X, float Y) ComputeStickyOffset(StickyBox sticky, RenderRect containingBlock, RenderRect viewport)
    {
        var style = sticky.Style;
        var box = sticky.Metrics;
        var viewportLeft = viewport.X;
        var viewportTop = viewport.Y;
        var viewportRight = viewport.X + viewport.Width;
        var viewportBottom = viewport.Y + viewport.Height;
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
