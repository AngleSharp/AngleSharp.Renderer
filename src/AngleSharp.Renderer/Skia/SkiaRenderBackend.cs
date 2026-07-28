using AngleSharp.Renderer.Rendering;

using SkiaSharp;

namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Uses SkiaSharp to render a display list.
/// </summary>
public sealed class SkiaRenderBackend : IRenderBackend
{
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
            DrawCommand(canvas, command);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new InvalidOperationException("Unable to encode the rendered image as PNG.");

        return new RenderedImage(data.ToArray(), viewport.Width, viewport.Height, "image/png");
    }

    private static void DrawCommand(SKCanvas canvas, RenderCommand command)
    {
        switch (command)
        {
            case FillRectCommand fill:
                DrawFillRect(canvas, fill);
                break;
            case DrawTextCommand text:
                DrawText(canvas, text);
                break;
        }
    }

    private static void DrawFillRect(SKCanvas canvas, FillRectCommand command)
    {
        if (command.Rect.IsEmpty)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Color = ToSkColor(command.Color),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        var rect = new SKRect(
            command.Rect.X,
            command.Rect.Y,
            command.Rect.X + command.Rect.Width,
            command.Rect.Y + command.Rect.Height);

        canvas.DrawRect(rect, paint);
    }

    private static void DrawText(SKCanvas canvas, DrawTextCommand command)
    {
        var fontStyle = new SKFontStyle(
            command.FontWeight >= 600f ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            command.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

        using var paint = new SKPaint
        {
            Color = ToSkColor(command.Color),
            IsAntialias = true,
            TextSize = command.FontSize,
            Typeface = CreateTypeface(command.FontFamily, fontStyle),
        };

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

            var textWidth = MeasureTextWidth(paint, command.Text, command.LetterSpacing);

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

    private static SKTypeface CreateTypeface(string fontFamily, SKFontStyle fontStyle)
    {
        var families = fontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var family in families)
        {
            var normalized = family.Trim('\'', '"', ' ');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            normalized = normalized.ToLowerInvariant();

            normalized = normalized switch
            {
                "serif" => "DejaVu Serif",
                "sans-serif" => "DejaVu Sans",
                "monospace" => "DejaVu Sans Mono",
                "cursive" => "DejaVu Sans",
                "fantasy" => "DejaVu Serif",
                _ => normalized,
            };

            var typeface = SKTypeface.FromFamilyName(normalized, fontStyle);

            if (typeface is not null)
            {
                return typeface;
            }
        }

        return SKTypeface.FromFamilyName(fontFamily, fontStyle) ?? SKTypeface.Default;
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

    private static float MeasureTextWidth(SKPaint paint, string text, float letterSpacing)
    {
        if (letterSpacing <= 0f)
        {
            return paint.MeasureText(text);
        }

        var width = 0f;

        foreach (var character in text)
        {
            width += paint.MeasureText(character.ToString()) + letterSpacing;
        }

        return width > 0f ? width - letterSpacing : 0f;
    }

    private static SKColor ToSkColor(RenderColor color) => new(color.R, color.G, color.B, color.A);
}