namespace AngleSharp;

using AngleSharp.Css;
using AngleSharp.Renderer;
using System.Linq;
using System.Runtime.CompilerServices;

/// <summary>
/// Extension methods for retrieving interactive renderer state from a browsing context.
/// </summary>
public static class InteractiveHtmlRendererStateExtensions
{
    private static readonly ConditionalWeakTable<IBrowsingContext, IDomHarness> s_harnesses = new();

    /// <summary>
    /// Gets the interactive DOM harness for the browsing context.
    /// </summary>
    public static IDomHarness GetDomHarness(this IBrowsingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return s_harnesses.GetValue(context, static browsingContext =>
        {
            var renderDevice = browsingContext.GetServices<IRenderDevice>().FirstOrDefault();

            if (renderDevice is null)
            {
                throw new InvalidOperationException("No IRenderDevice service is registered in the browsing context. Register a render device service in IConfiguration before creating the context.");
            }

            return new InteractiveHtmlRendererState(browsingContext, renderDevice);
        });
    }

    /// <summary>
    /// Gets the interactive DOM harness for the browsing context if one has already been created
    /// (via a prior <see cref="GetDomHarness"/> call), without creating one and without throwing
    /// when no <see cref="IRenderDevice"/> service is registered. Used by rendering code paths
    /// that want to honor interaction state (e.g. scroll position) when present, but must remain
    /// a no-op for the overwhelmingly common case of a document that was never wired up for
    /// interactive use.
    /// </summary>
    public static bool TryGetDomHarness(this IBrowsingContext context, out IDomHarness? harness)
    {
        ArgumentNullException.ThrowIfNull(context);

        return s_harnesses.TryGetValue(context, out harness);
    }
}
