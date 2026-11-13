namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Events;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;
using System.Collections;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RenderFontFace = AngleSharp.Renderer.Rendering.FontFace;

/// <summary>Descriptors accepted by the CSS Font Loading API.</summary>
[DomName("FontFaceDescriptors")]
public sealed class FontFaceDescriptors
{
    /// <summary>Gets or sets the font style descriptor.</summary>
    [DomName("style")]
    public string? Style { get; set; }

    /// <summary>Gets or sets the font weight descriptor.</summary>
    [DomName("weight")]
    public string? Weight { get; set; }

    /// <summary>Gets or sets the font stretch descriptor.</summary>
    [DomName("stretch")]
    public string? Stretch { get; set; }

    /// <summary>Gets or sets the Unicode range descriptor.</summary>
    [DomName("unicodeRange")]
    public string? UnicodeRange { get; set; }

    /// <summary>Gets or sets the font display descriptor.</summary>
    [DomName("display")]
    public string? Display { get; set; }
}

/// <summary>A script-constructible web font face.</summary>
[DomName("FontFace")]
[DomExposed("Window")]
public sealed class DocumentFontFace
{
    private readonly IDocument _document;
    private readonly object _source;
    private readonly TaskCompletionSource<DocumentFontFace> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RenderFontFace? _renderFace;
    private string _status = "unloaded";

    /// <summary>Creates a font face from a URL source or font binary data.</summary>
    [DomConstructor]
    public DocumentFontFace(IWindow window, string family, object source, FontFaceDescriptors? descriptors = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(source);
        Family = family.Trim().Trim('\'', '"');
        if (Family.Length == 0)
        {
            throw new ArgumentException("The font family must not be empty.", nameof(family));
        }

        _source = source;
        Style = descriptors?.Style ?? "normal";
        Weight = descriptors?.Weight ?? "normal";
        Stretch = descriptors?.Stretch ?? "normal";
        UnicodeRange = descriptors?.UnicodeRange ?? "U+0-10FFFF";
        Display = descriptors?.Display ?? "auto";
        _document = window.Document;
    }

    internal DocumentFontFace(IDocument document, RenderFontFace face)
    {
        _document = document;
        Family = face.Family;
        Weight = face.WeightDescriptor;
        Style = face.Style;
        Stretch = face.Stretch;
        UnicodeRange = face.UnicodeRange;
        Display = face.Display;
        _source = Array.Empty<byte>();
        _renderFace = face;
        _status = "loaded";
        _loaded.TrySetResult(this);
    }

    /// <summary>Gets the family name.</summary>
    [DomName("family")]
    public string Family { get; }

    /// <summary>Gets the style descriptor.</summary>
    [DomName("style")]
    public string Style { get; }

    /// <summary>Gets the weight descriptor.</summary>
    [DomName("weight")]
    public string Weight { get; }

    /// <summary>Gets the stretch descriptor.</summary>
    [DomName("stretch")]
    public string Stretch { get; }

    /// <summary>Gets the Unicode range descriptor.</summary>
    [DomName("unicodeRange")]
    public string UnicodeRange { get; }

    /// <summary>Gets the display descriptor.</summary>
    [DomName("display")]
    public string Display { get; }

    /// <summary>Gets the loading status.</summary>
    [DomName("status")]
    public string Status => _status;

    /// <summary>Gets the promise for this face's load operation.</summary>
    [DomName("loaded")]
    public Task<DocumentFontFace> Loaded => _loaded.Task;

    internal RenderFontFace? RenderFace => _renderFace;

    /// <summary>Loads the font and resolves to this face.</summary>
    [DomName("load")]
    public Task<DocumentFontFace> Load()
    {
        if (_status is "loaded" or "loading" or "error")
        {
            return _loaded.Task;
        }

        _status = "loading";
        DocumentFontFaceSet.For(_document).FaceStarted();
        _ = Task.Run(() =>
        {
            try
            {
                byte[] bytes;
                if (_source is byte[] data)
                {
                    if (!FontFaceLoader.TryDecodeProgrammaticData(data, out bytes))
                    {
                        throw new DomException(DomError.Network);
                    }
                }
                else if (_source is IEnumerable<byte> byteSequence)
                {
                    if (!FontFaceLoader.TryDecodeProgrammaticData(byteSequence.ToArray(), out bytes))
                    {
                        throw new DomException(DomError.Network);
                    }
                }
                else if (_source is string url && FontFaceLoader.TryLoadProgrammaticSource(_document, url, out bytes))
                {
                }
                else
                {
                    throw new DomException(DomError.Network);
                }

                var weight = ParseWeight(Weight);
                var italic = Style.Contains("italic", StringComparison.OrdinalIgnoreCase) ||
                    Style.Contains("oblique", StringComparison.OrdinalIgnoreCase);
                _renderFace = new RenderFontFace(
                    Family,
                    weight,
                    italic,
                    [FontFaceSource.FromData(bytes)],
                    Weight,
                    Style,
                    Stretch,
                    UnicodeRange,
                    Display);
                _status = "loaded";
                _loaded.TrySetResult(this);
                DocumentFontFaceSet.TryGet(_document)?.FaceLoaded(this);
            }
            catch (Exception exception)
            {
                _status = "error";
                _loaded.TrySetException(exception);
                DocumentFontFaceSet.TryGet(_document)?.FaceFailed(this);
            }
        });

        return _loaded.Task;
    }

