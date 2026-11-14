namespace AngleSharp.Renderer;

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Media.Dom;
using AngleSharp.Renderer.Rendering;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

/// <summary>
/// The controls of a media element (`audio[controls]`): a panel with a play/pause button, the
/// time, a timeline and a mute button, as in Chrome. What moves while it plays (the time and the
/// timeline) is a <see cref="MediaTimelineCommand"/>, which is painted for the moment the frame
/// is rasterized, so a playing element needs no layout to be shown as it goes on.
/// </summary>
public sealed partial class HtmlRenderer
{
    /// <summary>The size of the controls of an audio element in Chrome.</summary>
    private const float MediaControlsWidth = 300f;
    private const float MediaControlsHeight = 54f;

    private const float MediaButtonSize = 32f;
    private const float MediaControlsPadding = 12f;
    private const float MediaTimeFontSize = 13f;
    private const float MediaTrackHeight = 4f;
    private const float MediaThumbSize = 12f;

    private static readonly RenderColor MediaPanelColor = new(241, 243, 244);
    private static readonly RenderColor MediaIconColor = new(32, 33, 36);
    private static readonly RenderColor MediaTrackColor = new(32, 33, 36, 64);

    private static readonly ConditionalWeakTable<IElement, MediaControlsGeometry> s_mediaControls = new();

    /// <summary>
    /// Where the parts of the controls are, relative to the content box of the element.
    /// </summary>
    internal sealed record MediaControlsGeometry(RenderRect PlayButton, RenderRect Timeline, RenderRect MuteButton);

    /// <summary>
    /// If an element is a media element showing controls; an audio element without them is not
    /// rendered at all (HTML 15.5.6: `audio:not([controls]) { display: none }`).
    /// </summary>
    internal static bool ShowsMediaControls(IElement element) =>
        element is IHtmlAudioElement && element.HasAttribute("controls");

    private static bool IsAudioWithoutControls(IElement element) =>
        element is IHtmlAudioElement && element is not IPseudoElement && !element.HasAttribute("controls");

    /// <summary>
    /// Gets where the parts of the controls of a media element were painted last.
    /// </summary>
    internal static bool TryGetMediaControls(IElement element, out MediaControlsGeometry geometry) =>
        s_mediaControls.TryGetValue(element, out geometry!);

    private static void PaintMediaControls(DisplayList displayList, IElement element, RenderTextStyle textStyle, LayoutContext context, float x, float y, float width, float height)
    {
        if (width <= 0f || height <= 0f || element is not IHtmlMediaElement media)
        {
            return;
        }

        var radius = Math.Min(height / 2f, MediaControlsHeight / 2f);
        displayList.FillRect(new RenderRect(x, y, width, height), MediaPanelColor, new RenderCornerRadii(radius, radius, radius, radius, radius, radius, radius, radius));

        var centerY = y + (height / 2f);
        var button = Math.Min(MediaButtonSize, height);
        var play = new RenderRect(x + MediaControlsPadding, centerY - (button / 2f), button, button);
        var mute = new RenderRect(x + width - MediaControlsPadding - button, play.Y, button, button);

        if (mute.X < play.X + play.Width)
        {
            mute = default;
        }

        // without a source there is nothing to play (AngleSharp before 1.10.4 is never paused)
        PaintPlayIcon(displayList, play, media.IsPaused || media.IsEnded || string.IsNullOrEmpty(media.CurrentSource));

        if (mute.Width > 0f)
        {
            PaintVolumeIcon(displayList, mute, media.IsMuted || media.Volume <= 0d);
        }

        // the time takes the room of its widest value, so the timeline does not move with it
        var duration = media.Duration;
        var timeStyle = textStyle with
        {
            FontSize = MediaTimeFontSize,
            FontFamily = "sans-serif",
            FontWeight = 400f,
            IsItalic = false,
            LetterSpacing = 0f,
            Color = MediaIconColor,
            Underline = false,
            Overline = false,
            StrikeThrough = false,
            TextShadows = [],
            VerticalAlignOffset = 0f,
        };
        var widest = FormatMediaTimes(duration, duration).Select(digit => char.IsDigit(digit) ? '0' : digit).ToArray();
        var timeX = play.X + play.Width + 4f;
        var timeWidth = MediaTimeFontSize <= height ? MathF.Ceiling(MeasureTextWidth(context, new string(widest), timeStyle)) : 0f;
        var trackLeft = timeX + timeWidth + MediaControlsPadding;
        var trackRight = (mute.Width > 0f ? mute.X : x + width - MediaControlsPadding) - 8f;
        var track = trackRight - trackLeft >= 2f * MediaThumbSize
            ? new RenderRect(trackLeft, centerY - (MediaTrackHeight / 2f), trackRight - trackLeft, MediaTrackHeight)
            : default;
        var baseline = centerY + (MediaTimeFontSize * (FormControlTextAscentRatio - FormControlTextDescentRatio) / 2f);

        displayList.Add(new MediaTimelineCommand(new MediaTimeline(
            timeWidth > 0f && timeX + timeWidth <= x + width ? new RenderRect(timeX, y, timeWidth, height) : default,
            baseline,
            track,
            timeStyle.FontFamily,
            MediaTimeFontSize,
            MediaIconColor,
            media.CurrentTime,
            duration,
            media.PlaybackRate,
            IsMediaPlaying(media),
            Stopwatch.GetTimestamp())));

        s_mediaControls.AddOrUpdate(element, new MediaControlsGeometry(
            Relative(play),
            track.Width > 0f ? Relative(new RenderRect(track.X, centerY - (MediaThumbSize / 2f), track.Width, MediaThumbSize)) : default,
            mute.Width > 0f ? Relative(mute) : default));

        RenderRect Relative(RenderRect rect) => new(rect.X - x, rect.Y - y, rect.Width, rect.Height);
    }

