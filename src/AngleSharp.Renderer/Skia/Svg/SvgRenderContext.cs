namespace AngleSharp.Renderer.Skia.Svg;

using AngleSharp.Dom;

/// <summary>
/// Per-rasterization state shared across the whole element walk: an id index (for `url(#id)`
/// paint servers, `clip-path`, `mask` and `use`), the cascade of rules collected from any
/// SVG-internal &lt;style&gt; elements, and the cycle guard for `&lt;use&gt;`.
/// </summary>
internal sealed class SvgRenderContext
{
    public required IReadOnlyDictionary<string, IElement> ElementsById { get; init; }

    public required IReadOnlyList<SvgStyleRule> StyleRules { get; init; }

    /// <summary>
    /// For an inline SVG, the declarations the host page's style sheets give each element (only
    /// the properties in <see cref="SvgPaintState.PresentationProperties"/>); empty otherwise.
    /// </summary>
    public IReadOnlyDictionary<IElement, IReadOnlyDictionary<string, string>> PageDeclarations { get; init; } = NoPageDeclarations;

    private static readonly IReadOnlyDictionary<IElement, IReadOnlyDictionary<string, string>> NoPageDeclarations =
        new Dictionary<IElement, IReadOnlyDictionary<string, string>>(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Elements currently being rendered as the target of a `&lt;use&gt;` reference, so a direct or
    /// indirect self-reference stops instead of recursing forever.
    /// </summary>
    public HashSet<IElement> ActiveUseReferences { get; } = new();

    /// <summary>
    /// &lt;pattern&gt; elements currently being rendered into their own tile, so a pattern whose
    /// content (directly or transitively) references itself again stops instead of recursing
    /// forever.
    /// </summary>
    public HashSet<IElement> ActivePatterns { get; } = new();

    public static SvgRenderContext Build(IElement svgRoot, SvgPageStyle? pageStyle = null)
    {
        var elementsById = new Dictionary<string, IElement>(StringComparer.Ordinal);
        var styleRules = new List<SvgStyleRule>();
        var order = 0;

        void Visit(IElement element)
        {
            var id = element.GetAttribute("id");

            if (!string.IsNullOrWhiteSpace(id))
            {
                elementsById.TryAdd(id, element);
            }

            if (string.Equals(element.LocalName, "style", StringComparison.OrdinalIgnoreCase))
            {
                var css = element.TextContent;

                if (!string.IsNullOrWhiteSpace(css))
                {
                    var parsed = SvgStyleSheetParser.Parse(css, order);
                    styleRules.AddRange(parsed);
                    order += parsed.Count;
                }
            }

            foreach (var child in element.Children)
            {
                Visit(child);
            }
        }

        Visit(svgRoot);

        return new SvgRenderContext
        {
            ElementsById = elementsById,
            StyleRules = styleRules,
            PageDeclarations = pageStyle?.Declarations ?? NoPageDeclarations,
        };
    }
}

/// <summary>
/// The host page's styling of an inline SVG: the inherited values its root starts from (the
/// root's computed style, so `color`, `fill`, fonts and the like flow in from the HTML around it)
/// and the cascaded page declarations of every element inside it.
/// </summary>
internal sealed record SvgPageStyle(
    IReadOnlyDictionary<string, string> Inherited,
    IReadOnlyDictionary<IElement, IReadOnlyDictionary<string, string>> Declarations);