    private static float ParseWeight(string weight) => weight.Trim().ToLowerInvariant() switch
    {
        "bold" => 700f,
        "normal" => 400f,
        _ when float.TryParse(weight, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) => value,
        _ => 400f,
    };
}

/// <summary>The document's CSS Font Loading API set.</summary>
[DomName("FontFaceSet")]
[DomExposed("Window")]
public sealed class DocumentFontFaceSet : EventTarget, IEnumerable<DocumentFontFace>
{
    private static readonly ConditionalWeakTable<IDocument, DocumentFontFaceSet> s_sets = new();
    private readonly IDocument _document;
    private readonly List<DocumentFontFace> _faces = [];
    private readonly Dictionary<string, DocumentFontFace> _cssFaces = new(StringComparer.Ordinal);
    private TaskCompletionSource<DocumentFontFaceSet> _ready;
    private int _pending;

    private DocumentFontFaceSet(IDocument document)
    {
        _document = document;
        _ready = new TaskCompletionSource<DocumentFontFaceSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready.SetResult(this);
    }

    internal static DocumentFontFaceSet? TryGet(IDocument document) =>
        s_sets.TryGetValue(document, out var set) ? set : null;

    internal static DocumentFontFaceSet For(IDocument document) =>
        s_sets.GetValue(document, static value => new DocumentFontFaceSet(value));

    /// <summary>Gets the number of CSS and programmatic faces in the set.</summary>
    [DomName("size")]
    public int Size
    {
        get
        {
            RefreshCssFaces();
            return _faces.Count + _cssFaces.Count;
        }
    }

    /// <summary>Gets whether the set is loading or loaded.</summary>
    [DomName("status")]
    public string Status => _pending == 0 ? "loaded" : "loading";

    /// <summary>Gets a promise that settles when the current loading work completes.</summary>
    [DomName("ready")]
    public Task<DocumentFontFaceSet> Ready => _ready.Task;

    /// <summary>Adds a loaded face to the set.</summary>
    [DomName("add")]
    public DocumentFontFaceSet Add(DocumentFontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (face.Status != "loaded" || face.RenderFace is null)
        {
            throw new DomException(DomError.InvalidState);
        }

        if (!_faces.Contains(face))
        {
            _faces.Add(face);
            InvalidateLayout();
        }

        return this;
    }

    /// <summary>Removes a programmatically added face from the set.</summary>
    [DomName("delete")]
    public bool Delete(DocumentFontFace face)
    {
        var removed = _faces.Remove(face);
        if (removed)
        {
            InvalidateLayout();
        }
        return removed;
    }

    /// <summary>Removes all programmatically added faces from the set.</summary>
    [DomName("clear")]
    public void Clear()
    {
        if (_faces.Count > 0)
        {
            _faces.Clear();
            InvalidateLayout();
        }
    }

    /// <summary>Tests whether a face is in the set.</summary>
    [DomName("has")]
    public bool Has(DocumentFontFace face)
    {
        RefreshCssFaces();
        return _faces.Contains(face) || _cssFaces.Values.Contains(face);
    }

    /// <summary>Invokes a callback for each face in the set.</summary>
    [DomName("forEach")]
    public void ForEach(Action<DocumentFontFace, DocumentFontFaceSet, int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var faces = GetFaces();
        for (var index = 0; index < faces.Count; index++)
        {
            callback(faces[index], this, index);
        }
    }

    /// <summary>Gets an iterator over the faces.</summary>
    [DomName("values")]
    public IEnumerator<DocumentFontFace> GetEnumerator() => GetFaces().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Checks whether the given font can be used without loading more faces.</summary>
    [DomName("check")]
    public bool Check(string font, string text = " ")
    {
        var families = ParseFamilies(font);
        return !GetFaces().Any(face => families.Contains(face.Family, StringComparer.OrdinalIgnoreCase) && SupportsText(face, text) &&
            face.Status is "unloaded" or "loading");
    }

