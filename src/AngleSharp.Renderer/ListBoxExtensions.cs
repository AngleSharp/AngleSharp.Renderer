namespace AngleSharp.Dom;

using AngleSharp.Renderer;

/// <summary>
/// What a host needs to make a list box (a select showing several options at once) interactive:
/// its rows are painted by the renderer, so there are no boxes to hit test or to scroll to.
/// </summary>
public static class ListBoxExtensions
{
    /// <summary>
    /// Gets if the select is shown as a list box, i.e., not as a dropdown.
    /// </summary>
    /// <param name="select">The select element.</param>
    /// <returns>True if it shows several options at once.</returns>
    public static bool IsListBox(this IElement select)
    {
        ArgumentNullException.ThrowIfNull(select);
        return HtmlRenderer.IsListBox(select);
    }

    /// <summary>
    /// Gets the option or option group shown at the given position in the viewport.
    /// </summary>
    /// <param name="select">The select element shown as a list box.</param>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>The element of the row, or null if there is no row at that position.</returns>
    public static IElement? GetListBoxRowAt(this IElement select, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(select);

        if (!HtmlRenderer.IsListBox(select) || !TryGetContentBox(select, out var box) ||
            !HtmlRenderer.TryGetListBoxRowHeight(select, out var rowHeight) ||
            x < box.Left || x >= box.Left + box.Width || y < box.Top || y >= box.Top + box.Height)
        {
            return null;
        }

        var rows = HtmlRenderer.GetListBoxRowElements(select);
        var index = (int)Math.Floor((y - box.Top + select.GetScrollTop()) / rowHeight);
        return index >= 0 && index < rows.Count ? rows[index] : null;
    }

    /// <summary>
    /// Scrolls a list box just far enough to show the row of the given option.
    /// </summary>
    /// <param name="select">The select element shown as a list box.</param>
    /// <param name="option">The option to show.</param>
    public static void ScrollListBoxRowIntoView(this IElement select, IElement option)
    {
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(option);

        if (!HtmlRenderer.IsListBox(select) || !TryGetContentBox(select, out var box) ||
            !HtmlRenderer.TryGetListBoxRowHeight(select, out var rowHeight))
        {
            return;
        }

        var index = HtmlRenderer.GetListBoxRowElements(select).IndexOf(option);

        if (index >= 0)
        {
            var scrollTop = select.GetScrollTop();
            var top = index * rowHeight;

            if (top < scrollTop)
            {
                select.SetScrollTop(top);
            }
            else if (top + rowHeight > scrollTop + box.Height)
            {
                select.SetScrollTop(top + rowHeight - box.Height);
            }
        }
    }

    // the rows fill the box inside of the border and padding
    private static bool TryGetContentBox(IElement select, out (double Left, double Top, double Width, double Height) box)
    {
        box = default;

        if (ElementCssomViewExtensions.GetViewportMetrics(select) is not { } metrics)
        {
            return false;
        }

        box = (metrics.BorderBoxX + metrics.BorderLeft + metrics.PaddingLeft,
            metrics.BorderBoxY + metrics.BorderTop + metrics.PaddingTop,
            Math.Max(0d, metrics.BorderBoxWidth - metrics.BorderLeft - metrics.BorderRight - metrics.PaddingLeft - metrics.PaddingRight),
            Math.Max(0d, metrics.BorderBoxHeight - metrics.BorderTop - metrics.BorderBottom - metrics.PaddingTop - metrics.PaddingBottom));
        return true;
    }
}
