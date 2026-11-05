namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Css.Dom;
using AngleSharp.Dom.Geometry;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;
using System.Linq;

/// <summary>
/// Provides CSSOM View-style helpers for documents.
/// </summary>
[DomExposed("Document")]
public static class DocumentCssomViewExtensions
{
    /// <summary>
    /// Returns the topmost element at the given viewport coordinates.
    /// </summary>
    [DomName("elementFromPoint")]
    public static IElement? ElementFromPoint(this IDocument document, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(document);
        return HitTesting.FindTopMostElementAt(GetViewportMetricsMap(document), x, y);
    }

    /// <summary>
    /// Returns the elements at the given viewport coordinates, the topmost first: what was hit,
    /// followed by the elements around it that contain the point.
    /// </summary>
    [DomName("elementsFromPoint")]
    public static IElement[] ElementsFromPoint(this IDocument document, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(document);
        var metrics = GetViewportMetricsMap(document);
        var result = new System.Collections.Generic.List<IElement>();

        for (var element = HitTesting.FindTopMostElementAt(metrics, x, y); element is not null; element = element.ParentElement)
        {
            if (element.ParentElement is null || (metrics.TryGetValue(element, out var box) && x >= box.BorderBoxX && y >= box.BorderBoxY && x < box.BorderBoxX + box.BorderBoxWidth && y < box.BorderBoxY + box.BorderBoxHeight))
            {
                result.Add(element);
            }
        }

        return result.ToArray();
    }

    private static System.Collections.Generic.IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> GetViewportMetricsMap(IDocument document)
    {
        var harness = document.Context.GetDomHarness();

        // the point is in the viewport, so are the boxes it is looked up in
        return harness is InteractiveHtmlRendererState state
            ? document.DocumentElement is { } root
                ? state.GetLayoutSnapshot(document).ToViewport(state.GetScrollLeft(root, double.MaxValue), state.GetScrollTop(root, double.MaxValue))
                : state.GetLayoutMetrics(document)
            : HtmlRenderer.CaptureLayoutMetrics(document, harness.RenderDevice);
    }

    /// <summary>
    /// Returns a caret position for the given viewport coordinates.
    /// </summary>
    [DomName("caretPositionFromPoint")]
    public static ICaretPosition? CaretPositionFromPoint(this IDocument document, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(document);

        var harness = document.Context.GetDomHarness();
        // the point is in the viewport, so are the boxes it is looked up in
        var metricsMap = harness is InteractiveHtmlRendererState state
            ? document.DocumentElement is { } root
                ? state.GetLayoutSnapshot(document).ToViewport(state.GetScrollLeft(root, double.MaxValue), state.GetScrollTop(root, double.MaxValue))
                : state.GetLayoutMetrics(document)
            : HtmlRenderer.CaptureLayoutMetrics(document, harness.RenderDevice);
        var targetElement = HitTesting.FindTopMostElementAt(metricsMap, x, y);

        if (targetElement is null)
        {
            return null;
        }

        if (!metricsMap.TryGetValue(targetElement, out var metrics))
        {
            return null;
        }

        var textNode = GetFirstTextNode(targetElement);
        if (textNode is null)
        {
            var rect = new DomRect(metrics.BorderBoxX, metrics.BorderBoxY, 0d, metrics.BorderBoxHeight);
            return new CaretPosition(targetElement, 0, rect);
        }

        var style = (textNode.ParentElement ?? targetElement).ComputeCurrentStyle();
        var fontSize = ParseLengthOrDefault(style?.GetPropertyValue("font-size"), (float)harness.RenderDevice.FontSize);
        var font = CreateFont(style, document, fontSize);
        var measurer = new SkiaTextMeasurer();
        var fontMetrics = (IFontMetricsMeasurer)measurer;
        var lineHeight = ResolveLineHeight(style?.GetPropertyValue("line-height"), fontSize, fontMetrics.MeasureLineSpacing(font));
        var caretHeight = fontMetrics.MeasureContentHeight(font);
        var contentLeft = metrics.BorderBoxX + metrics.BorderLeft + metrics.PaddingLeft;
        var contentTop = metrics.BorderBoxY + metrics.BorderTop + metrics.PaddingTop;
        var contentWidth = Math.Max(0f, metrics.BorderBoxWidth - metrics.BorderLeft - metrics.BorderRight - metrics.PaddingLeft - metrics.PaddingRight);
        var whiteSpace = style?.GetPropertyValue("white-space")?.Trim().ToLowerInvariant() ?? string.Empty;

        // the line below the point, or the nearest one
        var lines = BreakIntoLines(textNode.Data ?? string.Empty, whiteSpace, contentWidth, font, measurer);
        var lineIndex = Math.Clamp((int)Math.Floor((y - contentTop) / lineHeight), 0, lines.Count - 1);
        var (lineText, rawOffsets) = lines[lineIndex];

        var lineLeft = contentLeft + (style?.GetPropertyValue("text-align")?.Trim().ToLowerInvariant() switch
        {
            "center" => (contentWidth - measurer.MeasureWidth(lineText, font)) / 2f,
            "right" or "end" => contentWidth - measurer.MeasureWidth(lineText, font),
            _ => 0f,
        });

        // the boundary between two characters that is nearest to the point
        var caretIndex = 0;
        var caretOffset = 0f;
        var nearest = double.MaxValue;

        for (var index = 0; index <= lineText.Length; index++)
        {
            if (index < lineText.Length && char.IsLowSurrogate(lineText[index]))
            {
                continue;
            }

            var width = index == 0 ? 0f : measurer.MeasureWidth(lineText[..index], font);
            var distance = Math.Abs(lineLeft + width - x);

            if (distance < nearest)
            {
                nearest = distance;
                caretIndex = index;
                caretOffset = width;
            }
        }

        var offset = rawOffsets[caretIndex];
        var caretTop = contentTop + (lineIndex * lineHeight) + MathF.Floor((lineHeight - caretHeight) / 2f);
        var rectAtCaret = new DomRect(lineLeft + caretOffset, caretTop, 0d, caretHeight);

        return new CaretPosition(textNode, offset, rectAtCaret);
    }

