using AngleSharp.Renderer.Rendering;
using System.Linq;

using SkiaSharp;

namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Uses SkiaSharp to render a display list.
/// </summary>
/// <remarks>
/// The backend also measures text, so a renderer using it lays out against the same advance
/// widths it paints with.
/// </remarks>
public sealed class SkiaRenderBackend : IRenderBackend, ITextMeasurer
{
    private readonly SkiaTextMeasurer _textMeasurer = new();

    /// <inheritdoc />
    public float MeasureWidth(string text, RenderFont font) => _textMeasurer.MeasureWidth(text, font);

    /// <inheritdoc />
    public RenderedImage RenderToPng(DisplayList displayList, RenderViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(displayList);

        if (viewport.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(viewport), "Viewport dimensions must be positive.");
        }

        var info = new SKImageInfo(viewport.Width, viewport.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Unable to create a Skia surface.");

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        foreach (var command in displayList.Commands)
        {
            DrawCommand(canvas, command, displayList.Fonts);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new InvalidOperationException("Unable to encode the rendered image as PNG.");

        return new RenderedImage(data.ToArray(), viewport.Width, viewport.Height, "image/png");
    }

    private static void DrawCommand(SKCanvas canvas, RenderCommand command, FontFaceSet fonts)
    {
        switch (command)
        {
            case FillRectCommand fill:
                DrawFillRect(canvas, fill);
                break;
            case StrokeRoundedRectCommand strokeRoundedRect:
                DrawStrokeRoundedRect(canvas, strokeRoundedRect);
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
        }
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

        using var paint = CreateFillPaint(command.Paint, command.Rect);

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
            RenderGradientPaint gradientPaint => CreateGradientPaint(gradientPaint.Gradient, rect),
            _ => throw new NotSupportedException($"Unsupported paint type: {paint.GetType().Name}"),
        };
    }

    private static SKPaint CreateGradientPaint(RenderGradient gradient, RenderRect rect)
    {
        var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        var colors = gradient.Stops.Select(stop => new SKColor(stop.Color.R, stop.Color.G, stop.Color.B, stop.Color.A)).ToArray();

        var centerX = rect.X + (gradient.CenterX * rect.Width);
        var centerY = rect.Y + (gradient.CenterY * rect.Height);
        var diagonal = (float)Math.Sqrt((rect.Width * rect.Width) + (rect.Height * rect.Height));
        var (unscaledRx, unscaledRy) = gradient.Kind == RenderGradientKind.Radial
            ? ComputeRadialRadii(gradient, rect, centerX, centerY)
            : (0f, 0f);

        // An absolute-length stop position ("red 10px") only becomes a fraction once the
        // gradient's own rendered geometry is known - the gradient line's length for linear, the
        // resolved (pre-repeat-scale) radius for radial. Conic stops use angles, never lengths.
        var referenceLength = gradient.Kind switch
        {
            RenderGradientKind.Linear => diagonal,
            RenderGradientKind.Radial => Math.Max(unscaledRx, unscaledRy),
            _ => 0f,
        };

        var rawPositions = gradient.Stops
            .Select(stop => stop.AbsolutePositionPixels is { } pixels && referenceLength > 0f
                ? Math.Clamp(pixels / referenceLength, 0f, 1f)
                : stop.Position)
            .ToArray();

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
            RenderGradientKind.Linear => CreateLinearGradientShader(gradient, centerX, centerY, diagonal, colors, positions, tileMode, repeatScale),
            RenderGradientKind.Radial => CreateRadialGradientShader(centerX, centerY, unscaledRx, unscaledRy, colors, positions, tileMode, repeatScale),
            RenderGradientKind.Conic => CreateConicGradientShader(gradient, centerX, centerY, colors, positions, tileMode, repeatScale),
            _ => throw new NotSupportedException($"Unsupported gradient kind: {gradient.Kind}"),
        };

        return paint;
    }

    private static SKShader CreateLinearGradientShader(RenderGradient gradient, float centerX, float centerY, float diagonal, SKColor[] colors, float[] positions, SKShaderTileMode tileMode, float repeatScale)
    {
        var halfDiagonal = diagonal / 2f;
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

        using var data = SKData.CreateCopy(command.Image.Data);
        using var image = SKImage.FromEncodedData(data);
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
            fonts);

        using var paint = SkiaTextShaping.CreateTextPaint(font);
        paint.Color = ToSkColor(command.Color);

        DrawTextWithLetterSpacing(canvas, paint, command.Text, command.X, command.Y, command.LetterSpacing);

        if (command.Underline || command.StrikeThrough)
        {
            using var decorationPaint = new SKPaint
            {
                Color = ToSkColor(command.DecorationColor),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, command.FontSize / 14f),
            };

            var textWidth = SkiaTextShaping.MeasureTextWidth(paint, command.Text, command.LetterSpacing);

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
            fonts);

        using var paint = SkiaTextShaping.CreateTextPaint(font);
        paint.Color = ToSkColor(command.Color);

        if (command.BlurRadius > 0f)
        {
            // Matches the same CSS-blur-radius-to-Gaussian-sigma approximation used for box-shadow.
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, command.BlurRadius / 2f);
        }

        DrawTextWithLetterSpacing(canvas, paint, command.Text, command.X, command.Y, command.LetterSpacing);
    }

    private static void DrawDecorationLine(SKCanvas canvas, SKPaint paint, float x, float y, float width, RenderTextDecorationStyle style)
    {
        switch (style)
        {
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

    private static void DrawTextWithLetterSpacing(SKCanvas canvas, SKPaint paint, string text, float x, float y, float letterSpacing)
    {
        if (letterSpacing <= 0f)
        {
            canvas.DrawText(text, x, y, paint);
            return;
        }

        var cursorX = x;

        foreach (var character in text)
        {
            var glyph = character.ToString();
            canvas.DrawText(glyph, cursorX, y, paint);
            cursorX += paint.MeasureText(glyph) + letterSpacing;
        }
    }

    private static SKColor ToSkColor(RenderColor color) => new(color.R, color.G, color.B, color.A);

}