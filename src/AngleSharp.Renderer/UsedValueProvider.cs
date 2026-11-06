namespace AngleSharp.Renderer;

using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using System;
using System.Globalization;

/// <summary>
/// Tells what layout found for what script reads off a computed style (CSSOM 9): the sizes of a
/// box in pixels, and what an element inherits instead of having nothing.
/// </summary>
internal sealed class UsedValueProvider : ICssUsedValueProvider
{
    public static readonly UsedValueProvider Instance = new();

    public string? GetUsedValue(IElement element, string propertyName)
    {
        switch (propertyName)
        {
            case "width":
            case "height":
            case "padding-top":
            case "padding-right":
            case "padding-bottom":
            case "padding-left":
            case "border-top-width":
            case "border-right-width":
            case "border-bottom-width":
            case "border-left-width":
                return GetBoxValue(element, propertyName);
            case "margin-left":
            case "margin-right":
            case "margin-top":
            case "margin-bottom":
                return GetMarginValue(element, propertyName);
            case "margin":
                return GetMarginShorthand(element);
            case "top":
            case "right":
            case "bottom":
            case "left":
                return GetInsetValue(element, propertyName);
            case "inset":
                return GetInsetShorthand(element);
            case "font-size":
            case "font-family":
            case "font-weight":
            case "font-style":
            case "line-height":
            case "color":
            case "text-align":
            case "white-space":
            case "visibility":
                return GetStyleValue(element, propertyName);
            default:
                return null;
        }
    }

    private static string? GetBoxValue(IElement element, string propertyName)
    {
        if (ElementCssomViewExtensions.GetViewportMetrics(element) is not { } metrics)
        {
            return null;
        }

        var style = GetStyle(element);

        switch (propertyName)
        {
            case "width" or "height":
                // an inline box has no size of its own that could be set
                if (style is not null && (!style.TryGetValue("display", out var display) || string.IsNullOrEmpty(display) || display == "inline") && !IsReplaced(element))
                {
                    return null;
                }

                var isBorderBox = style is not null && style.TryGetValue("box-sizing", out var sizing) && sizing == "border-box";
                var size = propertyName == "width"
                    ? isBorderBox ? metrics.BorderBoxWidth : metrics.BorderBoxWidth - metrics.BorderLeft - metrics.BorderRight - metrics.PaddingLeft - metrics.PaddingRight
                    : isBorderBox ? metrics.BorderBoxHeight : metrics.BorderBoxHeight - metrics.BorderTop - metrics.BorderBottom - metrics.PaddingTop - metrics.PaddingBottom;
                return ToPixels(Math.Max(0f, size));
            case "padding-top":
                return ToPixels(metrics.PaddingTop);
            case "padding-right":
                return ToPixels(metrics.PaddingRight);
            case "padding-bottom":
                return ToPixels(metrics.PaddingBottom);
            case "padding-left":
                return ToPixels(metrics.PaddingLeft);
            case "border-top-width":
                return ToPixels(metrics.BorderTop);
            case "border-right-width":
                return ToPixels(metrics.BorderRight);
            case "border-bottom-width":
                return ToPixels(metrics.BorderBottom);
            default:
                return ToPixels(metrics.BorderLeft);
        }
    }

    private static string? GetMarginValue(IElement element, string propertyName)
    {
        if (ElementCssomViewExtensions.GetPageMetrics(element) is not { } metrics)
        {
            return null;
        }

        if (propertyName is "margin-top" or "margin-bottom" && IsInline(element))
        {
            return null;
        }

        return propertyName switch
        {
            "margin-left" => ToPixels(metrics.MarginLeft),
            "margin-right" => ToPixels(metrics.MarginRight),
            "margin-top" => ToPixels(metrics.MarginTop),
            _ => ToPixels(metrics.MarginBottom),
        };
    }

    private static string? GetMarginShorthand(IElement element)
    {
        if (IsInline(element))
        {
            if (ElementCssomViewExtensions.GetPageMetrics(element) is not { } inlineMetrics ||
                element.Owner?.DefaultView?.GetComputedStyle(element) is not { } computed)
            {
                return null;
            }

            return CompressShorthand(
            [
                GetComputedMargin(computed, "margin-top"),
                ToPixels(inlineMetrics.MarginRight),
                GetComputedMargin(computed, "margin-bottom"),
                ToPixels(inlineMetrics.MarginLeft),
            ]);
        }

        if (ElementCssomViewExtensions.GetPageMetrics(element) is not { } metrics)
        {
            return null;
        }

        return ToShorthand(metrics.MarginTop, metrics.MarginRight, metrics.MarginBottom, metrics.MarginLeft);
    }

    private static string GetComputedMargin(AngleSharp.Css.Dom.ICssStyleDeclaration style, string propertyName)
    {
        var value = style.GetPropertyValue(propertyName);
        return string.IsNullOrEmpty(value) ? "0px" : value;
    }

