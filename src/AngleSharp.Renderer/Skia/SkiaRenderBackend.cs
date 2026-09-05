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
            case DrawImageCommand image:
                DrawImage(canvas, image);
                break;
            case DrawTextCommand text:
                DrawText(canvas, text, fonts);
                break;
        }
    }

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

        canvas.DrawRect(rect, paint);
    }

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
        var positions = gradient.Stops.Select(stop => stop.Position).ToArray();

        var centerX = rect.X + (rect.Width / 2f);
        var centerY = rect.Y + (rect.Height / 2f);
        var radius = (float)Math.Max(rect.Width, rect.Height) / 2f;

        paint.Shader = gradient.Kind switch
        {
            RenderGradientKind.Linear => CreateLinearGradientShader(gradient, rect),
            RenderGradientKind.Radial => SKShader.CreateRadialGradient(
                new SKPoint(centerX, centerY),
                radius,
                colors,
                positions,
                SKShaderTileMode.Clamp),
            RenderGradientKind.Conic => SKShader.CreateSweepGradient(
                new SKPoint(centerX, centerY),
                colors,
                positions),
            _ => throw new NotSupportedException($"Unsupported gradient kind: {gradient.Kind}"),
        };

        return paint;
    }

    private static SKShader CreateLinearGradientShader(RenderGradient gradient, RenderRect rect)
    {
        var centerX = rect.X + (rect.Width / 2f);
        var centerY = rect.Y + (rect.Height / 2f);
        var diagonal = Math.Sqrt((rect.Width * rect.Width) + (rect.Height * rect.Height));
        var halfDiagonal = (float)diagonal / 2f;
        var radians = (gradient.AngleDegrees % 360f + 360f) % 360f;
        var angle = radians * (Math.PI / 180d);
        var dx = (float)Math.Cos(angle);
        var dy = (float)Math.Sin(angle);

        var start = new SKPoint(centerX - (dx * halfDiagonal), centerY - (dy * halfDiagonal));
        var end = new SKPoint(centerX + (dx * halfDiagonal), centerY + (dy * halfDiagonal));

        return SKShader.CreateLinearGradient(
            start,
            end,
            gradient.Stops.Select(stop => new SKColor(stop.Color.R, stop.Color.G, stop.Color.B, stop.Color.A)).ToArray(),
            gradient.Stops.Select(stop => stop.Position).ToArray(),
            SKShaderTileMode.Clamp);
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