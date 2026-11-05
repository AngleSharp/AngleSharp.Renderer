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
