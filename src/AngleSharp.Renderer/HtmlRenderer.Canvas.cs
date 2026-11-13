namespace AngleSharp.Renderer;

using System.Runtime.CompilerServices;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;
using SkiaSharp;

public sealed partial class HtmlRenderer
{
    // The images drawn into canvases, decoded once (an SVG at its natural size).
    private static readonly ConditionalWeakTable<CachedImageResource, SKImage> s_canvasImages = new();
    private static readonly SKImage s_undecodable = SKImage.Create(new SKImageInfo(1, 1));

    /// <summary>
    /// Gets if the element is a canvas showing its bitmap, which it does with scripting; without,
    /// its content (the fallback) is shown instead.
    /// </summary>
    private static bool IsCanvas(IElement element) =>
        element is not IPseudoElement &&
        string.Equals(element.LocalName, "canvas", StringComparison.OrdinalIgnoreCase) &&
        (element.NamespaceUri is null || element.NamespaceUri == NamespaceNames.HtmlUri) &&
        element.Owner?.Context is { } context && context.IsScripting();

    /// <summary>
    /// Gets if the children of the element are not laid out, as it shows something else.
    /// </summary>
    private static bool HidesChildren(IElement element) =>
        element is not IPseudoElement &&
        (string.Equals(element.LocalName, "svg", StringComparison.OrdinalIgnoreCase) || IsCanvas(element));

    private static bool TryResolveCanvas(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect, out RenderRect? clipRect)
    {
        image = null;
        rect = default;
        clipRect = null;

        if (node.Ref is not IHtmlCanvasElement canvas || !IsCanvas(canvas))
        {
            return false;
        }

        var resource = new CachedImageResource(null, null, "image/png", Math.Max(0, canvas.Width), Math.Max(0, canvas.Height)) { Canvas = canvas };
        return TryResolveReplacedElementRect(resource, styleMap, containingWidth, x, y, out image, out rect, out clipRect);
    }

    /// <summary>
    /// The decoded image of an <c>img</c> element to draw into a canvas, if it is loaded. One
    /// still loading draws nothing, as in browsers (without waiting for it).
    /// </summary>
    internal static SKImage? TryDecodeLoadedImage(IHtmlImageElement element)
    {
        if (element is ILoadableElement { CurrentDownload.Task: { IsCompleted: false } })
        {
            return null;
        }

        if (!TryGetOrLoadImageResource(element, element.GetAttribute("src"), out var resource) || resource is null)
        {
            return null;
        }

        var image = s_canvasImages.GetValue(resource, static value =>
        {
            if (value.VectorImage is { } vector)
            {
                var info = new SKImageInfo(Math.Max(1, value.NaturalWidth), Math.Max(1, value.NaturalHeight), SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info);
                surface.Canvas.Clear(SKColors.Transparent);
                vector.Draw(surface.Canvas, SKRect.Create(info.Width, info.Height));
                return surface.Snapshot();
            }

            using var data = SKData.CreateCopy(value.Bytes ?? []);
            return SKImage.FromEncodedData(data) ?? s_undecodable;
        });

        return ReferenceEquals(image, s_undecodable) ? null : image;
    }

    /// <summary>
    /// Parses a CSS color, telling if it is one.
    /// </summary>
    internal static bool TryParseColor(string rawColor, out RenderColor color)
    {
        var parsed = ParseColor(rawColor, new RenderColor(0, 0, 0, 0));
        var other = ParseColor(rawColor, new RenderColor(255, 255, 255, 0));
        color = parsed;

        // only what is no color gives the fallback, which then differs
        return parsed == other;
    }
}
