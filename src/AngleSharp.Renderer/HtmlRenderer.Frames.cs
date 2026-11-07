namespace AngleSharp.Renderer;

using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using System.Linq;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Interactive frames: one layout pass yields the display list and, as its by-product, the
/// hit-test map and the page's scroll extent, so neither needs a layout of its own.
/// </summary>
public sealed partial class HtmlRenderer
{
    // documents don't nest without an end in browsers either
    private const int MaxSubframeDepth = 10;

    [ThreadStatic]
    private static int s_trialDepth;

    [ThreadStatic]
    private static int s_subframeDepth;

    /// <summary>
    /// An iframe laid out in a page: where its document is shown, and its frame.
    /// </summary>
    internal sealed class SubframeEntry
    {
        public List<DrawSubframeCommand> Commands { get; } = [];

        public RenderRect Viewport { get; set; }

        public RenderedFrame? Frame { get; set; }
    }

    /// <summary>
    /// Holds the place of what an iframe shows. Its document is laid out after the page around
    /// it, when no layout of the page is in progress.
    /// </summary>
    private static void PaintSubframe(DisplayList displayList, IElement element, LayoutContext context, float x, float y, float width, float height)
    {
        if (s_trialDepth > 0 || width <= 0f || height <= 0f)
        {
            return;
        }

        var viewport = new RenderRect(x, y, width, height);
        var command = new DrawSubframeCommand(viewport);
        displayList.Add(command);

        if (!context.Subframes.TryGetValue(element, out var entry))
        {
            context.Subframes[element] = entry = new SubframeEntry();
        }

        entry.Commands.Add(command);
        entry.Viewport = viewport;
    }

    private static void ResolveSubframes(IDocument document, LayoutContext context)
    {
        if (context.Subframes.Count == 0 || s_subframeDepth >= MaxSubframeDepth)
        {
            return;
        }

        var parent = document.Context?.TryGetDomHarness(out var harness) == true ? harness as InteractiveHtmlRendererState : null;
        s_subframeDepth++;

        try
        {
            foreach (var (element, entry) in context.Subframes)
            {
                if (entry.Frame is not null)
                {
                    continue;
                }

                // what it loads later, and what it navigates to
                parent?.WatchFrameLoad(element);

                if (element is IHtmlInlineFrameElement { ContentDocument: { } nested } && nested.Context?.GetDomHarness() is InteractiveHtmlRendererState state)
                {
                    parent?.AttachSubframe(state);
                    state.SetViewport((int)Math.Round(entry.Viewport.Width), (int)Math.Round(entry.Viewport.Height));
                    entry.Frame = state.PrepareFrame();
                }

                foreach (var command in entry.Commands)
                {
                    command.Frame = entry.Frame;
                }
            }
        }
        finally
        {
            s_subframeDepth--;
        }
    }

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
        var isRepaintOnly = snapshot.IsRepaintOnly;
        var start = RenderMetricsRecorder.Now;

        if (!snapshot.TryPatch(displayList))
        {
            return null;
        }

        // the boxes are where they were: the frame's map and extent stay
        if (isRepaintOnly)
        {
            var metrics = snapshot.TakeMetrics();
            metrics.Layout += RenderMetricsRecorder.Since(start);
            return new RenderedFrame(displayList, frame, metrics.Complete(isIncremental: true, frame.Metrics.Boxes, displayList.Commands.Count, frame.HitTestMap.Count));
        }

        snapshot.ResolveSubframes();

        var updated = CreateFrame(document, snapshot, displayList, frame.ScrollTop, frame.OverscanTop, isIncremental: true);
        updated.IsAnimating = frame.IsAnimating;
        return updated;
    }

    private RenderedFrame CreateFrame(IDocument document, LayoutSnapshot snapshot, DisplayList displayList, double scrollTop, int overscan, bool isIncremental = false)
    {
        var recorder = snapshot.TakeMetrics();
        var hitTestingStart = RenderMetricsRecorder.Now;
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
            // what a box clips (e.g. the content of a scroll container) is no content of the page
            else if (!HitTestMap.IsClipped(element, metrics, styleMaps))
            {
                contentBottom = Math.Max(contentBottom, elementMetrics.BorderBoxY + elementMetrics.BorderBoxHeight + scrollOffsetY);
            }
        }

        var hitTestMap = HitTestMap.Create(document, metrics, styleMaps, -overscan, snapshot.Subframes);
        var contentHeight = Math.Max(height, (double)contentBottom);
        recorder.HitTesting += RenderMetricsRecorder.Since(hitTestingStart);
        // fixed boxes are marked in the display list, sticky ones would have to stick anew
        return new RenderedFrame(displayList, _backend, surface, height, scrollTop, overscan, contentHeight, isScrollShiftable, !hasStickyBoxes, hitTestMap, document: document)
        {
            Metrics = recorder.Complete(isIncremental, metrics.Count, displayList.Commands.Count, hitTestMap.Count),
        };
    }
}
