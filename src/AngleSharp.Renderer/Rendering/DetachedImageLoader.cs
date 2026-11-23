namespace AngleSharp.Renderer.Rendering;

using AngleSharp.Browser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

/// <summary>
/// Loads the images not in a document as soon as they get a source, as browsers do (e.g. the
/// sprites a game preloads with <c>new Image()</c> and draws into a canvas once they fired
/// load). The images in a document are loaded once they are laid out (see
/// <c>HtmlRenderer.GetElementImage</c>), as AngleSharp's image element requests nothing by
/// itself. The parser sets up the attributes of an element before inserting it, so whether
/// the image is in a document is looked at on the event loop, after the change (and after
/// the document was parsed); without one (and so without scripts) nothing is loaded here.
/// </summary>
internal sealed class DetachedImageLoader : IAttributeObserver
{
    public static DetachedImageLoader Instance { get; } = new();

    public void NotifyChange(IElement host, string name, string? value)
    {
        if (host is not IHtmlImageElement image || name is not ("src" or "srcset" or "sizes" or "crossorigin") ||
            image.Owner?.Context?.GetService<IEventLoop>() is not { } loop)
        {
            return;
        }

        loop.Enqueue(cancel => LoadIfDetached(image), TaskPriority.Normal);
    }

    private static void LoadIfDetached(IHtmlImageElement image)
    {
        // the parser may still be about to insert it (it parses off the event loop), which it
        // has done once the document is parsed
        if (image.Owner is { ReadyState: DocumentReadyState.Loading } document)
        {
            document.AddEventListener("DOMContentLoaded", (_, _) => LoadIfDetached(image));
            return;
        }

        if (!IsConnected(image) && image.GetImageSource() is { } source)
        {
            _ = ImageLoads.Load(image, source.Url, source.Density);
        }
    }

    private static bool IsConnected(INode node)
    {
        var root = node.GetRoot();

        while (root is IShadowRoot { Host: { } host })
        {
            root = host.GetRoot();
        }

        return root is IDocument;
    }
}