    private static IText? GetFirstTextNode(IElement element)
    {
        foreach (var child in element.ChildNodes)
        {
            if (child is IText text && !string.IsNullOrEmpty(text.Data))
            {
                return text;
            }

            if (child is IElement childElement)
            {
                var nested = GetFirstTextNode(childElement);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }


    internal static float ParseLengthOrDefault(string? value, float fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim();

        if (normalized.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^2].Trim();
        }

        if (float.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(1f, parsed);
        }

        return fallback;
    }

    internal static RenderFont CreateFont(ICssStyleDeclaration? style, IDocument document, float fontSize)
    {
        var family = style?.GetPropertyValue("font-family");
        var weight = style?.GetPropertyValue("font-weight")?.Trim().ToLowerInvariant() switch
        {
            null or "" or "normal" => 400f,
            "bold" or "bolder" => 700f,
            "lighter" => 300f,
            var value => float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 400f,
        };
        var fontStyle = style?.GetPropertyValue("font-style")?.Trim().ToLowerInvariant();

        return new RenderFont(
            string.IsNullOrWhiteSpace(family) ? "sans-serif" : family,
            fontSize,
            weight,
            fontStyle is "italic" or "oblique",
            ParseLengthOrDefault(style?.GetPropertyValue("letter-spacing"), 0f),
            FontFaceLoader.Load(document));
    }

    /// <summary>
    /// The height of a line in pixels: `normal` is what the font states, a number a multiple of
    /// the font size, and a length (the computed value of a percentage, too) is that length.
    /// </summary>
    private static float ResolveLineHeight(string? value, float fontSize, float normal)
    {
        var normalized = value?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(normalized) || normalized == "normal")
        {
            return normal;
        }

        if (normalized.EndsWith('%') &&
            float.TryParse(normalized[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            return Math.Max(1f, fontSize * percent / 100f);
        }

        if (normalized.EndsWith("px", StringComparison.Ordinal))
        {
            return Math.Max(1f, ParseLengthOrDefault(normalized, normal));
        }

        return float.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var factor)
            ? Math.Max(1f, fontSize * factor)
            : normal;
    }

    /// <summary>
    /// The text of a node as its lines show it: white space collapsed (unless preserved) and broken
    /// where the words don't fit anymore, with the offset in the node of each of its characters
    /// (and of the end of the line). Never empty.
    /// </summary>
    private static List<(string Text, int[] RawOffsets)> BreakIntoLines(string data, string whiteSpace, float width, RenderFont font, SkiaTextMeasurer measurer)
    {
        var preservesSpaces = whiteSpace is "pre" or "pre-wrap" or "break-spaces";
        var preservesBreaks = preservesSpaces || whiteSpace == "pre-line";
        var wraps = whiteSpace is not ("pre" or "nowrap");
        var lines = new List<(string, int[])>();
        var text = new System.Text.StringBuilder();
        var offsets = new List<int>();
        var lastBreak = -1;

        void Finish(int end)
        {
            offsets.Add(end);
            lines.Add((text.ToString(), offsets.ToArray()));
            text.Clear();
            offsets.Clear();
            lastBreak = -1;
        }

        for (var index = 0; index < data.Length; index++)
        {
            var character = data[index];
            var isSpace = character is ' ' or '\t' or '\n' or '\r' or '\f';

            if (isSpace && preservesBreaks && character == '\n')
            {
                Finish(index);
                continue;
            }

            if (isSpace && !preservesSpaces)
            {
                // a run collapses into its first space, none at the start of a line
                if (text.Length == 0 || text[^1] == ' ')
                {
                    continue;
                }

                character = ' ';
            }

            if (isSpace)
            {
                lastBreak = text.Length;
            }

            text.Append(character);
            offsets.Add(index);

            if (wraps && !isSpace && lastBreak > 0 && width > 0f && measurer.MeasureWidth(text.ToString(), font) > width)
            {
                // the word moves to the next line; the space before it stays on this one
                var word = text.ToString(lastBreak + 1, text.Length - lastBreak - 1);
                var wordOffsets = offsets.GetRange(lastBreak + 1, offsets.Count - lastBreak - 1);
                text.Length = lastBreak + 1;
                offsets.RemoveRange(lastBreak + 1, wordOffsets.Count);
                Finish(wordOffsets[0]);
                text.Append(word);
                offsets.AddRange(wordOffsets);
            }
        }

        Finish(data.Length);
        return lines;
    }
}

