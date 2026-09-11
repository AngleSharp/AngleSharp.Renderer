namespace AngleSharp.Renderer;

using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.RenderTree;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;
using AngleSharp.Text;

using SkiaSharp;

/// <summary>
/// Renders HTML documents into image output.
/// </summary>
public sealed class HtmlRenderer
{
    // Per-document cache keeps image payloads stable across repeated renders of the same DOM instance.
    private static readonly ConditionalWeakTable<IDocument, DocumentImageCache> s_imageCacheByDocument = new();
    // Inline <svg> markup has no URL to key a per-document cache on, so it is cached per element instead.
    private static readonly ConditionalWeakTable<IElement, CachedImageResource> s_inlineSvgCacheByElement = new();
    private static readonly AsyncLocal<LayoutCapture?> s_layoutCapture = new();
    private static readonly ITextMeasurer s_defaultTextMeasurer = new SkiaTextMeasurer();

    private const float CollapsedBorderWidth = 1f;

    private readonly IRenderBackend _backend;
    private readonly ITextMeasurer _textMeasurer;

    private sealed class DocumentImageCache
    {
        public Dictionary<string, CachedImageResource?> Resources { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CachedImageResource(byte[] Bytes, string MimeType, int NaturalWidth, int NaturalHeight);

    internal readonly record struct ElementLayoutMetrics(
        float BorderBoxX,
        float BorderBoxY,
        float BorderBoxWidth,
        float BorderBoxHeight,
        float BorderLeft,
        float BorderRight,
        float BorderTop,
        float BorderBottom,
        float PaddingLeft,
        float PaddingRight,
        float PaddingTop,
        float PaddingBottom);

    private readonly record struct LayoutContext(
        int Width,
        int Height,
        RenderColor BackgroundColor,
        RenderColor TextColor,
        float Padding,
        string FontFamily,
        float FontSize,
        float LineHeightMultiplier,
        float ParagraphSpacing,
        ITextMeasurer TextMeasurer,
        FontFaceSet Fonts);

    private sealed class LayoutCapture
    {
        private readonly Dictionary<IElement, ElementLayoutMetrics> _metricsByElement = new(ReferenceEqualityComparer.Instance);

        public void Record(IElement element, ElementLayoutMetrics metrics)
        {
            _metricsByElement[element] = metrics;
        }

        public IReadOnlyDictionary<IElement, ElementLayoutMetrics> Snapshot()
        {
            return _metricsByElement;
        }
    }

    internal static IReadOnlyDictionary<IElement, ElementLayoutMetrics> CaptureLayoutMetrics(IDocument document, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        var context = CreateLayoutContext(document, renderDevice, s_defaultTextMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        var capture = new LayoutCapture();
        var previous = s_layoutCapture.Value;
        s_layoutCapture.Value = capture;

        try
        {
            _ = BuildDisplayList(document, viewport, context, renderDevice, measureFullExtent: true);
            return capture.Snapshot();
        }
        finally
        {
            s_layoutCapture.Value = previous;
        }
    }

    private static void RecordLayoutMetrics(
        IElement element,
        float borderBoxX,
        float borderBoxY,
        float borderBoxWidth,
        float borderBoxHeight,
        float borderLeft,
        float borderRight,
        float borderTop,
        float borderBottom,
        float paddingLeft,
        float paddingRight,
        float paddingTop,
        float paddingBottom)
    {
        var capture = s_layoutCapture.Value;
        if (capture is null)
        {
            return;
        }

        capture.Record(element, new ElementLayoutMetrics(
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom));
    }

    /// <summary>
    /// Creates a new renderer with a default Skia backend.
    /// </summary>
    public HtmlRenderer()
        : this(new SkiaRenderBackend())
    {
    }

    /// <summary>
    /// Creates a new renderer with a specific backend. A backend that measures text itself is
    /// used for layout as well, so that layout and rasterization agree on advance widths.
    /// </summary>
    /// <param name="backend">The backend used for rasterization.</param>
    public HtmlRenderer(IRenderBackend backend)
        : this(backend, (backend as ITextMeasurer) ?? s_defaultTextMeasurer)
    {
    }

    /// <summary>
    /// Creates a new renderer with a specific backend and text measurer.
    /// </summary>
    /// <param name="backend">The backend used for rasterization.</param>
    /// <param name="textMeasurer">The measurer used to compute advance widths during layout.</param>
    public HtmlRenderer(IRenderBackend backend, ITextMeasurer textMeasurer)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(textMeasurer);
        _backend = backend;
        _textMeasurer = textMeasurer;
    }

    private static LayoutContext CreateLayoutContext(IDocument document, IRenderDevice renderDevice, ITextMeasurer textMeasurer)
    {
        var width = Math.Max(1, (int)Math.Round(Convert.ToDouble(renderDevice.ViewPortWidth)));
        var height = Math.Max(1, (int)Math.Round(Convert.ToDouble(renderDevice.ViewPortHeight)));
        var defaultFontSize = Math.Max(1f, (float)renderDevice.FontSize);
        var defaultFontFamily = "sans-serif";
        var defaultLineHeight = 1.35f;
        var defaultTextColor = RenderColor.Black;
        var defaultBackgroundColor = RenderColor.White;

        var rootElement = document.Body ?? document.DocumentElement;
        if (rootElement is not null)
        {
            var styleMap = CreateStyleMap(rootElement.ComputeCurrentStyle(), rootElement);
            defaultFontSize = ParseLength(styleMap, "font-size", defaultFontSize, defaultFontSize, allowAuto: false);
            defaultFontFamily = styleMap.TryGetValue("font-family", out var family) && !string.IsNullOrWhiteSpace(family)
                ? family.Trim('\'', '"', ' ')
                : defaultFontFamily;
            defaultLineHeight = ParseLineHeight(styleMap, defaultLineHeight);
            defaultTextColor = ParseColor(styleMap.TryGetValue("color", out var colorValue) ? colorValue : null, defaultTextColor);
            defaultBackgroundColor = ParseColor(styleMap.TryGetValue("background-color", out var backgroundValue) ? backgroundValue : null, defaultBackgroundColor);
        }

        return new LayoutContext(
            Width: width,
            Height: height,
            BackgroundColor: defaultBackgroundColor,
            TextColor: defaultTextColor,
            Padding: 0f,
            FontFamily: defaultFontFamily,
            FontSize: defaultFontSize,
            LineHeightMultiplier: defaultLineHeight,
            ParagraphSpacing: 0f,
            TextMeasurer: textMeasurer,
            Fonts: FontFaceLoader.Load(document));
    }

    /// <summary>
    /// Renders the given document to a PNG image.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <returns>The rendered PNG image.</returns>
    public RenderedImage RenderToPng(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var renderDevice = document.Context.GetService<IRenderDevice>();
        return RenderToPng(document, renderDevice!);
    }

    /// <summary>
    /// Renders the given document to a PNG image.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="renderDevice">The render device used for viewport and typography defaults.</param>
    /// <returns>The rendered PNG image.</returns>
    public RenderedImage RenderToPng(IDocument document, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        var context = CreateLayoutContext(document, renderDevice, _textMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        var displayList = BuildDisplayList(document, viewport, context, renderDevice, ResolveRootScrollOffsetY(document));

        return _backend.RenderToPng(displayList, viewport);
    }

    /// <summary>
    /// Builds a display list from the given document.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <returns>The generated display list.</returns>
    public DisplayList BuildDisplayList(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var renderDevice = document.Context.GetService<IRenderDevice>();
        return BuildDisplayList(document, renderDevice!);
    }

    /// <summary>
    /// Builds a display list from the given document.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="renderDevice">The render device used for viewport and typography defaults.</param>
    /// <returns>The generated display list.</returns>
    public DisplayList BuildDisplayList(IDocument document, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        var context = CreateLayoutContext(document, renderDevice, _textMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        return BuildDisplayList(document, viewport, context, renderDevice, ResolveRootScrollOffsetY(document));
    }

    /// <summary>
    /// Resolves the page's vertical scroll offset from the interactive DOM harness, if one has
    /// been created for the document's browsing context (via <c>IBrowsingContext.GetDomHarness()</c>
    /// - typically through <see cref="IDomHarness.PaintToPng"/> or direct use of the CSSOM-view
    /// scroll APIs). Returns 0 for the overwhelmingly common case of a document that was never
    /// wired up for interactive use, so rendering stays unaffected unless a caller has actually
    /// opted into scroll state existing at all. <see cref="CaptureLayoutMetrics"/> deliberately
    /// does not call this - <c>getBoundingClientRect</c>/<c>scrollHeight</c>/max-scroll
    /// calculations need the document's true, unscrolled layout to stay correct (and clamping a
    /// newly-set scroll position depends on exactly that), so metrics capture always lays out at
    /// scroll offset 0 regardless of whatever is currently scrolled into view for painting.
    /// </summary>
    private static float ResolveRootScrollOffsetY(IDocument document)
    {
        var scrollingElement = document.DocumentElement;

        if (scrollingElement is null || !document.Context.TryGetDomHarness(out var harness) || harness is null)
        {
            return 0f;
        }

        // double.MaxValue as the clamp ceiling means this only floors at 0, never re-clamps to an
        // upper bound - the stored value was already correctly clamped against the document's
        // true scrollable extent when it was set via the public scrollTop/scrollTo DOM APIs
        // (which measure with CaptureLayoutMetrics, unaffected by this method).
        return (float)harness.GetScrollTop(scrollingElement, double.MaxValue);
    }

    private static DisplayList BuildDisplayList(IDocument document, RenderViewport viewport, LayoutContext context, IRenderDevice renderDevice, float scrollOffsetY = 0f, bool measureFullExtent = false)
    {
        var displayList = new DisplayList { Fonts = context.Fonts };
        displayList.FillRect(new RenderRect(0f, 0f, viewport.Width, viewport.Height), context.BackgroundColor);

        var window = document.DefaultView;
        if (window is null)
        {
            return displayList;
        }

        PrepareDocumentForRendering(document);

        var renderTree = window.Render(renderDevice);
        var body = document.Body;
        var root = body is null ? renderTree : renderTree.Find(body) ?? renderTree;

        var contentX = context.Padding;
        // A positive scroll offset moves the page's content up relative to the fixed viewport
        // surface - painted the same way as an unscrolled page, just starting from a Y position
        // that can be negative. Content scrolled above or below the surface's fixed pixel bounds
        // simply falls outside what a raster surface of that size can hold; no explicit clip is
        // needed to hide it; Skia only ever writes pixels that exist within the surface itself.
        var contentY = context.Padding - scrollOffsetY;
        var contentWidth = viewport.Width - (2f * context.Padding);

        if (contentWidth <= 0f)
        {
            return displayList;
        }

        var textStyle = new RenderTextStyle(context.FontSize, context.TextColor, context.FontFamily, context.LineHeightMultiplier, 400f, false, false, false, context.TextColor, global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Solid, TextAlign.Left, 0f, 0f, 0f, [], WhiteSpaceMode.Normal, WordBreakMode.Normal, OverflowWrapMode.Normal, TextOverflowMode.Clip);
        var cursorY = contentY;
        var previousBlockMarginBottom = 0f;
        var suppressNextBlockTopMargin = false;
        var activeFloatLeftOffset = 0f;
        var activeFloatBottom = 0f;
        var textIndentConsumed = false;

        // Normal painting stops laying out content once it has gone past the visible viewport -
        // there is no need to spend time measuring what will never be rasterized. Measuring the
        // page's true scrollable extent (for scrollTop/scrollHeight/max-scroll purposes) needs the
        // opposite: the full page, regardless of how tall it is relative to the viewport.
        var maxY = measureFullExtent ? float.MaxValue : viewport.Height - context.Padding;

        // A `position: sticky` page child is laid out right here, in plain document order, like
        // any other page child - its own cursorY/margin-collapse threading must stay in sequence
        // for its layout to be correct. But once actually stuck, its final on-screen position
        // routinely overlaps *later* siblings it would otherwise paint behind in that same document
        // order - a real, confirmed bug caught by rendering the sticky-header visual test and
        // seeing the header completely painted over by the content scrolling underneath it. Each
        // sticky child's own command range is recorded here (by index, since DisplayList is a flat
        // list) and relocated to the end, in original relative order, only after every page child
        // has been laid out - see the loop below.
        var stickyRanges = new List<(int StartIndex, int Count)>();

        foreach (var child in OrderChildrenForPainting(root.Children))
        {
            var isStickyChild = child is ElementRenderNode stickyCandidate &&
                IsStickyPositioned(CreateStyleMap(stickyCandidate.ComputedStyle, stickyCandidate.Ref));
            var stickyStartIndex = displayList.Commands.Count;

            LayoutNode(
                node: child,
                containingX: contentX,
                containingY: contentY,
                containingWidth: contentWidth,
                cursorY: ref cursorY,
                previousBlockMarginBottom: ref previousBlockMarginBottom,
                suppressNextBlockTopMargin: ref suppressNextBlockTopMargin,
                activeFloatLeftOffset: ref activeFloatLeftOffset,
                activeFloatBottom: ref activeFloatBottom,
                textIndentConsumed: ref textIndentConsumed,
                textStyle: textStyle,
                context: context,
                displayList: displayList,
                maxY: maxY);

            if (isStickyChild)
            {
                stickyRanges.Add((stickyStartIndex, displayList.Commands.Count - stickyStartIndex));
            }

            if (cursorY > maxY)
            {
                break;
            }
        }

        // Each earlier move shifts every later index left by however many commands it removed, so
        // later ranges (still expressed in their original, pre-move indices) need that same
        // cumulative shift subtracted before they are moved themselves.
        var cumulativeShift = 0;

        foreach (var (startIndex, count) in stickyRanges)
        {
            displayList.MoveRangeToEnd(startIndex - cumulativeShift, count);
            cumulativeShift += count;
        }

        // The page's own scrolling elements (<html>/<body>) are never laid out as boxes of their
        // own - the loop above only ever lays out *their children* directly onto the page canvas -
        // so neither ever gets an ordinary RecordLayoutMetrics call the way a normal descendant
        // does. Synthesizing one here, sized to the full stacked content height, is what lets
        // GetScrollHeight()/GetMaxScrollTop() (via ElementCssomViewExtensions.GetScrollExtents,
        // which reads exactly this metrics map) treat the document as scrollable at all. This is a
        // no-op outside of CaptureLayoutMetrics, since RecordLayoutMetrics itself only does
        // anything while that capture is active.
        if (body is not null)
        {
            // The synthesized box represents the *client* (viewport) area, not the total content
            // height - GetScrollExtents (ElementCssomViewExtensions) separately walks this map for
            // every actual descendant (each already carrying its own, normally-recorded metrics)
            // to work out how far content actually extends past this box, exactly the way it
            // already does for any other scrollable element. Recording the full content height
            // here instead would make the client and scroll heights identical, so nothing would
            // ever look scrollable.
            var clientHeight = Math.Max(0f, viewport.Height - (2f * context.Padding));
            RecordLayoutMetrics(body, contentX, contentY, contentWidth, clientHeight, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

            if (document.DocumentElement is not null)
            {
                RecordLayoutMetrics(document.DocumentElement, contentX, contentY, contentWidth, clientHeight, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
            }
        }

        return displayList;
    }

    private static void LayoutNode(
        IRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        ref float activeFloatLeftOffset,
        ref float activeFloatBottom,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        bool isFlexItem = false,
        bool isRowDirection = true,
        float? flexMainSize = null,
        float? flexCrossSize = null)
    {
        switch (node)
        {
            case TextRenderNode textNode:
                LayoutTextNode(textNode.Ref, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, textStyle, context, displayList, maxY);
                return;
            case ElementRenderNode element:
                LayoutElement(element, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, textStyle, context, displayList, maxY, isFlexItem, isRowDirection, flexMainSize, flexCrossSize);
                return;
            default:
                return;
        }
    }

    private static void LayoutElement(
        ElementRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        ref float activeFloatLeftOffset,
        ref float activeFloatBottom,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        bool isFlexItem = false,
        bool isRowDirection = true,
        float? flexMainSize = null,
        float? flexCrossSize = null)
    {
        var element = node.Ref;
        var computedStyle = node.ComputedStyle;
        var styleMap = CreateStyleMap(node.ComputedStyle, node.Ref);

        if (!node.IsVisible())
        {
            return;
        }

        var tagName = element.LocalName;

        if (string.Equals(tagName, "script", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tagName, "style", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(tagName, "br", StringComparison.OrdinalIgnoreCase))
        {
            cursorY += inheritedTextStyle.FontSize * inheritedTextStyle.LineHeightMultiplier;
            return;
        }

        var formControlKind = ResolveFormControlKind(element);

        // input[type=hidden] never paints at all, matching the UA `display: none` browsers give
        // it - AngleSharp.Css's own UA stylesheet does not special-case it (every <input> type
        // computes to plain inline-block), so this renderer has to.
        if (formControlKind == FormControlKind.Hidden)
        {
            return;
        }

        var display = GetDisplay(styleMap);

        if (string.Equals(display, "table", StringComparison.OrdinalIgnoreCase))
        {
            LayoutTable(node, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, inheritedTextStyle, context, displayList, maxY);
            return;
        }

        // IsReplacedElementTag reads the host's own tagName, which a ::before/::after pseudo-element
        // shares (PseudoElement.LocalName/TagName proxy through) - a real browser gives a generated-
        // content pseudo its own independent display computation (defaulting to inline, per spec),
        // entirely unrelated to whatever the host's tag would otherwise force, so this renderer must
        // not either.
        var renderAsBlock = ShouldRenderAsBlock(computedStyle) || (element is not IPseudoElement && IsReplacedElementTag(tagName));
        var isInlineBlock = IsInlineBlock(computedStyle);
        var currentTextStyle = ResolveTextStyle(styleMap, inheritedTextStyle);

        if (formControlKind != FormControlKind.None)
        {
            ApplyFormControlDefaults(formControlKind, element, styleMap, currentTextStyle, context);
        }

        if (cursorY >= activeFloatBottom)
        {
            activeFloatLeftOffset = 0f;
            activeFloatBottom = 0f;
        }

        var localFloatLeftOffset = cursorY < activeFloatBottom ? activeFloatLeftOffset : 0f;
        var flowContainingX = containingX + localFloatLeftOffset;
        var flowContainingWidth = Math.Max(0f, containingWidth - localFloatLeftOffset);

        if (!renderAsBlock)
        {
            if (isInlineBlock)
            {
                renderAsBlock = true;
            }
            else
            {
            var inlineText = NormalizeWhitespace(element.TextContent ?? string.Empty, currentTextStyle.WhiteSpace);
            if (inlineText.Length > 0)
            {
                    LayoutWrappedText(inlineText, flowContainingX, flowContainingWidth, ref cursorY, currentTextStyle, context, displayList, maxY, textIndentConsumed ? 0f : currentTextStyle.TextIndent);
                    textIndentConsumed = true;
            }

            previousBlockMarginBottom = 0f;
            suppressNextBlockTopMargin = false;

            return;
            }
        }

        if (flowContainingWidth <= 0f)
        {
            return;
        }

        var box = ResolveBoxStyle(styleMap, element);
        var marginTop = ParseLength(styleMap, "margin-top", flowContainingWidth, box.Margin.Top, allowAuto: false);
        var marginBottom = ParseLength(styleMap, "margin-bottom", flowContainingWidth, box.Margin.Bottom, allowAuto: false);
        var marginLeft = ParseLength(styleMap, "margin-left", flowContainingWidth, box.Margin.Left, allowAuto: true);
        var marginRight = ParseLength(styleMap, "margin-right", flowContainingWidth, box.Margin.Right, allowAuto: true);

        if (suppressNextBlockTopMargin)
        {
            marginTop = 0f;
            suppressNextBlockTopMargin = false;
        }

        var borderTop = box.BorderWidth.Top;
        var borderRight = box.BorderWidth.Right;
        var borderBottom = box.BorderWidth.Bottom;
        var borderLeft = box.BorderWidth.Left;

        var paddingTop = ParseLength(styleMap, "padding-top", flowContainingWidth, box.Padding.Top, allowAuto: false);
        var paddingRight = ParseLength(styleMap, "padding-right", flowContainingWidth, box.Padding.Right, allowAuto: false);
        var paddingBottom = ParseLength(styleMap, "padding-bottom", flowContainingWidth, box.Padding.Bottom, allowAuto: false);
        var paddingLeft = ParseLength(styleMap, "padding-left", flowContainingWidth, box.Padding.Left, allowAuto: false);

        var position = GetPosition(styleMap);
        var isAbsolute = string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase);
        var isFixed = string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase);
        var isRelative = string.Equals(position, "relative", StringComparison.OrdinalIgnoreCase);
        var isSticky = string.Equals(position, "sticky", StringComparison.OrdinalIgnoreCase);
        var isFloatLeft = string.Equals(GetFloat(styleMap), "left", StringComparison.OrdinalIgnoreCase);

        if (isAbsolute || isFixed)
        {
            marginTop = 0f;
            marginBottom = 0f;
            marginLeft = 0f;
            marginRight = 0f;
        }

        var collapseWithFirstChild = borderTop <= 0f && paddingTop <= 0f;
        var effectiveMarginTop = marginTop;

        if (collapseWithFirstChild &&
            TryGetFirstCollapsibleChildTopMargin(node, flowContainingWidth, out var firstChildTopMargin))
        {
            effectiveMarginTop = CollapseMargins(marginTop, firstChildTopMargin);
        }

        var specifiedContentWidth = ResolveFlexibleContentDimension(
            styleMap,
            flowContainingWidth,
            float.NaN,
            isFlexItem,
            isRowDirection,
            flexMainSize,
            flexCrossSize,
            propertyName: "width");
        ResolveHorizontalMetrics(
            flowContainingWidth,
            specifiedContentWidth,
            borderLeft,
            borderRight,
            paddingLeft,
            paddingRight,
            ref marginLeft,
            ref marginRight,
            out var contentWidth);

        var collapsedMarginTop = (isAbsolute || isFixed) ? 0f : CollapseMargins(previousBlockMarginBottom, effectiveMarginTop);

        var flowBorderBoxX = flowContainingX + marginLeft;
        var flowBorderBoxY = cursorY + collapsedMarginTop;

        var leftOffset = ParseLength(styleMap, "left", flowContainingWidth, 0f, allowAuto: true);
        var topOffset = ParseLength(styleMap, "top", flowContainingWidth, 0f, allowAuto: true);

        if (float.IsNaN(leftOffset))
        {
            leftOffset = 0f;
        }

        if (float.IsNaN(topOffset))
        {
            topOffset = 0f;
        }

        var borderBoxX = isFixed
            ? context.Padding + leftOffset
            : isAbsolute
                ? containingX + leftOffset
                : flowBorderBoxX + (isRelative ? leftOffset : 0f);
        // `position: sticky` stays in normal flow for every other purpose (margins, cursor
        // advancement, painting order - see the isAbsolute/isFixed checks elsewhere in this
        // method, none of which match "sticky") - only its own final paint Y is adjusted here.
        // This renderer already lays out an entire scrolled page in one coordinate space where
        // Y=context.Padding is the viewport's own top edge (BuildDisplayList shifts the whole
        // page's starting Y by -scrollOffsetY up front, so flowBorderBoxY arrives already
        // viewport-relative) - "stick to `top` once scrolled past it" is therefore just clamping
        // the element's own natural Y to never go above that threshold, with no separate scroll
        // lookup needed. Only triggers when `top` is actually authored (`styleMap.ContainsKey`,
        // not just defaulted to 0 via ParseLength's own allowAuto fallback below) - an
        // unconstrained sticky element (no offset property at all) has nothing to stick to and
        // behaves exactly like `static`, per spec.
        var borderBoxY = isFixed
            ? context.Padding + topOffset
            : isAbsolute
                ? containingY + topOffset
                : isSticky && styleMap.ContainsKey("top")
                    ? Math.Max(flowBorderBoxY, context.Padding + topOffset)
                    : flowBorderBoxY + (isRelative ? topOffset : 0f);
        var contentX = borderBoxX + borderLeft + paddingLeft;
        var contentY = borderBoxY + borderTop + paddingTop;

        // The box's own background/border/shadow/outline must paint behind its children, but an
        // auto-sized box's height is only known after its children are laid out (and therefore
        // appended to the display list) - so their commands are built into a scratch buffer here
        // and spliced in before this index once the box's final size is known, rather than simply
        // appended (which would paint them on top of - and hide - the children).
        var boxPaintInsertIndex = displayList.Commands.Count;

        if (string.Equals(display, "list-item", StringComparison.OrdinalIgnoreCase))
        {
            currentTextStyle = PaintListItemMarker(displayList, element, styleMap, currentTextStyle, context, borderBoxX, contentX, contentY);
        }

        if (formControlKind != FormControlKind.None)
        {
            // Children are suppressed or absent for every kind PaintFormControl actually paints
            // content for (TextLike/Select/Button/Checkbox/Radio/Color), so their box never grows
            // past its own specified height the way an ordinary auto-sized box can - the specified
            // height (falling back to a single line, for an author-supplied "auto") is therefore
            // also this box's final content height, safe to resolve here rather than waiting for
            // the auto-height computation later in this method.
            var formControlSpecifiedHeight = ResolveFlexibleContentDimension(
                styleMap,
                flowContainingWidth,
                float.NaN,
                isFlexItem,
                isRowDirection,
                flexMainSize,
                flexCrossSize,
                propertyName: "height");
            var formControlContentHeight = float.IsNaN(formControlSpecifiedHeight)
                ? currentTextStyle.FontSize * currentTextStyle.LineHeightMultiplier
                : formControlSpecifiedHeight;
            PaintFormControl(displayList, element, formControlKind, currentTextStyle, context, contentX, contentY, contentWidth, formControlContentHeight);
        }

        var childCursorY = contentY;
        var childPreviousBlockMarginBottom = 0f;
        var childSuppressNextBlockTopMargin = collapseWithFirstChild && !float.Equals(effectiveMarginTop, marginTop);
        var childActiveFloatLeftOffset = 0f;
        var childActiveFloatBottom = 0f;
        var childTextIndentConsumed = false;
        var inlineLineActive = false;
        var inlineLineTop = contentY;
        var inlineLineHeight = currentTextStyle.FontSize * currentTextStyle.LineHeightMultiplier;
        var inlineCursorX = flowContainingX + (textIndentConsumed ? 0f : currentTextStyle.TextIndent);

        // An <svg> root's children are foreign-namespaced SVG elements (circle, text, title, ...),
        // not HTML flow content; it is rasterized as a single replaced element below, so its
        // subtree must never be walked as if it were normal inline/block content. A <select>'s
        // <option> children and a <button>'s label are painted directly by PaintFormControl above
        // instead (a <select> shows only its selected option, never every option stacked; a
        // <button>'s own label is measured up front to size the button, so it is painted the same
        // self-contained way rather than through normal child text flow) - <textarea> is
        // deliberately excluded from this list, since its child text node flowing normally through
        // the ordinary block child-layout path below is exactly what a browser's own <textarea>
        // content does, and needed no special-casing at all once the box itself got its default
        // border/padding/background chrome.
        // A ::before/::after pseudo-element's own tagName proxies through to its host (see the
        // IsReplacedElementTag/ResolveFormControlKind guards above) - an <svg>'s own generated-content
        // pseudo is not itself the foreign-namespaced SVG subtree, and must still get to paint its own
        // single synthetic text child below rather than being emptied out like the real <svg> root is.
        var orderedChildren = element is not IPseudoElement &&
                               (string.Equals(tagName, "svg", StringComparison.OrdinalIgnoreCase) ||
                               formControlKind is FormControlKind.Select or FormControlKind.Button)
            ? []
            : OrderChildrenForPainting(node.Children).ToList();
        // An inline-block child counts as inline content here too (not just plain inline/br) - it
        // is a real, confirmed bug that it previously did not: a parent whose children were *only*
        // inline-block elements (the common form-control case - two checkboxes with no other inline
        // content between them) never took this "merge onto shared lines" path at all, so every one
        // of its inline-block children fell through to the plain block-stacking path below and
        // always started its own new line, identical to display:block. Confirmed independent of
        // form controls with two plain `<span style="display:inline-block">` siblings.
        var hasInlineRun = orderedChildren.Any(child =>
            (child is ElementRenderNode childElement &&
             (!ShouldRenderAsBlock(childElement.ComputedStyle) || IsInlineBlock(childElement.ComputedStyle))) ||
            (child is ElementRenderNode childElementWithBr && string.Equals(childElementWithBr.Ref.LocalName, "br", StringComparison.OrdinalIgnoreCase)));

        if (IsFlexContainer(styleMap))
        {
            LayoutFlexContainer(
                node,
                contentX,
                contentY,
                contentWidth,
                ref cursorY,
                ref previousBlockMarginBottom,
                ref suppressNextBlockTopMargin,
                ref activeFloatLeftOffset,
                ref activeFloatBottom,
                ref textIndentConsumed,
                currentTextStyle,
                context,
                displayList,
                maxY,
                styleMap,
                borderLeft,
                borderTop,
                borderRight,
                borderBottom,
                paddingLeft,
                paddingRight,
                paddingTop,
                paddingBottom,
                box,
                flowBorderBoxX,
                flowBorderBoxY,
                borderBoxX,
                borderBoxY);
            return;
        }

        if (IsGridContainer(styleMap))
        {
            LayoutGridContainer(
                node,
                contentX,
                contentY,
                contentWidth,
                ref cursorY,
                ref previousBlockMarginBottom,
                ref suppressNextBlockTopMargin,
                ref activeFloatLeftOffset,
                ref activeFloatBottom,
                ref textIndentConsumed,
                currentTextStyle,
                context,
                displayList,
                maxY,
                styleMap,
                borderLeft,
                borderTop,
                borderRight,
                borderBottom,
                paddingLeft,
                paddingRight,
                paddingTop,
                paddingBottom,
                box,
                flowBorderBoxX,
                flowBorderBoxY,
                borderBoxX,
                borderBoxY);
            return;
        }

        if (!hasInlineRun)
        {
            foreach (var child in orderedChildren)
            {
                if (child is TextRenderNode textNode)
                {
                    LayoutTextNode(
                        textNode.Ref,
                        contentX,
                        contentWidth,
                        ref childCursorY,
                        ref childPreviousBlockMarginBottom,
                        ref childSuppressNextBlockTopMargin,
                        ref childActiveFloatLeftOffset,
                        ref childActiveFloatBottom,
                        ref textIndentConsumed,
                        currentTextStyle,
                        context,
                        displayList,
                        maxY);
                }
                else if (child is ElementRenderNode blockChild)
                {
                    LayoutNode(
                        node: blockChild,
                        containingX: contentX,
                        containingY: contentY,
                        containingWidth: contentWidth,
                        cursorY: ref childCursorY,
                        previousBlockMarginBottom: ref childPreviousBlockMarginBottom,
                        suppressNextBlockTopMargin: ref childSuppressNextBlockTopMargin,
                        activeFloatLeftOffset: ref childActiveFloatLeftOffset,
                        activeFloatBottom: ref childActiveFloatBottom,
                        textIndentConsumed: ref childTextIndentConsumed,
                        textStyle: currentTextStyle,
                        context: context,
                        displayList: displayList,
                        maxY: maxY);
                }

                if (childCursorY > maxY)
                {
                    break;
                }
            }
        }
        else
        {
            foreach (var child in orderedChildren)
            {
                // An inline-block child flows next to its siblings on the shared line (like a real
                // browser) only when its own box size can be predicted up front without laying it
                // out first - i.e. both width and height resolve to an explicit, non-auto value,
                // which is guaranteed for every form control (ApplyFormControlDefaults always
                // injects concrete pixel defaults) and for any inline-block given explicit
                // width/height in CSS. An inline-block whose size genuinely depends on its own
                // auto-flowing content (shrink-to-fit width, or height driven by wrapped children)
                // cannot be predicted this cheaply without a full trial layout, so it deliberately
                // falls back to the older, still-correct-if-visually-imperfect behavior below: its
                // own line, exactly like display:block - a documented, deliberate scope cut rather
                // than building genuine two-pass (shrink-to-fit) inline-block layout.
                float ibWidth = 0f, ibHeight = 0f, ibMarginLeft = 0f, ibMarginRight = 0f, ibMarginTop = 0f, ibMarginBottom = 0f;
                var canFlowAsInlineBlock = child is ElementRenderNode ibCandidate &&
                    IsInlineBlock(ibCandidate.ComputedStyle) &&
                    TryMeasureInlineBlockBoxSize(ibCandidate, contentWidth, currentTextStyle, context, out ibWidth, out ibHeight, out ibMarginLeft, out ibMarginRight, out ibMarginTop, out ibMarginBottom);
                var childIsBlock = child is ElementRenderNode childElement &&
                    ShouldRenderAsBlock(childElement.ComputedStyle) &&
                    !canFlowAsInlineBlock;

                if (childIsBlock)
                {
                    if (inlineLineActive)
                    {
                        childCursorY = Math.Max(childCursorY, inlineLineTop + inlineLineHeight);
                        inlineLineActive = false;
                        inlineCursorX = flowContainingX;
                        textIndentConsumed = true;
                    }

                    LayoutNode(
                        node: child,
                        containingX: contentX,
                        containingY: contentY,
                        containingWidth: contentWidth,
                        cursorY: ref childCursorY,
                        previousBlockMarginBottom: ref childPreviousBlockMarginBottom,
                        suppressNextBlockTopMargin: ref childSuppressNextBlockTopMargin,
                        activeFloatLeftOffset: ref childActiveFloatLeftOffset,
                        activeFloatBottom: ref childActiveFloatBottom,
                        textIndentConsumed: ref childTextIndentConsumed,
                        textStyle: currentTextStyle,
                        context: context,
                        displayList: displayList,
                        maxY: maxY);
                }
                else
                {
                    inlineLineActive = true;

                    if (child is TextRenderNode textNode)
                    {
                        var inlineText = NormalizeWhitespaceForInlineRun(textNode.Ref.Data, currentTextStyle.WhiteSpace);

                        if (inlineText.Length > 0)
                        {
                            LayoutInlineTextRun(
                                displayList,
                                inlineText,
                                currentTextStyle,
                                flowContainingX,
                                flowContainingWidth,
                                context,
                                ref inlineCursorX,
                                ref inlineLineTop,
                                ref inlineLineHeight,
                                ref textIndentConsumed);
                        }
                    }
                    else if (canFlowAsInlineBlock && child is ElementRenderNode inlineBlockElement)
                    {
                        // Wraps to a new line first if this item does not fit in what is left of
                        // the current one - unless it is the very first thing being placed on this
                        // line at all, so a single inline-block item wider than its container still
                        // gets placed (on its own line) rather than looping forever.
                        var totalAdvance = ibMarginLeft + ibWidth + ibMarginRight;

                        // contentX/contentWidth (this element's own resolved content box), not
                        // flowContainingX/flowContainingWidth (the wider box this *element itself*
                        // was given to size *itself* within its own parent) - using the latter here
                        // was a real bug caught by BuildDisplayList_InlineBlockSiblingsWrapToANewLineWhenTheyDoNotFit:
                        // a narrower-than-parent container (e.g. width:50px) never wrapped its
                        // inline-block children at all, since the wrap boundary was being measured
                        // against the parent's own, much wider available width instead of this
                        // element's own.
                        if (inlineCursorX > contentX && inlineCursorX + totalAdvance > contentX + contentWidth)
                        {
                            childCursorY = Math.Max(childCursorY, inlineLineTop + inlineLineHeight);
                            inlineLineTop = childCursorY;
                            inlineCursorX = contentX;
                            inlineLineHeight = 0f;
                        }

                        // containingX is the margin box's own left edge, not the border box's -
                        // LayoutNode/LayoutElement apply this element's own margin-left internally
                        // (flowBorderBoxX = flowContainingX + marginLeft) exactly as they would for
                        // a block-level child, so the border box lands ibMarginLeft to the right of
                        // what is passed here, matching totalAdvance's own accounting below.
                        var itemStartX = inlineCursorX;
                        var inlineBlockCursorY = inlineLineTop;
                        var inlineBlockPreviousBlockMarginBottom = 0f;
                        var inlineBlockSuppressNextBlockTopMargin = false;
                        var inlineBlockActiveFloatLeftOffset = 0f;
                        var inlineBlockActiveFloatBottom = 0f;
                        var inlineBlockTextIndentConsumed = true;

                        LayoutNode(
                            node: inlineBlockElement,
                            containingX: itemStartX,
                            containingY: inlineLineTop,
                            containingWidth: contentWidth,
                            cursorY: ref inlineBlockCursorY,
                            previousBlockMarginBottom: ref inlineBlockPreviousBlockMarginBottom,
                            suppressNextBlockTopMargin: ref inlineBlockSuppressNextBlockTopMargin,
                            activeFloatLeftOffset: ref inlineBlockActiveFloatLeftOffset,
                            activeFloatBottom: ref inlineBlockActiveFloatBottom,
                            textIndentConsumed: ref inlineBlockTextIndentConsumed,
                            textStyle: currentTextStyle,
                            context: context,
                            displayList: displayList,
                            maxY: maxY);

                        inlineCursorX = itemStartX + totalAdvance;
                        inlineLineHeight = Math.Max(inlineLineHeight, ibMarginTop + ibHeight + ibMarginBottom);
                        textIndentConsumed = true;
                    }
                    else if (child is ElementRenderNode inlineElement)
                    {
                        var childTagName = inlineElement.Ref.LocalName;

                        if (string.Equals(childTagName, "br", StringComparison.OrdinalIgnoreCase))
                        {
                            childCursorY += inlineLineHeight;
                            inlineLineTop = childCursorY;
                            inlineCursorX = flowContainingX;
                            textIndentConsumed = true;
                        }
                        else
                        {
                            var childTextStyle = ResolveTextStyle(CreateStyleMap(inlineElement.ComputedStyle), currentTextStyle);
                            var inlineText = NormalizeWhitespaceForInlineRun(ResolvePlainInlineElementText(inlineElement), childTextStyle.WhiteSpace);

                            if (inlineText.Length > 0)
                            {
                                LayoutInlineTextRun(
                                    displayList,
                                    inlineText,
                                    childTextStyle,
                                    flowContainingX,
                                    flowContainingWidth,
                                    context,
                                    ref inlineCursorX,
                                    ref inlineLineTop,
                                    ref inlineLineHeight,
                                    ref textIndentConsumed);
                            }
                        }
                    }
                }
            }
        }

        if (inlineLineActive)
        {
            childCursorY = Math.Max(childCursorY, inlineLineTop + inlineLineHeight);
        }

        var autoContentHeight = Math.Max(0f, childCursorY - contentY);
        var specifiedContentHeight = ResolveFlexibleContentDimension(
            styleMap,
            flowContainingWidth,
            float.NaN,
            isFlexItem,
            isRowDirection,
            flexMainSize,
            flexCrossSize,
            propertyName: "height");
        var contentHeight = float.IsNaN(specifiedContentHeight) ? autoContentHeight : Math.Max(specifiedContentHeight, autoContentHeight);

        var borderBoxWidth = borderLeft + paddingLeft + contentWidth + paddingRight + borderRight;
        var borderBoxHeight = borderTop + paddingTop + contentHeight + paddingBottom + borderBottom;

        var canCollapseWithLastChild = borderBottom <= 0f &&
                                      paddingBottom <= 0f &&
                                      float.IsNaN(specifiedContentHeight);

        var effectiveMarginBottom = marginBottom;

        if (canCollapseWithLastChild)
        {
            effectiveMarginBottom = CollapseMargins(marginBottom, childPreviousBlockMarginBottom);
        }

        RecordLayoutMetrics(
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom);

        var clipsOverflow = ShouldClipOverflow(styleMap);
        var transform = ParseCssTransform(styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, currentTextStyle.FontSize);
        var hasTransform = !transform.IsIdentity;
        var filterFunctions = ParseCssFilter(styleMap);
        var hasFilter = filterFunctions.Count > 0;
        var opacity = ParseCssOpacity(styleMap);
        var hasOpacity = opacity < 1f;

        var boxPaintBuffer = new DisplayList();

        // A transform applies to the whole element - background, border, outline, and every
        // descendant all paint under it - so its push has to be the very first thing in this
        // element's own paint scope, wrapping even the background (unlike the overflow clip below,
        // which per spec explicitly excludes the border/outline it is nested inside).
        if (hasTransform)
        {
            boxPaintBuffer.PushTransform(transform);
        }

        // `opacity` nests inside `transform` (compositing does not care about coordinate space,
        // only about *where* transform already placed the content) but outside `filter` (matching
        // how a browser processes filter effects on the element's own content first, then
        // composites the already-filtered result onto the backdrop at the element's opacity).
        if (hasOpacity)
        {
            boxPaintBuffer.PushOpacity(opacity);
        }

        // `filter` wraps the whole element too, exactly like `transform` above - it is nested
        // inside the transform scope (not outside it) so the filter's own raster operates in the
        // element's already-transformed local space, matching how a scaled element's blur radius
        // should scale along with it rather than staying a fixed screen-space size.
        if (hasFilter)
        {
            boxPaintBuffer.PushFilter(filterFunctions);
        }

        PaintBackground(boxPaintBuffer, box.BackgroundPaint, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);
        PaintBoxShadows(boxPaintBuffer, box.BoxShadows, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);
        PaintBorder(boxPaintBuffer, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth, box.BorderRadius);
        PaintOutline(boxPaintBuffer, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        if (clipsOverflow)
        {
            // Per spec, overflow clips at the padding edge - content can extend into the padding
            // but not past it - so the clip rect is the padding box, not the border box. Border
            // and outline are unaffected because they were already appended above, outside this
            // clip scope's push.
            var clipRect = new RenderRect(
                borderBoxX + borderLeft,
                borderBoxY + borderTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom));
            boxPaintBuffer.PushClip(clipRect, box.BorderRadius.ClampToBox(borderBoxWidth, borderBoxHeight));
        }

        displayList.InsertRange(boxPaintInsertIndex, boxPaintBuffer.Commands);

        if (TryResolveReplacedElementImage(node, styleMap, flowContainingWidth, borderBoxX + borderLeft + paddingLeft, borderBoxY + borderTop + paddingTop, out var image, out var imageRect))
        {
            displayList.DrawImage(imageRect, image!);
        }

        if (clipsOverflow)
        {
            displayList.PopClip();
        }

        // Closes the filter scope opened above, once children (and, for a replaced element, its
        // image) have all been emitted - nested inside the opacity scope, so it has to close
        // before that one does.
        if (hasFilter)
        {
            displayList.PopFilter();
        }

        // Closes the opacity scope opened above - nested inside the transform scope, so it closes
        // before that one does too.
        if (hasOpacity)
        {
            displayList.PopOpacity();
        }

        // Closes the transform scope opened above, once children (and, for a replaced element, its
        // image) have all been emitted - the outermost scope, since it was also the first pushed.
        if (hasTransform)
        {
            displayList.PopTransform();
        }

        if (isFloatLeft)
        {
            var floatRightEdge = (flowBorderBoxX + borderBoxWidth) - containingX;
            activeFloatLeftOffset = Math.Max(activeFloatLeftOffset, floatRightEdge);
            activeFloatBottom = Math.Max(activeFloatBottom, flowBorderBoxY + borderBoxHeight + effectiveMarginBottom);
            previousBlockMarginBottom = 0f;
            suppressNextBlockTopMargin = false;
            return;
        }

        if (isAbsolute || isFixed)
        {
            previousBlockMarginBottom = 0f;
            suppressNextBlockTopMargin = false;
            return;
        }

        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + context.ParagraphSpacing;
    }

    private readonly record struct FlexItemLayoutInfo(
        IRenderNode Node,
        Dictionary<string, string> Style,
        float Order,
        float FlexGrow,
        float FlexShrink,
        float BaseMainSize,
        float CrossSize,
        string AlignSelf);

    private readonly record struct GridPlacement(int LineIndex, int Span);

    /// <summary>
    /// A CSS Grid track's own sizing function, kept unresolved (unlike a plain pixel size) until
    /// <see cref="LayoutGridContainer"/>'s own two-pass sizing algorithm can run: <see cref="Auto"/>
    /// tracks are grown by the estimated size of the items placed in them (this renderer's existing
    /// approximation for content-based sizing - see <see cref="ResolveGridItemEstimatedSize"/>), and
    /// only once every <see cref="Auto"/>/<see cref="Fixed"/> track is settled can the free space
    /// left over be distributed across <see cref="Fraction"/> (`fr`) tracks.
    /// </summary>
    private sealed class GridTrackSize
    {
        public GridTrackSizeKind Kind;

        /// <summary>The resolved pixel size for <see cref="GridTrackSizeKind.Fixed"/>, or the
        /// working/final grown size for <see cref="GridTrackSizeKind.Auto"/> and (once resolved)
        /// <see cref="GridTrackSizeKind.Fraction"/>.</summary>
        public float Pixels;

        /// <summary>The `fr` count, for <see cref="GridTrackSizeKind.Fraction"/> only.</summary>
        public float FractionValue;

        /// <summary>A `minmax()` floor, honored for both <see cref="GridTrackSizeKind.Auto"/> and
        /// <see cref="GridTrackSizeKind.Fraction"/> tracks.</summary>
        public float? MinPixels;

        /// <summary>A `minmax()`/`fit-content()` ceiling, honored for
        /// <see cref="GridTrackSizeKind.Auto"/> tracks only - a flexible track's own maximum is
        /// always itself (its `fr` share), matching spec.</summary>
        public float? MaxPixels;

        public static GridTrackSize Fixed(float pixels) => new() { Kind = GridTrackSizeKind.Fixed, Pixels = Math.Max(0f, pixels) };

        public static GridTrackSize Auto(float? min = null, float? max = null) => new() { Kind = GridTrackSizeKind.Auto, MinPixels = min, MaxPixels = max };

        public static GridTrackSize Fraction(float fr, float? min = null) => new() { Kind = GridTrackSizeKind.Fraction, FractionValue = Math.Max(0f, fr), MinPixels = min };
    }

    private enum GridTrackSizeKind
    {
        Fixed,
        Auto,
        Fraction,
    }

    private static void LayoutFlexContainer(
        ElementRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        ref float activeFloatLeftOffset,
        ref float activeFloatBottom,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        Dictionary<string, string> styleMap,
        float borderLeft,
        float borderTop,
        float borderRight,
        float borderBottom,
        float paddingLeft,
        float paddingRight,
        float paddingTop,
        float paddingBottom,
        BoxStyle box,
        float flowBorderBoxX,
        float flowBorderBoxY,
        float borderBoxX,
        float borderBoxY)
    {
        // See the matching comment in LayoutElement: the container's own background/border must
        // paint behind its items, but its auto-sized height is only known after they are laid out
        // (and appended), so their paint commands are spliced in before this index instead.
        var boxPaintInsertIndex = displayList.Commands.Count;

        var flexDirection = GetFlexDirection(styleMap);
        var isRowDirection = !string.Equals(flexDirection, "column", StringComparison.OrdinalIgnoreCase) && !string.Equals(flexDirection, "column-reverse", StringComparison.OrdinalIgnoreCase);
        var isReverseDirection = string.Equals(flexDirection, "row-reverse", StringComparison.OrdinalIgnoreCase) || string.Equals(flexDirection, "column-reverse", StringComparison.OrdinalIgnoreCase);
        var justifyContent = GetJustifyContent(styleMap);
        var alignItems = GetAlignItems(styleMap);
        var flexWrap = GetFlexWrap(styleMap);
        var alignContent = GetAlignContent(styleMap);
        var flexItems = OrderChildrenForPainting(node.Children)
            .Where(child => child is ElementRenderNode || child is TextRenderNode)
            .Select(child => CreateFlexItemLayoutInfo(child, isRowDirection, containingWidth))
            .OrderBy(item => item.Order)
            .ToList();

        if (flexItems.Count == 0)
        {
            cursorY = flowBorderBoxY + borderTop + paddingTop + borderBottom + paddingBottom;
            previousBlockMarginBottom = 0f;
            suppressNextBlockTopMargin = false;
            return;
        }

        var containerMainSize = isRowDirection
            ? ParseLength(styleMap, "width", containingWidth, containingWidth, allowAuto: true)
            : ParseLength(styleMap, "height", containingWidth, containingWidth, allowAuto: true);

        if (float.IsNaN(containerMainSize) || containerMainSize <= 0f)
        {
            containerMainSize = isRowDirection ? containingWidth : containingWidth;
        }

        var specifiedCrossSize = isRowDirection
            ? ParseLength(styleMap, "height", containingWidth, float.NaN, allowAuto: true)
            : ParseLength(styleMap, "width", containingWidth, float.NaN, allowAuto: true);
        var containerCrossSize = float.IsNaN(specifiedCrossSize) ? 0f : specifiedCrossSize;

        var contentWidth = containingWidth;
        var contentHeight = containerCrossSize;

        var lines = new List<List<FlexItemLayoutInfo>>();
        var currentLine = new List<FlexItemLayoutInfo>();
        var currentLineMainSize = 0f;

        foreach (var item in flexItems)
        {
            if (string.Equals(flexWrap, "wrap", StringComparison.OrdinalIgnoreCase) && currentLine.Count > 0 && currentLineMainSize + item.BaseMainSize > containerMainSize && containerMainSize > 0f)
            {
                lines.Add(currentLine);
                currentLine = new List<FlexItemLayoutInfo>();
                currentLineMainSize = 0f;
            }

            currentLine.Add(item);
            currentLineMainSize += item.BaseMainSize;
        }

        if (currentLine.Count > 0)
        {
            lines.Add(currentLine);
        }

        var lineCrossSizes = lines.Select(line => line.Count > 0 ? line.Max(item => item.CrossSize) : 0f).ToList();
        var totalCrossSize = lineCrossSizes.Sum();
        var remainingCrossSize = Math.Max(0f, containerCrossSize - totalCrossSize);
        var crossSpacing = 0f;
        var currentCrossOffset = 0f;

        switch (alignContent)
        {
            case "center":
                currentCrossOffset = remainingCrossSize / 2f;
                break;
            case "flex-end":
                currentCrossOffset = remainingCrossSize;
                break;
            case "space-between":
                crossSpacing = lines.Count > 1 ? remainingCrossSize / Math.Max(1, lines.Count - 1) : 0f;
                break;
            case "space-around":
                crossSpacing = lines.Count > 0 ? remainingCrossSize / Math.Max(1, lines.Count) : 0f;
                currentCrossOffset = crossSpacing / 2f;
                break;
            case "space-evenly":
                crossSpacing = lines.Count > 0 ? remainingCrossSize / Math.Max(1, lines.Count + 1) : 0f;
                currentCrossOffset = crossSpacing;
                break;
            default:
                currentCrossOffset = 0f;
                break;
        }

        var childCursorY = containingY;
        var childPreviousBlockMarginBottom = 0f;
        var childSuppressNextBlockTopMargin = false;
        var childActiveFloatLeftOffset = 0f;
        var childActiveFloatBottom = 0f;
        var childTextIndentConsumed = false;
        var totalLineMainSize = 0f;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineBaseSize = line.Sum(item => item.BaseMainSize);
            var lineGrowSum = line.Sum(item => item.FlexGrow);
            var lineShrinkSum = line.Sum(item => item.FlexShrink);
            var lineItems = new List<(FlexItemLayoutInfo Item, float FinalMainSize)>(line.Count);
            var availableMainSize = Math.Max(0f, containerMainSize - lineBaseSize);
            var lineMainSize = 0f;

            foreach (var item in line)
            {
                var finalMainSize = item.BaseMainSize;

                if (availableMainSize > 0f && lineGrowSum > 0f)
                {
                    finalMainSize = item.BaseMainSize + (availableMainSize * item.FlexGrow / lineGrowSum);
                }
                else if (availableMainSize < 0f && lineShrinkSum > 0f)
                {
                    finalMainSize = Math.Max(0f, item.BaseMainSize + (availableMainSize * item.FlexShrink / lineShrinkSum));
                }

                lineItems.Add((item, finalMainSize));
                lineMainSize += finalMainSize;
            }

            var spacerCount = Math.Max(0, lineItems.Count - 1);
            var lineMainSpacing = 0f;
            var lineMainStart = 0f;

            switch (justifyContent)
            {
                case "center":
                    lineMainStart = Math.Max(0f, containerMainSize - lineMainSize) / 2f;
                    break;
                case "flex-end":
                    lineMainStart = Math.Max(0f, containerMainSize - lineMainSize);
                    break;
                case "space-between":
                    lineMainSpacing = lineItems.Count > 1 ? Math.Max(0f, containerMainSize - lineMainSize) / spacerCount : 0f;
                    break;
                case "space-around":
                    lineMainSpacing = lineItems.Count > 0 ? Math.Max(0f, containerMainSize - lineMainSize) / lineItems.Count : 0f;
                    lineMainStart = lineMainSpacing / 2f;
                    break;
                case "space-evenly":
                    lineMainSpacing = lineItems.Count > 0 ? Math.Max(0f, containerMainSize - lineMainSize) / (lineItems.Count + 1) : 0f;
                    lineMainStart = lineMainSpacing;
                    break;
                default:
                    lineMainStart = 0f;
                    break;
            }

            var lineCrossSize = lineItems.Count > 0 ? lineItems.Max(entry => entry.Item.CrossSize) : 0f;
            var lineCrossPosition = currentCrossOffset;
            var lineCrossStart = 0f;

            if (string.Equals(alignItems, "center", StringComparison.OrdinalIgnoreCase))
            {
                lineCrossStart = containerCrossSize > 0f && lineCrossSize < containerCrossSize ? (containerCrossSize - lineCrossSize) / 2f : 0f;
            }
            else if (string.Equals(alignItems, "flex-end", StringComparison.OrdinalIgnoreCase))
            {
                lineCrossStart = containerCrossSize > 0f && lineCrossSize < containerCrossSize ? containerCrossSize - lineCrossSize : 0f;
            }
            else if (string.Equals(alignItems, "stretch", StringComparison.OrdinalIgnoreCase))
            {
                lineCrossStart = 0f;
            }

            var mainOffset = isReverseDirection ? containerMainSize - lineMainStart - lineMainSize : lineMainStart;
            var currentMainOffset = 0f;

            foreach (var (item, finalMainSize) in lineItems)
            {
                var resolvedCrossSize = item.CrossSize;
                if (string.Equals(alignItems, "stretch", StringComparison.OrdinalIgnoreCase) && resolvedCrossSize <= 0f && containerCrossSize > 0f)
                {
                    resolvedCrossSize = containerCrossSize;
                }

                var itemCrossPosition = lineCrossPosition + lineCrossStart;
                var alignSelf = item.AlignSelf;

                if (string.Equals(alignSelf, "center", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = containerCrossSize > 0f && resolvedCrossSize < containerCrossSize ? (containerCrossSize - resolvedCrossSize) / 2f : 0f;
                }
                else if (string.Equals(alignSelf, "flex-end", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = containerCrossSize > 0f && resolvedCrossSize < containerCrossSize ? containerCrossSize - resolvedCrossSize : 0f;
                }
                else if (string.Equals(alignSelf, "stretch", StringComparison.OrdinalIgnoreCase) && resolvedCrossSize <= 0f && containerCrossSize > 0f)
                {
                    resolvedCrossSize = containerCrossSize;
                    itemCrossPosition = 0f;
                }
                else if (!string.Equals(alignSelf, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = 0f;
                }

                var itemOffset = isReverseDirection
                    ? mainOffset + currentMainOffset
                    : lineMainStart + currentMainOffset;
                var childX = isRowDirection ? containingX + itemOffset : containingX + itemCrossPosition;
                var childY = isRowDirection ? containingY + itemCrossPosition + lineCrossPosition : containingY + itemOffset;
                var childWidth = isRowDirection ? finalMainSize : resolvedCrossSize;
                var childHeight = isRowDirection ? resolvedCrossSize : finalMainSize;

                if (item.Node is TextRenderNode textNode)
                {
                    LayoutTextNode(textNode.Ref, childX, containingWidth, ref childCursorY, ref childPreviousBlockMarginBottom, ref childSuppressNextBlockTopMargin, ref childActiveFloatLeftOffset, ref childActiveFloatBottom, ref childTextIndentConsumed, inheritedTextStyle, context, displayList, maxY);
                }
                else if (item.Node is ElementRenderNode elementChild)
                {
                    var childContainingWidth = Math.Max(0f, childWidth);
                    var childContainingHeight = Math.Max(0f, childHeight);
                    var childCursor = isRowDirection ? containingY + itemCrossPosition + lineCrossPosition : containingY + itemOffset;
                    var childBlockCursor = childCursor;
                    var childPreviousBottom = 0f;
                    var childSuppressMargin = false;
                    var childTextIndent = false;
                    var childActiveFloatLeft = 0f;
                    var childActiveFloatBottomOffset = 0f;

                    LayoutNode(
                        node: elementChild,
                        containingX: childX,
                        containingY: childY,
                        containingWidth: childContainingWidth,
                        cursorY: ref childBlockCursor,
                        previousBlockMarginBottom: ref childPreviousBottom,
                        suppressNextBlockTopMargin: ref childSuppressMargin,
                        activeFloatLeftOffset: ref childActiveFloatLeft,
                        activeFloatBottom: ref childActiveFloatBottomOffset,
                        textIndentConsumed: ref childTextIndent,
                        textStyle: inheritedTextStyle,
                        context: context,
                        displayList: displayList,
                        maxY: maxY,
                        isFlexItem: true,
                        isRowDirection: isRowDirection,
                        flexMainSize: finalMainSize,
                        flexCrossSize: resolvedCrossSize);
                }

                currentMainOffset += finalMainSize + lineMainSpacing;
            }

            currentCrossOffset += lineCrossSize + crossSpacing;
            totalLineMainSize = Math.Max(totalLineMainSize, lineMainSize);
        }

        var autoContentHeight = Math.Max(0f, (isRowDirection ? containerCrossSize : containerMainSize) - 0f);
        var specifiedContentHeight = ParseLength(styleMap, "height", containingWidth, float.NaN, allowAuto: true);
        contentHeight = float.IsNaN(specifiedContentHeight) ? Math.Max(autoContentHeight, totalLineMainSize) : Math.Max(specifiedContentHeight, autoContentHeight);
        var borderBoxWidth = borderLeft + paddingLeft + containingWidth + paddingRight + borderRight;
        var borderBoxHeight = borderTop + paddingTop + contentHeight + paddingBottom + borderBottom;
        var canCollapseWithLastChild = borderBottom <= 0f && paddingBottom <= 0f && float.IsNaN(specifiedContentHeight);
        var effectiveMarginBottom = ParseLength(styleMap, "margin-bottom", containingWidth, box.Margin.Bottom, allowAuto: false);

        if (canCollapseWithLastChild)
        {
            effectiveMarginBottom = CollapseMargins(effectiveMarginBottom, childPreviousBlockMarginBottom);
        }

        var boxPaintBuffer = new DisplayList();

        if (box.BackgroundPaint is RenderColorPaint colorPaint && colorPaint.Color.A == 0)
        {
            boxPaintBuffer.FillRect(new RenderRect(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight), RenderColor.Transparent);
        }
        else
        {
            PaintBackground(boxPaintBuffer, box.BackgroundPaint, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);
        }

        PaintBoxShadows(boxPaintBuffer, box.BoxShadows, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);

        RecordLayoutMetrics(
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom);

        PaintBorder(boxPaintBuffer, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth, box.BorderRadius);
        PaintOutline(boxPaintBuffer, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        var clipsOverflow = ShouldClipOverflow(styleMap);

        if (clipsOverflow)
        {
            var clipRect = new RenderRect(
                borderBoxX + borderLeft,
                borderBoxY + borderTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom));
            boxPaintBuffer.PushClip(clipRect, box.BorderRadius.ClampToBox(borderBoxWidth, borderBoxHeight));
        }

        displayList.InsertRange(boxPaintInsertIndex, boxPaintBuffer.Commands);

        if (TryResolveReplacedElementImage(node, styleMap, containingWidth, borderBoxX + borderLeft + paddingLeft, borderBoxY + borderTop + paddingTop, out var image, out var imageRect))
        {
            displayList.DrawImage(imageRect, image!);
        }

        if (clipsOverflow)
        {
            displayList.PopClip();
        }

        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + context.ParagraphSpacing;
        suppressNextBlockTopMargin = false;
    }

    private static float ResolveFlexibleContentDimension(
        Dictionary<string, string> styleMap,
        float relativeTo,
        float defaultValue,
        bool isFlexItem,
        bool isRowDirection,
        float? flexMainSize,
        float? flexCrossSize,
        string propertyName)
    {
        if (!isFlexItem)
        {
            return ParseLength(styleMap, propertyName, relativeTo, defaultValue, allowAuto: true);
        }

        if (string.Equals(propertyName, "width", StringComparison.OrdinalIgnoreCase))
        {
            return isRowDirection
                ? (flexMainSize.HasValue ? flexMainSize.Value : ParseLength(styleMap, propertyName, relativeTo, defaultValue, allowAuto: true))
                : (flexCrossSize.HasValue ? flexCrossSize.Value : ParseLength(styleMap, propertyName, relativeTo, defaultValue, allowAuto: true));
        }

        return isRowDirection
            ? (flexCrossSize.HasValue ? flexCrossSize.Value : ParseLength(styleMap, propertyName, relativeTo, defaultValue, allowAuto: true))
            : (flexMainSize.HasValue ? flexMainSize.Value : ParseLength(styleMap, propertyName, relativeTo, defaultValue, allowAuto: true));
    }

    /// <summary>
    /// Predicts an inline-block element's own border-box width/height and all four margins without
    /// actually laying it out, so its parent's inline-merging child loop can decide up front
    /// whether it fits on the current line and exactly where to place it. <c>LayoutNode</c>/
    /// <c>LayoutElement</c> have no "measure only" mode of their own - they only ever discover a
    /// box's final size as a side effect of actually laying out (and painting) it - so this exists
    /// purely to avoid needing one for the one case that can be predicted cheaply. It succeeds only
    /// when both <c>width</c> and <c>height</c> resolve to an explicit, non-auto value: guaranteed
    /// for every form control (<see cref="ApplyFormControlDefaults"/> always injects concrete pixel
    /// defaults for both) and for any inline-block given explicit <c>width</c>/<c>height</c> in
    /// CSS. It deliberately returns <see langword="false"/> for anything else - shrink-to-fit width
    /// or auto/content-driven height would need a genuine trial layout to measure, which this does
    /// not attempt (a documented scope cut, not an oversight: see the call site for what happens
    /// instead, which is not a regression - it is exactly this renderer's pre-existing behavior for
    /// every inline-block element, before shared-line flow existed for any of them).
    /// </summary>
    private static bool TryMeasureInlineBlockBoxSize(
        ElementRenderNode elementNode,
        float containingWidth,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        out float width,
        out float height,
        out float marginLeft,
        out float marginRight,
        out float marginTop,
        out float marginBottom)
    {
        width = 0f;
        height = 0f;
        marginLeft = 0f;
        marginRight = 0f;
        marginTop = 0f;
        marginBottom = 0f;

        var element = elementNode.Ref;
        var styleMap = CreateStyleMap(elementNode.ComputedStyle, element);
        var textStyle = ResolveTextStyle(styleMap, inheritedTextStyle);
        var formControlKind = ResolveFormControlKind(element);

        // A hidden input never lays out or paints at all (see LayoutElement's own early return for
        // it) - it has no box to flow inline, so it is neither flowable nor block-stackable here.
        if (formControlKind == FormControlKind.Hidden)
        {
            return false;
        }

        if (formControlKind != FormControlKind.None)
        {
            ApplyFormControlDefaults(formControlKind, element, styleMap, textStyle, context);
        }

        var specifiedContentWidth = ParseLength(styleMap, "width", containingWidth, float.NaN, allowAuto: true);
        var specifiedContentHeight = ParseLength(styleMap, "height", containingWidth, float.NaN, allowAuto: true);

        if (float.IsNaN(specifiedContentWidth) || float.IsNaN(specifiedContentHeight))
        {
            return false;
        }

        // Reuses the exact same box-model resolution LayoutElement itself calls for every other
        // element, rather than re-deriving border/padding/margin independently, so this prediction
        // can never quietly drift out of sync with what actually gets laid out.
        var box = ResolveBoxStyle(styleMap, element);
        width = box.BorderWidth.Left + box.Padding.Left + specifiedContentWidth + box.Padding.Right + box.BorderWidth.Right;
        height = box.BorderWidth.Top + box.Padding.Top + specifiedContentHeight + box.Padding.Bottom + box.BorderWidth.Bottom;

        // An auto horizontal margin resolves to NaN from ParseLength (mirroring width/height's own
        // auto signal) - correct for a block-level box's own centering math, but a non-block box's
        // auto margin simply computes to 0 per spec, since there is no "available space" to center
        // within on an inline-formatting-context line the way there is for text-align:center.
        marginLeft = float.IsNaN(box.Margin.Left) ? 0f : box.Margin.Left;
        marginRight = float.IsNaN(box.Margin.Right) ? 0f : box.Margin.Right;
        marginTop = box.Margin.Top;
        marginBottom = box.Margin.Bottom;

        return true;
    }

    private static IEnumerable<ElementRenderNode> CollectTableRows(ElementRenderNode tableNode)
    {
        foreach (var child in tableNode.Children)
        {
            if (child is ElementRenderNode childElement)
            {
                if (string.Equals(childElement.Ref.LocalName, "tr", StringComparison.OrdinalIgnoreCase))
                {
                    yield return childElement;
                }

                foreach (var descendant in CollectTableRows(childElement))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static void LayoutTable(
        ElementRenderNode tableNode,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        ref float activeFloatLeftOffset,
        ref float activeFloatBottom,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY)
    {
        var tableStyle = CreateStyleMap(tableNode.ComputedStyle);
        var specifiedWidth = ParseLength(tableStyle, "width", containingWidth, float.NaN, allowAuto: true);
        var availableWidth = float.IsNaN(specifiedWidth) ? containingWidth : specifiedWidth;
        var borderCollapse = string.Equals(tableStyle.TryGetValue("border-collapse", out var borderCollapseValue) ? borderCollapseValue : null, "collapse", StringComparison.OrdinalIgnoreCase);

        var rows = CollectTableRows(tableNode).ToList();
        var colgroup = tableNode.Children
            .Where(child => child is ElementRenderNode { Ref.LocalName: var localName } && string.Equals(localName, "colgroup", StringComparison.OrdinalIgnoreCase))
            .Cast<ElementRenderNode>()
            .FirstOrDefault();
        var columnSpecs = new List<(int ColumnIndex, float Width)>();

        if (colgroup is not null)
        {
            var columnNodes = colgroup.Children
                .Where(child => child is ElementRenderNode { Ref.LocalName: var localName } && string.Equals(localName, "col", StringComparison.OrdinalIgnoreCase))
                .Cast<ElementRenderNode>()
                .ToList();

            for (var index = 0; index < columnNodes.Count; index++)
            {
                var columnStyle = CreateStyleMap(columnNodes[index].ComputedStyle);
                var specifiedColumnWidth = ParseLength(columnStyle, "width", availableWidth, float.NaN, allowAuto: true);
                if (!float.IsNaN(specifiedColumnWidth))
                {
                    columnSpecs.Add((index, specifiedColumnWidth));
                }
            }
        }

        if (rows.Count == 0)
        {
            cursorY += inheritedTextStyle.FontSize * inheritedTextStyle.LineHeightMultiplier;
            return;
        }

        var rowCellLists = rows
            .Select(row => row.Children
                .Where(child => child is ElementRenderNode cellNode && (string.Equals(cellNode.Ref.LocalName, "td", StringComparison.OrdinalIgnoreCase) || string.Equals(cellNode.Ref.LocalName, "th", StringComparison.OrdinalIgnoreCase)))
                .Cast<ElementRenderNode>()
                .ToList())
            .ToList();

        var tableCells = new List<TableCellPlacement>();
        var rowSpanOccupancy = new List<int>();
        var columnCount = 0;

        for (var rowIndex = 0; rowIndex < rowCellLists.Count; rowIndex++)
        {
            var cells = rowCellLists[rowIndex];
            var currentRowOccupied = new List<int>();
            var nextRowSpanOccupancy = new List<int>();
            var currentColumnIndex = 0;

            foreach (var cellNode in cells)
            {
                while (true)
                {
                    while (currentColumnIndex >= currentRowOccupied.Count)
                    {
                        currentRowOccupied.Add(0);
                    }

                    while (currentColumnIndex >= rowSpanOccupancy.Count)
                    {
                        rowSpanOccupancy.Add(0);
                    }

                    if (rowSpanOccupancy[currentColumnIndex] == 0 && currentRowOccupied[currentColumnIndex] == 0)
                    {
                        break;
                    }

                    currentColumnIndex++;
                }

                var cellStyle = CreateStyleMap(cellNode.ComputedStyle);
                var cellTextStyle = ResolveTextStyle(cellStyle, inheritedTextStyle);
                var text = NormalizeWhitespace(cellNode.Ref.TextContent ?? string.Empty);
                var colspan = 1;
                var rowspan = 1;

                if (int.TryParse(cellNode.Ref.GetAttribute("colspan"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedColspan) && parsedColspan > 0)
                {
                    colspan = parsedColspan;
                }

                if (int.TryParse(cellNode.Ref.GetAttribute("rowspan"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRowspan) && parsedRowspan > 0)
                {
                    rowspan = parsedRowspan;
                }

                for (var spanOffset = 0; spanOffset < colspan; spanOffset++)
                {
                    while (currentColumnIndex + spanOffset >= currentRowOccupied.Count)
                    {
                        currentRowOccupied.Add(0);
                    }

                    currentRowOccupied[currentColumnIndex + spanOffset] = 1;
                }

                for (var spanOffset = 0; spanOffset < colspan; spanOffset++)
                {
                    while (currentColumnIndex + spanOffset >= nextRowSpanOccupancy.Count)
                    {
                        nextRowSpanOccupancy.Add(0);
                    }

                    nextRowSpanOccupancy[currentColumnIndex + spanOffset] = Math.Max(nextRowSpanOccupancy[currentColumnIndex + spanOffset], Math.Max(0, rowspan - 1));
                }

                tableCells.Add(new TableCellPlacement(rowIndex, currentColumnIndex, colspan, rowspan, cellNode, cellStyle, cellTextStyle, text,
                    ParseLength(cellStyle, "padding-left", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "padding-right", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "padding-top", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "padding-bottom", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "border-left-width", containingWidth, 1f, allowAuto: false),
                    ParseLength(cellStyle, "border-right-width", containingWidth, 1f, allowAuto: false),
                    ParseLength(cellStyle, "border-top-width", containingWidth, 1f, allowAuto: false),
                    ParseLength(cellStyle, "border-bottom-width", containingWidth, 1f, allowAuto: false),
                    ParseColor(cellStyle.TryGetValue("background-color", out var backgroundColor) ? backgroundColor : null, RenderColor.Transparent),
                    ParseCellVerticalAlign(cellStyle)));

                columnCount = Math.Max(columnCount, currentColumnIndex + colspan);
                currentColumnIndex += colspan;
            }

            foreach (var index in Enumerable.Range(0, rowSpanOccupancy.Count))
            {
                if (rowSpanOccupancy[index] > 0)
                {
                    // A row with fewer cells than the span reaches over leaves the new list short.
                    while (index >= nextRowSpanOccupancy.Count)
                    {
                        nextRowSpanOccupancy.Add(0);
                    }

                    nextRowSpanOccupancy[index] = Math.Max(nextRowSpanOccupancy[index], rowSpanOccupancy[index] - 1);
                }
            }

            rowSpanOccupancy = nextRowSpanOccupancy;
        }

        if (columnCount <= 0)
        {
            cursorY += inheritedTextStyle.FontSize * inheritedTextStyle.LineHeightMultiplier;
            return;
        }

        var columnMinWidths = new float[columnCount];
        foreach (var placement in tableCells)
        {
            var specifiedCellWidth = ParseLength(placement.CellStyle, "width", availableWidth, float.NaN, allowAuto: true);
            var paddingLeft = placement.PaddingLeft;
            var paddingRight = placement.PaddingRight;
            var borderLeftWidth = placement.BorderLeftWidth;
            var borderRightWidth = placement.BorderRightWidth;
            var textWidth = placement.Text.Length > 0 ? MeasureTextWidth(context, placement.Text, placement.CellTextStyle) : 0f;
            var minCellWidth = textWidth + paddingLeft + paddingRight + borderLeftWidth + borderRightWidth + 8f;
            var widthPerColumn = float.IsNaN(specifiedCellWidth) ? minCellWidth / Math.Max(1, placement.ColumnSpan) : specifiedCellWidth / Math.Max(1, placement.ColumnSpan);

            for (var spanOffset = 0; spanOffset < placement.ColumnSpan; spanOffset++)
            {
                var columnIndex = placement.ColumnIndex + spanOffset;
                columnMinWidths[columnIndex] = Math.Max(columnMinWidths[columnIndex], widthPerColumn);
            }
        }

        foreach (var (columnIndex, columnWidth) in columnSpecs)
        {
            if (columnIndex < columnMinWidths.Length)
            {
                columnMinWidths[columnIndex] = Math.Max(columnMinWidths[columnIndex], columnWidth);
            }
        }

        var totalMinWidth = columnMinWidths.Sum();
        var tableWidth = Math.Max(availableWidth, totalMinWidth);
        var columnWidths = new float[columnCount];

        if (availableWidth > totalMinWidth)
        {
            var extraWidth = availableWidth - totalMinWidth;
            var extraPerColumn = extraWidth / Math.Max(1, columnCount);
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columnWidths[columnIndex] = columnMinWidths[columnIndex] + extraPerColumn;
            }
        }
        else if (availableWidth > 0f && totalMinWidth > availableWidth)
        {
            var scale = availableWidth / totalMinWidth;
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                columnWidths[columnIndex] = columnMinWidths[columnIndex] * scale;
            }
        }
        else
        {
            columnWidths = columnMinWidths.ToArray();
        }

        var tableX = containingX;
        var tableY = cursorY;
        var rowTopOffsets = new float[rowCellLists.Count];
        var rowHeights = new float[rowCellLists.Count];

        // Cells confined to a single row establish the row heights on their own.
        foreach (var placement in tableCells.Where(placement => placement.RowSpan <= 1))
        {
            rowHeights[placement.RowIndex] = Math.Max(rowHeights[placement.RowIndex], MeasureCellHeight(context, placement, columnWidths));
        }

        for (var rowIndex = 0; rowIndex < rowHeights.Length; rowIndex++)
        {
            rowHeights[rowIndex] = Math.Max(rowHeights[rowIndex], 20f);
        }

        // A spanning cell only has to fit across the rows it covers taken together, so it grows
        // them by whatever is still missing rather than imposing its full height on each one.
        foreach (var placement in tableCells.Where(placement => placement.RowSpan > 1))
        {
            var spannedRows = Enumerable.Range(placement.RowIndex, placement.RowSpan).ToArray();
            var available = spannedRows.Sum(rowIndex => rowHeights[rowIndex]);
            var required = MeasureCellHeight(context, placement, columnWidths);

            if (required > available)
            {
                var deficitPerRow = (required - available) / placement.RowSpan;

                foreach (var rowIndex in spannedRows)
                {
                    rowHeights[rowIndex] += deficitPerRow;
                }
            }
        }

        var currentRowTop = 0f;
        for (var rowIndex = 0; rowIndex < rowCellLists.Count; rowIndex++)
        {
            rowTopOffsets[rowIndex] = currentRowTop;
            currentRowTop += rowHeights[rowIndex];
        }

        var columnLefts = new float[columnCount];
        var currentColumnLeft = 0f;
        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            columnLefts[columnIndex] = currentColumnLeft;
            currentColumnLeft += columnWidths[columnIndex];
        }

        var tableHeight = currentRowTop;
        foreach (var placement in tableCells)
        {
            var contentWidth = Math.Max(0f, columnWidths.Skip(placement.ColumnIndex).Take(placement.ColumnSpan).Sum() - placement.PaddingLeft - placement.PaddingRight - placement.BorderLeftWidth - placement.BorderRightWidth);
            var cellX = tableX + columnLefts[placement.ColumnIndex];
            var cellY = tableY + rowTopOffsets[placement.RowIndex];
            var cellWidth = columnWidths.Skip(placement.ColumnIndex).Take(placement.ColumnSpan).Sum();
            var cellHeight = 0f;
            for (var rowIndex = placement.RowIndex; rowIndex < placement.RowIndex + placement.RowSpan; rowIndex++)
            {
                cellHeight += rowHeights[rowIndex];
            }

            RecordLayoutMetrics(
                placement.CellNode.Ref,
                cellX,
                cellY,
                cellWidth,
                cellHeight,
                placement.BorderLeftWidth,
                placement.BorderRightWidth,
                placement.BorderTopWidth,
                placement.BorderBottomWidth,
                placement.PaddingLeft,
                placement.PaddingRight,
                placement.PaddingTop,
                placement.PaddingBottom);

            displayList.FillRect(new RenderRect(cellX, cellY, cellWidth, cellHeight), placement.BackgroundColor);

            if (borderCollapse)
            {
                // Collapsed borders are shared, so each cell contributes only its top and left
                // edge and the table frame closes the far sides. Drawing full outlines instead
                // would lay two lines over every shared edge, and a grid spanning the whole table
                // would cut straight through the cells that span rows or columns.
                displayList.FillRect(new RenderRect(cellX, cellY, cellWidth, CollapsedBorderWidth), RenderColor.Black);
                displayList.FillRect(new RenderRect(cellX, cellY, CollapsedBorderWidth, cellHeight), RenderColor.Black);
            }
            else
            {
                displayList.FillRect(new RenderRect(cellX, cellY, cellWidth, placement.BorderTopWidth), RenderColor.Black);
                displayList.FillRect(new RenderRect(cellX + cellWidth - placement.BorderRightWidth, cellY, placement.BorderRightWidth, cellHeight), RenderColor.Black);
                displayList.FillRect(new RenderRect(cellX, cellY + cellHeight - placement.BorderBottomWidth, cellWidth, placement.BorderBottomWidth), RenderColor.Black);
                displayList.FillRect(new RenderRect(cellX, cellY, placement.BorderLeftWidth, cellHeight), RenderColor.Black);
            }

            if (contentWidth > 0f && placement.Text.Length > 0)
            {
                var wrappedLines = WrapText(context, placement.Text, contentWidth, placement.CellTextStyle);
                var lineHeight = placement.CellTextStyle.FontSize * placement.CellTextStyle.LineHeightMultiplier;
                var lineX = cellX + placement.PaddingLeft + placement.BorderLeftWidth;

                // The content box can be taller than the text, most visibly in a cell that spans
                // rows, so the block of lines is placed according to the cell's vertical-align.
                var contentBoxHeight = cellHeight - placement.PaddingTop - placement.PaddingBottom
                    - placement.BorderTopWidth - placement.BorderBottomWidth;
                var slack = Math.Max(0f, contentBoxHeight - (wrappedLines.Count * lineHeight));
                var verticalOffset = placement.VerticalAlign switch
                {
                    CellVerticalAlign.Middle => slack / 2f,
                    CellVerticalAlign.Bottom => slack,
                    _ => 0f,
                };

                var lineY = cellY + placement.PaddingTop + placement.BorderTopWidth + verticalOffset + lineHeight;

                for (var lineIndex = 0; lineIndex < wrappedLines.Count; lineIndex++)
                {
                    var line = wrappedLines[lineIndex];
                    var cellLineY = lineY + (lineIndex * lineHeight);
                    PaintTextShadows(displayList, placement.CellTextStyle.TextShadows, line, lineX, cellLineY, placement.CellTextStyle);
                    displayList.DrawText(line, lineX, cellLineY, placement.CellTextStyle.Color, placement.CellTextStyle.FontSize, placement.CellTextStyle.FontFamily, placement.CellTextStyle.FontWeight, placement.CellTextStyle.IsItalic, placement.CellTextStyle.Underline, placement.CellTextStyle.StrikeThrough, placement.CellTextStyle.DecorationColor, placement.CellTextStyle.DecorationStyle, placement.CellTextStyle.LetterSpacing);
                }
            }
        }

        if (borderCollapse)
        {
            // The interior lines come from the cell outlines above; only the frame is left, which
            // also closes the edge of rows that hold fewer cells than the table has columns.
            displayList.FillRect(new RenderRect(tableX, tableY, tableWidth, CollapsedBorderWidth), RenderColor.Black);
            displayList.FillRect(new RenderRect(tableX, tableY + tableHeight - CollapsedBorderWidth, tableWidth, CollapsedBorderWidth), RenderColor.Black);
            displayList.FillRect(new RenderRect(tableX, tableY, CollapsedBorderWidth, tableHeight), RenderColor.Black);
            displayList.FillRect(new RenderRect(tableX + tableWidth - CollapsedBorderWidth, tableY, CollapsedBorderWidth, tableHeight), RenderColor.Black);
        }

        RecordLayoutMetrics(
            tableNode.Ref,
            tableX,
            tableY,
            tableWidth,
            tableHeight,
            borderLeft: 0f,
            borderRight: 0f,
            borderTop: 0f,
            borderBottom: 0f,
            paddingLeft: 0f,
            paddingRight: 0f,
            paddingTop: 0f,
            paddingBottom: 0f);

        displayList.FillRect(new RenderRect(tableX, tableY, tableWidth, tableHeight), RenderColor.Transparent);
        cursorY = tableY + tableHeight + 4f;
        previousBlockMarginBottom = 0f;
        suppressNextBlockTopMargin = false;
    }

    private static void LayoutGridContainer(
        ElementRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        ref float activeFloatLeftOffset,
        ref float activeFloatBottom,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        Dictionary<string, string> styleMap,
        float borderLeft,
        float borderTop,
        float borderRight,
        float borderBottom,
        float paddingLeft,
        float paddingRight,
        float paddingTop,
        float paddingBottom,
        BoxStyle box,
        float flowBorderBoxX,
        float flowBorderBoxY,
        float borderBoxX,
        float borderBoxY)
    {
        // See the matching comment in LayoutElement: the container's own background/border must
        // paint behind its items, but its auto-sized height is only known after they are laid out
        // (and appended), so their paint commands are spliced in before this index instead.
        var boxPaintInsertIndex = displayList.Commands.Count;

        var explicitGridTemplateColumns = ResolveExplicitPropertyValue(node.Ref, node.ComputedStyle, "grid-template-columns");
        var columns = ParseGridTrackListStructured(explicitGridTemplateColumns, containingWidth);

        if (columns.Count == 0)
        {
            columns.Add(GridTrackSize.Fixed(containingWidth));
        }

        var columnGap = ParseGridGap(styleMap, "column-gap", containingWidth, 0)
            ?? ParseGridGap(styleMap, "gap", containingWidth, 0);
        var rowGap = ParseGridGap(styleMap, "row-gap", containingWidth, 0)
            ?? ParseGridGap(styleMap, "gap", containingWidth, 0);
        var resolvedColumnGap = columnGap ?? 0f;
        var resolvedRowGap = rowGap ?? 0f;
        var gridItems = node.Children
            .Where(child => child is ElementRenderNode || (child is TextRenderNode textNode && NormalizeWhitespace(textNode.Ref.Data).Length > 0))
            .ToList();
        var containerHeight = ParseLength(styleMap, "height", containingWidth, containingWidth, allowAuto: true);
        var explicitGridTemplateRows = ResolveExplicitPropertyValue(node.Ref, node.ComputedStyle, "grid-template-rows");
        var rows = ParseGridTrackListStructured(explicitGridTemplateRows, containerHeight);
        var hasExplicitRowTracks = rows.Count > 0;

        if (!hasExplicitRowTracks)
        {
            // Implicit rows default to content-sized (Auto), the same way a real browser sizes
            // them - grown below from each item's own estimated height, exactly like an Auto
            // column. The row count itself is still only a starting guess (one row per
            // ceil(itemCount / columnCount)); ResolveGridItemPlacement grows this list further for
            // any item that lands past it (an explicit grid-row/an oversized span).
            var rowCount = Math.Max(1, (int)Math.Ceiling((double)gridItems.Count / Math.Max(1, columns.Count)));

            for (var i = 0; i < rowCount; i++)
            {
                rows.Add(GridTrackSize.Auto());
            }
        }

        // Pass 1: resolve every item's placement once (reused unchanged in pass 2 below, so a
        // wrapping auto-placement cursor can never land an item in a different cell the second
        // time around) and grow every Auto column/row to fit its own items' estimated size -
        // mirroring this renderer's existing, pre-structured-parsing item-estimate approximation
        // for content sizing (ResolveGridItemEstimatedSize), just now applied through the new
        // GridTrackSize model instead of a flat pixel list.
        var itemPlacements = new List<(ElementRenderNode Element, GridPlacement Column, GridPlacement Row)>();
        var currentColumn = 0;
        var currentRow = 0;

        foreach (var child in gridItems)
        {
            if (child is not ElementRenderNode elementChild)
            {
                continue;
            }

            var childStyleMap = CreateStyleMap(elementChild.ComputedStyle, elementChild.Ref);
            var placementColumn = ResolveGridPlacementFromMap(childStyleMap, "grid-column", currentColumn);
            var placementRow = ResolveGridPlacementFromMap(childStyleMap, "grid-row", currentRow);
            var hasExplicitPlacement = childStyleMap.ContainsKey("grid-column") || childStyleMap.ContainsKey("grid-row");

            var effectivePlacementColumn = hasExplicitPlacement
                ? new GridPlacement(Math.Max(0, placementColumn.LineIndex), placementColumn.Span)
                : new GridPlacement(Math.Max(0, currentColumn), placementColumn.Span);
            var effectivePlacementRow = hasExplicitPlacement
                ? new GridPlacement(Math.Max(0, placementRow.LineIndex), placementRow.Span)
                : new GridPlacement(Math.Max(0, currentRow), placementRow.Span);

            itemPlacements.Add((elementChild, effectivePlacementColumn, effectivePlacementRow));

            // The item's *own* width/height (childStyleMap), not the container's - a real,
            // confirmed bug in the pre-existing estimate (it read the container's styleMap here,
            // so every item's estimate was really just re-reading the container's own width/height
            // regardless of what any individual item was actually styled with).
            var estimatedItemWidth = ResolveGridItemEstimatedSize(elementChild, childStyleMap, containingWidth, "width");
            var estimatedItemHeight = ResolveGridItemEstimatedSize(elementChild, childStyleMap, containingWidth, "height");
            var effectiveColumnCount = Math.Max(columns.Count, effectivePlacementColumn.LineIndex + effectivePlacementColumn.Span);
            var effectiveRowCount = Math.Max(rows.Count, effectivePlacementRow.LineIndex + effectivePlacementRow.Span);

            while (columns.Count < effectiveColumnCount)
            {
                columns.Add(GridTrackSize.Auto());
            }

            while (rows.Count < effectiveRowCount)
            {
                rows.Add(GridTrackSize.Auto());
            }

            GrowGridTrackSize(columns, effectivePlacementColumn.LineIndex, estimatedItemWidth);
            GrowGridTrackSize(rows, effectivePlacementRow.LineIndex, estimatedItemHeight);

            currentColumn++;
            if (currentColumn >= columns.Count)
            {
                currentColumn = 0;
                currentRow++;
            }
        }

        // Between passes: distribute each axis's remaining free space across its own `fr` tracks.
        // Row `fr` tracks only grow when the container has a definite height to distribute - `fr`
        // rows have no real meaning against an otherwise auto-sized container (there is no "free
        // space" to speak of), a deliberate, documented scope cut rather than an attempt at the
        // spec's own intrinsic-sizing fallback for that case.
        ResolveGridFractionTracks(columns, resolvedColumnGap, containingWidth);

        if (hasExplicitRowTracks && !float.IsNaN(containerHeight))
        {
            ResolveGridFractionTracks(rows, resolvedRowGap, containerHeight);
        }

        var columnSizes = columns.Select(t => t.Pixels).ToList();
        var rowSizes = rows.Select(t => t.Pixels).ToList();

        // Pass 2: lay out every item for real, against the now-fully-resolved track sizes.
        foreach (var child in gridItems)
        {
            if (child is TextRenderNode textNode)
            {
                LayoutTextNode(textNode.Ref, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, inheritedTextStyle, context, displayList, maxY);
                continue;
            }

            if (child is not ElementRenderNode elementChild)
            {
                continue;
            }

            var (_, effectivePlacementColumn, effectivePlacementRow) = itemPlacements.First(p => ReferenceEquals(p.Element, elementChild));

            var contentX = borderBoxX + borderLeft + paddingLeft;
            var contentY = borderBoxY + borderTop + paddingTop;
            var cellX = contentX + GetGridTrackOffset(columnSizes, effectivePlacementColumn.LineIndex, resolvedColumnGap);
            var cellY = contentY + GetGridTrackOffset(rowSizes, effectivePlacementRow.LineIndex, resolvedRowGap);
            var cellWidth = GetGridTrackSpanSize(columnSizes, effectivePlacementColumn.LineIndex, effectivePlacementColumn.Span, resolvedColumnGap, containingWidth);
            var cellHeight = GetGridTrackSpanSize(rowSizes, effectivePlacementRow.LineIndex, effectivePlacementRow.Span, resolvedRowGap, containingWidth);

            var childCursor = cellY;
            var childPreviousBlockMarginBottom = 0f;
            var childSuppressNextBlockTopMargin = false;
            var childActiveFloatLeftOffset = 0f;
            var childActiveFloatBottom = 0f;
            var childTextIndentConsumed = false;

            LayoutNode(
                node: elementChild,
                containingX: cellX,
                containingY: cellY,
                containingWidth: Math.Max(0f, cellWidth),
                cursorY: ref childCursor,
                previousBlockMarginBottom: ref childPreviousBlockMarginBottom,
                suppressNextBlockTopMargin: ref childSuppressNextBlockTopMargin,
                activeFloatLeftOffset: ref childActiveFloatLeftOffset,
                activeFloatBottom: ref childActiveFloatBottom,
                textIndentConsumed: ref childTextIndentConsumed,
                textStyle: inheritedTextStyle,
                context: context,
                displayList: displayList,
                maxY: maxY,
                isFlexItem: false,
                isRowDirection: true,
                flexMainSize: null,
                flexCrossSize: null);
        }

        var gridContentWidth = GetGridContentSize(columnSizes, resolvedColumnGap, containingWidth);
        var specifiedHeight = ParseLength(styleMap, "height", containingWidth, containingWidth, allowAuto: true);
        var gridContentHeight = GetGridContentSize(rowSizes, resolvedRowGap, specifiedHeight);
        var borderBoxWidth = borderLeft + paddingLeft + Math.Max(containingWidth, gridContentWidth) + paddingRight + borderRight;
        var borderBoxHeight = borderTop + paddingTop + Math.Max(ParseLength(styleMap, "height", containingWidth, containingWidth, allowAuto: true), gridContentHeight) + paddingBottom + borderBottom;
        var canCollapseWithLastChild = borderBottom <= 0f && paddingBottom <= 0f;
        var effectiveMarginBottom = ParseLength(styleMap, "margin-bottom", containingWidth, box.Margin.Bottom, allowAuto: false);

        if (canCollapseWithLastChild)
        {
            effectiveMarginBottom = CollapseMargins(effectiveMarginBottom, previousBlockMarginBottom);
        }

        var boxPaintBuffer = new DisplayList();

        if (box.BackgroundPaint is RenderColorPaint colorPaint && colorPaint.Color.A == 0)
        {
            boxPaintBuffer.FillRect(new RenderRect(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight), RenderColor.Transparent);
        }
        else
        {
            PaintBackground(boxPaintBuffer, box.BackgroundPaint, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);
        }

        PaintBoxShadows(boxPaintBuffer, box.BoxShadows, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);

        RecordLayoutMetrics(
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom);

        PaintBorder(boxPaintBuffer, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth, box.BorderRadius);
        PaintOutline(boxPaintBuffer, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        var clipsOverflow = ShouldClipOverflow(styleMap);

        if (clipsOverflow)
        {
            var clipRect = new RenderRect(
                borderBoxX + borderLeft,
                borderBoxY + borderTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom));
            boxPaintBuffer.PushClip(clipRect, box.BorderRadius.ClampToBox(borderBoxWidth, borderBoxHeight));
        }

        displayList.InsertRange(boxPaintInsertIndex, boxPaintBuffer.Commands);

        if (clipsOverflow)
        {
            displayList.PopClip();
        }

        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + context.ParagraphSpacing;
        suppressNextBlockTopMargin = false;
    }

    private static float GetGridTrackOffset(IReadOnlyList<float> tracks, int index, float gap)
    {
        if (index <= 0)
        {
            return 0f;
        }

        var offset = 0f;
        for (var current = 0; current < index && current < tracks.Count; current++)
        {
            offset += tracks[current];
            offset += gap;
        }

        return offset;
    }

    private static float GetGridTrackSpanSize(IReadOnlyList<float> tracks, int index, int span, float gap, float fallback)
    {
        var totalSize = 0f;
        var spanCount = Math.Max(1, span);

        for (var current = 0; current < spanCount; current++)
        {
            var trackIndex = index + current;
            totalSize += GetGridTrackSize(tracks, trackIndex, fallback);

            if (current < spanCount - 1)
            {
                totalSize += gap;
            }
        }

        return totalSize;
    }

    /// <summary>
    /// Parses `grid-template-columns`/`grid-template-rows` into a track-sizing-function list,
    /// delegating the outer function/list grammar to AngleSharp.Css's own `GridParser.ParseTrackList`
    /// (mirroring the `transform`/`filter`/gradient precedent: AngleSharp.Css computes the pure-CSS
    /// structure - `repeat()`, `minmax()`, `fit-content()`, the `fr` unit - this renderer still owns
    /// turning that into its own backend-agnostic <see cref="GridTrackSize"/> list and, later, the
    /// actual pixel sizes). Returns an empty list when unset or `none`, matching CSS's own initial
    /// value - the caller falls back to a single implicit track, the same default a real browser
    /// gives an unstyled grid container.
    /// </summary>
    private static List<GridTrackSize> ParseGridTrackListStructured(string rawValue, float relativeTo)
    {
        var tracks = new List<GridTrackSize>();

        if (string.IsNullOrWhiteSpace(rawValue) || string.Equals(rawValue.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return tracks;
        }

        var source = new StringSource(rawValue.Trim());
        var parsed = source.ParseTrackList();

        if (parsed is not null)
        {
            AppendGridTracks(parsed, relativeTo, tracks);
        }

        return tracks;
    }

    private static void AppendGridTracks(ICssValue value, float relativeTo, List<GridTrackSize> tracks)
    {
        switch (value)
        {
            case CssLineNamesValue:
                // Named grid lines (`[name]`) are not represented in this renderer's line-index
                // placement model (see ResolveGridPlacement, which only understands numeric lines
                // and spans) - a deliberate, documented scope cut, not a crash or a dropped track.
                break;

            // CssRepeatValue/CssFitContentValue are both `internal` in AngleSharp.Css (unlike
            // CssMinMaxValue, which is public) - matched via the public ICssFunctionValue interface
            // (Name/Arguments) they both implement instead of the concrete type.
            case ICssFunctionValue repeatFunc when string.Equals(repeatFunc.Name, "repeat", StringComparison.OrdinalIgnoreCase) && repeatFunc.Arguments.Length == 2:
                var count = ResolveGridRepeatCount(repeatFunc.Arguments[0]);

                for (var i = 0; i < count; i++)
                {
                    AppendGridTracks(repeatFunc.Arguments[1], relativeTo, tracks);
                }

                break;

            case CssTupleValue<ICssValue> tuple:
                foreach (var item in tuple.Items)
                {
                    if (item is not null)
                    {
                        AppendGridTracks(item, relativeTo, tracks);
                    }
                }

                break;

            default:
                tracks.Add(ConvertGridTrackSize(value, relativeTo));
                break;
        }
    }

    /// <summary>
    /// `repeat(auto-fill, ...)`/`repeat(auto-fit, ...)` need the container's own available space to
    /// compute how many repetitions fit - a genuinely different, container-size-dependent algorithm
    /// this renderer does not implement. Falls back to a single repetition (the count `1` never
    /// causes a dropped track or a NaN/absurd count), a deliberate, documented scope cut.
    /// </summary>
    private static int ResolveGridRepeatCount(ICssValue countValue)
    {
        var text = countValue.CssText;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return Math.Clamp(count, 1, 1000);
        }

        return 1;
    }

    private static GridTrackSize ConvertGridTrackSize(ICssValue value, float relativeTo)
    {
        switch (value)
        {
            case CssFractionValue fraction:
                return GridTrackSize.Fraction((float)fraction.Value);

            case CssMinMaxValue minMax:
                return ConvertGridMinMax(minMax, relativeTo);

            case ICssFunctionValue fitContentFunc when string.Equals(fitContentFunc.Name, "fit-content", StringComparison.OrdinalIgnoreCase) && fitContentFunc.Arguments.Length == 1:
                return GridTrackSize.Auto(max: ResolveGridTrackLength(fitContentFunc.Arguments[0], relativeTo));

            case CssLengthValue:
                return GridTrackSize.Fixed(ResolveGridTrackLength(value, relativeTo));

            default:
                // "auto"/"min-content"/"max-content" and anything else unrecognized - all treated
                // as content-sized, the same approximation this renderer already used for every
                // plain "auto" track before structured parsing.
                return GridTrackSize.Auto();
        }
    }

    /// <summary>
    /// `minmax(min, max)` where `max` is a `&lt;flex&gt;` (`fr`) is the common, important case
    /// (`minmax(100px, 1fr)` - "at least 100px, then grow to fill") and gets real support: the
    /// track participates in free-space distribution like any other `fr` track, but never shrinks
    /// below `min`. Any other combination (`minmax(100px, 300px)`, `minmax(min-content, 1fr)`, ...)
    /// is approximated as a content-sized track clamped to whichever bounds were themselves plain
    /// lengths/percentages - not the spec's own iterative clamping algorithm, but consistent with
    /// this renderer's existing item-estimate-based approximation for content sizing in general.
    /// </summary>
    private static GridTrackSize ConvertGridMinMax(CssMinMaxValue minMax, float relativeTo)
    {
        if (minMax.Maximum is CssFractionValue maxFraction)
        {
            var floorPixels = minMax.Minimum is CssLengthValue ? ResolveGridTrackLength(minMax.Minimum, relativeTo) : (float?)null;
            return GridTrackSize.Fraction((float)maxFraction.Value, floorPixels);
        }

        var min = minMax.Minimum is CssLengthValue ? ResolveGridTrackLength(minMax.Minimum, relativeTo) : (float?)null;
        var max = minMax.Maximum is CssLengthValue ? ResolveGridTrackLength(minMax.Maximum, relativeTo) : (float?)null;
        return GridTrackSize.Auto(min, max);
    }

    /// <summary>
    /// Resolves one already-structured track-size sub-value's own length/percentage into pixels,
    /// reading its `.CssText` (e.g. "50%", "20px") the same "re-parse the structured value's own
    /// serialized text with this renderer's existing semantic parsers" pattern already established
    /// for `filter`/gradients - <see cref="ParseLengthValue"/> itself has no percentage handling
    /// (every other caller resolves percentages against a styleMap-driven containing dimension it
    /// doesn't have here), so this adds that one case directly rather than reusing it verbatim.
    /// </summary>
    private static float ResolveGridTrackLength(ICssValue value, float relativeTo)
    {
        var text = value.CssText.Trim();

        if (text.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return Math.Max(0f, relativeTo * percent / 100f);
        }

        var pixels = ParseLengthValue(text, float.NaN, allowAuto: false);
        return float.IsNaN(pixels) ? 0f : Math.Max(0f, pixels);
    }

    private static float? ParseGridGap(Dictionary<string, string> styleMap, string propertyName, float relativeTo, int tokenIndex)
    {
        if (!styleMap.TryGetValue(propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var tokens = rawValue.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var token = tokenIndex >= 0 && tokenIndex < tokens.Length ? tokens[tokenIndex] : tokens[^1];
        var parsed = ParseLengthValue(token, float.NaN, allowAuto: false);
        return float.IsNaN(parsed) ? null : parsed;
    }

    private static GridPlacement ResolveGridPlacementFromMap(Dictionary<string, string> childStyleMap, string propertyName, int fallbackIndex)
    {
        if (!childStyleMap.TryGetValue(propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return new GridPlacement(fallbackIndex, 1);
        }

        var tokens = rawValue.Split(new[] { ' ', '/', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var startToken = tokens.FirstOrDefault(token => int.TryParse(token, out _));

        if (startToken is not null && int.TryParse(startToken, out var explicitIndex))
        {
            return new GridPlacement(Math.Max(0, explicitIndex - 1), 1);
        }

        if (tokens.Length >= 3 && string.Equals(tokens[1], "span", StringComparison.OrdinalIgnoreCase) && int.TryParse(tokens[2], out var spanCount))
        {
            return new GridPlacement(Math.Max(0, fallbackIndex), Math.Max(1, spanCount));
        }

        return new GridPlacement(Math.Max(0, fallbackIndex), 1);
    }

    private static float ResolveGridItemEstimatedSize(ElementRenderNode elementChild, Dictionary<string, string> styleMap, float fallbackSize, string propertyName)
    {
        var rawValue = styleMap.TryGetValue(propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return fallbackSize;
        }

        var parsed = ParseLengthValue(rawValue, fallbackSize, allowAuto: false);
        return float.IsNaN(parsed) ? fallbackSize : Math.Max(0f, parsed);
    }

    /// <summary>
    /// Grows an <see cref="GridTrackSizeKind.Auto"/> track to fit an item's own estimated size,
    /// the same "grow, never shrink" approximation for content-based sizing this renderer already
    /// used before structured `grid-template-columns`/`rows` parsing - now scoped to `Auto` tracks
    /// specifically (a `Fixed` track already has its final size; a `Fraction` track's only comes
    /// from <see cref="ResolveGridFractionTracks"/>, once every `Auto`/`Fixed` track is settled).
    /// </summary>
    private static void GrowGridTrackSize(List<GridTrackSize> tracks, int index, float itemEstimate)
    {
        while (tracks.Count <= index)
        {
            tracks.Add(GridTrackSize.Auto());
        }

        var track = tracks[index];

        if (track.Kind != GridTrackSizeKind.Auto)
        {
            return;
        }

        var candidate = Math.Max(track.Pixels, itemEstimate);

        if (track.MaxPixels is { } max)
        {
            candidate = Math.Min(candidate, max);
        }

        if (track.MinPixels is { } min)
        {
            candidate = Math.Max(candidate, min);
        }

        track.Pixels = candidate;
    }

    /// <summary>
    /// Distributes one axis's remaining free space across its own `fr` tracks, proportional to
    /// each one's own `fr` count - the CSS Grid spec's own "distribute free space by flex factor"
    /// step, simplified (not the full spec algorithm's iterative handling of a `minmax()` track
    /// whose floor alone already exceeds its fair share - a rare, defensible approximation gap).
    /// A `minmax(floor, 1fr)` track's floor is reserved as already-used space before the remaining
    /// free space is computed, then added back on top of that track's own distributed share.
    /// </summary>
    private static void ResolveGridFractionTracks(List<GridTrackSize> tracks, float gap, float availableSpace)
    {
        var totalFraction = tracks.Where(t => t.Kind == GridTrackSizeKind.Fraction).Sum(t => t.FractionValue);

        if (totalFraction <= 0f)
        {
            return;
        }

        var usedSpace = tracks.Sum(t => t.Kind == GridTrackSizeKind.Fraction ? (t.MinPixels ?? 0f) : t.Pixels);
        var gapSpace = Math.Max(0, tracks.Count - 1) * gap;
        var freeSpace = Math.Max(0f, availableSpace - usedSpace - gapSpace);

        foreach (var track in tracks)
        {
            if (track.Kind == GridTrackSizeKind.Fraction)
            {
                track.Pixels = (track.MinPixels ?? 0f) + (freeSpace * (track.FractionValue / totalFraction));
            }
        }
    }

    private static float GetGridTrackSize(IReadOnlyList<float> tracks, int index, float fallback)
    {
        if (index >= 0 && index < tracks.Count)
        {
            return tracks[index];
        }

        return fallback;
    }

    private static float GetGridContentSize(IReadOnlyList<float> tracks, float gap, float fallbackSize)
    {
        if (tracks.Count <= 1)
        {
            return Math.Max(fallbackSize, tracks.Sum());
        }

        var trackSize = tracks.Sum();
        var gapSize = (tracks.Count - 1) * gap;
        return Math.Max(fallbackSize, trackSize + gapSize);
    }

    private static void LayoutTextNode(
        IText textNode,
        float containingX,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        ref float activeFloatLeftOffset,
        ref float activeFloatBottom,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY)
    {
        var text = NormalizeWhitespace(textNode.Data, textStyle.WhiteSpace);

        if (text.Length == 0)
        {
            return;
        }

        previousBlockMarginBottom = 0f;
        suppressNextBlockTopMargin = false;

        if (cursorY >= activeFloatBottom)
        {
            activeFloatLeftOffset = 0f;
            activeFloatBottom = 0f;
        }

        var localFloatLeftOffset = cursorY < activeFloatBottom ? activeFloatLeftOffset : 0f;
        LayoutWrappedText(text, containingX + localFloatLeftOffset, containingWidth - localFloatLeftOffset, ref cursorY, textStyle, context, displayList, maxY, textIndentConsumed ? 0f : textStyle.TextIndent);
        textIndentConsumed = true;
    }

    private static void LayoutWrappedText(
        string text,
        float x,
        float maxWidth,
        ref float cursorY,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        float firstLineIndent)
    {
        var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;
        var lines = WrapTextRespectingWhiteSpace(context, text, maxWidth, textStyle);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            cursorY += lineHeight;

            if (cursorY > maxY)
            {
                return;
            }

            // An empty line (a blank `pre`/`pre-wrap`/`pre-line` row from a run of consecutive
            // forced breaks) still needs to advance cursorY above, but has nothing to measure/paint.
            if (line.Length == 0)
            {
                continue;
            }

            var lineWidth = MeasureTextWidth(context, line, textStyle);
            var lineMaxWidth = index == 0 ? Math.Max(0f, maxWidth - firstLineIndent) : maxWidth;

            // `text-overflow: ellipsis` is scoped to the single-line case - by far the dominant
            // real-world usage (`overflow: hidden; white-space: nowrap; text-overflow: ellipsis`) -
            // rather than truncating the last of several wrapped lines, which the CSS spec itself
            // does not define without a non-standard extension (`-webkit-line-clamp`); a genuinely
            // multi-line result here (`lines.Count > 1`) is left as-is, matching that scope cut.
            if (textStyle.TextOverflow == TextOverflowMode.Ellipsis && lines.Count == 1 && lineWidth > lineMaxWidth)
            {
                line = TruncateWithEllipsis(context, line, lineMaxWidth, textStyle);
                lineWidth = MeasureTextWidth(context, line, textStyle);
            }

            var lineX = x + (index == 0 ? firstLineIndent : 0f) + ResolveTextAlignmentOffset(textStyle.TextAlign, lineMaxWidth, lineWidth);
            var baselineY = cursorY + textStyle.VerticalAlignOffset;

            PaintTextShadows(displayList, textStyle.TextShadows, line, lineX, baselineY, textStyle);
            displayList.DrawText(
                line,
                lineX,
                baselineY,
                textStyle.Color,
                textStyle.FontSize,
                textStyle.FontFamily,
                textStyle.FontWeight,
                textStyle.IsItalic,
                textStyle.Underline,
                textStyle.StrikeThrough,
                textStyle.DecorationColor,
                textStyle.DecorationStyle,
                textStyle.LetterSpacing);
        }
    }

    /// <summary>
    /// Splits <paramref name="text"/> into display lines, honoring <c>white-space</c>'s two layout-
    /// relevant effects <see cref="NormalizeWhitespace(string, WhiteSpaceMode)"/> itself cannot: an
    /// explicit `\n` (only ever present in the input at all under <c>pre</c>/<c>pre-wrap</c>/
    /// <c>pre-line</c>/<c>break-spaces</c> - every other mode already collapsed it away) is always a
    /// forced break, laid out as its own paragraph rather than merely a wrappable space; and
    /// <c>nowrap</c>/<c>pre</c> never word-wrap at all, so each paragraph becomes exactly one
    /// (possibly overflowing) line regardless of <paramref name="maxWidth"/> - the box's own
    /// `overflow` clipping, if any, still applies to whatever ends up painted past its edge, since
    /// this only changes layout, not painting.
    /// </summary>
    private static IReadOnlyList<string> WrapTextRespectingWhiteSpace(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        var noWrap = IsNoWrapWhiteSpace(textStyle.WhiteSpace);
        var paragraphs = text.Split('\n');

        if (paragraphs.Length == 1)
        {
            return noWrap ? [text] : WrapText(context, text, maxWidth, textStyle);
        }

        var lines = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            if (noWrap || paragraph.Length == 0)
            {
                lines.Add(paragraph);
            }
            else
            {
                lines.AddRange(WrapText(context, paragraph, maxWidth, textStyle));
            }
        }

        return lines;
    }

    private static bool IsNoWrapWhiteSpace(WhiteSpaceMode whiteSpace) =>
        whiteSpace is WhiteSpaceMode.Nowrap or WhiteSpaceMode.Pre;

    private static void LayoutInlineTextRun(
        DisplayList displayList,
        string text,
        RenderTextStyle textStyle,
        float flowX,
        float flowWidth,
        LayoutContext context,
        ref float inlineCursorX,
        ref float inlineLineTop,
        ref float inlineLineHeight,
        ref bool textIndentConsumed)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightEdge = flowX + flowWidth;
        var spaceWidth = MeasureTextWidth(context, " ", textStyle);
        // `nowrap`/`pre` on mixed inline content (a <span> sharing a line with sibling text/elements)
        // still suppresses width-driven wrapping, same as the block-level LayoutWrappedText path -
        // multi-space preservation and explicit forced breaks are not supported at this level (a
        // deliberate scope cut: the caller already collapsed any literal '\n' in `text` to a plain
        // space before it ever reaches here, since this word-by-word model has no way to represent
        // one - see the two LayoutNode call sites that build `inlineText`). `word-break: break-all`/
        // `overflow-wrap: break-word` are the same kind of scope cut, for the same reason: this
        // model paints one whole word per DrawText call with no sub-word split point, unlike
        // WrapText's line-based model where a broken chunk can simply become its own line - an
        // overlong word here still overflows its line whole, exactly like `overflow-wrap: normal`.
        var noWrap = IsNoWrapWhiteSpace(textStyle.WhiteSpace);

        foreach (var word in words)
        {
            var wordWidth = MeasureTextWidth(context, word, textStyle);

            if (!noWrap && inlineCursorX > flowX && inlineCursorX + spaceWidth + wordWidth > rightEdge)
            {
                inlineLineTop += inlineLineHeight;
                inlineCursorX = flowX;
                textIndentConsumed = true;
            }

            if (inlineCursorX > flowX)
            {
                inlineCursorX += spaceWidth;
            }

            var wordBaselineY = inlineLineTop + textStyle.VerticalAlignOffset;

            PaintTextShadows(displayList, textStyle.TextShadows, word, inlineCursorX, wordBaselineY, textStyle);
            displayList.DrawText(
                word,
                inlineCursorX,
                wordBaselineY,
                textStyle.Color,
                textStyle.FontSize,
                textStyle.FontFamily,
                textStyle.FontWeight,
                textStyle.IsItalic,
                textStyle.Underline,
                textStyle.StrikeThrough,
                textStyle.DecorationColor,
                textStyle.DecorationStyle,
                textStyle.LetterSpacing);

            inlineCursorX += wordWidth;
            inlineLineHeight = Math.Max(inlineLineHeight, textStyle.FontSize * textStyle.LineHeightMultiplier);
            textIndentConsumed = true;
        }
    }

    private static string? GetDisplay(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("display", out var display) ? display : null;
    }

    private static bool IsFlexContainer(Dictionary<string, string> styleMap)
    {
        var display = GetDisplay(styleMap);
        return string.Equals(display, "flex", StringComparison.OrdinalIgnoreCase) || string.Equals(display, "inline-flex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGridContainer(Dictionary<string, string> styleMap)
    {
        var display = GetDisplay(styleMap);
        return string.Equals(display, "grid", StringComparison.OrdinalIgnoreCase) || string.Equals(display, "inline-grid", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFlexDirection(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("flex-direction", out var direction) && !string.IsNullOrWhiteSpace(direction)
            ? direction.Trim().ToLowerInvariant()
            : "row";
    }

    private static string GetJustifyContent(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("justify-content", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "flex-start";
    }

    private static string GetAlignItems(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("align-items", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "stretch";
    }

    private static string GetFlexWrap(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("flex-wrap", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "nowrap";
    }

    private static string GetAlignContent(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("align-content", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "stretch";
    }

    private static float GetFlexGrow(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("flex-grow", out var value) && !string.IsNullOrWhiteSpace(value)
            ? ParseLengthValue(value.Trim(), 0f, allowAuto: false)
            : 0f;
    }

    private static float GetFlexShrink(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("flex-shrink", out var value) && !string.IsNullOrWhiteSpace(value)
            ? ParseLengthValue(value.Trim(), 1f, allowAuto: false)
            : 1f;
    }

    private static float GetFlexOrder(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("order", out var value) && !string.IsNullOrWhiteSpace(value)
            ? ParseLengthValue(value.Trim(), 0f, allowAuto: false)
            : 0f;
    }

    private static string GetAlignSelf(Dictionary<string, string> styleMap, string fallback)
    {
        if (!styleMap.TryGetValue("align-self", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized == "auto" ? fallback : normalized;
    }

    private static FlexItemLayoutInfo CreateFlexItemLayoutInfo(IRenderNode child, bool isRowDirection, float relativeTo)
    {
        if (child is not ElementRenderNode elementChild)
        {
            return new FlexItemLayoutInfo(child, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 0f, 0f, 1f, 0f, 0f, "auto");
        }

        var childStyle = CreateStyleMap(elementChild.ComputedStyle, elementChild.Ref);
        var baseMainSize = ResolveFlexBaseSize(childStyle, isRowDirection, relativeTo);
        var crossSize = ResolveFlexCrossSize(childStyle, isRowDirection, relativeTo);
        return new FlexItemLayoutInfo(
            child,
            childStyle,
            GetFlexOrder(childStyle),
            GetFlexGrow(childStyle),
            GetFlexShrink(childStyle),
            baseMainSize,
            crossSize,
            GetAlignSelf(childStyle, "auto"));
    }

    private static float ResolveFlexBaseSize(Dictionary<string, string> styleMap, bool isRowDirection, float relativeTo)
    {
        var flexBasis = ParseLength(styleMap, "flex-basis", relativeTo, float.NaN, allowAuto: true);
        if (!float.IsNaN(flexBasis))
        {
            return flexBasis;
        }

        var mainSize = isRowDirection
            ? ParseLength(styleMap, "width", relativeTo, float.NaN, allowAuto: true)
            : ParseLength(styleMap, "height", relativeTo, float.NaN, allowAuto: true);

        return float.IsNaN(mainSize) ? 0f : mainSize;
    }

    private static float ResolveFlexCrossSize(Dictionary<string, string> styleMap, bool isRowDirection, float relativeTo)
    {
        var crossSize = isRowDirection
            ? ParseLength(styleMap, "height", relativeTo, float.NaN, allowAuto: true)
            : ParseLength(styleMap, "width", relativeTo, float.NaN, allowAuto: true);

        return float.IsNaN(crossSize) ? 0f : crossSize;
    }

    private static bool ShouldRenderAsBlock(ICssStyleDeclaration computedStyle)
    {
        var display = computedStyle.GetDisplay();

        if (!string.IsNullOrWhiteSpace(display))
        {
            var normalized = display.Trim().ToLowerInvariant();

            if (normalized.Length == 0)
            {
                return true;
            }

            return normalized switch
            {
                "none" => false,
                "inline" => false,
                "inline-flex" => false,
                "inline-grid" => false,
                "inline-table" => false,
                "contents" => false,
                _ => true,
            };
        }

        return true;
    }

    private static bool IsInlineBlock(ICssStyleDeclaration computedStyle)
    {
        var display = computedStyle.GetDisplay();
        return string.Equals(display?.Trim(), "inline-block", StringComparison.OrdinalIgnoreCase);
    }

    private static RenderTextStyle ResolveTextStyle(Dictionary<string, string> styleMap, RenderTextStyle inherited)
    {
        var fontSize = ParseLength(styleMap, "font-size", inherited.FontSize, inherited.FontSize, allowAuto: false);
        var fontFamily = styleMap.TryGetValue("font-family", out var family) && !string.IsNullOrWhiteSpace(family)
            ? family.Trim('\'', '"', ' ')
            : inherited.FontFamily;

        var lineHeight = ParseLineHeight(styleMap, inherited.LineHeightMultiplier);
        var color = ParseColor(styleMap.TryGetValue("color", out var colorValue) ? colorValue : null, inherited.Color);
        var fontWeight = ParseFontWeight(styleMap, inherited.FontWeight);
        var isItalic = ParseFontStyle(styleMap, inherited.IsItalic);
        var (underline, strikeThrough) = ParseTextDecoration(styleMap, inherited.Underline, inherited.StrikeThrough);
        var decorationColor = ParseColor(styleMap.TryGetValue("text-decoration-color", out var decorationColorValue) ? decorationColorValue : null, color);
        var decorationStyle = ParseTextDecorationStyle(styleMap, inherited.DecorationStyle);
        var textAlign = ParseTextAlign(styleMap, inherited.TextAlign);
        var letterSpacing = ParseLength(styleMap, "letter-spacing", inherited.FontSize, inherited.LetterSpacing, allowAuto: false);
        var textIndent = ParseLength(styleMap, "text-indent", inherited.FontSize, 0f, allowAuto: false);
        var verticalAlignOffset = ParseVerticalAlign(styleMap, fontSize);
        var textShadows = ParseTextShadows(styleMap.TryGetValue("text-shadow", out var textShadowValue) ? textShadowValue : null, inherited.TextShadows);
        var whiteSpace = ParseWhiteSpace(styleMap, inherited.WhiteSpace);
        var wordBreak = ParseWordBreak(styleMap, inherited.WordBreak);
        var overflowWrap = ParseOverflowWrap(styleMap, inherited.OverflowWrap);
        var textOverflow = ParseTextOverflow(styleMap);

        return new RenderTextStyle(fontSize, color, fontFamily, lineHeight, fontWeight, isItalic, underline, strikeThrough, decorationColor, decorationStyle, textAlign, letterSpacing, textIndent, verticalAlignOffset, textShadows, whiteSpace, wordBreak, overflowWrap, textOverflow);
    }

    /// <summary>
    /// <c>word-break</c> is inherited, the same as <c>white-space</c> above.
    /// </summary>
    private static WordBreakMode ParseWordBreak(Dictionary<string, string> styleMap, WordBreakMode inherited)
    {
        if (!styleMap.TryGetValue("word-break", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return string.Equals(value.Trim(), "break-all", StringComparison.OrdinalIgnoreCase)
            ? WordBreakMode.BreakAll
            : WordBreakMode.Normal;
    }

    /// <summary>
    /// <c>overflow-wrap</c>, falling back to its legacy <c>word-wrap</c> alias when the modern
    /// property was not itself authored - both are inherited, the same as <c>white-space</c> above.
    /// </summary>
    private static OverflowWrapMode ParseOverflowWrap(Dictionary<string, string> styleMap, OverflowWrapMode inherited)
    {
        var raw = styleMap.TryGetValue("overflow-wrap", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : (styleMap.TryGetValue("word-wrap", out var legacyValue) ? legacyValue : null);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return inherited;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "break-word" or "anywhere" => OverflowWrapMode.BreakWord,
            _ => OverflowWrapMode.Normal,
        };
    }

    /// <summary>
    /// <c>text-overflow</c>, unlike every other property resolved in <see cref="ResolveTextStyle"/>,
    /// is deliberately never inherited - every element re-derives it fresh from its own style map,
    /// defaulting to <see cref="TextOverflowMode.Clip"/> even when an ancestor set
    /// `text-overflow: ellipsis`, mirroring the same non-inheritance <see cref="RenderTextStyle.TextIndent"/>
    /// already establishes for itself. It also has no effect unless this element's own `overflow`
    /// clips (per spec, and matching <see cref="ShouldClipOverflow"/>'s existing "either axis"
    /// simplification) - `ellipsis` on a box that does not clip is simply ignored, same as a real
    /// browser.
    /// </summary>
    private static TextOverflowMode ParseTextOverflow(Dictionary<string, string> styleMap)
    {
        if (!ShouldClipOverflow(styleMap))
        {
            return TextOverflowMode.Clip;
        }

        return styleMap.TryGetValue("text-overflow", out var value) && string.Equals(value.Trim(), "ellipsis", StringComparison.OrdinalIgnoreCase)
            ? TextOverflowMode.Ellipsis
            : TextOverflowMode.Clip;
    }

    /// <summary>
    /// <c>white-space</c> is inherited (confirmed empirically - an unset value on a child reports
    /// empty, not the CSS initial <c>normal</c>, the same "never serialized when nothing in the
    /// cascade set it explicitly" behavior already documented for <c>list-style-type</c> - so
    /// <paramref name="inherited"/> is the correct fallback, not a hardcoded <c>Normal</c>).
    /// </summary>
    private static WhiteSpaceMode ParseWhiteSpace(Dictionary<string, string> styleMap, WhiteSpaceMode inherited)
    {
        if (!styleMap.TryGetValue("white-space", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "nowrap" => WhiteSpaceMode.Nowrap,
            "pre" => WhiteSpaceMode.Pre,
            "pre-wrap" => WhiteSpaceMode.PreWrap,
            "pre-line" => WhiteSpaceMode.PreLine,
            "break-spaces" => WhiteSpaceMode.BreakSpaces,
            _ => WhiteSpaceMode.Normal,
        };
    }

    /// <summary>
    /// Reads the box-level meaning of <c>vertical-align</c>, which is what the property means on a
    /// table cell. On inline content the same property shifts the text instead, which is what
    /// <see cref="ParseVerticalAlign"/> handles.
    /// </summary>
    private static CellVerticalAlign ParseCellVerticalAlign(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("vertical-align", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return CellVerticalAlign.Middle;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "top" or "text-top" => CellVerticalAlign.Top,
            "bottom" or "text-bottom" => CellVerticalAlign.Bottom,
            "middle" => CellVerticalAlign.Middle,
            _ => CellVerticalAlign.Top,
        };
    }

    private static float ParseVerticalAlign(Dictionary<string, string> styleMap, float fontSize)
    {
        if (!styleMap.TryGetValue("vertical-align", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 0f;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "super" => -fontSize * 1.5f,
            "sub" => fontSize * 0.8f,
            "middle" => -fontSize * 0.15f,
            "text-top" => -fontSize * 0.25f,
            "text-bottom" => fontSize * 0.1f,
            _ when ParseLengthValue(normalized, float.NaN, allowAuto: false) is var offset && !float.IsNaN(offset) => offset,
            _ => 0f,
        };
    }

    private static global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle ParseTextDecorationStyle(Dictionary<string, string> styleMap, global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle defaultValue)
    {
        if (!styleMap.TryGetValue("text-decoration-style", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "dashed" => global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Dashed,
            "dotted" => global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Dotted,
            _ => global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Solid,
        };
    }

    private static TextAlign ParseTextAlign(Dictionary<string, string> styleMap, TextAlign defaultValue)
    {
        if (!styleMap.TryGetValue("text-align", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "center" => TextAlign.Center,
            "right" => TextAlign.Right,
            "end" => TextAlign.Right,
            "left" => TextAlign.Left,
            "start" => TextAlign.Left,
            _ => defaultValue,
        };
    }

    private static float ParseFontWeight(Dictionary<string, string> styleMap, float defaultValue)
    {
        if (!styleMap.TryGetValue("font-weight", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "normal" => 400f,
            "bold" => 700f,
            _ when float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) => weight,
            _ => defaultValue,
        };
    }

    private static bool ParseFontStyle(Dictionary<string, string> styleMap, bool defaultValue)
    {
        if (!styleMap.TryGetValue("font-style", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "italic" or "oblique";
    }

    private static (bool Underline, bool StrikeThrough) ParseTextDecoration(Dictionary<string, string> styleMap, bool defaultUnderline, bool defaultStrikeThrough)
    {
        var underline = defaultUnderline;
        var strikeThrough = defaultStrikeThrough;

        if (styleMap.TryGetValue("text-decoration-line", out var lineValue) || styleMap.TryGetValue("text-decoration", out lineValue))
        {
            var tokens = lineValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var token in tokens)
            {
                var normalized = token.Trim().ToLowerInvariant();

                if (normalized == "underline")
                {
                    underline = true;
                }
                else if (normalized is "line-through" or "strikethrough")
                {
                    strikeThrough = true;
                }
                else if (normalized == "none")
                {
                    underline = false;
                    strikeThrough = false;
                }
            }
        }

        return (underline, strikeThrough);
    }

    private static float ParseLineHeight(Dictionary<string, string> styleMap, float defaultValue)
    {
        if (!styleMap.TryGetValue("line-height", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        if (normalized.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(normalized[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100f;
        }

        if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var unitless))
        {
            return unitless;
        }

        return defaultValue;
    }

    private static void PrepareDocumentForRendering(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var element in document.All.OfType<IElement>())
        {
            var styleAttribute = element.GetAttribute("style");

            if (string.IsNullOrWhiteSpace(styleAttribute))
            {
                continue;
            }

            var currentStyle = styleAttribute;
            var changed = false;

            if (TryExtractTransformDeclaration(currentStyle, out var transformValue, out var updatedStyle))
            {
                currentStyle = updatedStyle;
                changed = true;
                element.SetAttribute("data-render-transform", transformValue);
            }

            if (changed)
            {
                element.SetAttribute("style", currentStyle);
            }
        }
    }

    /// <summary>
    /// Extracts a `transform` declaration out of an inline `style` attribute before AngleSharp.Css
    /// ever sees it (via a `data-render-transform` attribute this renderer reads back in
    /// `CreateStyleMap` instead) - a workaround for a genuine upstream crash, not an unsupported-
    /// value gap: AngleSharp.Css's own `CssTranslateValue.Compute()` throws a
    /// `NullReferenceException` - confirmed via a failing test with a minimal repro, not assumed -
    /// for *any* `translate`/`translateX`/`translateY` function, and that crash happens eagerly
    /// while building the render tree (`RenderTreeBuilder.RenderElement` computing the *entire*
    /// style declaration at once), before this renderer's own code ever runs. Extraction therefore
    /// has to happen unconditionally for every `transform` declaration - not only ones containing
    /// `translate` - both to keep this single code path simple and because relying on exactly
    /// which other functions are crash-free would be fragile against a future AngleSharp.Css
    /// version. `rotate()`/`scale()` were separately confirmed *not* to crash, but are extracted
    /// the same way regardless, for that same reason. `background-image` gradients used to need
    /// this identical extraction pattern too, until AngleSharp.Css's own `GradientParser` shipped
    /// and computed style started round-tripping the raw gradient text correctly - see the
    /// `filter`/gradient paragraphs in AGENTS.md for the general "each upstream gap gets its own
    /// fix, not a shared local workaround" policy this follows.
    /// </summary>
    private static bool TryExtractTransformDeclaration(string styleAttribute, out string transformValue, out string updatedStyle)
    {
        transformValue = string.Empty;
        updatedStyle = styleAttribute;

        if (!styleAttribute.Contains("transform", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var declarations = styleAttribute.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var remaining = new List<string>();

        foreach (var declaration in declarations)
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var property = declaration[..separator].Trim();
            var value = declaration[(separator + 1)..].Trim();

            // Exact match only - "transform" must not also swallow "transform-origin", which is
            // safe to leave for AngleSharp.Css's own (uncrashing) computation.
            if (string.Equals(property, "transform", StringComparison.OrdinalIgnoreCase))
            {
                transformValue = value;
                continue;
            }

            remaining.Add(declaration);
        }

        if (string.IsNullOrWhiteSpace(transformValue))
        {
            return false;
        }

        updatedStyle = string.Join(";", remaining);
        return true;
    }

    /// <summary>
    /// Resolves `background-image` from the cascaded but *uncomputed* declaration rather than
    /// <c>ComputeCurrentStyle()</c>'s normal computed value, a deliberate, narrow exception to how
    /// every other property in this map is read. AngleSharp.Css's `.Compute()` step eagerly
    /// resolves a `CssPoint2D`'s percentage/keyword components (a gradient's `at &lt;position&gt;`,
    /// and equally a radial gradient's explicit percentage size) into absolute pixels using
    /// whatever `IRenderDimensions` happens to be current at CSSOM-compute time - the *viewport*,
    /// not the element's own box, since a gradient's box is not a concept the general CSSOM compute
    /// pass has any way to know about. Confirmed empirically: a `radial-gradient(red, blue)` (no
    /// `at` clause at all - the default, and by far the most common way one is written) on a
    /// 200x100px box inside a 300x200px viewport computed to `at 150px 150px` - both axes resolved
    /// against the *viewport's* 300px width, not each axis against the box's own matching
    /// dimension, which would already be wrong even before <see cref="CssPoint2D"/>'s own separate
    /// `Compute()` bug (its `y` local is assigned from `_x.Compute(context)` instead of
    /// `_y.Compute(context)` - reported upstream) compounds it further. There is no way to recover
    /// the original percentage/keyword/position from that already-resolved-against-the-wrong-thing
    /// pixel text, so this renderer cannot use the computed value for `background-image` at all -
    /// `StyleCollectionExtensions.GetDeclarations` (`ComputeExplicitStyle` under the hood) gives the
    /// same cascaded, colour-normalized value with none of this resolution applied, matching exactly
    /// what a plain `style.GetPropertyValue("background-image")` gives for every other case
    /// (`url(...)`, or a gradient with only non-percentage arguments) where the two do not diverge.
    /// Falls back to the ordinary computed value if no window/device is available to build the
    /// style collection from (mirrors the same fallback <c>ComputeCurrentStyle()</c> itself uses
    /// internally when an element has no `Owner.DefaultView`).
    /// </summary>
    private static string ResolveExplicitBackgroundImage(IElement? element, ICssStyleDeclaration computedStyle) =>
        ResolveExplicitPropertyValue(element, computedStyle, "background-image");

    /// <summary>
    /// Reads <paramref name="propertyName"/> from the cascaded but *uncomputed* declaration rather
    /// than <c>ComputeCurrentStyle()</c>'s normal computed value - the same technique
    /// <see cref="ResolveExplicitBackgroundImage"/> established for `background-image`'s gradient
    /// percentages, reused here for the CSS Grid properties that hit the identical class of bug:
    /// `grid-template-columns`/`grid-template-rows` can hold percentage tracks, which AngleSharp.Css's
    /// `.Compute()` step eagerly resolves against whatever `IRenderDimensions` happens to be current
    /// at CSSOM-compute time (the *viewport*, confirmed empirically - `grid-template-rows: 30%` on an
    /// element with no defined height inside a 600x300 viewport computed to `180px`, i.e. 30% of the
    /// 600px *width*, not the 300px height a row percentage should track) rather than the grid
    /// container's own box, which is not a concept the general CSSOM compute pass has any way to
    /// know about - the same root cause `ResolveExplicitBackgroundImage`'s own remarks document in
    /// full. There is no way to recover the original percentage from that already-resolved-against-
    /// the-wrong-thing pixel text, so the computed value cannot be used for these properties at all.
    /// </summary>
    private static string ResolveExplicitPropertyValue(IElement? element, ICssStyleDeclaration computedStyle, string propertyName)
    {
        var window = element?.Owner?.DefaultView;

        if (window is not null)
        {
            var device = window.Document.Context.GetService<IRenderDevice>() ?? new DefaultRenderDevice();
            var styleCollection = window.GetStyleCollection(device);
            var explicitValue = styleCollection.GetDeclarations(element!).GetPropertyValue(propertyName);

            if (!string.IsNullOrWhiteSpace(explicitValue))
            {
                return explicitValue;
            }
        }

        return computedStyle.GetPropertyValue(propertyName);
    }

    private static Dictionary<string, string> CreateStyleMap(ICssStyleDeclaration style, IElement? element = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inlineStyle = element?.GetAttribute("style");

        var displayValue = style.GetDisplay();
        if (string.IsNullOrWhiteSpace(displayValue))
        {
            displayValue = ParseStyleAttributeValue(inlineStyle, "display");
        }

        AddIfPresent(map, "display", displayValue);
        AddIfPresent(map, "visibility", style.GetVisibility());
        AddIfPresent(map, "width", style.GetWidth());
        AddIfPresent(map, "height", style.GetHeight());
        AddIfPresent(map, "position", style.GetPropertyValue("position"));
        AddIfPresent(map, "left", style.GetPropertyValue("left"));
        AddIfPresent(map, "top", style.GetPropertyValue("top"));
        AddIfPresent(map, "float", style.GetPropertyValue("float"));
        AddIfPresent(map, "z-index", style.GetPropertyValue("z-index"));

        AddIfPresent(map, "margin-top", style.GetMarginTop());
        AddIfPresent(map, "margin-right", style.GetMarginRight());
        AddIfPresent(map, "margin-bottom", style.GetMarginBottom());
        AddIfPresent(map, "margin-left", style.GetMarginLeft());

        AddIfPresent(map, "padding-top", style.GetPaddingTop());
        AddIfPresent(map, "padding-right", style.GetPaddingRight());
        AddIfPresent(map, "padding-bottom", style.GetPaddingBottom());
        AddIfPresent(map, "padding-left", style.GetPaddingLeft());

        AddIfPresent(map, "border-top-width", style.GetBorderTopWidth());
        AddIfPresent(map, "border-right-width", style.GetBorderRightWidth());
        AddIfPresent(map, "border-bottom-width", style.GetBorderBottomWidth());
        AddIfPresent(map, "border-left-width", style.GetBorderLeftWidth());
        AddIfPresent(map, "border-collapse", style.GetPropertyValue("border-collapse"));

        AddIfPresent(map, "border-top-style", style.GetBorderTopStyle());
        AddIfPresent(map, "border-right-style", style.GetBorderRightStyle());
        AddIfPresent(map, "border-bottom-style", style.GetBorderBottomStyle());
        AddIfPresent(map, "border-left-style", style.GetBorderLeftStyle());

        AddIfPresent(map, "border-top-color", style.GetBorderTopColor());
        AddIfPresent(map, "border-right-color", style.GetBorderRightColor());
        AddIfPresent(map, "border-bottom-color", style.GetBorderBottomColor());
        AddIfPresent(map, "border-left-color", style.GetBorderLeftColor());

        AddIfPresent(map, "border-top-left-radius", style.GetBorderTopLeftRadius());
        AddIfPresent(map, "border-top-right-radius", style.GetBorderTopRightRadius());
        AddIfPresent(map, "border-bottom-right-radius", style.GetBorderBottomRightRadius());
        AddIfPresent(map, "border-bottom-left-radius", style.GetBorderBottomLeftRadius());

        AddIfPresent(map, "box-shadow", style.GetBoxShadow());
        AddIfPresent(map, "text-shadow", style.GetTextShadow());

        AddIfPresent(map, "list-style-type", style.GetPropertyValue("list-style-type"));
        AddIfPresent(map, "list-style-position", style.GetPropertyValue("list-style-position"));

        AddIfPresent(map, "overflow-x", style.GetPropertyValue("overflow-x"));
        AddIfPresent(map, "overflow-y", style.GetPropertyValue("overflow-y"));

        AddIfPresent(map, "outline-width", style.GetPropertyValue("outline-width"));
        AddIfPresent(map, "outline-style", style.GetPropertyValue("outline-style"));
        AddIfPresent(map, "outline-color", style.GetPropertyValue("outline-color"));

        AddIfPresent(map, "background-color", style.GetBackgroundColor());
        // grid-template-columns/rows, grid-column/row, and the gap properties are all read from
        // the explicit/cascaded declaration rather than computed style, for the same reason
        // `background-image` is (see ResolveExplicitPropertyValue's own remarks): a percentage
        // track/gap gets eagerly resolved against the wrong reference dimension by AngleSharp.Css's
        // `.Compute()` step. grid-column/grid-row additionally used to crash computed style
        // entirely for the common `<line> / span <n>` form (CssTupleValue<T>.Compute() calling
        // .Compute() on the omitted end line's null entry) - fixed upstream, but reading the
        // explicit declaration sidesteps that whole bug class regardless.
        var explicitGridTemplateColumns = ResolveExplicitPropertyValue(element, style, "grid-template-columns");
        AddIfPresent(map, "grid-template-columns", string.IsNullOrWhiteSpace(explicitGridTemplateColumns) ? ParseStyleAttributeValue(inlineStyle, "grid-template-columns") : explicitGridTemplateColumns);
        var explicitGridTemplateRows = ResolveExplicitPropertyValue(element, style, "grid-template-rows");
        AddIfPresent(map, "grid-template-rows", string.IsNullOrWhiteSpace(explicitGridTemplateRows) ? ParseStyleAttributeValue(inlineStyle, "grid-template-rows") : explicitGridTemplateRows);
        var explicitColumnGap = ResolveExplicitPropertyValue(element, style, "column-gap");
        AddIfPresent(map, "column-gap", string.IsNullOrWhiteSpace(explicitColumnGap) ? ParseStyleAttributeValue(inlineStyle, "column-gap") : explicitColumnGap);
        var explicitRowGap = ResolveExplicitPropertyValue(element, style, "row-gap");
        AddIfPresent(map, "row-gap", string.IsNullOrWhiteSpace(explicitRowGap) ? ParseStyleAttributeValue(inlineStyle, "row-gap") : explicitRowGap);
        var explicitGap = ResolveExplicitPropertyValue(element, style, "gap");
        AddIfPresent(map, "gap", string.IsNullOrWhiteSpace(explicitGap) ? ParseStyleAttributeValue(inlineStyle, "gap") : explicitGap);
        var explicitGridColumn = ResolveExplicitPropertyValue(element, style, "grid-column");
        AddIfPresent(map, "grid-column", string.IsNullOrWhiteSpace(explicitGridColumn) ? ParseStyleAttributeValue(inlineStyle, "grid-column") : explicitGridColumn);
        var explicitGridRow = ResolveExplicitPropertyValue(element, style, "grid-row");
        AddIfPresent(map, "grid-row", string.IsNullOrWhiteSpace(explicitGridRow) ? ParseStyleAttributeValue(inlineStyle, "grid-row") : explicitGridRow);
        AddIfPresent(map, "flex-direction", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-direction")) ? ParseStyleAttributeValue(inlineStyle, "flex-direction") : style.GetPropertyValue("flex-direction"));
        AddIfPresent(map, "justify-content", string.IsNullOrWhiteSpace(style.GetPropertyValue("justify-content")) ? ParseStyleAttributeValue(inlineStyle, "justify-content") : style.GetPropertyValue("justify-content"));
        AddIfPresent(map, "align-items", string.IsNullOrWhiteSpace(style.GetPropertyValue("align-items")) ? ParseStyleAttributeValue(inlineStyle, "align-items") : style.GetPropertyValue("align-items"));
        AddIfPresent(map, "align-self", string.IsNullOrWhiteSpace(style.GetPropertyValue("align-self")) ? ParseStyleAttributeValue(inlineStyle, "align-self") : style.GetPropertyValue("align-self"));
        AddIfPresent(map, "flex-wrap", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-wrap")) ? ParseStyleAttributeValue(inlineStyle, "flex-wrap") : style.GetPropertyValue("flex-wrap"));
        AddIfPresent(map, "flex-grow", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-grow")) ? ParseStyleAttributeValue(inlineStyle, "flex-grow") : style.GetPropertyValue("flex-grow"));
        AddIfPresent(map, "flex-shrink", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-shrink")) ? ParseStyleAttributeValue(inlineStyle, "flex-shrink") : style.GetPropertyValue("flex-shrink"));
        AddIfPresent(map, "flex-basis", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-basis")) ? ParseStyleAttributeValue(inlineStyle, "flex-basis") : style.GetPropertyValue("flex-basis"));
        AddIfPresent(map, "order", string.IsNullOrWhiteSpace(style.GetPropertyValue("order")) ? ParseStyleAttributeValue(inlineStyle, "order") : style.GetPropertyValue("order"));
        AddIfPresent(map, "align-content", string.IsNullOrWhiteSpace(style.GetPropertyValue("align-content")) ? ParseStyleAttributeValue(inlineStyle, "align-content") : style.GetPropertyValue("align-content"));

        var resolvedBackgroundImage = ResolveExplicitBackgroundImage(element, style);
        AddIfPresent(map, "background-image", string.IsNullOrWhiteSpace(resolvedBackgroundImage)
            ? ParseStyleAttributeValue(inlineStyle, "background-image")
            : resolvedBackgroundImage);

        AddIfPresent(map, "background-repeat", string.IsNullOrWhiteSpace(style.GetPropertyValue("background-repeat")) ? ParseStyleAttributeValue(inlineStyle, "background-repeat") : style.GetPropertyValue("background-repeat"));
        AddIfPresent(map, "background-position", string.IsNullOrWhiteSpace(style.GetPropertyValue("background-position")) ? ParseStyleAttributeValue(inlineStyle, "background-position") : style.GetPropertyValue("background-position"));
        AddIfPresent(map, "background-size", string.IsNullOrWhiteSpace(style.GetPropertyValue("background-size")) ? ParseStyleAttributeValue(inlineStyle, "background-size") : style.GetPropertyValue("background-size"));
        // Never read from AngleSharp.Css's own computed `transform` (nor even the raw inline style,
        // since PrepareDocumentForRendering has already stripped it out by this point) -
        // TryExtractTransformDeclaration moves the raw value to `data-render-transform` before
        // AngleSharp.Css ever computes anything, working around a confirmed upstream crash in its
        // `CssTranslateValue.Compute()`. See TryExtractTransformDeclaration's own remarks.
        var rawTransformValue = element?.GetAttribute("data-render-transform");
        AddIfPresent(map, "transform", rawTransformValue);
        AddIfPresent(map, "transform-origin", string.IsNullOrWhiteSpace(style.GetPropertyValue("transform-origin")) ? ParseStyleAttributeValue(inlineStyle, "transform-origin") : style.GetPropertyValue("transform-origin"));
        // AngleSharp.Css now parses `filter` into a structured CssFilterValue and preserves it
        // through computed style (fixed upstream - it used to always report an empty string here),
        // so this can now read the ordinary computed-style value directly, the same as any other
        // property.
        AddIfPresent(map, "filter", style.GetPropertyValue("filter"));
        AddIfPresent(map, "opacity", style.GetOpacity());
        AddIfPresent(map, "font-size", style.GetFontSize());
        AddIfPresent(map, "font-family", style.GetFontFamily());
        AddIfPresent(map, "font-weight", style.GetPropertyValue("font-weight"));
        AddIfPresent(map, "font-style", style.GetPropertyValue("font-style"));
        AddIfPresent(map, "text-decoration", style.GetPropertyValue("text-decoration"));
        AddIfPresent(map, "text-decoration-line", style.GetPropertyValue("text-decoration-line"));
        AddIfPresent(map, "text-decoration-color", style.GetPropertyValue("text-decoration-color"));
        AddIfPresent(map, "text-decoration-style", style.GetPropertyValue("text-decoration-style"));
        AddIfPresent(map, "text-align", style.GetPropertyValue("text-align"));
        AddIfPresent(map, "text-indent", style.GetTextIndent());
        AddIfPresent(map, "vertical-align", style.GetVerticalAlign());
        AddIfPresent(map, "letter-spacing", style.GetPropertyValue("letter-spacing"));
        AddIfPresent(map, "line-height", style.GetLineHeight());
        AddIfPresent(map, "color", style.GetColor());
        AddIfPresent(map, "white-space", style.GetPropertyValue("white-space"));
        AddIfPresent(map, "text-overflow", style.GetPropertyValue("text-overflow"));
        AddIfPresent(map, "word-break", style.GetPropertyValue("word-break"));
        AddIfPresent(map, "overflow-wrap", style.GetPropertyValue("overflow-wrap"));
        AddIfPresent(map, "word-wrap", style.GetPropertyValue("word-wrap"));

        ApplyActiveTransitionAndAnimationOverrides(map, element);

        return map;
    }

    // Overrides every style-map entry that is currently mid-`transition` or mid-`animation` with
    // its interpolated value, so the rest of this renderer's normal per-property parsing
    // (ParseLength, ParseColor, ...) never has to know either is even happening - it just sees
    // whatever value would otherwise be in the map, substituted for the eased in-between one. A
    // no-op (and effectively free - one ConditionalWeakTable lookup) for the overwhelming majority
    // of documents, which were never wired up for interactive use via
    // IBrowsingContext.GetDomHarness() at all. `animation` takes precedence over `transition` for a
    // property both are currently affecting - a reasonable simplification of the CSS cascade's own
    // "animations, then transitions" ordering (transitions technically animate on top of whatever
    // the animation produces, which this renderer does not attempt to layer precisely).
    private static void ApplyActiveTransitionAndAnimationOverrides(Dictionary<string, string> map, IElement? element)
    {
        if (element?.Owner is null || !element.Owner.Context.TryGetDomHarness(out var harness) || harness is null)
        {
            return;
        }

        // Iterates the full interpolatable whitelist, not just map.Keys - a property that only
        // ever appears inside a @keyframes block (never as a base/inherited declaration, e.g.
        // `opacity` set solely by an animation) would otherwise have no key in the map at all for
        // this loop to find and override.
        foreach (var property in CssValueInterpolation.InterpolatableProperties.Keys)
        {
            var animatedValue = harness.GetAnimatedValue(element, property);

            if (animatedValue is not null)
            {
                map[property] = animatedValue;
                continue;
            }

            var transitioningValue = harness.GetTransitioningValue(element, property);

            if (transitioningValue is not null)
            {
                map[property] = transitioningValue;
            }
        }
    }

    private static void AddIfPresent(Dictionary<string, string> map, string property, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            map[property] = value;
        }
    }

    private static string? ParseStyleAttributeValue(string? styleAttribute, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(styleAttribute))
        {
            return null;
        }

        foreach (var declaration in styleAttribute.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = declaration.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var candidateProperty = declaration[..separatorIndex].Trim();
            if (string.Equals(candidateProperty, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(separatorIndex + 1)..].Trim();
            }
        }

        return null;
    }

    private static bool TryGetFirstCollapsibleChildTopMargin(ElementRenderNode node, float containingWidth, out float marginTop)
    {
        foreach (var child in node.Children)
        {
            if (child is TextRenderNode textNode)
            {
                if (NormalizeWhitespace(textNode.Ref.Data).Length > 0)
                {
                    marginTop = 0f;
                    return false;
                }

                continue;
            }

            if (child is not ElementRenderNode childElement)
            {
                continue;
            }

            if (!childElement.IsVisible())
            {
                continue;
            }

            var tagName = childElement.Ref.LocalName;

            if (string.Equals(tagName, "script", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tagName, "style", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ShouldRenderAsBlock(childElement.ComputedStyle))
            {
                marginTop = 0f;
                return false;
            }

            var childStyle = CreateStyleMap(childElement.ComputedStyle, childElement.Ref);
            marginTop = ParseLength(childStyle, "margin-top", containingWidth, 0f, allowAuto: false);
            return true;
        }

        marginTop = 0f;
        return false;
    }

    private static IEnumerable<IRenderNode> OrderChildrenForPainting(IEnumerable<IRenderNode> children)
    {
        var negatives = new List<(IRenderNode Node, int Z, int Index)>();
        var flow = new List<(IRenderNode Node, int Index)>();
        var nonNegatives = new List<(IRenderNode Node, int Z, int Index)>();

        var index = 0;

        foreach (var child in children)
        {
            if (child is ElementRenderNode elementChild)
            {
                var childStyleMap = CreateStyleMap(elementChild.ComputedStyle, elementChild.Ref);

                if (IsOutOfFlowPositioned(childStyleMap))
                {
                    var z = ParseZIndex(childStyleMap);

                    if (z < 0)
                    {
                        negatives.Add((child, z, index));
                    }
                    else
                    {
                        nonNegatives.Add((child, z, index));
                    }

                    index++;
                    continue;
                }
            }

            flow.Add((child, index));
            index++;
        }

        foreach (var entry in negatives.OrderBy(m => m.Z).ThenBy(m => m.Index))
        {
            yield return entry.Node;
        }

        foreach (var entry in flow.OrderBy(m => m.Index))
        {
            yield return entry.Node;
        }

        foreach (var entry in nonNegatives.OrderBy(m => m.Z).ThenBy(m => m.Index))
        {
            yield return entry.Node;
        }
    }

    private static bool IsOutOfFlowPositioned(Dictionary<string, string> styleMap)
    {
        var position = GetPosition(styleMap);
        return string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStickyPositioned(Dictionary<string, string> styleMap) =>
        string.Equals(GetPosition(styleMap), "sticky", StringComparison.OrdinalIgnoreCase);

    private static int ParseZIndex(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("z-index", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var normalized = value.Trim().ToLowerInvariant();

        if (string.Equals(normalized, "auto", StringComparison.Ordinal))
        {
            return 0;
        }

        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
            ? z
            : 0;
    }

    private static BoxStyle ResolveBoxStyle(Dictionary<string, string> styleMap, IElement element)
    {
        var margin = new EdgeSizes(
            Top: ParseLength(styleMap, "margin-top", 0f, 0f, allowAuto: false),
            Right: ParseLength(styleMap, "margin-right", 0f, 0f, allowAuto: true),
            Bottom: ParseLength(styleMap, "margin-bottom", 0f, 0f, allowAuto: false),
            Left: ParseLength(styleMap, "margin-left", 0f, 0f, allowAuto: true));

        var padding = new EdgeSizes(
            Top: ParseLength(styleMap, "padding-top", 0f, 0f, allowAuto: false),
            Right: ParseLength(styleMap, "padding-right", 0f, 0f, allowAuto: false),
            Bottom: ParseLength(styleMap, "padding-bottom", 0f, 0f, allowAuto: false),
            Left: ParseLength(styleMap, "padding-left", 0f, 0f, allowAuto: false));

        var borderWidth = new EdgeSizes(
            Top: ParseLength(styleMap, "border-top-width", 0f, 0f, allowAuto: false),
            Right: ParseLength(styleMap, "border-right-width", 0f, 0f, allowAuto: false),
            Bottom: ParseLength(styleMap, "border-bottom-width", 0f, 0f, allowAuto: false),
            Left: ParseLength(styleMap, "border-left-width", 0f, 0f, allowAuto: false));

        var borderStyle = ResolveBorderStyles(styleMap);

        borderWidth = ApplyBorderStyleToWidths(borderWidth, borderStyle);

        var backgroundColor = ParseColor(styleMap.TryGetValue("background-color", out var background) ? background : null, RenderColor.Transparent);
        var backgroundPaint = ParseBackgroundPaint(styleMap, backgroundColor, element);
        var borderColor = ParseColor(
            styleMap.TryGetValue("border-top-color", out var topColor) ? topColor :
            styleMap.TryGetValue("border-right-color", out var rightColor) ? rightColor :
            styleMap.TryGetValue("border-bottom-color", out var bottomColor) ? bottomColor :
            styleMap.TryGetValue("border-left-color", out var leftColor) ? leftColor :
            null,
            RenderColor.Black);

        var (topLeftX, topLeftY) = ParseCornerRadius(styleMap, "border-top-left-radius");
        var (topRightX, topRightY) = ParseCornerRadius(styleMap, "border-top-right-radius");
        var (bottomRightX, bottomRightY) = ParseCornerRadius(styleMap, "border-bottom-right-radius");
        var (bottomLeftX, bottomLeftY) = ParseCornerRadius(styleMap, "border-bottom-left-radius");
        var borderRadius = new RenderCornerRadii(
            topLeftX, topLeftY,
            topRightX, topRightY,
            bottomRightX, bottomRightY,
            bottomLeftX, bottomLeftY);

        var boxShadows = ParseBoxShadows(styleMap.TryGetValue("box-shadow", out var boxShadowValue) ? boxShadowValue : null);

        return new BoxStyle(margin, padding, borderWidth, backgroundPaint, borderColor, borderRadius, boxShadows);
    }

    /// <summary>
    /// Parses a `box-shadow` value into its comma-separated layers, in CSS authoring order
    /// (first-listed shadow paints topmost among shadows). AngleSharp.Css's computed style
    /// normalizes every layer's color to `rgba(r, g, b, a)` regardless of how it was authored
    /// (`red`, `#f00`, ...), which <see cref="ExtractColorToken"/> relies on to split a layer's
    /// color from its lengths without being confused by the commas inside `rgba(...)`.
    /// </summary>
    private static IReadOnlyList<RenderBoxShadow> ParseBoxShadows(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var layers = SplitTopLevelCommaList(value);
        var shadows = new List<RenderBoxShadow>(layers.Length);

        foreach (var layer in layers)
        {
            if (ParseSingleBoxShadow(layer) is { } shadow)
            {
                shadows.Add(shadow);
            }
        }

        return shadows;
    }

    private static RenderBoxShadow? ParseSingleBoxShadow(string layer)
    {
        var trimmed = layer.Trim();
        var inset = false;

        if (trimmed.StartsWith("inset", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
        {
            inset = true;
            trimmed = trimmed[5..].TrimStart();
        }
        else if (trimmed.EndsWith("inset", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[^6])))
        {
            inset = true;
            trimmed = trimmed[..^5].TrimEnd();
        }

        var (colorToken, remainder) = ExtractColorToken(trimmed);
        var color = ParseColor(colorToken, RenderColor.Black);
        var tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 2)
        {
            return null;
        }

        var offsetX = ParseLengthValue(tokens[0], 0f, allowAuto: false);
        var offsetY = ParseLengthValue(tokens[1], 0f, allowAuto: false);
        var blurRadius = tokens.Length > 2 ? Math.Max(0f, ParseLengthValue(tokens[2], 0f, allowAuto: false)) : 0f;
        var spreadRadius = tokens.Length > 3 ? ParseLengthValue(tokens[3], 0f, allowAuto: false) : 0f;

        return new RenderBoxShadow(offsetX, offsetY, blurRadius, spreadRadius, color, inset);
    }

    /// <summary>
    /// Parses a `text-shadow` value into its comma-separated layers, in CSS authoring order
    /// (first-listed shadow paints topmost among shadows). Unlike `box-shadow`, a layer has no
    /// `inset` keyword and no spread component.
    /// </summary>
    private static IReadOnlyList<RenderTextShadow> ParseTextShadows(string? value, IReadOnlyList<RenderTextShadow> inherited)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        if (string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var layers = SplitTopLevelCommaList(value);
        var shadows = new List<RenderTextShadow>(layers.Length);

        foreach (var layer in layers)
        {
            if (ParseSingleTextShadow(layer) is { } shadow)
            {
                shadows.Add(shadow);
            }
        }

        return shadows;
    }

    private static RenderTextShadow? ParseSingleTextShadow(string layer)
    {
        var (colorToken, remainder) = ExtractColorToken(layer.Trim());
        var color = ParseColor(colorToken, RenderColor.Black);
        var tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 2)
        {
            return null;
        }

        var offsetX = ParseLengthValue(tokens[0], 0f, allowAuto: false);
        var offsetY = ParseLengthValue(tokens[1], 0f, allowAuto: false);
        var blurRadius = tokens.Length > 2 ? Math.Max(0f, ParseLengthValue(tokens[2], 0f, allowAuto: false)) : 0f;

        return new RenderTextShadow(offsetX, offsetY, blurRadius, color);
    }

    /// <summary>
    /// Splits a shadow layer's color from its offset/blur/spread lengths. AngleSharp.Css always
    /// normalizes a shadow's color to `rgba(r, g, b, a)`, so the color is located by its
    /// parenthesized function call rather than by naive whitespace splitting, which would
    /// otherwise be misled by the spaces after the commas inside `rgba(...)`. Falls back to
    /// treating the last whitespace-separated token as the color for any value that reaches this
    /// parser without going through AngleSharp.Css's own normalization.
    /// </summary>
    private static (string? ColorToken, string Remainder) ExtractColorToken(string value)
    {
        var functionStart = value.IndexOf("rgba(", StringComparison.OrdinalIgnoreCase);

        if (functionStart < 0)
        {
            var lastSpace = value.TrimEnd().LastIndexOf(' ');
            return lastSpace < 0
                ? (value.Length > 0 ? value.Trim() : null, string.Empty)
                : (value[(lastSpace + 1)..].Trim(), value[..lastSpace]);
        }

        var functionEnd = value.IndexOf(')', functionStart);

        if (functionEnd < 0)
        {
            return (value[functionStart..].Trim(), value[..functionStart]);
        }

        var colorToken = value[functionStart..(functionEnd + 1)];
        var remainder = value[..functionStart] + value[(functionEnd + 1)..];
        return (colorToken, remainder);
    }

    /// <summary>
    /// Parses a `border-*-radius` longhand value, which AngleSharp.Css reports as one length
    /// ("8px", for a circular corner) or two space-separated lengths ("8px 4px", horizontal then
    /// vertical, for an elliptical corner). Percentages arrive already resolved to pixels by
    /// AngleSharp.Css's computed style engine.
    /// </summary>
    private static (float X, float Y) ParseCornerRadius(Dictionary<string, string> styleMap, string propertyName)
    {
        if (!styleMap.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return (0f, 0f);
        }

        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return (0f, 0f);
        }

        var x = ParseLengthValue(parts[0], 0f, allowAuto: false);
        var y = parts.Length > 1 ? ParseLengthValue(parts[1], 0f, allowAuto: false) : x;

        return (x, y);
    }

    private static EdgeBorderStyle ResolveBorderStyles(Dictionary<string, string> styleMap)
    {
        var top = styleMap.TryGetValue("border-top-style", out var topStyle)
            ? ParseBorderStyleToken(topStyle)
            : BorderStyleKind.Solid;
        var right = styleMap.TryGetValue("border-right-style", out var rightStyle)
            ? ParseBorderStyleToken(rightStyle)
            : BorderStyleKind.Solid;
        var bottom = styleMap.TryGetValue("border-bottom-style", out var bottomStyle)
            ? ParseBorderStyleToken(bottomStyle)
            : BorderStyleKind.Solid;
        var left = styleMap.TryGetValue("border-left-style", out var leftStyle)
            ? ParseBorderStyleToken(leftStyle)
            : BorderStyleKind.Solid;

        return new EdgeBorderStyle(top, right, bottom, left);
    }

    private static BorderStyleKind ParseBorderStyleToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BorderStyleKind.Solid;
        }

        var normalized = token.Trim().ToLowerInvariant();

        return normalized switch
        {
            "none" => BorderStyleKind.None,
            "hidden" => BorderStyleKind.Hidden,
            _ => BorderStyleKind.Solid,
        };
    }

    private static EdgeSizes ApplyBorderStyleToWidths(EdgeSizes widths, EdgeBorderStyle styles)
    {
        return widths with
        {
            Top = IsPaintedBorderStyle(styles.Top) ? widths.Top : 0f,
            Right = IsPaintedBorderStyle(styles.Right) ? widths.Right : 0f,
            Bottom = IsPaintedBorderStyle(styles.Bottom) ? widths.Bottom : 0f,
            Left = IsPaintedBorderStyle(styles.Left) ? widths.Left : 0f,
        };
    }

    private static bool IsPaintedBorderStyle(BorderStyleKind style) => style != BorderStyleKind.None && style != BorderStyleKind.Hidden;

    

    private static string GetPosition(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("position", out var position)
            ? position.Trim().ToLowerInvariant()
            : string.Empty;
    }

    private static string GetFloat(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("float", out var value)
            ? value.Trim().ToLowerInvariant()
            : string.Empty;
    }

    private static void PaintOutline(DisplayList displayList, Dictionary<string, string> styleMap, float x, float y, float width, float height)
    {
        if (!styleMap.TryGetValue("outline-width", out var outlineWidthRaw) ||
            !styleMap.TryGetValue("outline-style", out var outlineStyleRaw))
        {
            return;
        }

        var style = ParseBorderStyleToken(outlineStyleRaw);

        if (!IsPaintedBorderStyle(style))
        {
            return;
        }

        var outlineWidth = ParseLengthValue(outlineWidthRaw, 0f, allowAuto: false);

        if (outlineWidth <= 0f)
        {
            return;
        }

        var color = ParseColor(
            styleMap.TryGetValue("outline-color", out var outlineColor) ? outlineColor : null,
            RenderColor.Black);

        PaintBorder(
            displayList,
            color,
            x - outlineWidth,
            y - outlineWidth,
            width + (2f * outlineWidth),
            height + (2f * outlineWidth),
            new EdgeSizes(outlineWidth, outlineWidth, outlineWidth, outlineWidth));
    }

    private static bool IsReplacedElementTag(string tagName) =>
        string.Equals(tagName, "img", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(tagName, "svg", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveReplacedElementImage(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect) =>
        TryResolveImage(node, styleMap, containingWidth, x, y, out image, out rect) ||
        TryResolveInlineSvg(node, styleMap, containingWidth, x, y, out image, out rect);

    private static bool TryResolveImage(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect)
    {
        image = null;
        rect = default;

        // node.Ref.LocalName/GetAttribute("src") proxy through to the host for a ::before/::after
        // pseudo-element (PseudoElement.cs, AngleSharp.Css) - without this guard, an <img>'s own
        // generated-content pseudo would be mistaken for the <img> itself and paint the same image
        // a second time as if the pseudo were a replaced element in its own right.
        if (node.Ref is IPseudoElement || !string.Equals(node.Ref.LocalName, "img", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var source = node.Ref.GetAttribute("src");
        if (!TryGetOrLoadImageResource(node.Ref, source, out var imageResource) || imageResource is null)
        {
            return false;
        }

        return TryResolveReplacedElementRect(imageResource, styleMap, containingWidth, x, y, out image, out rect);
    }

    private static bool TryResolveInlineSvg(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect)
    {
        image = null;
        rect = default;

        // Same reasoning as the identical guard in TryResolveImage above - an <svg>'s own
        // generated-content pseudo aliases its host's LocalName and must not be treated as the SVG.
        if (node.Ref is IPseudoElement || !string.Equals(node.Ref.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryGetOrLoadInlineSvgResource(node.Ref, out var imageResource) || imageResource is null)
        {
            return false;
        }

        return TryResolveReplacedElementRect(imageResource, styleMap, containingWidth, x, y, out image, out rect);
    }

    private static bool TryResolveReplacedElementRect(CachedImageResource imageResource, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect)
    {
        image = null;
        rect = default;

        var width = ParseLength(styleMap, "width", containingWidth, float.NaN, allowAuto: true);
        var height = ParseLength(styleMap, "height", containingWidth, float.NaN, allowAuto: true);

        var naturalWidth = imageResource.NaturalWidth;
        var naturalHeight = imageResource.NaturalHeight;

        if (float.IsNaN(width) && float.IsNaN(height))
        {
            width = naturalWidth;
            height = naturalHeight;
        }
        else if (float.IsNaN(width) && !float.IsNaN(height) && naturalWidth > 0f)
        {
            width = (height / naturalHeight) * naturalWidth;
        }
        else if (!float.IsNaN(width) && float.IsNaN(height) && naturalHeight > 0f)
        {
            height = (width / naturalWidth) * naturalHeight;
        }

        if (float.IsNaN(width) || float.IsNaN(height) || width <= 0f || height <= 0f)
        {
            width = naturalWidth;
            height = naturalHeight;
        }

        image = new RenderedImage(imageResource.Bytes, (int)Math.Max(1, Math.Round(width)), (int)Math.Max(1, Math.Round(height)), imageResource.MimeType);
        rect = new RenderRect(x, y, width, height);
        return true;
    }

    private static bool TryGetOrLoadInlineSvgResource(IElement element, out CachedImageResource? imageResource)
    {
        if (s_inlineSvgCacheByElement.TryGetValue(element, out var cached))
        {
            imageResource = cached;
            return true;
        }

        if (!TryRasterizeInlineSvgElement(element, out imageResource) || imageResource is null)
        {
            return false;
        }

        s_inlineSvgCacheByElement.AddOrUpdate(element, imageResource);
        return true;
    }

    private static bool TryRasterizeInlineSvgElement(IElement element, out CachedImageResource? imageResource)
    {
        imageResource = null;

        // The already-parsed element is walked directly - AngleSharp parsed this SVG once, as
        // part of the host document, and it is never serialized back to text and re-parsed. Only
        // the SVG's own presentation attributes/style apply - page CSS never cascades into it.
        if (!SvgRasterizer.TryRasterizeElement(element, out var pngBytes, out var naturalWidth, out var naturalHeight))
        {
            return false;
        }

        imageResource = new CachedImageResource(pngBytes, "image/png", naturalWidth, naturalHeight);
        return true;
    }

    private static bool TryGetOrLoadImageResource(IElement element, string? source, out CachedImageResource? imageResource)
    {
        imageResource = null;

        return !string.IsNullOrWhiteSpace(source) &&
               TryGetOrLoadCachedResource(element, source.Trim(), TryLoadImageResource, out imageResource);
    }

    /// <summary>
    /// Resolves a `background-image: url(...)` reference to a decoded, cached image, sharing the
    /// same per-document <see cref="DocumentImageCache"/> (keyed by the same trimmed URL string) as
    /// an `&lt;img src&gt;` reference to the exact same URL - one fetch serves both. Unlike
    /// <see cref="TryGetOrLoadImageResource"/>, there is no <see cref="ILoadableElement"/> download
    /// already in flight to consult first: a CSS property value has no DOM-level load of its own,
    /// so <see cref="TryLoadBackgroundImageResource"/> always resolves the URL itself (data URI, or
    /// the network - and only the network - when the browsing context has an
    /// <see cref="IDocumentLoader"/> configured), mirroring how <c>@font-face url()</c> sources are
    /// handled in <c>FontFaceLoader</c>.
    /// </summary>
    private static bool TryGetOrLoadBackgroundImageResource(IElement element, string url, out CachedImageResource? imageResource) =>
        TryGetOrLoadCachedResource(element, url.Trim(), TryLoadBackgroundImageResource, out imageResource);

    private delegate bool ImageResourceLoader(IElement element, string source, out CachedImageResource? imageResource);

    private static bool TryGetOrLoadCachedResource(IElement element, string cacheKey, ImageResourceLoader loader, out CachedImageResource? imageResource)
    {
        imageResource = null;
        var cache = GetImageCache(element);

        if (cache is not null)
        {
            lock (cache.Resources)
            {
                if (cache.Resources.TryGetValue(cacheKey, out imageResource))
                {
                    return imageResource is not null;
                }
            }
        }

        var loaded = loader(element, cacheKey, out imageResource);

        if (cache is not null)
        {
            lock (cache.Resources)
            {
                cache.Resources[cacheKey] = loaded ? imageResource : null;
            }
        }

        return loaded;
    }

    private static DocumentImageCache? GetImageCache(IElement element)
    {
        var owner = element.Owner;
        return owner is null ? null : s_imageCacheByDocument.GetValue(owner, static _ => new DocumentImageCache());
    }

    private static bool TryLoadImageResource(IElement element, string? source, out CachedImageResource? imageResource)
    {
        imageResource = null;

        byte[]? bytes = null;
        string? mimeType = null;

        if (element is ILoadableElement loadableElement && loadableElement.CurrentDownload is { Task: not null } download)
        {
            IResponse? response;

            try
            {
                response = download.Task.GetAwaiter().GetResult();
            }
            catch
            {
                response = null;
            }

            if (response?.Content is not null)
            {
                using var sourceStream = response.Content;
                using var memoryStream = new MemoryStream();
                sourceStream.CopyTo(memoryStream);
                bytes = memoryStream.ToArray();
                mimeType = response.Headers?.TryGetValue("Content-Type", out var contentType) == true && !string.IsNullOrWhiteSpace(contentType)
                    ? contentType
                    : "image/unknown";
            }
        }

        if (bytes is null && TryParseDataUri(source, out var dataUriBytes, out var dataUriMimeType))
        {
            bytes = dataUriBytes;
            mimeType = dataUriMimeType;
        }

        return bytes is not null && TryDecodeImageBytes(bytes, mimeType, out imageResource);
    }

    /// <summary>
    /// Resolves a `background-image: url(...)` value: a data URI decodes inline, exactly like an
    /// `&lt;img src&gt;` data URI; anything else is only ever fetched when the browsing context was
    /// configured with an <see cref="IDocumentLoader"/> - matching how images and `@font-face`
    /// sources are already handled, a renderer should not silently reach out to the network. The
    /// fetch is synchronous (blocking on the download's Task) so the resource is
    /// available - loaded and decoded - within the very same <c>BuildDisplayList</c> call that
    /// requested it, the same way an `&lt;img&gt;`'s already-in-flight <c>CurrentDownload</c> is
    /// awaited: this renderer has no separate "repaint later once loaded" pass to defer to.
    /// </summary>
    private static bool TryLoadBackgroundImageResource(IElement element, string url, out CachedImageResource? imageResource)
    {
        imageResource = null;

        if (TryParseDataUri(url, out var dataUriBytes, out var dataUriMimeType))
        {
            return dataUriBytes is not null && TryDecodeImageBytes(dataUriBytes, dataUriMimeType, out imageResource);
        }

        var document = element.Owner;
        var loader = document?.Context.GetService<IDocumentLoader>();

        if (document is null || loader is null)
        {
            return false;
        }

        try
        {
            var target = new Url(document.BaseUrl, url);
            var download = loader.FetchAsync(DocumentRequest.Get(target, source: document, referer: document.BaseUri));
            var response = download.Task.GetAwaiter().GetResult();

            if (response?.Content is null)
            {
                return false;
            }

            using var content = response.Content;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();

            if (bytes.Length == 0)
            {
                return false;
            }

            var mimeType = response.Headers?.TryGetValue("Content-Type", out var contentType) == true && !string.IsNullOrWhiteSpace(contentType)
                ? contentType
                : "image/unknown";

            return TryDecodeImageBytes(bytes, mimeType, out imageResource);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeImageBytes(byte[] bytes, string? mimeType, out CachedImageResource? imageResource)
    {
        imageResource = null;

        if (bytes.Length == 0)
        {
            return false;
        }

        if (SvgRasterizer.IsSvg(bytes))
        {
            if (!SvgRasterizer.TryRasterizeMarkup(bytes, out var rasterizedBytes, out var svgNaturalWidth, out var svgNaturalHeight))
            {
                return false;
            }

            imageResource = new CachedImageResource(rasterizedBytes, "image/png", svgNaturalWidth, svgNaturalHeight);
            return true;
        }

        using var skImage = SKImage.FromEncodedData(bytes);
        if (skImage is null)
        {
            return false;
        }

        imageResource = new CachedImageResource(bytes, mimeType ?? "image/unknown", skImage.Width, skImage.Height);
        return true;
    }

    private static bool TryParseDataUri(string? source, out byte[]? bytes, out string? mimeType)
    {
        bytes = null;
        mimeType = null;

        if (string.IsNullOrWhiteSpace(source) || !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = source.IndexOf(',');
        if (commaIndex <= 5)
        {
            return false;
        }

        var header = source[5..commaIndex];
        var isBase64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var mediaType = isBase64 ? header[..^7] : header;
        var payload = source[(commaIndex + 1)..];

        if (string.IsNullOrEmpty(mediaType))
        {
            mimeType = "image/unknown";
        }
        else if (mediaType.StartsWith(";", StringComparison.Ordinal))
        {
            mimeType = "image/unknown";
        }
        else
        {
            mimeType = mediaType;
        }

        try
        {
            bytes = isBase64
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch
        {
            bytes = null;
            return false;
        }

        return bytes.Length > 0;
    }

    /// <summary>
    /// Identifies which kind of default styling/content-painting a form control needs. `None`
    /// covers every non-form element, so callers can skip all of the form-control machinery with a
    /// single check. `Hidden` (`input[type=hidden]`) never lays out or paints at all - handled by
    /// an early return in <c>LayoutElement</c> rather than here, since AngleSharp.Css's UA
    /// stylesheet does not give it `display: none` the way a real browser's does (every `&lt;input&gt;`
    /// type computes to plain `inline-block` in this AngleSharp.Css version, verified empirically).
    /// </summary>
    private enum FormControlKind
    {
        /// <summary>Not a form control.</summary>
        None,

        /// <summary>
        /// A single-line text value box: <c>text</c>, and every other textual `&lt;input&gt;` type this
        /// renderer treats identically (`search`, `url`, `tel`, `email`, `number`, the date/time
        /// family, and any unrecognized/absent type - matching the HTML spec's "unknown type falls
        /// back to text" rule), plus `password` (masked with bullet characters).
        /// </summary>
        TextLike,

        /// <summary>`input[type=checkbox]`.</summary>
        Checkbox,

        /// <summary>`input[type=radio]`.</summary>
        Radio,

        /// <summary>`input[type=color]`.</summary>
        Color,

        /// <summary>
        /// A clickable, label-centered button box: `&lt;button&gt;` and
        /// `input[type=button|submit|reset]`.
        /// </summary>
        Button,

        /// <summary>`&lt;select&gt;` - shows only its selected `&lt;option&gt;`'s text, never every option.</summary>
        Select,

        /// <summary>`&lt;textarea&gt;` - gets the same box chrome as a text input, but keeps its real
        /// child text node flowing through the ordinary block child-layout path for its content.</summary>
        TextArea,

        /// <summary>`input[type=hidden]` - never laid out or painted; see the type's own remarks.</summary>
        Hidden,
    }

    /// <summary>
    /// A common browser checkbox/radio accent color (Chrome/Edge's default `accent-color`),
    /// approximated as a fixed constant - this renderer does not parse the CSS `accent-color`
    /// property itself, a deliberate scope cut for a rarely-overridden value.
    /// </summary>
    private static readonly RenderColor FormControlAccentColor = new(26, 115, 232, 255);

    /// <summary>
    /// The bundled sans-serif font's own ascent, as a fraction of font-size, measured empirically
    /// via <c>SKPaint.FontMetrics</c> (14.8515625px ascent at font-size 16 -> 0.928) - used to
    /// center a form control's single-line text on its real visual ink rather than on the taller
    /// CSS line-height box. See <see cref="PaintFormControl"/> for why: the "baseline at the bottom
    /// of the line-height box" convention the rest of this renderer uses for stacked body text
    /// leaves no room for descenders/leading below the baseline, which is invisible across many
    /// stacked lines but visibly pushed single-line form-control text toward the bottom of its box.
    /// </summary>
    private const float FormControlTextAscentRatio = 0.928f;

    /// <summary>Companion to <see cref="FormControlTextAscentRatio"/> - measured descent 3.7734375px at font-size 16 -> 0.236.</summary>
    private const float FormControlTextDescentRatio = 0.236f;

    /// <summary>
    /// "⌄" (DOWNWARDS ARROWHEAD, a thin chevron) rather than a custom-drawn triangle - <c>
    /// DisplayList</c> has no generic polygon-fill primitive, so any dropdown indicator has to come
    /// from a real font glyph. A plain ASCII "v" (this constant's original value) is guaranteed to
    /// exist in every bundled font but reads as a literal letter, not an icon; U+2304 was verified
    /// - by rendering several candidate glyphs and inspecting the actual pixels, not assumed from a
    /// coverage table - to exist in the bundled DejaVu Sans as a proper thin chevron shape close to
    /// a real browser's own native indicator, unlike, for example, U+23D7 which the bundled font
    /// has no glyph for at all (renders as a hollow "tofu" box). Shared between the default-width
    /// measurement in <see cref="ApplyFormControlDefaults"/> and the actual paint in
    /// <see cref="PaintFormControl"/> so the two can never disagree about how much room it needs.
    /// </summary>
    private const string FormControlSelectArrowGlyph = "⌄";

    /// <summary>
    /// A downward correction applied only to <see cref="FormControlSelectArrowGlyph"/>'s own
    /// baseline, on top of the ordinary text baseline every other form-control label already
    /// centers on via <see cref="FormControlTextAscentRatio"/>/<see cref="FormControlTextDescentRatio"/>.
    /// Those two ratios approximate *ordinary latin text's* ink extents (built from measuring a
    /// word like "Second"), but "⌄" is a short symbol glyph whose own ink sits much closer to the
    /// baseline - measured via <c>SKPaint.MeasureText</c>'s tight bounding box at font-size 16:
    /// "⌄" spans roughly 6px above the baseline to 1px below it (a 7px-tall glyph, vertical ink
    /// center ~2.5px above baseline), while "Second" spans roughly 14px above to 2px below (a
    /// 16px-tall run, vertical ink center ~6px above baseline). Painting both at the *same*
    /// baseline - which is what correctly centers the text label - therefore left the arrow's own,
    /// much shorter ink sitting visibly low in the box: a real, confirmed bug, not a hypothetical
    /// one (caught from a direct visual report, not assumed). This ratio is the measured difference
    /// between those two ink-centers as a fraction of font-size (~-3px at font-size 16, i.e.
    /// -3/16), shifting the arrow's baseline up just enough that its own ink centers where the
    /// text's ink already does.
    /// </summary>
    private const float FormControlSelectArrowVerticalOffsetRatio = -0.19f;

    /// <summary>
    /// The width, in pixels, of a focused text-like input's caret - a fixed value rather than a
    /// font-size fraction, matching how a real browser's own caret stays a thin ~1-2px line
    /// regardless of font size rather than scaling with it.
    /// </summary>
    private const float FormControlCaretWidth = 1.5f;

    /// <summary>
    /// One full fade cycle of the caret's blink animation, in milliseconds - see
    /// <see cref="PaintFormControlCaret"/> for how this is used.
    /// </summary>
    private const double FormControlCaretBlinkPeriodMs = 1000d;

    private static FormControlKind ResolveFormControlKind(IElement element)
    {
        // A ::before/::after pseudo-element's LocalName/GetAttribute proxy straight through to its
        // host (PseudoElement.cs, in AngleSharp.Css) - an <input>/<select>/<textarea>/<button>'s own
        // generated-content pseudo would otherwise be misidentified as that same form control and
        // get its default chrome/value painted a second time.
        if (element is IPseudoElement)
        {
            return FormControlKind.None;
        }

        var tagName = element.LocalName;

        if (string.Equals(tagName, "textarea", StringComparison.OrdinalIgnoreCase))
        {
            return FormControlKind.TextArea;
        }

        if (string.Equals(tagName, "select", StringComparison.OrdinalIgnoreCase))
        {
            return FormControlKind.Select;
        }

        if (string.Equals(tagName, "button", StringComparison.OrdinalIgnoreCase))
        {
            return FormControlKind.Button;
        }

        if (!string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase))
        {
            return FormControlKind.None;
        }

        var type = element.GetAttribute("type")?.Trim().ToLowerInvariant();

        return type switch
        {
            "checkbox" => FormControlKind.Checkbox,
            "radio" => FormControlKind.Radio,
            "color" => FormControlKind.Color,
            "button" or "submit" or "reset" => FormControlKind.Button,
            "hidden" => FormControlKind.Hidden,
            _ => FormControlKind.TextLike,
        };
    }

    /// <summary>
    /// Fills in the browser-like default declarations a form control needs (border, padding,
    /// background, and a natural size) directly into its style map, but only for whichever
    /// individual properties the author has not already set - exactly the same "synthesize the UA
    /// default only where the cascade left a gap" approach already used for
    /// `list-style-type`/`list-style-position`, just for a much larger set of properties at once.
    /// This is what makes the defaults "somewhat overridable, like in real browsers": setting
    /// `border`, `background-color`, `padding`, or an explicit `width`/`height` in CSS pre-empts
    /// the corresponding default below exactly as it would in a real browser's form-control
    /// rendering, while every property the author leaves alone still gets a sensible UA look
    /// instead of rendering as an invisible, zero-size box (this renderer's actual previous
    /// behavior for every form control, since neither this renderer nor the AngleSharp.Css UA
    /// stylesheet gave them any border/padding/background/size at all).
    /// </summary>
    private static void ApplyFormControlDefaults(
        FormControlKind kind,
        IElement element,
        Dictionary<string, string> styleMap,
        RenderTextStyle textStyle,
        LayoutContext context)
    {
        void SetDefault(string property, string value)
        {
            if (!styleMap.TryGetValue(property, out var existing) || string.IsNullOrWhiteSpace(existing))
            {
                styleMap[property] = value;
            }
        }

        // Unlike border-width/style/color (which GetPropertyValue reports as a genuinely empty
        // string when nothing in the cascade set them, verified empirically), AngleSharp.Css always
        // resolves a concrete border-*-radius computed value - "0px" - even for a plain, completely
        // unstyled element. A bare SetDefault would see that "0px" as "the author already set this"
        // and never apply the radio's circular default at all, so radius defaults specifically also
        // treat the computed zero-length initial value as still-unset.
        void SetDefaultRadius(string property, string value)
        {
            if (!styleMap.TryGetValue(property, out var existing) ||
                string.IsNullOrWhiteSpace(existing) ||
                existing.Trim() is "0px" or "0" or "0%")
            {
                styleMap[property] = value;
            }
        }

        void SetDefaultBorder()
        {
            foreach (var side in new[] { "top", "right", "bottom", "left" })
            {
                SetDefault($"border-{side}-width", "1px");
                SetDefault($"border-{side}-style", "solid");
                SetDefault($"border-{side}-color", "#767676");
            }
        }

        void SetDefaultTextPadding()
        {
            SetDefault("padding-top", "2px");
            SetDefault("padding-bottom", "2px");
            SetDefault("padding-left", "4px");
            SetDefault("padding-right", "4px");
        }

        // A single line's worth of content height - the box's own vertical size, not to be
        // confused with the ascent/descent-based metric PaintFormControl separately uses to place
        // where the *baseline* sits within that box (see FormControlTextAscentRatio/DescentRatio).
        // Sizing the box itself off the full CSS line-height (rather than an independent constant
        // like the previous 1.2f) keeps a text-like control's default box roomy enough for
        // normal text leading, matching a real browser's own default input height reasonably
        // closely.
        var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;

        switch (kind)
        {
            case FormControlKind.TextLike:
                SetDefaultBorder();
                SetDefaultTextPadding();
                SetDefault("background-color", "#ffffff");
                SetDefault("width", "150px");
                SetDefault("height", FormatPixelValue(lineHeight));
                break;

            case FormControlKind.Select:
            {
                SetDefaultBorder();
                SetDefaultTextPadding();
                // A light gray, button-like background (not the white a text-like input gets) -
                // matching how a real browser's own native <select> chrome looks closer to a
                // button than to a text box.
                SetDefault("background-color", "#e8e8e8");
                SetDefault("height", FormatPixelValue(lineHeight));

                // Shrinks to fit its selected option's own label plus room for the dropdown arrow -
                // the same shrink-to-fit idea a <button> uses for its own label - rather than a
                // text-like input's fixed 150px default: a native <select> is never that wide
                // unless its own content actually demands it.
                var selectLabel = ResolveFormControlLabel(FormControlKind.Select, element);
                var selectLabelWidth = MeasureTextWidth(context, selectLabel, textStyle);
                var arrowWidth = MeasureTextWidth(context, FormControlSelectArrowGlyph, textStyle);
                var arrowGap = textStyle.FontSize * 0.5f;
                SetDefault("width", FormatPixelValue(Math.Max(30f, selectLabelWidth + arrowGap + arrowWidth)));
                break;
            }

            case FormControlKind.TextArea:
                SetDefaultBorder();
                SetDefaultTextPadding();
                SetDefault("background-color", "#ffffff");
                SetDefault("width", "150px");
                // Only a floor, not a cap: an auto-sized box's height is already
                // Math.Max(specifiedHeight, autoContentHeight) throughout this renderer, so a
                // <textarea> whose real content wraps past two lines still grows past this default
                // rather than clipping it - this default only guarantees the common "empty or
                // short" textarea still shows a multi-line box, like a browser's default 2 rows.
                SetDefault("height", FormatPixelValue(lineHeight * 2f));
                break;

            case FormControlKind.Color:
                SetDefaultBorder();
                SetDefault("padding-top", "2px");
                SetDefault("padding-bottom", "2px");
                SetDefault("padding-left", "2px");
                SetDefault("padding-right", "2px");
                SetDefault("background-color", "#ffffff");
                SetDefault("width", "36px");
                SetDefault("height", FormatPixelValue(lineHeight));
                break;

            case FormControlKind.Checkbox:
            case FormControlKind.Radio:
            {
                var boxSize = Math.Max(10f, textStyle.FontSize * 0.9f);
                SetDefaultBorder();
                SetDefault("background-color", "#ffffff");
                SetDefault("width", FormatPixelValue(boxSize));
                SetDefault("height", FormatPixelValue(boxSize));

                // Real UA stylesheets give a checkbox/radio a small default margin (Chromium's
                // html.css uses exactly these four values) that every other form control kind does
                // not get - without it, two adjacent checkboxes/radios in markup with no whitespace
                // between their tags (as this renderer's own gallery test and countless real forms
                // both write them) render flush against each other with no gap at all. Unlike
                // border/padding/background, AngleSharp.Css's UA stylesheet does not set any margin
                // on these elements (verified empirically: GetPropertyValue("margin-*") comes back
                // empty), so there is no upstream default to fall back to here.
                SetDefault("margin-top", "3px");
                SetDefault("margin-right", "3px");
                SetDefault("margin-bottom", "3px");
                SetDefault("margin-left", "4px");

                if (kind == FormControlKind.Radio)
                {
                    // Two things ParseCornerRadius needs accounted for, neither obvious from
                    // reading it in isolation: (1) it (and ResolveBoxStyle generally) only ever
                    // reads the four longhand border-*-radius keys, matching how AngleSharp.Css's
                    // own computed style populates the style map (see CreateStyleMap) - the
                    // `border-radius` shorthand itself is never consulted, so it has to be expanded
                    // here. (2) ParseCornerRadius's own ParseLengthValue call has no percentage
                    // handling at all (unlike the general ParseLength used for width/padding/etc.,
                    // which resolves a percentage against a containing dimension) - it only expects
                    // a plain pixel value, because AngleSharp.Css itself always pre-resolves a
                    // percentage border-radius to pixels before this renderer ever sees it. An
                    // injected "50%" string here would silently parse to 0 rather than a circle, so
                    // an already-resolved pixel value (half of this box's own default size) is
                    // written instead.
                    var cornerRadius = FormatPixelValue(boxSize / 2f);
                    SetDefaultRadius("border-top-left-radius", cornerRadius);
                    SetDefaultRadius("border-top-right-radius", cornerRadius);
                    SetDefaultRadius("border-bottom-right-radius", cornerRadius);
                    SetDefaultRadius("border-bottom-left-radius", cornerRadius);
                }

                break;
            }

            case FormControlKind.Button:
            {
                SetDefaultBorder();
                SetDefault("padding-top", "2px");
                SetDefault("padding-bottom", "2px");
                SetDefault("padding-left", "10px");
                SetDefault("padding-right", "10px");
                SetDefault("background-color", "#e8e8e8");
                SetDefault("height", FormatPixelValue(lineHeight));

                // A button shrinks to fit its own label, unlike the fixed-width text-like controls
                // above - measured now (rather than left to a general shrink-to-fit layout
                // algorithm this renderer does not otherwise have) using the exact same
                // ITextMeasurer layout already measures body text with.
                var label = ResolveFormControlLabel(FormControlKind.Button, element);
                var labelWidth = MeasureTextWidth(context, label, textStyle);
                SetDefault("width", FormatPixelValue(Math.Max(20f, labelWidth)));
                break;
            }
        }
    }

    private static string FormatPixelValue(float pixels) =>
        string.Create(CultureInfo.InvariantCulture, $"{pixels:0.##}px");

    /// <summary>
    /// Resolves the text a form control shows as its own content - a typed `value`, a button's
    /// label, or a select's currently-selected option - independently of normal child-text flow,
    /// since <see cref="PaintFormControl"/> paints it directly rather than relying on any child
    /// nodes being laid out (an `&lt;input&gt;` has none at all; a `&lt;select&gt;`'s/`&lt;button&gt;`'s are
    /// deliberately excluded from `orderedChildren` in `LayoutElement`).
    /// </summary>
    private static string ResolveFormControlLabel(FormControlKind kind, IElement element)
    {
        switch (kind)
        {
            case FormControlKind.Button:
            {
                if (string.Equals(element.LocalName, "button", StringComparison.OrdinalIgnoreCase))
                {
                    var text = NormalizeWhitespace(element.TextContent ?? string.Empty);
                    return text.Length > 0 ? text : "Button";
                }

                var value = element.GetAttribute("value");

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                return element.GetAttribute("type")?.Trim().ToLowerInvariant() switch
                {
                    "submit" => "Submit",
                    "reset" => "Reset",
                    _ => "Button",
                };
            }

            case FormControlKind.TextLike:
            {
                var value = element.GetAttribute("value") ?? string.Empty;

                // A password field's own value never paints as plain text, matching the one
                // input type where masking is part of the type's defining behavior rather than an
                // optional/overridable styling choice.
                return string.Equals(element.GetAttribute("type")?.Trim(), "password", StringComparison.OrdinalIgnoreCase) && value.Length > 0
                    ? new string('•', value.Length)
                    : value;
            }

            case FormControlKind.Select:
            {
                // The DOM's own last-`selected`-wins semantics (mirroring how a real <select>
                // resolves multiple `selected` attributes) - falling back to the first <option> when
                // none is marked selected, exactly like a browser's own initial-selection default.
                var options = element.Children.Where(child => string.Equals(child.LocalName, "option", StringComparison.OrdinalIgnoreCase)).ToList();
                var selected = options.LastOrDefault(option => option.HasAttribute("selected")) ?? options.FirstOrDefault();
                return selected is not null ? NormalizeWhitespace(selected.TextContent ?? string.Empty) : string.Empty;
            }

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Paints a form control's own visible content - typed/selected text, or a checkbox/radio's
    /// checked-state fill, or a color swatch - directly into <paramref name="displayList"/> at the
    /// same point <see cref="PaintListItemMarker"/> paints a list marker, so it lands after the
    /// box's own background/border (spliced in later at the already-captured
    /// <c>boxPaintInsertIndex</c>) but is otherwise unaffected by however tall the box's children
    /// end up making it - none of these controls have real children of their own to wait on.
    /// </summary>
    private static void PaintFormControl(
        DisplayList displayList,
        IElement element,
        FormControlKind kind,
        RenderTextStyle textStyle,
        LayoutContext context,
        float contentX,
        float contentY,
        float contentWidth,
        float contentHeight)
    {
        switch (kind)
        {
            case FormControlKind.TextLike:
            case FormControlKind.Select:
            case FormControlKind.Button:
            {
                var label = ResolveFormControlLabel(kind, element);

                // Centers the glyphs' own visual ink, not the CSS line box: LayoutWrappedText's
                // "content-box top plus one full line height" baseline convention places the
                // baseline as if every pixel of the line-height were ascent, with none left over
                // for descenders or leading - fine for stacked body-text lines (the next line's own
                // top absorbs the difference), but for a single line centered in a form control's
                // padded box it visibly pushed text toward the bottom. FormControlTextAscentRatio/
                // DescentRatio approximate the bundled sans-serif font's real metrics at a
                // representative size (measured via SKPaint.FontMetrics: ascent 14.85px, descent
                // 3.77px at font-size 16, i.e. ~0.928/~0.236 of the em) the same way
                // ParseVerticalAlign already approximates super/sub/middle offsets as fontSize
                // fractions rather than querying per-font metrics through ITextMeasurer (which only
                // ever exposes advance width, by design - see ITextMeasurer's own remarks). Computed
                // unconditionally (not just when there is a label) since a focused, empty TextLike
                // input still needs a baseline/left-edge position for its caret below.
                var visualTextHeight = textStyle.FontSize * (FormControlTextAscentRatio + FormControlTextDescentRatio);
                var verticalCenteringOffset = Math.Max(0f, (contentHeight - visualTextHeight) / 2f);
                var baselineY = contentY + verticalCenteringOffset + (textStyle.FontSize * FormControlTextAscentRatio) + textStyle.VerticalAlignOffset;
                var labelWidth = label.Length > 0 ? MeasureTextWidth(context, label, textStyle) : 0f;

                if (label.Length > 0)
                {
                    // A text input's typed value and a select's selected option are both
                    // left-aligned, matching how browsers show them; only a button's own label is
                    // centered in its box.
                    var labelX = kind == FormControlKind.Button
                        ? contentX + Math.Max(0f, (contentWidth - labelWidth) / 2f)
                        : contentX;

                    displayList.DrawText(label, labelX, baselineY, textStyle.Color, textStyle.FontSize, textStyle.FontFamily, textStyle.FontWeight, textStyle.IsItalic, letterSpacing: textStyle.LetterSpacing);

                    if (kind == FormControlKind.Select)
                    {
                        var arrowWidth = MeasureTextWidth(context, FormControlSelectArrowGlyph, textStyle);
                        var arrowX = contentX + Math.Max(labelWidth, contentWidth - arrowWidth);
                        var arrowBaselineY = baselineY + (textStyle.FontSize * FormControlSelectArrowVerticalOffsetRatio);
                        displayList.DrawText(FormControlSelectArrowGlyph, arrowX, arrowBaselineY, textStyle.Color, textStyle.FontSize, textStyle.FontFamily, textStyle.FontWeight, textStyle.IsItalic, letterSpacing: textStyle.LetterSpacing);
                    }
                }

                if (kind == FormControlKind.TextLike)
                {
                    PaintFormControlCaret(displayList, element, textStyle, contentX + labelWidth, baselineY);
                }

                break;
            }

            case FormControlKind.Checkbox:
            {
                if (element.HasAttribute("checked"))
                {
                    // Sized off the box's own smaller dimension so a checkbox whose width and
                    // height default to the same font-relative size (the common case) still gets a
                    // perfectly square fill even if either is overridden asymmetrically, and inset
                    // independently per axis so the fill is centered on both axes rather than only
                    // assuming a square box.
                    var size = Math.Max(0f, Math.Min(contentWidth, contentHeight) * 0.6f);
                    var insetX = (contentWidth - size) / 2f;
                    var insetY = (contentHeight - size) / 2f;
                    displayList.FillRect(new RenderRect(contentX + insetX, contentY + insetY, size, size), FormControlAccentColor);
                }

                break;
            }

            case FormControlKind.Radio:
            {
                if (element.HasAttribute("checked"))
                {
                    var size = Math.Max(0f, Math.Min(contentWidth, contentHeight) * 0.5f);
                    var insetX = (contentWidth - size) / 2f;
                    var insetY = (contentHeight - size) / 2f;
                    var radii = new RenderCornerRadii(
                        size / 2f, size / 2f, size / 2f, size / 2f,
                        size / 2f, size / 2f, size / 2f, size / 2f);
                    displayList.FillRect(new RenderRect(contentX + insetX, contentY + insetY, size, size), FormControlAccentColor, radii);
                }

                break;
            }

            case FormControlKind.Color:
            {
                // The swatch is painted as its own fill, independent of the box's own
                // background-color (already defaulted to white above, as the swatch's "frame") -
                // deliberately not overridable via CSS background-color, matching how a real
                // browser's native color swatch ignores it too; only the frame around it (border,
                // padding, size) goes through the ordinarily-overridable box model. It fills the
                // entire content box (rather than a fixed line-height-tall rect within it) so it is
                // always centered regardless of how tall the box's own content height ends up being.
                var color = ParseColor(element.GetAttribute("value"), RenderColor.Black);
                displayList.FillRect(new RenderRect(contentX, contentY, contentWidth, contentHeight), color);
                break;
            }
        }
    }

    /// <summary>
    /// Paints a text-insertion caret for a focused <see cref="FormControlKind.TextLike"/> input,
    /// right after its current value (or at the content edge, for an empty one) - this renderer
    /// has no concept of a selection/cursor offset within an input's own value (AngleSharp does
    /// not track one either), so the caret always sits at the end of the text, matching by far the
    /// most common "user is appending" case a static render would want to depict. Real focus comes
    /// straight from the DOM (<see cref="IElement.IsFocused"/>) rather than being tracked
    /// separately by this renderer - AngleSharp.Css's own `:focus` pseudo-class forcing already
    /// delegates to it (see AGENTS.md's `:hover` section for the equivalent story with
    /// <c>SetPseudoClass</c>), so a plain <c>element.Focus()</c> or
    /// <c>element.SetPseudoClass("focus")</c> is already enough to make a caret appear, with no
    /// extra wiring needed here. `&lt;textarea&gt;` is a deliberate, documented scope cut - unlike
    /// a single-line input's fixed baseline, its caret position would depend on which wrapped line
    /// its real child text node currently ends on, which this renderer does not track.
    /// </summary>
    private static void PaintFormControlCaret(
        DisplayList displayList,
        IElement element,
        RenderTextStyle textStyle,
        float caretX,
        float baselineY)
    {
        if (!element.IsFocused)
        {
            return;
        }

        var alpha = 1f;

        // The fade needs a live clock to animate against; without one (a plain, non-interactive
        // RenderToPng/BuildDisplayList call, or a harness whose AdvanceTime was simply never
        // called) it paints fully opaque rather than frozen at some arbitrary phase - the same
        // "no harness means no animation, not a stuck mid-animation frame" fallback `transition`/
        // `animation` already use (see ApplyActiveTransitionAndAnimationOverrides).
        if (element.Owner is not null && element.Owner.Context.TryGetDomHarness(out var harness) && harness is not null)
        {
            // A smooth cosine "breathe" - continuously sweeping through fully visible, partway
            // faded, and fully invisible - rather than a hard on/off blink, so sampling the virtual
            // clock at any instant shows a plausible in-between frame instead of only ever a flat
            // "on" or "off" pixel value.
            var cyclePosition = harness.CurrentTime.TotalMilliseconds % FormControlCaretBlinkPeriodMs / FormControlCaretBlinkPeriodMs;
            alpha = (float)((Math.Cos(cyclePosition * 2 * Math.PI) + 1) / 2);
        }

        var caretTop = baselineY - (textStyle.FontSize * FormControlTextAscentRatio);
        var caretBottom = baselineY + (textStyle.FontSize * FormControlTextDescentRatio);
        var alphaByte = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        var caretColor = new RenderColor(textStyle.Color.R, textStyle.Color.G, textStyle.Color.B, alphaByte);

        displayList.FillRect(new RenderRect(caretX, caretTop, FormControlCaretWidth, caretBottom - caretTop), caretColor);
    }

    /// <summary>
    /// Paints a `display: list-item` element's marker (bullet or number) and, for
    /// `list-style-position: inside`, returns a copy of <paramref name="textStyle"/> whose
    /// <see cref="RenderTextStyle.TextIndent"/> is widened to make room for it on the first line.
    /// Safe to widen locally: <see cref="ResolveTextStyle"/> never falls back to an inherited
    /// <c>TextIndent</c> (each element re-derives its own from its own style map, defaulting to
    /// 0), so this local adjustment cannot leak into nested descendants' own indent.
    /// </summary>
    private static RenderTextStyle PaintListItemMarker(
        DisplayList displayList,
        IElement element,
        Dictionary<string, string> styleMap,
        RenderTextStyle textStyle,
        LayoutContext context,
        float borderBoxX,
        float contentX,
        float contentY)
    {
        var listStyleType = ResolveListStyleType(styleMap);

        if (string.Equals(listStyleType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return textStyle;
        }

        var isInside = styleMap.TryGetValue("list-style-position", out var positionValue) &&
            string.Equals(positionValue.Trim(), "inside", StringComparison.OrdinalIgnoreCase);

        // Aligns the marker with the first line of the li's own content, assuming that content
        // starts as normal inline text at the default vertical-align - the common case, but an
        // approximation when a li's first child is itself a block (its own first line may sit at
        // a different offset than this).
        var markerBaselineY = contentY + (textStyle.FontSize * textStyle.LineHeightMultiplier) + textStyle.VerticalAlignOffset;

        // Not derived from any spec metric - a small, fixed visual gap between the marker and the
        // content that follows it, matching the general proportions browsers use.
        const float MarkerGap = 6f;

        if (IsShapeListStyleType(listStyleType))
        {
            var markerSize = Math.Max(2f, textStyle.FontSize * 0.35f);
            var markerTop = markerBaselineY - (textStyle.FontSize * 0.68f);
            var markerLeft = isInside ? contentX : borderBoxX - MarkerGap - markerSize;
            var markerRect = new RenderRect(markerLeft, markerTop, markerSize, markerSize);
            var circularRadii = new RenderCornerRadii(
                markerSize / 2f, markerSize / 2f, markerSize / 2f, markerSize / 2f,
                markerSize / 2f, markerSize / 2f, markerSize / 2f, markerSize / 2f);

            switch (listStyleType)
            {
                case "circle":
                    displayList.StrokeRoundedRect(markerRect, textStyle.Color, Math.Max(1f, textStyle.FontSize * 0.08f), circularRadii);
                    break;
                case "square":
                    displayList.FillRect(markerRect, textStyle.Color);
                    break;
                default: // disc
                    displayList.FillRect(markerRect, textStyle.Color, circularRadii);
                    break;
            }

            return isInside ? textStyle with { TextIndent = textStyle.TextIndent + markerSize + MarkerGap } : textStyle;
        }

        var markerText = FormatListMarkerText(ResolveListItemOrdinal(element), listStyleType);
        var markerWidth = MeasureTextWidth(context, markerText, textStyle);
        var markerX = isInside ? contentX : borderBoxX - MarkerGap - markerWidth;

        displayList.DrawText(markerText, markerX, markerBaselineY, textStyle.Color, textStyle.FontSize, textStyle.FontFamily, textStyle.FontWeight, textStyle.IsItalic, letterSpacing: textStyle.LetterSpacing);

        return isInside ? textStyle with { TextIndent = textStyle.TextIndent + markerWidth + MarkerGap } : textStyle;
    }

    /// <summary>
    /// Whether an element's `overflow` clips its content. `scroll` and `auto` are treated the
    /// same as `hidden`: a rendered PNG has no scrollbars or interactivity, so anything a browser
    /// would let the user scroll to reveal is, here, simply clipped away like `hidden`.
    /// </summary>
    private static bool ShouldClipOverflow(Dictionary<string, string> styleMap) =>
        IsClippingOverflowValue(ResolveOverflowAxis(styleMap, "overflow-x")) ||
        IsClippingOverflowValue(ResolveOverflowAxis(styleMap, "overflow-y"));

    /// <summary>
    /// Resolves one overflow axis. AngleSharp.Css's `overflow` shorthand decomposes into
    /// `overflow-x`/`overflow-y` in its own computed style, so the longhand can always be
    /// read directly.
    /// </summary>
    private static string ResolveOverflowAxis(Dictionary<string, string> styleMap, string longhandProperty) =>
        styleMap.TryGetValue(longhandProperty, out var axisValue) && !string.IsNullOrWhiteSpace(axisValue)
            ? axisValue.Trim().ToLowerInvariant()
            : "visible";

    private static bool IsClippingOverflowValue(string overflowValue) =>
        overflowValue is "hidden" or "scroll" or "auto";

    // AngleSharp.Css's UA stylesheet sets list-style-type: disc directly on ol/ul/dir/menu/dd
    // (fixed upstream), so this default is dead for the common case - it is still needed, and
    // still correct, for an element given `display: list-item` explicitly (any other tag), which
    // that UA rule does not cover and which still computes an empty list-style-type - confirmed
    // empirically, not assumed.
    private static string ResolveListStyleType(Dictionary<string, string> styleMap) =>
        styleMap.TryGetValue("list-style-type", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "disc";

    private static bool IsShapeListStyleType(string listStyleType) =>
        listStyleType is "disc" or "circle" or "square";

    /// <summary>
    /// Formats an ordinal into the marker text for a numbered `list-style-type`. Any keyword this
    /// renderer does not specifically implement (and plain `decimal`) falls back to a decimal
    /// number, matching how browsers treat an unsupported `list-style-type` value.
    /// </summary>
    private static string FormatListMarkerText(int ordinal, string listStyleType) => listStyleType switch
    {
        "decimal-leading-zero" => (ordinal is >= 0 and < 10 ? "0" + ordinal.ToString(CultureInfo.InvariantCulture) : ordinal.ToString(CultureInfo.InvariantCulture)) + ".",
        "lower-alpha" or "lower-latin" => FormatAlphaListMarker(ordinal, upper: false) + ".",
        "upper-alpha" or "upper-latin" => FormatAlphaListMarker(ordinal, upper: true) + ".",
        "lower-roman" => FormatRomanListMarker(ordinal, upper: false) + ".",
        "upper-roman" => FormatRomanListMarker(ordinal, upper: true) + ".",
        _ => ordinal.ToString(CultureInfo.InvariantCulture) + ".",
    };

    /// <summary>
    /// Formats a 1-based ordinal as a base-26 letter sequence (a, b, ..., z, aa, ab, ...),
    /// matching `list-style-type: lower-alpha`/`upper-alpha`.
    /// </summary>
    private static string FormatAlphaListMarker(int ordinal, bool upper)
    {
        if (ordinal < 1)
        {
            return ordinal.ToString(CultureInfo.InvariantCulture);
        }

        var baseChar = upper ? 'A' : 'a';
        var chars = new Stack<char>();
        var remaining = ordinal;

        while (remaining > 0)
        {
            remaining--;
            chars.Push((char)(baseChar + (remaining % 26)));
            remaining /= 26;
        }

        return new string(chars.ToArray());
    }

    private static readonly (int Value, string Symbol)[] s_romanNumeralValues =
    [
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    ];

    /// <summary>
    /// Formats an ordinal as a Roman numeral, matching `list-style-type: lower-roman`/`upper-roman`.
    /// Roman numerals have no standard representation outside 1-3999, so values outside that range
    /// fall back to a plain decimal number, matching typical browser behavior.
    /// </summary>
    private static string FormatRomanListMarker(int ordinal, bool upper)
    {
        if (ordinal is < 1 or > 3999)
        {
            return ordinal.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder();
        var remaining = ordinal;

        foreach (var (value, symbol) in s_romanNumeralValues)
        {
            while (remaining >= value)
            {
                builder.Append(symbol);
                remaining -= value;
            }
        }

        var result = builder.ToString();
        return upper ? result : result.ToLowerInvariant();
    }

    /// <summary>
    /// Resolves a `&lt;li&gt;`'s 1-based ordinal from its position among its parent's direct
    /// `&lt;li&gt;` children in document order - not from a render-tree traversal order, which
    /// can reorder for z-index/absolute positioning, and not from any generic CSS counter, since
    /// only the common `&lt;ol&gt;`/`&lt;li&gt;` counting model (`start`, `reversed`, per-item
    /// `value`) is implemented. A `&lt;li&gt;` outside any `&lt;ol&gt;`/`&lt;ul&gt;` still resolves
    /// to 1, matching how browsers render a bare `&lt;li&gt;`.
    /// </summary>
    private static int ResolveListItemOrdinal(IElement liElement)
    {
        var parent = liElement.ParentElement;

        if (parent is null)
        {
            return 1;
        }

        var isOrderedList = string.Equals(parent.LocalName, "ol", StringComparison.OrdinalIgnoreCase);
        var reversed = isOrderedList && parent.HasAttribute("reversed");
        var counter = isOrderedList && int.TryParse(parent.GetAttribute("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitStart)
            ? explicitStart
            : reversed
                ? parent.Children.Count(c => string.Equals(c.LocalName, "li", StringComparison.OrdinalIgnoreCase))
                : 1;

        foreach (var child in parent.Children)
        {
            if (!string.Equals(child.LocalName, "li", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(child.GetAttribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var overrideValue))
            {
                counter = overrideValue;
            }

            if (ReferenceEquals(child, liElement))
            {
                return counter;
            }

            counter += reversed ? -1 : 1;
        }

        return counter;
    }

    private static void PaintTextShadows(DisplayList displayList, IReadOnlyList<RenderTextShadow> shadows, string text, float x, float y, RenderTextStyle textStyle)
    {
        if (shadows.Count == 0 || string.IsNullOrEmpty(text))
        {
            return;
        }

        // The first-listed shadow paints topmost among shadows (though always behind the text
        // itself), so shadows are added back-to-front: last-authored first, first-authored last.
        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            var shadow = shadows[i];
            displayList.DrawTextShadow(text, x + shadow.OffsetX, y + shadow.OffsetY, shadow.Color, textStyle.FontSize, textStyle.FontFamily, textStyle.FontWeight, textStyle.IsItalic, shadow.BlurRadius, textStyle.LetterSpacing);
        }
    }

    private static void PaintBoxShadows(DisplayList displayList, IReadOnlyList<RenderBoxShadow> shadows, float x, float y, float width, float height, RenderCornerRadii radii)
    {
        if (shadows.Count == 0 || width <= 0f || height <= 0f)
        {
            return;
        }

        var clampedRadii = radii.ClampToBox(width, height);
        var borderBoxRect = new RenderRect(x, y, width, height);

        // The first-listed shadow paints topmost among shadows (though always behind the box
        // itself), so shadows are added back-to-front: last-authored first, first-authored last.
        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            displayList.DrawBoxShadow(borderBoxRect, clampedRadii, shadows[i]);
        }
    }

    private static void PaintBackground(DisplayList displayList, RenderPaint paint, float x, float y, float width, float height, RenderCornerRadii radii = default)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var clampedRadii = radii.ClampToBox(width, height);

        if (paint is RenderColorPaint colorPaint)
        {
            if (colorPaint.Color.A == 0)
            {
                return;
            }

            displayList.FillRect(new RenderRect(x, y, width, height), colorPaint.Color, clampedRadii);
            return;
        }

        if (paint is RenderGradientPaint or RenderImagePaint)
        {
            displayList.FillRect(new RenderRect(x, y, width, height), paint, clampedRadii);
        }
    }

    private static void PaintBorder(DisplayList displayList, RenderColor color, float x, float y, float width, float height, EdgeSizes border, RenderCornerRadii radii = default)
    {
        if (color.A == 0 || width <= 0f || height <= 0f)
        {
            return;
        }

        var clampedRadii = radii.ClampToBox(width, height);

        // A uniform-width border with rounded corners can be drawn as a single stroked ring; a
        // border whose edges differ in width has no single stroke width to give the ring, so it
        // falls back to the un-rounded four-rectangle path (a documented limitation - mixed-width
        // rounded borders are rare enough not to warrant a per-edge rounded-quad implementation).
        if (!clampedRadii.IsZero && border.Top > 0f && border.Top == border.Right && border.Top == border.Bottom && border.Top == border.Left)
        {
            var half = border.Top / 2f;
            var strokeRect = new RenderRect(x + half, y + half, width - border.Top, height - border.Top);
            var strokeRadii = new RenderCornerRadii(
                Math.Max(0f, clampedRadii.TopLeftX - half), Math.Max(0f, clampedRadii.TopLeftY - half),
                Math.Max(0f, clampedRadii.TopRightX - half), Math.Max(0f, clampedRadii.TopRightY - half),
                Math.Max(0f, clampedRadii.BottomRightX - half), Math.Max(0f, clampedRadii.BottomRightY - half),
                Math.Max(0f, clampedRadii.BottomLeftX - half), Math.Max(0f, clampedRadii.BottomLeftY - half));

            displayList.StrokeRoundedRect(strokeRect, color, border.Top, strokeRadii);
            return;
        }

        if (border.Top > 0f)
        {
            displayList.FillRect(new RenderRect(x, y, width, border.Top), color);
        }

        if (border.Right > 0f)
        {
            displayList.FillRect(new RenderRect(x + width - border.Right, y, border.Right, height), color);
        }

        if (border.Bottom > 0f)
        {
            displayList.FillRect(new RenderRect(x, y + height - border.Bottom, width, border.Bottom), color);
        }

        if (border.Left > 0f)
        {
            displayList.FillRect(new RenderRect(x, y, border.Left, height), color);
        }
    }

    private static float CollapseMargins(float previousMarginBottom, float currentMarginTop)
    {
        var positivePart = Math.Max(0f, previousMarginBottom) + Math.Max(0f, currentMarginTop);
        var negativePart = Math.Min(0f, previousMarginBottom) + Math.Min(0f, currentMarginTop);

        if (positivePart > 0f && negativePart < 0f)
        {
            return positivePart + negativePart;
        }

        if (positivePart > 0f)
        {
            return Math.Max(previousMarginBottom, currentMarginTop);
        }

        return Math.Min(previousMarginBottom, currentMarginTop);
    }

    private static void ResolveHorizontalMetrics(
        float containingWidth,
        float specifiedContentWidth,
        float borderLeft,
        float borderRight,
        float paddingLeft,
        float paddingRight,
        ref float marginLeft,
        ref float marginRight,
        out float contentWidth)
    {
        var hasAutoWidth = float.IsNaN(specifiedContentWidth);
        var hasAutoLeft = float.IsNaN(marginLeft);
        var hasAutoRight = float.IsNaN(marginRight);

        var usedMarginLeft = hasAutoLeft ? 0f : marginLeft;
        var usedMarginRight = hasAutoRight ? 0f : marginRight;
        var horizontalExtras = borderLeft + borderRight + paddingLeft + paddingRight;

        if (hasAutoWidth)
        {
            contentWidth = containingWidth - horizontalExtras - usedMarginLeft - usedMarginRight;

            if (contentWidth < 0f)
            {
                contentWidth = 0f;
            }

            marginLeft = usedMarginLeft;
            marginRight = usedMarginRight;
            return;
        }

        contentWidth = Math.Max(0f, specifiedContentWidth);
        var underflow = containingWidth - horizontalExtras - contentWidth - usedMarginLeft - usedMarginRight;

        if (hasAutoLeft && hasAutoRight)
        {
            var half = underflow / 2f;
            marginLeft = half;
            marginRight = half;
            return;
        }

        if (hasAutoLeft)
        {
            marginLeft = underflow;
            marginRight = usedMarginRight;
            return;
        }

        if (hasAutoRight)
        {
            marginLeft = usedMarginLeft;
            marginRight = underflow;
            return;
        }

        marginLeft = usedMarginLeft;
        marginRight = usedMarginRight + underflow;
    }

    private static float ParseLength(Dictionary<string, string> styleMap, string propertyName, float relativeTo, float defaultValue, bool allowAuto)
    {
        if (!styleMap.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var parsed = value.Trim().ToLowerInvariant();

        if (allowAuto && parsed == "auto")
        {
            return float.NaN;
        }

        if (parsed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(parsed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            return (pct / 100f) * relativeTo;
        }

        return ParseLengthValue(parsed, defaultValue, allowAuto: false);
    }

    internal static float ParseLengthValue(string value, float defaultValue, bool allowAuto = true)
    {
        if (allowAuto && string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            return float.NaN;
        }

        if (TryParsePixelValue(value, out var pixels))
        {
            return pixels;
        }

        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var unitless))
        {
            return unitless;
        }

        return defaultValue;
    }

    private static bool TryParsePixelValue(string value, out float pixels)
    {
        var trimmed = value.Trim().ToLowerInvariant();

        if (trimmed.EndsWith("px", StringComparison.Ordinal))
        {
            return float.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out pixels);
        }

        pixels = 0f;
        return false;
    }

    private static RenderPaint ParseBackgroundPaint(Dictionary<string, string> styleMap, RenderColor fallbackColor, IElement element)
    {
        if (!styleMap.TryGetValue("background-image", out var backgroundImage) || string.IsNullOrWhiteSpace(backgroundImage))
        {
            return new RenderColorPaint(fallbackColor);
        }

        var trimmed = backgroundImage.Trim();

        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderColorPaint(fallbackColor);
        }

        if (TryExtractCssUrl(trimmed, out var url))
        {
            // A background image that fails to load (bad data URI, 404, no IDocumentLoader
            // configured, unsupported format) falls back to the background-color exactly like a
            // browser does - the box is never left entirely unpainted because of it.
            return TryGetOrLoadBackgroundImageResource(element, url, out var imageResource) && imageResource is not null
                ? BuildImagePaint(imageResource, styleMap)
                : new RenderColorPaint(fallbackColor);
        }

        return ParseGradientPaint(trimmed, fallbackColor);
    }

    private static bool TryExtractCssUrl(string value, out string url)
    {
        url = string.Empty;

        if (!value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var close = value.LastIndexOf(')');

        if (close < 4)
        {
            return false;
        }

        url = value[4..close].Trim().Trim('\'', '"').Trim();
        return url.Length > 0;
    }

    private static RenderPaint BuildImagePaint(CachedImageResource imageResource, Dictionary<string, string> styleMap)
    {
        var image = new RenderedImage(imageResource.Bytes, imageResource.NaturalWidth, imageResource.NaturalHeight, imageResource.MimeType);
        var (repeatX, repeatY) = ParseBackgroundRepeat(styleMap);
        var (positionX, positionY) = ParseBackgroundPosition(styleMap);
        var size = ParseBackgroundSize(styleMap);

        return new RenderImagePaint(image, repeatX, repeatY, positionX, positionY, size);
    }

    /// <summary>
    /// Parses `background-repeat`. Only the single-keyword and two-keyword-per-axis forms are
    /// recognized; `space`/`round` fall back to tiling like plain `repeat` rather than the spacing
    /// they actually specify, since neither is otherwise implemented. The CSS initial value is
    /// `repeat` on both axes.
    /// </summary>
    private static (bool RepeatX, bool RepeatY) ParseBackgroundRepeat(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("background-repeat", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return (true, true);
        }

        var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (true, true);
        }

        bool IsRepeating(string token) => token.ToLowerInvariant() is "repeat" or "space" or "round";

        return tokens[0].ToLowerInvariant() switch
        {
            "repeat-x" => (true, false),
            "repeat-y" => (false, true),
            "no-repeat" when tokens.Length == 1 => (false, false),
            _ when tokens.Length >= 2 => (IsRepeating(tokens[0]), IsRepeating(tokens[1])),
            _ => (IsRepeating(tokens[0]), IsRepeating(tokens[0])),
        };
    }

    /// <summary>
    /// Parses `background-position`. Only the common one- and two-value forms are recognized
    /// (keywords, percentages, lengths); the four-value `&lt;side&gt; &lt;offset&gt;` edge-relative
    /// syntax is not. The CSS initial value is `0% 0%` (top-left).
    /// </summary>
    private static (RenderBackgroundPositionComponent X, RenderBackgroundPositionComponent Y) ParseBackgroundPosition(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("background-position", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return (RenderBackgroundPositionComponent.Zero, RenderBackgroundPositionComponent.Zero);
        }

        var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (RenderBackgroundPositionComponent.Zero, RenderBackgroundPositionComponent.Zero);
        }

        if (tokens.Length == 1)
        {
            // A single token positions the X axis (or both, for `center`); the Y axis defaults to
            // centered, matching the CSS single-value `background-position` rule.
            var only = tokens[0].ToLowerInvariant();

            if (only is "top" or "bottom")
            {
                return (new RenderBackgroundPositionComponent(0.5f, 0f), ParsePositionComponent(only));
            }

            return (ParsePositionComponent(only), new RenderBackgroundPositionComponent(0.5f, 0f));
        }

        return (ParsePositionComponent(tokens[0]), ParsePositionComponent(tokens[1]));
    }

    private static RenderBackgroundPositionComponent ParsePositionComponent(string token)
    {
        return token.ToLowerInvariant() switch
        {
            "left" or "top" => RenderBackgroundPositionComponent.Zero,
            "right" or "bottom" => new RenderBackgroundPositionComponent(1f, 0f),
            "center" => new RenderBackgroundPositionComponent(0.5f, 0f),
            _ => ParsePositionLength(token),
        };
    }

    private static RenderBackgroundPositionComponent ParsePositionLength(string token)
    {
        var trimmed = token.Trim();

        if (trimmed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return new RenderBackgroundPositionComponent(percent / 100f, 0f);
        }

        if (TryParsePixelValue(trimmed, out var pixels))
        {
            return new RenderBackgroundPositionComponent(0f, pixels);
        }

        return RenderBackgroundPositionComponent.Zero;
    }

    /// <summary>
    /// Parses the CSS `transform` property plus `transform-origin` into a single, fully-resolved
    /// <see cref="RenderTransform2D"/> - delegating each individual transform function's own
    /// parsing and matrix computation entirely to AngleSharp.Css's own
    /// <see cref="TransformParser"/>/<see cref="ICssTransformFunctionValue.ComputeMatrix"/>,
    /// rather than re-implementing CSS transform function parsing, length/percentage/angle
    /// resolution, or the underlying trigonometry in this renderer. This method's own
    /// responsibility is limited to: looping <see cref="TransformParser.ParseTransform"/> across
    /// the space-separated function list (it parses one function per call and advances the source
    /// itself - including skipping the whitespace between calls, confirmed empirically rather than
    /// assumed - returning <see langword="null"/> cleanly once the source is exhausted, which is
    /// what ends the loop), multiplying the resulting per-function matrices together in CSS's own
    /// left-to-right composition order (see <see cref="RenderTransform2D.Multiply"/>), converting
    /// AngleSharp.Css's own <see cref="TransformMatrix"/> into this renderer's flat 2D `a,b,c,d,e,f`
    /// representation (see <see cref="ConvertToRenderTransform"/> for the exact field mapping,
    /// which is not the naive one), and wrapping the composed result around `transform-origin` -
    /// AngleSharp.Css's own `ComputeMatrix` takes no origin parameter, so that translate/apply/
    /// translate-back is this renderer's to do regardless of how the individual functions resolve.
    ///
    /// Resolved eagerly here (unlike a gradient's or background-image's own paint-time-deferred
    /// geometry) because a transform's inputs - percentage `translate()` values, the default
    /// `transform-origin` - only ever need the element's own already-known border-box dimensions,
    /// never anything (like an image's natural size) that is not available until paint time.
    ///
    /// This renderer never special-cases individual transform functions or guards against
    /// AngleSharp.Css bugs locally - `ConvertToRenderTransform` always trusts whatever
    /// <see cref="ICssTransformFunctionValue.ComputeMatrix"/> returns, unconditionally. A
    /// `TransformMatrix` is a genuinely general matrix regardless of which function produced it;
    /// for a function whose own effect is entirely 2D, the third dimension simply stays at its own
    /// identity value, so there is nothing to specialize for a "2D case" - the same six components
    /// (M11/M12/M21/M22/Tx/Ty) are read the same way for every function. Any bug in what
    /// AngleSharp.Css itself computes for a given function is AngleSharp.Css's own bug to fix -
    /// this renderer reports and reproduces it there (with a failing test in that project's own
    /// suite) rather than working around it here, matching this project's own "we do not touch the
    /// CSS ourselves" policy. `translate`/`translateX`/`translateY` need one exception to "just call
    /// AngleSharp.Css normally", though: the raw value is parsed directly here rather than through
    /// AngleSharp.Css's own computed-style cascade, because that cascade path has a separate,
    /// confirmed crash for exactly those three functions - see `TryExtractTransformDeclaration`'s
    /// own remarks for why that cascade path has to be avoided entirely, independent of this method.
    ///
    /// `TransformParser` is AngleSharp.Css's own general-purpose transform-function parser and
    /// recognizes 3D functions (`translate3d`, `rotate3d`, `matrix3d`, `perspective`, ...) too -
    /// this method does not filter them out before parsing. For one of them, `translateZ`,
    /// `ComputeMatrix`'s 2D component (M11/M12/M21/M22/Tx/Ty) happens to come back as pure identity
    /// (only its Z-only shift is non-zero, which this renderer never reads), so it is harmlessly a
    /// no-op through the exact same path real 2D functions use - confirmed with a structural test,
    /// not assumed. A 3D function whose effect genuinely depends on a Z axis this renderer has no
    /// projection for at all (`rotate3d`, `perspective`, ...) would not be meaningfully
    /// representable even with a bug-free `ComputeMatrix`; this renderer's flat, backend-agnostic
    /// display list simply has no camera/projection concept to give such a function real meaning -
    /// a deliberate, permanent scope cut, unrelated to any upstream bug.
    /// </summary>
    private static RenderTransform2D ParseCssTransform(Dictionary<string, string> styleMap, float borderBoxX, float borderBoxY, float borderBoxWidth, float borderBoxHeight, float fontSize)
    {
        if (!styleMap.TryGetValue("transform", out var value) ||
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return RenderTransform2D.Identity;
        }

        var dimensions = new CssTransformRenderDimensions(borderBoxWidth, borderBoxHeight, fontSize);
        var source = new StringSource(value);
        var functionsMatrix = RenderTransform2D.Identity;

        while (TransformParser.ParseTransform(source) is { } functionValue)
        {
            var next = ConvertToRenderTransform(functionValue, dimensions);
            functionsMatrix = RenderTransform2D.Multiply(functionsMatrix, next);
        }

        if (functionsMatrix.IsIdentity)
        {
            return RenderTransform2D.Identity;
        }

        // transform-origin shifts the whole composed function chain to pivot around a point other
        // than the box's own top-left corner (the origin every individual function's matrix
        // otherwise implicitly pivots/scales/skews around): translate to the origin, apply the
        // functions, translate back. The origin itself is resolved relative to the box (a fraction/
        // offset of its own width/height), but PushTransformCommand's matrix is concatenated onto
        // the canvas *before* any of this element's own commands run - which still carry their
        // ordinary absolute page coordinates (borderBoxX/Y, not box-local 0..width/0..height) - so
        // the pivot point has to be expressed in that same absolute space (borderBoxX/Y + the
        // relative origin), not just the relative offset within the box. Getting this wrong was a
        // real bug caught while building this feature: verified by hand-checking the resulting
        // matrix against a box positioned away from the page origin, where a box-relative-only
        // pivot rotated the box around the wrong point entirely (only appearing correct for a box
        // that happened to sit at page position (0, 0), where relative and absolute coincide).
        var (originOffsetX, originOffsetY) = ParseTransformOrigin(styleMap, borderBoxWidth, borderBoxHeight);
        var originX = borderBoxX + originOffsetX;
        var originY = borderBoxY + originOffsetY;
        var toOrigin = RenderTransform2D.Translate(originX, originY);
        var fromOrigin = RenderTransform2D.Translate(-originX, -originY);
        return RenderTransform2D.Multiply(toOrigin, RenderTransform2D.Multiply(functionsMatrix, fromOrigin));
    }

    /// <summary>
    /// Converts one already-parsed CSS transform function's own <see cref="TransformMatrix"/> into
    /// this renderer's flat 2D <see cref="RenderTransform2D"/>, unconditionally - this renderer
    /// always trusts the matrix AngleSharp.Css computes directly, with no NaN/exception guarding
    /// and no 2D-vs-3D special-casing of its own. A <see cref="TransformMatrix"/> is a genuinely
    /// general matrix regardless of which CSS function produced it; for a function whose own effect
    /// is entirely 2D, the third dimension simply stays at its own identity value, so reading off
    /// the same six 2D-relevant components (M11/M12/M21/M22/Tx/Ty) works uniformly for every
    /// function - there is nothing here for this renderer to specialize. Any bug in what
    /// AngleSharp.Css itself computes belongs to AngleSharp.Css, not to a defensive workaround in
    /// this method - report and fix it there (with a reproducing test in its own suite) instead.
    /// </summary>
    private static RenderTransform2D ConvertToRenderTransform(ICssTransformFunctionValue functionValue, IRenderDimensions dimensions)
    {
        var matrix = functionValue.ComputeMatrix(dimensions);

        // AngleSharp.Css's TransformMatrix uses a row-vector convention -
        // x' = M11*x + M12*y + Tx, y' = M21*x + M22*y + Ty - which is *not* the naive mapping onto
        // CSS's own column-vector matrix(a,b,c,d,e,f) (x' = a*x + c*y + e, y' = b*x + d*y + f) a
        // reader might expect. Verified empirically (not assumed) against a known skew(10deg,5deg)
        // result: M12 came back as tan(10deg) - the coefficient of y in the x' equation, i.e. CSS's
        // "c" - and M21 came back as tan(5deg) - the coefficient of x in the y' equation, i.e. CSS's
        // "b" - the opposite of what matching M12 to "b" and M21 to "c" by position would give.
        return new RenderTransform2D(
            (float)matrix.M11,
            (float)matrix.M21,
            (float)matrix.M12,
            (float)matrix.M22,
            (float)matrix.Tx,
            (float)matrix.Ty);
    }

    private readonly struct CssTransformRenderDimensions(double renderWidth, double renderHeight, double fontSize) : IRenderDimensions
    {
        public double RenderWidth { get; } = renderWidth;

        public double RenderHeight { get; } = renderHeight;

        public double FontSize { get; } = fontSize;
    }

    /// <summary>
    /// Parses `transform-origin` into pixel coordinates relative to the element's own border box -
    /// the CSS initial value, "50% 50%", is the box's own center. Reuses `ParsePositionComponent`'s
    /// keyword/percentage/length tokenizing (the identical single-axis grammar `background-position`
    /// already uses: `left`/`center`/`right`/`top`/`bottom`, a percentage, or a length) rather than
    /// re-deriving it, but resolves its `Percentage`/`OffsetPixels` breakdown with
    /// `transform-origin`'s own formula (a position *within* the box) instead of
    /// `background-position`'s (a position within the *remaining* space after subtracting the
    /// image's own size) - the two properties share a grammar but not a resolution formula.
    /// </summary>
    private static (float X, float Y) ParseTransformOrigin(Dictionary<string, string> styleMap, float borderBoxWidth, float borderBoxHeight)
    {
        var xComponent = new RenderBackgroundPositionComponent(0.5f, 0f);
        var yComponent = new RenderBackgroundPositionComponent(0.5f, 0f);

        if (styleMap.TryGetValue("transform-origin", out var value) && !string.IsNullOrWhiteSpace(value))
        {
            var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 1)
            {
                var only = tokens[0].ToLowerInvariant();

                if (only is "top" or "bottom")
                {
                    yComponent = ParsePositionComponent(only);
                }
                else
                {
                    xComponent = ParsePositionComponent(only);
                }
            }
            else if (tokens.Length >= 2)
            {
                xComponent = ParsePositionComponent(tokens[0]);
                yComponent = ParsePositionComponent(tokens[1]);
            }
        }

        var x = (xComponent.Percentage * borderBoxWidth) + xComponent.OffsetPixels;
        var y = (yComponent.Percentage * borderBoxHeight) + yComponent.OffsetPixels;
        return (x, y);
    }

    /// <summary>
    /// Parses CSS `opacity` via AngleSharp.Css's own typed `GetOpacity()` accessor - unlike
    /// `filter`, this is an ordinary numeric property with no function-value syntax, so
    /// AngleSharp.Css computes it normally with nothing for this renderer to work around. The CSS
    /// initial value is `1` (fully opaque); a value outside `0..1` is clamped, per spec.
    /// </summary>
    private static float ParseCssOpacity(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("opacity", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 1f;
        }

        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity)
            ? Math.Clamp(opacity, 0f, 1f)
            : 1f;
    }

    /// <summary>
    /// Parses `filter` via AngleSharp.Css's own `FilterParser.ParseFilter`/`ICssFilterFunctionValue`
    /// (fixed upstream - AngleSharp.Css previously had no structured `filter` support at all, so
    /// this renderer had to hand-parse the raw text itself, mirroring the CSS-gradient precedent;
    /// it now mirrors the `transform` precedent instead) - AngleSharp.Css handles the outer
    /// function-list syntax (name/parenthesis extraction, including nested parens) and gives each
    /// function's own name plus its raw, still-unparsed argument text as a single
    /// <c>Arguments</c> entry; this renderer still interprets what
    /// each function *means* (its own argument grammar, and the CSS Filter Effects spec's per-function
    /// color-matrix/blur-radius semantics - see <see cref="ConvertToRenderFilterFunction"/>), since
    /// that is a rendering concern AngleSharp.Css does not (and should not) resolve for it, the same
    /// division of labor `transform`'s `ConvertToRenderTransform` already established for
    /// `ComputeMatrix`. `url(#filterId)` (an SVG filter reference) and any other unrecognized
    /// function are silently skipped rather than aborting the whole chain, mirroring how an
    /// unsupported SVG `&lt;filter&gt;` primitive passes its input through unchanged in
    /// `SvgFilterBuilder` instead of breaking that chain.
    /// </summary>
    private static IReadOnlyList<RenderFilterFunction> ParseCssFilter(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("filter", out var value) ||
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (FilterParser.ParseFilter(new StringSource(value)) is not CssFilterValue parsed)
        {
            return [];
        }

        var functions = new List<RenderFilterFunction>();

        foreach (var cssFunction in parsed.Functions)
        {
            if (ConvertToRenderFilterFunction(cssFunction, out var function))
            {
                functions.Add(function);
            }
        }

        return functions;
    }

    private static bool ConvertToRenderFilterFunction(ICssFilterFunctionValue cssFunction, out RenderFilterFunction function)
    {
        function = null!;
        var argsText = cssFunction.Arguments.Length > 0 ? cssFunction.Arguments[0].CssText.Trim() : string.Empty;

        switch (cssFunction.Name.ToLowerInvariant())
        {
            case "blur":
                function = RenderFilterFunction.Blur(Math.Max(0f, ParseLengthValue(argsText, defaultValue: 0f, allowAuto: false)));
                return true;
            case "brightness":
                function = RenderFilterFunction.Brightness(Math.Max(0f, ParseFilterAmount(argsText, 1f)));
                return true;
            case "contrast":
                function = RenderFilterFunction.Contrast(Math.Max(0f, ParseFilterAmount(argsText, 1f)));
                return true;
            case "grayscale":
                function = RenderFilterFunction.Grayscale(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "invert":
                function = RenderFilterFunction.Invert(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "opacity":
                function = RenderFilterFunction.Opacity(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "saturate":
                function = RenderFilterFunction.Saturate(Math.Max(0f, ParseFilterAmount(argsText, 1f)));
                return true;
            case "sepia":
                function = RenderFilterFunction.Sepia(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "hue-rotate":
                function = RenderFilterFunction.HueRotate(ParseAngle(argsText));
                return true;
            case "drop-shadow":
                function = ParseDropShadowFilterFunction(argsText);
                return true;
            default:
                return false;
        }
    }

    // `grayscale(90%)` and `grayscale(0.9)` are equivalent per spec - a percentage argument is
    // normalized to the same 0..1 fraction a bare number already is, so every downstream consumer
    // (RenderFilterFunction.Amount) only ever has to handle one representation.
    private static float ParseFilterAmount(string value, float defaultValue)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return defaultValue;
        }

        if (trimmed.EndsWith('%') &&
            float.TryParse(trimmed[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
        {
            return percentage / 100f;
        }

        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : defaultValue;
    }

    // `drop-shadow(<offset-x> <offset-y> <blur-radius>? <color>?)` - CSS allows the color argument
    // either first or last, so this classifies each whitespace-separated token by its own shape
    // (a leading digit/sign/decimal point is a length, anything else is a color) rather than
    // assuming a fixed position, then assigns lengths to offsetX/offsetY/blurRadius in the order
    // they were seen.
    private static RenderFilterFunction ParseDropShadowFilterFunction(string argsText)
    {
        var offsetX = 0f;
        var offsetY = 0f;
        var blurRadius = 0f;
        var color = RenderColor.Black;
        var lengthIndex = 0;

        foreach (var token in SplitTopLevelWhitespaceList(argsText))
        {
            var trimmed = token.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            var firstChar = trimmed[0];

            if (char.IsDigit(firstChar) || firstChar is '-' or '+' or '.')
            {
                var length = ParseLengthValue(trimmed, 0f, allowAuto: false);

                switch (lengthIndex)
                {
                    case 0:
                        offsetX = length;
                        break;
                    case 1:
                        offsetY = length;
                        break;
                    default:
                        blurRadius = length;
                        break;
                }

                lengthIndex++;
            }
            else
            {
                color = ParseColor(trimmed, color);
            }
        }

        return RenderFilterFunction.DropShadow(offsetX, offsetY, Math.Max(0f, blurRadius), color);
    }

    // Splits a value on whitespace at paren-depth 0 only, so a nested function call within one of
    // the tokens (e.g. a `rgb(255, 0, 0)` color argument) is never split apart. Used to tokenize
    // `drop-shadow`'s own space-separated argument list - the outer `filter` function list itself
    // is now split by AngleSharp.Css's own FilterParser instead (see ParseCssFilter's remarks).
    private static string[] SplitTopLevelWhitespaceList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var character in value)
        {
            if (character == '(')
            {
                depth++;
                current.Append(character);
            }
            else if (character == ')')
            {
                depth = Math.Max(0, depth - 1);
                current.Append(character);
            }
            else if (char.IsWhiteSpace(character) && depth == 0)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }

    /// <summary>
    /// Parses `background-size`. The CSS initial value is `auto` (the image's own natural size).
    /// </summary>
    private static RenderBackgroundSize ParseBackgroundSize(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("background-size", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return RenderBackgroundSize.Auto;
        }

        var trimmed = value.Trim();

        if (string.Equals(trimmed, "cover", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderBackgroundSize(RenderBackgroundSizeKind.Cover);
        }

        if (string.Equals(trimmed, "contain", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderBackgroundSize(RenderBackgroundSizeKind.Contain);
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return RenderBackgroundSize.Auto;
        }

        var width = ParseSizeAxisToken(tokens[0]);
        // A single-value `background-size` sizes only the width explicitly; the height is always
        // `auto` (proportional to the image's aspect ratio), never a copy of the width token.
        var height = tokens.Length > 1 ? ParseSizeAxisToken(tokens[1]) : new RenderBackgroundSizeAxis(true, false, 0f);

        return new RenderBackgroundSize(RenderBackgroundSizeKind.Explicit, width, height);
    }

    private static RenderBackgroundSizeAxis ParseSizeAxisToken(string token)
    {
        var trimmed = token.Trim();

        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderBackgroundSizeAxis(true, false, 0f);
        }

        if (trimmed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return new RenderBackgroundSizeAxis(false, true, percent / 100f);
        }

        if (TryParsePixelValue(trimmed, out var pixels))
        {
            return new RenderBackgroundSizeAxis(false, false, pixels);
        }

        return new RenderBackgroundSizeAxis(true, false, 0f);
    }

    // AngleSharp.Css now parses gradients into structured values (`GradientParser.ParseGradient`,
    // mirroring `TransformParser`/`FilterParser` exactly), so this no longer hand-parses the raw
    // `background-image` text itself - it delegates the outer function/argument grammar and reads
    // each already-typed piece (angle, stop color/position, center point) off the result, the same
    // division of labor already established for `transform`/`filter`: AngleSharp.Css computes the
    // pure-CSS structure, this renderer still owns turning it into its own backend-agnostic
    // `RenderGradient`. `ParseGradient` returns null cleanly for anything it does not recognize
    // (including a plain color), so no name-prefix pre-check is needed before calling it.
    private static RenderPaint ParseGradientPaint(string rawValue, RenderColor fallbackColor)
    {
        var source = new StringSource(rawValue.Trim());

        return source.ParseGradient() switch
        {
            CssLinearGradientValue linear => new RenderGradientPaint(ConvertLinearGradient(linear, fallbackColor)),
            CssRadialGradientValue radial => new RenderGradientPaint(ConvertRadialGradient(radial, fallbackColor)),
            CssConicGradientValue conic => new RenderGradientPaint(ConvertConicGradient(conic, fallbackColor)),
            _ => new RenderColorPaint(fallbackColor),
        };
    }

    private static RenderGradient ConvertLinearGradient(CssLinearGradientValue gradient, RenderColor fallbackColor)
    {
        // `.Angle` already defaults correctly to 180deg ("to bottom") when no direction was
        // authored - confirmed against AngleSharp.Css's own test suite, unlike the conic-gradient
        // equivalent below. Re-parsing its own `.CssText` (rather than reading a numeric degree
        // value directly) reuses the exact same deg/grad/turn/rad-unit handling `filter`'s
        // `hue-rotate` already relies on (`TryParseAngle`), so a keyword direction like "to right"
        // (which AngleSharp.Css resolves to a plain `CssAngleValue` internally, per `Map.GradientAngles`)
        // and an explicit `135deg` both flow through the same one conversion path - both are CSS's
        // own "0deg points up, clockwise" convention. `CreateLinearGradientShader` (unlike its conic
        // counterpart, which applies this same correction itself via a rotation matrix) expects
        // `AngleDegrees` pre-converted to its own "0 points right, clockwise" screen convention, so
        // the -90 shift has to happen here - confirmed by a real, visible bug this surfaced:
        // `to right` (CSS 90deg) rendered as horizontal stripes instead of vertical ones without it.
        var angleDegrees = ParseAngle(gradient.Angle.CssText) - 90f;
        var stops = ConvertGradientStops(gradient.Stops, fallbackColor, isConic: false);
        return new RenderGradient(RenderGradientKind.Linear, stops, AngleDegrees: angleDegrees, Repeating: gradient.IsRepeating);
    }

    private static RenderGradient ConvertRadialGradient(CssRadialGradientValue gradient, RenderColor fallbackColor)
    {
        var (centerX, centerY) = ParsePosition(gradient.Position.CssText);
        var sizeKind = RenderGradientSizeKind.FarthestCorner;
        float? explicitRadiusX = null;
        float? explicitRadiusY = null;

        if (gradient.Mode != CssRadialGradientValue.SizeMode.None)
        {
            sizeKind = gradient.Mode switch
            {
                CssRadialGradientValue.SizeMode.ClosestCorner => RenderGradientSizeKind.ClosestCorner,
                CssRadialGradientValue.SizeMode.ClosestSide => RenderGradientSizeKind.ClosestSide,
                CssRadialGradientValue.SizeMode.FarthestSide => RenderGradientSizeKind.FarthestSide,
                _ => RenderGradientSizeKind.FarthestCorner,
            };
        }
        else if (gradient.MajorRadius.CssText != CssLengthValue.Full.CssText || gradient.MinorRadius.CssText != CssLengthValue.Full.CssText)
        {
            // `Mode.None` alone does not distinguish "no size/radius was authored at all" from "an
            // explicit radius was given" - `CssRadialGradientValue` has no public signal for that
            // beyond this: an unset radius's own getter substitutes `CssLengthValue.Full` (100%)
            // for the `null` it actually holds internally, with no way to tell the two apart from
            // the outside. Comparing against that same sentinel is therefore the only available
            // signal; the one case it cannot distinguish - an *explicit* ellipse radius that
            // legitimately happens to be exactly 100% on both axes - is rare enough (and visually
            // close to `farthest-corner` in most box aspect ratios anyway) to accept as a known,
            // narrow approximation rather than threading extra state through GradientParser for it.
            sizeKind = RenderGradientSizeKind.Explicit;

            if (TryParsePixelValue(gradient.MajorRadius.CssText, out var radiusX))
            {
                explicitRadiusX = radiusX;
            }

            explicitRadiusY = TryParsePixelValue(gradient.MinorRadius.CssText, out var radiusY) ? radiusY : explicitRadiusX;
        }

        var stops = ConvertGradientStops(gradient.Stops, fallbackColor, isConic: false);
        return new RenderGradient(
            RenderGradientKind.Radial,
            stops,
            CenterX: centerX,
            CenterY: centerY,
            IsCircle: gradient.IsCircle,
            Repeating: gradient.IsRepeating,
            SizeKind: sizeKind,
            ExplicitRadiusX: explicitRadiusX,
            ExplicitRadiusY: explicitRadiusY);
    }

    private static RenderGradient ConvertConicGradient(CssConicGradientValue gradient, RenderColor fallbackColor)
    {
        // Unlike linear's `.Angle`, conic's own default-angle fallback was a confirmed AngleSharp.Css
        // bug (reported and fixed upstream, AngleSharp.Css.Tests/Values/Gradient.cs
        // ConicGradientDefaultAngleIsZeroNotHalfCircle) - it used to report 180deg for an omitted
        // `from` clause instead of the CSS spec's own 0deg default, so this renderer must build
        // against a version with that fix rather than working around it locally.
        var angleDegrees = ParseAngle(gradient.Angle.CssText);
        var (centerX, centerY) = ParsePosition(gradient.Center.CssText);
        var stops = ConvertGradientStops(gradient.Stops, fallbackColor, isConic: true);
        return new RenderGradient(RenderGradientKind.Conic, stops, AngleDegrees: angleDegrees, CenterX: centerX, CenterY: centerY, Repeating: gradient.IsRepeating);
    }

    private static RenderColor ToRenderColor(CssColorValue color) => new(color.R, color.G, color.B, color.A);

    private static readonly string[] PositionKeywords = ["left", "right", "top", "bottom", "center"];

    /// <summary>
    /// Parses a CSS `&lt;position&gt;` value (1-2 tokens, keywords and/or percentages, in either
    /// order for keywords) into fractional (0-1) X/Y coordinates. Absolute lengths (`at 20px
    /// 10px`) and the 4-value edge-offset syntax are not supported and fall back to center.
    /// </summary>
    private static (float X, float Y) ParsePosition(string text)
    {
        var tokens = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (0.5f, 0.5f);
        }

        float? x = null;
        float? y = null;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "left":
                    x = 0f;
                    break;
                case "right":
                    x = 1f;
                    break;
                case "top":
                    y = 0f;
                    break;
                case "bottom":
                    y = 1f;
                    break;
            }
        }

        var positionalTokens = tokens.Where(token => !PositionKeywords.Contains(token.ToLowerInvariant())).ToArray();
        var assignedX = x is not null;

        foreach (var token in positionalTokens)
        {
            var value = ParseStopPosition(token);

            if (!assignedX)
            {
                x = value;
                assignedX = true;
            }
            else if (y is null)
            {
                y = value;
            }
        }

        return (x ?? 0.5f, y ?? 0.5f);
    }

    internal static string[] SplitTopLevelCommaList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var character in value)
        {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (character == ',' && depth == 0)
            {
                var part = current.ToString().Trim();
                if (part.Length > 0)
                {
                    parts.Add(part);
                }

                current.Clear();
                continue;
            }

            current.Append(character);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
        {
            parts.Add(last);
        }

        return parts.ToArray();
    }

    /// <summary>
    /// Converts AngleSharp.Css's own <see cref="CssGradientStopValue"/> list (already split and
    /// individually parsed by <see cref="GradientParser"/>) into this renderer's backend-agnostic
    /// stops - the counterpart to the old text-splitting version this replaced, kept as narrow a
    /// change as possible: each stop's own color comes directly off <c>CssGradientStopValue.Color</c>
    /// (a real, already-resolved <see cref="CssColorValue"/> - no re-parsing needed, unlike before),
    /// while its position still goes through <see cref="ParseStopPosition"/> exactly as it always
    /// did, just fed that stop's own <c>Location.CssText</c> instead of a hand-split token.
    /// </summary>
    private static IReadOnlyList<RenderGradientStop> ConvertGradientStops(ICssValue[] rawStops, RenderColor fallbackColor, bool isConic)
    {
        var stops = rawStops.OfType<CssGradientStopValue>().ToArray();

        if (stops.Length == 0)
        {
            return [new RenderGradientStop(0f, fallbackColor)];
        }

        var result = new List<RenderGradientStop>(stops.Length);

        for (var index = 0; index < stops.Length; index++)
        {
            var stop = stops[index];
            var color = ToRenderColor(stop.Color);
            var autoPosition = stops.Length == 1 ? 0f : (index / (float)Math.Max(1, stops.Length - 1));

            if (stop.IsUndetermined)
            {
                result.Add(new RenderGradientStop(autoPosition, color));
                continue;
            }

            var positionText = stop.Location.CssText;

            if (!isConic && TryParsePixelValue(positionText, out var pixels))
            {
                // An absolute-length stop position ("red 10px") cannot become a fraction until
                // the gradient's own rendered geometry (line length/radius) is known, so the raw
                // pixel value is carried through and resolved by the backend at paint time.
                result.Add(new RenderGradientStop(autoPosition, color, pixels));
            }
            else
            {
                result.Add(new RenderGradientStop(ParseStopPosition(positionText, isConic), color));
            }
        }

        return result;
    }

    private static float ParseStopPosition(string rawPosition, bool isConic = false)
    {
        var value = rawPosition.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0f;
        }

        // conic-gradient stops are naturally written as angles ("90deg", "0.25turn"), which are a
        // fraction of the full circle rather than of a linear 0-100% run - resolve those first.
        if (isConic && TryParseAngle(value, out var angleDegrees))
        {
            var normalizedDegrees = ((angleDegrees % 360f) + 360f) % 360f;
            return Math.Clamp(normalizedDegrees / 360f, 0f, 1f);
        }

        if (value.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return Math.Clamp(percent / 100f, 0f, 1f);
        }

        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return Math.Clamp(numeric, 0f, 1f);
        }

        return 0f;
    }

    private static bool TryParseAngle(string value, out float angleDegrees)
    {
        angleDegrees = 90f;
        var trimmed = value.Trim();

        if (trimmed.EndsWith("deg", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
        {
            angleDegrees = degrees;
            return true;
        }

        if (trimmed.EndsWith("grad", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gradians))
        {
            angleDegrees = gradians * 0.9f;
            return true;
        }

        if (trimmed.EndsWith("turn", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var turns))
        {
            angleDegrees = turns * 360f;
            return true;
        }

        // Checked after "grad" - "grad" also ends with "rad" and would otherwise be misread here.
        if (trimmed.EndsWith("rad", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var radians))
        {
            angleDegrees = radians * 180f / (float)Math.PI;
            return true;
        }

        return false;
    }

    private static float ParseAngle(string value)
    {
        return TryParseAngle(value, out var angle) ? angle : 0f;
    }

    internal static RenderColor ParseColor(string? rawColor, RenderColor fallback)
    {
        if (string.IsNullOrWhiteSpace(rawColor))
        {
            return fallback;
        }

        var color = rawColor.Trim().ToLowerInvariant();

        if (color.StartsWith("#", StringComparison.Ordinal))
        {
            return ParseHexColor(color, fallback);
        }

        if ((color.StartsWith("rgb(", StringComparison.Ordinal) || color.StartsWith("rgba(", StringComparison.Ordinal)) && color.EndsWith(')'))
        {
            var start = color.IndexOf('(');
            var content = color[(start + 1)..^1].Trim();

            if (content.Contains(',', StringComparison.Ordinal))
            {
                var commaParts = content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (commaParts.Length >= 3 &&
                    TryParseColorChannel(commaParts[0], out var r) &&
                    TryParseColorChannel(commaParts[1], out var g) &&
                    TryParseColorChannel(commaParts[2], out var b))
                {
                    var alpha = byte.MaxValue;

                    if (commaParts.Length >= 4 && TryParseAlphaChannel(commaParts[3], out var a))
                    {
                        alpha = a;
                    }

                    return new RenderColor(r, g, b, alpha);
                }
            }
            else
            {
                var slashParts = content.Split('/', StringSplitOptions.TrimEntries);
                var rgbParts = slashParts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (rgbParts.Length >= 3 &&
                    TryParseColorChannel(rgbParts[0], out var r) &&
                    TryParseColorChannel(rgbParts[1], out var g) &&
                    TryParseColorChannel(rgbParts[2], out var b))
                {
                    var alpha = byte.MaxValue;

                    if (slashParts.Length > 1 && TryParseAlphaChannel(slashParts[1], out var a))
                    {
                        alpha = a;
                    }

                    return new RenderColor(r, g, b, alpha);
                }
            }
        }

        if (string.Equals(color, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return RenderColor.Transparent;
        }

        // Regular box/text colors are normalized by AngleSharp.Css's own computed-style engine
        // before they ever reach this method (it resolves "red" to an rgb()/hex form itself), so
        // this named-color table only matters for values this renderer parses from raw CSS text
        // itself - chiefly gradient stop colors, since a `background-image: linear-gradient(...)`
        // function's internals are opaque to AngleSharp.Css and are hand-parsed here instead.
        // Without it, every named gradient stop color silently fell back to the same color,
        // producing an invisible (fallback-to-fallback) "gradient".
        if (NamedColors.TryGetValue(color, out var named))
        {
            return named;
        }

        return fallback;
    }

    private static readonly IReadOnlyDictionary<string, RenderColor> NamedColors = new Dictionary<string, RenderColor>(StringComparer.OrdinalIgnoreCase)
    {
        ["aliceblue"] = new RenderColor(240, 248, 255),
        ["antiquewhite"] = new RenderColor(250, 235, 215),
        ["aqua"] = new RenderColor(0, 255, 255),
        ["aquamarine"] = new RenderColor(127, 255, 212),
        ["azure"] = new RenderColor(240, 255, 255),
        ["beige"] = new RenderColor(245, 245, 220),
        ["bisque"] = new RenderColor(255, 228, 196),
        ["black"] = new RenderColor(0, 0, 0),
        ["blanchedalmond"] = new RenderColor(255, 235, 205),
        ["blue"] = new RenderColor(0, 0, 255),
        ["blueviolet"] = new RenderColor(138, 43, 226),
        ["brown"] = new RenderColor(165, 42, 42),
        ["burlywood"] = new RenderColor(222, 184, 135),
        ["cadetblue"] = new RenderColor(95, 158, 160),
        ["chartreuse"] = new RenderColor(127, 255, 0),
        ["chocolate"] = new RenderColor(210, 105, 30),
        ["coral"] = new RenderColor(255, 127, 80),
        ["cornflowerblue"] = new RenderColor(100, 149, 237),
        ["cornsilk"] = new RenderColor(255, 248, 220),
        ["crimson"] = new RenderColor(220, 20, 60),
        ["cyan"] = new RenderColor(0, 255, 255),
        ["darkblue"] = new RenderColor(0, 0, 139),
        ["darkcyan"] = new RenderColor(0, 139, 139),
        ["darkgoldenrod"] = new RenderColor(184, 134, 11),
        ["darkgray"] = new RenderColor(169, 169, 169),
        ["darkgreen"] = new RenderColor(0, 100, 0),
        ["darkgrey"] = new RenderColor(169, 169, 169),
        ["darkkhaki"] = new RenderColor(189, 183, 107),
        ["darkmagenta"] = new RenderColor(139, 0, 139),
        ["darkolivegreen"] = new RenderColor(85, 107, 47),
        ["darkorange"] = new RenderColor(255, 140, 0),
        ["darkorchid"] = new RenderColor(153, 50, 204),
        ["darkred"] = new RenderColor(139, 0, 0),
        ["darksalmon"] = new RenderColor(233, 150, 122),
        ["darkseagreen"] = new RenderColor(143, 188, 143),
        ["darkslateblue"] = new RenderColor(72, 61, 139),
        ["darkslategray"] = new RenderColor(47, 79, 79),
        ["darkslategrey"] = new RenderColor(47, 79, 79),
        ["darkturquoise"] = new RenderColor(0, 206, 209),
        ["darkviolet"] = new RenderColor(148, 0, 211),
        ["deeppink"] = new RenderColor(255, 20, 147),
        ["deepskyblue"] = new RenderColor(0, 191, 255),
        ["dimgray"] = new RenderColor(105, 105, 105),
        ["dimgrey"] = new RenderColor(105, 105, 105),
        ["dodgerblue"] = new RenderColor(30, 144, 255),
        ["firebrick"] = new RenderColor(178, 34, 34),
        ["floralwhite"] = new RenderColor(255, 250, 240),
        ["forestgreen"] = new RenderColor(34, 139, 34),
        ["fuchsia"] = new RenderColor(255, 0, 255),
        ["gainsboro"] = new RenderColor(220, 220, 220),
        ["ghostwhite"] = new RenderColor(248, 248, 255),
        ["gold"] = new RenderColor(255, 215, 0),
        ["goldenrod"] = new RenderColor(218, 165, 32),
        ["gray"] = new RenderColor(128, 128, 128),
        ["green"] = new RenderColor(0, 128, 0),
        ["greenyellow"] = new RenderColor(173, 255, 47),
        ["grey"] = new RenderColor(128, 128, 128),
        ["honeydew"] = new RenderColor(240, 255, 240),
        ["hotpink"] = new RenderColor(255, 105, 180),
        ["indianred"] = new RenderColor(205, 92, 92),
        ["indigo"] = new RenderColor(75, 0, 130),
        ["ivory"] = new RenderColor(255, 255, 240),
        ["khaki"] = new RenderColor(240, 230, 140),
        ["lavender"] = new RenderColor(230, 230, 250),
        ["lavenderblush"] = new RenderColor(255, 240, 245),
        ["lawngreen"] = new RenderColor(124, 252, 0),
        ["lemonchiffon"] = new RenderColor(255, 250, 205),
        ["lightblue"] = new RenderColor(173, 216, 230),
        ["lightcoral"] = new RenderColor(240, 128, 128),
        ["lightcyan"] = new RenderColor(224, 255, 255),
        ["lightgoldenrodyellow"] = new RenderColor(250, 250, 210),
        ["lightgray"] = new RenderColor(211, 211, 211),
        ["lightgreen"] = new RenderColor(144, 238, 144),
        ["lightgrey"] = new RenderColor(211, 211, 211),
        ["lightpink"] = new RenderColor(255, 182, 193),
        ["lightsalmon"] = new RenderColor(255, 160, 122),
        ["lightseagreen"] = new RenderColor(32, 178, 170),
        ["lightskyblue"] = new RenderColor(135, 206, 250),
        ["lightslategray"] = new RenderColor(119, 136, 153),
        ["lightslategrey"] = new RenderColor(119, 136, 153),
        ["lightsteelblue"] = new RenderColor(176, 196, 222),
        ["lightyellow"] = new RenderColor(255, 255, 224),
        ["lime"] = new RenderColor(0, 255, 0),
        ["limegreen"] = new RenderColor(50, 205, 50),
        ["linen"] = new RenderColor(250, 240, 230),
        ["magenta"] = new RenderColor(255, 0, 255),
        ["maroon"] = new RenderColor(128, 0, 0),
        ["mediumaquamarine"] = new RenderColor(102, 205, 170),
        ["mediumblue"] = new RenderColor(0, 0, 205),
        ["mediumorchid"] = new RenderColor(186, 85, 211),
        ["mediumpurple"] = new RenderColor(147, 112, 219),
        ["mediumseagreen"] = new RenderColor(60, 179, 113),
        ["mediumslateblue"] = new RenderColor(123, 104, 238),
        ["mediumspringgreen"] = new RenderColor(0, 250, 154),
        ["mediumturquoise"] = new RenderColor(72, 209, 204),
        ["mediumvioletred"] = new RenderColor(199, 21, 133),
        ["midnightblue"] = new RenderColor(25, 25, 112),
        ["mintcream"] = new RenderColor(245, 255, 250),
        ["mistyrose"] = new RenderColor(255, 228, 225),
        ["moccasin"] = new RenderColor(255, 228, 181),
        ["navajowhite"] = new RenderColor(255, 222, 173),
        ["navy"] = new RenderColor(0, 0, 128),
        ["oldlace"] = new RenderColor(253, 245, 230),
        ["olive"] = new RenderColor(128, 128, 0),
        ["olivedrab"] = new RenderColor(107, 142, 35),
        ["orange"] = new RenderColor(255, 165, 0),
        ["orangered"] = new RenderColor(255, 69, 0),
        ["orchid"] = new RenderColor(218, 112, 214),
        ["palegoldenrod"] = new RenderColor(238, 232, 170),
        ["palegreen"] = new RenderColor(152, 251, 152),
        ["paleturquoise"] = new RenderColor(175, 238, 238),
        ["palevioletred"] = new RenderColor(219, 112, 147),
        ["papayawhip"] = new RenderColor(255, 239, 213),
        ["peachpuff"] = new RenderColor(255, 218, 185),
        ["peru"] = new RenderColor(205, 133, 63),
        ["pink"] = new RenderColor(255, 192, 203),
        ["plum"] = new RenderColor(221, 160, 221),
        ["powderblue"] = new RenderColor(176, 224, 230),
        ["purple"] = new RenderColor(128, 0, 128),
        ["rebeccapurple"] = new RenderColor(102, 51, 153),
        ["red"] = new RenderColor(255, 0, 0),
        ["rosybrown"] = new RenderColor(188, 143, 143),
        ["royalblue"] = new RenderColor(65, 105, 225),
        ["saddlebrown"] = new RenderColor(139, 69, 19),
        ["salmon"] = new RenderColor(250, 128, 114),
        ["sandybrown"] = new RenderColor(244, 164, 96),
        ["seagreen"] = new RenderColor(46, 139, 87),
        ["seashell"] = new RenderColor(255, 245, 238),
        ["sienna"] = new RenderColor(160, 82, 45),
        ["silver"] = new RenderColor(192, 192, 192),
        ["skyblue"] = new RenderColor(135, 206, 235),
        ["slateblue"] = new RenderColor(106, 90, 205),
        ["slategray"] = new RenderColor(112, 128, 144),
        ["slategrey"] = new RenderColor(112, 128, 144),
        ["snow"] = new RenderColor(255, 250, 250),
        ["springgreen"] = new RenderColor(0, 255, 127),
        ["steelblue"] = new RenderColor(70, 130, 180),
        ["tan"] = new RenderColor(210, 180, 140),
        ["teal"] = new RenderColor(0, 128, 128),
        ["thistle"] = new RenderColor(216, 191, 216),
        ["tomato"] = new RenderColor(255, 99, 71),
        ["turquoise"] = new RenderColor(64, 224, 208),
        ["violet"] = new RenderColor(238, 130, 238),
        ["wheat"] = new RenderColor(245, 222, 179),
        ["white"] = new RenderColor(255, 255, 255),
        ["whitesmoke"] = new RenderColor(245, 245, 245),
        ["yellow"] = new RenderColor(255, 255, 0),
        ["yellowgreen"] = new RenderColor(154, 205, 50),
    };

    private static bool TryParseColorChannel(string raw, out byte value)
    {
        var token = raw.Trim();

        if (token.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            percent = Math.Clamp(percent, 0f, 100f);
            value = (byte)Math.Round((percent / 100f) * 255f);
            return true;
        }

        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var channel))
        {
            channel = Math.Clamp(channel, 0f, 255f);
            value = (byte)Math.Round(channel);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseAlphaChannel(string raw, out byte alpha)
    {
        var token = raw.Trim();

        if (token.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            percent = Math.Clamp(percent, 0f, 100f);
            alpha = (byte)Math.Round((percent / 100f) * 255f);
            return true;
        }

        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
        {
            if (a <= 1f)
            {
                a *= 255f;
            }

            a = Math.Clamp(a, 0f, 255f);
            alpha = (byte)Math.Round(a);
            return true;
        }

        alpha = byte.MaxValue;
        return false;
    }

    private static RenderColor ParseHexColor(string color, RenderColor fallback)
    {
        if (color.Length == 4)
        {
            var rs = string.Concat(color[1], color[1]);
            var gs = string.Concat(color[2], color[2]);
            var bs = string.Concat(color[3], color[3]);

            if (byte.TryParse(rs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r3) &&
                byte.TryParse(gs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g3) &&
                byte.TryParse(bs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b3))
            {
                return new RenderColor(r3, g3, b3);
            }
        }

        if (color.Length == 7 &&
            byte.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return new RenderColor(r, g, b);
        }

        return fallback;
    }

    /// <summary>
    /// The height a cell needs for its own content, independent of the rows it spans.
    /// </summary>
    private static float MeasureCellHeight(
        LayoutContext context,
        TableCellPlacement placement,
        float[] columnWidths)
    {
        var contentWidth = Math.Max(0f, columnWidths.Skip(placement.ColumnIndex).Take(placement.ColumnSpan).Sum()
            - placement.PaddingLeft - placement.PaddingRight - placement.BorderLeftWidth - placement.BorderRightWidth);
        var contentHeight = 0f;

        if (contentWidth > 0f && placement.Text.Length > 0)
        {
            var wrappedLines = WrapText(context, placement.Text, contentWidth, placement.CellTextStyle);
            contentHeight = wrappedLines.Count * placement.CellTextStyle.FontSize * placement.CellTextStyle.LineHeightMultiplier;
        }

        return Math.Max(20f, contentHeight + placement.PaddingTop + placement.PaddingBottom + placement.BorderTopWidth + placement.BorderBottomWidth);
    }

    /// <summary>
    /// Greedy word-wrapping, extended with `word-break: break-all`/`overflow-wrap: break-word`
    /// support - both read straight off <paramref name="textStyle"/> rather than as separate
    /// parameters, since every caller already carries a fully-resolved <see cref="RenderTextStyle"/>.
    /// `break-all` (<see cref="WrapTextCharacterWise"/>) breaks at any character boundary
    /// everywhere, matching spec precedence over `overflow-wrap` (a word-break-all element ignores
    /// `overflow-wrap` entirely, since breaking is already unrestricted). Otherwise, the ordinary
    /// word-based algorithm below only reaches for character-level breaking (<see cref="SplitOverlongWord"/>)
    /// as the spec's own "last resort": a single word wider than the *entire* line (not just what is
    /// left of the current line) that `overflow-wrap: break-word`/`anywhere` explicitly permits
    /// breaking - a word that merely doesn't fit what's left of the current line still simply wraps
    /// to a new line whole, exactly like `overflow-wrap: normal`.
    /// </summary>
    private static IReadOnlyList<string> WrapText(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        if (textStyle.WordBreak == WordBreakMode.BreakAll)
        {
            return WrapTextCharacterWise(context, text, maxWidth, textStyle);
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0f;

        void FlushCurrentLine()
        {
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0f;
            }
        }

        void AppendToken(string token, float tokenWidth)
        {
            var separatorWidth = current.Length == 0 ? 0f : MeasureTextWidth(context, " ", textStyle);

            if (current.Length > 0 && currentWidth + separatorWidth + tokenWidth > maxWidth)
            {
                FlushCurrentLine();
                separatorWidth = 0f;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
                currentWidth += separatorWidth;
            }

            current.Append(token);
            currentWidth += tokenWidth;
        }

        foreach (var word in words)
        {
            var wordWidth = MeasureTextWidth(context, word, textStyle);

            if (wordWidth > maxWidth && textStyle.OverflowWrap == OverflowWrapMode.BreakWord)
            {
                var chunks = SplitOverlongWord(context, word, maxWidth, textStyle);

                for (var i = 0; i < chunks.Count; i++)
                {
                    if (i == 0)
                    {
                        // The first chunk of a broken word is still an ordinary word boundary -
                        // it gets ordinary inter-word wrapping/spacing against the current line.
                        AppendToken(chunks[i], MeasureTextWidth(context, chunks[i], textStyle));
                    }
                    else
                    {
                        // A mid-word break always continues on a fresh line - there is no space to
                        // share the previous chunk's remaining room with.
                        FlushCurrentLine();
                        current.Append(chunks[i]);
                        currentWidth = MeasureTextWidth(context, chunks[i], textStyle);
                    }
                }

                continue;
            }

            AppendToken(word, wordWidth);
        }

        FlushCurrentLine();

        return lines;
    }

    /// <summary>
    /// Splits one overlong word into the largest chunks that each fit within <paramref name="maxWidth"/>,
    /// for <c>overflow-wrap: break-word</c>/<c>anywhere</c>'s "last resort" mid-word break. Always
    /// makes progress (appends at least one character per chunk) even if a single character alone
    /// exceeds <paramref name="maxWidth"/>, so an extreme case (a huge font size in a tiny box) still
    /// terminates rather than looping.
    /// </summary>
    private static List<string> SplitOverlongWord(LayoutContext context, string word, float maxWidth, RenderTextStyle textStyle)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0f;

        foreach (var rune in word.EnumerateRunes())
        {
            var chStr = rune.ToString();
            var chWidth = MeasureTextWidth(context, chStr, textStyle);

            if (current.Length > 0 && currentWidth + chWidth > maxWidth)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentWidth = 0f;
            }

            current.Append(chStr);
            currentWidth += chWidth;
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>
    /// `word-break: break-all` wrapping: every character (not just every word) is a potential break
    /// point, so this greedily fills each line character-by-character instead of word-by-word - a
    /// space is simply a character like any other here (no separator width added around it), which
    /// reproduces ordinary space-based wrapping for free wherever a line happens to break at one,
    /// while still allowing a break mid-word wherever it does not.
    /// </summary>
    private static IReadOnlyList<string> WrapTextCharacterWise(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0f;

        foreach (var rune in text.EnumerateRunes())
        {
            var chStr = rune.ToString();
            var chWidth = MeasureTextWidth(context, chStr, textStyle);

            if (current.Length > 0 && currentWidth + chWidth > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0f;
            }

            current.Append(chStr);
            currentWidth += chWidth;
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private const string EllipsisCharacter = "…";

    /// <summary>
    /// Truncates <paramref name="text"/> to the longest prefix (by rune, not raw UTF-16 char, so a
    /// truncation point never lands inside a surrogate pair) whose width plus the ellipsis
    /// character's own width still fits within <paramref name="maxWidth"/>, then appends it - the
    /// approach every real browser's own `text-overflow: ellipsis` uses (truncate, do not scale or
    /// reflow). Binary search over rune count, not a linear scan, since <see cref="MeasureTextWidth"/>
    /// goes through the backend's own font shaping and this runs on already-overflowing text on the
    /// hot layout path.
    /// </summary>
    private static string TruncateWithEllipsis(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        var ellipsisWidth = MeasureTextWidth(context, EllipsisCharacter, textStyle);

        if (ellipsisWidth > maxWidth || text.Length == 0)
        {
            return EllipsisCharacter;
        }

        var runes = text.EnumerateRunes().ToArray();
        var low = 0;
        var high = runes.Length;

        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var prefixWidth = MeasureTextWidth(context, string.Concat(runes.Take(mid).Select(r => r.ToString())), textStyle);

            if (prefixWidth + ellipsisWidth <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return string.Concat(runes.Take(low).Select(r => r.ToString())) + EllipsisCharacter;
    }

    private static RenderFont ToRenderFont(RenderTextStyle textStyle, FontFaceSet fonts) => new(
        textStyle.FontFamily,
        textStyle.FontSize,
        textStyle.FontWeight,
        textStyle.IsItalic,
        textStyle.LetterSpacing,
        fonts);

    private static float MeasureTextWidth(LayoutContext context, string text, RenderTextStyle textStyle) =>
        context.TextMeasurer.MeasureWidth(text, ToRenderFont(textStyle, context.Fonts));

    private static float ResolveTextAlignmentOffset(TextAlign align, float availableWidth, float textWidth)
    {
        if (availableWidth <= textWidth)
        {
            return 0f;
        }

        return align switch
        {
            TextAlign.Center => (availableWidth - textWidth) / 2f,
            TextAlign.Right => availableWidth - textWidth,
            _ => 0f,
        };
    }

    /// <summary>
    /// Collapses whitespace the way <c>white-space: normal</c> always did before this property was
    /// read at all - every call site that is not itself part of text layout (table cell text, form
    /// control labels, and the two "does this text node count as non-empty content" checks used for
    /// child-ordering purposes) still goes through this, a deliberate scope cut: none of those sites
    /// feed a wrapping-aware layout path today, so a real `white-space` value on them would have
    /// nothing to change even if read.
    /// </summary>
    private static string NormalizeWhitespace(string value) => NormalizeWhitespace(value, WhiteSpaceMode.Normal);

    /// <summary>
    /// Collapses or preserves whitespace per the resolved <c>white-space</c> value, matching the CSS
    /// spec's own collapsing/newline-preservation table: <c>normal</c>/<c>nowrap</c> collapse both
    /// runs of horizontal whitespace and newlines into a single space and trim the ends; <c>pre</c>/
    /// <c>pre-wrap</c>/<c>break-spaces</c> preserve every character verbatim (only normalizing
    /// <c>\r\n</c>/<c>\r</c> line endings to a plain <c>\n</c>, the same source-text preprocessing
    /// step HTML always applies regardless of `white-space`); <c>pre-line</c> collapses runs of
    /// horizontal whitespace to a single space but preserves each `\n` as its own forced break.
    /// </summary>
    private static string NormalizeWhitespace(string value, WhiteSpaceMode whiteSpace)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var preserveNewlines = whiteSpace is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap or WhiteSpaceMode.PreLine or WhiteSpaceMode.BreakSpaces;
        var collapseHorizontalWhitespace = whiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.Nowrap or WhiteSpaceMode.PreLine;

        // Collapse \r\n/\r to a plain \n unconditionally, before any mode-specific handling - this
        // is source-text preprocessing HTML always applies regardless of `white-space`, not part of
        // the whitespace-collapsing semantics below. Doing it first (rather than per-character
        // inside the loop) also avoids double-counting a \r\n pair as two separate forced breaks
        // under pre-line's own char-by-char newline handling.
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');

        if (!collapseHorizontalWhitespace)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var inWhitespace = false;

        foreach (var c in value)
        {
            if (preserveNewlines && c == '\n')
            {
                sb.Append('\n');
                inWhitespace = false;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }

                continue;
            }

            sb.Append(c);
            inWhitespace = false;
        }

        return preserveNewlines ? sb.ToString() : sb.ToString().Trim();
    }

    /// <summary>
    /// Like <see cref="NormalizeWhitespace(string, WhiteSpaceMode)"/>, but always collapses an
    /// explicit forced break down to a plain space rather than preserving it - <see
    /// cref="LayoutInlineTextRun"/>'s word-by-word model (mixed inline content sharing a line with
    /// sibling elements) has no way to represent a forced mid-line break, so preserving one here
    /// would hand it a literal <c>\n</c> character to measure/draw as if it were an ordinary glyph.
    /// `nowrap`/`pre`'s wrap *suppression* still applies (read directly off <c>textStyle.WhiteSpace</c>
    /// inside <see cref="LayoutInlineTextRun"/> itself) - only the newline-*preservation* half of
    /// `pre`/`pre-wrap`/`pre-line` is out of scope for this specific layout path, not the whole
    /// `white-space` feature; that half is fully supported for the far more common case of `pre`/
    /// `pre-wrap`/`pre-line` on a block-level element's own direct text content (see
    /// <see cref="LayoutWrappedText"/>/<see cref="WrapTextRespectingWhiteSpace"/>).
    /// </summary>
    private static string NormalizeWhitespaceForInlineRun(string value, WhiteSpaceMode whiteSpace) =>
        NormalizeWhitespace(value, whiteSpace).Replace('\n', ' ');

    /// <summary>
    /// Resolves the flat text an inline element contributes to a shared line in the "generic plain
    /// inline element" fallback (an <see cref="ElementRenderNode"/> with no other special-cased
    /// handling - not a <c>&lt;br&gt;</c>, not inline-block, ...), which otherwise reads
    /// <c>element.TextContent</c> directly (a flat, whole-subtree DOM accessor - nested real elements
    /// beyond that are a pre-existing, separate scope cut of this fallback path, unrelated to
    /// pseudo-elements: it has never recursed into further nested elements via full layout, only
    /// concatenated their own text). Two related gaps that `.TextContent` alone cannot cover:
    /// 1. A <c>::before</c>/<c>::after</c> pseudo-element's own <c>TextContent</c> is hardcoded to
    ///    always be an empty string (<c>PseudoElement.cs</c>, in AngleSharp.Css - it has no real DOM
    ///    text content of its own to report), so a bare `inlineElement.Ref.TextContent` on the
    ///    pseudo itself silently produces no text at all.
    /// 2. A *host* element's own `.TextContent` also cannot see a pseudo child's generated text at
    ///    all (pseudo-elements exist only in the render tree, never in the DOM `.TextContent` walks),
    ///    so `<span class="required"></span>` - empty DOM text, all its visible content coming from
    ///    its own `::after` - previously vanished completely, a real, confirmed bug caught by
    ///    rendering exactly that pattern and seeing nothing painted.
    /// Both are fixed the same way: walk this element's own render-tree children in order rather
    /// than reading `.TextContent` once, appending each child's own contribution - a
    /// <see cref="TextRenderNode"/>'s data, a pseudo child's own single synthetic text child (see
    /// AngleSharp.Css's `RenderTreeBuilder`), or (preserving the exact old behavior for anything
    /// else, i.e. a real nested element) that child's own flat `.TextContent`.
    /// </summary>
    private static string ResolvePlainInlineElementText(ElementRenderNode inlineElement)
    {
        if (inlineElement.Ref is IPseudoElement)
        {
            return string.Concat(inlineElement.Children.OfType<TextRenderNode>().Select(t => t.Ref.Data));
        }

        var sb = new StringBuilder();

        foreach (var child in inlineElement.Children)
        {
            if (child is TextRenderNode textChild)
            {
                sb.Append(textChild.Ref.Data);
            }
            else if (child is ElementRenderNode elementChild)
            {
                sb.Append(elementChild.Ref is IPseudoElement
                    ? string.Concat(elementChild.Children.OfType<TextRenderNode>().Select(t => t.Ref.Data))
                    : elementChild.Ref.TextContent ?? string.Empty);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Where a cell's content sits within the box the cell occupies.
    /// </summary>
    /// <remarks>
    /// <c>baseline</c> is treated as <c>top</c>: aligning the first line boxes of every cell in a
    /// row against a shared baseline is not implemented.
    /// </remarks>
    private enum CellVerticalAlign
    {
        Top,
        Middle,
        Bottom,
    }

    private readonly record struct TableCellPlacement(
        int RowIndex,
        int ColumnIndex,
        int ColumnSpan,
        int RowSpan,
        ElementRenderNode CellNode,
        Dictionary<string, string> CellStyle,
        RenderTextStyle CellTextStyle,
        string Text,
        float PaddingLeft,
        float PaddingRight,
        float PaddingTop,
        float PaddingBottom,
        float BorderLeftWidth,
        float BorderRightWidth,
        float BorderTopWidth,
        float BorderBottomWidth,
        RenderColor BackgroundColor,
        CellVerticalAlign VerticalAlign);

    private readonly record struct RenderTextStyle(
        float FontSize,
        RenderColor Color,
        string FontFamily,
        float LineHeightMultiplier,
        float FontWeight,
        bool IsItalic,
        bool Underline,
        bool StrikeThrough,
        RenderColor DecorationColor,
        global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle DecorationStyle,
        TextAlign TextAlign,
        float LetterSpacing,
        float TextIndent,
        float VerticalAlignOffset,
        IReadOnlyList<global::AngleSharp.Renderer.Rendering.RenderTextShadow> TextShadows,
        WhiteSpaceMode WhiteSpace,
        WordBreakMode WordBreak,
        OverflowWrapMode OverflowWrap,
        TextOverflowMode TextOverflow);

    private enum TextAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// The CSS <c>white-space</c> keywords this renderer distinguishes. See
    /// <see cref="NormalizeWhitespace(string, WhiteSpaceMode)"/> for collapsing/newline-preservation
    /// and <see cref="LayoutWrappedText"/>/<see cref="LayoutInlineTextRun"/> for wrap suppression.
    /// </summary>
    private enum WhiteSpaceMode
    {
        Normal,
        Nowrap,
        Pre,
        PreWrap,
        PreLine,
        BreakSpaces,
    }

    /// <summary>
    /// <c>word-break</c>. <c>KeepAll</c> (meant for CJK text, suppressing breaks between ideographic
    /// characters that <c>Normal</c> would otherwise allow) is folded into <c>Normal</c> - this
    /// renderer has no CJK-aware line-breaking of any kind to differentiate the two, a deliberate,
    /// documented scope cut rather than an oversight.
    /// </summary>
    private enum WordBreakMode
    {
        Normal,
        BreakAll,
    }

    /// <summary>
    /// <c>overflow-wrap</c> (and its legacy <c>word-wrap</c> alias). <c>Anywhere</c> is folded into
    /// <c>BreakWord</c> - the two keywords only differ in how they affect *intrinsic* (min-content)
    /// sizing, a concept this renderer's already-approximate, non-intrinsic text layout does not
    /// model, so both simply mean "break an otherwise-unbreakable word as a last resort" here.
    /// </summary>
    private enum OverflowWrapMode
    {
        Normal,
        BreakWord,
    }

    /// <summary>
    /// <c>text-overflow</c>. Unlike <see cref="WhiteSpaceMode"/>/<see cref="WordBreakMode"/>/
    /// <see cref="OverflowWrapMode"/>, this is deliberately never inherited - see
    /// <see cref="ParseTextOverflow"/>, which mirrors <see cref="RenderTextStyle.TextIndent"/>'s own
    /// existing non-inheritance for the same reason (it targets this element's own line box, not a
    /// descendant's).
    /// </summary>
    private enum TextOverflowMode
    {
        Clip,
        Ellipsis,
    }

    private readonly record struct EdgeSizes(float Top, float Right, float Bottom, float Left);

    private enum BorderStyleKind
    {
        Solid,
        None,
        Hidden,
    }

    private readonly record struct EdgeBorderStyle(BorderStyleKind Top, BorderStyleKind Right, BorderStyleKind Bottom, BorderStyleKind Left);

    private readonly record struct BoxStyle(
        EdgeSizes Margin,
        EdgeSizes Padding,
        EdgeSizes BorderWidth,
        RenderPaint BackgroundPaint,
        RenderColor BorderColor,
        RenderCornerRadii BorderRadius,
        IReadOnlyList<RenderBoxShadow> BoxShadows);
}
