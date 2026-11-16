namespace AngleSharp.Renderer.Rendering;

using AngleSharp.Renderer.Skia;

/// <summary>
/// A laid out frame of an interactive document (see <see cref="IDomHarness.PrepareFrame"/>). It
/// no longer refers to the DOM for painting, so it can be rasterized on another thread while the
/// document goes on, e.g., with the layout of the next frame.
/// </summary>
public sealed class RenderedFrame
{
    private readonly DisplayList _displayList;
    private readonly IRenderBackend _backend;
    private readonly RenderViewport _surface;
    // how far the display list is moved when rasterized, see Reposition
    private readonly float _shiftY;
    // what is shown on top of the page, see WithOverlay
    private readonly DisplayList? _overlay;
    // the display list with the selection painted into it, see WithSelection
    private readonly DisplayList? _selection;
    // the same as painted into the display list of the frame's sticky boxes as laid out (see Reposition)
    private readonly DisplayList? _selectionBase;
    private HtmlRenderer.TextMap? _textMap;
    private readonly AngleSharp.Dom.IDocument? _document;

    internal RenderedFrame(DisplayList displayList, IRenderBackend backend, RenderViewport surface, int viewportHeight, double scrollTop, int overscanTop, double contentHeight, bool isScrollShiftable, bool canReposition, HitTestMap hitTestMap, float shiftY = 0f, AngleSharp.Dom.IDocument? document = null)
    {
        _document = document;
        CanReposition = canReposition;
        _displayList = displayList;
        _backend = backend;
        _surface = surface;
        _shiftY = shiftY;
        ViewportHeight = viewportHeight;
        ScrollTop = scrollTop;
        OverscanTop = overscanTop;
        ContentHeight = contentHeight;
        IsScrollShiftable = isScrollShiftable;
        HitTestMap = hitTestMap;
    }

    private RenderedFrame(RenderedFrame frame, DisplayList? overlay, DisplayList? selection)
        : this(frame._displayList, frame._backend, frame._surface, frame.ViewportHeight, frame.ScrollTop, frame.OverscanTop, frame.ContentHeight, frame.IsScrollShiftable, frame.CanReposition, frame.HitTestMap, frame._shiftY, frame._document)
    {
        IsAnimating = frame.IsAnimating;
        IsPartial = frame.IsPartial;
        Metrics = frame.Metrics;
        Stickies = frame.Stickies;
        _overlay = overlay;
        _selection = selection;
        _selectionBase = selection;
        _textMap = frame._textMap;
    }

    /// <summary>
    /// Creates the frame showing the given display list (the frame's own with some boxes painted
    /// again), whose boxes are where the frame has them.
    /// </summary>
    internal RenderedFrame(DisplayList displayList, RenderedFrame frame, RenderMetrics metrics)
        : this(displayList, frame._backend, frame._surface, frame.ViewportHeight, frame.ScrollTop, frame.OverscanTop, frame.ContentHeight, frame.IsScrollShiftable, frame.CanReposition, frame.HitTestMap, frame._shiftY, frame._document)
    {
        IsAnimating = frame.IsAnimating;
        Metrics = metrics;

        // its sticky boxes, if they are still where they were
        if (frame.Stickies is { } stickies)
        {
            Stickies = frame._shiftY == 0f ? stickies.With(displayList, frame.HitTestMap) : null;
            CanReposition = Stickies is not null;
        }
    }

    private RenderedFrame(DisplayList displayList, RenderedFrame frame, double scrollTop, HitTestMap hitTestMap, float shiftY, DisplayList? stuckSelection)
        : this(displayList, frame, scrollTop, hitTestMap, shiftY)
    {
        _selection = stuckSelection;
    }

    private RenderedFrame(DisplayList displayList, RenderedFrame frame, double scrollTop, HitTestMap hitTestMap, float shiftY)
        : this(displayList, frame._backend, frame._surface, frame.ViewportHeight, scrollTop, frame.OverscanTop, frame.ContentHeight, frame.IsScrollShiftable, true, hitTestMap, shiftY, frame._document)
    {
        IsPartial = frame.IsPartial;
        Stickies = frame.Stickies;
        _selection = frame._selection;
        _selectionBase = frame._selectionBase;
        _textMap = frame._textMap;
    }

    /// <summary>
    /// Gets the width of the surface (and the viewport) in pixels.
    /// </summary>
    public int Width => _surface.Width;

    /// <summary>
    /// Gets the height of the surface, i.e., the viewport and the overscan above and below it.
    /// </summary>
    public int Height => _surface.Height;

    /// <summary>
    /// Gets the height of the viewport in pixels.
    /// </summary>
    public int ViewportHeight { get; }

    /// <summary>
    /// Gets the vertical scroll offset of the page the frame was laid out for.
    /// </summary>
    public double ScrollTop { get; }

    /// <summary>
    /// Gets the rows of the surface above the viewport: the viewport starts at this row.
    /// </summary>
    public int OverscanTop { get; }

    /// <summary>
    /// Gets the height of the page's content, at least the viewport's (the root's scrollHeight).
    /// </summary>
    public double ContentHeight { get; }

