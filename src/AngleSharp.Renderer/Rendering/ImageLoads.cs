namespace AngleSharp.Renderer.Rendering;

using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

/// <summary>
/// Starts the loads of images (AngleSharp's <c>LoadAsync</c>), remembering the one in flight
/// per image.
/// </summary>
internal static class ImageLoads
{
    private static readonly ConditionalWeakTable<IHtmlImageElement, PendingLoad> s_pending = new();

    public static Task Load(IHtmlImageElement image, Url source, double density)
    {
        lock (s_pending)
        {
            if (s_pending.TryGetValue(image, out var pending) && !pending.Task.IsCompleted && pending.Source.Equals(source))
            {
                return pending.Task;
            }

            var task = image.LoadAsync(source, density);

            if (task.IsCompleted)
            {
                s_pending.Remove(image);
            }
            else
            {
                s_pending.AddOrUpdate(image, new PendingLoad(source, task));
            }

            return task;
        }
    }

    private sealed record PendingLoad(Url Source, Task Task);
}
