namespace AngleSharp.Renderer.Skia;

using SkiaSharp;

/// <summary>
/// The recorded painting of an SVG, in a coordinate space of its natural size. Painting it at the
/// resolution it ends up at is what keeps it sharp, as no sampling of a raster many times larger
/// or smaller can match that.
/// </summary>
internal sealed class SvgVectorImage
{
    // The PNG is only for those reading RenderedImage.Data, as this backend paints the vectors.
    // Rasterizing at natural size only would look sharp at 1:1 but blur once it is scaled up to a
    // larger CSS box; oversampling trades memory for headroom against that case.
    private const float OversampleFactor = 3f;
    private const int MaxRasterDimension = 4096;

    // Rasters of the sizes the image was last painted at. A replay costs about as much as building
    // one and drawing a built one next to nothing, so a repaint (scrolling, animations) or another
    // use of an icon does not pay for the vectors again. How much all images keep together is up
    // to SvgRasterBudget; the limit per image keeps one animating its size from taking all of it.
    private const int MaxCachedRasters = 4;
    private const long BytesPerPixel = 4L;
    private const long MaxCachedRasterPixels = 2048L * 2048L;
    private const int MaxCachedShaders = 4;

    // Sizes and positions of a raster in 1/64 device pixels: exact floats would differ in their
    // last bits for what is the same placement, and never hit the cache.
    private const float PlacementUnitsPerPixel = 64f;

    private readonly Lazy<byte[]> _png;
    private readonly List<(RasterPlacement Placement, SKImage Raster, SvgRasterBudget.Ticket Ticket)> _rasters = [];
    private readonly List<(TileShape Shape, SKShader Shader)> _shaders = [];

    public SvgVectorImage(SKPicture picture, float width, float height)
    {
        Picture = picture;
        Width = width;
        Height = height;
        _png = new Lazy<byte[]>(EncodePng);
    }

    public SKPicture Picture { get; }

    public float Width { get; }

    public float Height { get; }

    public int NaturalWidth => (int)Math.Max(1, Math.Round(Width));

    public int NaturalHeight => (int)Math.Max(1, Math.Round(Height));

    /// <summary>
    /// The image as a PNG, rendered on first use. Empty when that is not possible.
    /// </summary>
    public byte[] Png => _png.Value;

    /// <summary>
    /// A shader of the image scaled to a tile, with the given gaps between the tiles. Skia renders
    /// the tile at the resolution it ends up at. A paint using the shader needs
    /// <see cref="SKFilterQuality.Low"/>, or a tile at a fractional size or position snaps to pixels.
    /// </summary>
    public SKShader CreateShader(float tileWidth, float tileHeight, float gapX, float gapY, SKShaderTileMode tileModeX, SKShaderTileMode tileModeY, SKMatrix localMatrix)
    {
        var shape = new TileShape(tileWidth, tileHeight, gapX, gapY, tileModeX, tileModeY);

        // Skia keeps a rendered tile only for the very same shader object, so a shader made anew
        // per paint would replay the vectors every time: the placement goes around a kept one.
        lock (_shaders)
        {
            var index = _shaders.FindIndex(entry => entry.Shape == shape);

            if (index < 0)
            {
                if (_shaders.Count == MaxCachedShaders)
                {
                    _shaders.RemoveAt(0);
                }

                _shaders.Add((shape, CreateTileShader(shape)));
                index = _shaders.Count - 1;
            }

            return _shaders[index].Shader.WithLocalMatrix(localMatrix);
        }
    }

    /// <summary>
    /// Paints the image into <paramref name="destination"/>.
    /// </summary>
    public void Draw(SKCanvas canvas, SKRect destination)
    {
        if (canvas.QuickReject(destination))
        {
            return;
        }

        if (TryGetPlacement(canvas.TotalMatrix, destination, out var placement, out var originX, out var originY) &&
            GetOrCreateRaster(placement) is { } raster)
        {
            canvas.Save();
            canvas.SetMatrix(SKMatrix.Identity);
            canvas.DrawImage(raster, originX, originY);
            canvas.Restore();
            return;
        }

        var count = canvas.Save();
        canvas.ClipRect(destination, antialias: true);
        canvas.Translate(destination.Left, destination.Top);
        canvas.Scale(destination.Width / Width, destination.Height / Height);
        canvas.DrawPicture(Picture);
        canvas.RestoreToCount(count);
    }

    /// <summary>
    /// Paints the <paramref name="source"/> part of the image (in natural coordinates) into
    /// <paramref name="destination"/>. The clip is not antialiased, so that adjacent parts of an
    /// image have no seams.
    /// </summary>
    public void DrawPart(SKCanvas canvas, SKRect source, SKRect destination)
    {
        var count = canvas.Save();
        canvas.ClipRect(destination);
        canvas.Translate(destination.Left, destination.Top);
        canvas.Scale(destination.Width / source.Width, destination.Height / source.Height);
        canvas.Translate(-source.Left, -source.Top);
        canvas.DrawPicture(Picture);
        canvas.RestoreToCount(count);
    }

