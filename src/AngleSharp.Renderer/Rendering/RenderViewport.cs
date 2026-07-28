namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Defines the target viewport for rendering.
/// </summary>
public readonly record struct RenderViewport(int Width, int Height)
{
    /// <summary>
    /// Indicates if the viewport dimensions are invalid.
    /// </summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}