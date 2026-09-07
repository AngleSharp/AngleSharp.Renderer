namespace AngleSharp.Renderer.Skia.Svg;

/// <summary>
/// A single parsed rule from an SVG-internal &lt;style&gt; element: one selector, its
/// declarations, and enough ordering/specificity information to resolve the cascade.
/// </summary>
internal sealed record SvgStyleRule(SvgSelector Selector, IReadOnlyDictionary<string, string> Declarations, int Specificity, int Order);

/// <summary>
/// Parses the plain-CSS body of an SVG &lt;style&gt; element into <see cref="SvgStyleRule"/>s.
/// This is a rule/selector-list/declaration-block tokenizer, not a full CSS parser: `/* ... */`
/// comments are stripped and top-level `@`-rules (`@media`, `@import`, `@keyframes`, ...) are
/// skipped wholesale - a conditional block like `@media` cannot be evaluated (there is no viewport
/// or user-agent context to test against), so its rules never apply rather than always applying.
/// </summary>
internal static class SvgStyleSheetParser
{
    public static List<SvgStyleRule> Parse(string cssText, int startOrder)
    {
        var rules = new List<SvgStyleRule>();
        var order = startOrder;
        var index = 0;
        cssText = StripComments(cssText);

        while (index < cssText.Length)
        {
            while (index < cssText.Length && char.IsWhiteSpace(cssText[index]))
            {
                index++;
            }

            if (index < cssText.Length && cssText[index] == '@')
            {
                index = SkipAtRule(cssText, index);
                continue;
            }

            var openBrace = cssText.IndexOf('{', index);

            if (openBrace < 0)
            {
                break;
            }

            var closeBrace = cssText.IndexOf('}', openBrace);

            if (closeBrace < 0)
            {
                break;
            }

            var selectorListText = cssText[index..openBrace].Trim();
            var bodyText = cssText[(openBrace + 1)..closeBrace];
            var declarations = ParseDeclarations(bodyText);

            if (declarations.Count > 0)
            {
                foreach (var selectorText in selectorListText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (SvgSelector.TryParse(selectorText, out var selector) && selector is not null)
                    {
                        rules.Add(new SvgStyleRule(selector, declarations, selector.Specificity, order++));
                    }
                }
            }

            index = closeBrace + 1;
        }

        return rules;
    }

    private static Dictionary<string, string> ParseDeclarations(string body)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIndex = declaration.IndexOf(':');

            if (colonIndex <= 0)
            {
                continue;
            }

            var property = declaration[..colonIndex].Trim();
            var value = declaration[(colonIndex + 1)..].Trim();

            if (property.Length > 0 && value.Length > 0)
            {
                declarations[property] = value;
            }
        }

        return declarations;
    }

    private static string StripComments(string css)
    {
        while (true)
        {
            var start = css.IndexOf("/*", StringComparison.Ordinal);

            if (start < 0)
            {
                return css;
            }

            var end = css.IndexOf("*/", start + 2, StringComparison.Ordinal);
            css = end < 0 ? css[..start] : string.Concat(css.AsSpan(0, start), css.AsSpan(end + 2));
        }
    }

    /// <summary>
    /// Skips one `@`-rule starting at <paramref name="index"/>: either a statement ending in `;`
    /// (`@import "...";`) or a block whose braces may nest (`@media { ... { ... } ... }`).
    /// </summary>
    private static int SkipAtRule(string css, int index)
    {
        var i = index;

        while (i < css.Length && css[i] != ';' && css[i] != '{')
        {
            i++;
        }

        if (i >= css.Length || css[i] == ';')
        {
            return Math.Min(i + 1, css.Length);
        }

        var depth = 0;

        while (i < css.Length)
        {
            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return i + 1;
                }
            }

            i++;
        }

        return css.Length;
    }
}
