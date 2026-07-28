using System.Globalization;
using System.Text;

using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

namespace AngleSharp.Renderer;

/// <summary>
/// Renders HTML documents into image output.
/// </summary>
public sealed class HtmlRenderer
{
    private static readonly HashSet<string> BlockElements =
    [
        "article", "aside", "blockquote", "div", "dl", "dt", "dd", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav",
        "ol", "p", "pre", "section", "table", "ul",
    ];

    private static readonly Dictionary<string, RenderColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["transparent"] = RenderColor.Transparent,
        ["black"] = new RenderColor(0, 0, 0),
        ["white"] = new RenderColor(255, 255, 255),
        ["red"] = new RenderColor(255, 0, 0),
        ["green"] = new RenderColor(0, 128, 0),
        ["blue"] = new RenderColor(0, 0, 255),
        ["yellow"] = new RenderColor(255, 255, 0),
        ["gray"] = new RenderColor(128, 128, 128),
        ["grey"] = new RenderColor(128, 128, 128),
        ["silver"] = new RenderColor(192, 192, 192),
        ["maroon"] = new RenderColor(128, 0, 0),
        ["purple"] = new RenderColor(128, 0, 128),
        ["fuchsia"] = new RenderColor(255, 0, 255),
        ["lime"] = new RenderColor(0, 255, 0),
        ["olive"] = new RenderColor(128, 128, 0),
        ["navy"] = new RenderColor(0, 0, 128),
        ["teal"] = new RenderColor(0, 128, 128),
        ["aqua"] = new RenderColor(0, 255, 255),
    };

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

        var root = document.Body ?? document.DocumentElement;
        if (root is null)
        {
            return displayList;
        }

        var contentX = options.Padding;
        var contentY = options.Padding;
        var contentWidth = viewport.Width - (2f * options.Padding);

        if (contentWidth <= 0f)
        {
            return displayList;
        }

        var textStyle = new RenderTextStyle(options.FontSize, options.TextColor, options.FontFamily, options.LineHeightMultiplier);
        var cursorY = contentY;

        foreach (var child in root.ChildNodes)
        {
            LayoutNode(
                node: child,
                containingX: contentX,
                containingY: contentY,
                containingWidth: contentWidth,
                cursorY: ref cursorY,
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
        INode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        RenderTextStyle textStyle,
        HtmlRenderOptions options,
        DisplayList displayList,
        float maxY)
    {
        switch (node)
        {
            case IText textNode:
                LayoutTextNode(textNode, containingX, containingWidth, ref cursorY, textStyle, options, displayList, maxY);
                return;
            case IElement element:
                LayoutElement(element, containingX, containingY, containingWidth, ref cursorY, textStyle, options, displayList, maxY);
                return;
            default:
                return;
        }
    }

    private static void LayoutElement(
        IElement element,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        RenderTextStyle inheritedTextStyle,
        HtmlRenderOptions options,
        DisplayList displayList,
        float maxY)
    {
        if (IsHidden(element))
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

        var styleMap = ParseStyleMap(element.GetAttribute("style"));

        if (styleMap.TryGetValue("display", out var explicitDisplay) &&
            string.Equals(explicitDisplay.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var renderAsBlock = ShouldRenderAsBlock(tagName, styleMap);
        var currentTextStyle = ResolveTextStyle(tagName, styleMap, inheritedTextStyle);

        if (!renderAsBlock)
        {
            var inlineText = NormalizeWhitespace(element.TextContent ?? string.Empty);
            if (inlineText.Length > 0)
            {
                LayoutWrappedText(inlineText, containingX, containingWidth, ref cursorY, currentTextStyle, options, displayList, maxY);
            }

            return;
        }

        var box = ResolveBoxStyle(styleMap);
        var marginTop = box.Margin.Top;
        var marginRight = box.Margin.Right;
        var marginBottom = box.Margin.Bottom;
        var marginLeft = box.Margin.Left;

        var borderTop = box.BorderWidth.Top;
        var borderRight = box.BorderWidth.Right;
        var borderBottom = box.BorderWidth.Bottom;
        var borderLeft = box.BorderWidth.Left;

        var paddingTop = box.Padding.Top;
        var paddingRight = box.Padding.Right;
        var paddingBottom = box.Padding.Bottom;
        var paddingLeft = box.Padding.Left;

        var specifiedContentWidth = ParseLength(styleMap, "width", containingWidth, float.NaN);
        var availableWidth = containingWidth - marginLeft - marginRight - borderLeft - borderRight - paddingLeft - paddingRight;
        var contentWidth = float.IsNaN(specifiedContentWidth) ? availableWidth : specifiedContentWidth;
        contentWidth = Math.Max(0f, contentWidth);

        var borderBoxX = containingX + marginLeft;
        var borderBoxY = cursorY + marginTop;
        var contentX = borderBoxX + borderLeft + paddingLeft;
        var contentY = borderBoxY + borderTop + paddingTop;

        var childCursorY = contentY;

        foreach (var child in element.ChildNodes)
        {
            LayoutNode(
                node: child,
                containingX: contentX,
                containingY: contentY,
                containingWidth: contentWidth,
                cursorY: ref childCursorY,
                textStyle: currentTextStyle,
                options: options,
                displayList: displayList,
                maxY: maxY);

            if (childCursorY > maxY)
            {
                break;
            }
        }

        var autoContentHeight = Math.Max(0f, childCursorY - contentY);
        var specifiedContentHeight = ParseLength(styleMap, "height", containingWidth, float.NaN);
        var contentHeight = float.IsNaN(specifiedContentHeight) ? autoContentHeight : Math.Max(specifiedContentHeight, autoContentHeight);

        var borderBoxWidth = borderLeft + paddingLeft + contentWidth + paddingRight + borderRight;
        var borderBoxHeight = borderTop + paddingTop + contentHeight + paddingBottom + borderBottom;

        PaintBackground(displayList, box.BackgroundColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        PaintBorder(displayList, box.BorderColor, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderWidth);

        cursorY += marginTop + borderBoxHeight + marginBottom + options.ParagraphSpacing;
    }

    private static void LayoutTextNode(
        IText textNode,
        float containingX,
        float containingWidth,
        ref float cursorY,
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

        LayoutWrappedText(text, containingX, containingWidth, ref cursorY, textStyle, options, displayList, maxY);
    }

    private static void LayoutWrappedText(
        string text,
        float x,
        float maxWidth,
        ref float cursorY,
        RenderTextStyle textStyle,
        HtmlRenderOptions options,
        DisplayList displayList,
        float maxY)
    {
        var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;
        var lines = WrapText(text, maxWidth, textStyle.FontSize, options.AverageCharacterWidthFactor);

        foreach (var line in lines)
        {
            cursorY += lineHeight;

            if (cursorY > maxY)
            {
                return;
            }

            displayList.DrawText(line, x, cursorY, textStyle.Color, textStyle.FontSize, textStyle.FontFamily);
        }
    }

    private static bool ShouldRenderAsBlock(string tagName, Dictionary<string, string> styleMap)
    {
        if (styleMap.TryGetValue("display", out var display))
        {
            var normalized = display.Trim().ToLowerInvariant();
            return normalized switch
            {
                "inline" => false,
                _ => true,
            };
        }

        return IsBlockElement(tagName);
    }

    private static bool IsBlockElement(string tagName) => BlockElements.Contains(tagName);

    private static bool IsHidden(IElement element)
    {
        var style = element.GetAttribute("style");
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        var normalized = style.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized.Contains("display:none", StringComparison.Ordinal) ||
               normalized.Contains("visibility:hidden", StringComparison.Ordinal);
    }

    private static RenderTextStyle ResolveTextStyle(string tagName, Dictionary<string, string> styleMap, RenderTextStyle inherited)
    {
        var scale = GetScaleMultiplier(tagName);
        var fontSize = ParseLength(styleMap, "font-size", inherited.FontSize, inherited.FontSize * scale);
        var fontFamily = styleMap.TryGetValue("font-family", out var family) && !string.IsNullOrWhiteSpace(family)
            ? family.Trim('\'', '"', ' ')
            : inherited.FontFamily;

        var lineHeight = ParseLineHeight(styleMap, inherited.LineHeightMultiplier);
        var color = ParseColor(styleMap.TryGetValue("color", out var colorValue) ? colorValue : null, inherited.Color);

        return new RenderTextStyle(fontSize, color, fontFamily, lineHeight);
    }

    private static float GetScaleMultiplier(string tagName)
    {
        return tagName.ToLowerInvariant() switch
        {
            "h1" => 2.0f,
            "h2" => 1.7f,
            "h3" => 1.5f,
            "h4" => 1.25f,
            "h5" => 1.1f,
            "h6" => 1.0f,
            "small" => 0.85f,
            _ => 1f,
        };
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

    private static Dictionary<string, string> ParseStyleMap(string? style)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(style))
        {
            return map;
        }

        var declarations = style.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var declaration in declarations)
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var property = declaration[..separator].Trim().ToLowerInvariant();
            var value = declaration[(separator + 1)..].Trim();

            if (property.Length == 0 || value.Length == 0)
            {
                continue;
            }

            map[property] = value;
        }

        return map;
    }

    private static BoxStyle ResolveBoxStyle(Dictionary<string, string> styleMap)
    {
        var margin = ResolveEdgeSizes(styleMap, "margin", defaultValue: 0f);
        var padding = ResolveEdgeSizes(styleMap, "padding", defaultValue: 0f);
        var borderWidth = ResolveEdgeSizes(styleMap, "border-width", defaultValue: 0f);

        if (styleMap.TryGetValue("border", out var borderShorthand))
        {
            borderWidth = ResolveBorderWidthFromShorthand(borderShorthand, borderWidth);
        }

        borderWidth = borderWidth with
        {
            Top = ParseLength(styleMap, "border-top-width", 0f, borderWidth.Top),
            Right = ParseLength(styleMap, "border-right-width", 0f, borderWidth.Right),
            Bottom = ParseLength(styleMap, "border-bottom-width", 0f, borderWidth.Bottom),
            Left = ParseLength(styleMap, "border-left-width", 0f, borderWidth.Left),
        };

        var backgroundColor = ParseColor(styleMap.TryGetValue("background-color", out var background) ? background : null, RenderColor.Transparent);
        var borderColor = ParseColor(styleMap.TryGetValue("border-color", out var borderColorValue) ? borderColorValue : null, RenderColor.Black);

        if (styleMap.TryGetValue("border", out var borderValue))
        {
            var colorFromBorder = ParseBorderColorFromShorthand(borderValue);
            if (colorFromBorder is not null)
            {
                borderColor = colorFromBorder.Value;
            }
        }

        return new BoxStyle(margin, padding, borderWidth, backgroundColor, borderColor);
    }

    private static EdgeSizes ResolveEdgeSizes(Dictionary<string, string> styleMap, string propertyName, float defaultValue)
    {
        var edges = new EdgeSizes(defaultValue, defaultValue, defaultValue, defaultValue);

        if (!styleMap.TryGetValue(propertyName, out var value))
        {
            return edges;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return edges;
        }

        var values = new float[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = ParseLengthValue(parts[i], defaultValue);
        }

        return parts.Length switch
        {
            1 => new EdgeSizes(values[0], values[0], values[0], values[0]),
            2 => new EdgeSizes(values[0], values[1], values[0], values[1]),
            3 => new EdgeSizes(values[0], values[1], values[2], values[1]),
            _ => new EdgeSizes(values[0], values[1], values[2], values[3]),
        };
    }

    private static EdgeSizes ResolveBorderWidthFromShorthand(string value, EdgeSizes existing)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (TryParsePixelValue(token, out var parsed))
            {
                return new EdgeSizes(parsed, parsed, parsed, parsed);
            }

            if (string.Equals(token, "thin", StringComparison.OrdinalIgnoreCase))
            {
                return new EdgeSizes(1f, 1f, 1f, 1f);
            }

            if (string.Equals(token, "medium", StringComparison.OrdinalIgnoreCase))
            {
                return new EdgeSizes(3f, 3f, 3f, 3f);
            }

            if (string.Equals(token, "thick", StringComparison.OrdinalIgnoreCase))
            {
                return new EdgeSizes(5f, 5f, 5f, 5f);
            }
        }

        return existing;
    }

    private static RenderColor? ParseBorderColorFromShorthand(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            var color = ParseColor(token, RenderColor.Transparent);
            if (color.A > 0)
            {
                return color;
            }
        }

        return null;
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

    private static float ParseLength(Dictionary<string, string> styleMap, string propertyName, float relativeTo, float defaultValue)
    {
        if (!styleMap.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var parsed = value.Trim().ToLowerInvariant();

        if (parsed == "auto")
        {
            return float.NaN;
        }

        if (parsed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(parsed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            return (pct / 100f) * relativeTo;
        }

        return ParseLengthValue(parsed, defaultValue);
    }

    private static float ParseLengthValue(string value, float defaultValue)
    {
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

        if (color.StartsWith("rgb(", StringComparison.Ordinal) && color.EndsWith(')'))
        {
            var content = color[4..^1];
            var parts = content.Split(',', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 3 &&
                byte.TryParse(parts[0].Trim(), out var r) &&
                byte.TryParse(parts[1].Trim(), out var g) &&
                byte.TryParse(parts[2].Trim(), out var b))
            {
                return new RenderColor(r, g, b);
            }
        }

        if (NamedColors.TryGetValue(color, out var namedColor))
        {
            return namedColor;
        }

        return fallback;
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

    private static IReadOnlyList<string> WrapText(string text, float maxWidth, float fontSize, float averageCharacterWidthFactor)
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
            var wordWidth = EstimateTextWidth(word, fontSize, averageCharacterWidthFactor);
            var separatorWidth = current.Length == 0 ? 0f : EstimateTextWidth(" ", fontSize, averageCharacterWidthFactor);

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

    private static float EstimateTextWidth(string text, float fontSize, float averageCharacterWidthFactor)
    {
        var width = 0f;

        foreach (var c in text)
        {
            width += c switch
            {
                'i' or 'l' or '!' or '|' => fontSize * 0.35f,
                'm' or 'w' or 'M' or 'W' => fontSize * 0.9f,
                ' ' => fontSize * 0.33f,
                _ => fontSize * averageCharacterWidthFactor,
            };
        }

        return width;
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

    private readonly record struct RenderTextStyle(float FontSize, RenderColor Color, string FontFamily, float LineHeightMultiplier);

    private readonly record struct EdgeSizes(float Top, float Right, float Bottom, float Left);

    private readonly record struct BoxStyle(
        EdgeSizes Margin,
        EdgeSizes Padding,
        EdgeSizes BorderWidth,
        RenderColor BackgroundColor,
        RenderColor BorderColor);
}
