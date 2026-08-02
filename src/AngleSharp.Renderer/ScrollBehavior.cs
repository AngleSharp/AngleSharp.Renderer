namespace AngleSharp.Dom;

using AngleSharp.Attributes;

/// <summary>
/// Defines how a scroll operation should be animated.
/// </summary>
[DomName("ScrollBehavior")]
public enum ScrollBehavior
{
    /// <summary>
    /// Uses automatic behavior.
    /// </summary>
    [DomName("auto")]
    Auto,

    /// <summary>
    /// Scrolls instantly.
    /// </summary>
    [DomName("instant")]
    Instant,

    /// <summary>
    /// Scrolls smoothly.
    /// </summary>
    [DomName("smooth")]
    Smooth,
}
