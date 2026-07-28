namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Represents a rectangle in renderer coordinates.
/// </summary>
public readonly record struct RenderRect(float X, float Y, float Width, float Height)
{
    /// <summary>
    /// Indicates if the rectangle has a non-positive width or height.
    /// </summary>
    public bool IsEmpty => Width <= 0f || Height <= 0f;
}