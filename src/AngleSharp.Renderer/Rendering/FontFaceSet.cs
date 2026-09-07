namespace AngleSharp.Renderer.Rendering;

using System.Linq;

/// <summary>
/// The <c>@font-face</c> declarations that apply to a document.
/// </summary>
public sealed class FontFaceSet
{
    /// <summary>
    /// An empty set, used for documents that declare no custom fonts.
    /// </summary>
    public static readonly FontFaceSet Empty = new([]);

    private readonly Dictionary<string, List<FontFace>> _facesByFamily;

    /// <summary>
    /// Creates a set from the given faces.
    /// </summary>
    /// <param name="faces">The faces to include.</param>
    public FontFaceSet(IEnumerable<FontFace> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);

        Faces = faces.ToArray();
        _facesByFamily = new Dictionary<string, List<FontFace>>(StringComparer.OrdinalIgnoreCase);

        foreach (var face in Faces)
        {
            if (!_facesByFamily.TryGetValue(face.Family, out var group))
            {
                group = [];
                _facesByFamily[face.Family] = group;
            }

            group.Add(face);
        }
    }

    /// <summary>
    /// Gets the declared faces.
    /// </summary>
    public IReadOnlyList<FontFace> Faces { get; }

    /// <summary>
    /// Gets whether the set declares no faces at all.
    /// </summary>
    public bool IsEmpty => Faces.Count == 0;

    /// <summary>
    /// Finds the face that best matches the requested family, weight and style.
    /// </summary>
    /// <remarks>
    /// This is a reduced form of the CSS font matching algorithm: a matching slant wins first,
    /// then the nearest weight. Stretch and unicode ranges are not considered.
    /// </remarks>
    public bool TryMatch(string family, float weight, bool isItalic, out FontFace face)
    {
        if (!_facesByFamily.TryGetValue(family, out var candidates))
        {
            face = null!;
            return false;
        }

        face = candidates
            .OrderBy(candidate => candidate.IsItalic == isItalic ? 0 : 1)
            .ThenBy(candidate => Math.Abs(candidate.Weight - weight))
            .First();

        return true;
    }
}
