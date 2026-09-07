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
}
