namespace AngleSharp.Renderer;

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Turns the <c>@font-face</c> rules of a document into a <see cref="FontFaceSet"/>.
/// </summary>
internal static class FontFaceLoader
{
    // Skia reads raw TrueType/OpenType only. The compressed web wrappers would need a decoder
    // this library does not carry, so those sources are skipped and the next one is tried.
    private static readonly HashSet<string> UnsupportedFormats =
        new(StringComparer.OrdinalIgnoreCase) { "woff", "woff2", "svg", "embedded-opentype" };

    public static FontFaceSet Load(IDocument document)
    {
        var faces = new List<FontFace>();

        foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
        {
            foreach (var rule in sheet.Rules.OfType<ICssFontFaceRule>())
            {
                if (TryCreateFace(document, rule, out var face))
                {
                    faces.Add(face);
                }
            }
        }

        return faces.Count == 0 ? FontFaceSet.Empty : new FontFaceSet(faces);
    }

    private static bool TryCreateFace(IDocument document, ICssFontFaceRule rule, out FontFace face)
    {
        face = null!;

        var family = Unquote(rule.Family);

        if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(rule.Source))
        {
            return false;
        }

        var weight = ParseWeight(rule.Weight);
        var isItalic = IsItalic(rule.Style);
        var sources = new List<FontFaceSource>();

        // The declaration order is preserved. Whether a local() source exists is a property of the
        // machine, so that decision belongs to the backend rather than to this loader.
        foreach (var source in SplitSources(rule.Source))
        {
            if (TryParseLocal(source, out var localFamily))
            {
                sources.Add(FontFaceSource.FromLocal(localFamily));
            }
            else if (TryParseUrl(source, out var url, out var format))
            {
                if (format is not null && UnsupportedFormats.Contains(format))
                {
                    continue;
                }

                if (TryLoadFontData(document, url, out var data))
                {
                    sources.Add(FontFaceSource.FromData(data));
                }
            }
        }

        if (sources.Count == 0)
        {
            return false;
        }

        face = new FontFace(family, weight, isItalic, sources);
        return true;
    }

    private static bool TryLoadFontData(IDocument document, string url, out byte[] data)
    {
        data = [];

        if (TryDecodeDataUri(url, out var inlineData))
        {
            data = inlineData;
            return IsSupportedFontData(data);
        }

        // Matching how images are handled, nothing is fetched unless the browsing context was
        // configured with a loader. A renderer should not silently reach out to the network.
        var loader = document.Context.GetService<IDocumentLoader>();

        if (loader is null)
        {
            return false;
        }

        try
        {
            var target = new Url(document.BaseUrl, url);
            var download = loader.FetchAsync(DocumentRequest.Get(target, source: document, referer: document.BaseUri));
            var response = download.Task.GetAwaiter().GetResult();

            if (response?.Content is null)
            {
                return false;
            }

            using var content = response.Content;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            data = buffer.ToArray();

            return IsSupportedFontData(data);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Rejects the compressed wrappers up front, so a face that could never be rasterized does not
    /// shadow the next source or the next family in the fallback list.
    /// </summary>
    private static bool IsSupportedFontData(byte[] data)
    {
        if (data.Length < 4)
        {
            return false;
        }

        var tag = Encoding.ASCII.GetString(data, 0, 4);

        return tag is not ("wOFF" or "wOF2");
    }

    private static IEnumerable<string> SplitSources(string source)
    {
        var depth = 0;
        var quote = '\0';
        var start = 0;

        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];

            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }
            }
            else if (current is '\'' or '"')
            {
                quote = current;
            }
            else if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
            }
            else if (current == ',' && depth == 0)
            {
                yield return source[start..index];
                start = index + 1;
            }
        }

        if (start < source.Length)
        {
            yield return source[start..];
        }
    }

    private static bool TryParseLocal(string source, out string localFamily)
    {
        localFamily = string.Empty;

        var value = ExtractFunctionArgument(source.Trim(), "local");

        if (value is null)
        {
            return false;
        }

        localFamily = Unquote(value);
        return localFamily.Length > 0;
    }

    private static bool TryParseUrl(string source, out string url, out string? format)
    {
        url = string.Empty;
        format = null;

        var trimmed = source.Trim();
        var value = ExtractFunctionArgument(trimmed, "url");

        if (value is null)
        {
            return false;
        }

        url = Unquote(value);

        var formatIndex = trimmed.IndexOf("format", StringComparison.OrdinalIgnoreCase);

        if (formatIndex >= 0)
        {
            var formatValue = ExtractFunctionArgument(trimmed[formatIndex..], "format");

            if (formatValue is not null)
            {
                format = Unquote(formatValue);
            }
        }

        return url.Length > 0;
    }

    private static string? ExtractFunctionArgument(string source, string functionName)
    {
        if (!source.StartsWith(functionName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var open = source.IndexOf('(', functionName.Length);

        if (open < 0 || source[functionName.Length..open].Trim().Length > 0)
        {
            return null;
        }

        var close = source.IndexOf(')', open);

        return close < 0 ? null : source[(open + 1)..close].Trim();
    }

    private static bool TryDecodeDataUri(string url, out byte[] data)
    {
        data = [];

        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = url.IndexOf(',');

        if (separator < 0)
        {
            return false;
        }

        var metadata = url[5..separator];
        var payload = url[(separator + 1)..];

        try
        {
            data = metadata.Contains("base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload.Trim())
                : Encoding.ASCII.GetBytes(Uri.UnescapeDataString(payload));

            return data.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static float ParseWeight(string? weight)
    {
        if (string.IsNullOrWhiteSpace(weight))
        {
            return 400f;
        }

        var trimmed = weight.Trim();

        if (string.Equals(trimmed, "bold", StringComparison.OrdinalIgnoreCase))
        {
            return 700f;
        }

        if (string.Equals(trimmed, "normal", StringComparison.OrdinalIgnoreCase))
        {
            return 400f;
        }

        // A weight range such as "400 700" contributes its lower bound.
        var first = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return float.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 400f;
    }

    private static bool IsItalic(string? style) =>
        style is not null &&
        (style.Contains("italic", StringComparison.OrdinalIgnoreCase) ||
         style.Contains("oblique", StringComparison.OrdinalIgnoreCase));

    private static string Unquote(string? value) =>
        value is null ? string.Empty : value.Trim().Trim('\'', '"').Trim();
}
