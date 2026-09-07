namespace AngleSharp.Dom;

using AngleSharp.Attributes;
using AngleSharp.Dom.Geometry;
using AngleSharp.Renderer;
using System.Linq;

/// <summary>
/// Provides CSSOM View-style helpers for documents.
/// </summary>
public static class DocumentCssomViewExtensions
{
    /// <summary>
    /// Returns a caret position for the given viewport coordinates.
    /// </summary>
    [DomName("caretPositionFromPoint")]
    public static ICaretPosition? CaretPositionFromPoint(this IDocument document, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(document);

        var harness = document.Context.GetDomHarness();
        var metricsMap = HtmlRenderer.CaptureLayoutMetrics(document, harness.RenderDevice);
        var targetElement = FindTopMostElementAt(metricsMap, x, y);

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

        var style = targetElement.ComputeCurrentStyle();
        var fontSize = ParseLengthOrDefault(style?.GetPropertyValue("font-size"), (float)harness.RenderDevice.FontSize);
        var lineHeightFactor = ParseLineHeightFactor(style?.GetPropertyValue("line-height"), 1.35f);
        var caretHeight = Math.Max(1d, fontSize * lineHeightFactor);
        var contentLeft = metrics.BorderBoxX + metrics.BorderLeft + metrics.PaddingLeft;
        var contentTop = metrics.BorderBoxY + metrics.BorderTop + metrics.PaddingTop;

        var text = textNode.Data ?? string.Empty;
        var averageCharWidth = Math.Max(1d, fontSize * 0.55d);
        var maxX = contentLeft + (text.Length * averageCharWidth);
        var clampedX = Math.Max(contentLeft, Math.Min(x, maxX));
        var offset = (int)Math.Round((clampedX - contentLeft) / averageCharWidth, MidpointRounding.AwayFromZero);
        offset = Math.Clamp(offset, 0, text.Length);

        var caretX = contentLeft + (offset * averageCharWidth);
        var rectAtCaret = new DomRect(caretX, contentTop, 0d, caretHeight);

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

    private static IElement? FindTopMostElementAt(IReadOnlyDictionary<IElement, HtmlRenderer.ElementLayoutMetrics> metrics, double x, double y)
    {
        return metrics
            .Where(pair => Contains(pair.Value, x, y))
            .OrderByDescending(pair => GetDepth(pair.Key))
            .ThenBy(pair => Math.Max(0f, pair.Value.BorderBoxWidth) * Math.Max(0f, pair.Value.BorderBoxHeight))
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private static bool Contains(HtmlRenderer.ElementLayoutMetrics metrics, double x, double y)
    {
        var left = metrics.BorderBoxX;
        var top = metrics.BorderBoxY;
        var right = metrics.BorderBoxX + metrics.BorderBoxWidth;
        var bottom = metrics.BorderBoxY + metrics.BorderBoxHeight;

        return x >= left && x <= right && y >= top && y <= bottom;
    }

    private static int GetDepth(IElement element)
    {
        var depth = 0;
        var current = element.ParentElement;

        while (current is not null)
        {
            depth++;
            current = current.ParentElement;
        }

        return depth;
    }

    private static float ParseLengthOrDefault(string? value, float fallback)
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

    private static float ParseLineHeightFactor(string? value, float fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();

        if (normalized == "normal")
        {
            return fallback;
        }

        if (normalized.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(normalized[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            return Math.Max(0.1f, percent / 100f);
        }

        if (normalized.EndsWith("px", StringComparison.Ordinal))
        {
            normalized = normalized[..^2].Trim();
        }

        if (float.TryParse(normalized, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Max(0.1f, parsed);
        }

        return fallback;
    }
}

