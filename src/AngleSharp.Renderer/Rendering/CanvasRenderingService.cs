namespace AngleSharp.Renderer;

using System.Runtime.CompilerServices;
using AngleSharp.Html.Dom;
using AngleSharp.Media.Dom;

/// <summary>
/// Registers a bitmap-backed 2D rendering context for canvas elements.
/// </summary>
public sealed class CanvasRenderingService : IRenderingService
{
    // A canvas has one context, which getContext("2d") gives again and again: the element asks
    // for a new one each time, which would start with an empty bitmap.
    private static readonly ConditionalWeakTable<IHtmlCanvasElement, Canvas2DRenderingContext> s_contexts = new();

    /// <inheritdoc/>
    public bool IsSupportingContext(string contextId) => string.Equals(contextId, "2d", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public IRenderingContext CreateContext(IHtmlCanvasElement host, string contextId)
    {
        if (!IsSupportingContext(contextId))
        {
            throw new InvalidOperationException($"Unsupported rendering context '{contextId}'.");
        }

        return s_contexts.GetValue(host, static element => new Canvas2DRenderingContext(element));
    }

    /// <summary>
    /// Gets the 2D context of the canvas, if it has one.
    /// </summary>
    internal static Canvas2DRenderingContext? GetContext(IHtmlCanvasElement host) =>
        s_contexts.TryGetValue(host, out var context) ? context : null;
}