    /// <summary>
    /// Where a raster goes on the device: on a whole pixel, with the subpixel position inside the
    /// raster. Only for scaling and moving (a rotated image is replayed), and not for a size that
    /// is better clipped by the canvas than rendered in full.
    /// </summary>
    private static bool TryGetPlacement(SKMatrix matrix, SKRect destination, out RasterPlacement placement, out int originX, out int originY)
    {
        placement = default;
        originX = 0;
        originY = 0;

        if (matrix.SkewX != 0f || matrix.SkewY != 0f || matrix.ScaleX <= 0f || matrix.ScaleY <= 0f ||
            matrix.Persp0 != 0f || matrix.Persp1 != 0f || matrix.Persp2 != 1f)
        {
            return false;
        }

        var device = matrix.MapRect(destination);
        var left = (long)MathF.Round(device.Left * PlacementUnitsPerPixel);
        var top = (long)MathF.Round(device.Top * PlacementUnitsPerPixel);
        var width = (int)MathF.Round(device.Width * PlacementUnitsPerPixel);
        var height = (int)MathF.Round(device.Height * PlacementUnitsPerPixel);

        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var unit = (long)PlacementUnitsPerPixel;
        originX = (int)Math.Floor(left / PlacementUnitsPerPixel);
        originY = (int)Math.Floor(top / PlacementUnitsPerPixel);
        placement = new RasterPlacement((int)(left - (originX * unit)), (int)(top - (originY * unit)), width, height);

        return (long)placement.PixelWidth * placement.PixelHeight <= MaxCachedRasterPixels;
    }

    private SKImage? GetOrCreateRaster(RasterPlacement placement)
    {
        SKImage? raster;
        List<SvgRasterBudget.Ticket> evicted;

        lock (_rasters)
        {
            var index = _rasters.FindIndex(entry => entry.Placement == placement);

            if (index >= 0)
            {
                // the most recently used one goes last, and the first one is dropped
                var entry = _rasters[index];
                _rasters.RemoveAt(index);
                _rasters.Add(entry);
                SvgRasterBudget.Touch(entry.Ticket);
                return entry.Raster;
            }

            raster = Rasterize(placement);

            if (raster is null)
            {
                return null;
            }

            if (_rasters.Count == MaxCachedRasters)
            {
                // not disposed: another thread may be drawing it, the finalizer takes care of it
                SvgRasterBudget.Remove(_rasters[0].Ticket);
                _rasters.RemoveAt(0);
            }

            var ticket = new SvgRasterBudget.Ticket(this, placement.PixelWidth * placement.PixelHeight * BytesPerPixel);
            _rasters.Add((placement, raster, ticket));
            evicted = SvgRasterBudget.Add(ticket);
        }

        foreach (var ticket in evicted)
        {
            if (ticket.Owner.TryGetTarget(out var owner))
            {
                owner.DropRaster(ticket);
            }
        }

        return raster;
    }

    private void DropRaster(SvgRasterBudget.Ticket ticket)
    {
        lock (_rasters)
        {
            _rasters.RemoveAll(entry => entry.Ticket == ticket);
        }
    }

    private SKImage? Rasterize(RasterPlacement placement)
    {
        using var surface = SKSurface.Create(new SKImageInfo(placement.PixelWidth, placement.PixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));

        if (surface is null)
        {
            return null;
        }

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(placement.OffsetX / PlacementUnitsPerPixel, placement.OffsetY / PlacementUnitsPerPixel);
        canvas.Scale(placement.Width / PlacementUnitsPerPixel / Width, placement.Height / PlacementUnitsPerPixel / Height);
        canvas.ClipRect(new SKRect(0f, 0f, Width, Height), antialias: true);
        canvas.DrawPicture(Picture);
        canvas.Flush();
        return surface.Snapshot();
    }

    private SKShader CreateTileShader(TileShape shape)
    {
        var cell = new SKRect(0f, 0f, shape.Width + shape.GapX, shape.Height + shape.GapY);
        using var recorder = new SKPictureRecorder();
        var canvas = recorder.BeginRecording(cell);
        DrawPart(canvas, new SKRect(0f, 0f, Width, Height), new SKRect(0f, 0f, shape.Width, shape.Height));
        using var picture = recorder.EndRecording();
        return picture.ToShader(shape.ModeX, shape.ModeY, cell);
    }

    private byte[] EncodePng()
    {
        try
        {
            var scale = OversampleFactor;
            var maxNatural = Math.Max(Width, Height);

            if (maxNatural * scale > MaxRasterDimension)
            {
                scale = Math.Max(1f, MaxRasterDimension / maxNatural);
            }

            var pixelWidth = Math.Max(1, (int)Math.Round(Width * scale));
            var pixelHeight = Math.Max(1, (int)Math.Round(Height * scale));

            using var bitmap = new SKBitmap(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            bitmap.Erase(SKColors.Transparent);

            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Scale(scale, scale);
                canvas.DrawPicture(Picture);
                canvas.Flush();
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private readonly record struct TileShape(float Width, float Height, float GapX, float GapY, SKShaderTileMode ModeX, SKShaderTileMode ModeY);

    private readonly record struct RasterPlacement(int OffsetX, int OffsetY, int Width, int Height)
    {
        public int PixelWidth => (int)MathF.Ceiling((OffsetX + Width) / PlacementUnitsPerPixel);

        public int PixelHeight => (int)MathF.Ceiling((OffsetY + Height) / PlacementUnitsPerPixel);
    }
}
