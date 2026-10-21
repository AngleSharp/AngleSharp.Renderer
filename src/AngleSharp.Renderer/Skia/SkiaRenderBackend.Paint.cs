using AngleSharp.Renderer.Rendering;

using SkiaSharp;

namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Tiled background/mask images, `border-image`, masks and `backdrop-filter`.
/// </summary>
public sealed partial class SkiaRenderBackend
{
    /// <summary>
    /// State that spans several commands of one render.
    /// </summary>
    private sealed class DrawState(SKSurface surface)
    {
        public SKSurface Surface { get; } = surface;

        /// <summary>Open mask scopes; each one's layers are applied when its scope is popped.</summary>
        public Stack<PushMaskCommand> Masks { get; } = new();
    }

    /// <summary>
    /// A background or mask image (or gradient) sized, positioned and repeated in
    /// <paramref name="area"/>, including the `space` and `round` repetitions a plain image
    /// shader cannot express: one tile is recorded into a picture whose cell includes the spacing,
    /// and the picture is repeated.
    /// </summary>
    private static SKPaint CreateTiledPaint(
        RenderPaint content,
        RenderRect area,
        RenderBackgroundSize size,
        RenderBackgroundPositionComponent positionX,
        RenderBackgroundPositionComponent positionY,
        RenderBackgroundRepeat repeatX,
        RenderBackgroundRepeat repeatY)
    {
        var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        if (area.Width <= 0f || area.Height <= 0f)
        {
            return paint;
        }

        using var image = content is RenderImagePaint imagePaint ? DecodeImage(imagePaint.Image) : null;

        if (content is RenderImagePaint && image is null)
        {
            return paint;
        }

        // An image keeps its aspect ratio; a gradient has no intrinsic size, so `auto`, `cover`
        // and `contain` all make it as large as the area.
        var (tileWidth, tileHeight) = image is not null
            ? ResolveBackgroundImageSize(size, image.Width, image.Height, area.Width, area.Height)
            : ResolveGradientTileSize(size, area.Width, area.Height);

        if (tileWidth <= 0f || tileHeight <= 0f)
        {
            return paint;
        }

        tileWidth = RoundTile(repeatX, tileWidth, area.Width);
        tileHeight = RoundTile(repeatY, tileHeight, area.Height);

        var (originX, gapX, modeX) = PlaceTiles(repeatX, positionX, tileWidth, area.X, area.Width);
        var (originY, gapY, modeY) = PlaceTiles(repeatY, positionY, tileHeight, area.Y, area.Height);

        if (float.IsNaN(originX) || float.IsNaN(originY))
        {
            // `space` with not even one whole tile fitting paints nothing.
            return paint;
        }

        var cell = new SKRect(0f, 0f, tileWidth + gapX, tileHeight + gapY);
        using var recorder = new SKPictureRecorder();
        var tileCanvas = recorder.BeginRecording(cell);
        var tileRect = new SKRect(0f, 0f, tileWidth, tileHeight);

        if (image is not null)
        {
            using var imagePaintForTile = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            tileCanvas.DrawImage(image, tileRect, imagePaintForTile);
        }
        else if (content is RenderGradientPaint gradientPaint)
        {
            using var gradientPaintForTile = CreateGradientPaint(gradientPaint.Gradient, new RenderRect(0f, 0f, tileWidth, tileHeight));
            tileCanvas.DrawRect(tileRect, gradientPaintForTile);
        }

        using var picture = recorder.EndRecording();
        paint.Shader = SKShader.CreatePicture(picture, modeX, modeY, SKMatrix.CreateTranslation(originX, originY), cell);
        return paint;
    }

    private static SKImage? DecodeImage(RenderedImage image)
    {
        using var data = SKData.CreateCopy(image.Data);
        var decoded = SKImage.FromEncodedData(data);
        return decoded is { Width: > 0, Height: > 0 } ? decoded : null;
    }

