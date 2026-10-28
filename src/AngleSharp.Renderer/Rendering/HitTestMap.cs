namespace AngleSharp.Renderer.Rendering;

using System.Linq;
using AngleSharp.Dom;

/// <summary>
/// The result of hit testing a <see cref="HitTestMap"/>.
/// </summary>
/// <param name="Element">The element painted top-most at the point.</param>
/// <param name="Cursor">The element's computed `cursor`, e.g., "auto" or "pointer".</param>
/// <param name="IsInScrollContainer">If an ancestor (not the viewport) scrolls its overflow, i.e., wheeling may scroll it rather than the page.</param>
public readonly record struct HitTestResult(IElement Element, string Cursor, bool IsInScrollContainer);

/// <summary>
/// What a frame painted where, prepared while laying it out: the boxes that can be hit, in
/// painting order (CSS 2.1 Appendix E) and with the overflow clips
/// of their ancestors. Answering a hit test is a lookup that does not touch the DOM, so it is
/// instant and safe from any thread.
/// </summary>
public sealed class HitTestMap
{
    // larger than any page, yet small enough for the rectangle's right and bottom to be finite
    private const float Unbounded = 1e9f;

    private readonly Entry[] _entries;
    // how far the boxes moved since the map was created, see Shift
    private readonly float _offsetY;

    // a viewport-anchored box is fixed itself or inside a fixed box, so it does not scroll
    private readonly record struct Entry(IElement Element, RenderRect Box, RenderRect Clip, string Cursor, bool IsInScrollContainer, bool IsViewportAnchored);

    private HitTestMap(Entry[] entries, float offsetY = 0f)
    {
        _entries = entries;
        _offsetY = offsetY;
    }

    /// <summary>
    /// Gets a map without any boxes.
    /// </summary>
    public static HitTestMap Empty { get; } = new([]);

    /// <summary>
    /// Gets the elements that can be hit, top-most first.
    /// </summary>
    public IEnumerable<IElement> Elements => _entries.Select(m => m.Element);