    /// <summary>Loads the set's faces matching the given CSS font string.</summary>
    [DomName("load")]
    public async Task<DocumentFontFace[]> Load(string font, string text = " ")
    {
        var families = ParseFamilies(font);
        var matching = GetFaces().Where(face => families.Contains(face.Family, StringComparer.OrdinalIgnoreCase) && SupportsText(face, text)).ToArray();
        if (matching.Length == 0)
        {
            return [];
        }

        await Task.WhenAll(matching.Select(face => face.Load())).ConfigureAwait(false);
        return matching;
    }

    internal IEnumerable<RenderFontFace> LoadedFaces => _faces
        .Select(face => face.RenderFace)
        .Where(face => face is not null)
        .Cast<RenderFontFace>();

    internal void FaceLoaded(DocumentFontFace face)
    {
        CompleteLoad("loadingdone");
        InvalidateLayout();
    }

    internal void FaceStarted() => BeginLoad();

    internal void FaceFailed(DocumentFontFace face) => CompleteLoad("loadingerror");

    private List<DocumentFontFace> GetFaces()
    {
        RefreshCssFaces();
        return [.. _cssFaces.Values, .. _faces];
    }

    private void RefreshCssFaces()
    {
        var current = new Dictionary<string, DocumentFontFace>(StringComparer.Ordinal);
        foreach (var face in FontFaceLoader.LoadCss(_document))
        {
            var sourceKey = string.Join('|', face.Sources.Select(source =>
                source.LocalFamily ?? (source.Data is { } data ? RuntimeHelpers.GetHashCode(data).ToString() : string.Empty)));
            var key = $"{face.Family}\0{face.WeightDescriptor}\0{face.Style}\0{face.Stretch}\0{face.UnicodeRange}\0{sourceKey}";
            current[key] = _cssFaces.TryGetValue(key, out var existing)
                ? existing
                : new DocumentFontFace(_document, face);
        }

        _cssFaces.Clear();
        foreach (var pair in current)
        {
            _cssFaces.Add(pair.Key, pair.Value);
        }
    }

    private void BeginLoad()
    {
        if (_pending++ == 0)
        {
            _ready = new TaskCompletionSource<DocumentFontFaceSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatch("loading");
        }
    }

    private void CompleteLoad(string eventName)
    {
        if (--_pending == 0)
        {
            Dispatch(eventName);
            _ready.TrySetResult(this);
        }
    }

    private void InvalidateLayout()
    {
        if (_document.Context.TryGetDomHarness(out var harness) && harness is InteractiveHtmlRendererState state)
        {
            state.OnFontsChanged();
        }
    }

    private void Dispatch(string type) => Dispatch(new Event(type));

    private static List<string> ParseFamilies(string font)
    {
        var match = Regex.Match(font, @"(?:^|\s)(?:\d+(?:\.\d+)?(?:px|pt|pc|em|rem|%))(?:\s*/\s*[^ ]+)?\s+(.+)$", RegexOptions.IgnoreCase);
        var familyList = match.Success ? match.Groups[1].Value : font;
        return familyList.Split(',').Select(value => value.Trim().Trim('\'', '"')).Where(value => value.Length > 0).ToList();
    }

    private static bool SupportsText(DocumentFontFace face, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var ranges = face.UnicodeRange.Split(',');
        return text.EnumerateRunes().Any(rune => ranges.Any(range => IncludesCodePoint(range, rune.Value)));
    }

    internal static bool IncludesCodePoint(string range, int codePoint)
    {
        var token = range.Trim();
        if (!token.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = token[2..];
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            return int.TryParse(value[..dash], System.Globalization.NumberStyles.HexNumber, null, out var minimum) &&
                int.TryParse(value[(dash + 1)..], System.Globalization.NumberStyles.HexNumber, null, out var maximum) &&
                codePoint >= minimum && codePoint <= maximum;
        }

        var wildcard = value.IndexOf('?');
        if (wildcard >= 0)
        {
            var prefix = value[..wildcard];
            var wildcards = value.Length - wildcard;
            if (!int.TryParse(prefix, System.Globalization.NumberStyles.HexNumber, null, out var leading))
            {
                return false;
            }

            var minimum = leading << (wildcards * 4);
            var maximum = minimum + (1 << (wildcards * 4)) - 1;
            return codePoint >= minimum && codePoint <= maximum;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var exact) && codePoint == exact;
    }
}

/// <summary>Exposes <c>document.fonts</c>.</summary>
[DomExposed("Document")]
public static class DocumentFontLoadingExtensions
{
    /// <summary>Gets the CSS Font Loading API set for the document.</summary>
    [DomName("fonts")]
    public static DocumentFontFaceSet Fonts(this IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return DocumentFontFaceSet.For(document);
    }
}
