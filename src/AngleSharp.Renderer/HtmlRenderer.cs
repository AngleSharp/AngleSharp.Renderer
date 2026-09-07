namespace AngleSharp.Renderer;

using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

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
            _ = BuildDisplayList(document, viewport, context, renderDevice);
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
        var displayList = BuildDisplayList(document, viewport, context, renderDevice);

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
        return BuildDisplayList(document, viewport, context, renderDevice);
    }

    private static DisplayList BuildDisplayList(IDocument document, RenderViewport viewport, LayoutContext context, IRenderDevice renderDevice)
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
        var contentY = context.Padding;
        var contentWidth = viewport.Width - (2f * context.Padding);

        if (contentWidth <= 0f)
        {
            return displayList;
        }

        var textStyle = new RenderTextStyle(context.FontSize, context.TextColor, context.FontFamily, context.LineHeightMultiplier, 400f, false, false, false, context.TextColor, global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Solid, TextAlign.Left, 0f, 0f, 0f, []);
        var cursorY = contentY;
        var previousBlockMarginBottom = 0f;
        var suppressNextBlockTopMargin = false;
        var activeFloatLeftOffset = 0f;
        var activeFloatBottom = 0f;
        var textIndentConsumed = false;

        foreach (var child in OrderChildrenForPainting(root.Children))
        {
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
                maxY: viewport.Height - context.Padding);

            if (cursorY > viewport.Height - context.Padding)
            {
                break;
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

        var display = GetDisplay(styleMap);

        if (string.Equals(display, "table", StringComparison.OrdinalIgnoreCase))
        {
            LayoutTable(node, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, inheritedTextStyle, context, displayList, maxY);
            return;
        }

        var renderAsBlock = ShouldRenderAsBlock(computedStyle) || IsReplacedElementTag(tagName);
        var isInlineBlock = IsInlineBlock(computedStyle);
        var currentTextStyle = ResolveTextStyle(styleMap, inheritedTextStyle);

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
            var inlineText = NormalizeWhitespace(element.TextContent ?? string.Empty);
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

        var box = ResolveBoxStyle(styleMap);
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
        var borderBoxY = isFixed
            ? context.Padding + topOffset
            : isAbsolute
                ? containingY + topOffset
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
        // subtree must never be walked as if it were normal inline/block content.
        var orderedChildren = string.Equals(tagName, "svg", StringComparison.OrdinalIgnoreCase)
            ? []
            : OrderChildrenForPainting(node.Children).ToList();
        var hasInlineRun = orderedChildren.Any(child =>
            (child is ElementRenderNode childElement &&
             !ShouldRenderAsBlock(childElement.ComputedStyle) &&
             !IsInlineBlock(childElement.ComputedStyle)) ||
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
                var childIsBlock = child is ElementRenderNode childElement && (ShouldRenderAsBlock(childElement.ComputedStyle) || IsInlineBlock(childElement.ComputedStyle));

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
                        var inlineText = NormalizeWhitespace(textNode.Ref.Data);

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
                            var inlineText = NormalizeWhitespace(inlineElement.Ref.TextContent ?? string.Empty);

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

        var boxPaintBuffer = new DisplayList();
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

        var columns = ParseGridTrackList(styleMap, "grid-template-columns", containingWidth, 1);
        var columnGap = ParseGridGap(styleMap, "column-gap", containingWidth, 0)
            ?? ParseGridGap(styleMap, "gap", containingWidth, 0);
        var rowGap = ParseGridGap(styleMap, "row-gap", containingWidth, 0)
            ?? ParseGridGap(styleMap, "gap", containingWidth, 0);
        var resolvedColumnGap = columnGap ?? 0f;
        var resolvedRowGap = rowGap ?? 0f;
        var gridItems = node.Children
            .Where(child => child is ElementRenderNode || (child is TextRenderNode textNode && NormalizeWhitespace(textNode.Ref.Data).Length > 0))
            .ToList();
        var hasExplicitRowTracks = styleMap.TryGetValue("grid-template-rows", out var rowTemplateValue) && !string.IsNullOrWhiteSpace(rowTemplateValue);
        var containerHeight = ParseLength(styleMap, "height", containingWidth, containingWidth, allowAuto: true);
        var rows = hasExplicitRowTracks
            ? ParseGridTrackList(styleMap, "grid-template-rows", containerHeight, 1)
            : CreateAutoRows(gridItems.Count, columns.Count, containerHeight);

        var currentColumn = 0;
        var currentRow = 0;

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

            var placementColumn = ResolveGridPlacement(styleMap, elementChild, "grid-column", currentColumn);
            var placementRow = ResolveGridPlacement(styleMap, elementChild, "grid-row", currentRow);
            var effectivePlacementColumn = placementColumn;
            var effectivePlacementRow = placementRow;

            var hasExplicitColumnPlacement = elementChild.Ref.GetAttribute("data-render-grid-column") is not null;
            var hasExplicitRowPlacement = elementChild.Ref.GetAttribute("data-render-grid-row") is not null;

            if (hasExplicitColumnPlacement || hasExplicitRowPlacement)
            {
                effectivePlacementColumn = new GridPlacement(Math.Max(0, placementColumn.LineIndex), placementColumn.Span);
                effectivePlacementRow = new GridPlacement(Math.Max(0, placementRow.LineIndex), placementRow.Span);
            }
            else
            {
                effectivePlacementColumn = new GridPlacement(Math.Max(0, currentColumn), placementColumn.Span);
                effectivePlacementRow = new GridPlacement(Math.Max(0, currentRow), placementRow.Span);
            }
            var estimatedItemWidth = ResolveGridItemEstimatedSize(elementChild, styleMap, containingWidth, "width");
            var estimatedItemHeight = ResolveGridItemEstimatedSize(elementChild, styleMap, containingWidth, "height");
            var effectiveColumnCount = Math.Max(columns.Count, effectivePlacementColumn.LineIndex + effectivePlacementColumn.Span);
            var effectiveRowCount = Math.Max(rows.Count, effectivePlacementRow.LineIndex + effectivePlacementRow.Span);

            if (effectiveColumnCount > columns.Count)
            {
                columns.AddRange(Enumerable.Repeat(containingWidth, effectiveColumnCount - columns.Count));
            }

            if (effectiveRowCount > rows.Count)
            {
                rows.AddRange(Enumerable.Repeat(0f, effectiveRowCount - rows.Count));
            }

            EnsureGridTrackSize(columns, effectivePlacementColumn.LineIndex, estimatedItemWidth, containingWidth);
            EnsureGridTrackSize(rows, effectivePlacementRow.LineIndex, estimatedItemHeight, 0f);

            var contentX = borderBoxX + borderLeft + paddingLeft;
            var contentY = borderBoxY + borderTop + paddingTop;
            var cellX = contentX + GetGridTrackOffset(columns, effectivePlacementColumn.LineIndex, resolvedColumnGap);
            var cellY = contentY + GetGridTrackOffset(rows, effectivePlacementRow.LineIndex, resolvedRowGap);
            var cellWidth = GetGridTrackSpanSize(columns, effectivePlacementColumn.LineIndex, effectivePlacementColumn.Span, resolvedColumnGap, containingWidth);
            var cellHeight = GetGridTrackSpanSize(rows, effectivePlacementRow.LineIndex, effectivePlacementRow.Span, resolvedRowGap, containingWidth);

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

            currentColumn++;
            if (currentColumn >= columns.Count)
            {
                currentColumn = 0;
                currentRow++;
            }
        }

        var gridContentWidth = GetGridContentSize(columns, resolvedColumnGap, containingWidth);
        var specifiedHeight = ParseLength(styleMap, "height", containingWidth, containingWidth, allowAuto: true);
        var gridContentHeight = GetGridContentSize(rows, resolvedRowGap, specifiedHeight);
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

    private static List<float> ParseGridTrackList(Dictionary<string, string> styleMap, string propertyName, float fallbackSize, int minimumCount)
    {
        if (!styleMap.TryGetValue(propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            var fallbackTracks = new List<float>(Math.Max(1, minimumCount));
            var fallbackTrackSize = Math.Max(0f, fallbackSize / Math.Max(1, minimumCount));
            for (var index = 0; index < Math.Max(1, minimumCount); index++)
            {
                fallbackTracks.Add(fallbackTrackSize);
            }

            return fallbackTracks;
        }

        var tracks = new List<float>();
        foreach (var token in rawValue.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.Trim().ToLowerInvariant();
            var trackSize = normalized switch
            {
                "auto" => Math.Max(0f, fallbackSize),
                _ => ParseLengthValue(normalized, fallbackSize, allowAuto: false)
            };

            tracks.Add(float.IsNaN(trackSize) ? Math.Max(0f, fallbackSize) : Math.Max(0f, trackSize));
        }

        return tracks.Count > 0 ? tracks : new List<float> { Math.Max(0f, fallbackSize) };
    }

    private static List<float> CreateAutoRows(int itemCount, int columnCount, float containerHeight)
    {
        var rowCount = Math.Max(1, (int)Math.Ceiling((double)itemCount / Math.Max(1, columnCount)));
        var fallbackRowSize = containerHeight > 0f ? containerHeight / rowCount : 0f;
        return Enumerable.Range(0, rowCount).Select(_ => fallbackRowSize).ToList();
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

    private static GridPlacement ResolveGridPlacement(Dictionary<string, string> styleMap, ElementRenderNode elementChild, string propertyName, int fallbackIndex)
    {
        var childStyleMap = CreateStyleMap(elementChild.ComputedStyle, elementChild.Ref);
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

    private static void EnsureGridTrackSize(List<float> tracks, int index, float size, float fallbackSize)
    {
        while (tracks.Count <= index)
        {
            tracks.Add(Math.Max(0f, fallbackSize));
        }

        if (tracks[index] <= 0f)
        {
            tracks[index] = Math.Max(0f, Math.Max(size, fallbackSize));
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
        var text = NormalizeWhitespace(textNode.Data);

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
        var lines = WrapText(context, text, maxWidth, textStyle);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            cursorY += lineHeight;

            if (cursorY > maxY)
            {
                return;
            }

            var lineWidth = MeasureTextWidth(context, line, textStyle);
            var lineMaxWidth = index == 0 ? Math.Max(0f, maxWidth - firstLineIndent) : maxWidth;
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

        foreach (var word in words)
        {
            var wordWidth = MeasureTextWidth(context, word, textStyle);

            if (inlineCursorX > flowX && inlineCursorX + spaceWidth + wordWidth > rightEdge)
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

        return new RenderTextStyle(fontSize, color, fontFamily, lineHeight, fontWeight, isItalic, underline, strikeThrough, decorationColor, decorationStyle, textAlign, letterSpacing, textIndent, verticalAlignOffset, textShadows);
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

            if (TryExtractGradientBackground(currentStyle, out var gradientValue, out var updatedStyle))
            {
                currentStyle = updatedStyle;
                changed = true;
                element.SetAttribute("data-render-gradient", gradientValue);
            }

            if (TryExtractGridDeclarations(currentStyle, out var gridValues, out updatedStyle))
            {
                currentStyle = updatedStyle;
                changed = true;

                foreach (var entry in gridValues)
                {
                    element.SetAttribute($"data-render-{entry.Key}", entry.Value);
                }
            }

            if (changed)
            {
                element.SetAttribute("style", currentStyle);
            }
        }
    }

    private static bool TryExtractGradientBackground(string styleAttribute, out string gradientValue, out string updatedStyle)
    {
        gradientValue = string.Empty;
        updatedStyle = styleAttribute;

        if (!styleAttribute.Contains("background-image", StringComparison.OrdinalIgnoreCase))
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

            if (string.Equals(property, "background-image", StringComparison.OrdinalIgnoreCase) &&
                (value.StartsWith("linear-gradient", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("radial-gradient", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("conic-gradient", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("repeating-linear-gradient", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("repeating-radial-gradient", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("repeating-conic-gradient", StringComparison.OrdinalIgnoreCase)))
            {
                gradientValue = value;
                continue;
            }

            remaining.Add(declaration);
        }

        if (string.IsNullOrWhiteSpace(gradientValue))
        {
            return false;
        }

        updatedStyle = string.Join(";", remaining);
        return true;
    }

    private static bool TryExtractGridDeclarations(string styleAttribute, out Dictionary<string, string> values, out string updatedStyle)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        updatedStyle = styleAttribute;

        if (string.IsNullOrWhiteSpace(styleAttribute))
        {
            return false;
        }

        var declarations = styleAttribute.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var remaining = new List<string>();
        var strippedAny = false;

        foreach (var declaration in declarations)
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var property = declaration[..separator].Trim();
            var value = declaration[(separator + 1)..].Trim();

            if (string.Equals(property, "grid-column", StringComparison.OrdinalIgnoreCase))
            {
                values["grid-column"] = value;
                strippedAny = true;
                continue;
            }

            if (string.Equals(property, "grid-row", StringComparison.OrdinalIgnoreCase))
            {
                values["grid-row"] = value;
                strippedAny = true;
                continue;
            }

            if (string.Equals(property, "grid-template-columns", StringComparison.OrdinalIgnoreCase))
            {
                values["grid-template-columns"] = value;
                strippedAny = true;
                continue;
            }

            if (string.Equals(property, "grid-template-rows", StringComparison.OrdinalIgnoreCase))
            {
                values["grid-template-rows"] = value;
                strippedAny = true;
                continue;
            }

            if (string.Equals(property, "column-gap", StringComparison.OrdinalIgnoreCase))
            {
                values["column-gap"] = value;
                strippedAny = true;
                continue;
            }

            if (string.Equals(property, "row-gap", StringComparison.OrdinalIgnoreCase))
            {
                values["row-gap"] = value;
                strippedAny = true;
                continue;
            }

            if (string.Equals(property, "gap", StringComparison.OrdinalIgnoreCase))
            {
                values["gap"] = value;
                strippedAny = true;
                continue;
            }

            remaining.Add(declaration);
        }

        if (!strippedAny)
        {
            return false;
        }

        updatedStyle = string.Join(";", remaining);
        return true;
    }

    private static Dictionary<string, string> CreateStyleMap(ICssStyleDeclaration style, IElement? element = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inlineStyle = element?.GetAttribute("style");
        var gridColumnValue = element?.GetAttribute("data-render-grid-column");
        var gridRowValue = element?.GetAttribute("data-render-grid-row");
        var gridTemplateColumnsValue = element?.GetAttribute("data-render-grid-template-columns");
        var gridTemplateRowsValue = element?.GetAttribute("data-render-grid-template-rows");
        var columnGapValue = element?.GetAttribute("data-render-column-gap");
        var rowGapValue = element?.GetAttribute("data-render-row-gap");
        var gapValue = element?.GetAttribute("data-render-gap");

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

        AddIfPresent(map, "overflow", style.GetPropertyValue("overflow"));
        AddIfPresent(map, "overflow-x", style.GetPropertyValue("overflow-x"));
        AddIfPresent(map, "overflow-y", style.GetPropertyValue("overflow-y"));

        AddIfPresent(map, "outline-width", style.GetPropertyValue("outline-width"));
        AddIfPresent(map, "outline-style", style.GetPropertyValue("outline-style"));
        AddIfPresent(map, "outline-color", style.GetPropertyValue("outline-color"));

        AddIfPresent(map, "background-color", style.GetBackgroundColor());
        AddIfPresent(map, "grid-template-columns", !string.IsNullOrWhiteSpace(gridTemplateColumnsValue) ? gridTemplateColumnsValue : (string.IsNullOrWhiteSpace(style.GetPropertyValue("grid-template-columns")) ? ParseStyleAttributeValue(inlineStyle, "grid-template-columns") : style.GetPropertyValue("grid-template-columns")));
        AddIfPresent(map, "grid-template-rows", !string.IsNullOrWhiteSpace(gridTemplateRowsValue) ? gridTemplateRowsValue : (string.IsNullOrWhiteSpace(style.GetPropertyValue("grid-template-rows")) ? ParseStyleAttributeValue(inlineStyle, "grid-template-rows") : style.GetPropertyValue("grid-template-rows")));
        AddIfPresent(map, "column-gap", !string.IsNullOrWhiteSpace(columnGapValue) ? columnGapValue : (string.IsNullOrWhiteSpace(style.GetPropertyValue("column-gap")) ? ParseStyleAttributeValue(inlineStyle, "column-gap") : style.GetPropertyValue("column-gap")));
        AddIfPresent(map, "row-gap", !string.IsNullOrWhiteSpace(rowGapValue) ? rowGapValue : (string.IsNullOrWhiteSpace(style.GetPropertyValue("row-gap")) ? ParseStyleAttributeValue(inlineStyle, "row-gap") : style.GetPropertyValue("row-gap")));
        AddIfPresent(map, "gap", !string.IsNullOrWhiteSpace(gapValue) ? gapValue : (string.IsNullOrWhiteSpace(style.GetPropertyValue("gap")) ? ParseStyleAttributeValue(inlineStyle, "gap") : style.GetPropertyValue("gap")));
        AddIfPresent(map, "grid-column", !string.IsNullOrWhiteSpace(gridColumnValue) ? gridColumnValue : ParseStyleAttributeValue(inlineStyle, "grid-column"));
        AddIfPresent(map, "grid-row", !string.IsNullOrWhiteSpace(gridRowValue) ? gridRowValue : ParseStyleAttributeValue(inlineStyle, "grid-row"));
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

        var backgroundImageValue = element is not null
            ? element.GetAttribute("data-render-gradient")
            : null;

        if (!string.IsNullOrWhiteSpace(backgroundImageValue))
        {
            AddIfPresent(map, "background-image", backgroundImageValue);
        }
        else
        {
            AddIfPresent(map, "background-image", style.GetPropertyValue("background-image"));
        }
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

        return map;
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

    private static BoxStyle ResolveBoxStyle(Dictionary<string, string> styleMap)
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
        var backgroundPaint = ParseBackgroundPaint(styleMap, backgroundColor);
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

        if (!string.Equals(node.Ref.LocalName, "img", StringComparison.OrdinalIgnoreCase))
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

        if (!string.Equals(node.Ref.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
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
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var cacheKey = source.Trim();
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

        if (!TryLoadImageResource(element, source, out imageResource))
        {
            if (cache is not null)
            {
                lock (cache.Resources)
                {
                    cache.Resources[cacheKey] = null;
                }
            }

            return false;
        }

        if (cache is not null)
        {
            lock (cache.Resources)
            {
                cache.Resources[cacheKey] = imageResource;
            }
        }

        return true;
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

        if (bytes is null || bytes.Length == 0)
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

        var naturalWidth = skImage.Width;
        var naturalHeight = skImage.Height;

        imageResource = new CachedImageResource(bytes, mimeType ?? "image/unknown", naturalWidth, naturalHeight);
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
    /// Resolves one overflow axis. AngleSharp.Css's `overflow` shorthand does not decompose into
    /// `overflow-x`/`overflow-y` in its computed style (verified empirically) - unlike a real
    /// cascade, the longhand and shorthand never coexist in the computed declaration here, so the
    /// longhand is preferred when both happen to be present and the shorthand is used as a
    /// fallback, rather than needing to resolve cascade precedence between them.
    /// </summary>
    private static string ResolveOverflowAxis(Dictionary<string, string> styleMap, string longhandProperty) =>
        styleMap.TryGetValue(longhandProperty, out var axisValue) && !string.IsNullOrWhiteSpace(axisValue)
            ? axisValue.Trim().ToLowerInvariant()
            : styleMap.TryGetValue("overflow", out var shorthandValue) && !string.IsNullOrWhiteSpace(shorthandValue)
                ? shorthandValue.Trim().ToLowerInvariant()
                : "visible";

    private static bool IsClippingOverflowValue(string overflowValue) =>
        overflowValue is "hidden" or "scroll" or "auto";

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

        if (paint is RenderGradientPaint)
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

    private static float ParseLengthValue(string value, float defaultValue, bool allowAuto = true)
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

    private static RenderPaint ParseBackgroundPaint(Dictionary<string, string> styleMap, RenderColor fallbackColor)
    {
        if (!styleMap.TryGetValue("background-image", out var backgroundImage) || string.IsNullOrWhiteSpace(backgroundImage))
        {
            return new RenderColorPaint(fallbackColor);
        }

        return ParseGradientPaint(backgroundImage, fallbackColor);
    }

    private static RenderPaint ParseGradientPaint(string rawValue, RenderColor fallbackColor)
    {
        var value = rawValue.Trim();

        if (value.StartsWith("repeating-linear-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseLinearGradient(value, "repeating-linear-gradient", repeating: true, fallbackColor));
        }

        if (value.StartsWith("linear-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseLinearGradient(value, "linear-gradient", repeating: false, fallbackColor));
        }

        if (value.StartsWith("repeating-radial-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseRadialGradient(value, "repeating-radial-gradient", repeating: true, fallbackColor));
        }

        if (value.StartsWith("radial-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseRadialGradient(value, "radial-gradient", repeating: false, fallbackColor));
        }

        if (value.StartsWith("repeating-conic-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseConicGradient(value, "repeating-conic-gradient", repeating: true, fallbackColor));
        }

        if (value.StartsWith("conic-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseConicGradient(value, "conic-gradient", repeating: false, fallbackColor));
        }

        return new RenderColorPaint(fallbackColor);
    }

    private static RenderGradient ParseLinearGradient(string rawValue, string functionName, bool repeating, RenderColor fallbackColor)
    {
        var inner = ExtractGradientInnerExpression(rawValue, functionName);
        var parts = SplitTopLevelCommaList(inner);
        var startIndex = 0;
        var angleDegrees = 90f;

        if (parts.Length > 0)
        {
            var first = parts[0].Trim();

            if (TryParseDirection(first, out var parsedAngle))
            {
                angleDegrees = parsedAngle;
                startIndex = 1;
            }
        }

        var stops = ParseGradientStops(parts.Skip(startIndex).ToArray(), fallbackColor);
        return new RenderGradient(RenderGradientKind.Linear, stops, AngleDegrees: angleDegrees, Repeating: repeating);
    }

    private static RenderGradient ParseRadialGradient(string rawValue, string functionName, bool repeating, RenderColor fallbackColor)
    {
        var inner = ExtractGradientInnerExpression(rawValue, functionName);
        var parts = SplitTopLevelCommaList(inner);
        var startIndex = 0;

        var isCircle = false;
        var sizeKind = RenderGradientSizeKind.FarthestCorner;
        float? explicitRadiusX = null;
        float? explicitRadiusY = null;
        var centerX = 0.5f;
        var centerY = 0.5f;

        if (parts.Length > 0 && LooksLikeRadialConfiguration(parts[0]))
        {
            var configText = parts[0].Trim();
            startIndex = 1;

            var atIndex = configText.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
            var shapeSizeText = atIndex >= 0 ? configText[..atIndex].Trim() : configText;
            var positionText = atIndex >= 0 ? configText[(atIndex + 4)..].Trim() : null;

            ParseRadialShapeAndSize(shapeSizeText, out isCircle, out sizeKind, out explicitRadiusX, out explicitRadiusY);

            if (positionText is not null)
            {
                (centerX, centerY) = ParsePosition(positionText);
            }
        }

        var stops = ParseGradientStops(parts.Skip(startIndex).ToArray(), fallbackColor);
        return new RenderGradient(
            RenderGradientKind.Radial,
            stops,
            CenterX: centerX,
            CenterY: centerY,
            IsCircle: isCircle,
            Repeating: repeating,
            SizeKind: sizeKind,
            ExplicitRadiusX: explicitRadiusX,
            ExplicitRadiusY: explicitRadiusY);
    }

    private static RenderGradient ParseConicGradient(string rawValue, string functionName, bool repeating, RenderColor fallbackColor)
    {
        var inner = ExtractGradientInnerExpression(rawValue, functionName);
        var parts = SplitTopLevelCommaList(inner);
        var startIndex = 0;
        var angleDegrees = 0f;
        var centerX = 0.5f;
        var centerY = 0.5f;

        if (parts.Length > 0)
        {
            var first = parts[0].Trim();

            if (first.StartsWith("from", StringComparison.OrdinalIgnoreCase) || first.StartsWith("at", StringComparison.OrdinalIgnoreCase))
            {
                var atIndex = first.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
                var fromText = atIndex >= 0 ? first[..atIndex].Trim() : first;
                var positionText = atIndex >= 0
                    ? first[(atIndex + 4)..].Trim()
                    : (first.StartsWith("at", StringComparison.OrdinalIgnoreCase) ? first[2..].Trim() : null);

                if (fromText.StartsWith("from", StringComparison.OrdinalIgnoreCase))
                {
                    angleDegrees = ParseAngle(fromText[4..].Trim());
                }

                if (positionText is not null)
                {
                    (centerX, centerY) = ParsePosition(positionText);
                }

                startIndex = 1;
            }
        }

        var stops = ParseGradientStops(parts.Skip(startIndex).ToArray(), fallbackColor, isConic: true);
        return new RenderGradient(RenderGradientKind.Conic, stops, AngleDegrees: angleDegrees, CenterX: centerX, CenterY: centerY, Repeating: repeating);
    }

    /// <summary>
    /// Distinguishes a radial-gradient's leading `&lt;ending-shape&gt; || &lt;size&gt; [at
    /// &lt;position&gt;]` configuration clause from what is actually just its first color stop -
    /// a color stop never starts with a shape/size keyword, "at", or a bare length.
    /// </summary>
    private static bool LooksLikeRadialConfiguration(string part)
    {
        var lower = part.Trim().ToLowerInvariant();

        return lower.StartsWith("circle", StringComparison.Ordinal) ||
               lower.StartsWith("ellipse", StringComparison.Ordinal) ||
               lower.StartsWith("closest-", StringComparison.Ordinal) ||
               lower.StartsWith("farthest-", StringComparison.Ordinal) ||
               lower.StartsWith("at ", StringComparison.Ordinal) ||
               lower.Contains(" at ", StringComparison.Ordinal) ||
               (TryParsePixelValue(lower.Split(' ')[0], out _) && !lower.Contains(','));
    }

    private static void ParseRadialShapeAndSize(string text, out bool isCircle, out RenderGradientSizeKind sizeKind, out float? explicitRadiusX, out float? explicitRadiusY)
    {
        isCircle = false;
        sizeKind = RenderGradientSizeKind.FarthestCorner;
        explicitRadiusX = null;
        explicitRadiusY = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lengths = new List<float>();

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "circle":
                    isCircle = true;
                    break;
                case "ellipse":
                    isCircle = false;
                    break;
                case "closest-side":
                    sizeKind = RenderGradientSizeKind.ClosestSide;
                    break;
                case "farthest-side":
                    sizeKind = RenderGradientSizeKind.FarthestSide;
                    break;
                case "closest-corner":
                    sizeKind = RenderGradientSizeKind.ClosestCorner;
                    break;
                case "farthest-corner":
                    sizeKind = RenderGradientSizeKind.FarthestCorner;
                    break;
                default:
                    if (TryParsePixelValue(token, out var pixels))
                    {
                        lengths.Add(pixels);
                    }

                    break;
            }
        }

        if (lengths.Count > 0)
        {
            sizeKind = RenderGradientSizeKind.Explicit;
            explicitRadiusX = lengths[0];
            explicitRadiusY = lengths.Count > 1 ? lengths[1] : lengths[0];

            if (lengths.Count == 1)
            {
                // A single explicit length implies a circle - CSS grammar doesn't allow one
                // length with an explicit "ellipse" keyword (that needs two lengths).
                isCircle = true;
            }
        }
    }

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

    private static string ExtractGradientInnerExpression(string rawValue, string functionName)
    {
        if (!rawValue.StartsWith(functionName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var opening = rawValue.IndexOf('(');
        var closing = rawValue.LastIndexOf(')');

        if (opening < 0 || closing <= opening)
        {
            return string.Empty;
        }

        return rawValue[(opening + 1)..closing].Trim();
    }

    private static string[] SplitTopLevelCommaList(string value)
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

    private static IReadOnlyList<RenderGradientStop> ParseGradientStops(string[] parts, RenderColor fallbackColor, bool isConic = false)
    {
        if (parts.Length == 0)
        {
            return [new RenderGradientStop(0f, fallbackColor)];
        }

        var stops = new List<RenderGradientStop>(parts.Length);

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index].Trim();
            if (part.Length == 0)
            {
                continue;
            }

            var separatorIndex = part.IndexOfAny([ ' ', '\t', '\n', '\r' ]);
            var colorToken = separatorIndex >= 0 ? part[..separatorIndex].Trim() : part;
            var positionToken = separatorIndex >= 0 ? part[(separatorIndex + 1)..].Trim() : string.Empty;

            var color = ParseColor(colorToken, fallbackColor);
            var autoPosition = parts.Length == 1 ? 0f : (index / (float)Math.Max(1, parts.Length - 1));

            if (string.IsNullOrWhiteSpace(positionToken))
            {
                stops.Add(new RenderGradientStop(autoPosition, color));
            }
            else if (!isConic && TryParsePixelValue(positionToken, out var pixels))
            {
                // An absolute-length stop position ("red 10px") cannot become a fraction until
                // the gradient's own rendered geometry (line length/radius) is known, so the raw
                // pixel value is carried through and resolved by the backend at paint time.
                stops.Add(new RenderGradientStop(autoPosition, color, pixels));
            }
            else
            {
                stops.Add(new RenderGradientStop(ParseStopPosition(positionToken, isConic), color));
            }
        }

        return stops;
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

    private static bool TryParseDirection(string value, out float angleDegrees)
    {
        angleDegrees = 90f;
        var normalized = value.Trim().ToLowerInvariant();

        if (normalized.StartsWith("to ", StringComparison.Ordinal))
        {
            var direction = normalized[3..].Trim();
            angleDegrees = direction switch
            {
                "top" => 270f,
                "right" => 0f,
                "bottom" => 90f,
                "left" => 180f,
                "top right" or "right top" => 315f,
                "top left" or "left top" => 225f,
                "bottom right" or "right bottom" => 45f,
                "bottom left" or "left bottom" => 135f,
                _ => 90f,
            };
            return true;
        }

        return TryParseAngle(normalized, out angleDegrees);
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

    private static RenderColor ParseColor(string? rawColor, RenderColor fallback)
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

    private static IReadOnlyList<string> WrapText(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0f;

        foreach (var word in words)
        {
            var wordWidth = MeasureTextWidth(context, word, textStyle);
            var separatorWidth = current.Length == 0 ? 0f : MeasureTextWidth(context, " ", textStyle);

            if (current.Length > 0 && currentWidth + separatorWidth + wordWidth > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0f;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
                currentWidth += separatorWidth;
            }

            current.Append(word);
            currentWidth += wordWidth;
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
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

    private static string NormalizeWhitespace(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        var inWhitespace = false;

        foreach (var c in value)
        {
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

        return sb.ToString().Trim();
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
        IReadOnlyList<global::AngleSharp.Renderer.Rendering.RenderTextShadow> TextShadows);

    private enum TextAlign
    {
        Left,
        Center,
        Right,
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
