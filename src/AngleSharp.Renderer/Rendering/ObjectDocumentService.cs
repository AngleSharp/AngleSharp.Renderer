namespace AngleSharp.Renderer.Rendering;

using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Media;

/// <summary>
/// Notes the responses of <c>object</c> data that are documents (e.g. a page of HTML), which
/// an object shows instead of its fallback. AngleSharp clears the headers of a response once
/// it has been processed, so the type has to be noted while it is.
/// </summary>
internal sealed class ObjectDocumentService : IResourceService<IObjectInfo>
{
    private static readonly ConditionalWeakTable<IResponse, object> s_documents = new();
    private static readonly object s_marker = new();

    public static ObjectDocumentService Instance { get; } = new();

    private ObjectDocumentService()
    {
    }

    public bool SupportsType(string mimeType) => IsDocumentType(mimeType);

    public Task<IObjectInfo> CreateAsync(IResponse response, CancellationToken cancel)
    {
        s_documents.AddOrUpdate(response, s_marker);
        return Task.FromResult<IObjectInfo>(new ObjectInfo { Source = response.Address });
    }

    public static bool IsDocument(IResponse response) => s_documents.TryGetValue(response, out _);

    private static bool IsDocumentType(string? mimeType) =>
        mimeType?.Split(';')[0].Trim().ToLowerInvariant() is "text/html" or "application/xhtml+xml" or "text/plain" or "text/xml" or "application/xml";

    private sealed class ObjectInfo : IObjectInfo
    {
        public required Url Source { get; set; }

        public int Width => 0;

        public int Height => 0;
    }
}
