namespace AngleSharp.Renderer;

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

        return best;
    }

    private static bool Contains(HtmlRenderer.ElementLayoutMetrics metrics, double x, double y) =>
        x >= metrics.BorderBoxX && x <= metrics.BorderBoxX + metrics.BorderBoxWidth &&
        y >= metrics.BorderBoxY && y <= metrics.BorderBoxY + metrics.BorderBoxHeight;

    private static bool IsClippedAway(IElement element, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, double x, double y)
    {
        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
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

    // the layer roots from the root down to the element, which ends the path
    private static List<IElement> GetLayerPath(IElement element, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics)
    {
        var path = new List<IElement> { element };

        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (metrics.TryGetValue(ancestor, out var ancestorMetrics) && ancestorMetrics.Layer.IsLayerRoot)
            {
                path.Add(ancestor);
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

            // b follows a in document order, so it paints above (or b contains a: a is above)
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
        }

        // an element paints above the layer roots it is in
        return first.Count.CompareTo(second.Count);
    }

    // (step, z-index) within the stacking context: negative z, in flow, positioned or 0, positive z
    private static (int Step, int ZIndex) GetLayerRank(IElement element, IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics)
    {
        if (!metrics.TryGetValue(element, out var elementMetrics) || !elementMetrics.Layer.IsLayerRoot)
        {
            return (1, 0);
        }

        var zIndex = elementMetrics.Layer.ZIndex;
        return zIndex < 0 ? (0, zIndex) : zIndex > 0 ? (3, zIndex) : (2, 0);
    }
}
