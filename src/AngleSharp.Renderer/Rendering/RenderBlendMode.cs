namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// A CSS <c>&lt;blend-mode&gt;</c> (used by <c>mix-blend-mode</c> and <c>background-blend-mode</c>).
/// </summary>
public enum RenderBlendMode
{
    /// <summary>Plain source-over compositing.</summary>
    Normal,

    /// <summary><c>multiply</c>.</summary>
    Multiply,

    /// <summary><c>screen</c>.</summary>
    Screen,

    /// <summary><c>overlay</c>.</summary>
    Overlay,

    /// <summary><c>darken</c>.</summary>
    Darken,

    /// <summary><c>lighten</c>.</summary>
    Lighten,

    /// <summary><c>color-dodge</c>.</summary>
    ColorDodge,

    /// <summary><c>color-burn</c>.</summary>
    ColorBurn,

    /// <summary><c>hard-light</c>.</summary>
    HardLight,

    /// <summary><c>soft-light</c>.</summary>
    SoftLight,

    /// <summary><c>difference</c>.</summary>
    Difference,

    /// <summary><c>exclusion</c>.</summary>
    Exclusion,

    /// <summary><c>hue</c>.</summary>
    Hue,

    /// <summary><c>saturation</c>.</summary>
    Saturation,

    /// <summary><c>color</c>.</summary>
    Color,

    /// <summary><c>luminosity</c>.</summary>
    Luminosity,
}
