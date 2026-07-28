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
        using var paint = new SKPaint
        {
            Color = ToSkColor(command.Color),
            IsAntialias = true,
            TextSize = command.FontSize,
            Typeface = SKTypeface.FromFamilyName(command.FontFamily),
        };

        canvas.DrawText(command.Text, command.X, command.Y, paint);
    }

    private static SKColor ToSkColor(RenderColor color) => new(color.R, color.G, color.B, color.A);
}