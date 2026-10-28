namespace AngleSharp.Renderer.Skia.Svg;

using System.Globalization;
using System.Linq;

using AngleSharp.Dom;

using SkiaSharp;

/// <summary>
/// Text alignment relative to the position an SVG &lt;text&gt;/&lt;tspan&gt; was given.
/// </summary>
internal enum SvgTextAnchor
{
    Start,
    Middle,
    End,
}

/// <summary>
/// What a `fill`/`stroke` value resolved to: unpainted, a solid color, or a `url(#id)` reference
/// to a paint server (a gradient) that needs the element's own geometry to finish resolving.
/// </summary>
internal readonly record struct SvgPaintValue(SvgPaintKind Kind, SKColor Color, string? ReferenceId)
{
    public static SvgPaintValue None { get; } = new(SvgPaintKind.None, SKColors.Transparent, null);

    public static SvgPaintValue FromColor(SKColor color) => new(SvgPaintKind.Color, color, null);

    public static SvgPaintValue FromReference(string id) => new(SvgPaintKind.Reference, SKColors.Transparent, id);

    public static SvgPaintValue Parse(string raw, SvgPaintValue fallback, SKColor currentColor)
    {
        var trimmed = raw.Trim();

        if (SvgUrlReference.TryExtract(trimmed, out var id))
        {
            return FromReference(id);
        }

        if (trimmed.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return FromColor(currentColor);
        }

        return SvgColorParsing.TryParsePaint(trimmed, out var color) ? FromColor(color) : None;
    }
}

internal enum SvgPaintKind
{
    None,
    Color,
    Reference,
}