    // its time goes on (also while it waits for data, which is soon a new frame anyway)
    private static bool IsMediaPlaying(IHtmlMediaElement media) =>
        !media.IsPaused && !media.IsEnded && media.ReadyState >= MediaReadyState.CurrentData;

    private static void PaintPlayIcon(DisplayList displayList, RenderRect button, bool isPaused)
    {
        var centerX = button.X + (button.Width / 2f);
        var centerY = button.Y + (button.Height / 2f);
        var size = button.Height * 0.44f;

        if (isPaused)
        {
            // a triangle pointing to the right, its weight a bit right of the center
            var left = centerX - (size * 0.4f);
            PaintPolygon(displayList, [(left, centerY - (size / 2f)), (left + (size * 0.9f), centerY), (left, centerY + (size / 2f))], MediaIconColor);
        }
        else
        {
            var bar = size * 0.3f;
            displayList.FillRect(new RenderRect(centerX - (size * 0.4f), centerY - (size / 2f), bar, size), MediaIconColor);
            displayList.FillRect(new RenderRect(centerX + (size * 0.4f) - bar, centerY - (size / 2f), bar, size), MediaIconColor);
        }
    }

    private static void PaintVolumeIcon(DisplayList displayList, RenderRect button, bool isMuted)
    {
        var centerX = button.X + (button.Width / 2f);
        var centerY = button.Y + (button.Height / 2f);
        var size = button.Height * 0.5f;
        var left = centerX - (size / 2f);

        // the speaker: a box with a cone
        displayList.FillRect(new RenderRect(left, centerY - (size * 0.18f), size * 0.22f, size * 0.36f), MediaIconColor);
        PaintPolygon(displayList,
        [
            (left + (size * 0.2f), centerY - (size * 0.18f)),
            (left + (size * 0.5f), centerY - (size * 0.45f)),
            (left + (size * 0.5f), centerY + (size * 0.45f)),
            (left + (size * 0.2f), centerY + (size * 0.18f)),
        ], MediaIconColor);

        if (isMuted)
        {
            // a cross where the sound would be
            var crossX = left + (size * 0.78f);
            var arm = size * 0.2f;
            var stroke = Math.Max(1.5f, size * 0.08f);
            PaintLine(displayList, crossX - arm, centerY - arm, crossX + arm, centerY + arm, stroke, MediaIconColor);
            PaintLine(displayList, crossX - arm, centerY + arm, crossX + arm, centerY - arm, stroke, MediaIconColor);
        }
        else
        {
            // the waves, as two bars getting longer
            var stroke = Math.Max(1.5f, size * 0.1f);
            var radii = new RenderCornerRadii(stroke / 2f, stroke / 2f, stroke / 2f, stroke / 2f, stroke / 2f, stroke / 2f, stroke / 2f, stroke / 2f);
            displayList.FillRect(new RenderRect(left + (size * 0.65f), centerY - (size * 0.15f), stroke, size * 0.3f), MediaIconColor, radii);
            displayList.FillRect(new RenderRect(left + (size * 0.88f), centerY - (size * 0.32f), stroke, size * 0.64f), MediaIconColor, radii);
        }
    }

    private static void PaintPolygon(DisplayList displayList, IReadOnlyList<(float X, float Y)> points, RenderColor color)
    {
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        displayList.PushClipShape(new RenderClipPolygon(points));
        displayList.FillRect(new RenderRect(minX, minY, maxX - minX, maxY - minY), color);
        displayList.PopClip();
    }

    private static void PaintLine(DisplayList displayList, float x1, float y1, float x2, float y2, float stroke, RenderColor color)
    {
        var length = MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

        if (length <= 0f)
        {
            return;
        }

        // the corners of the line: its ends moved by half the stroke across it
        var nx = -(y2 - y1) / length * (stroke / 2f);
        var ny = (x2 - x1) / length * (stroke / 2f);
        PaintPolygon(displayList, [(x1 + nx, y1 + ny), (x2 + nx, y2 + ny), (x2 - nx, y2 - ny), (x1 - nx, y1 - ny)], color);
    }