    private static (float Width, float Height) ResolveGradientTileSize(RenderBackgroundSize size, float areaWidth, float areaHeight)
    {
        if (size.Kind != RenderBackgroundSizeKind.Explicit)
        {
            return (areaWidth, areaHeight);
        }

        var width = size.Width.IsAuto ? areaWidth : size.Width.IsPercentage ? size.Width.Value * areaWidth : size.Width.Value;
        var height = size.Height.IsAuto ? areaHeight : size.Height.IsPercentage ? size.Height.Value * areaHeight : size.Height.Value;
        return (width, height);
    }

    /// <summary>`round` rescales a tile so a whole number of them fills the area.</summary>
    private static float RoundTile(RenderBackgroundRepeat repeat, float tile, float area) =>
        repeat == RenderBackgroundRepeat.Round
            ? area / Math.Max(1f, MathF.Round(area / tile))
            : tile;

    /// <summary>
    /// Where the tile grid starts on one axis, the gap between tiles, and how the shader tiles.
    /// The origin is <see cref="float.NaN"/> when nothing is painted.
    /// </summary>
    private static (float Origin, float Gap, SKShaderTileMode Mode) PlaceTiles(RenderBackgroundRepeat repeat, RenderBackgroundPositionComponent position, float tile, float start, float area)
    {
        var positioned = start + (position.Percentage * (area - tile)) + position.OffsetPixels;

        switch (repeat)
        {
            case RenderBackgroundRepeat.Space:
            {
                var count = MathF.Floor(area / tile);

                if (count < 1f)
                {
                    return (float.NaN, 0f, SKShaderTileMode.Decal);
                }

                // A single tile is simply positioned; more fill the area edge to edge.
                return count < 2f
                    ? (positioned, 0f, SKShaderTileMode.Decal)
                    : (start, (area - (count * tile)) / (count - 1f), SKShaderTileMode.Repeat);
            }
            case RenderBackgroundRepeat.NoRepeat:
                return (positioned, 0f, SKShaderTileMode.Decal);
            default:
                return (positioned, 0f, SKShaderTileMode.Repeat);
        }
    }

