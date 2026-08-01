using AngleSharp.Renderer.Rendering;
using System.Linq;
using System.Reflection;
using System.Threading;

using SkiaSharp;

namespace AngleSharp.Renderer.Skia;

/// <summary>
/// Uses SkiaSharp to render a display list.
/// </summary>
public sealed class SkiaRenderBackend : IRenderBackend
{
    private const string FontResourcePrefix = "AngleSharp.Renderer.Resources.Fonts.";

    private static readonly Lazy<IReadOnlyDictionary<string, BundledFontFamily>> BundledFonts =
        new(CreateBundledFonts, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly IReadOnlyDictionary<string, string> GenericFontMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["serif"] = "serif",
            ["sans-serif"] = "sans-serif",
            ["monospace"] = "monospace",
            ["cursive"] = "sans-serif",
            ["fantasy"] = "serif",
            ["dejavu serif"] = "serif",
            ["dejavu sans"] = "sans-serif",
            ["dejavu sans mono"] = "monospace",
        };

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
            case DrawImageCommand image:
                DrawImage(canvas, image);
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
            SubpixelText = false,
            LcdRenderText = false,
            HintingLevel = SKPaintHinting.Normal,
            TextSize = command.FontSize,
            Typeface = CreateTypeface(command.FontFamily, fontStyle),
            TextSkewX = command.IsItalic ? -0.25f : 0f,
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
        var isBold = fontStyle.Weight >= (int)SKFontStyleWeight.Bold;

        var families = fontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var family in families)
        {
            var normalized = family.Trim('\'', '"', ' ');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (GenericFontMappings.TryGetValue(normalized, out var bundledFamilyKey) &&
                BundledFonts.Value.TryGetValue(bundledFamilyKey, out var bundledFamily))
            {
                return isBold ? bundledFamily.Bold : bundledFamily.Regular;
            }

            var typeface = SKTypeface.FromFamilyName(normalized, fontStyle);

            if (typeface is not null)
            {
                return typeface;
            }
        }

        if (BundledFonts.Value.TryGetValue("sans-serif", out var defaultFamily))
        {
            return isBold ? defaultFamily.Bold : defaultFamily.Regular;
        }

        return SKTypeface.FromFamilyName(fontFamily, fontStyle) ?? SKTypeface.Default;
    }

    private static IReadOnlyDictionary<string, BundledFontFamily> CreateBundledFonts()
    {
        var sansRegular = LoadBundledTypeface("DejaVuSans.ttf");
        var sansBold = LoadBundledTypeface("DejaVuSans-Bold.ttf");
        var serifRegular = LoadBundledTypeface("DejaVuSerif.ttf");
        var serifBold = LoadBundledTypeface("DejaVuSerif-Bold.ttf");
        var monoRegular = LoadBundledTypeface("DejaVuSansMono.ttf");
        var monoBold = LoadBundledTypeface("DejaVuSansMono-Bold.ttf");

        return new Dictionary<string, BundledFontFamily>(StringComparer.OrdinalIgnoreCase)
        {
            ["sans-serif"] = new BundledFontFamily(sansRegular, sansBold),
            ["serif"] = new BundledFontFamily(serifRegular, serifBold),
            ["monospace"] = new BundledFontFamily(monoRegular, monoBold),
        };
    }

    private static SKTypeface LoadBundledTypeface(string fileName)
    {
        var assembly = typeof(SkiaRenderBackend).Assembly;
        var resourceName = string.Concat(FontResourcePrefix, fileName);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Bundled font resource not found: {resourceName}");
        using var data = SKData.Create(stream)
            ?? throw new InvalidOperationException($"Unable to read bundled font resource: {resourceName}");

        return SKTypeface.FromData(data)
            ?? throw new InvalidOperationException($"Unable to load bundled font resource: {resourceName}");
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

    private readonly record struct BundledFontFamily(SKTypeface Regular, SKTypeface Bold);
}