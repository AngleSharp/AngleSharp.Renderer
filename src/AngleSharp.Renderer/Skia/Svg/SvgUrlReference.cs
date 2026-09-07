namespace AngleSharp.Renderer.Skia.Svg;

/// <summary>
/// Extracts the fragment id out of an SVG `url(#id)` reference, used by `fill`, `stroke`,
/// `clip-path` and `mask`.
/// </summary>
internal static class SvgUrlReference
{
    public static bool TryExtract(string? value, out string id)
    {
        id = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (!trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(')'))
        {
            return false;
        }

        var inner = trimmed[4..^1].Trim().Trim('\'', '"');

        if (!inner.StartsWith('#') || inner.Length < 2)
        {
            return false;
        }

        id = inner[1..];
        return true;
    }

    /// <summary>
    /// Resolves the `href`/`xlink:href` attribute an element may carry (SVG2 dropped the
    /// namespace prefix, but SVG1.1 content still uses it).
    /// </summary>
    public static string? GetHref(AngleSharp.Dom.IElement element) =>
        element.GetAttribute("href") is { Length: > 0 } href
            ? href
            : element.GetAttribute("xlink:href") is { Length: > 0 } legacyHref
                ? legacyHref
                : null;
}
