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
    public FontFace(string family, float weight, bool isItalic, IEnumerable<FontFaceSource> sources)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(sources);

        Family = family;
        Weight = weight;
        IsItalic = isItalic;
        Sources = [.. sources];
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
    /// Gets the sources to try, in declaration order.
    /// </summary>
    /// <remarks>
    /// The order matters and resolution is deliberately deferred: whether a <c>local()</c> source
    /// is usable depends on the fonts installed on the machine, which only the backend knows. A
    /// source that cannot be used falls through to the next one.
    /// </remarks>
    public IReadOnlyList<FontFaceSource> Sources { get; }
}