    /// <summary>
    /// Draws a nine-slice `border-image`: the four corners stretched into their corner areas, the
    /// four edges and (with `fill`) the middle according to `border-image-repeat`.
    /// </summary>
    private static void DrawBorderImage(SKCanvas canvas, DrawBorderImageCommand command)
    {
        var area = command.Area;
        var borderImage = command.Image;

        if (area.Width <= 0f || area.Height <= 0f)
        {
            return;
        }

        using var image = borderImage.Source switch
        {
            RenderImagePaint imagePaint => DecodeImage(imagePaint.Image),
            RenderGradientPaint gradientPaint => RasterizeGradient(gradientPaint.Gradient, area.Width, area.Height),
            _ => null,
        };

        if (image is null)
        {
            return;
        }

        float imageWidth = image.Width;
        float imageHeight = image.Height;
        var sliceTop = Math.Min(ResolveSlice(borderImage.SliceTop, imageHeight), imageHeight);
        var sliceBottom = Math.Min(ResolveSlice(borderImage.SliceBottom, imageHeight), imageHeight - sliceTop);
        var sliceLeft = Math.Min(ResolveSlice(borderImage.SliceLeft, imageWidth), imageWidth);
        var sliceRight = Math.Min(ResolveSlice(borderImage.SliceRight, imageWidth), imageWidth - sliceLeft);

        // Edge widths that do not fit into the area are scaled down together.
        var widthTop = borderImage.WidthTop;
        var widthRight = borderImage.WidthRight;
        var widthBottom = borderImage.WidthBottom;
        var widthLeft = borderImage.WidthLeft;
        var fit = Math.Min(
            widthLeft + widthRight > area.Width ? area.Width / (widthLeft + widthRight) : 1f,
            widthTop + widthBottom > area.Height ? area.Height / (widthTop + widthBottom) : 1f);
        widthTop *= fit;
        widthRight *= fit;
        widthBottom *= fit;
        widthLeft *= fit;

        float[] destX = [area.X, area.X + widthLeft, area.X + area.Width - widthRight, area.X + area.Width];
        float[] destY = [area.Y, area.Y + widthTop, area.Y + area.Height - widthBottom, area.Y + area.Height];
        float[] sourceX = [0f, sliceLeft, imageWidth - sliceRight, imageWidth];
        float[] sourceY = [0f, sliceTop, imageHeight - sliceBottom, imageHeight];

        // How much the edge parts are scaled across their thickness, which also sizes their tiles.
        var scaleTop = sliceTop > 0f ? widthTop / sliceTop : 1f;
        var scaleBottom = sliceBottom > 0f ? widthBottom / sliceBottom : 1f;
        var scaleLeft = sliceLeft > 0f ? widthLeft / sliceLeft : 1f;
        var scaleRight = sliceRight > 0f ? widthRight / sliceRight : 1f;

        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if (row == 1 && column == 1 && !borderImage.Fill)
                {
                    continue;
                }

                var source = new SKRect(sourceX[column], sourceY[row], sourceX[column + 1], sourceY[row + 1]);
                var destination = new SKRect(destX[column], destY[row], destX[column + 1], destY[row + 1]);

                if (source.Width <= 0f || source.Height <= 0f || destination.Width <= 0f || destination.Height <= 0f)
                {
                    continue;
                }

                // Corners always stretch; edges repeat along their length; the middle uses both.
                var horizontal = column == 1 ? borderImage.RepeatX : RenderBorderImageRepeat.Stretch;
                var vertical = row == 1 ? borderImage.RepeatY : RenderBorderImageRepeat.Stretch;
                var tileScaleX = row == 2 ? scaleBottom : scaleTop;
                var tileScaleY = column == 2 ? scaleRight : scaleLeft;

                DrawImagePart(canvas, image, source, destination, horizontal, vertical, source.Width * tileScaleX, source.Height * tileScaleY, paint);
            }
        }
    }

    private static float ResolveSlice(RenderBorderImageSlice slice, float imageSize) =>
        Math.Max(0f, slice.IsPercentage ? slice.Value * imageSize : slice.Value);

    private static SKImage? RasterizeGradient(RenderGradient gradient, float width, float height)
    {
        var pixelWidth = Math.Max(1, (int)MathF.Ceiling(width));
        var pixelHeight = Math.Max(1, (int)MathF.Ceiling(height));
        using var surface = SKSurface.Create(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul));

        if (surface is null)
        {
            return null;
        }

        using var paint = CreateGradientPaint(gradient, new RenderRect(0f, 0f, width, height));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawRect(new SKRect(0f, 0f, width, height), paint);
        return surface.Snapshot();
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the <paramref name="source"/> part of the image,
    /// stretched or tiled per axis.
    /// </summary>
    private static void DrawImagePart(
        SKCanvas canvas,
        SKImage image,
        SKRect source,
        SKRect destination,
        RenderBorderImageRepeat horizontal,
        RenderBorderImageRepeat vertical,
        float tileWidth,
        float tileHeight,
        SKPaint paint)
    {
        var columns = LayoutBorderImageTiles(horizontal, destination.Left, destination.Width, tileWidth);
        var rows = LayoutBorderImageTiles(vertical, destination.Top, destination.Height, tileHeight);

        canvas.Save();
        canvas.ClipRect(destination);

        foreach (var (x, width) in columns)
        {
            foreach (var (y, height) in rows)
            {
                canvas.DrawImage(image, source, new SKRect(x, y, x + width, y + height), paint);
            }
        }

        canvas.Restore();
    }

    /// <summary>
    /// The tile positions along one axis: one stretched tile, centered repeated tiles, a whole
    /// number of rescaled tiles (`round`), or whole tiles with the leftover space around them
    /// (`space`).
    /// </summary>
    private static List<(float Start, float Size)> LayoutBorderImageTiles(RenderBorderImageRepeat repeat, float start, float length, float tile)
    {
        var tiles = new List<(float Start, float Size)>();

        if (repeat == RenderBorderImageRepeat.Stretch || tile <= 0.01f)
        {
            tiles.Add((start, length));
            return tiles;
        }

        switch (repeat)
        {
            case RenderBorderImageRepeat.Round:
            {
                var count = Math.Max(1, (int)MathF.Round(length / tile));
                var size = length / count;

                for (var index = 0; index < count; index++)
                {
                    tiles.Add((start + (index * size), size));
                }

                break;
            }
            case RenderBorderImageRepeat.Space:
            {
                var count = (int)MathF.Floor(length / tile);

                if (count > 0)
                {
                    var gap = (length - (count * tile)) / (count + 1);

                    for (var index = 0; index < count; index++)
                    {
                        tiles.Add((start + gap + (index * (tile + gap)), tile));
                    }
                }

                break;
            }
            default:
            {
                // Centered, so the partial tiles at both ends are equally large.
                var center = start + (length / 2f);
                var first = center - (tile / 2f) - (MathF.Ceiling(((length / 2f) - (tile / 2f)) / tile) * tile);

                for (var position = first; position < start + length; position += tile)
                {
                    tiles.Add((position, tile));
                }

                break;
            }
        }

        return tiles;
    }

    /// <summary>
    /// Ends a mask scope: the group painted since <see cref="PushMaskCommand"/> keeps only what
    /// the mask layers cover, by drawing them with destination-in onto it.
    /// </summary>
    private static void ApplyMask(SKCanvas canvas, PushMaskCommand command)
    {
        using var destinationIn = new SKPaint { BlendMode = SKBlendMode.DstIn };
        canvas.SaveLayer(destinationIn);

        // Layers are listed topmost first; with the default `mask-composite: add` they simply
        // paint over each other, bottom layer first.
        for (var index = command.Layers.Count - 1; index >= 0; index--)
        {
            var layer = command.Layers[index];
            using var paint = CreateFillPaint(layer.Paint, layer.PaintArea);

            if (layer.UseLuminance)
            {
                paint.ColorFilter = SKColorFilter.CreateLumaColor();
            }

            canvas.DrawRect(ToSkRect(layer.Area), paint);
        }

        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>
    /// `backdrop-filter`: what is already painted behind the rounded rect is read back from the
    /// surface, filtered, and drawn over itself, clipped to the rect. SkiaSharp 2.x has no
    /// backdrop save-layer, so the backdrop is what has reached the surface: content inside a
    /// still-open ancestor layer (an `opacity` group, say) is not part of it.
    /// </summary>
    private static void DrawBackdropFilter(SKCanvas canvas, SKSurface surface, DrawBackdropFilterCommand command)
    {
        using var filter = BuildFilterChain(command.Functions);

        if (filter is null || command.Rect.IsEmpty)
        {
            return;
        }

        // Blurred pixels near the edge sample from outside the rect, so read a padded region.
        var padding = 2f;

        foreach (var function in command.Functions)
        {
            if (function.Kind is RenderFilterFunctionKind.Blur or RenderFilterFunctionKind.DropShadow)
            {
                padding += (function.Amount * 1.5f) + Math.Abs(function.OffsetX) + Math.Abs(function.OffsetY);
            }
        }

        var deviceRect = canvas.TotalMatrix.MapRect(ToSkRect(command.Rect));
        deviceRect.Inflate(padding, padding);
        var bounds = SKRectI.Round(deviceRect);
        var surfaceBounds = new SKRectI(0, 0, surface.Canvas.DeviceClipBounds.Right, surface.Canvas.DeviceClipBounds.Bottom);

        if (!bounds.IntersectsWith(surfaceBounds))
        {
            return;
        }

        bounds.Intersect(surfaceBounds);
        canvas.Flush();
        using var snapshot = surface.Snapshot(bounds);

        if (snapshot is null)
        {
            return;
        }

        canvas.Save();

        using (var clip = new SKRoundRect())
        {
            clip.SetRectRadii(ToSkRect(command.Rect), ToSkPoints(command.Radii));
            canvas.ClipRoundRect(clip, antialias: true);
        }

        // The clip stays in place, but the snapshot is in device pixels.
        canvas.ResetMatrix();
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawImage(snapshot, bounds.Left, bounds.Top, paint);
        canvas.Restore();
    }
}
