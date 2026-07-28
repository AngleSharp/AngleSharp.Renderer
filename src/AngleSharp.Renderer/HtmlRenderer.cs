using System.Globalization;
using System.Linq;
using System.Text;

using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

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
        float maxY)
    {
        switch (node)
        {
            case TextRenderNode textNode:
                LayoutTextNode(textNode.Ref, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, textStyle, options, displayList, maxY);
                return;
            case ElementRenderNode element:
                LayoutElement(element, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, ref activeFloatLeftOffset, ref activeFloatBottom, ref textIndentConsumed, textStyle, options, displayList, maxY);
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
        float maxY)
    {
        var element = node.Ref;
        var computedStyle = node.ComputedStyle;
        var styleMap = CreateStyleMap(node.ComputedStyle);

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

        var renderAsBlock = ShouldRenderAsBlock(computedStyle);
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

        var specifiedContentWidth = ParseLength(styleMap, "width", flowContainingWidth, float.NaN, allowAuto: true);
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
        var specifiedContentHeight = ParseLength(styleMap, "height", flowContainingWidth, float.NaN, allowAuto: true);
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

        PaintBackground(displayList, box.BackgroundColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        PaintBorder(displayList, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth);
        PaintOutline(displayList, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

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

    private static Dictionary<string, string> CreateStyleMap(ICssStyleDeclaration style)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddIfPresent(map, "display", style.GetDisplay());
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

            var childStyle = CreateStyleMap(childElement.ComputedStyle);
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
                var childStyleMap = CreateStyleMap(elementChild.ComputedStyle);

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
        var borderColor = ParseColor(
            styleMap.TryGetValue("border-top-color", out var topColor) ? topColor :
            styleMap.TryGetValue("border-right-color", out var rightColor) ? rightColor :
            styleMap.TryGetValue("border-bottom-color", out var bottomColor) ? bottomColor :
            styleMap.TryGetValue("border-left-color", out var leftColor) ? leftColor :
            null,
            RenderColor.Black);

        return new BoxStyle(margin, padding, borderWidth, backgroundColor, borderColor);
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

    private static void PaintBackground(DisplayList displayList, RenderColor color, float x, float y, float width, float height)
    {
        if (color.A == 0 || width <= 0f || height <= 0f)
        {
            return;
        }

        displayList.FillRect(new RenderRect(x, y, width, height), color);
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
        RenderColor BackgroundColor,
        RenderColor BorderColor);
}