    /// <summary>
    /// Finds the element painted top-most at the given point.
    /// </summary>
    /// <param name="x">The horizontal position in viewport coordinates.</param>
    /// <param name="y">The vertical position in viewport coordinates.</param>
    /// <returns>The hit element and its cursor, or null if nothing is there.</returns>
    public HitTestResult? HitTest(double x, double y)
    {
        foreach (var entry in _entries)
        {
            var entryY = entry.IsViewportAnchored ? y : y - _offsetY;

            if (Contains(entry.Box, x, entryY) && Contains(entry.Clip, x, entryY))
            {
                return new HitTestResult(entry.Element, entry.Cursor, entry.IsInScrollContainer);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the map with all boxes moved down by the given distance.
    /// </summary>
    internal HitTestMap Shift(float deltaY) => new(_entries, _offsetY + deltaY);

    internal static HitTestMap Create(IDocument document, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps, float offsetY)
    {
        var order = GetDocumentOrder(document);
        var unordered = (double)order.Count;
        var candidates = new List<(Entry Entry, (int Step, int ZIndex, double Order)[] Key)>(metrics.Count);

        foreach (var (element, elementMetrics) in metrics)
        {
            if (elementMetrics.Layer.IsHidden)
            {
                continue;
            }

            var box = new RenderRect(elementMetrics.BorderBoxX, elementMetrics.BorderBoxY + offsetY, elementMetrics.BorderBoxWidth, elementMetrics.BorderBoxHeight);
            var clip = new RenderRect(-Unbounded, -Unbounded, 2 * Unbounded, 2 * Unbounded);
            var path = new List<(int Step, int ZIndex, double Order)> { (1, 0, GetOrder(element)) };
            var isInScrollContainer = false;
            var isViewportAnchored = IsFixed(styleMaps, element);

            for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                if (!metrics.TryGetValue(ancestor, out var ancestorMetrics))
                {
                    continue;
                }

                isViewportAnchored |= IsFixed(styleMaps, ancestor);

                if (ancestorMetrics.Layer.ClipsOverflow)
                {
                    isInScrollContainer |= styleMaps.TryGetValue(ancestor, out var ancestorStyle) && IsScrolling(ancestorStyle);
                    clip = Intersect(clip, new RenderRect(
                        ancestorMetrics.BorderBoxX + ancestorMetrics.BorderLeft,
                        ancestorMetrics.BorderBoxY + ancestorMetrics.BorderTop + offsetY,
                        ancestorMetrics.BorderBoxWidth - ancestorMetrics.BorderLeft - ancestorMetrics.BorderRight,
                        ancestorMetrics.BorderBoxHeight - ancestorMetrics.BorderTop - ancestorMetrics.BorderBottom));
                }

                if (ancestorMetrics.Layer.IsLayerRoot)
                {
                    path.Add(GetRank(ancestor, ancestorMetrics.Layer));
                }
            }

            if (elementMetrics.Layer.IsLayerRoot)
            {
                path[0] = GetRank(element, elementMetrics.Layer);
            }

            path.Reverse();
            var cursor = styleMaps.TryGetValue(element, out var styleMap) && styleMap.TryGetValue("cursor", out var value)
                ? value.Trim().ToLowerInvariant()
                : "auto";
            candidates.Add((new Entry(element, box, clip, cursor, isInScrollContainer, isViewportAnchored), path.ToArray()));
        }

        // top-most first; ties (which only unordered elements can have) keep the first one on top
        var indexed = candidates.Select((m, i) => (m.Entry, m.Key, Index: i)).ToList();
        indexed.Sort((a, b) =>
        {
            var result = ComparePaintOrder(b.Key, a.Key);
            return result != 0 ? result : a.Index.CompareTo(b.Index);
        });
        return new HitTestMap(indexed.Select(m => m.Entry).ToArray());

        double GetOrder(IElement element)
        {
            if (order.TryGetValue(element, out var index))
            {
                return index.Start;
            }

            // generated content paints with its originating element: ::before ahead of the
            // element's children, ::after (and anything else) behind them
            if (element is IPseudoElement pseudo && element.ParentElement is { } host && order.TryGetValue(host, out var hostIndex))
            {
                return pseudo.PseudoName.Contains("before", StringComparison.OrdinalIgnoreCase) || pseudo.PseudoName.Contains("marker", StringComparison.OrdinalIgnoreCase)
                    ? hostIndex.Start + 0.5
                    : hostIndex.End + 0.5;
            }

            return unordered;
        }

        (int Step, int ZIndex, double Order) GetRank(IElement element, HtmlRenderer.HitTestLayer layer)
        {
            // (step, z-index) within the stacking context: negative z, in flow, positioned or 0, positive z
            var step = layer.ZIndex < 0 ? 0 : layer.ZIndex > 0 ? 3 : 2;
            return (step, layer.ZIndex, GetOrder(element));
        }
    }

    // lexicographic over the layer paths; an element paints above the layer roots it is in
    private static int ComparePaintOrder((int Step, int ZIndex, double Order)[] first, (int Step, int ZIndex, double Order)[] second)
    {
        var count = Math.Min(first.Length, second.Length);

        for (var index = 0; index < count; index++)
        {
            var result = first[index].CompareTo(second[index]);

            if (result != 0)
            {
                return result;
            }
        }

        return first.Length.CompareTo(second.Length);
    }

    /// <summary>
    /// Numbers the elements in tree order (shadow trees ahead of the light children they
    /// render), with the last number used within each element's subtree.
    /// </summary>
    private static Dictionary<IElement, (int Start, int End)> GetDocumentOrder(IDocument document)
    {
        var order = new Dictionary<IElement, (int Start, int End)>(ReferenceEqualityComparer.Instance);
        var next = 0;
        Visit(document);
        return order;

        void Visit(INode node)
        {
            if (node is IElement { ShadowRoot: { } shadowRoot })
            {
                VisitChildren(shadowRoot);
            }

            VisitChildren(node);
        }

        void VisitChildren(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IElement element)
                {
                    var start = next++;
                    Visit(element);
                    order[element] = (start, next - 1);
                }
            }
        }
    }

    private static bool IsFixed(IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps, IElement element) =>
        styleMaps.TryGetValue(element, out var styleMap) &&
        string.Equals(styleMap.GetValueOrDefault("position")?.Trim(), "fixed", StringComparison.OrdinalIgnoreCase);

    // the root and the body hand their overflow to the viewport, so their maps have none
    private static bool IsScrolling(Dictionary<string, string> styleMap)
    {
        var overflow = styleMap.GetValueOrDefault("overflow-y") ?? styleMap.GetValueOrDefault("overflow") ?? string.Empty;
        return overflow.Contains("auto", StringComparison.OrdinalIgnoreCase) || overflow.Contains("scroll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(RenderRect rect, double x, double y) =>
        x >= rect.X && x <= rect.X + rect.Width && y >= rect.Y && y <= rect.Y + rect.Height;

    private static RenderRect Intersect(RenderRect a, RenderRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new RenderRect(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
    }
}
