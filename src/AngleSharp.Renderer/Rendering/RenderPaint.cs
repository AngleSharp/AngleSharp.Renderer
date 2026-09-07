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
/// <param name="Kind">The gradient's shape: linear, radial, or conic.</param>
/// <param name="Stops">The ordered color stops along the gradient.</param>
/// <param name="AngleDegrees">The gradient's angle in degrees, for linear and conic gradients.</param>
/// <param name="CenterX">Fractional center X (0-1) within the painted box, for radial/conic gradients.</param>
/// <param name="CenterY">Fractional center Y (0-1) within the painted box, for radial/conic gradients.</param>
/// <param name="Radius">Unused; retained for source compatibility. Radial sizing is governed by <see cref="SizeKind"/>.</param>
/// <param name="IsCircle">Whether a radial gradient uses a circular (vs. the CSS-default elliptical) ending shape.</param>
/// <param name="Repeating">Whether the gradient repeats past its defined extent (`repeating-*-gradient`).</param>
/// <param name="SizeKind">How a radial gradient's ending shape is sized when not given explicit radii.</param>
/// <param name="ExplicitRadiusX">An explicit radial gradient radius (or the X radius of an explicit ellipse), in pixels.</param>
/// <param name="ExplicitRadiusY">An explicit radial gradient's Y radius, in pixels, when it differs from <see cref="ExplicitRadiusX"/>.</param>
public sealed record RenderGradient(
    RenderGradientKind Kind,
    IReadOnlyList<RenderGradientStop> Stops,
    float AngleDegrees = 90f,
    float CenterX = 0.5f,
    float CenterY = 0.5f,
    float Radius = 0.5f,
    bool IsCircle = false,
    bool Repeating = false,
    RenderGradientSizeKind SizeKind = RenderGradientSizeKind.FarthestCorner,
    float? ExplicitRadiusX = null,
    float? ExplicitRadiusY = null);

/// <summary>
/// Describes a single gradient stop.
/// </summary>
/// <param name="Position">
/// The stop's position as a 0-1 fraction. When the stop was given in an absolute length (`10px`)
/// rather than a percentage, this holds the value that was in effect before
/// <see cref="AbsolutePositionPixels"/> could be resolved (typically 0) and is not the position
/// actually painted with - the backend resolves <see cref="AbsolutePositionPixels"/> against the
/// gradient's own geometry once that is known, since a stop's absolute-length position cannot be
/// turned into a fraction until the gradient's rendered size is.
/// </param>
/// <param name="Color">The stop's color.</param>
/// <param name="AbsolutePositionPixels">
/// The stop's position as an absolute length in pixels (`red 10px`), if it was given as one
/// rather than a percentage/unitless fraction; otherwise <see langword="null"/>.
/// </param>
public sealed record RenderGradientStop(float Position, RenderColor Color, float? AbsolutePositionPixels = null);

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

/// <summary>
/// Describes how a radial gradient's ending shape is sized, matching the CSS `&lt;extent-keyword&gt;`
/// values (or an explicit radius/radii).
/// </summary>
public enum RenderGradientSizeKind
{
    /// <summary>
    /// The ending shape meets the corner of the box farthest from its center. The CSS default.
    /// </summary>
    FarthestCorner,

    /// <summary>
    /// The ending shape meets the side of the box closest to its center.
    /// </summary>
    ClosestSide,

    /// <summary>
    /// The ending shape meets the side of the box farthest from its center.
    /// </summary>
    FarthestSide,

    /// <summary>
    /// The ending shape meets the corner of the box closest to its center.
    /// </summary>
    ClosestCorner,

    /// <summary>
    /// The ending shape uses an explicit radius (<see cref="RenderGradient.ExplicitRadiusX"/> /
    /// <see cref="RenderGradient.ExplicitRadiusY"/>) rather than one derived from the box.
    /// </summary>
    Explicit,
}