    private static bool IsInline(IElement element)
    {
        var style = GetStyle(element);
        var display = style?.GetValueOrDefault("display");
        display = string.IsNullOrEmpty(display)
            ? element.Owner?.DefaultView?.GetComputedStyle(element).GetPropertyValue("display")
            : display;
        return string.IsNullOrWhiteSpace(display) || string.Equals(display.Trim(), "inline", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetInsetValue(IElement element, string propertyName)
    {
        if (GetStyle(element) is not { } style ||
            !style.TryGetValue("position", out var position))
        {
            return null;
        }

        position = position.Trim();

        if (string.Equals(position, "static", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(position, "sticky", StringComparison.OrdinalIgnoreCase) ||
            ElementCssomViewExtensions.GetPageMetrics(element) is not { } metrics)
        {
            return null;
        }

        if (string.Equals(position, "relative", StringComparison.OrdinalIgnoreCase))
        {
            var relativeValue = propertyName switch
            {
                "left" => metrics.InsetLeft,
                "right" => metrics.InsetRight,
                "top" => metrics.InsetTop,
                _ => metrics.InsetBottom,
            };
            return float.IsNaN(relativeValue) ? null : ToPixels(relativeValue);
        }

        if (!string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (GetContainingBlock(element, string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase)) is not { } containingBlock)
        {
            return null;
        }

        var containing = containingBlock.Metrics;
        var left = containing.BorderBoxX + containing.BorderLeft;
        var top = containing.BorderBoxY + containing.BorderTop;
        var width = Math.Max(0f, containing.BorderBoxWidth - containing.BorderLeft - containing.BorderRight);
        var height = Math.Max(0f, containing.BorderBoxHeight - containing.BorderTop - containing.BorderBottom);
        var scrollX = ReferenceEquals(containingBlock.Element, element.Owner?.DocumentElement) ? 0f : (float)containingBlock.Element.GetScrollLeft();
        var scrollY = ReferenceEquals(containingBlock.Element, element.Owner?.DocumentElement) ? 0f : (float)containingBlock.Element.GetScrollTop();
        var elementX = metrics.BorderBoxX + scrollX;
        var elementY = metrics.BorderBoxY + scrollY;
        var value = propertyName switch
        {
            "left" => elementX - left - metrics.MarginLeft,
            "right" => left + width - elementX - metrics.BorderBoxWidth - metrics.MarginRight,
            "top" => elementY - top - metrics.MarginTop,
            _ => top + height - elementY - metrics.BorderBoxHeight - metrics.MarginBottom,
        };
        return ToPixels(value);
    }

    private static string? GetInsetShorthand(IElement element)
    {
        if (GetStyle(element) is not { } style ||
            !style.TryGetValue("position", out var position) ||
            string.Equals(position.Trim(), "static", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(position.Trim(), "sticky", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var values = new string[4];

        for (var index = 0; index < values.Length; index++)
        {
            var property = index switch { 0 => "top", 1 => "right", 2 => "bottom", _ => "left" };
            if (GetInsetValue(element, property) is not { } value)
            {
                return null;
            }

            values[index] = value;
        }

        return CompressShorthand(values);
    }

    private static (IElement Element, HtmlRenderer.ElementLayoutMetrics Metrics)? GetContainingBlock(IElement element, bool isFixed)
    {
        var root = element.Owner?.DocumentElement;

        if (root is null)
        {
            return null;
        }

        if (isFixed)
        {
            return ElementCssomViewExtensions.GetPageMetrics(root) is { } viewportMetrics ? (root, viewportMetrics) : null;
        }

        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (GetStyle(ancestor) is { } style &&
                style.TryGetValue("position", out var position) &&
                !string.Equals(position.Trim(), "static", StringComparison.OrdinalIgnoreCase) &&
                ElementCssomViewExtensions.GetPageMetrics(ancestor) is { } metrics)
            {
                return (ancestor, metrics);
            }
        }

        return ElementCssomViewExtensions.GetPageMetrics(root) is { } rootMetrics ? (root, rootMetrics) : null;
    }

    private static string ToShorthand(float top, float right, float bottom, float left) =>
        CompressShorthand([ToPixels(top), ToPixels(right), ToPixels(bottom), ToPixels(left)]);

    private static string CompressShorthand(string[] values)
    {
        if (values[0] == values[2])
        {
            return values[1] == values[3]
                ? values[0] == values[1] ? values[0] : $"{values[0]} {values[1]}"
                : $"{values[0]} {values[1]} {values[3]}";
        }

        return $"{values[0]} {values[1]} {values[2]} {values[3]}";
    }

    private static string? GetStyleValue(IElement element, string propertyName) =>
        GetStyle(element) is { } style && style.TryGetValue(propertyName, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static System.Collections.Generic.Dictionary<string, string>? GetStyle(IElement element)
    {
        if (element.Owner is { } owner && owner.Context.GetDomHarness() is InteractiveHtmlRendererState state &&
            state.GetLayoutSnapshot(owner).StyleMaps.TryGetValue(element, out var style))
        {
            return style;
        }

        return null;
    }

    private static bool IsReplaced(IElement element) => element.LocalName is "img" or "video" or "canvas" or "iframe" or "embed" or "object" or "svg" or "input" or "select" or "textarea" or "button";

    private static string ToPixels(float value) => value.ToString("0.###", CultureInfo.InvariantCulture) + "px";
}
