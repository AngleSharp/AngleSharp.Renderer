using AngleSharp.Renderer.Rendering;
using System.Linq;
using System.Runtime.InteropServices;

using SkiaSharp;

namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Uses SkiaSharp to render a display list.
/// </summary>
/// <remarks>
/// The backend also measures text, so a renderer using it lays out against the same advance
/// widths it paints with.
/// </remarks>
public sealed partial class SkiaRenderBackend : IPixelRenderBackend, ITextMeasurer
{
    private readonly SkiaTextMeasurer _textMeasurer = new();

    /// <inheritdoc />
    public float MeasureWidth(string text, RenderFont font) => _textMeasurer.MeasureWidth(text, font);

    /// <inheritdoc />
    public IReadOnlyList<int> GetClusterBoundaries(string text, RenderFont font) => _textMeasurer.GetClusterBoundaries(text, font);

    /// <inheritdoc />
    public RenderedImage RenderToPng(DisplayList displayList, RenderViewport viewport)
    {
        using var surface = Rasterize(displayList, viewport);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new InvalidOperationException("Unable to encode the rendered image as PNG.");

        return new RenderedImage(data.ToArray(), viewport.Width, viewport.Height, "image/png");
    }

    /// <inheritdoc />
    public RenderedPixels RenderToPixels(DisplayList displayList, RenderViewport viewport)
    {
        var pixels = new byte[viewport.Width * viewport.Height * 4];
        var info = CreateImageInfo(viewport);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            // the surface draws right into the returned buffer, nothing is encoded or copied
            using var surface = SKSurface.Create(info, handle.AddrOfPinnedObject(), info.RowBytes)
                ?? throw new InvalidOperationException("Unable to create a Skia surface.");
            Draw(surface, displayList);
        }
        finally
        {
            handle.Free();
        }

