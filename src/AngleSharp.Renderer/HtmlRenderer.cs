using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

using SkiaSharp;

namespace AngleSharp.Renderer;

/// <summary>
/// Renders HTML documents into image output.
/// </summary>
public sealed class HtmlRenderer
{
    private readonly IRenderBackend _backend;

    /// <summary>
    /// Creates a new renderer with a default Skia backend.
    /// </summary>
    public HtmlRenderer()
        : this(new SkiaRenderBackend())
    {
    }

    /// <summary>
    /// Creates a new renderer with a specific backend.
    /// </summary>
    /// <param name="backend">The backend used for rasterization.</param>
    public HtmlRenderer(IRenderBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
    }

    /// <summary>
    /// Renders the given document to a PNG image.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="options">Optional rendering settings.</param>
    /// <returns>The rendered PNG image.</returns>
    public RenderedImage RenderToPng(IDocument document, HtmlRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var effectiveOptions = options ?? new HtmlRenderOptions();
        var viewport = new RenderViewport(effectiveOptions.Width, effectiveOptions.Height);
        var displayList = BuildDisplayList(document, viewport, effectiveOptions);

        return _backend.RenderToPng(displayList, viewport);
    }

    /// <summary>
    /// Builds a display list from the given document.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="options">Optional rendering settings.</param>
    /// <returns>The generated display list.</returns>
    public DisplayList BuildDisplayList(IDocument document, HtmlRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var effectiveOptions = options ?? new HtmlRenderOptions();
        var viewport = new RenderViewport(effectiveOptions.Width, effectiveOptions.Height);
        return BuildDisplayList(document, viewport, effectiveOptions);
    }

    private static DisplayList BuildDisplayList(IDocument document, RenderViewport viewport, HtmlRenderOptions options)
    {
        var displayList = new DisplayList();
        displayList.FillRect(new RenderRect(0f, 0f, viewport.Width, viewport.Height), options.BackgroundColor);

        var window = document.DefaultView;
        if (window is null)
        {
            return displayList;
        }

        PrepareDocumentForRendering(document);

        var renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = viewport.Width,
            ViewPortHeight = viewport.Height,
            DeviceWidth = viewport.Width,
            DeviceHeight = viewport.Height,
            FontSize = options.FontSize,
        };

        var renderTree = window.Render(renderDevice);
        var body = document.Body;
        var root = body is null ? renderTree : renderTree.Find(body) ?? renderTree;

        var contentX = options.Padding;
        var contentY = options.Padding;
        var contentWidth = viewport.Width - (2f * options.Padding);

        if (contentWidth <= 0f)
        {
            return displayList;
        }

