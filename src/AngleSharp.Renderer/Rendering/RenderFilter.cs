namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Identifies which CSS Filter Effects function a <see cref="RenderFilterFunction"/> represents.
/// </summary>
public enum RenderFilterFunctionKind
{
    /// <summary>
    /// `blur(&lt;length&gt;)` - a Gaussian blur.
    /// </summary>
    Blur,

    /// <summary>
    /// `brightness(&lt;number-or-percentage&gt;)` - linearly multiplies every color channel.
    /// </summary>
    Brightness,

    /// <summary>
    /// `contrast(&lt;number-or-percentage&gt;)` - linearly scales color channels around 50% gray.
    /// </summary>
    Contrast,

    /// <summary>
    /// `grayscale(&lt;number-or-percentage&gt;)` - desaturates toward a luminance-preserving gray.
    /// </summary>
    Grayscale,

    /// <summary>
    /// `hue-rotate(&lt;angle&gt;)` - rotates every color's hue around the color wheel.
    /// </summary>
    HueRotate,

    /// <summary>
    /// `invert(&lt;number-or-percentage&gt;)` - inverts color channels.
    /// </summary>
    Invert,

    /// <summary>
    /// `opacity(&lt;number-or-percentage&gt;)` - scales the alpha channel.
    /// </summary>
    Opacity,

    /// <summary>
    /// `saturate(&lt;number-or-percentage&gt;)` - scales color saturation.
    /// </summary>
    Saturate,

    /// <summary>
    /// `sepia(&lt;number-or-percentage&gt;)` - shifts toward a sepia tone.
    /// </summary>
    Sepia,

    /// <summary>
    /// `drop-shadow(&lt;offset-x&gt; &lt;offset-y&gt; &lt;blur-radius&gt;? &lt;color&gt;?)` - a blurred,
    /// offset silhouette painted behind the element's own (already-filtered) alpha.
    /// </summary>
    DropShadow,
}

/// <summary>
/// A single function from a CSS `filter` value (e.g. one of `grayscale(0.9) blur(2px)`'s two
/// entries), backend-agnostic like every other <c>Rendering/*</c> value type. <see cref="Amount"/>
/// is interpreted per <see cref="Kind"/>: a blur radius in pixels for
/// <see cref="RenderFilterFunctionKind.Blur"/> (and, for <see cref="RenderFilterFunctionKind.DropShadow"/>,
/// its own blur radius), degrees for <see cref="RenderFilterFunctionKind.HueRotate"/>, and an
/// already-normalized `0..1`-or-higher multiplier for every other function - a percentage argument
/// like `grayscale(90%)` is converted to `0.9` at parse time, not carried as a percentage.
/// <see cref="OffsetX"/>/<see cref="OffsetY"/>/<see cref="Color"/> are only meaningful for
/// <see cref="RenderFilterFunctionKind.DropShadow"/>.
/// </summary>
/// <param name="Kind">Which filter function this is.</param>
/// <param name="Amount">The function's primary numeric argument; see the type summary for units per <paramref name="Kind"/>.</param>
/// <param name="OffsetX">`drop-shadow`'s horizontal offset in pixels; unused by every other kind.</param>
/// <param name="OffsetY">`drop-shadow`'s vertical offset in pixels; unused by every other kind.</param>
/// <param name="Color">`drop-shadow`'s shadow color; unused by every other kind.</param>
public sealed record RenderFilterFunction(
    RenderFilterFunctionKind Kind,
    float Amount,
    float OffsetX,
    float OffsetY,
    RenderColor Color)
{
    /// <summary>Creates a `blur(&lt;length&gt;)` function.</summary>
    public static RenderFilterFunction Blur(float radiusPixels) =>
        new(RenderFilterFunctionKind.Blur, radiusPixels, 0f, 0f, RenderColor.Black);

    /// <summary>Creates a `brightness(&lt;number-or-percentage&gt;)` function.</summary>
    public static RenderFilterFunction Brightness(float amount) =>
        new(RenderFilterFunctionKind.Brightness, amount, 0f, 0f, RenderColor.Black);

    /// <summary>Creates a `contrast(&lt;number-or-percentage&gt;)` function.</summary>
    public static RenderFilterFunction Contrast(float amount) =>
        new(RenderFilterFunctionKind.Contrast, amount, 0f, 0f, RenderColor.Black);

    /// <summary>Creates a `grayscale(&lt;number-or-percentage&gt;)` function.</summary>
    public static RenderFilterFunction Grayscale(float amount) =>
        new(RenderFilterFunctionKind.Grayscale, amount, 0f, 0f, RenderColor.Black);

    /// <summary>Creates a `hue-rotate(&lt;angle&gt;)` function.</summary>
    public static RenderFilterFunction HueRotate(float angleDegrees) =>
        new(RenderFilterFunctionKind.HueRotate, angleDegrees, 0f, 0f, RenderColor.Black);

    /// <summary>Creates an `invert(&lt;number-or-percentage&gt;)` function.</summary>
    public static RenderFilterFunction Invert(float amount) =>
        new(RenderFilterFunctionKind.Invert, amount, 0f, 0f, RenderColor.Black);

    /// <summary>Creates an `opacity(&lt;number-or-percentage&gt;)` function.</summary>
    public static RenderFilterFunction Opacity(float amount) =>
        new(RenderFilterFunctionKind.Opacity, amount, 0f, 0f, RenderColor.Black);

    /// <summary>Creates a `saturate(&lt;number-or-percentage&gt;)` function.</summary>
    public static RenderFilterFunction Saturate(float amount) =>
        new(RenderFilterFunctionKind.Saturate, amount, 0f, 0f, RenderColor.Black);

    /// <summary>Creates a `sepia(&lt;number-or-percentage&gt;)` function.</summary>
    public static RenderFilterFunction Sepia(float amount) =>
        new(RenderFilterFunctionKind.Sepia, amount, 0f, 0f, RenderColor.Black);

    /// <summary>Creates a `drop-shadow(&lt;offset-x&gt; &lt;offset-y&gt; &lt;blur-radius&gt;? &lt;color&gt;?)` function.</summary>
    public static RenderFilterFunction DropShadow(float offsetX, float offsetY, float blurRadiusPixels, RenderColor color) =>
        new(RenderFilterFunctionKind.DropShadow, blurRadiusPixels, offsetX, offsetY, color);
}
