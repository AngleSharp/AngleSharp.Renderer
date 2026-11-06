namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Html.Dom.Events;

/// <summary>
/// The positions of a mouse event that need to know the layout (CSSOM View 11).
/// </summary>
[DomExposed("MouseEvent")]
public static class MouseEventCssomViewExtensions
{
    /// <summary>
    /// Gets the horizontal position on the page.
    /// </summary>
    [DomName("pageX")]
    [DomAccessor(Accessors.Getter)]
    public static double GetPageX(this MouseEvent ev) => ev.ClientX + GetScroll(ev, true);

    /// <summary>
    /// Gets the vertical position on the page.
    /// </summary>
    [DomName("pageY")]
    [DomAccessor(Accessors.Getter)]
    public static double GetPageY(this MouseEvent ev) => ev.ClientY + GetScroll(ev, false);

    /// <summary>
    /// Gets the horizontal position in the padding box of the target.
    /// </summary>
    [DomName("offsetX")]
    [DomAccessor(Accessors.Getter)]
    public static double GetOffsetX(this MouseEvent ev)
    {
        if (ev.OriginalTarget is IElement element)
        {
            var box = element.GetBoundingClientRect();
            return ev.ClientX - box.Left - element.GetClientLeft();
        }

        return ev.ClientX;
    }

    /// <summary>
    /// Gets the vertical position in the padding box of the target.
    /// </summary>
    [DomName("offsetY")]
    [DomAccessor(Accessors.Getter)]
    public static double GetOffsetY(this MouseEvent ev)
    {
        if (ev.OriginalTarget is IElement element)
        {
            var box = element.GetBoundingClientRect();
            return ev.ClientY - box.Top - element.GetClientTop();
        }

        return ev.ClientY;
    }

    private static double GetScroll(MouseEvent ev, bool isHorizontal)
    {
        var root = ev.View?.Document?.DocumentElement;
        return root is null ? 0d : isHorizontal ? root.GetScrollLeft() : root.GetScrollTop();
    }
}