        var textStyle = new RenderTextStyle(options.FontSize, options.TextColor, options.FontFamily, options.LineHeightMultiplier, 400f, false, false, false, options.TextColor, global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Solid, TextAlign.Left, 0f, 0f, 0f);
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
                options: options,
                displayList: displayList,
                maxY: viewport.Height - options.Padding);

            if (cursorY > viewport.Height - options.Padding)
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
        HtmlRenderOptions options,
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
                LayoutTextNode(textNode.Ref, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, textStyle, options, displayList, maxY);
                return;
            case ElementRenderNode element:
                LayoutElement(element, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, textStyle, options, displayList, maxY, isFlexItem, isRowDirection, flexMainSize, flexCrossSize);
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
        HtmlRenderOptions options,
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
            LayoutTable(node, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, inheritedTextStyle, options, displayList, maxY);
            return;
        }

        var renderAsBlock = ShouldRenderAsBlock(computedStyle) || string.Equals(tagName, "img", StringComparison.OrdinalIgnoreCase);
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
                    LayoutWrappedText(inlineText, flowContainingX, flowContainingWidth, ref cursorY, currentTextStyle, options, displayList, maxY, textIndentConsumed ? 0f : currentTextStyle.TextIndent);
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
            ? options.Padding + leftOffset
            : isAbsolute
                ? containingX + leftOffset
                : flowBorderBoxX + (isRelative ? leftOffset : 0f);
        var borderBoxY = isFixed
            ? options.Padding + topOffset
            : isAbsolute
                ? containingY + topOffset
                : flowBorderBoxY + (isRelative ? topOffset : 0f);
        var contentX = borderBoxX + borderLeft + paddingLeft;
        var contentY = borderBoxY + borderTop + paddingTop;

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

        var orderedChildren = OrderChildrenForPainting(node.Children).ToList();
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
                options,
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
                options,
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
                        options,
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
                        options: options,
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
                        options: options,
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
                                options.AverageCharacterWidthFactor,
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
                                    options.AverageCharacterWidthFactor,
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

        PaintBackground(displayList, box.BackgroundPaint, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        PaintBorder(displayList, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth);
        PaintOutline(displayList, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        if (string.Equals(tagName, "img", StringComparison.OrdinalIgnoreCase) &&
            TryResolveImage(node, styleMap, flowContainingWidth, borderBoxX + borderLeft + paddingLeft, borderBoxY + borderTop + paddingTop, out var image, out var imageRect))
        {
            displayList.DrawImage(imageRect, image!);
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
        previousBlockMarginBottom = effectiveMarginBottom + options.ParagraphSpacing;
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
        HtmlRenderOptions options,
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
                    LayoutTextNode(textNode.Ref, childX, containingWidth, ref childCursorY, ref childPreviousBlockMarginBottom, ref childSuppressNextBlockTopMargin, ref childActiveFloatLeftOffset, ref childActiveFloatBottom, ref childTextIndentConsumed, inheritedTextStyle, options, displayList, maxY);
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
                        options: options,
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

        if (box.BackgroundPaint is RenderColorPaint colorPaint && colorPaint.Color.A == 0)
        {
            displayList.FillRect(new RenderRect(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight), RenderColor.Transparent);
        }
        else
        {
            PaintBackground(displayList, box.BackgroundPaint, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        }

        PaintBorder(displayList, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth);
        PaintOutline(displayList, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        if (string.Equals(node.Ref.LocalName, "img", StringComparison.OrdinalIgnoreCase) &&
            TryResolveImage(node, styleMap, containingWidth, borderBoxX + borderLeft + paddingLeft, borderBoxY + borderTop + paddingTop, out var image, out var imageRect))
        {
            displayList.DrawImage(imageRect, image!);
        }

        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + options.ParagraphSpacing;
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
        HtmlRenderOptions options,
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

        var tableCells = new List<(int RowIndex, int ColumnIndex, int ColumnSpan, int RowSpan, ElementRenderNode CellNode, Dictionary<string, string> CellStyle, RenderTextStyle CellTextStyle, string Text, float PaddingLeft, float PaddingRight, float PaddingTop, float PaddingBottom, float BorderLeftWidth, float BorderRightWidth, float BorderTopWidth, float BorderBottomWidth, RenderColor BackgroundColor)>();
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

                tableCells.Add((rowIndex, currentColumnIndex, colspan, rowspan, cellNode, cellStyle, cellTextStyle, text,
                    ParseLength(cellStyle, "padding-left", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "padding-right", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "padding-top", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "padding-bottom", containingWidth, 4f, allowAuto: false),
                    ParseLength(cellStyle, "border-left-width", containingWidth, 1f, allowAuto: false),
                    ParseLength(cellStyle, "border-right-width", containingWidth, 1f, allowAuto: false),
                    ParseLength(cellStyle, "border-top-width", containingWidth, 1f, allowAuto: false),
                    ParseLength(cellStyle, "border-bottom-width", containingWidth, 1f, allowAuto: false),
                    ParseColor(cellStyle.TryGetValue("background-color", out var backgroundColor) ? backgroundColor : null, RenderColor.Transparent)));

                columnCount = Math.Max(columnCount, currentColumnIndex + colspan);
                currentColumnIndex += colspan;
            }

            foreach (var index in Enumerable.Range(0, rowSpanOccupancy.Count))
            {
                if (rowSpanOccupancy[index] > 0)
                {
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
            var textWidth = placement.Text.Length > 0 ? EstimateTextWidth(placement.Text, placement.CellTextStyle.FontSize, options.AverageCharacterWidthFactor, placement.CellTextStyle.LetterSpacing) : 0f;
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

        foreach (var placement in tableCells)
        {
            var contentWidth = Math.Max(0f, columnWidths.Skip(placement.ColumnIndex).Take(placement.ColumnSpan).Sum() - placement.PaddingLeft - placement.PaddingRight - placement.BorderLeftWidth - placement.BorderRightWidth);
            var contentHeight = 0f;
            var text = placement.Text;

            if (contentWidth > 0f && text.Length > 0)
            {
                var wrappedLines = WrapText(text, contentWidth, placement.CellTextStyle.FontSize, options.AverageCharacterWidthFactor, placement.CellTextStyle.LetterSpacing);
                var lineHeight = placement.CellTextStyle.FontSize * placement.CellTextStyle.LineHeightMultiplier;
                contentHeight = wrappedLines.Count * lineHeight;
            }

            var effectiveHeight = Math.Max(20f, contentHeight + placement.PaddingTop + placement.PaddingBottom + placement.BorderTopWidth + placement.BorderBottomWidth);
            for (var rowIndex = placement.RowIndex; rowIndex < placement.RowIndex + placement.RowSpan; rowIndex++)
            {
                rowHeights[rowIndex] = Math.Max(rowHeights[rowIndex], effectiveHeight);
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

            var contentHeight = 0f;
            if (contentWidth > 0f && placement.Text.Length > 0)
            {
                var wrappedLines = WrapText(placement.Text, contentWidth, placement.CellTextStyle.FontSize, options.AverageCharacterWidthFactor, placement.CellTextStyle.LetterSpacing);
                var lineHeight = placement.CellTextStyle.FontSize * placement.CellTextStyle.LineHeightMultiplier;
                contentHeight = wrappedLines.Count * lineHeight;
            }

            var effectiveHeight = Math.Max(20f, contentHeight + placement.PaddingTop + placement.PaddingBottom + placement.BorderTopWidth + placement.BorderBottomWidth);
            displayList.FillRect(new RenderRect(cellX, cellY, cellWidth, effectiveHeight), placement.BackgroundColor);

            if (!borderCollapse)
            {
                displayList.FillRect(new RenderRect(cellX, cellY, cellWidth, placement.BorderTopWidth), RenderColor.Black);
                displayList.FillRect(new RenderRect(cellX + cellWidth - placement.BorderRightWidth, cellY, placement.BorderRightWidth, effectiveHeight), RenderColor.Black);
                displayList.FillRect(new RenderRect(cellX, cellY + effectiveHeight - placement.BorderBottomWidth, cellWidth, placement.BorderBottomWidth), RenderColor.Black);
                displayList.FillRect(new RenderRect(cellX, cellY, placement.BorderLeftWidth, effectiveHeight), RenderColor.Black);
            }

            if (contentWidth > 0f && placement.Text.Length > 0)
            {
                var wrappedLines = WrapText(placement.Text, contentWidth, placement.CellTextStyle.FontSize, options.AverageCharacterWidthFactor, placement.CellTextStyle.LetterSpacing);
                var lineHeight = placement.CellTextStyle.FontSize * placement.CellTextStyle.LineHeightMultiplier;
                var lineX = cellX + placement.PaddingLeft + placement.BorderLeftWidth;
                var lineY = cellY + placement.PaddingTop + placement.BorderTopWidth + lineHeight;

                for (var lineIndex = 0; lineIndex < wrappedLines.Count; lineIndex++)
                {
                    var line = wrappedLines[lineIndex];
                    displayList.DrawText(line, lineX, lineY + (lineIndex * lineHeight), placement.CellTextStyle.Color, placement.CellTextStyle.FontSize, placement.CellTextStyle.FontFamily, placement.CellTextStyle.FontWeight, placement.CellTextStyle.IsItalic, placement.CellTextStyle.Underline, placement.CellTextStyle.StrikeThrough, placement.CellTextStyle.DecorationColor, placement.CellTextStyle.DecorationStyle, placement.CellTextStyle.LetterSpacing);
                }
            }
        }

        if (borderCollapse)
        {
            var collapsedBorderWidth = 1f;

            displayList.FillRect(new RenderRect(tableX, tableY, tableWidth, collapsedBorderWidth), RenderColor.Black);
            displayList.FillRect(new RenderRect(tableX, tableY + tableHeight - collapsedBorderWidth, tableWidth, collapsedBorderWidth), RenderColor.Black);
            displayList.FillRect(new RenderRect(tableX, tableY, collapsedBorderWidth, tableHeight), RenderColor.Black);
            displayList.FillRect(new RenderRect(tableX + tableWidth - collapsedBorderWidth, tableY, collapsedBorderWidth, tableHeight), RenderColor.Black);

            var currentVerticalX = tableX;
            for (var columnIndex = 1; columnIndex < columnCount; columnIndex++)
            {
                currentVerticalX += columnWidths[columnIndex - 1];
                displayList.FillRect(new RenderRect(currentVerticalX, tableY, collapsedBorderWidth, tableHeight), RenderColor.Black);
            }

            var currentHorizontalY = tableY;
            for (var rowIndex = 1; rowIndex < rowCellLists.Count; rowIndex++)
            {
                currentHorizontalY += rowHeights[rowIndex - 1];
                displayList.FillRect(new RenderRect(tableX, currentHorizontalY, tableWidth, collapsedBorderWidth), RenderColor.Black);
            }
        }

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
        HtmlRenderOptions options,
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
                LayoutTextNode(textNode.Ref, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, inheritedTextStyle, options, displayList, maxY);
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
                options: options,
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

        if (box.BackgroundPaint is RenderColorPaint colorPaint && colorPaint.Color.A == 0)
        {
            displayList.FillRect(new RenderRect(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight), RenderColor.Transparent);
        }
        else
        {
            PaintBackground(displayList, box.BackgroundPaint, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        }

        PaintBorder(displayList, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth);
        PaintOutline(displayList, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + options.ParagraphSpacing;
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
        HtmlRenderOptions options,
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
        LayoutWrappedText(text, containingX + localFloatLeftOffset, containingWidth - localFloatLeftOffset, ref cursorY, textStyle, options, displayList, maxY, textIndentConsumed ? 0f : textStyle.TextIndent);
        textIndentConsumed = true;
    }

    private static void LayoutWrappedText(
        string text,
        float x,
        float maxWidth,
        ref float cursorY,
        RenderTextStyle textStyle,
        HtmlRenderOptions options,
        DisplayList displayList,
        float maxY,
        float firstLineIndent)
    {
        var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;
        var lines = WrapText(text, maxWidth, textStyle.FontSize, options.AverageCharacterWidthFactor, textStyle.LetterSpacing);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            cursorY += lineHeight;

            if (cursorY > maxY)
            {
                return;
            }

            var lineWidth = EstimateTextWidth(line, textStyle.FontSize, options.AverageCharacterWidthFactor, textStyle.LetterSpacing);
            var lineMaxWidth = index == 0 ? Math.Max(0f, maxWidth - firstLineIndent) : maxWidth;
            var lineX = x + (index == 0 ? firstLineIndent : 0f) + ResolveTextAlignmentOffset(textStyle.TextAlign, lineMaxWidth, lineWidth);
            var baselineY = cursorY + textStyle.VerticalAlignOffset;

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
        float averageCharacterWidthFactor,
        ref float inlineCursorX,
        ref float inlineLineTop,
        ref float inlineLineHeight,
        ref bool textIndentConsumed)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightEdge = flowX + flowWidth;
        var spaceWidth = EstimateTextWidth(" ", textStyle.FontSize, averageCharacterWidthFactor, textStyle.LetterSpacing);

        foreach (var word in words)
        {
            var wordWidth = EstimateTextWidth(word, textStyle.FontSize, averageCharacterWidthFactor, textStyle.LetterSpacing);

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

            displayList.DrawText(
                word,
                inlineCursorX,
                inlineLineTop + textStyle.VerticalAlignOffset,
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

        return new RenderTextStyle(fontSize, color, fontFamily, lineHeight, fontWeight, isItalic, underline, strikeThrough, decorationColor, decorationStyle, textAlign, letterSpacing, textIndent, verticalAlignOffset);
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
                 value.StartsWith("conic-gradient", StringComparison.OrdinalIgnoreCase)))
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

        return new BoxStyle(margin, padding, borderWidth, backgroundPaint, borderColor);
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

    private static bool TryResolveImage(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect)
    {
        image = null;
        rect = default;

        if (!string.Equals(node.Ref.LocalName, "img", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var source = node.Ref.GetAttribute("src");
        byte[]? bytes = null;
        string? mimeType = null;

        if (node.Ref is ILoadableElement loadableElement && loadableElement.CurrentDownload is { Task: not null } download)
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

        using var skImage = SKImage.FromEncodedData(bytes);
        if (skImage is null)
        {
            return false;
        }

        var width = ParseLength(styleMap, "width", containingWidth, float.NaN, allowAuto: true);
        var height = ParseLength(styleMap, "height", containingWidth, float.NaN, allowAuto: true);

        var naturalWidth = skImage.Width;
        var naturalHeight = skImage.Height;

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

        image = new RenderedImage(bytes, (int)Math.Max(1, Math.Round(width)), (int)Math.Max(1, Math.Round(height)), mimeType ?? "image/unknown");
        rect = new RenderRect(x, y, width, height);
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

    private static void PaintBackground(DisplayList displayList, RenderPaint paint, float x, float y, float width, float height)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        if (paint is RenderColorPaint colorPaint)
        {
            if (colorPaint.Color.A == 0)
            {
                return;
            }

            displayList.FillRect(new RenderRect(x, y, width, height), colorPaint.Color);
            return;
        }

        if (paint is RenderGradientPaint)
        {
            displayList.FillRect(new RenderRect(x, y, width, height), paint);
        }
    }

    private static void PaintBorder(DisplayList displayList, RenderColor color, float x, float y, float width, float height, EdgeSizes border)
    {
        if (color.A == 0 || width <= 0f || height <= 0f)
        {
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

        if (value.StartsWith("linear-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseLinearGradient(value, fallbackColor));
        }

        if (value.StartsWith("radial-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseRadialGradient(value, fallbackColor));
        }

        if (value.StartsWith("conic-gradient", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderGradientPaint(ParseConicGradient(value, fallbackColor));
        }

        return new RenderColorPaint(fallbackColor);
    }

    private static RenderGradient ParseLinearGradient(string rawValue, RenderColor fallbackColor)
    {
        var inner = ExtractGradientInnerExpression(rawValue, "linear-gradient");
        var parts = SplitGradientArguments(inner);
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
        return new RenderGradient(RenderGradientKind.Linear, stops, AngleDegrees: angleDegrees);
    }

    private static RenderGradient ParseRadialGradient(string rawValue, RenderColor fallbackColor)
    {
        var inner = ExtractGradientInnerExpression(rawValue, "radial-gradient");
        var parts = SplitGradientArguments(inner);
        var startIndex = 0;

        if (parts.Length > 0)
        {
            var first = parts[0].Trim();
            if (first.StartsWith("circle", StringComparison.OrdinalIgnoreCase) || first.StartsWith("ellipse", StringComparison.OrdinalIgnoreCase))
            {
                startIndex = 1;
            }
        }

        var stops = ParseGradientStops(parts.Skip(startIndex).ToArray(), fallbackColor);
        return new RenderGradient(RenderGradientKind.Radial, stops);
    }

    private static RenderGradient ParseConicGradient(string rawValue, RenderColor fallbackColor)
    {
        var inner = ExtractGradientInnerExpression(rawValue, "conic-gradient");
        var parts = SplitGradientArguments(inner);
        var startIndex = 0;
        var angleDegrees = 0f;

        if (parts.Length > 0)
        {
            var first = parts[0].Trim();
            if (first.StartsWith("from", StringComparison.OrdinalIgnoreCase))
            {
                angleDegrees = ParseAngle(first[4..].Trim());
                startIndex = 1;
            }
        }

        var stops = ParseGradientStops(parts.Skip(startIndex).ToArray(), fallbackColor);
        return new RenderGradient(RenderGradientKind.Conic, stops, AngleDegrees: angleDegrees);
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

    private static string[] SplitGradientArguments(string value)
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

    private static IReadOnlyList<RenderGradientStop> ParseGradientStops(string[] parts, RenderColor fallbackColor)
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
            var position = string.IsNullOrWhiteSpace(positionToken)
                ? (parts.Length == 1 ? 0f : (index / (float)Math.Max(1, parts.Length - 1)))
                : ParseStopPosition(positionToken);

            stops.Add(new RenderGradientStop(position, color));
        }

        return stops;
    }

    private static float ParseStopPosition(string rawPosition)
    {
        var value = rawPosition.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0f;
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

        if (value.EndsWith("deg", StringComparison.Ordinal) &&
            float.TryParse(value[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
        {
            angleDegrees = degrees;
            return true;
        }

        if (value.EndsWith("rad", StringComparison.Ordinal) &&
            float.TryParse(value[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var radians))
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

        return fallback;
    }

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

    private static IReadOnlyList<string> WrapText(string text, float maxWidth, float fontSize, float averageCharacterWidthFactor, float letterSpacing)
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
            var wordWidth = EstimateTextWidth(word, fontSize, averageCharacterWidthFactor, letterSpacing);
            var separatorWidth = current.Length == 0 ? 0f : EstimateTextWidth(" ", fontSize, averageCharacterWidthFactor, letterSpacing);

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

    private static float EstimateTextWidth(string text, float fontSize, float averageCharacterWidthFactor, float letterSpacing)
    {
        var width = 0f;
        var characterCount = 0;

        foreach (var c in text)
        {
            characterCount++;
            width += c switch
            {
                'i' or 'l' or '!' or '|' => fontSize * 0.35f,
                'm' or 'w' or 'M' or 'W' => fontSize * 0.9f,
                ' ' => fontSize * 0.33f,
                _ => fontSize * averageCharacterWidthFactor,
            };
        }

        if (characterCount > 1)
        {
            width += (characterCount - 1) * letterSpacing;
        }

        return width;
    }

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
        float VerticalAlignOffset);

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
        RenderColor BorderColor);
}
