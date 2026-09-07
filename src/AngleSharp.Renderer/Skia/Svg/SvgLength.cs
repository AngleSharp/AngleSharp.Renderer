namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;

/// <summary>
/// The current SVG viewport (in user-space units) that percentage lengths resolve against. The
/// root &lt;svg&gt; establishes the first one; a nested &lt;svg&gt; or a &lt;symbol&gt; referenced
/// through &lt;use&gt; establishes a new one for its own subtree.
/// </summary>
internal readonly record struct SvgViewport(float Width, float Height)
{
    /// <summary>
    /// The reference length percentages on non-axis-specific properties (`r`, `stroke-width`, ...)
    /// resolve against - the length of the viewport diagonal divided by sqrt(2), per the SVG spec.
    /// </summary>
    public float DiagonalReference => (float)(Math.Sqrt((double)(Width * Width) + (Height * Height)) / Math.Sqrt(2));
}

/// <summary>
/// Resolves an SVG/CSS length that may be a plain number, a `px` value, or a percentage of some
/// reference dimension (a viewport axis, or the viewport diagonal for non-axis-specific lengths).
/// </summary>
internal static class SvgLength
{
    public static float? ParseOptional(string? value, float referenceDimension)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.EndsWith('%'))
        {
            return float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? percent / 100f * referenceDimension
                : null;
        }

        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2];
        }

        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    public static float Parse(string? value, float referenceDimension, float fallback) =>
        ParseOptional(value, referenceDimension) ?? fallback;
}