/// <summary>
/// The inherited paint and font state while walking an SVG element tree. Presentation attributes
/// inherit down the tree by default; SVG-internal &lt;style&gt; rules and (for an inline SVG) the
/// host page's style sheets override them, and an inline `style=""` declaration on the element
/// overrides all of them, matching the CSS cascade.
/// </summary>
internal readonly record struct SvgPaintState(
    SvgPaintValue Fill,
    float FillOpacity,
    SvgPaintValue Stroke,
    float StrokeOpacity,
    float StrokeWidth,
    float Opacity,
    SKPathFillType FillRule,
    string FontFamily,
    float FontSize,
    float FontWeight,
    bool IsItalic,
    SvgTextAnchor TextAnchor,
    SKColor CurrentColor,
    bool IsRightToLeft = false,
    string? Language = null)
{
    public static SvgPaintState Initial { get; } = new(
        Fill: SvgPaintValue.FromColor(SKColors.Black),
        FillOpacity: 1f,
        Stroke: SvgPaintValue.None,
        StrokeOpacity: 1f,
        StrokeWidth: 1f,
        Opacity: 1f,
        FillRule: SKPathFillType.Winding,
        FontFamily: "sans-serif",
        FontSize: 16f,
        FontWeight: 400f,
        IsItalic: false,
        TextAnchor: SvgTextAnchor.Start,
        CurrentColor: SKColors.Black);

    /// <summary>
    /// Resolves the paint state for <paramref name="element"/>, inheriting from this instance and
    /// layering in, from weakest to strongest: presentation attributes, matched
    /// SVG-internal &lt;style&gt; rules, then an inline `style` attribute.
    /// </summary>
    public SvgPaintState Resolve(IElement element, SvgRenderContext context, SvgViewport viewport) =>
        ResolveDeclarations(ReadDeclarations(element, context), viewport) with { Language = ResolveLanguage(element, Language) };

    /// <summary>
    /// The language of the element's text (shaping takes it into account): its own <c>lang</c>
    /// or <c>xml:lang</c>, else the inherited one, else that of the nearest ancestor that has one,
    /// which reaches into the host page for an inline SVG.
    /// </summary>
    private static string? ResolveLanguage(IElement element, string? inherited)
    {
        for (var current = element; current is not null; current = current.ParentElement)
        {
            var language = current.GetAttribute("lang") ?? current.GetAttribute("xml:lang");

            if (!string.IsNullOrWhiteSpace(language))
            {
                return language.Trim();
            }

            if (inherited is not null)
            {
                return inherited;
            }
        }

        return null;
    }

    /// <summary>
    /// Layers <paramref name="declarations"/> (property name to value) over this inherited state -
    /// also how the root picks up the inherited style of the HTML around an inline SVG.
    /// </summary>
    public SvgPaintState ResolveDeclarations(IReadOnlyDictionary<string, string> declarations, SvgViewport viewport)
    {
        // `color` is resolved first so `currentColor` on this same element's fill/stroke picks up
        // its own computed value, not the inherited one, matching the CSS `currentColor` keyword.
        var currentColor = CurrentColor;

        if (TryGetDeclaration(declarations, "color", out var colorValue) && SvgColorParsing.TryParsePaint(colorValue, out var parsedColor))
        {
            currentColor = parsedColor;
        }

        var fill = TryGetDeclaration(declarations, "fill", out var fillValue) ? SvgPaintValue.Parse(fillValue, Fill, currentColor) : Fill;
        var stroke = TryGetDeclaration(declarations, "stroke", out var strokeValue) ? SvgPaintValue.Parse(strokeValue, Stroke, currentColor) : Stroke;

        var fillOpacity = FillOpacity;

        if (TryGetDeclaration(declarations, "fill-opacity", out var fillOpacityValue) &&
            TryParseOpacity(fillOpacityValue, out var parsedFillOpacity))
        {
            fillOpacity = parsedFillOpacity;
        }

        var strokeOpacity = StrokeOpacity;

        if (TryGetDeclaration(declarations, "stroke-opacity", out var strokeOpacityValue) &&
            TryParseOpacity(strokeOpacityValue, out var parsedStrokeOpacity))
        {
            strokeOpacity = parsedStrokeOpacity;
        }

        var strokeWidth = StrokeWidth;

        if (TryGetDeclaration(declarations, "stroke-width", out var strokeWidthValue))
        {
            strokeWidth = SvgLength.Parse(strokeWidthValue, viewport.DiagonalReference, strokeWidth);
        }

        // Element opacity composites with a translucent group in a real SVG renderer (via an
        // isolated layer); multiplying it directly into fill/stroke alpha is a simplification
        // that is exact for opaque descendants and close enough for the common icon/logo case.
        var opacity = Opacity;

        if (TryGetDeclaration(declarations, "opacity", out var opacityValue) &&
            TryParseOpacity(opacityValue, out var parsedOpacity))
        {
            opacity *= parsedOpacity;
        }

        var fillRule = FillRule;

        if (TryGetDeclaration(declarations, "fill-rule", out var fillRuleValue))
        {
            fillRule = string.Equals(fillRuleValue.Trim(), "evenodd", StringComparison.OrdinalIgnoreCase)
                ? SKPathFillType.EvenOdd
                : SKPathFillType.Winding;
        }

        var fontFamily = TryGetDeclaration(declarations, "font-family", out var fontFamilyValue) ? fontFamilyValue : FontFamily;

        var fontSize = FontSize;

        if (TryGetDeclaration(declarations, "font-size", out var fontSizeValue))
        {
            fontSize = ParseFontSize(fontSizeValue, FontSize);
        }

        var fontWeight = FontWeight;

        if (TryGetDeclaration(declarations, "font-weight", out var fontWeightValue))
        {
            fontWeight = ParseFontWeight(fontWeightValue, FontWeight);
        }

        var isItalic = IsItalic;

        if (TryGetDeclaration(declarations, "font-style", out var fontStyleValue))
        {
            var normalized = fontStyleValue.Trim();
            isItalic = normalized.Equals("italic", StringComparison.OrdinalIgnoreCase) || normalized.Equals("oblique", StringComparison.OrdinalIgnoreCase);
        }

        var textAnchor = TextAnchor;

        if (TryGetDeclaration(declarations, "text-anchor", out var textAnchorValue))
        {
            textAnchor = textAnchorValue.Trim().ToLowerInvariant() switch
            {
                "middle" => SvgTextAnchor.Middle,
                "end" => SvgTextAnchor.End,
                _ => SvgTextAnchor.Start,
            };
        }

        // `direction` sets the paragraph direction of text (and what `start`/`end` anchors mean)
        var isRightToLeft = TryGetDeclaration(declarations, "direction", out var directionValue)
            ? string.Equals(directionValue.Trim(), "rtl", StringComparison.OrdinalIgnoreCase)
            : IsRightToLeft;

        return new SvgPaintState(fill, fillOpacity, stroke, strokeOpacity, strokeWidth, opacity, fillRule, fontFamily, fontSize, fontWeight, isItalic, textAnchor, currentColor, isRightToLeft, Language);
    }

    public SKPaint? CreateFillPaint(SKRect bounds, SvgRenderContext context, SvgViewport viewport) =>
        CreatePaint(Fill, FillOpacity, SKPaintStyle.Fill, bounds, context, viewport);

    public SKPaint? CreateStrokePaint(SKRect bounds, SvgRenderContext context, SvgViewport viewport)
    {
        if (StrokeWidth <= 0f)
        {
            return null;
        }

        var paint = CreatePaint(Stroke, StrokeOpacity, SKPaintStyle.Stroke, bounds, context, viewport);

        if (paint is not null)
        {
            paint.StrokeWidth = StrokeWidth;
        }

        return paint;
    }

    private SKPaint? CreatePaint(SvgPaintValue paintValue, float paintOpacity, SKPaintStyle style, SKRect bounds, SvgRenderContext context, SvgViewport viewport)
    {
        var alpha = Math.Clamp(paintOpacity * Opacity, 0f, 1f);

        switch (paintValue.Kind)
        {
            case SvgPaintKind.None:
                return null;

            case SvgPaintKind.Color:
                return new SKPaint
                {
                    Style = style,
                    IsAntialias = true,
                    Color = paintValue.Color.WithAlpha((byte)Math.Round(paintValue.Color.Alpha * alpha)),
                };

            case SvgPaintKind.Reference when paintValue.ReferenceId is { } id && context.ElementsById.TryGetValue(id, out var paintServer):
                var shader = string.Equals(paintServer.LocalName, "pattern", StringComparison.OrdinalIgnoreCase)
                    ? SvgPatternBuilder.Build(paintServer, bounds, context, viewport)
                    : SvgGradientBuilder.Build(paintServer, bounds, context.ElementsById);

                if (shader is null)
                {
                    return null;
                }

                // A shader ignores the paint's RGB, but its alpha still modulates every sample the
                // shader produces - that is how fill-opacity/opacity reach a gradient or pattern fill.
                return new SKPaint
                {
                    Style = style,
                    IsAntialias = true,
                    Shader = shader,
                    Color = SKColors.White.WithAlpha((byte)Math.Round(255 * alpha)),
                };

            default:
                // A url() reference to a nonexistent or unsupported paint server paints nothing,
                // per the SVG spec, rather than falling back to some default color.
                return null;
        }
    }

    private static float ParseFontSize(string value, float inherited)
    {
        var trimmed = value.Trim();

        if (trimmed.EndsWith('%') && float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return inherited * percent / 100f;
        }

        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2];
        }

        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0f ? number : inherited;
    }

    private static float ParseFontWeight(string value, float inherited)
    {
        var trimmed = value.Trim();

        return trimmed.ToLowerInvariant() switch
        {
            "normal" => 400f,
            "bold" => 700f,
            "bolder" => Math.Min(900f, inherited + 300f),
            "lighter" => Math.Max(100f, inherited - 300f),
            _ when float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) => numeric,
            _ => inherited,
        };
    }

    private static bool TryGetDeclaration(IReadOnlyDictionary<string, string> declarations, string name, out string value)
    {
        if (declarations.TryGetValue(name, out var found) && !string.IsNullOrWhiteSpace(found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryParseOpacity(string value, out float opacity)
    {
        var trimmed = value.Trim();

        if (trimmed.EndsWith('%') && float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            opacity = Math.Clamp(percent / 100f, 0f, 1f);
            return true;
        }

        if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
        {
            opacity = Math.Clamp(raw, 0f, 1f);
            return true;
        }

        opacity = 1f;
        return false;
    }

    /// <summary>The properties this renderer reads, from presentation attributes and CSS alike.</summary>
    public static readonly string[] PresentationProperties =
    [
        "fill", "stroke", "fill-opacity", "stroke-opacity", "stroke-width", "opacity", "fill-rule",
        "font-family", "font-size", "font-weight", "font-style", "text-anchor", "color", "direction",
    ];

    private static Dictionary<string, string> ReadDeclarations(IElement element, SvgRenderContext context)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Presentation attributes - weakest precedence.
        foreach (var property in PresentationProperties)
        {
            AddIfPresent(declarations, property, element.GetAttribute(property));
        }

        // 2. SVG-internal <style> rules that match this element, weakest-specificity-and-earliest
        // first so a later or more specific rule overwrites an earlier, weaker one.
        foreach (var rule in context.StyleRules
                     .Where(rule => rule.Selector.Matches(element))
                     .OrderBy(rule => rule.Specificity)
                     .ThenBy(rule => rule.Order))
        {
            foreach (var (property, value) in rule.Declarations)
            {
                declarations[property] = value;
            }
        }

        // 3. The host page's own style sheets, for an inline SVG (author rules; approximated as
        // applying after the SVG-internal ones instead of interleaving by specificity).
        if (context.PageDeclarations.TryGetValue(element, out var pageDeclarations))
        {
            foreach (var (property, value) in pageDeclarations)
            {
                declarations[property] = value;
            }
        }

        // 4. Inline style overrides everything else, matching the CSS-over-attribute cascade.
        var style = element.GetAttribute("style");

        if (!string.IsNullOrWhiteSpace(style))
        {
            foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
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
        }

        return declarations;
    }

    private static void AddIfPresent(Dictionary<string, string> declarations, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            declarations[name] = value;
        }
    }
}
