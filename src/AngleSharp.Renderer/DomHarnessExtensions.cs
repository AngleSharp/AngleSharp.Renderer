namespace AngleSharp.Renderer;

using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Extensions for <see cref="IDomHarness"/>.
/// </summary>
public static class DomHarnessExtensions
{
    /// <summary>
    /// Tells the harness that the state of the element changed without a change of the DOM, e.g.
    /// it got or lost the focus, or the caret of a text field moved. The next frame shows it,
    /// with just the boxes laid out again whose styles may change with it (e.g. by
    /// <c>:focus-within</c>), if possible (see <see cref="IDomHarness.UpdateFrame"/>).
    /// </summary>
    /// <param name="harness">The harness of the element's document.</param>
    /// <param name="element">The element whose state changed.</param>
    public static void NotifyStateChanged(this IDomHarness harness, IElement element)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(element);

        if (harness is InteractiveHtmlRendererState state)
        {
            state.OnStateChanged(element);
        }
    }

    /// <summary>
    /// Prepares the next frame like <see cref="IDomHarness.PrepareFrame"/>, but lays out just as
    /// much of the page as the viewport (with the overscan) shows, e.g., to show a page that was
    /// just loaded early on. If the page goes on below, the frame is partial (see
    /// <see cref="RenderedFrame.IsPartial"/>): it is animating, and the next frame lays out the
    /// whole page; until then, reading the geometry lays it out as well.
    /// </summary>
    /// <param name="harness">The harness of the document.</param>
    /// <param name="overscan">The rows to paint above and below the viewport.</param>
    /// <returns>The frame.</returns>
    public static RenderedFrame PrepareViewportFrame(this IDomHarness harness, int overscan = 0)
    {
        ArgumentNullException.ThrowIfNull(harness);

        return harness is InteractiveHtmlRendererState state
            ? state.PrepareFrame(overscan, viewportOnly: true)
            : harness.PrepareFrame(overscan);
    }
}
