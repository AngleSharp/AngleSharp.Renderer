namespace AngleSharp;

using AngleSharp.Media.Dom;

/// <summary>
/// Configuration extensions for registering canvas rendering services.
/// </summary>
public static class RenderingConfigurationExtensions
{
    /// <summary>
    /// Registers a canvas rendering service with the configuration.
    /// </summary>
    public static IConfiguration WithRendering(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.WithRendering(context => new Renderer.CanvasRenderingService());
    }

    /// <summary>
    /// Registers a custom rendering service with the configuration.
    /// </summary>
    public static IConfiguration WithRendering(this IConfiguration configuration, Func<IBrowsingContext, IRenderingService> factory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(factory);
        return configuration.WithOnly(factory);
    }
}