    /// <summary>
    /// The time a media element shows, e.g. "0:07 / 3:12", or only the current one for a stream
    /// without an end.
    /// </summary>
    internal static string FormatMediaTimes(double currentTime, double duration) =>
        double.IsPositiveInfinity(duration)
            ? FormatMediaTime(currentTime)
            : string.Concat(FormatMediaTime(currentTime), " / ", FormatMediaTime(duration));

    internal static string FormatMediaTime(double seconds)
    {
        var total = double.IsFinite(seconds) && seconds > 0d ? (long)Math.Floor(seconds) : 0L;
        var hours = total / 3600;
        var minutes = (total / 60) % 60;
        var rest = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>
    /// The display list to rasterize: the same, unless it has media timelines, which are painted
    /// for this moment.
    /// </summary>
    internal static DisplayList PaintMediaTimelines(DisplayList displayList)
    {
        if (!displayList.HasMediaTimelines)
        {
            return displayList;
        }

        var now = Stopwatch.GetTimestamp();
        var painted = new DisplayList { Fonts = displayList.Fonts };

        foreach (var command in displayList.Commands)
        {
            if (command is MediaTimelineCommand timeline)
            {
                timeline.Timeline.Paint(painted, now);
            }
            else
            {
                painted.Add(command);
            }
        }

        return painted;
    }

    /// <summary>
    /// What moves in the controls of a media element: its time and its timeline. It carries the
    /// state of the element when it was laid out, and goes on with the time that passed since
    /// when it plays, as a compositor would.
    /// </summary>
    internal sealed class MediaTimeline(
        RenderRect time,
        float baseline,
        RenderRect track,
        string fontFamily,
        float fontSize,
        RenderColor color,
        double currentTime,
        double duration,
        double playbackRate,
        bool isPlaying,
        long timestamp)
    {
        // the timeline is no animation to watch closely
        private static readonly TimeSpan MinimumStep = TimeSpan.FromMilliseconds(33);

        /// <summary>Where it paints: the time and the timeline.</summary>
        public RenderRect Area => track.Width > 0f && time.Width > 0f
            ? new RenderRect(time.X, time.Y, track.X + track.Width - time.X, time.Height)
            : track.Width > 0f ? track : time;

        public double GetTime(long now)
        {
            if (!isPlaying || !double.IsFinite(currentTime))
            {
                return currentTime;
            }

            var time = currentTime + (Stopwatch.GetElapsedTime(timestamp, now).TotalSeconds * playbackRate);
            return double.IsFinite(duration) ? Math.Clamp(time, 0d, duration) : Math.Max(0d, time);
        }

        public void Paint(DisplayList displayList, long now)
        {
            var position = GetTime(now);

            if (track.Width > 0f)
            {
                var radius = track.Height / 2f;
                var radii = new RenderCornerRadii(radius, radius, radius, radius, radius, radius, radius, radius);
                var fraction = double.IsFinite(duration) && duration > 0d ? (float)Math.Clamp(position / duration, 0d, 1d) : 0f;
                var played = track.Width * fraction;
                displayList.FillRect(track, MediaTrackColor, radii);

                if (played > 0f)
                {
                    displayList.FillRect(new RenderRect(track.X, track.Y, played, track.Height), color, radii);
                }

                var thumb = MediaThumbSize / 2f;
                displayList.FillRect(
                    new RenderRect(track.X + played - thumb, track.Y + radius - thumb, MediaThumbSize, MediaThumbSize),
                    color,
                    new RenderCornerRadii(thumb, thumb, thumb, thumb, thumb, thumb, thumb, thumb));
            }

            if (time.Width > 0f)
            {
                displayList.DrawText(FormatMediaTimes(position, duration), time.X, baseline, color, fontSize, fontFamily);
            }
        }

        /// <summary>
        /// The time until it looks different: the next second of its time, or the next pixel of
        /// its timeline, or null if it does not play.
        /// </summary>
        public TimeSpan? GetTimeToNextChange(long now)
        {
            var current = GetTime(now);

            if (!isPlaying || playbackRate <= 0d || !double.IsFinite(current) || double.IsFinite(duration) && current >= duration)
            {
                return null;
            }

            var step = 1d - (current - Math.Floor(current));

            if (track.Width > 0f && double.IsFinite(duration) && duration > 0d)
            {
                step = Math.Min(step, duration / track.Width);
            }

            var remaining = TimeSpan.FromSeconds(step / playbackRate);
            return remaining < MinimumStep ? MinimumStep : remaining;
        }
    }
}

/// <summary>
/// The time and timeline of the controls of a media element, painted for the moment the frame is
/// rasterized (see <see cref="HtmlRenderer.PaintMediaTimelines"/>), never given to a backend.
/// </summary>
internal sealed record MediaTimelineCommand(HtmlRenderer.MediaTimeline Timeline) : RenderCommand;
