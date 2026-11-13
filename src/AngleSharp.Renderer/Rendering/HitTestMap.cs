namespace AngleSharp.Renderer.Rendering;

using System.Linq;
using AngleSharp.Dom;

/// <summary>
/// The result of hit testing a <see cref="HitTestMap"/>.
/// </summary>
/// <param name="Element">The element painted top-most at the point.</param>
/// <param name="Cursor">The element's computed `cursor`, e.g., "auto" or "pointer".</param>
/// <param name="IsInScrollContainer">If an ancestor (not the viewport) scrolls its overflow, i.e., wheeling may scroll it rather than the page.</param>
public readonly record struct HitTestResult(IElement Element, string Cursor, bool IsInScrollContainer)
{
    /// <summary>
    /// Gets where the viewport of the document of the element is, for one in an iframe.
    /// </summary>
    public (double X, double Y) FrameOrigin { get; init; }
}

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
    /// <summary>
    /// Gets if a box clips the element. What is out of the flow isn't clipped by what is below its
    /// containing block.
    /// </summary>
    internal static bool IsClipped(IElement element, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps) =>
        !IsContentOf(element, null, metrics, styleMaps);

    /// <summary>
    /// Gets if the element counts as content of the container (null or the root: the page): it has
    /// to be in there without a box clipping it, and out of the flow its containing block has to be
    /// in there, too.
    /// </summary>
    internal static bool IsContentOf(IElement element, IElement? container, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps)
    {
        var escapes = GetEscape(styleMaps, element);
        var isPage = container is null || container.ParentElement is null;

        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            var isContainer = ReferenceEquals(ancestor, container);

            if (!metrics.TryGetValue(ancestor, out var ancestorMetrics))
            {
                if (isContainer)
                {
                    return isPage || escapes == ClipEscape.None;
                }

                continue;
            }

            if (escapes != ClipEscape.None && styleMaps.TryGetValue(ancestor, out var escapeStyle) &&
                (escapes == ClipEscape.ToViewport ? HtmlRenderer.HasTransformLikeEffect(escapeStyle) : HtmlRenderer.ActsAsContainingBlock(escapeStyle, 0f)))
            {
                escapes = ClipEscape.None;
            }

            if (isContainer)
            {
                return isPage || escapes == ClipEscape.None;
            }

            if (escapes == ClipEscape.None)
            {
                if (ancestorMetrics.Layer.ClipsOverflow)
                {
                    return false;
                }

                escapes = GetEscape(styleMaps, ancestor);
            }
        }

        // nothing but the page is around everything
        return container is null;
    }

    /// <summary>
    /// Gets the boxes clipping the element, up to the given one (not including it).
    /// </summary>
    internal static IEnumerable<IElement> GetClippingBoxes(IElement element, IElement? until, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps)
    {
        var escapes = GetEscape(styleMaps, element);

        for (var ancestor = element.ParentElement; ancestor is not null && !ReferenceEquals(ancestor, until); ancestor = ancestor.ParentElement)
        {
            if (!metrics.TryGetValue(ancestor, out var ancestorMetrics))
            {
                continue;
            }

            if (escapes != ClipEscape.None && styleMaps.TryGetValue(ancestor, out var escapeStyle) &&
                (escapes == ClipEscape.ToViewport ? HtmlRenderer.HasTransformLikeEffect(escapeStyle) : HtmlRenderer.ActsAsContainingBlock(escapeStyle, 0f)))
            {
                escapes = ClipEscape.None;
            }

            if (escapes == ClipEscape.None)
            {
                if (ancestorMetrics.Layer.ClipsOverflow)
                {
                    yield return ancestor;
                }

                escapes = GetEscape(styleMaps, ancestor);
            }
        }
    }

    private enum ClipEscape
    {
        None,
        ToContainingBlock,
        ToViewport,
    }

    private static ClipEscape GetEscape(IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps, IElement element) =>
        styleMaps.TryGetValue(element, out var style) && style.TryGetValue("position", out var position)
            ? position.Trim().ToLowerInvariant() switch
            {
                "absolute" => ClipEscape.ToContainingBlock,
                "fixed" => ClipEscape.ToViewport,
                _ => ClipEscape.None,
            }
            : ClipEscape.None;

    // what an ancestor with a box tells the boxes in it (see Create)
    private sealed record AncestorInfo(
        HtmlRenderer.ElementLayoutMetrics Metrics,
        bool IsFixed,
        ClipEscape Escape,
        bool HasTransformLikeEffect,
        bool ActsAsContainingBlock,
        bool IsScrolling,
        RenderRect Clip,
        RenderTransform2D? Transform);

    // Fragments: where an inline box on several lines is (within Box), if it isn't transformed
    private readonly record struct Entry(IElement Element, RenderRect Box, RenderRect Clip, string Cursor, bool IsInScrollContainer, bool IsViewportAnchored, RenderedFrame? Subframe = null, RenderRect Viewport = default, RenderRect[]? Fragments = null);

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
    public IEnumerable<IElement> Elements => _entries.SelectMany(static m => m.Subframe is { } frame ? frame.HitTestMap.Elements.Prepend(m.Element) : [m.Element]);

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

            if (Contains(entry.Box, x, entryY) && Contains(entry.Clip, x, entryY) && (entry.Fragments is null || ContainsAny(entry.Fragments, x, entryY)))
            {
                // what an iframe shows is hit in its document: everything there, as it scrolls of its own
                if (entry.Subframe is { } frame &&
                    x >= entry.Viewport.X && x < entry.Viewport.X + entry.Viewport.Width && entryY >= entry.Viewport.Y && entryY < entry.Viewport.Y + entry.Viewport.Height &&
                    frame.HitTestMap.HitTest(x - entry.Viewport.X, entryY - entry.Viewport.Y) is { } inner)
                {
                    return inner with { FrameOrigin = (inner.FrameOrigin.X + entry.Viewport.X, inner.FrameOrigin.Y + entry.Viewport.Y + (y - entryY)), IsInScrollContainer = true };
                }

                return new HitTestResult(entry.Element, entry.Cursor, entry.IsInScrollContainer);
            }
        }

        return null;
    }

    /// <summary>Gets the number of boxes in the map (not counting those in iframes).</summary>
    internal int Count => _entries.Length;

    /// <summary>
    /// Gets the map with all boxes moved down by the given distance.
    /// </summary>
    internal HitTestMap Shift(float deltaY) => new(_entries, _offsetY + deltaY);

    private static RenderTransform2D? GetTransform(IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps, IElement element, HtmlRenderer.ElementLayoutMetrics metrics, float offsetY) =>
        styleMaps.TryGetValue(element, out var style) && HtmlRenderer.HasTransform(style)
            ? HtmlRenderer.ResolveTransform(style, metrics with { BorderBoxY = metrics.BorderBoxY + offsetY }) is { IsIdentity: false } transform ? transform : null
            : null;

    internal static HitTestMap Create(IDocument document, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps, float offsetY, IReadOnlyDictionary<IElement, HtmlRenderer.SubframeEntry>? subframes = null)
    {
        var builder = new EntryBuilder(metrics, styleMaps, offsetY, subframes, GetDocumentOrder(document));
        var candidates = new List<(Entry Entry, (int Step, int ZIndex, double Order)[] Key)>(metrics.Count);

        foreach (var (element, elementMetrics) in metrics)
        {
            if (!elementMetrics.Layer.IsHidden)
            {
                candidates.Add(builder.Build(element, elementMetrics));
            }
        }

        // top-most first; ties (which only unordered elements can have) keep the first one on top
        var indexed = candidates.Select((m, i) => (m.Entry, m.Key, Index: i)).ToList();
        indexed.Sort((a, b) =>
        {
            var result = ComparePaintOrder(b.Key, a.Key);
            return result != 0 ? result : a.Index.CompareTo(b.Index);
        });
        return new HitTestMap(indexed.Select(m => m.Entry).ToArray());
    }

    /// <summary>
    /// The map with the boxes in the subtrees of the given elements where the metrics have them
    /// now, e.g. after the elements were moved: what can be hit and in which order is the same,
    /// so the other boxes and the order are kept. Null if the map can't be updated so (it was
    /// shifted, or a box in the subtrees is not in it as it was).
    /// </summary>
    internal HitTestMap? WithMovedBoxes(IReadOnlyCollection<IElement> roots, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps, float offsetY)
    {
        if (_offsetY != 0f)
        {
            return null;
        }

        var builder = new EntryBuilder(metrics, styleMaps, offsetY, null, null);
        var entries = (Entry[])_entries.Clone();

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];

            if (!IsInAny(roots, entry.Element))
            {
                continue;
            }

            if (entry.Subframe is not null || !metrics.TryGetValue(entry.Element, out var elementMetrics) || elementMetrics.Layer.IsHidden)
            {
                return null;
            }

            entries[index] = builder.Build(entry.Element, elementMetrics, withKey: false).Entry;
        }

        return new HitTestMap(entries);

        static bool IsInAny(IReadOnlyCollection<IElement> roots, IElement element)
        {
            var host = element is IPseudoElement ? element.ParentElement : element;

            foreach (var root in roots)
            {
                if (host is not null && (ReferenceEquals(root, host) || root.Contains(host)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // Makes the entry of a box (and the key it is ordered by): where it can be hit, clipped by the
    // boxes around it, and what it is in.
    private sealed class EntryBuilder(
        IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics,
        IReadOnlyDictionary<IElement, Dictionary<string, string>> styleMaps,
        float offsetY,
        IReadOnlyDictionary<IElement, HtmlRenderer.SubframeEntry>? subframes,
        Dictionary<IElement, (int Start, int End)>? order)
    {
        // what the walks from the boxes up to the root ask of an ancestor, once for each of them
        private readonly Dictionary<IElement, AncestorInfo?> _ancestors = new(ReferenceEqualityComparer.Instance);
        private readonly double _unordered = order?.Count ?? 0;

        public (Entry Entry, (int Step, int ZIndex, double Order)[] Key) Build(IElement element, HtmlRenderer.ElementLayoutMetrics elementMetrics, bool withKey = true)
        {
            var box = new RenderRect(elementMetrics.BorderBoxX, elementMetrics.BorderBoxY + offsetY, elementMetrics.BorderBoxWidth, elementMetrics.BorderBoxHeight);
            var clip = new RenderRect(-Unbounded, -Unbounded, 2 * Unbounded, 2 * Unbounded);
            var fragments = elementMetrics.Fragments?.Select(fragment => new RenderRect(box.X + fragment.X, box.Y + fragment.Y, fragment.Width, fragment.Height)).ToArray();

            if (GetTransform(styleMaps, element, elementMetrics, offsetY) is { } ownTransform)
            {
                box = ownTransform.MapBounds(box);
                fragments = null;
            }

            var path = withKey ? new List<(int Step, int ZIndex, double Order)> { (1, 0, GetOrder(element)) } : null;
            // a float paints (and is hit) above the in-flow blocks of its layer, also the later
            // ones overlapping it, with what is in it (CSS 2.2 Appendix E): it is ranked as if it
            // was a layer of its own there, up to the first layer root around it
            var isInLayer = elementMetrics.Layer.IsLayerRoot;

            if (path is not null && !isInLayer && elementMetrics.Layer.IsFloat)
            {
                path[0] = FloatRank(element);
            }

            var isInScrollContainer = false;
            var isViewportAnchored = IsFixed(styleMaps, element);

            // what is out of the flow isn't clipped by what is below its containing block
            var escapes = GetEscape(styleMaps, element);

            for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                if (GetAncestorInfo(ancestor) is not { } info)
                {
                    continue;
                }

                var ancestorMetrics = info.Metrics;
                isViewportAnchored |= info.IsFixed;

                if (escapes != ClipEscape.None && (escapes == ClipEscape.ToViewport ? info.HasTransformLikeEffect : info.ActsAsContainingBlock))
                {
                    escapes = ClipEscape.None;
                }

                if (escapes == ClipEscape.None && ancestorMetrics.Layer.ClipsOverflow)
                {
                    isInScrollContainer |= info.IsScrolling;
                    clip = Intersect(clip, info.Clip);
                }

                if (path is not null && ancestorMetrics.Layer.IsLayerRoot)
                {
                    path.Add(GetRank(ancestor, ancestorMetrics.Layer));
                    isInLayer = true;
                }
                else if (path is not null && !isInLayer && ancestorMetrics.Layer.IsFloat)
                {
                    path.Add(FloatRank(ancestor));
                }

                // what is in a transformed box is hit where it is painted
                if (info.Transform is { } ancestorTransform)
                {
                    box = ancestorTransform.MapBounds(box);
                    fragments = null;
                    clip = clip.Width >= Unbounded ? clip : ancestorTransform.MapBounds(clip);
                }

                if (escapes == ClipEscape.None)
                {
                    escapes = info.Escape;
                }
            }

            if (path is not null && elementMetrics.Layer.IsLayerRoot)
            {
                path[0] = GetRank(element, elementMetrics.Layer);
            }

            path?.Reverse();
            var cursor = styleMaps.TryGetValue(element, out var styleMap) && styleMap.TryGetValue("cursor", out var value)
                ? value.Trim().ToLowerInvariant()
                : "auto";
            var subframe = subframes is not null && subframes.TryGetValue(element, out var frameEntry) ? frameEntry.Frame : null;
            var frameViewport = new RenderRect(
                box.X + elementMetrics.BorderLeft + elementMetrics.PaddingLeft,
                box.Y + elementMetrics.BorderTop + elementMetrics.PaddingTop,
                Math.Max(0f, box.Width - elementMetrics.BorderLeft - elementMetrics.BorderRight - elementMetrics.PaddingLeft - elementMetrics.PaddingRight),
                Math.Max(0f, box.Height - elementMetrics.BorderTop - elementMetrics.BorderBottom - elementMetrics.PaddingTop - elementMetrics.PaddingBottom));
            return (new Entry(element, box, clip, cursor, isInScrollContainer, isViewportAnchored, subframe, frameViewport, fragments), path?.ToArray() ?? []);
        }

        private AncestorInfo? GetAncestorInfo(IElement ancestor)
        {
            if (_ancestors.TryGetValue(ancestor, out var info))
            {
                return info;
            }

            if (!metrics.TryGetValue(ancestor, out var ancestorMetrics))
            {
                _ancestors[ancestor] = null;
                return null;
            }

            styleMaps.TryGetValue(ancestor, out var style);
            info = new AncestorInfo(
                ancestorMetrics,
                IsFixed(styleMaps, ancestor),
                GetEscape(styleMaps, ancestor),
                style is not null && HtmlRenderer.HasTransformLikeEffect(style),
                style is not null && HtmlRenderer.ActsAsContainingBlock(style, 0f),
                style is not null && IsScrolling(style),
                new RenderRect(
                    ancestorMetrics.BorderBoxX + ancestorMetrics.BorderLeft,
                    ancestorMetrics.BorderBoxY + ancestorMetrics.BorderTop + offsetY,
                    ancestorMetrics.BorderBoxWidth - ancestorMetrics.BorderLeft - ancestorMetrics.BorderRight,
                    ancestorMetrics.BorderBoxHeight - ancestorMetrics.BorderTop - ancestorMetrics.BorderBottom),
                GetTransform(styleMaps, ancestor, ancestorMetrics, offsetY));
            _ancestors[ancestor] = info;
            return info;
        }

        private double GetOrder(IElement element)
        {
            if (order is null)
            {
                return _unordered;
            }

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

            return _unordered;
        }

        // in flow (step 1), above the blocks (0) of the layer
        private (int Step, int ZIndex, double Order) FloatRank(IElement element) => (1, 1, GetOrder(element));

        private (int Step, int ZIndex, double Order) GetRank(IElement element, HtmlRenderer.HitTestLayer layer)
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

    private static bool ContainsAny(RenderRect[] rects, double x, double y)
    {
        foreach (var rect in rects)
        {
            if (Contains(rect, x, y))
            {
                return true;
            }
        }

        return false;
    }

    private static RenderRect Intersect(RenderRect a, RenderRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return new RenderRect(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
    }
}
