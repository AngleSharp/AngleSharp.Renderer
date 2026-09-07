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

    public static SvgRenderContext Build(IElement svgRoot)
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
        };
    }
}
