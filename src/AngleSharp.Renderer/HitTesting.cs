namespace AngleSharp.Renderer;

using System.Linq;
using AngleSharp.Dom;

/// <summary>
/// Finds the element painted top-most at a point, following the painting order of CSS 2.1
/// Appendix E: within a stacking context, negative z-indices first, then the content in normal
/// flow, then positioned boxes (z-index auto or 0), then positive z-indices; later in document
/// order paints above earlier, and a descendant above its ancestor. Content clipped away by an
/// ancestor's overflow is not hit, nor are boxes with visibility: hidden or pointer-events: none.
/// </summary>
internal static class HitTesting
{
    public static IElement? FindTopMostElementAt(IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, double x, double y)
    {
        IElement? best = null;
        List<IElement>? bestPath = null;

        foreach (var (element, elementMetrics) in metrics)
        {
            if (elementMetrics.Layer.IsHidden || !Contains(elementMetrics, x, y) || IsClippedAway(element, metrics, x, y))
            {
                continue;
            }

            var path = GetLayerPath(element, metrics);

            if (best is null || ComparePaintOrder(path, bestPath!, metrics) > 0)
            {
                best = element;
                bestPath = path;
            }
        }

        // generated content is hit as the element it belongs to (CSSOM View: pseudo-elements are
        // never the result), e.g. a link's `::after` stretched over a whole card is the link
        return best is null ? null : GetOriginatingElement(best);
    }

    /// <summary>
    /// The element itself, or the one a pseudo-element belongs to (its <c>ParentElement</c> is
    /// the parent of that element).
    /// </summary>
    internal static IElement GetOriginatingElement(IElement element) =>
        element is IPseudoElement pseudo && CssAnimationTracker.GetHost(pseudo) is { } host ? host : element;

    /// <summary>
    /// The element whose box the element's box is in: its parent, or for a pseudo-element the
    /// element it belongs to (whose <c>ParentElement</c> it shares, so a walk up from it would
    /// leave out that element's layer and clip).
    /// </summary>
    internal static IElement? GetParentBox(IElement element) =>
        element is IPseudoElement pseudo && CssAnimationTracker.GetHost(pseudo) is { } host ? host : element.ParentElement;

    // an inline box on several lines is hit on them, not in the box around them
    private static bool Contains(HtmlRenderer.ElementLayoutMetrics metrics, double x, double y) =>
        x >= metrics.BorderBoxX && x <= metrics.BorderBoxX + metrics.BorderBoxWidth &&
        y >= metrics.BorderBoxY && y <= metrics.BorderBoxY + metrics.BorderBoxHeight &&
        (metrics.Fragments is not { } fragments || fragments.Any(fragment =>
            x >= metrics.BorderBoxX + fragment.X && x <= metrics.BorderBoxX + fragment.X + fragment.Width &&
            y >= metrics.BorderBoxY + fragment.Y && y <= metrics.BorderBoxY + fragment.Y + fragment.Height));

    private static bool IsClippedAway(IElement element, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, double x, double y)
    {
        for (var ancestor = GetParentBox(element); ancestor is not null; ancestor = GetParentBox(ancestor))
        {
            if (metrics.TryGetValue(ancestor, out var clip) && clip.Layer.ClipsOverflow)
            {
                var left = clip.BorderBoxX + clip.BorderLeft;
                var top = clip.BorderBoxY + clip.BorderTop;
                var right = clip.BorderBoxX + clip.BorderBoxWidth - clip.BorderRight;
                var bottom = clip.BorderBoxY + clip.BorderBoxHeight - clip.BorderBottom;

                if (x < left || x > right || y < top || y > bottom)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // the layer roots from the root down to the element, which ends the path; a float paints with
    // its content above the in-flow blocks of its layer, so it counts as a layer there
    private static List<IElement> GetLayerPath(IElement element, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics)
    {
        var path = new List<IElement> { element };
        var isInLayer = metrics.TryGetValue(element, out var elementMetrics) && elementMetrics.Layer.IsLayerRoot;

        for (var ancestor = GetParentBox(element); ancestor is not null; ancestor = GetParentBox(ancestor))
        {
            if (metrics.TryGetValue(ancestor, out var ancestorMetrics) && (ancestorMetrics.Layer.IsLayerRoot || (!isInLayer && ancestorMetrics.Layer.IsFloat)))
            {
                path.Add(ancestor);
                isInLayer |= ancestorMetrics.Layer.IsLayerRoot;
            }
        }

        path.Reverse();
        return path;
    }

    private static int ComparePaintOrder(List<IElement> first, List<IElement> second, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics)
    {
        var count = Math.Min(first.Count, second.Count);

        for (var index = 0; index < count; index++)
        {
            var a = first[index];
            var b = second[index];

            if (ReferenceEquals(a, b))
            {
                continue;
            }

            var layerA = GetLayerRank(a, metrics);
            var layerB = GetLayerRank(b, metrics);

            if (layerA != layerB)
            {
                return layerA.CompareTo(layerB);
            }

            // what comes later in tree order paints above (a box contains what is in it)
            return CompareTreeOrder(a, b);
        }

        // an element paints above the layer roots it is in
        return first.Count.CompareTo(second.Count);
    }

    // -1 if a comes before b in tree order (an element before its descendants), else 1, with
    // ::before right after the element it belongs to and ::after after all of its descendants
    private static int CompareTreeOrder(IElement a, IElement b)
    {
        if (a is IPseudoElement pseudo && CssAnimationTracker.GetHost(pseudo) is { } host)
        {
            var isBefore = IsBefore(pseudo);

            if (b is IPseudoElement otherPseudo && CssAnimationTracker.GetHost(otherPseudo) is { } otherHost)
            {
                if (ReferenceEquals(host, otherHost))
                {
                    return isBefore && !IsBefore(otherPseudo) ? -1 : 1;
                }

                // the other one is where its element is, unless this one is in that element
                return CompareTreeOrder(a, otherHost) < 0 ? -1
                    : otherHost.Contains(host) && IsBefore(otherPseudo) ? 1
                    : otherHost.Contains(host) ? -1
                    : 1;
            }

            if (ReferenceEquals(b, host))
            {
                return 1;
            }

            if (host.Contains(b))
            {
                return isBefore ? -1 : 1;
            }

            return b.Contains(host) ? 1 : CompareTreeOrder(host, b);
        }

        if (b is IPseudoElement && GetOriginatingElement(b) != b)
        {
            return -CompareTreeOrder(b, a);
        }

        var position = a.CompareDocumentPosition(b);

        if ((position & DocumentPositions.ContainedBy) != 0)
        {
            return -1;
        }

        if ((position & DocumentPositions.Contains) != 0)
        {
            return 1;
        }

        return (position & DocumentPositions.Following) != 0 ? -1 : 1;

        static bool IsBefore(IPseudoElement pseudo) =>
            pseudo.PseudoName.Contains("before", StringComparison.OrdinalIgnoreCase) || pseudo.PseudoName.Contains("marker", StringComparison.OrdinalIgnoreCase);
    }

    // (step, z-index) within the stacking context: negative z, in flow (floats above blocks),
    // positioned or 0, positive z
    private static (int Step, int ZIndex) GetLayerRank(IElement element, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics)
    {
        if (!metrics.TryGetValue(element, out var elementMetrics) || !elementMetrics.Layer.IsLayerRoot)
        {
            return elementMetrics.Layer.IsFloat ? (1, 1) : (1, 0);
        }

        var zIndex = elementMetrics.Layer.ZIndex;
        return zIndex < 0 ? (0, zIndex) : zIndex > 0 ? (3, zIndex) : (2, 0);
    }
}
