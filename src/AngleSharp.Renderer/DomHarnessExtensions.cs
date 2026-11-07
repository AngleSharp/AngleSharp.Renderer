namespace AngleSharp.Renderer;

using AngleSharp.Dom;

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
}
