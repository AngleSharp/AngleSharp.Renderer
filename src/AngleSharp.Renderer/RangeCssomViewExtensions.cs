namespace AngleSharp.Dom;

using AngleSharp;
using AngleSharp.Attributes;
using AngleSharp.Dom.Geometry;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;
using System.Linq;

/// <summary>
/// Provides CSSOM View geometry helpers for ranges.
/// </summary>
[DomExposed("Range")]
public static class RangeCssomViewExtensions
{
    /// <summary>
    /// Returns the union of the rectangles occupied by the range.
    /// </summary>
    [DomName("getBoundingClientRect")]
    public static IDomRect GetBoundingClientRect(this IRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        var rects = GetClientRects(range).Where(static rect => rect is not null).ToArray();

        if (rects.Length == 0)
        {
            return new DomRect();
        }

        var nonEmpty = rects.Where(static rect => rect!.Width > 0d && rect.Height > 0d).ToArray();
        var union = nonEmpty.Length > 0 ? nonEmpty : rects;
        var left = union.Min(static rect => rect!.Left);
        var top = union.Min(static rect => rect!.Top);
        var right = union.Max(static rect => rect!.Right);
        var bottom = union.Max(static rect => rect!.Bottom);
        return new DomRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Returns the rectangles occupied by the range's text and fully contained elements.
    /// </summary>
    [DomName("getClientRects")]
    public static IDomRectList GetClientRects(this IRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        var document = range.Head as IDocument ?? range.Head.Owner;

        if (document is null ||
            !ReferenceEquals(document, range.Tail as IDocument ?? range.Tail.Owner) ||
            !IsConnected(range.Head, document) ||
            !IsConnected(range.Tail, document))
        {
            return new ClientRectList([]);
        }

        if (!document.Context.TryGetDomHarness(out var harness))
        {
            try
            {
                harness = document.Context.GetDomHarness();
            }
            catch (InvalidOperationException)
            {
                return new ClientRectList([]);
            }
        }

        if (harness is not InteractiveHtmlRendererState state)
        {
            return new ClientRectList([]);
        }

        var frame = state.GetRangeGeometryFrame(document);
        var result = new List<(INode Node, int Offset, RenderRect Rect)>();

        foreach (var textRect in frame.TextMap.GetRangeRects(range))
        {
            var scrollShift = textRect.IsAnchored ? 0f : frame.TextMapShiftY;
            result.Add((textRect.Node, textRect.Offset, textRect.Rect with { Y = textRect.Rect.Y + scrollShift - frame.OverscanTop }));
        }

        var snapshot = state.GetLayoutSnapshot(document);
        var root = document.DocumentElement;
        var scrollLeft = root is null ? 0d : state.GetScrollLeft(root, double.MaxValue);
        var scrollTop = root is null ? 0d : state.GetScrollTop(root, double.MaxValue);

        foreach (var (element, layoutMetrics) in snapshot.Metrics)
        {
            if (element is IPseudoElement || !IsConnected(element, document) || !IsFullyContained(range, element))
            {
                continue;
            }

            var metrics = snapshot.ToViewport(layoutMetrics, scrollLeft, scrollTop);

            if (metrics.Fragments is { } fragments)
            {
                foreach (var fragment in fragments)
                {
                    var box = new RenderRect(metrics.BorderBoxX + fragment.X, metrics.BorderBoxY + fragment.Y, fragment.Width, fragment.Height);
                    result.Add((element, 0, ElementCssomViewExtensions.ApplyTransforms(element, box)));
                }
            }
            else
            {
                result.Add((element, 0, ElementCssomViewExtensions.ApplyTransforms(element, metrics.ReportedBox)));
            }
        }

        result.Sort(static (first, second) =>
        {
            var order = CompareNodes(first.Node, second.Node);
            if (order != 0)
            {
                return order;
            }

            order = first.Offset.CompareTo(second.Offset);
            return order != 0 ? order : first.Rect.Y.CompareTo(second.Rect.Y);
        });

        return new ClientRectList(result.Select(static item =>
            (IDomRect)new DomRect(item.Rect.X, item.Rect.Y, item.Rect.Width, item.Rect.Height)).ToList());
    }

    private static bool IsConnected(INode node, IDocument document) =>
        ReferenceEquals(node, document) || ReferenceEquals(node.Owner, document) && document.Contains(node);

    private static bool IsFullyContained(IRange range, IElement element)
    {
        if (element.Parent is not { } parent)
        {
            return false;
        }

        var index = parent.ChildNodes.Index(element);
        return CompareBoundaryPoints(range.Head, range.Start, parent, index) <= 0 &&
            CompareBoundaryPoints(parent, index + 1, range.Tail, range.End) <= 0;
    }

    private static int CompareBoundaryPoints(INode firstNode, int firstOffset, INode secondNode, int secondOffset)
    {
        if (ReferenceEquals(firstNode, secondNode))
        {
            return firstOffset.CompareTo(secondOffset);
        }

        var position = firstNode.CompareDocumentPosition(secondNode);

        if ((position & DocumentPositions.ContainedBy) != 0)
        {
            return ChildIndexOfAncestor(firstNode, secondNode) < firstOffset ? 1 : -1;
        }

        if ((position & DocumentPositions.Contains) != 0)
        {
            return ChildIndexOfAncestor(secondNode, firstNode) < secondOffset ? -1 : 1;
        }

        return (position & DocumentPositions.Following) != 0 ? -1 : 1;
    }

    private static int ChildIndexOfAncestor(INode parent, INode descendant)
    {
        var child = descendant;

        while (child.Parent is { } next && !ReferenceEquals(next, parent))
        {
            child = next;
        }

        return parent.ChildNodes.Index(child);
    }

    private static int CompareNodes(INode first, INode second)
    {
        if (ReferenceEquals(first, second))
        {
            return 0;
        }

        var position = first.CompareDocumentPosition(second);
        return (position & DocumentPositions.Following) != 0 ? -1 :
            (position & DocumentPositions.Preceding) != 0 ? 1 :
            (position & DocumentPositions.Contains) != 0 ? -1 : 1;
    }
}
