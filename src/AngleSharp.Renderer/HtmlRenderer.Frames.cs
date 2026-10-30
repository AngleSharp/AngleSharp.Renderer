namespace AngleSharp.Renderer;

using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Interactive frames: one layout pass yields the display list and, as its by-product, the
/// hit-test map and the page's scroll extent, so neither needs a layout of its own.
/// </summary>
public sealed partial class HtmlRenderer
{
    /// <summary>
    /// Lays out the document for a frame scrolled to <paramref name="scrollTop"/>. The frame's
    /// surface extends <paramref name="overscan"/> rows above and below the viewport, so a host
    /// can scroll it (see <see cref="RenderedFrame.IsScrollShiftable"/>) until the next frame.
    /// </summary>
    internal RenderedFrame PrepareFrame(IDocument document, IRenderDevice renderDevice, double scrollTop, int overscan)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        overscan = Math.Max(0, overscan);
        var context = CreateLayoutContext(document, renderDevice, _textMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        var surface = new RenderViewport(context.Width, context.Height + (2 * overscan));
        var scrollOffsetY = (float)scrollTop - overscan;
        var capture = new LayoutCapture();
        var previous = s_layoutCapture.Value;
        s_layoutCapture.Value = capture;
        DisplayList displayList;
        IReadOnlyDictionary<IElement, ElementLayoutMetrics> metrics;

        try
        {
            // the whole page is laid out: hit testing below the viewport and the scroll extent need it
            displayList = BuildDisplayList(document, viewport, context, renderDevice, scrollOffsetY, measureFullExtent: true, surfaceExtraHeight: 2 * overscan);
            metrics = AddHitTestLayers(capture.Snapshot(), context);
        }
        finally
        {
            s_layoutCapture.Value = previous;
        }

        var isScrollShiftable = true;
        var hasStickyBoxes = false;
        var contentBottom = 0f;

        foreach (var (element, elementMetrics) in metrics)
        {
            var position = context.StyleMapCache.TryGetValue(element, out var styleMap)
                ? styleMap.GetValueOrDefault("position")?.Trim()
                : null;

            // boxes attached to the viewport do not move with the page
            if (string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(position, "sticky", StringComparison.OrdinalIgnoreCase))
            {
                isScrollShiftable = false;
                hasStickyBoxes |= string.Equals(position, "sticky", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                contentBottom = Math.Max(contentBottom, elementMetrics.BorderBoxY + elementMetrics.BorderBoxHeight + scrollOffsetY);
            }
        }

        var hitTestMap = HitTestMap.Create(document, metrics, context.StyleMapCache, -overscan);
        var contentHeight = Math.Max(context.Height, (double)contentBottom);
        // fixed boxes are marked in the display list, sticky ones would have to stick anew
        return new RenderedFrame(displayList, _backend, surface, context.Height, scrollTop, overscan, contentHeight, isScrollShiftable, !hasStickyBoxes, hitTestMap);
    }
}