        return new RenderedPixels(pixels, viewport.Width, viewport.Height);
    }

    private static SKSurface Rasterize(DisplayList displayList, RenderViewport viewport)
    {
        var surface = SKSurface.Create(CreateImageInfo(viewport))
            ?? throw new InvalidOperationException("Unable to create a Skia surface.");
        Draw(surface, displayList);
        return surface;
    }

    private static SKImageInfo CreateImageInfo(RenderViewport viewport)
    {
        if (viewport.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(viewport), "Viewport dimensions must be positive.");
        }

        return new SKImageInfo(viewport.Width, viewport.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    }

    private static void Draw(SKSurface surface, DisplayList displayList)
    {
        ArgumentNullException.ThrowIfNull(displayList);

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var state = new DrawState(surface);

        foreach (var command in displayList.Commands)
        {
            DrawCommand(canvas, command, displayList.Fonts, state);
        }
    }

    private static void DrawCommand(SKCanvas canvas, RenderCommand command, FontFaceSet fonts, DrawState state)
    {
        switch (command)
        {
            case DrawBorderImageCommand borderImage:
                DrawBorderImage(canvas, borderImage);
                break;
            case DrawBackdropFilterCommand backdropFilter:
                DrawBackdropFilter(canvas, state.Surface, backdropFilter);
                break;
            case PushMaskCommand pushMask:
                canvas.SaveLayer();
                state.Masks.Push(pushMask);
                break;
            case PopMaskCommand:
                ApplyMask(canvas, state.Masks.Pop());
                break;
            case FillRectCommand fill:
                DrawFillRect(canvas, fill);
                break;
            case StrokeRoundedRectCommand strokeRoundedRect:
                DrawStrokeRoundedRect(canvas, strokeRoundedRect);
                break;
            case FillRingCommand fillRing:
                DrawFillRing(canvas, fillRing);
                break;
            case DrawBoxShadowCommand boxShadow:
                DrawBoxShadow(canvas, boxShadow);
                break;
            case DrawImageCommand image:
                DrawImage(canvas, image);
                break;
            case DrawTextCommand text:
                DrawText(canvas, text, fonts);
                break;
            case DrawTextShadowCommand textShadow:
                DrawTextShadow(canvas, textShadow, fonts);
                break;
            case PushClipCommand pushClip:
                PushClip(canvas, pushClip);
                break;
            case PushClipShapeCommand pushClipShape:
                PushClipShape(canvas, pushClipShape);
                break;
            case PopClipCommand:
                canvas.Restore();
                break;
            case PushTransformCommand pushTransform:
                PushTransform(canvas, pushTransform);
                break;
            case PopTransformCommand:
                canvas.Restore();
                break;
            case PushFilterCommand pushFilter:
                PushFilter(canvas, pushFilter);
                break;
            case PopFilterCommand:
                canvas.Restore();
                break;
            case PushOpacityCommand pushOpacity:
                PushOpacity(canvas, pushOpacity);
                break;
            case PopOpacityCommand:
                canvas.Restore();
                break;
            case PushBlendModeCommand pushBlendMode:
                PushBlendMode(canvas, pushBlendMode);
                break;
            case PopBlendModeCommand:
                canvas.Restore();
                break;
        }
    }

    private static void PushTransform(SKCanvas canvas, PushTransformCommand command)
    {
        canvas.Save();

        var t = command.Transform;
        // SKMatrix's constructor takes (scaleX, skewX, transX, skewY, scaleY, transY, ...) - the
        // same field reordering already used everywhere else in this file that builds an SKMatrix
        // from an a/b/c/d/e/f-style source, not the a/b/c/d/e/f order CSS's own matrix() function
        // (and RenderTransform2D, matching it) uses.
        var matrix = new SKMatrix(t.A, t.C, t.E, t.B, t.D, t.F, 0f, 0f, 1f);
        canvas.Concat(ref matrix);
    }

    private static void PushOpacity(SKCanvas canvas, PushOpacityCommand command)
    {
        var alpha = (byte)Math.Round(Math.Clamp(command.Alpha, 0f, 1f) * 255f);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
        canvas.SaveLayer(paint);
    }

    private static void PushBlendMode(SKCanvas canvas, PushBlendModeCommand command)
    {
        // The layer collects the group in isolation; restoring it composites the group onto the
        // backdrop with the paint's blend mode.
        using var paint = new SKPaint { BlendMode = ToSkiaBlendMode(command.Mode) };
        canvas.SaveLayer(paint);
    }

    private static SKBlendMode ToSkiaBlendMode(RenderBlendMode mode) => mode switch
    {
        RenderBlendMode.Multiply => SKBlendMode.Multiply,
        RenderBlendMode.Screen => SKBlendMode.Screen,
        RenderBlendMode.Overlay => SKBlendMode.Overlay,
        RenderBlendMode.Darken => SKBlendMode.Darken,
        RenderBlendMode.Lighten => SKBlendMode.Lighten,
        RenderBlendMode.ColorDodge => SKBlendMode.ColorDodge,
        RenderBlendMode.ColorBurn => SKBlendMode.ColorBurn,
        RenderBlendMode.HardLight => SKBlendMode.HardLight,
        RenderBlendMode.SoftLight => SKBlendMode.SoftLight,
        RenderBlendMode.Difference => SKBlendMode.Difference,
        RenderBlendMode.Exclusion => SKBlendMode.Exclusion,
        RenderBlendMode.Hue => SKBlendMode.Hue,
        RenderBlendMode.Saturation => SKBlendMode.Saturation,
        RenderBlendMode.Color => SKBlendMode.Color,
        RenderBlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };

    private static void PushFilter(SKCanvas canvas, PushFilterCommand command)
    {
        using var paint = new SKPaint { ImageFilter = BuildFilterChain(command.Functions) };
        canvas.SaveLayer(paint);
    }

    private static SKImageFilter? BuildFilterChain(IReadOnlyList<RenderFilterFunction> functions)
    {
        SKImageFilter? chain = null;

        foreach (var function in functions)
        {
            chain = function.Kind switch
            {
                RenderFilterFunctionKind.Blur =>
                    SKImageFilter.CreateBlur(Math.Max(0f, function.Amount) / 2f, Math.Max(0f, function.Amount) / 2f, chain),
                RenderFilterFunctionKind.DropShadow =>
                    SKImageFilter.CreateDropShadow(function.OffsetX, function.OffsetY, Math.Max(0f, function.Amount) / 2f, Math.Max(0f, function.Amount) / 2f, ToSkColor(function.Color), chain),
                _ => SKImageFilter.CreateColorFilter(SKColorFilter.CreateColorMatrix(BuildFilterColorMatrix(function)), chain),
            };
        }

        return chain;
    }

    // The CSS Filter Effects spec's per-function color matrices, each a 4x5 row-major matrix
    // operating on un-premultiplied 0..1 color components (SKColorFilter.CreateColorMatrix's own
    // convention, already relied on by SvgFilterBuilder's independent feColorMatrix support - these
    // two implementations deliberately do not share code, mirroring how SVG gradients do not share
    // code with the CSS gradient path).
    private static float[] BuildFilterColorMatrix(RenderFilterFunction function) => function.Kind switch
    {
        RenderFilterFunctionKind.Grayscale => Lerp(IdentityColorMatrix, GrayscaleColorMatrix, Math.Clamp(function.Amount, 0f, 1f)),
        RenderFilterFunctionKind.Sepia => Lerp(IdentityColorMatrix, SepiaColorMatrix, Math.Clamp(function.Amount, 0f, 1f)),
        RenderFilterFunctionKind.Saturate => CreateSaturateColorMatrix(Math.Max(0f, function.Amount)),
        RenderFilterFunctionKind.HueRotate => CreateHueRotateColorMatrix(function.Amount),
        RenderFilterFunctionKind.Invert => CreateInvertColorMatrix(Math.Clamp(function.Amount, 0f, 1f)),
        RenderFilterFunctionKind.Brightness => CreateBrightnessColorMatrix(Math.Max(0f, function.Amount)),
        RenderFilterFunctionKind.Contrast => CreateContrastColorMatrix(Math.Max(0f, function.Amount)),
        RenderFilterFunctionKind.Opacity => CreateOpacityColorMatrix(Math.Clamp(function.Amount, 0f, 1f)),
        _ => IdentityColorMatrix,
    };

    private static float[] Lerp(float[] from, float[] to, float t)
    {
        var result = new float[20];

        for (var i = 0; i < 20; i++)
        {
            result[i] = from[i] + ((to[i] - from[i]) * t);
        }

        return result;
    }

    private static float[] CreateSaturateColorMatrix(float s) =>
    [
        0.213f + (0.787f * s), 0.715f - (0.715f * s), 0.072f - (0.072f * s), 0f, 0f,
        0.213f - (0.213f * s), 0.715f + (0.285f * s), 0.072f - (0.072f * s), 0f, 0f,
        0.213f - (0.213f * s), 0.715f - (0.715f * s), 0.072f + (0.928f * s), 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    private static float[] CreateHueRotateColorMatrix(float angleDegrees)
    {
        var radians = angleDegrees * (float)Math.PI / 180f;
        var cos = (float)Math.Cos(radians);
        var sin = (float)Math.Sin(radians);

        return
        [
            0.213f + (cos * 0.787f) - (sin * 0.213f), 0.715f - (cos * 0.715f) - (sin * 0.715f), 0.072f - (cos * 0.072f) + (sin * 0.928f), 0f, 0f,
            0.213f - (cos * 0.213f) + (sin * 0.143f), 0.715f + (cos * 0.285f) + (sin * 0.140f), 0.072f - (cos * 0.072f) - (sin * 0.283f), 0f, 0f,
            0.213f - (cos * 0.213f) - (sin * 0.787f), 0.715f - (cos * 0.715f) + (sin * 0.715f), 0.072f + (cos * 0.928f) + (sin * 0.072f), 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
        ];
    }

    private static float[] CreateInvertColorMatrix(float amount)
    {
        var scale = 1f - (2f * amount);

        return
        [
            scale, 0f, 0f, 0f, amount,
            0f, scale, 0f, 0f, amount,
            0f, 0f, scale, 0f, amount,
            0f, 0f, 0f, 1f, 0f,
        ];
    }

    private static float[] CreateBrightnessColorMatrix(float amount) =>
    [
        amount, 0f, 0f, 0f, 0f,
        0f, amount, 0f, 0f, 0f,
        0f, 0f, amount, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    private static float[] CreateContrastColorMatrix(float amount)
    {
        var offset = 0.5f * (1f - amount);

        return
        [
            amount, 0f, 0f, 0f, offset,
            0f, amount, 0f, 0f, offset,
            0f, 0f, amount, 0f, offset,
            0f, 0f, 0f, 1f, 0f,
        ];
    }

    private static float[] CreateOpacityColorMatrix(float amount) =>
    [
        1f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f, 0f,
        0f, 0f, 0f, amount, 0f,
    ];

    private static readonly float[] IdentityColorMatrix =
    [
        1f, 0f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    private static readonly float[] GrayscaleColorMatrix =
    [
        0.2126f, 0.7152f, 0.0722f, 0f, 0f,
        0.2126f, 0.7152f, 0.0722f, 0f, 0f,
        0.2126f, 0.7152f, 0.0722f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    private static readonly float[] SepiaColorMatrix =
    [
        0.393f, 0.769f, 0.189f, 0f, 0f,
        0.349f, 0.686f, 0.168f, 0f, 0f,
        0.272f, 0.534f, 0.131f, 0f, 0f,
        0f, 0f, 0f, 1f, 0f,
    ];

    private static void PushClip(SKCanvas canvas, PushClipCommand command)
    {
        canvas.Save();

        var rect = new SKRect(
            command.Rect.X,
            command.Rect.Y,
            command.Rect.X + command.Rect.Width,
            command.Rect.Y + command.Rect.Height);

        if (command.Radii.IsZero)
        {
            canvas.ClipRect(rect, SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            using var roundRect = new SKRoundRect();
            roundRect.SetRectRadii(rect, ToSkPoints(command.Radii));
            canvas.ClipRoundRect(roundRect, SKClipOperation.Intersect, antialias: true);
        }
    }

    /// <summary>
    /// `clip-path`'s basic shapes each map to a single `SKPath` built directly from its own
    /// already-resolved geometry (see <see cref="RenderClipShape"/>'s own remarks) - a circle/
    /// ellipse becomes an oval path, `inset()` a rounded-rect path (reusing the exact
    /// `RenderCornerRadii`/`SKRoundRect` machinery `border-radius`/`PushClip` already established),
    /// and `polygon()` a straight-edged path connecting its own vertices in order.
    /// </summary>
    private static void PushClipShape(SKCanvas canvas, PushClipShapeCommand command)
    {
        canvas.Save();

        using var path = new SKPath();

        switch (command.Shape)
        {
            case RenderClipCircle circle:
                path.AddOval(new SKRect(
                    circle.CenterX - circle.Radius,
                    circle.CenterY - circle.Radius,
                    circle.CenterX + circle.Radius,
                    circle.CenterY + circle.Radius));
                break;

            case RenderClipEllipse ellipse:
                path.AddOval(new SKRect(
                    ellipse.CenterX - ellipse.RadiusX,
                    ellipse.CenterY - ellipse.RadiusY,
                    ellipse.CenterX + ellipse.RadiusX,
                    ellipse.CenterY + ellipse.RadiusY));
                break;

            case RenderClipInset inset:
            {
                var rect = new SKRect(
                    inset.Rect.X,
                    inset.Rect.Y,
                    inset.Rect.X + inset.Rect.Width,
                    inset.Rect.Y + inset.Rect.Height);

                if (inset.Radii.IsZero)
                {
                    path.AddRect(rect);
                }
                else
                {
                    using var roundRect = new SKRoundRect();
                    roundRect.SetRectRadii(rect, ToSkPoints(inset.Radii));
                    path.AddRoundRect(roundRect);
                }

                break;
            }

            case RenderClipPolygon polygon when polygon.Points.Count > 0:
                path.MoveTo(polygon.Points[0].X, polygon.Points[0].Y);

                for (var i = 1; i < polygon.Points.Count; i++)
                {
                    path.LineTo(polygon.Points[i].X, polygon.Points[i].Y);
                }

                path.Close();
                break;
        }

        canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
    }

    private static void DrawBoxShadow(SKCanvas canvas, DrawBoxShadowCommand command)
    {
        var shadow = command.Shadow;

        if (command.BorderBoxRect.IsEmpty || shadow.Color.A == 0)
        {
            return;
        }

        var borderBoxRect = new SKRect(
            command.BorderBoxRect.X,
            command.BorderBoxRect.Y,
            command.BorderBoxRect.X + command.BorderBoxRect.Width,
            command.BorderBoxRect.Y + command.BorderBoxRect.Height);

        using var borderBoxRRect = new SKRoundRect();
        borderBoxRRect.SetRectRadii(borderBoxRect, ToSkPoints(command.BorderBoxRadii));

        using var paint = new SKPaint
        {
            Color = ToSkColor(shadow.Color),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        if (shadow.BlurRadius > 0f)
        {
            // Browsers commonly derive the Gaussian sigma as half the CSS blur radius.
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadow.BlurRadius / 2f);
        }

        canvas.Save();

        if (shadow.Inset)
        {
            // Per spec, an inset shadow is clipped to the border box, then painted as the area
            // between a rect well outside the box and the spread/offset inner rect - i.e. the
            // "ring" that is inside the border box but outside the (shrunk, moved) inner shape.
            canvas.ClipRoundRect(borderBoxRRect, SKClipOperation.Intersect, antialias: true);

            using var innerRRect = new SKRoundRect(borderBoxRRect);
            ApplySpread(innerRRect, -shadow.SpreadRadius);
            innerRRect.SetRectRadii(OffsetRect(innerRRect.Rect, shadow.OffsetX, shadow.OffsetY), innerRRect.Radii);

            // A generous fixed outset keeps the even-odd path's outer boundary outside the
            // blurred region in every direction, regardless of blur radius, spread, or offset.
            var outerPadding = Math.Abs(shadow.OffsetX) + Math.Abs(shadow.OffsetY) + shadow.BlurRadius + Math.Abs(shadow.SpreadRadius) + 32f;
            var outerRect = SKRect.Inflate(borderBoxRect, outerPadding, outerPadding);

            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            path.AddRect(outerRect, SKPathDirection.Clockwise);
            path.AddRoundRect(innerRRect, SKPathDirection.Clockwise);

            canvas.DrawPath(path, paint);
        }
        else
        {
            // Clip OUT the border box's own shape so a transparent-background box does not show
            // the shadow bleeding through its own interior, per spec.
            canvas.ClipRoundRect(borderBoxRRect, SKClipOperation.Difference, antialias: true);

            using var shadowRRect = new SKRoundRect(borderBoxRRect);
            ApplySpread(shadowRRect, shadow.SpreadRadius);
            shadowRRect.SetRectRadii(OffsetRect(shadowRRect.Rect, shadow.OffsetX, shadow.OffsetY), shadowRRect.Radii);

            canvas.DrawRoundRect(shadowRRect, paint);
        }

        canvas.Restore();
    }

    /// <summary>
    /// Grows or shrinks a rounded rect by a `box-shadow` spread amount, which may be negative
    /// (shrink) - routed to <see cref="SKRoundRect.Inflate(float, float)"/> or
    /// <see cref="SKRoundRect.Deflate(float, float)"/> explicitly, since neither is verified to
    /// accept a negative argument as "the other operation".
    /// </summary>
    private static void ApplySpread(SKRoundRect rrect, float spread)
    {
        if (spread >= 0f)
        {
            rrect.Inflate(spread, spread);
        }
        else
        {
            rrect.Deflate(-spread, -spread);
        }
    }

    private static SKRect OffsetRect(SKRect rect, float dx, float dy) =>
        new(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);

    private static void DrawFillRect(SKCanvas canvas, FillRectCommand command)
    {
        if (command.Rect.IsEmpty)
        {
            return;
        }

        using var paint = CreateFillPaint(command.Paint, command.PaintArea ?? command.Rect);

        var rect = new SKRect(
            command.Rect.X,
            command.Rect.Y,
            command.Rect.X + command.Rect.Width,
            command.Rect.Y + command.Rect.Height);

        if (command.Radii.IsZero)
        {
            canvas.DrawRect(rect, paint);
        }
        else
        {
            using var roundRect = new SKRoundRect();
            roundRect.SetRectRadii(rect, ToSkPoints(command.Radii));
            canvas.DrawRoundRect(roundRect, paint);
        }
    }

    private static void DrawFillRing(SKCanvas canvas, FillRingCommand command)
    {
        if (command.Outer.IsEmpty || command.Color.A == 0)
        {
            return;
        }

        using var outer = new SKRoundRect();
        outer.SetRectRadii(ToSkRect(command.Outer), ToSkPoints(command.OuterRadii));
        using var paint = new SKPaint { Color = ToSkColor(command.Color), IsAntialias = true, Style = SKPaintStyle.Fill };

        if (command.Inner.Width <= 0f || command.Inner.Height <= 0f)
        {
            canvas.DrawRoundRect(outer, paint);
            return;
        }

        using var inner = new SKRoundRect();
        inner.SetRectRadii(ToSkRect(command.Inner), ToSkPoints(command.InnerRadii));
        canvas.DrawRoundRectDifference(outer, inner, paint);
    }

    private static SKRect ToSkRect(RenderRect rect) =>
        new(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);

    private static void DrawStrokeRoundedRect(SKCanvas canvas, StrokeRoundedRectCommand command)
    {
        if (command.Rect.IsEmpty || command.Color.A == 0 || command.StrokeWidth <= 0f)
        {
            return;
        }

        var rect = new SKRect(
            command.Rect.X,
            command.Rect.Y,
            command.Rect.X + command.Rect.Width,
            command.Rect.Y + command.Rect.Height);

        using var roundRect = new SKRoundRect();
        roundRect.SetRectRadii(rect, ToSkPoints(command.Radii));

        using var paint = new SKPaint
        {
            Color = ToSkColor(command.Color),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = command.StrokeWidth,
        };

        canvas.DrawRoundRect(roundRect, paint);
    }

    private static SKPoint[] ToSkPoints(RenderCornerRadii radii) =>
    [
        new SKPoint(radii.TopLeftX, radii.TopLeftY),
        new SKPoint(radii.TopRightX, radii.TopRightY),
        new SKPoint(radii.BottomRightX, radii.BottomRightY),
        new SKPoint(radii.BottomLeftX, radii.BottomLeftY),
    ];

    private static SKPaint CreateFillPaint(RenderPaint paint, RenderRect rect)
    {
        return paint switch
        {
            RenderColorPaint colorPaint => new SKPaint
            {
                Color = ToSkColor(colorPaint.Color),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            },
            RenderGradientPaint { Tile: { } tile } gradientPaint => CreateTiledPaint(gradientPaint, rect, tile.Size, tile.PositionX, tile.PositionY, tile.RepeatX, tile.RepeatY),
            RenderGradientPaint gradientPaint => CreateGradientPaint(gradientPaint.Gradient, rect),
            RenderImagePaint imagePaint when imagePaint.EffectiveRepeatX is RenderBackgroundRepeat.Space or RenderBackgroundRepeat.Round ||
                                             imagePaint.EffectiveRepeatY is RenderBackgroundRepeat.Space or RenderBackgroundRepeat.Round =>
                CreateTiledPaint(imagePaint, rect, imagePaint.Size, imagePaint.PositionX, imagePaint.PositionY, imagePaint.EffectiveRepeatX, imagePaint.EffectiveRepeatY),
            RenderImagePaint imagePaint => CreateImagePaint(imagePaint, rect),
            _ => throw new NotSupportedException($"Unsupported paint type: {paint.GetType().Name}"),
        };
    }

    /// <summary>
    /// Builds a `background-image: url(...)` paint as a tiled/positioned image shader, resolving
    /// <see cref="RenderImagePaint.Size"/> and the position components against <paramref name="rect"/>
    /// - the box's own final geometry, not known when the paint was constructed (see
    /// <see cref="RenderImagePaint"/>'s own remarks). This mirrors <c>SvgPatternBuilder</c>'s
    /// tile-to-user-space matrix technique: build the shader in the image's own natural pixel space,
    /// then let a constructor-time <see cref="SKMatrix"/> position and scale it - not the inverse
    /// (the same "local space maps directly into world space" behavior already verified for the
    /// gradient shaders above).
    /// </summary>
    private static SKPaint CreateImagePaint(RenderImagePaint imagePaint, RenderRect rect)
    {
        var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        if (rect.Width <= 0f || rect.Height <= 0f)
        {
            return paint;
        }

        var image = DecodeImage(imagePaint.Image);

        if (image is null)
        {
            return paint;
        }

        var (drawWidth, drawHeight) = ResolveBackgroundImageSize(imagePaint.Size, image.Width, image.Height, rect.Width, rect.Height);

        if (drawWidth <= 0f || drawHeight <= 0f)
        {
            return paint;
        }

        var originX = rect.X + (imagePaint.PositionX.Percentage * (rect.Width - drawWidth)) + imagePaint.PositionX.OffsetPixels;
        var originY = rect.Y + (imagePaint.PositionY.Percentage * (rect.Height - drawHeight)) + imagePaint.PositionY.OffsetPixels;

        var localMatrix = new SKMatrix(
            drawWidth / image.Width, 0f, originX,
            0f, drawHeight / image.Height, originY,
            0f, 0f, 1f);

        // `no-repeat` uses Decal (transparent outside the single placed tile), not Clamp - Clamp
        // would smear the image's edge pixels across the rest of the box instead of leaving it
        // uncovered, which is what CSS actually specifies for a non-repeating axis.
        var tileX = imagePaint.RepeatX ? SKShaderTileMode.Repeat : SKShaderTileMode.Decal;
        var tileY = imagePaint.RepeatY ? SKShaderTileMode.Repeat : SKShaderTileMode.Decal;

        paint.Shader = SKShader.CreateImage(image, tileX, tileY, localMatrix);
        return paint;
    }

    /// <summary>
    /// Resolves a `background-size` value against the image's natural pixel size and the box's
    /// rendered rect, matching the CSS `cover`/`contain`/explicit-axis sizing algorithms.
    /// </summary>
    private static (float Width, float Height) ResolveBackgroundImageSize(RenderBackgroundSize size, int naturalWidth, int naturalHeight, float boxWidth, float boxHeight)
    {
        switch (size.Kind)
        {
            case RenderBackgroundSizeKind.Cover:
            {
                var scale = Math.Max(boxWidth / naturalWidth, boxHeight / naturalHeight);
                return (naturalWidth * scale, naturalHeight * scale);
            }
            case RenderBackgroundSizeKind.Contain:
            {
                var scale = Math.Min(boxWidth / naturalWidth, boxHeight / naturalHeight);
                return (naturalWidth * scale, naturalHeight * scale);
            }
            case RenderBackgroundSizeKind.Explicit:
            {
                var aspect = (float)naturalWidth / naturalHeight;
                float? width = size.Width.IsAuto ? null : size.Width.IsPercentage ? size.Width.Value * boxWidth : size.Width.Value;
                float? height = size.Height.IsAuto ? null : size.Height.IsPercentage ? size.Height.Value * boxHeight : size.Height.Value;

                if (width is null && height is null)
                {
                    return (naturalWidth, naturalHeight);
                }

                width ??= height!.Value * aspect;
                height ??= width.Value / aspect;

                return (width.Value, height.Value);
            }
            default:
                return (naturalWidth, naturalHeight);
        }
    }

    private static SKPaint CreateGradientPaint(RenderGradient gradient, RenderRect rect)
    {
        var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        var centerX = rect.X + (gradient.CenterX * rect.Width);
        var centerY = rect.Y + (gradient.CenterY * rect.Height);
        var (unscaledRx, unscaledRy) = gradient.Kind == RenderGradientKind.Radial
            ? ComputeRadialRadii(gradient, rect, centerX, centerY)
            : (0f, 0f);
        var lineLength = gradient.Kind == RenderGradientKind.Linear ? ComputeGradientLineLength(gradient, rect) : 0f;

        // An absolute-length stop position ("red 10px") only becomes a fraction once the
        // gradient's own rendered geometry is known - the gradient line's length for linear, the
        // resolved (pre-repeat-scale) radius for radial. Conic stops use angles, never lengths.
        var referenceLength = gradient.Kind switch
        {
            RenderGradientKind.Linear => lineLength,
            RenderGradientKind.Radial => Math.Max(unscaledRx, unscaledRy),
            _ => 0f,
        };

        var (colors, rawPositions) = ResolveGradientStops(gradient.Stops, referenceLength);

        var tileMode = gradient.Repeating ? SKShaderTileMode.Repeat : SKShaderTileMode.Clamp;

        // A repeating-*-gradient's defined stops are one period of an infinitely repeated
        // pattern. Rescaling every position by the last stop's position makes that period fill
        // the shader's whole [0,1] domain; shrinking the shader's own geometric extent by the
        // same factor (see each Create*Shader below) then gives Repeat room to tile the rest.
        // This assumes the first stop is at/near 0%, the overwhelmingly common case - a repeating
        // gradient whose first stop is well past 0% renders its period at the right cadence but
        // not anchored at the exact original offset.
        var repeatScale = 1f;
        var positions = rawPositions;

        if (gradient.Repeating && rawPositions.Length > 0)
        {
            var maxPosition = rawPositions.Max();

            if (maxPosition > 0f && maxPosition < 1f)
            {
                repeatScale = maxPosition;
                positions = rawPositions.Select(position => position / maxPosition).ToArray();
            }
        }

        paint.Shader = gradient.Kind switch
        {
            RenderGradientKind.Linear => CreateLinearGradientShader(gradient, centerX, centerY, lineLength, colors, positions, tileMode, repeatScale),
            RenderGradientKind.Radial => CreateRadialGradientShader(centerX, centerY, unscaledRx, unscaledRy, colors, positions, tileMode, repeatScale),
            RenderGradientKind.Conic => CreateConicGradientShader(gradient, centerX, centerY, colors, positions, tileMode, repeatScale),
            _ => throw new NotSupportedException($"Unsupported gradient kind: {gradient.Kind}"),
        };

        return paint;
    }

    /// <summary>
    /// The length of the gradient line: it passes through the center of the box at the
    /// gradient's angle and ends where the perpendicular lines through the corners cross it
    /// (CSS Images 3 §3.1.1), e.g. the height of the box for `to bottom`.
    /// </summary>
    private static float ComputeGradientLineLength(RenderGradient gradient, RenderRect rect)
    {
        var angle = ((gradient.AngleDegrees % 360f) + 360f) % 360f * (Math.PI / 180d);
        return (float)(Math.Abs(rect.Width * Math.Cos(angle)) + Math.Abs(rect.Height * Math.Sin(angle)));
    }

    /// <summary>
    /// Resolves the stops to colors and positions in [0, 1] the way CSS does (CSS Images 3
    /// §3.5.3): a position never precedes the one before it, and stops without one are spread
    /// evenly between their neighbours. Colors are interpolated premultiplied by alpha, so a fully
    /// transparent stop takes the color of its neighbours on either side (fading `white` to
    /// `transparent` must not pass through grey, as `transparent` is transparent black).
    /// </summary>
    private static (SKColor[] Colors, float[] Positions) ResolveGradientStops(IReadOnlyList<RenderGradientStop> stops, float referenceLength)
    {
        var count = stops.Count;
        var positions = new float?[count];

        for (var i = 0; i < count; i++)
        {
            var stop = stops[i];

            if (stop.IsAuto)
            {
                positions[i] = i == 0 ? 0f : i == count - 1 ? 1f : null;
            }
            else if (stop.AbsolutePositionPixels is { } pixels)
            {
                positions[i] = referenceLength > 0f ? pixels / referenceLength : stop.Position;
            }
            else
            {
                positions[i] = stop.Position + (referenceLength > 0f ? stop.OffsetPixels / referenceLength : 0f);
            }
        }

        var max = float.NegativeInfinity;

        for (var i = 0; i < count; i++)
        {
            if (positions[i] is { } position)
            {
                max = Math.Max(max, position);
                positions[i] = max;
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (positions[i] is not null)
            {
                continue;
            }

            var end = i;

            while (positions[end] is null)
            {
                end++;
            }

            var from = positions[i - 1]!.Value;
            var to = positions[end]!.Value;

            for (var j = i; j < end; j++)
            {
                positions[j] = from + ((to - from) * (j - i + 1) / (end - i + 1));
            }
        }

        var colors = new List<SKColor>(count + 2);
        var resolved = new List<float>(count + 2);

        for (var i = 0; i < count; i++)
        {
            var color = stops[i].Color;
            var position = Math.Clamp(positions[i]!.Value, 0f, 1f);

            if (color.A == 0)
            {
                var before = i > 0 ? stops[i - 1].Color : color;
                var after = i < count - 1 ? stops[i + 1].Color : color;
                colors.Add(new SKColor(before.R, before.G, before.B, 0));
                resolved.Add(position);

                if (before != after)
                {
                    colors.Add(new SKColor(after.R, after.G, after.B, 0));
                    resolved.Add(position);
                }

                continue;
            }

            colors.Add(new SKColor(color.R, color.G, color.B, color.A));
            resolved.Add(position);
        }

        return (colors.ToArray(), resolved.ToArray());
    }

    private static SKShader CreateLinearGradientShader(RenderGradient gradient, float centerX, float centerY, float lineLength, SKColor[] colors, float[] positions, SKShaderTileMode tileMode, float repeatScale)
    {
        var halfDiagonal = lineLength / 2f;
        var radians = (gradient.AngleDegrees % 360f + 360f) % 360f;
        var angle = radians * (Math.PI / 180d);
        var dx = (float)Math.Cos(angle);
        var dy = (float)Math.Sin(angle);

        var start = new SKPoint(centerX - (dx * halfDiagonal), centerY - (dy * halfDiagonal));
        var fullEnd = new SKPoint(centerX + (dx * halfDiagonal), centerY + (dy * halfDiagonal));
        var end = new SKPoint(start.X + ((fullEnd.X - start.X) * repeatScale), start.Y + ((fullEnd.Y - start.Y) * repeatScale));

        return SKShader.CreateLinearGradient(start, end, colors, positions, tileMode);
    }

    private static SKShader CreateRadialGradientShader(float centerX, float centerY, float unscaledRx, float unscaledRy, SKColor[] colors, float[] positions, SKShaderTileMode tileMode, float repeatScale)
    {
        var rx = Math.Max(0.0001f, unscaledRx * repeatScale);
        var ry = Math.Max(0.0001f, unscaledRy * repeatScale);

        if (Math.Abs(rx - ry) < 0.01f)
        {
            // A circle - no elliptical distortion needed, so the plain center+radius overload
            // (unambiguous, no matrix semantics to get backwards) is enough.
            return SKShader.CreateRadialGradient(new SKPoint(centerX, centerY), rx, colors, positions, tileMode);
        }

        // An ellipse: build the gradient as a unit circle at the origin, then use the
        // constructor-time local-matrix overload to map it onto an ellipse of the right size at
        // the right position. Verified empirically (not just by API docs) that this matrix maps
        // the shader's local space directly into world space - i.e. this scales/positions the
        // visible gradient exactly as constructed, not its inverse.
        var matrix = new SKMatrix(rx, 0f, centerX, 0f, ry, centerY, 0f, 0f, 1f);
        return SKShader.CreateRadialGradient(new SKPoint(0f, 0f), 1f, colors, positions, tileMode, matrix);
    }

    /// <summary>
    /// Resolves a radial gradient's ending-shape radii from its <see cref="RenderGradientSizeKind"/>,
    /// matching the CSS `&lt;size&gt;` keyword definitions (farthest-corner is the CSS default).
    /// </summary>
    private static (float Rx, float Ry) ComputeRadialRadii(RenderGradient gradient, RenderRect rect, float centerX, float centerY)
    {
        if (gradient.SizeKind == RenderGradientSizeKind.Explicit && gradient.ExplicitRadiusX is { } explicitX)
        {
            var explicitY = gradient.ExplicitRadiusY ?? explicitX;
            return gradient.IsCircle ? (explicitX, explicitX) : (explicitX, explicitY);
        }

        var nearestX = Math.Min(Math.Abs(centerX - rect.X), Math.Abs((rect.X + rect.Width) - centerX));
        var farthestX = Math.Max(Math.Abs(centerX - rect.X), Math.Abs((rect.X + rect.Width) - centerX));
        var nearestY = Math.Min(Math.Abs(centerY - rect.Y), Math.Abs((rect.Y + rect.Height) - centerY));
        var farthestY = Math.Max(Math.Abs(centerY - rect.Y), Math.Abs((rect.Y + rect.Height) - centerY));

        if (gradient.IsCircle)
        {
            var radius = gradient.SizeKind switch
            {
                RenderGradientSizeKind.ClosestSide => Math.Min(nearestX, nearestY),
                RenderGradientSizeKind.FarthestSide => Math.Max(farthestX, farthestY),
                RenderGradientSizeKind.ClosestCorner => (float)Math.Sqrt((nearestX * nearestX) + (nearestY * nearestY)),
                _ => (float)Math.Sqrt((farthestX * farthestX) + (farthestY * farthestY)),
            };

            return (radius, radius);
        }

        return gradient.SizeKind switch
        {
            RenderGradientSizeKind.ClosestSide => (nearestX, nearestY),
            RenderGradientSizeKind.FarthestSide => (farthestX, farthestY),
            RenderGradientSizeKind.ClosestCorner => EllipseThroughCorner(nearestX, nearestY, rect.Width, rect.Height),
            _ => EllipseThroughCorner(farthestX, farthestY, rect.Width, rect.Height),
        };
    }

    /// <summary>
    /// The semi-axes of an ellipse that shares the box's aspect ratio and passes through a corner
    /// offset by (<paramref name="dx"/>, <paramref name="dy"/>) from its center - the CSS
    /// closest-corner/farthest-corner ellipse sizing algorithm.
    /// </summary>
    private static (float Rx, float Ry) EllipseThroughCorner(float dx, float dy, float boxWidth, float boxHeight)
    {
        if (boxWidth <= 0f || boxHeight <= 0f)
        {
            return (Math.Abs(dx), Math.Abs(dy));
        }

        var aspect = boxWidth / boxHeight;
        var ry = (float)Math.Sqrt(((double)dx * dx / (aspect * aspect)) + ((double)dy * dy));
        var rx = aspect * ry;

        return (rx, ry);
    }

    private static SKShader CreateConicGradientShader(RenderGradient gradient, float centerX, float centerY, SKColor[] colors, float[] positions, SKShaderTileMode tileMode, float repeatScale)
    {
        // Skia's sweep gradient silently degenerates to a zero-width (solid first-color) span
        // once endAngle exceeds 360 - verified empirically, it does not treat e.g. [270,630] as
        // "a full revolution starting at 270". So the shader's own angle span always stays a
        // plain [0, 360*repeatScale], and CSS's "from <angle>" rotation - plus the fact that CSS
        // conic-gradient's 0deg points up while Skia's sweep 0deg points right, both clockwise
        // (also verified empirically) - is applied via the constructor-time rotation matrix
        // instead, the same "shader-local-space maps directly into world-space" mechanism already
        // used for elliptical radial gradients.
        var rotation = SKMatrix.CreateRotationDegrees(gradient.AngleDegrees - 90f, centerX, centerY);
        var endAngle = 360f * repeatScale;

        return SKShader.CreateSweepGradient(new SKPoint(centerX, centerY), colors, positions, tileMode, 0f, endAngle, rotation);
    }

    private static void DrawImage(SKCanvas canvas, DrawImageCommand command)
    {
        if (command.Image.Data.Length == 0 || command.Rect.IsEmpty)
        {
            return;
        }

        var image = DecodeImage(command.Image);
        if (image is null)
        {
            return;
        }

        var rect = new SKRect(
            command.Rect.X,
            command.Rect.Y,
            command.Rect.X + command.Rect.Width,
            command.Rect.Y + command.Rect.Height);

        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawImage(image, rect, paint);
    }

    private static void DrawText(SKCanvas canvas, DrawTextCommand command, FontFaceSet fonts)
    {
        var font = new RenderFont(
            command.FontFamily,
            command.FontSize,
            command.FontWeight,
            command.IsItalic,
            command.LetterSpacing,
            fonts,
            command.FontStretch,
            command.IsRightToLeft,
            command.FontFeatures,
            command.Language,
            command.ContextBefore,
            command.ContextAfter);

        using var paint = SkiaTextShaping.CreateTextPaint(font);
        paint.Color = ToSkColor(command.Color);

        SkiaTextShaping.DrawText(canvas, paint, command.Text, command.X, command.Y, command.LetterSpacing, font);

        if (command.Underline || command.StrikeThrough)
        {
            using var decorationPaint = new SKPaint
            {
                Color = ToSkColor(command.DecorationColor),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, command.FontSize / 14f),
            };

            var textWidth = SkiaTextShaping.MeasureTextWidth(paint, command.Text, command.LetterSpacing, font);

            if (command.Underline)
            {
                var underlineY = command.Y + Math.Max(1f, command.FontSize * 0.12f);
                DrawDecorationLine(canvas, decorationPaint, command.X, underlineY, textWidth, command.DecorationStyle);
            }

            if (command.StrikeThrough)
            {
                var strikeY = command.Y - (command.FontSize * 0.32f);
                DrawDecorationLine(canvas, decorationPaint, command.X, strikeY, textWidth, command.DecorationStyle);
            }
        }
    }

    private static void DrawTextShadow(SKCanvas canvas, DrawTextShadowCommand command, FontFaceSet fonts)
    {
        var font = new RenderFont(
            command.FontFamily,
            command.FontSize,
            command.FontWeight,
            command.IsItalic,
            command.LetterSpacing,
            fonts,
            command.FontStretch,
            command.IsRightToLeft,
            command.FontFeatures,
            command.Language,
            command.ContextBefore,
            command.ContextAfter);

        using var paint = SkiaTextShaping.CreateTextPaint(font);
        paint.Color = ToSkColor(command.Color);

        if (command.BlurRadius > 0f)
        {
            // Matches the same CSS-blur-radius-to-Gaussian-sigma approximation used for box-shadow.
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, command.BlurRadius / 2f);
        }

        SkiaTextShaping.DrawText(canvas, paint, command.Text, command.X, command.Y, command.LetterSpacing, font);
    }

    private static void DrawDecorationLine(SKCanvas canvas, SKPaint paint, float x, float y, float width, RenderTextDecorationStyle style)
    {
        switch (style)
        {
            case RenderTextDecorationStyle.Wavy:
                DrawWavyLine(canvas, paint, x, y, width);
                break;
            case RenderTextDecorationStyle.Dashed:
                DrawPatternedLine(canvas, paint, x, y, width, dashLength: 6f, gapLength: 4f);
                break;
            case RenderTextDecorationStyle.Dotted:
                DrawPatternedLine(canvas, paint, x, y, width, dashLength: 1f, gapLength: 4f);
                break;
            default:
                canvas.DrawLine(x, y, x + width, y, paint);
                break;
        }
    }

    private static void DrawWavyLine(SKCanvas canvas, SKPaint paint, float x, float y, float width)
    {
        using var path = new SKPath();
        var wavelength = 8f;
        var amplitude = 1.5f;
        var steps = Math.Max(1, (int)Math.Ceiling(width / 2f));

        path.MoveTo(x, y);

        for (var index = 1; index <= steps; index++)
        {
            var position = Math.Min(width, index * 2f);
            var phase = position / wavelength * MathF.PI * 2f;
            path.LineTo(x + position, y + MathF.Sin(phase) * amplitude);
        }

        canvas.DrawPath(path, paint);
    }

    private static void DrawPatternedLine(SKCanvas canvas, SKPaint paint, float x, float y, float width, float dashLength, float gapLength)
    {
        var cursor = x;
        var end = x + width;

        while (cursor < end)
        {
            var segmentEnd = Math.Min(cursor + dashLength, end);
            canvas.DrawLine(cursor, y, segmentEnd, y, paint);
            cursor = segmentEnd + gapLength;
        }
    }

    private static SKColor ToSkColor(RenderColor color) => new(color.R, color.G, color.B, color.A);

}