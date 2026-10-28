namespace AngleSharp.Renderer.Rendering;

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

    internal RenderedFrame(DisplayList displayList, IRenderBackend backend, RenderViewport surface, int viewportHeight, double scrollTop, int overscanTop, double contentHeight, bool isScrollShiftable, bool canReposition, HitTestMap hitTestMap, float shiftY = 0f)
    {
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
    public bool CanReposition { get; }

    /// <summary>
    /// Gets if CSS transitions or animations are running, i.e., the next frame looks different
    /// even if nothing else changes.
    /// </summary>
    public bool IsAnimating { get; internal set; }

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

        var delta = (float)(ScrollTop - scrollTop);
        return new RenderedFrame(_displayList, _backend, _surface, ViewportHeight, scrollTop, OverscanTop, ContentHeight, IsScrollShiftable, true, HitTestMap.Shift(delta), _shiftY + delta);
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
        if (_shiftY == 0f)
        {
            return _displayList;
        }

        // the surface stays white where the moved page no longer covers it
        var shifted = new DisplayList { Fonts = _displayList.Fonts };
        shifted.FillRect(new RenderRect(0f, 0f, _surface.Width, _surface.Height), RenderColor.White);
        shifted.PushTransform(RenderTransform2D.Translate(0f, _shiftY));
        var anchorDepth = 0;

        foreach (var command in _displayList.Commands)
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
