namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Represents an RGBA color used by the rendering pipeline.
/// </summary>
public readonly record struct RenderColor(byte R, byte G, byte B, byte A = byte.MaxValue)
{
    /// <summary>
    /// Represents an opaque white color.
    /// </summary>
    public static RenderColor White { get; } = new(byte.MaxValue, byte.MaxValue, byte.MaxValue);

    /// <summary>
    /// Represents an opaque black color.
    /// </summary>
    public static RenderColor Black { get; } = new(0, 0, 0);

    /// <summary>
    /// Represents a fully transparent color.
    /// </summary>
    public static RenderColor Transparent { get; } = new(0, 0, 0, 0);
}