namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Describes the four independent corner radii of a rounded box (`border-radius`), each as a
/// separate horizontal/vertical pair so elliptical corners (`border-radius: 8px / 4px`) are
/// representable, not just circular ones.
/// </summary>
public readonly record struct RenderCornerRadii(
    float TopLeftX,
    float TopLeftY,
    float TopRightX,
    float TopRightY,
    float BottomRightX,
    float BottomRightY,
    float BottomLeftX,
    float BottomLeftY)
{
    /// <summary>
    /// No rounding on any corner - the common case, and the default for elements without
    /// `border-radius`.
    /// </summary>
    public static RenderCornerRadii Zero { get; } = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

    /// <summary>
    /// Whether every corner is unrounded, so callers can take the cheaper plain-rectangle path.
    /// </summary>
    public bool IsZero =>
        TopLeftX <= 0f && TopLeftY <= 0f &&
        TopRightX <= 0f && TopRightY <= 0f &&
        BottomRightX <= 0f && BottomRightY <= 0f &&
        BottomLeftX <= 0f && BottomLeftY <= 0f;

    /// <summary>
    /// Scales every radius down (uniformly) just enough that adjacent corners along the same edge
    /// never overlap, per the CSS corner-overlap-prevention algorithm
    /// (https://www.w3.org/TR/css-backgrounds-3/#corner-overlap). A radius pair whose sum already
    /// fits within the edge it shares is left untouched.
    /// </summary>
    public RenderCornerRadii ClampToBox(float width, float height)
    {
        if (width <= 0f || height <= 0f)
        {
            return Zero;
        }

        var scale = 1f;

        scale = Math.Min(scale, SafeRatio(width, TopLeftX + TopRightX));
        scale = Math.Min(scale, SafeRatio(height, TopRightY + BottomRightY));
        scale = Math.Min(scale, SafeRatio(width, BottomRightX + BottomLeftX));
        scale = Math.Min(scale, SafeRatio(height, BottomLeftY + TopLeftY));

        if (scale >= 1f)
        {
            return this;
        }

        return new RenderCornerRadii(
            TopLeftX * scale, TopLeftY * scale,
            TopRightX * scale, TopRightY * scale,
            BottomRightX * scale, BottomRightY * scale,
            BottomLeftX * scale, BottomLeftY * scale);
    }

    private static float SafeRatio(float available, float requested) =>
        requested > 0f ? available / requested : float.MaxValue;
}
