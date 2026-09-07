namespace AngleSharp.Dom;

using AngleSharp.Attributes;

/// <summary>
/// Options for scrolling an element into view.
/// </summary>
[DomName("ScrollIntoViewOptions")]
[DomExposed("Window")]
public sealed class ScrollIntoViewOptions
{
    /// <summary>
    /// Gets or sets the scroll behavior.
    /// </summary>
    [DomName("behavior")]
    public ScrollBehavior Behavior { get; set; } = ScrollBehavior.Auto;

    /// <summary>
    /// Gets or sets vertical alignment mode.
    /// </summary>
    [DomName("block")]
    public ScrollLogicalPosition Block { get; set; } = ScrollLogicalPosition.Start;

    /// <summary>
    /// Gets or sets horizontal alignment mode.
    /// </summary>
    [DomName("inline")]
    public ScrollLogicalPosition Inline { get; set; } = ScrollLogicalPosition.Nearest;
}
