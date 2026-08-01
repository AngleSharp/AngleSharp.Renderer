namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Represents a paint that can fill a rectangle.
/// </summary>
public abstract record RenderPaint;

/// <summary>
/// Represents a solid-color paint.
/// </summary>
public sealed record RenderColorPaint(RenderColor Color) : RenderPaint;

/// <summary>
/// Represents a gradient paint.
/// </summary>
public sealed record RenderGradientPaint(RenderGradient Gradient) : RenderPaint;

/// <summary>
/// Describes a gradient definition.
/// </summary>
public sealed record RenderGradient(
    RenderGradientKind Kind,
    IReadOnlyList<RenderGradientStop> Stops,
    float AngleDegrees = 90f,
    float CenterX = 0.5f,
    float CenterY = 0.5f,
    float Radius = 0.5f,
    bool IsCircle = false);

/// <summary>
/// Describes a single gradient stop.
/// </summary>
public sealed record RenderGradientStop(float Position, RenderColor Color);

/// <summary>
/// Describes the available gradient kinds.
/// </summary>
public enum RenderGradientKind
{
    /// <summary>
    /// Draws a linear gradient.
    /// </summary>
    Linear,

    /// <summary>
    /// Draws a radial gradient.
    /// </summary>
    Radial,

    /// <summary>
    /// Draws a conic gradient.
    /// </summary>
    Conic,
}
