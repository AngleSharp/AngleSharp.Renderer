namespace AngleSharp.Renderer.Rendering;

using System.Diagnostics;

/// <summary>
/// What it took to lay out a frame (see <see cref="RenderedFrame.Metrics"/>): the time of each
/// phase and how much work they did, e.g., to show it in the developer tools of a browser.
/// </summary>
public sealed class RenderMetrics
{
    /// <summary>
    /// Gets the metrics of a frame that needed no layout, e.g., one moved to another scroll position.
    /// </summary>
    public static RenderMetrics None { get; } = new();

    /// <summary>
    /// Gets if the frame was brought up to date by laying out only the boxes that changed
    /// (see <see cref="IDomHarness.UpdateFrame"/>) instead of the whole document.
    /// </summary>
    public bool IsIncremental { get; init; }

    /// <summary>
    /// Gets the time it took to prepare the frame in total, i.e., of all phases.
    /// </summary>
    public TimeSpan Total { get; init; }

    /// <summary>
    /// Gets the time spent on loading the document's web fonts and resolving the page's default
    /// text style, before anything was laid out.
    /// </summary>
    public TimeSpan Setup { get; init; }

    /// <summary>
    /// Gets the time spent on computing the styles of the elements, i.e., building the render tree
    /// (selector matching and the cascade).
    /// </summary>
    public TimeSpan Style { get; init; }

    /// <summary>
    /// Gets the time spent on laying out the boxes and recording what to paint (the display list).
    /// </summary>
    public TimeSpan Layout { get; init; }

    /// <summary>
    /// Gets the time spent on mapping where the boxes are for hit testing and geometry reads.
    /// </summary>
    public TimeSpan HitTesting { get; init; }

    /// <summary>
    /// Gets the time spent on laying out the documents of iframes (part of <see cref="Layout"/>).
    /// </summary>
    public TimeSpan Subframes { get; init; }

    /// <summary>
    /// Gets the number of elements in the render tree, i.e., those with a computed style.
    /// </summary>
    public int Elements { get; init; }

    /// <summary>
    /// Gets the number of boxes that were laid out.
    /// </summary>
    public int Boxes { get; init; }

    /// <summary>
    /// Gets the number of boxes laid out once more to measure them (e.g., flex and grid items or
    /// shrink-to-fit boxes), on top of their final layout.
    /// </summary>
    public int TrialLayouts { get; init; }

    /// <summary>
    /// Gets the number of style maps that were created from computed styles.
    /// </summary>
    public int StyleMaps { get; init; }

    /// <summary>
    /// Gets the number of times text was measured.
    /// </summary>
    public int TextMeasurements { get; init; }

    /// <summary>
    /// Gets the number of commands of the display list.
    /// </summary>
    public int Commands { get; init; }

    /// <summary>
    /// Gets the number of places in the hit-test map.
    /// </summary>
    public int HitTestEntries { get; init; }

    /// <summary>
    /// Gets the managed memory allocated on the thread preparing the frame, in bytes. Not known
    /// for <see cref="IsIncremental"/> frames, where it is 0.
    /// </summary>
    public long AllocatedBytes { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"total {Total.TotalMilliseconds:F1}ms (setup {Setup.TotalMilliseconds:F1}, style {Style.TotalMilliseconds:F1}, layout {Layout.TotalMilliseconds:F1}, hit testing {HitTesting.TotalMilliseconds:F1}, subframes {Subframes.TotalMilliseconds:F1}), " +
        $"{Elements} elements, {Boxes} boxes, {TrialLayouts} trial layouts, {StyleMaps} style maps, {TextMeasurements} text measurements, {Commands} commands, {HitTestEntries} hit-test entries, {AllocatedBytes / 1024} KiB allocated" +
        (IsIncremental ? ", incremental" : string.Empty);
}

/// <summary>
/// Collects the <see cref="RenderMetrics"/> of a layout pass.
/// </summary>
internal sealed class RenderMetricsRecorder
{
    private readonly long _allocated = GC.GetAllocatedBytesForCurrentThread();

    public long Setup;
    public long Style;
    public long Layout;
    public long HitTesting;
    public long Subframes;
    public int Elements;
    public int TrialLayouts;
    public int StyleMaps;
    public int TextMeasurements;

    public static long Now => Stopwatch.GetTimestamp();

    public static long Since(long start) => Stopwatch.GetTimestamp() - start;

    public RenderMetrics Complete(bool isIncremental, int boxes, int commands, int hitTestEntries)
    {
        var setup = ToTimeSpan(Setup);
        var style = ToTimeSpan(Style);
        var layout = ToTimeSpan(Layout);
        var hitTesting = ToTimeSpan(HitTesting);
        var subframes = ToTimeSpan(Subframes);

        return new()
        {
            IsIncremental = isIncremental,
            Total = setup + style + layout + hitTesting + subframes,
            Setup = setup,
            Style = style,
            Layout = layout,
            HitTesting = hitTesting,
            Subframes = subframes,
            Elements = Elements,
            Boxes = boxes,
            TrialLayouts = TrialLayouts,
            StyleMaps = StyleMaps,
            TextMeasurements = TextMeasurements,
            Commands = commands,
            HitTestEntries = hitTestEntries,
            // the boxes laid out again since the last frame may have been laid out at any time in between
            AllocatedBytes = isIncremental ? 0 : GC.GetAllocatedBytesForCurrentThread() - _allocated,
        };
    }

    private static TimeSpan ToTimeSpan(long timestamp) => TimeSpan.FromTicks((long)(timestamp * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
}
