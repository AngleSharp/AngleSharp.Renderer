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
    /// Lays out the document unscrolled for geometry reads, measuring text like the frames do.
    /// </summary>
    internal LayoutSnapshot CaptureLayout(IDocument document, IRenderDevice renderDevice) =>
        LayoutSnapshot.LayOut(document, renderDevice, _textMeasurer, 0f, 0, isFrame: false, out _);

    /// <summary>
    /// Lays out the document for a frame scrolled to <paramref name="scrollTop"/>. The frame's
    /// surface extends <paramref name="overscan"/> rows above and below the viewport, so a host
    /// can scroll it (see <see cref="RenderedFrame.IsScrollShiftable"/>) until the next frame.
    /// </summary>
    internal RenderedFrame PrepareFrame(IDocument document, IRenderDevice renderDevice, double scrollTop, int overscan) =>
        PrepareFrame(document, renderDevice, scrollTop, overscan, out _);

    /// <summary>
    /// Lays out the document for a frame (see above), which also gives the layout to answer
    /// geometry reads from until the document changes.
    /// </summary>
    internal RenderedFrame PrepareFrame(IDocument document, IRenderDevice renderDevice, double scrollTop, int overscan, out LayoutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        overscan = Math.Max(0, overscan);
        var scrollOffsetY = (float)scrollTop - overscan;
        // the whole page is laid out: hit testing below the viewport and the scroll extent need it
        snapshot = LayoutSnapshot.LayOut(document, renderDevice, _textMeasurer, scrollOffsetY, 2 * overscan, isFrame: true, out var displayList);
        return CreateFrame(document, snapshot, displayList, scrollTop, overscan);
    }

    /// <summary>
    /// The last frame with the boxes laid out on their own since (see
    /// <see cref="LayoutSnapshot.TryPatch"/>), or null if it needs a new layout.
    /// </summary>
    internal RenderedFrame? UpdateFrame(IDocument document, RenderedFrame frame, LayoutSnapshot snapshot)
    {
        if (snapshot.IsUnchangedSinceFrame)
        {
            return frame;
        }

        var displayList = new DisplayList { Fonts = frame.DisplayList.Fonts };
        displayList.InsertRange(0, frame.DisplayList.Commands);

        if (!snapshot.TryPatch(displayList))
        {
            return null;
        }

        var updated = CreateFrame(document, snapshot, displayList, frame.ScrollTop, frame.OverscanTop);
        updated.IsAnimating = frame.IsAnimating;
        return updated;
    }

    private RenderedFrame CreateFrame(IDocument document, LayoutSnapshot snapshot, DisplayList displayList, double scrollTop, int overscan)
    {
        var scrollOffsetY = (float)scrollTop - overscan;
        var (width, height) = snapshot.Viewport;
        var styleMaps = snapshot.StyleMaps;
        var surface = new RenderViewport(width, height + (2 * overscan));
        var metrics = snapshot.LayoutMetrics;

        var isScrollShiftable = true;
        var hasStickyBoxes = false;
        var contentBottom = 0f;

        foreach (var (element, elementMetrics) in metrics)
        {
            var position = styleMaps.TryGetValue(element, out var styleMap)
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

        var hitTestMap = HitTestMap.Create(document, metrics, styleMaps, -overscan);
        var contentHeight = Math.Max(height, (double)contentBottom);
        // fixed boxes are marked in the display list, sticky ones would have to stick anew
        return new RenderedFrame(displayList, _backend, surface, height, scrollTop, overscan, contentHeight, isScrollShiftable, !hasStickyBoxes, hitTestMap);
    }
}
