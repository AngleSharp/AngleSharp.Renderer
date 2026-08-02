namespace AngleSharp.Dom;

using AngleSharp.Attributes;

/// <summary>
/// Defines logical alignment positions for scrolling.
/// </summary>
[DomName("ScrollLogicalPosition")]
public enum ScrollLogicalPosition
{
    /// <summary>
    /// Aligns the start edge.
    /// </summary>
    [DomName("start")]
    Start,

    /// <summary>
    /// Centers the target.
    /// </summary>
    [DomName("center")]
    Center,

    /// <summary>
    /// Aligns the end edge.
    /// </summary>
    [DomName("end")]
    End,

    /// <summary>
    /// Uses the nearest edge that requires minimal movement.
    /// </summary>
    [DomName("nearest")]
    Nearest,
}