    /// <summary>
    /// Gets if the page has no boxes attached to the viewport (fixed or sticky), such that the
    /// surface shifted by a scroll delta looks like a frame painted at the scrolled position.
    /// </summary>
    public bool IsScrollShiftable { get; }

    /// <summary>
    /// Gets if the frame can be moved to another scroll position without a layout (see
    /// <see cref="Reposition"/>), which holds unless the page has sticky boxes.
    /// </summary>
    public bool CanReposition { get; private set; }

    /// <summary>Gets the sticky boxes of the frame, which stick anew when it is repositioned.</summary>
    internal HtmlRenderer.IStickyFrame? Stickies { get; init; }

    /// <summary>
    /// Gets if CSS transitions or animations are running, i.e., the next frame looks different
    /// even if nothing else changes.
    /// </summary>
    public bool IsAnimating { get; internal set; }

    /// <summary>
    /// Gets if the frame shows the part of the page in and around the viewport only (see
    /// <see cref="DomHarnessExtensions.PrepareViewportFrame"/>): below it, the page is not laid out,
    /// so <see cref="ContentHeight"/> and the <see cref="HitTestMap"/> end there. Such a frame is
    /// animating, as the next one shows the whole page.
    /// </summary>
    public bool IsPartial { get; internal set; }

    /// <summary>
    /// Gets what it took to lay out the frame. A frame moved to another scroll position (see
    /// <see cref="Reposition"/>) needed no layout: it has <see cref="RenderMetrics.None"/>.
    /// </summary>
    public RenderMetrics Metrics { get; internal init; } = RenderMetrics.None;

    /// <summary>Gets the commands of the frame, as laid out (before any shift).</summary>
    internal DisplayList DisplayList => _displayList;

    /// <summary>
    /// Gets what is painted where, in viewport coordinates of this frame.
    /// </summary>
    public HitTestMap HitTestMap { get; }

    /// <summary>
    /// Moves the frame to another scroll position without laying out the page again: its
    /// display list covers the whole page, which scrolls alike, except for the fixed boxes
    /// that stay where they are. The overscan follows the new position.
    /// </summary>
    /// <param name="scrollTop">The vertical scroll offset of the page to show.</param>
    /// <returns>The frame at the new position.</returns>
    public RenderedFrame Reposition(double scrollTop)
    {
        if (!CanReposition)
        {
            throw new InvalidOperationException("Frames with sticky boxes cannot be repositioned.");
        }

        // the sticky boxes stick as they do at the position, from where they were laid out
        if (Stickies is { } stickies)
        {
            var (displayList, selection, map) = stickies.StickAt(scrollTop, _selectionBase);
            var shift = (float)(stickies.ScrollTop - scrollTop);
            return new RenderedFrame(displayList, this, scrollTop, map.Shift(shift), shift, selection);
        }

        var delta = (float)(ScrollTop - scrollTop);
        return new RenderedFrame(_displayList, this, scrollTop, HitTestMap.Shift(delta), _shiftY + delta);
    }

    /// <summary>
    /// The frame with something on top of it that isn't part of the page, which needs no layout.
    /// </summary>
    /// <param name="overlay">What to show on top, in viewport coordinates.</param>
    /// <returns>The frame showing the overlay, which is not repositioned with it.</returns>
    public RenderedFrame WithOverlay(DisplayList overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return new RenderedFrame(this, overlay, _selection);
    }

    /// <summary>
    /// The frame with the given display list (the frame's own with a selection painted into it)
    /// painted instead, which needs no layout.
    /// </summary>
    internal RenderedFrame WithSelection(DisplayList selection) => new(this, _overlay, selection);

    /// <summary>Gets where the text of the frame is, as laid out (before any shift).</summary>
    internal HtmlRenderer.TextMap TextMap => _textMap ??= new HtmlRenderer.TextMap(_displayList, OverscanTop, _document!);

    /// <summary>Gets how far a repositioned frame's text map moves with the page.</summary>
    internal float TextMapShiftY => _shiftY;

    /// <summary>
    /// Gets if text of the page is painted at the point, e.g., for showing the cursor of text
    /// there. Only a frame of a document whose text can be selected (see
    /// <see cref="IDomHarness.IsTextSelectionEnabled"/>) knows where its text is. Does not touch
    /// the DOM, so it is safe to call from any thread.
    /// </summary>
    /// <param name="x">The horizontal position in viewport coordinates.</param>
    /// <param name="y">The vertical position in viewport coordinates.</param>
    /// <returns>True if a glyph box of the text covers the point.</returns>
    public bool HasTextAt(double x, double y) =>
        _document is not null && TextMap.HasTextAt((float)x, (float)y + OverscanTop, -_shiftY);

