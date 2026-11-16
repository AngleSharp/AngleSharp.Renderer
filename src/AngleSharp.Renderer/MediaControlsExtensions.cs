namespace AngleSharp.Dom;

using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// The parts of the controls the renderer paints for a media element (`audio[controls]`).
/// </summary>
public enum MediaControlPart
{
    /// <summary>No part, e.g. the panel around them.</summary>
    None,

    /// <summary>The button playing or pausing the media.</summary>
    PlayPause,

    /// <summary>The timeline, which seeks (see <see cref="MediaControlsExtensions.GetMediaTimelinePosition"/>).</summary>
    Timeline,

    /// <summary>The button muting the media, or making it audible again.</summary>
    Mute,
}

/// <summary>
/// What a host needs to make the controls of a media element interactive: they are painted by
/// the renderer, so there are no boxes to hit test.
/// </summary>
public static class MediaControlsExtensions
{
    /// <summary>
    /// Gets if the element is shown with the controls of a media element.
    /// </summary>
    /// <param name="element">The element, e.g. an audio element.</param>
    /// <returns>True if the renderer paints controls for it.</returns>
    public static bool HasMediaControls(this IElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return HtmlRenderer.ShowsMediaControls(element);
    }

    /// <summary>
    /// Gets the part of the controls of a media element at the given position in the viewport,
    /// as they were laid out last.
    /// </summary>
    /// <param name="element">The media element showing controls.</param>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <param name="y">The vertical position in CSS pixels.</param>
    /// <returns>The part, or <see cref="MediaControlPart.None"/> if there is none at that position.</returns>
    public static MediaControlPart GetMediaControlAt(this IElement element, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!TryGetControls(element, out var box, out var geometry))
        {
            return MediaControlPart.None;
        }

        var left = x - box.Left;
        var top = y - box.Top;
        return Contains(geometry.PlayButton, left, top) ? MediaControlPart.PlayPause
            : Contains(geometry.MuteButton, left, top) ? MediaControlPart.Mute
            : Contains(geometry.Timeline, left, top) ? MediaControlPart.Timeline
            : MediaControlPart.None;
    }

    /// <summary>
    /// Gets where on the timeline of the controls of a media element a horizontal position is, also
    /// beyond its ends (e.g. while dragging it).
    /// </summary>
    /// <param name="element">The media element showing controls.</param>
    /// <param name="x">The horizontal position in CSS pixels.</param>
    /// <returns>The fraction of the media's duration (0 to 1), or null if no timeline is shown.</returns>
    public static double? GetMediaTimelinePosition(this IElement element, double x)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!TryGetControls(element, out var box, out var geometry) || geometry.Timeline.Width <= 0f)
        {
            return null;
        }

        return Math.Clamp((x - box.Left - geometry.Timeline.X) / geometry.Timeline.Width, 0d, 1d);
    }

    private static bool TryGetControls(IElement element, out (double Left, double Top, double Width, double Height) box, out HtmlRenderer.MediaControlsGeometry geometry)
    {
        box = default;
        geometry = default!;
        return HtmlRenderer.ShowsMediaControls(element) && HtmlRenderer.TryGetMediaControls(element, out geometry) &&
            ListBoxExtensions.TryGetContentBox(element, out box);
    }

    private static bool Contains(RenderRect rect, double x, double y) =>
        rect.Width > 0f && x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;
}
