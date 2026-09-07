namespace AngleSharp.Renderer.Skia.Svg;

using AngleSharp.Dom;

/// <summary>
/// A minimal CSS selector matcher for SVG-internal &lt;style&gt; rules. Supports type, `.class`,
/// `#id` and `*` in compound selectors, joined by the descendant combinator (whitespace). Child
/// (`&gt;`), sibling (`+`/`~`) combinators, attribute selectors and pseudo-classes are not
/// supported - a rule using them fails to parse and is skipped.
/// </summary>
internal sealed class SvgSelector
{
    private readonly IReadOnlyList<CompoundSelector> _parts;

    private SvgSelector(IReadOnlyList<CompoundSelector> parts, int specificity)
    {
        _parts = parts;
        Specificity = specificity;
    }

    /// <summary>
    /// A simplified (id-count, class-count, type-count) specificity encoded as a single
    /// comparable integer, matching standard CSS cascade ordering closely enough for the common
    /// case this renderer targets.
    /// </summary>
    public int Specificity { get; }

    public static bool TryParse(string text, out SvgSelector? selector)
    {
        selector = null;
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return false;
        }

        var parts = new List<CompoundSelector>(tokens.Length);
        var idCount = 0;
        var classCount = 0;
        var typeCount = 0;

        foreach (var token in tokens)
        {
            if (token.Contains('>') || token.Contains('+') || token.Contains('~') ||
                token.Contains('[') || token.Contains(':'))
            {
                // Unsupported combinator/selector kind - skip the whole rule rather than guess.
                return false;
            }

            if (!CompoundSelector.TryParse(token, out var compound))
            {
                return false;
            }

            parts.Add(compound);
            idCount += compound.Id is not null ? 1 : 0;
            classCount += compound.Classes.Count;
            typeCount += compound.Type is not null and not "*" ? 1 : 0;
        }

        var specificity = (idCount * 10_000) + (classCount * 100) + typeCount;
        selector = new SvgSelector(parts, specificity);
        return true;
    }

    public bool Matches(IElement element)
    {
        if (!_parts[^1].Matches(element))
        {
            return false;
        }

        var partIndex = _parts.Count - 2;
        var current = element.ParentElement;

        while (partIndex >= 0)
        {
            if (current is null)
            {
                return false;
            }

            if (_parts[partIndex].Matches(current))
            {
                partIndex--;
            }

            current = current.ParentElement;
        }

        return true;
    }

    private readonly record struct CompoundSelector(string? Type, string? Id, IReadOnlyList<string> Classes)
    {
        public static bool TryParse(string token, out CompoundSelector compound)
        {
            string? type = null;
            string? id = null;
            var classes = new List<string>();

            var i = 0;

            if (i < token.Length && token[i] != '.' && token[i] != '#')
            {
                var start = i;

                while (i < token.Length && token[i] != '.' && token[i] != '#')
                {
                    i++;
                }

                type = token[start..i];
            }

            while (i < token.Length)
            {
                var marker = token[i];
                var start = ++i;

                while (i < token.Length && token[i] != '.' && token[i] != '#')
                {
                    i++;
                }

                var name = token[start..i];

                if (name.Length == 0)
                {
                    compound = default;
                    return false;
                }

                if (marker == '.')
                {
                    classes.Add(name);
                }
                else
                {
                    id = name;
                }
            }

            if (type is { Length: 0 } && id is null && classes.Count == 0)
            {
                compound = default;
                return false;
            }

            compound = new CompoundSelector(type is { Length: 0 } ? null : type, id, classes);
            return true;
        }

        public bool Matches(IElement element)
        {
            if (Type is not null and not "*" && !string.Equals(element.LocalName, Type, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Id is not null && !string.Equals(element.GetAttribute("id"), Id, StringComparison.Ordinal))
            {
                return false;
            }

            if (Classes.Count > 0)
            {
                var classAttribute = element.GetAttribute("class");

                if (string.IsNullOrWhiteSpace(classAttribute))
                {
                    return false;
                }

                var elementClasses = new HashSet<string>(classAttribute.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

                foreach (var required in Classes)
                {
                    if (!elementClasses.Contains(required))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
