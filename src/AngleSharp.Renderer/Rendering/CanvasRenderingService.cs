namespace AngleSharp.Renderer;

using AngleSharp.Html.Dom;
using AngleSharp.Media.Dom;

/// <summary>
/// Registers a simple bitmap-backed 2D rendering context for canvas elements.
/// </summary>
public sealed class CanvasRenderingService : IRenderingService
{
    /// <inheritdoc/>
    public bool IsSupportingContext(string contextId) => string.Equals(contextId, "2d", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public IRenderingContext CreateContext(IHtmlCanvasElement host, string contextId)
    {
        if (!IsSupportingContext(contextId))
        {
            throw new InvalidOperationException($"Unsupported rendering context '{contextId}'.");
        }

        return new Canvas2DRenderingContext(host);
    }
}
