namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Geometry;
using AngleSharp.Renderer;

/// <summary>
/// Provides CSSOM View-style geometry helpers for elements.
/// </summary>
public static class ElementCssomViewExtensions
{
    /// <summary>
    /// Returns the element's border-box rectangle in viewport coordinates.
    /// </summary>
    [DomName("getBoundingClientRect")]
    public static IDomRect GetBoundingClientRect(this IElement element, HtmlRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element, options);
        if (metricsMap is null || !metricsMap.TryGetValue(element, out var metrics))
        {
            return new DomRect();
        }

        return new DomRect(metrics.BorderBoxX, metrics.BorderBoxY, metrics.BorderBoxWidth, metrics.BorderBoxHeight);
    }

    /// <summary>
    /// Returns the list of border-box fragments for the element.
    /// </summary>
    [DomName("getClientRects")]
    public static IDomRectList GetClientRects(this IElement element, HtmlRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element, options);
        if (metricsMap is null || !metricsMap.TryGetValue(element, out var metrics))
        {
            return new DomRectList();
        }

        if (metrics.BorderBoxWidth <= 0f && metrics.BorderBoxHeight <= 0f)
        {
            return new DomRectList();
        }

        return new DomRectList(new IDomRect[]
        {
            new DomRect(metrics.BorderBoxX, metrics.BorderBoxY, metrics.BorderBoxWidth, metrics.BorderBoxHeight),
        });
    }

    /// <summary>
    /// Returns the inner width including padding, excluding borders.
    /// </summary>
    [DomName("clientWidth")]
    public static int GetClientWidth(this IElement element, HtmlRenderOptions? options = null)
    {
        return GetRoundedDimension(element, options, static metrics => metrics.BorderBoxWidth - metrics.BorderLeft - metrics.BorderRight);
    }

    /// <summary>
    /// Returns the inner height including padding, excluding borders.
    /// </summary>
    [DomName("clientHeight")]
    public static int GetClientHeight(this IElement element, HtmlRenderOptions? options = null)
    {
        return GetRoundedDimension(element, options, static metrics => metrics.BorderBoxHeight - metrics.BorderTop - metrics.BorderBottom);
    }

    /// <summary>
    /// Returns the width of the element's scrolling area.
    /// </summary>
    [DomName("scrollWidth")]
    public static int GetScrollWidth(this IElement element, HtmlRenderOptions? options = null)
    {
        return GetClientWidth(element, options);
    }

    /// <summary>
    /// Returns the height of the element's scrolling area.
    /// </summary>
    [DomName("scrollHeight")]
    public static int GetScrollHeight(this IElement element, HtmlRenderOptions? options = null)
    {
        return GetClientHeight(element, options);
    }

    /// <summary>
    /// Returns the border-box width.
    /// </summary>
    [DomName("offsetWidth")]
    public static int GetOffsetWidth(this IElement element, HtmlRenderOptions? options = null)
    {
        return GetRoundedDimension(element, options, static metrics => metrics.BorderBoxWidth);
    }

    /// <summary>
    /// Returns the border-box height.
    /// </summary>
    [DomName("offsetHeight")]
    public static int GetOffsetHeight(this IElement element, HtmlRenderOptions? options = null)
    {
        return GetRoundedDimension(element, options, static metrics => metrics.BorderBoxHeight);
    }

    /// <summary>
    /// Returns the left offset relative to the offset parent's padding edge.
    /// </summary>
    [DomName("offsetLeft")]
    public static int GetOffsetLeft(this IElement element, HtmlRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element, options);
        if (metricsMap is null || !metricsMap.TryGetValue(element, out var metrics))
        {
            return 0;
        }

        var offsetParent = GetOffsetParent(element);
        if (offsetParent is null || !metricsMap.TryGetValue(offsetParent, out var parentMetrics))
        {
            return (int)Math.Round(metrics.BorderBoxX);
        }

        var relativeLeft = metrics.BorderBoxX - (parentMetrics.BorderBoxX + parentMetrics.BorderLeft);
        return (int)Math.Round(relativeLeft);
    }

    /// <summary>
    /// Returns the top offset relative to the offset parent's padding edge.
    /// </summary>
    [DomName("offsetTop")]
    public static int GetOffsetTop(this IElement element, HtmlRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element, options);
        if (metricsMap is null || !metricsMap.TryGetValue(element, out var metrics))
        {
            return 0;
        }

        var offsetParent = GetOffsetParent(element);
        if (offsetParent is null || !metricsMap.TryGetValue(offsetParent, out var parentMetrics))
        {
            return (int)Math.Round(metrics.BorderBoxY);
        }

        var relativeTop = metrics.BorderBoxY - (parentMetrics.BorderBoxY + parentMetrics.BorderTop);
        return (int)Math.Round(relativeTop);
    }

    /// <summary>
    /// Returns the nearest offset parent.
    /// </summary>
    [DomName("offsetParent")]
    public static IElement? GetOffsetParent(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (IsPositionFixed(element))
        {
            return null;
        }

        var ancestor = element.ParentElement;

        while (ancestor is not null)
        {
            if (!IsPositionStatic(ancestor))
            {
                return ancestor;
            }

            ancestor = ancestor.ParentElement;
        }

        return element.Owner?.Body;
    }

    private static int GetRoundedDimension(IElement element, HtmlRenderOptions? options, Func<HtmlRenderer.ElementLayoutMetrics, float> selector)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element, options);
        if (metricsMap is null || !metricsMap.TryGetValue(element, out var metrics))
        {
            return 0;
        }

        var value = Math.Max(0f, selector(metrics));
        return (int)Math.Round(value);
    }

    private static IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics>? GetMetricsMap(IElement element, HtmlRenderOptions? options)
    {
        var owner = element.Owner;
        return owner is null ? null : HtmlRenderer.CaptureLayoutMetrics(owner, options);
    }

    private static bool IsPositionFixed(IElement element)
    {
        var style = element.ComputeCurrentStyle();
        var position = style?.GetPropertyValue("position");
        return string.Equals(position?.Trim(), "fixed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPositionStatic(IElement element)
    {
        var style = element.ComputeCurrentStyle();
        var position = style?.GetPropertyValue("position");

        if (string.IsNullOrWhiteSpace(position))
        {
            return true;
        }

        return string.Equals(position.Trim(), "static", StringComparison.OrdinalIgnoreCase);
    }
}
