namespace AngleSharp.Dom;

using AngleSharp;
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
    public static IDomRect GetBoundingClientRect(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element);
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
    public static IDomRectList GetClientRects(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element);
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
    public static int GetClientWidth(this IElement element)
    {
        return GetRoundedDimension(element, static metrics => metrics.BorderBoxWidth - metrics.BorderLeft - metrics.BorderRight);
    }

    /// <summary>
    /// Returns the left border width.
    /// </summary>
    [DomName("clientLeft")]
    public static int GetClientLeft(this IElement element)
    {
        return GetRoundedDimension(element, static metrics => metrics.BorderLeft);
    }

    /// <summary>
    /// Returns the inner height including padding, excluding borders.
    /// </summary>
    [DomName("clientHeight")]
    public static int GetClientHeight(this IElement element)
    {
        return GetRoundedDimension(element, static metrics => metrics.BorderBoxHeight - metrics.BorderTop - metrics.BorderBottom);
    }

    /// <summary>
    /// Returns the top border width.
    /// </summary>
    [DomName("clientTop")]
    public static int GetClientTop(this IElement element)
    {
        return GetRoundedDimension(element, static metrics => metrics.BorderTop);
    }

    /// <summary>
    /// Returns the width of the element's scrolling area.
    /// </summary>
    [DomName("scrollWidth")]
    public static int GetScrollWidth(this IElement element)
    {
        return GetScrollExtents(element).Width;
    }

    /// <summary>
    /// Returns the height of the element's scrolling area.
    /// </summary>
    [DomName("scrollHeight")]
    public static int GetScrollHeight(this IElement element)
    {
        return GetScrollExtents(element).Height;
    }

    /// <summary>
    /// Returns the current horizontal scroll position.
    /// </summary>
    [DomName("scrollLeft")]
    public static double GetScrollLeft(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var maxLeft = GetMaxScrollLeft(element);
        var state = GetInteractiveState(element);
        return state is null ? 0d : state.GetScrollLeft(element, maxLeft);
    }

    /// <summary>
    /// Sets the horizontal scroll position.
    /// </summary>
    [DomName("scrollLeft")]
    public static void SetScrollLeft(this IElement element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = GetInteractiveState(element);
        if (state is null)
        {
            return;
        }

        state.SetScrollLeft(element, value, GetMaxScrollLeft(element));
    }

    /// <summary>
    /// Returns the current vertical scroll position.
    /// </summary>
    [DomName("scrollTop")]
    public static double GetScrollTop(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var maxTop = GetMaxScrollTop(element);
        var state = GetInteractiveState(element);
        return state is null ? 0d : state.GetScrollTop(element, maxTop);
    }

    /// <summary>
    /// Sets the vertical scroll position.
    /// </summary>
    [DomName("scrollTop")]
    public static void SetScrollTop(this IElement element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);

        var state = GetInteractiveState(element);
        if (state is null)
        {
            return;
        }

        state.SetScrollTop(element, value, GetMaxScrollTop(element));
    }

    /// <summary>
    /// Sets scroll positions to absolute coordinates.
    /// </summary>
    [DomName("scrollTo")]
    public static void ScrollTo(this IElement element, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(element);

        SetScrollLeft(element, x);
        SetScrollTop(element, y);
    }

    /// <summary>
    /// Adjusts scroll positions by the supplied deltas.
    /// </summary>
    [DomName("scrollBy")]
    public static void ScrollBy(this IElement element, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(element);

        ScrollTo(element, GetScrollLeft(element) + x, GetScrollTop(element) + y);
    }

    /// <summary>
    /// Returns the border-box width.
    /// </summary>
    [DomName("offsetWidth")]
    public static int GetOffsetWidth(this IElement element)
    {
        return GetRoundedDimension(element, static metrics => metrics.BorderBoxWidth);
    }

    /// <summary>
    /// Returns the border-box height.
    /// </summary>
    [DomName("offsetHeight")]
    public static int GetOffsetHeight(this IElement element)
    {
        return GetRoundedDimension(element, static metrics => metrics.BorderBoxHeight);
    }

    /// <summary>
    /// Returns the left offset relative to the offset parent's padding edge.
    /// </summary>
    [DomName("offsetLeft")]
    public static int GetOffsetLeft(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element);
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
    public static int GetOffsetTop(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element);
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

    private static int GetRoundedDimension(IElement element, Func<HtmlRenderer.ElementLayoutMetrics, float> selector)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element);
        if (metricsMap is null || !metricsMap.TryGetValue(element, out var metrics))
        {
            return 0;
        }

        var value = Math.Max(0f, selector(metrics));
        return (int)Math.Round(value);
    }

    private static IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics>? GetMetricsMap(IElement element)
    {
        var owner = element.Owner;
        if (owner is null)
        {
            return null;
        }

        var harness = owner.Context.GetDomHarness();
        return HtmlRenderer.CaptureLayoutMetrics(owner, harness.RenderDevice);
    }

    private static IDomHarness? GetInteractiveState(IElement element)
    {
        var owner = element.Owner;
        return owner?.Context.GetDomHarness();
    }

    private static (int Width, int Height) GetScrollExtents(IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        var metricsMap = GetMetricsMap(element);
        if (metricsMap is null || !metricsMap.TryGetValue(element, out var elementMetrics))
        {
            return (0, 0);
        }

        var clientWidth = Math.Max(0d, elementMetrics.BorderBoxWidth - elementMetrics.BorderLeft - elementMetrics.BorderRight);
        var clientHeight = Math.Max(0d, elementMetrics.BorderBoxHeight - elementMetrics.BorderTop - elementMetrics.BorderBottom);
        var paddingBoxLeft = elementMetrics.BorderBoxX + elementMetrics.BorderLeft;
        var paddingBoxTop = elementMetrics.BorderBoxY + elementMetrics.BorderTop;
        var rightExtent = paddingBoxLeft + clientWidth;
        var bottomExtent = paddingBoxTop + clientHeight;

        foreach (var (candidate, candidateMetrics) in metricsMap)
        {
            if (ReferenceEquals(candidate, element) || !IsDescendantOf(candidate, element))
            {
                continue;
            }

            rightExtent = Math.Max(rightExtent, candidateMetrics.BorderBoxX + candidateMetrics.BorderBoxWidth);
            bottomExtent = Math.Max(bottomExtent, candidateMetrics.BorderBoxY + candidateMetrics.BorderBoxHeight);
        }

        var scrollWidth = (int)Math.Round(Math.Max(clientWidth, rightExtent - paddingBoxLeft));
        var scrollHeight = (int)Math.Round(Math.Max(clientHeight, bottomExtent - paddingBoxTop));

        return (Math.Max(0, scrollWidth), Math.Max(0, scrollHeight));
    }

    private static double GetMaxScrollLeft(IElement element)
    {
        var extents = GetScrollExtents(element);
        var maxLeft = extents.Width - GetClientWidth(element);
        return Math.Max(0d, maxLeft);
    }

    private static double GetMaxScrollTop(IElement element)
    {
        var extents = GetScrollExtents(element);
        var maxTop = extents.Height - GetClientHeight(element);
        return Math.Max(0d, maxTop);
    }

    private static bool IsDescendantOf(IElement candidate, IElement ancestor)
    {
        var current = candidate.ParentElement;

        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current.ParentElement;
        }

        return false;
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
