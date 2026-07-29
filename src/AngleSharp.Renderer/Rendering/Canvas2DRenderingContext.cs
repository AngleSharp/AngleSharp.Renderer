namespace AngleSharp.Renderer;

using System.Linq;
using AngleSharp.Html.Dom;
using AngleSharp.Media.Dom;
using SkiaSharp;

/// <summary>
/// A minimal 2D canvas context backed by a Skia bitmap.
/// </summary>
public sealed class Canvas2DRenderingContext : ICanvasRenderingContext2D
{
    private SKBitmap _bitmap;
    private SKCanvas _canvas;
    private SKColor _fillColor = SKColors.Black;
    private SKColor _strokeColor = SKColors.Black;
    private float _lineWidth = 1f;
    private string _font = "10px sans-serif";
    private float _translateX;
    private float _translateY;
    private readonly List<(float X, float Y)> _pathPoints = new();
    private readonly Stack<(SKColor FillColor, SKColor StrokeColor, float LineWidth, string Font, float TranslateX, float TranslateY, int Width, int Height)> _stateStack = new();

    /// <summary>
    /// Creates a new bitmap-backed 2D canvas rendering context.
    /// </summary>
    /// <param name="host">The canvas element hosting the context.</param>
    public Canvas2DRenderingContext(IHtmlCanvasElement host)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        var width = Math.Max(1, host.Width);
        var height = Math.Max(1, host.Height);
        _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _bitmap.Erase(SKColors.Transparent);
        _canvas = new SKCanvas(_bitmap);
    }

    /// <inheritdoc/>
    public string ContextId => "2d";

    /// <inheritdoc/>
    public bool IsFixed => false;

    /// <inheritdoc/>
    public IHtmlCanvasElement Host { get; }

    /// <inheritdoc/>
    public IHtmlCanvasElement Canvas => Host;

    /// <inheritdoc/>
    public int Width
    {
        get => Host.Width;
        set
        {
            Host.Width = value;
            ResizeBitmap();
        }
    }

    /// <inheritdoc/>
    public int Height
    {
        get => Host.Height;
        set
        {
            Host.Height = value;
            ResizeBitmap();
        }
    }

    /// <summary>
    /// Fills a rectangle with the current fill style.
    /// </summary>
    public void FillRect(float x, float y, float width, float height)
    {
        using var paint = new SKPaint
        {
            Color = _fillColor,
            Style = SKPaintStyle.Fill,
        };

        _canvas.DrawRect(new SKRect(x + _translateX, y + _translateY, x + _translateX + width, y + _translateY + height), paint);
    }

    /// <summary>
    /// Strokes the outline of a rectangle with the current stroke style.
    /// </summary>
    public void StrokeRect(float x, float y, float width, float height)
    {
        using var paint = new SKPaint
        {
            Color = _strokeColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = _lineWidth,
        };

        _canvas.DrawRect(new SKRect(x + _translateX, y + _translateY, x + _translateX + width, y + _translateY + height), paint);
    }

    /// <summary>
    /// Clears a rectangular area of the backing bitmap.
    /// </summary>
    public void ClearRect(float x, float y, float width, float height)
    {
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            BlendMode = SKBlendMode.Clear,
        };

        _canvas.DrawRect(new SKRect(x + _translateX, y + _translateY, x + _translateX + width, y + _translateY + height), paint);
    }

    /// <summary>
    /// Sets the current fill style.
    /// </summary>
    public void SetFillStyle(string? color)
    {
        _fillColor = ParseColor(color);
    }

    /// <summary>
    /// Sets the current stroke style.
    /// </summary>
    public void SetStrokeStyle(string? color)
    {
        _strokeColor = ParseColor(color);
    }

    /// <summary>
    /// Sets the current line width.
    /// </summary>
    public void SetLineWidth(float width)
    {
        _lineWidth = Math.Max(0f, width);
    }

    /// <summary>
    /// Sets the current font string.
    /// </summary>
    public void SetFont(string? font)
    {
        _font = string.IsNullOrWhiteSpace(font) ? "10px sans-serif" : font;
    }

    /// <summary>
    /// Translates subsequent drawing operations by the specified amount.
    /// </summary>
    public void Translate(float x, float y)
    {
        _translateX += x;
        _translateY += y;
    }

    /// <summary>
    /// Begins a new path.
    /// </summary>
    public void BeginPath()
    {
        _pathPoints.Clear();
    }

    /// <summary>
    /// Adds a point to the current path.
    /// </summary>
    public void MoveTo(float x, float y)
    {
        _pathPoints.Clear();
        _pathPoints.Add((x, y));
    }

    /// <summary>
    /// Adds a line segment to the current path.
    /// </summary>
    public void LineTo(float x, float y)
    {
        if (_pathPoints.Count == 0)
        {
            _pathPoints.Add((x, y));
            return;
        }

        _pathPoints.Add((x, y));
    }

    /// <summary>
    /// Closes the current path.
    /// </summary>
    public void ClosePath()
    {
        if (_pathPoints.Count > 1)
        {
            _pathPoints.Add(_pathPoints[0]);
        }

    }

    /// <summary>
    /// Fills the current path.
    /// </summary>
    public void Fill()
    {
        if (_pathPoints.Count < 2)
        {
            return;
        }

        using var paint = CreatePaint(_fillColor, SKPaintStyle.Fill);
        var points = _pathPoints.Select(point => new SKPoint(point.X + _translateX, point.Y + _translateY)).ToArray();
        _canvas.DrawPoints(SKPointMode.Polygon, points, paint);
    }

    /// <summary>
    /// Strokes the current path.
    /// </summary>
    public void Stroke()
    {
        if (_pathPoints.Count < 2)
        {
            return;
        }

        using var paint = CreatePaint(_strokeColor, SKPaintStyle.Stroke);
        paint.StrokeWidth = _lineWidth;
        var points = _pathPoints.Select(point => new SKPoint(point.X + _translateX, point.Y + _translateY)).ToArray();
        _canvas.DrawPoints(SKPointMode.Polygon, points, paint);
    }

    /// <summary>
    /// Draws filled text at the requested position.
    /// </summary>
    public void FillText(string? text, float x, float y)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var paint = CreatePaint(_fillColor, SKPaintStyle.Fill);
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, ParseFontSize(_font));
        _canvas.DrawText(text, x + _translateX, y + _translateY, font, paint);
    }

    /// <summary>
    /// Saves the current canvas state.
    /// </summary>
    public void Save()
    {
        _stateStack.Push((_fillColor, _strokeColor, _lineWidth, _font, _translateX, _translateY, Host.Width, Host.Height));
        _canvas.Save();
    }

    /// <summary>
    /// Restores the most recently saved canvas state.
    /// </summary>
    public void Restore()
    {
        if (_stateStack.Count == 0)
        {
            return;
        }

        var (fillColor, strokeColor, lineWidth, font, translateX, translateY, width, height) = _stateStack.Pop();
        _fillColor = fillColor;
        _strokeColor = strokeColor;
        _lineWidth = lineWidth;
        _font = font;
        _translateX = translateX;
        _translateY = translateY;
        Host.Width = width;
        Host.Height = height;
        _canvas.Restore();
    }

    /// <inheritdoc/>
    public void SaveState()
    {
        Save();
    }

    /// <inheritdoc/>
    public void RestoreState()
    {
        Restore();
    }

    /// <inheritdoc/>
    public byte[] ToImage(string type)
    {
        _canvas.Flush();

        using var image = SKImage.FromBitmap(_bitmap);
        using var data = image.Encode(type.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Png, 100);

        return data is null ? Array.Empty<byte>() : data.ToArray();
    }

    private void ResizeBitmap()
    {
        if (_bitmap.Width == Host.Width && _bitmap.Height == Host.Height)
        {
            return;
        }

        var previous = _bitmap;
        var resized = new SKBitmap(Math.Max(1, Host.Width), Math.Max(1, Host.Height), SKColorType.Rgba8888, SKAlphaType.Premul);
        resized.Erase(SKColors.Transparent);

        using var canvas = new SKCanvas(resized);
        canvas.DrawBitmap(previous, new SKRect(0, 0, previous.Width, previous.Height), new SKRect(0, 0, resized.Width, resized.Height));
        canvas.Flush();

        _bitmap.Dispose();
        _canvas.Dispose();
        _bitmap = resized;
        _canvas = new SKCanvas(_bitmap);
    }

    private SKPaint CreatePaint(SKColor color, SKPaintStyle style)
    {
        return new SKPaint
        {
            Color = color,
            Style = style,
            StrokeWidth = _lineWidth,
        };
    }

    private static float ParseFontSize(string font)
    {
        if (string.IsNullOrWhiteSpace(font))
        {
            return 10f;
        }

        var token = font.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (token is null)
        {
            return 10f;
        }

        if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase) &&
            float.TryParse(token[..^2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px))
        {
            return px;
        }

        return 10f;
    }

    private static SKColor ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SKColors.Black;
        }

        var color = value.Trim();

        if (color.StartsWith("#", StringComparison.Ordinal) && color.Length == 7)
        {
            if (byte.TryParse(color[1..3], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(color[3..5], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(color[5..7], System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return new SKColor(r, g, b);
            }
        }

        if (color.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && color.EndsWith(")", StringComparison.Ordinal))
        {
            var content = color[4..^1].Trim();
            var components = content.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (components.Length >= 3 &&
                byte.TryParse(components[0], out var r) &&
                byte.TryParse(components[1], out var g) &&
                byte.TryParse(components[2], out var b))
            {
                return new SKColor(r, g, b);
            }
        }

        return color.ToLowerInvariant() switch
        {
            "transparent" => SKColors.Transparent,
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            "blue" => SKColors.Blue,
            _ => SKColors.Black,
        };
    }
}
