namespace AngleSharp.Dom;

using AngleSharp.Attributes;

/// <summary>
/// Options for absolute element scrolling.
/// </summary>
[DomName("ScrollToOptions")]
[DomExposed("Window")]
public sealed class ScrollToOptions
{
    /// <summary>
    /// Gets or sets the horizontal destination.
    /// </summary>
    [DomName("left")]
    public double? Left { get; set; }

    /// <summary>
    /// Gets or sets the vertical destination.
    /// </summary>
    [DomName("top")]
    public double? Top { get; set; }

    /// <summary>
    /// Gets or sets the scroll behavior.
    /// </summary>
    [DomName("behavior")]
    public ScrollBehavior Behavior { get; set; } = ScrollBehavior.Auto;
}
