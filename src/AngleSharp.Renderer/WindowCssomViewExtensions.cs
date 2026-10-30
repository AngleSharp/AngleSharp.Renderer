namespace AngleSharp.Dom;

using AngleSharp.Attributes;

/// <summary>
/// Provides the CSSOM View scrolling members of the window, which scroll the viewport, i.e.
/// the document element.
/// </summary>
[DomExposed("Window")]
public static class WindowCssomViewExtensions
{
    /// <summary>
    /// Returns the horizontal scroll position of the viewport.
    /// </summary>
    [DomName("scrollX")]
    [DomName("pageXOffset")]
    [DomAccessor(Accessors.Getter)]
    public static double GetScrollX(this IWindow window) =>
        GetScrollingElement(window)?.GetScrollLeft() ?? 0d;

    /// <summary>
    /// Returns the vertical scroll position of the viewport.
    /// </summary>
    [DomName("scrollY")]
    [DomName("pageYOffset")]
    [DomAccessor(Accessors.Getter)]
    public static double GetScrollY(this IWindow window) =>
        GetScrollingElement(window)?.GetScrollTop() ?? 0d;

    /// <summary>
    /// Scrolls the viewport to the given position.
    /// </summary>
    [DomName("scrollTo")]
    [DomName("scroll")]
    public static void ScrollTo(this IWindow window, double x, double y) =>
        GetScrollingElement(window)?.ScrollTo(x, y);

    /// <summary>
    /// Scrolls the viewport to the position of the given options.
    /// </summary>
    [DomName("scrollTo")]
    [DomName("scroll")]
    public static void ScrollTo(this IWindow window, ScrollToOptions options) =>
        GetScrollingElement(window)?.ScrollTo(options);

    /// <summary>
    /// Scrolls the viewport by the given deltas.
    /// </summary>
    [DomName("scrollBy")]
    public static void ScrollBy(this IWindow window, double x, double y) =>
        GetScrollingElement(window)?.ScrollBy(x, y);

    /// <summary>
    /// Scrolls the viewport by the deltas of the given options.
    /// </summary>
    [DomName("scrollBy")]
    public static void ScrollBy(this IWindow window, ScrollToOptions options) =>
        GetScrollingElement(window)?.ScrollBy(options);

    private static IElement? GetScrollingElement(IWindow window) => window?.Document?.DocumentElement;
}