    /// <summary>
    /// Gets how long the frame still looks as it does now because of its animated images (e.g.,
    /// a GIF of several frames in view or in the overscan) or the controls of playing media (their
    /// time and timeline), i.e., when rasterizing it again shows another frame of one of them,
    /// which needs no layout. Safe to call from any thread.
    /// </summary>
    /// <returns>The time until an animated image or the controls of playing media change, or null if none does.</returns>
    public TimeSpan? GetTimeToNextImageFrame()
    {
        TimeSpan? next = null;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var transforms = new Stack<RenderTransform2D>();
        // a repositioned frame is shifted, except for its fixed boxes (see GetPageDisplayList)
        var transform = RenderTransform2D.Translate(0f, _shiftY);
        var anchorDepth = 0;

        foreach (var command in (_selection ?? _displayList).Commands)
        {
            switch (command)
            {
                case PushTransformCommand push:
                    transforms.Push(transform);
                    transform = RenderTransform2D.Multiply(transform, push.Transform);
                    continue;
                case PopTransformCommand when transforms.Count > 0:
                    transform = transforms.Pop();
                    continue;
                case PushViewportAnchorCommand when anchorDepth++ == 0:
                    transforms.Push(transform);
                    transform = RenderTransform2D.Multiply(transform, RenderTransform2D.Translate(0f, -_shiftY));
                    continue;
                case PopViewportAnchorCommand when --anchorDepth == 0 && transforms.Count > 0:
                    transform = transforms.Pop();
                    continue;
                case MediaTimelineCommand { Timeline: var timeline }:
                    if (IsOnSurface(timeline.Area, transform) && timeline.GetTimeToNextChange(now) is { } change)
                    {
                        next = next is { } sooner && sooner <= change ? sooner : change;
                    }

                    continue;
                case DrawSubframeCommand { Frame: { } subframe } draw:
                    if (IsOnSurface(draw.Viewport, transform) && subframe.GetTimeToNextImageFrame() is { } nested)
                    {
                        next = next is { } sooner && sooner <= nested ? sooner : nested;
                    }

                    continue;
            }

            var (rect, image) = command switch
            {
                DrawImageCommand draw => (draw.Rect, draw.Image),
                FillRectCommand { Paint: RenderImagePaint paint } fill => (fill.Rect, paint.Image),
                DrawBorderImageCommand { Image.Source: RenderImagePaint paint } border => (border.Area, paint.Image),
                _ => (default(RenderRect), null),
            };

            if (image is null || image.VectorImage is not null || image.Canvas is not null || !IsOnSurface(rect, transform) ||
                AnimatedImage.For(image.Data) is not { } animated || animated.GetTimeToNextFrame() is not { } remaining)
            {
                continue;
            }

            next = next is { } earlier && earlier <= remaining ? earlier : remaining;
        }

        return next;
    }

    // whether any of the rectangle is on the surface, vertically
    private bool IsOnSurface(RenderRect rect, RenderTransform2D transform)
    {
        var bounds = transform.MapBounds(rect);
        return bounds.Y + bounds.Height >= 0f && bounds.Y <= _surface.Height;
    }

    /// <summary>
    /// Rasterizes the frame into pixels. Safe to call from any thread.
    /// </summary>
    /// <returns>The pixels of the whole surface.</returns>
    public RenderedPixels Rasterize()
    {
        if (_backend is not IPixelRenderBackend backend)
        {
            throw new NotSupportedException("The render backend cannot rasterize into pixels.");
        }

        return backend.RenderToPixels(GetDisplayList(), _surface);
    }

    /// <summary>
    /// Rasterizes the frame into a PNG image. Safe to call from any thread.
    /// </summary>
    /// <returns>The image of the whole surface.</returns>
    public RenderedImage RasterizeToPng() => _backend.RenderToPng(GetDisplayList(), _surface);

    private DisplayList GetDisplayList()
    {
        var page = GetPageDisplayList();

        if (_overlay is null)
        {
            return page;
        }

        var composed = new DisplayList { Fonts = _overlay.Fonts.IsEmpty ? page.Fonts : _overlay.Fonts };
        composed.InsertRange(0, page.Commands);
        composed.PushTransform(RenderTransform2D.Translate(0f, OverscanTop));
        composed.InsertRange(composed.Commands.Count, _overlay.Commands);
        composed.PopTransform();
        return composed;
    }

    private DisplayList GetPageDisplayList()
    {
        var displayList = HtmlRenderer.PaintMediaTimelines(_selection ?? _displayList);

        if (_shiftY == 0f)
        {
            return displayList;
        }

        // the surface stays white where the moved page no longer covers it
        var shifted = new DisplayList { Fonts = displayList.Fonts };
        shifted.FillRect(new RenderRect(0f, 0f, _surface.Width, _surface.Height), RenderColor.White);
        shifted.PushTransform(RenderTransform2D.Translate(0f, _shiftY));
        var anchorDepth = 0;

        foreach (var command in displayList.Commands)
        {
            // fixed boxes take the shift back (nested ones are inside the outermost already)
            if (command is PushViewportAnchorCommand && anchorDepth++ == 0)
            {
                shifted.PushTransform(RenderTransform2D.Translate(0f, -_shiftY));
            }
            else if (command is PopViewportAnchorCommand && --anchorDepth == 0)
            {
                shifted.PopTransform();
            }

            shifted.Add(command);
        }

        shifted.PopTransform();
        return shifted;
    }
}
