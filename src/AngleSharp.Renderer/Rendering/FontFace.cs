namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// One source listed in the <c>src</c> descriptor of an <c>@font-face</c> rule.
/// </summary>
/// <param name="Data">The raw font file, when the source was a <c>url()</c>.</param>
/// <param name="LocalFamily">The installed family, when the source was a <c>local()</c>.</param>
public readonly record struct FontFaceSource(byte[]? Data, string? LocalFamily)
{
    /// <summary>
    /// Creates a source backed by an embedded font file.
    /// </summary>
    public static FontFaceSource FromData(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new FontFaceSource(data, null);
    }

    /// <summary>
    /// Creates a source that refers to an installed family.
    /// </summary>
    public static FontFaceSource FromLocal(string localFamily)
    {
        ArgumentNullException.ThrowIfNull(localFamily);
        return new FontFaceSource(null, localFamily);
    }
}

/// <summary>
/// Represents a single <c>@font-face</c> declaration.
/// </summary>
public sealed class FontFace
{
    /// <summary>
    /// Creates a face.
    /// </summary>
    /// <param name="family">The family name the face is registered under.</param>
    /// <param name="weight">The numeric weight the face provides.</param>
    /// <param name="isItalic">Whether the face is italic or oblique.</param>
    /// <param name="sources">The sources to try, in declaration order.</param>
    /// <param name="weightDescriptor">The authored weight descriptor.</param>
    /// <param name="style">The authored style descriptor.</param>
    /// <param name="stretch">The authored stretch descriptor.</param>
    /// <param name="unicodeRange">The authored Unicode range descriptor.</param>
    /// <param name="display">The authored display descriptor.</param>
    public FontFace(
        string family,
        float weight,
        bool isItalic,
        IEnumerable<FontFaceSource> sources,
        string? weightDescriptor = null,
        string? style = null,
        string? stretch = null,
        string? unicodeRange = null,
        string? display = null)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(sources);

        Family = family;
        Weight = weight;
        IsItalic = isItalic;
        Sources = [.. sources];
        WeightDescriptor = weightDescriptor ?? weight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Style = style ?? (isItalic ? "italic" : "normal");
        Stretch = stretch ?? "normal";
        UnicodeRange = unicodeRange ?? "U+0-10FFFF";
        Display = display ?? "auto";
    }

    /// <summary>
    /// Gets the family name the face is registered under.
    /// </summary>
    public string Family { get; }

    /// <summary>
    /// Gets the numeric weight the face provides.
    /// </summary>
    public float Weight { get; }

    /// <summary>
    /// Gets whether the face is italic or oblique.
    /// </summary>
    public bool IsItalic { get; }

    /// <summary>
    /// Gets the authored weight descriptor.
    /// </summary>
    public string WeightDescriptor { get; }

    /// <summary>
    /// Gets the authored style descriptor.
    /// </summary>
    public string Style { get; }

    /// <summary>
    /// Gets the authored stretch descriptor.
    /// </summary>
    public string Stretch { get; }

    /// <summary>
    /// Gets the authored Unicode range descriptor.
    /// </summary>
    public string UnicodeRange { get; }

    /// <summary>
    /// Gets whether the Unicode range of the face includes the code point, i.e., the face is the
    /// one to draw it (e.g. Google Fonts declares a family as faces for Latin, Cyrillic, ...).
    /// </summary>
    internal bool Covers(int codePoint)
    {
        foreach (var range in UnicodeRange.Split(','))
        {
            if (AngleSharp.Dom.DocumentFontFaceSet.IncludesCodePoint(range, codePoint))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the font display descriptor.
    /// </summary>
    public string Display { get; }

    /// <summary>
    /// Gets the sources to try, in declaration order.
    /// </summary>
    /// <remarks>
    /// The order matters and resolution is deliberately deferred: whether a <c>local()</c> source
    /// is usable depends on the fonts installed on the machine, which only the backend knows. A
    /// source that cannot be used falls through to the next one.
    /// </remarks>
    public IReadOnlyList<FontFaceSource> Sources { get; }
}
