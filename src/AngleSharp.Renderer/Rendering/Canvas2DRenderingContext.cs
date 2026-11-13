namespace AngleSharp.Renderer;

using System.Globalization;
using System.Linq;
using System.Threading;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using AngleSharp.Media.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;
using SkiaSharp;

/// <summary>
/// A 2D canvas context backed by a Skia bitmap, which the page paints as the canvas's image (see
/// <see cref="CanvasRenderingContext2DExtensions"/> for what script sees of it).
/// </summary>
/// <remarks>
/// Script draws on the event loop, while frames are rasterized on other threads: the bitmap is
/// only touched under a lock. A frame of an interactive document draws the copy taken when it was
/// prepared (see <see cref="Present"/>), as a browser shows a canvas after the task drawing into it
/// ended: a copy taken while the raster runs could show a script's drawing half done, e.g. right
/// after it cleared the canvas, which flickers.
/// </remarks>
public sealed class Canvas2DRenderingContext : ICanvasRenderingContext2D
{
    private const string DefaultFont = "10px sans-serif";

    private readonly object _sync = new();
    private SKBitmap _bitmap;
    private SKCanvas _canvas;
    private DrawingState _state = new();
    private readonly Stack<DrawingState> _stateStack = new();
    private SKPath _path = new();
    private bool _hasCurrentPoint;
    private SKImage? _snapshot;
    // what frames show, see Present
    private SKImage? _presented;
    private bool _isPresented;
    private int _invalidationRequested;
    private FontFaceSet? _fonts;
    private long _fontsLoadedAt;

    /// <summary>
    /// Creates a new bitmap-backed 2D canvas rendering context.
    /// </summary>
    /// <param name="host">The canvas element hosting the context.</param>
    public Canvas2DRenderingContext(IHtmlCanvasElement host)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _bitmap = CreateBitmap(host.Width, host.Height);
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
            EnsureBitmapSize();
        }
    }

    /// <inheritdoc/>
    public int Height
    {
        get => Host.Height;
        set
        {
            Host.Height = value;
            EnsureBitmapSize();
        }
    }

    #region Drawing state

    /// <summary>
    /// Gets or sets the fill style: a CSS color (serialized when read), a
    /// <see cref="CanvasGradient"/> or a <see cref="CanvasPattern"/>.
    /// </summary>
    public object FillStyle
    {
        get => _state.Fill.Value;
        set => _state.Fill = ParseStyle(value, _state.Fill);
    }

    /// <summary>
    /// Gets or sets the stroke style (see <see cref="FillStyle"/>).
    /// </summary>
    public object StrokeStyle
    {
        get => _state.Stroke.Value;
        set => _state.Stroke = ParseStyle(value, _state.Stroke);
    }

    /// <summary>
    /// Gets or sets the width of lines; values that are not positive and finite are ignored.
    /// </summary>
    public double LineWidth
    {
        get => _state.LineWidth;
        set
        {
            if (value > 0d && double.IsFinite(value))
            {
                _state.LineWidth = (float)value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the ends of lines (<c>butt</c>, <c>round</c> or <c>square</c>).
    /// </summary>
    public string LineCap
    {
        get => _state.LineCap switch { SKStrokeCap.Round => "round", SKStrokeCap.Square => "square", _ => "butt" };
        set
        {
            if (value is "butt" or "round" or "square")
            {
                _state.LineCap = value switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
            }
        }
    }

    /// <summary>
    /// Gets or sets the corners of lines (<c>miter</c>, <c>round</c> or <c>bevel</c>).
    /// </summary>
    public string LineJoin
    {
        get => _state.LineJoin switch { SKStrokeJoin.Round => "round", SKStrokeJoin.Bevel => "bevel", _ => "miter" };
        set
        {
            if (value is "miter" or "round" or "bevel")
            {
                _state.LineJoin = value switch { "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
            }
        }
    }

    /// <summary>
    /// Gets or sets the miter limit.
    /// </summary>
    public double MiterLimit
    {
        get => _state.MiterLimit;
        set
        {
            if (value > 0d && double.IsFinite(value))
            {
                _state.MiterLimit = (float)value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the offset of the line dash pattern.
    /// </summary>
    public double LineDashOffset
    {
        get => _state.LineDashOffset;
        set
        {
            if (double.IsFinite(value))
            {
                _state.LineDashOffset = (float)value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the font as the CSS <c>font</c> shorthand.
    /// </summary>
    public string Font
    {
        get => _state.Font;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && TryParseFont(value, out var font))
            {
                _state.Font = value.Trim();
                _state.ParsedFont = font;
            }
        }
    }

    /// <summary>
    /// Gets or sets the horizontal alignment of text (<c>start</c>, <c>end</c>, <c>left</c>,
    /// <c>right</c> or <c>center</c>).
    /// </summary>
    public string TextAlign
    {
        get => _state.TextAlign;
        set
        {
            if (value is "start" or "end" or "left" or "right" or "center")
            {
                _state.TextAlign = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the baseline of text (<c>top</c>, <c>hanging</c>, <c>middle</c>,
    /// <c>alphabetic</c>, <c>ideographic</c> or <c>bottom</c>).
    /// </summary>
    public string TextBaseline
    {
        get => _state.TextBaseline;
        set
        {
            if (value is "top" or "hanging" or "middle" or "alphabetic" or "ideographic" or "bottom")
            {
                _state.TextBaseline = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the direction of text (<c>ltr</c>, <c>rtl</c> or <c>inherit</c>).
    /// </summary>
    public string Direction
    {
        get => _state.Direction;
        set
        {
            if (value is "ltr" or "rtl" or "inherit")
            {
                _state.Direction = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the alpha applied to everything drawn, between 0 and 1.
    /// </summary>
    public double GlobalAlpha
    {
        get => _state.GlobalAlpha;
        set
        {
            if (value >= 0d && value <= 1d)
            {
                _state.GlobalAlpha = (float)value;
            }
        }
    }

    /// <summary>
    /// Gets or sets how what is drawn is composed with what is there.
    /// </summary>
    public string GlobalCompositeOperation
    {
        get => _state.CompositeOperation;
        set
        {
            if (value is not null && TryGetBlendMode(value, out var mode))
            {
                _state.CompositeOperation = value;
                _state.BlendMode = mode;
            }
        }
    }

    /// <summary>
    /// Gets or sets the blur of shadows.
    /// </summary>
    public double ShadowBlur
    {
        get => _state.ShadowBlur;
        set
        {
            if (value >= 0d && double.IsFinite(value))
            {
                _state.ShadowBlur = (float)value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the color of shadows.
    /// </summary>
    public string ShadowColor
    {
        get => SerializeColor(_state.ShadowColor);
        set
        {
            if (TryParseColor(value, out var color))
            {
                _state.ShadowColor = color;
            }
        }
    }

    /// <summary>
    /// Gets or sets the horizontal offset of shadows.
    /// </summary>
    public double ShadowOffsetX
    {
        get => _state.ShadowOffsetX;
        set
        {
            if (double.IsFinite(value))
            {
                _state.ShadowOffsetX = (float)value;
            }
        }
    }

    /// <summary>
    /// Gets or sets the vertical offset of shadows.
    /// </summary>
    public double ShadowOffsetY
    {
        get => _state.ShadowOffsetY;
        set
        {
            if (double.IsFinite(value))
            {
                _state.ShadowOffsetY = (float)value;
            }
        }
    }

    /// <summary>
    /// Gets or sets if scaled images are smoothed.
    /// </summary>
    public bool ImageSmoothingEnabled
    {
        get => _state.ImageSmoothingEnabled;
        set => _state.ImageSmoothingEnabled = value;
    }

    /// <summary>
    /// Sets the current fill style.
    /// </summary>
    public void SetFillStyle(string? color) => FillStyle = color ?? string.Empty;

    /// <summary>
    /// Sets the current stroke style.
    /// </summary>
    public void SetStrokeStyle(string? color) => StrokeStyle = color ?? string.Empty;

    /// <summary>
    /// Sets the current line width.
    /// </summary>
    public void SetLineWidth(float width) => LineWidth = width;

    /// <summary>
    /// Sets the current font string.
    /// </summary>
    public void SetFont(string? font) => Font = string.IsNullOrWhiteSpace(font) ? DefaultFont : font;

    /// <summary>
    /// Sets the line dash pattern; one with an odd number of entries is repeated once.
    /// </summary>
    public void SetLineDash(IEnumerable<double>? segments)
    {
        var values = segments?.ToArray() ?? [];

        if (values.Any(static value => value < 0d || !double.IsFinite(value)))
        {
            return;
        }

        _state.LineDash = (values.Length % 2 == 1 ? values.Concat(values) : values).Select(static value => (float)value).ToArray();
    }

    /// <summary>
    /// Gets the line dash pattern.
    /// </summary>
    public double[] GetLineDash() => _state.LineDash.Select(static value => (double)value).ToArray();

    /// <summary>
    /// Saves the current drawing state.
    /// </summary>
    public void Save()
    {
        _stateStack.Push(_state.Clone());

        lock (_sync)
        {
            _canvas.Save();
        }
    }

    /// <summary>
    /// Restores the most recently saved drawing state.
    /// </summary>
    public void Restore()
    {
        if (_stateStack.Count == 0)
        {
            return;
        }

        _state = _stateStack.Pop();

        lock (_sync)
        {
            _canvas.Restore();
        }
    }

    /// <inheritdoc/>
    public void SaveState() => Save();

    /// <inheritdoc/>
    public void RestoreState() => Restore();

    /// <summary>
    /// Resets the context: clears the bitmap, the path, and the drawing state.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            ResetLocked();
        }

        Invalidate();
    }

    #endregion

    #region Transformations

    /// <summary>
    /// Translates subsequent drawing operations by the specified amount.
    /// </summary>
    public void Translate(float x, float y) => Translate((double)x, y);

    /// <summary>
    /// Translates subsequent drawing operations by the specified amount.
    /// </summary>
    public void Translate(double x, double y) => Transform(1d, 0d, 0d, 1d, x, y);

    /// <summary>
    /// Rotates subsequent drawing operations clockwise by the angle (in radians).
    /// </summary>
    public void Rotate(double angle)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        Transform(cos, sin, -sin, cos, 0d, 0d);
    }

    /// <summary>
    /// Scales subsequent drawing operations.
    /// </summary>
    public void Scale(double x, double y) => Transform(x, 0d, 0d, y, 0d, 0d);

    /// <summary>
    /// Multiplies the current transformation with the given matrix.
    /// </summary>
    public void Transform(double a, double b, double c, double d, double e, double f)
    {
        if (!AreFinite(a, b, c, d, e, f))
        {
            return;
        }

        _state.Transform = SKMatrix.Concat(_state.Transform, CreateMatrix(a, b, c, d, e, f));
    }

    /// <summary>
    /// Replaces the current transformation with the given matrix.
    /// </summary>
    public void SetTransform(double a, double b, double c, double d, double e, double f)
    {
        if (AreFinite(a, b, c, d, e, f))
        {
            _state.Transform = CreateMatrix(a, b, c, d, e, f);
        }
    }

    /// <summary>
    /// Resets the current transformation to the identity.
    /// </summary>
    public void ResetTransform() => _state.Transform = SKMatrix.Identity;

    /// <summary>
    /// Gets the current transformation as (a, b, c, d, e, f).
    /// </summary>
    public double[] GetTransform()
    {
        var m = _state.Transform;
        return [m.ScaleX, m.SkewY, m.SkewX, m.ScaleY, m.TransX, m.TransY];
    }

    #endregion

    #region Paths

    /// <summary>
    /// Begins a new path.
    /// </summary>
    public void BeginPath()
    {
        _path.Reset();
        _hasCurrentPoint = false;
    }

    /// <summary>
    /// Starts a new subpath at the given point.
    /// </summary>
    public void MoveTo(float x, float y) => MoveTo((double)x, y);

    /// <summary>
    /// Starts a new subpath at the given point.
    /// </summary>
    public void MoveTo(double x, double y)
    {
        if (AreFinite(x, y))
        {
            _path.MoveTo(Map(x, y));
            _hasCurrentPoint = true;
        }
    }

    /// <summary>
    /// Adds a line segment to the current path.
    /// </summary>
    public void LineTo(float x, float y) => LineTo((double)x, y);

    /// <summary>
    /// Adds a line segment to the current path.
    /// </summary>
    public void LineTo(double x, double y)
    {
        if (!AreFinite(x, y))
        {
            return;
        }

        if (_hasCurrentPoint)
        {
            _path.LineTo(Map(x, y));
        }
        else
        {
            MoveTo(x, y);
        }
    }

    /// <summary>
    /// Adds a quadratic Bézier curve to the current path.
    /// </summary>
    public void QuadraticCurveTo(double cpx, double cpy, double x, double y)
    {
        if (!AreFinite(cpx, cpy, x, y))
        {
            return;
        }

        EnsureCurrentPoint(cpx, cpy);
        _path.QuadTo(Map(cpx, cpy), Map(x, y));
    }

    /// <summary>
    /// Adds a cubic Bézier curve to the current path.
    /// </summary>
    public void BezierCurveTo(double cp1x, double cp1y, double cp2x, double cp2y, double x, double y)
    {
        if (!AreFinite(cp1x, cp1y, cp2x, cp2y, x, y))
        {
            return;
        }

        EnsureCurrentPoint(cp1x, cp1y);
        _path.CubicTo(Map(cp1x, cp1y), Map(cp2x, cp2y), Map(x, y));
    }

    /// <summary>
    /// Adds an arc of a circle to the current path, connected by a line to what is there.
    /// </summary>
    public void Arc(double x, double y, double radius, double startAngle, double endAngle, bool anticlockwise = false) =>
        Ellipse(x, y, radius, radius, 0d, startAngle, endAngle, anticlockwise);

    /// <summary>
    /// Adds an arc of an ellipse to the current path, connected by a line to what is there.
    /// </summary>
    public void Ellipse(double x, double y, double radiusX, double radiusY, double rotation, double startAngle, double endAngle, bool anticlockwise = false)
    {
        if (!AreFinite(x, y, radiusX, radiusY, rotation, startAngle, endAngle))
        {
            return;
        }

        if (radiusX < 0d || radiusY < 0d)
        {
            throw new DomException(DomError.IndexSizeError);
        }

        var sweep = endAngle - startAngle;
        const double FullCircle = 2d * Math.PI;

        // a sweep of a full turn or more (in its direction) draws the whole ellipse
        if (!anticlockwise && sweep >= FullCircle)
        {
            sweep = FullCircle;
        }
        else if (anticlockwise && sweep <= -FullCircle)
        {
            sweep = -FullCircle;
        }
        // against its direction, it goes the other way round to the end angle, which is the
        // whole ellipse when that is a full turn away (e.g. 0 to 2π anticlockwise, as Chromium)
        else if (!anticlockwise && sweep < 0d)
        {
            sweep = FullCircle - (-sweep % FullCircle);
        }
        else if (anticlockwise && sweep > 0d)
        {
            sweep = -(FullCircle - (sweep % FullCircle));
        }

        var oval = new SKRect((float)(x - radiusX), (float)(y - radiusY), (float)(x + radiusX), (float)(y + radiusY));
        var start = (float)(startAngle * 180d / Math.PI);
        var sweepDegrees = (float)(sweep * 180d / Math.PI);
        var transform = _state.Transform;

        if (rotation != 0d)
        {
            transform = SKMatrix.Concat(transform, SKMatrix.CreateRotation((float)rotation, (float)x, (float)y));
        }

        using var arc = new SKPath();

        // Skia leaves out an oval's sweep of 360 degrees, which is two halves then
        if (Math.Abs(sweepDegrees) >= 360f)
        {
            var half = sweepDegrees / 2f;
            arc.ArcTo(oval, start, half, true);
            arc.ArcTo(oval, start + half, half, false);
        }
        else
        {
            arc.ArcTo(oval, start, sweepDegrees, true);
        }

        arc.Transform(transform);
        AppendConnected(arc);
    }

    /// <summary>
    /// Adds an arc with the given control points and radius to the current path.
    /// </summary>
    public void ArcTo(double x1, double y1, double x2, double y2, double radius)
    {
        if (!AreFinite(x1, y1, x2, y2, radius))
        {
            return;
        }

        if (radius < 0d)
        {
            throw new DomException(DomError.IndexSizeError);
        }

        EnsureCurrentPoint(x1, y1);

        // the corner is rounded in user space, where the current point is too
        if (!_state.Transform.TryInvert(out var inverse))
        {
            return;
        }

        var last = inverse.MapPoint(_path.LastPoint);
        using var corner = new SKPath();
        corner.MoveTo(last);
        corner.ArcTo(new SKPoint((float)x1, (float)y1), new SKPoint((float)x2, (float)y2), (float)radius);
        corner.Transform(_state.Transform);
        _path.AddPath(corner, SKPathAddMode.Extend);
    }

    /// <summary>
    /// Adds a closed rectangle subpath to the current path.
    /// </summary>
    public void Rect(double x, double y, double width, double height)
    {
        if (!AreFinite(x, y, width, height))
        {
            return;
        }

        _path.MoveTo(Map(x, y));
        _path.LineTo(Map(x + width, y));
        _path.LineTo(Map(x + width, y + height));
        _path.LineTo(Map(x, y + height));
        _path.Close();
        _path.MoveTo(Map(x, y));
        _hasCurrentPoint = true;
    }

    /// <summary>
    /// Adds a closed rectangle with rounded corners (one radius for all of them) to the current path.
    /// </summary>
    public void RoundRect(double x, double y, double width, double height, double radius)
    {
        if (!AreFinite(x, y, width, height, radius))
        {
            return;
        }

        if (radius < 0d)
        {
            throw new DomException(DomError.IndexSizeError);
        }

        var rect = SKRect.Create((float)Math.Min(x, x + width), (float)Math.Min(y, y + height), (float)Math.Abs(width), (float)Math.Abs(height));
        using var rounded = new SKPath();
        rounded.AddRoundRect(rect, (float)radius, (float)radius);
        rounded.Transform(_state.Transform);
        _path.AddPath(rounded);
        _path.MoveTo(Map(x, y));
        _hasCurrentPoint = true;
    }

    /// <summary>
    /// Closes the current subpath.
    /// </summary>
    public void ClosePath()
    {
        if (_hasCurrentPoint)
        {
            _path.Close();
        }
    }

    /// <summary>
    /// Fills the current path with the fill style.
    /// </summary>
    public void Fill() => Fill("nonzero");

    /// <summary>
    /// Fills the current path with the fill style, by the given rule (<c>nonzero</c> or <c>evenodd</c>).
    /// </summary>
    public void Fill(string fillRule)
    {
        _path.FillType = ToFillType(fillRule);
        Draw(_path, isStroke: false);
    }

    /// <summary>
    /// Strokes the current path with the stroke style.
    /// </summary>
    public void Stroke() => Draw(_path, isStroke: true);

    /// <summary>
    /// Restricts what is drawn from now on to the current path (until the state is restored).
    /// </summary>
    public void Clip(string fillRule = "nonzero")
    {
        using var clip = new SKPath(_path) { FillType = ToFillType(fillRule) };

        lock (_sync)
        {
            EnsureBitmapSizeLocked();
            _canvas.ResetMatrix();
            _canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        }
    }

    /// <summary>
    /// Gets if the point (in the coordinates of the canvas) is inside of the current path.
    /// </summary>
    public bool IsPointInPath(double x, double y, string fillRule = "nonzero")
    {
        if (!AreFinite(x, y))
        {
            return false;
        }

        using var path = new SKPath(_path) { FillType = ToFillType(fillRule) };
        return path.Contains((float)x, (float)y);
    }

    /// <summary>
    /// Gets if the point (in the coordinates of the canvas) is on the line the current path is stroked with.
    /// </summary>
    public bool IsPointInStroke(double x, double y)
    {
        if (!AreFinite(x, y) || !_state.Transform.TryInvert(out var inverse))
        {
            return false;
        }

        using var userPath = new SKPath(_path);
        userPath.Transform(inverse);
        using var paint = CreateStrokeOutlinePaint();
        using var outline = paint.GetFillPath(userPath);
        outline.Transform(_state.Transform);
        return outline.Contains((float)x, (float)y);
    }

    #endregion

    #region Rectangles

    /// <summary>
    /// Fills a rectangle with the current fill style.
    /// </summary>
    public void FillRect(float x, float y, float width, float height) => FillRect((double)x, y, width, height);

    /// <summary>
    /// Fills a rectangle with the current fill style.
    /// </summary>
    public void FillRect(double x, double y, double width, double height)
    {
        if (AreFinite(x, y, width, height) && width != 0d && height != 0d)
        {
            using var path = CreateRectPath(x, y, width, height);
            Draw(path, isStroke: false);
        }
    }

    /// <summary>
    /// Strokes the outline of a rectangle with the current stroke style.
    /// </summary>
    public void StrokeRect(float x, float y, float width, float height) => StrokeRect((double)x, y, width, height);

    /// <summary>
    /// Strokes the outline of a rectangle with the current stroke style.
    /// </summary>
    public void StrokeRect(double x, double y, double width, double height)
    {
        if (AreFinite(x, y, width, height))
        {
            using var path = CreateRectPath(x, y, width, height);
            Draw(path, isStroke: true);
        }
    }

    /// <summary>
    /// Clears a rectangular area of the backing bitmap to transparent black.
    /// </summary>
    public void ClearRect(float x, float y, float width, float height) => ClearRect((double)x, y, width, height);

    /// <summary>
    /// Clears a rectangular area of the backing bitmap to transparent black.
    /// </summary>
    public void ClearRect(double x, double y, double width, double height)
    {
        if (!AreFinite(x, y, width, height))
        {
            return;
        }

        using var path = CreateRectPath(x, y, width, height);
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.Clear, IsAntialias = false };

        lock (_sync)
        {
            EnsureBitmapSizeLocked();
            _canvas.ResetMatrix();
            _canvas.DrawPath(path, paint);
            Changed();
        }

        Invalidate();
    }

    #endregion

    #region Text

    /// <summary>
    /// Draws filled text at the requested position.
    /// </summary>
    public void FillText(string? text, float x, float y) => FillText(text, x, y, double.NaN);

    /// <summary>
    /// Draws filled text at the requested position, squeezed horizontally to the maximum width if given.
    /// </summary>
    public void FillText(string? text, double x, double y, double maxWidth = double.NaN) => DrawText(text, x, y, maxWidth, isStroke: false);

    /// <summary>
    /// Draws the outline of text at the requested position, squeezed horizontally to the maximum width if given.
    /// </summary>
    public void StrokeText(string? text, double x, double y, double maxWidth = double.NaN) => DrawText(text, x, y, maxWidth, isStroke: true);

    /// <summary>
    /// Measures text in the current font.
    /// </summary>
    public CanvasTextMetrics MeasureText(string? text)
    {
        var value = NormalizeText(text);
        var font = CreateFont();
        using var paint = SkiaTextShaping.CreateTextPaint(font);
        var width = value.Length == 0 ? 0f : SkiaTextShaping.MeasureParagraph(paint, value, font, IsRightToLeft());
        var metrics = paint.FontMetrics;
        var baselineShift = GetBaselineShift(metrics);
        var bounds = new SKRect();

        if (value.Length > 0)
        {
            paint.MeasureText(value, ref bounds);
        }

        var left = GetAlignmentOffset(width);
        return new CanvasTextMetrics(
            width,
            -left - bounds.Left,
            left + bounds.Right,
            -bounds.Top - baselineShift,
            bounds.Bottom + baselineShift,
            -metrics.Ascent - baselineShift,
            metrics.Descent + baselineShift);
    }

    #endregion

    #region Images

    /// <summary>
    /// Draws an image (an <c>img</c> element, or another canvas) at its natural size.
    /// </summary>
    public void DrawImage(object image, double dx, double dy)
    {
        if (TryGetImage(image, out var source, out var width, out var height))
        {
            DrawImage(source!, 0d, 0d, width, height, dx, dy, width, height);
        }
    }

    /// <summary>
    /// Draws an image (an <c>img</c> element, or another canvas) into the given rectangle.
    /// </summary>
    public void DrawImage(object image, double dx, double dy, double dw, double dh)
    {
        if (TryGetImage(image, out var source, out var width, out var height))
        {
            DrawImage(source!, 0d, 0d, width, height, dx, dy, dw, dh);
        }
    }

    /// <summary>
    /// Draws a part of an image (an <c>img</c> element, or another canvas) into the given rectangle.
    /// </summary>
    public void DrawImage(object image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        if (TryGetImage(image, out var source, out _, out _))
        {
            DrawImage(source!, sx, sy, sw, sh, dx, dy, dw, dh);
        }
    }

    /// <summary>
    /// Creates a linear gradient along the line between the given points.
    /// </summary>
    public CanvasGradient CreateLinearGradient(double x0, double y0, double x1, double y1) =>
        CanvasGradient.CreateLinear(x0, y0, x1, y1);

    /// <summary>
    /// Creates a radial gradient between the given circles.
    /// </summary>
    public CanvasGradient CreateRadialGradient(double x0, double y0, double r0, double x1, double y1, double r1)
    {
        if (r0 < 0d || r1 < 0d)
        {
            throw new DomException(DomError.IndexSizeError);
        }

        return CanvasGradient.CreateRadial(x0, y0, r0, x1, y1, r1);
    }

    /// <summary>
    /// Creates a conic gradient around the given point, starting at the angle (in radians).
    /// </summary>
    public CanvasGradient CreateConicGradient(double startAngle, double x, double y) =>
        CanvasGradient.CreateConic(startAngle, x, y);

    /// <summary>
    /// Creates a pattern of the image, repeated as given (<c>repeat</c>, <c>repeat-x</c>,
    /// <c>repeat-y</c> or <c>no-repeat</c>), or null if the image is not ready.
    /// </summary>
    public CanvasPattern? CreatePattern(object image, string? repetition)
    {
        var mode = string.IsNullOrEmpty(repetition) ? "repeat" : repetition;

        if (mode is not ("repeat" or "repeat-x" or "repeat-y" or "no-repeat"))
        {
            throw new DomException(DomError.Syntax);
        }

        return TryGetImage(image, out var source, out _, out _) ? new CanvasPattern(source!, mode) : null;
    }

    /// <summary>
    /// Gets a copy of the bitmap (premultiplied alpha), e.g. for a frame to paint.
    /// </summary>
    internal SKImage GetSnapshot()
    {
        lock (_sync)
        {
            return GetSnapshotLocked();
        }
    }

    /// <summary>
    /// Gets what a frame shows of the canvas: what was drawn until the frame was prepared (see
    /// <see cref="Present"/>) if an interactive document presents it, else what is drawn now.
    /// </summary>
    internal SKImage? GetFrameSnapshot()
    {
        lock (_sync)
        {
            return _isPresented ? _presented : GetSnapshotLocked();
        }
    }

    /// <summary>
    /// Takes what was drawn until now for the frames to show, which happens when a frame is
    /// prepared, i.e. between the tasks of the event loop.
    /// </summary>
    internal void Present()
    {
        lock (_sync)
        {
            _isPresented = true;
            _presented = GetSnapshotLocked();
            Interlocked.Exchange(ref _invalidationRequested, 0);
        }
    }

    private SKImage GetSnapshotLocked()
    {
        EnsureBitmapSizeLocked();
        return _snapshot ??= SKImage.FromBitmap(_bitmap);
    }

    /// <summary>
    /// Gets the size of the bitmap as the canvas has it now.
    /// </summary>
    internal (int Width, int Height) GetSize() => (Math.Max(0, Host.Width), Math.Max(0, Host.Height));

    /// <inheritdoc/>
    public byte[] ToImage(string type)
    {
        var snapshot = GetSnapshot();
        var format = type?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => SKEncodedImageFormat.Jpeg,
            "image/webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        using var data = snapshot.Encode(format, 92) ?? snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data is null ? [] : data.ToArray();
    }

    #endregion

    #region Drawing

    private void Draw(SKPath devicePath, bool isStroke)
    {
        var style = isStroke ? _state.Stroke : _state.Fill;

        if (devicePath.IsEmpty || (isStroke && _state.LineWidth <= 0f))
        {
            return;
        }

        using var paint = CreatePaint(style, isStroke);

        lock (_sync)
        {
            EnsureBitmapSizeLocked();

            if (isStroke)
            {
                // a line is as wide as the transformation makes it, so it is stroked in user space
                if (!_state.Transform.TryInvert(out var inverse))
                {
                    return;
                }

                using var userPath = new SKPath(devicePath);
                userPath.Transform(inverse);
                _canvas.SetMatrix(_state.Transform);
                _canvas.DrawPath(userPath, paint);
            }
            else
            {
                _canvas.ResetMatrix();
                _canvas.DrawPath(devicePath, paint);
            }

            Changed();
        }

        Invalidate();
    }

    private void DrawText(string? text, double x, double y, double maxWidth, bool isStroke)
    {
        var value = NormalizeText(text);

        if (value.Length == 0 || !AreFinite(x, y) || maxWidth <= 0d)
        {
            return;
        }

        var font = CreateFont();
        var isRightToLeft = IsRightToLeft();
        using var paint = CreatePaint(isStroke ? _state.Stroke : _state.Fill, isStroke);
        using var textPaint = SkiaTextShaping.CreateTextPaint(font);
        textPaint.Shader = paint.Shader;
        textPaint.Color = paint.Color;
        textPaint.BlendMode = paint.BlendMode;
        textPaint.ImageFilter = paint.ImageFilter;
        textPaint.Style = paint.Style;
        textPaint.StrokeWidth = paint.StrokeWidth;
        textPaint.StrokeJoin = paint.StrokeJoin;
        textPaint.StrokeMiter = paint.StrokeMiter;
        textPaint.PathEffect = paint.PathEffect;
        var width = SkiaTextShaping.MeasureParagraph(textPaint, value, font, isRightToLeft);
        var scaleX = !double.IsNaN(maxWidth) && width > maxWidth ? (float)(maxWidth / width) : 1f;
        var left = (float)x + (GetAlignmentOffset(width) * scaleX);
        var baseline = (float)y + GetBaselineShift(textPaint.FontMetrics);

        lock (_sync)
        {
            EnsureBitmapSizeLocked();
            var matrix = _state.Transform;

            if (scaleX != 1f)
            {
                matrix = SKMatrix.Concat(matrix, SKMatrix.CreateScale(scaleX, 1f, left, 0f));
            }

            _canvas.SetMatrix(matrix);
            SkiaTextShaping.DrawParagraph(_canvas, textPaint, value, left, baseline, font, isRightToLeft);
            Changed();
        }

        Invalidate();
    }

    private void DrawImage(SKImage image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        if (!AreFinite(sx, sy, sw, sh, dx, dy, dw, dh) || sw == 0d || sh == 0d || dw == 0d || dh == 0d)
        {
            return;
        }

        var source = SKRect.Create((float)Math.Min(sx, sx + sw), (float)Math.Min(sy, sy + sh), (float)Math.Abs(sw), (float)Math.Abs(sh));
        var destination = SKRect.Create((float)Math.Min(dx, dx + dw), (float)Math.Min(dy, dy + dh), (float)Math.Abs(dw), (float)Math.Abs(dh));
        var bounds = SKRect.Create(image.Width, image.Height);
        var clipped = SKRect.Intersect(source, bounds);

        if (clipped.IsEmpty)
        {
            return;
        }

        // the part of the source outside of the image is left out, and that of the destination with it
        if (clipped != source)
        {
            var scaleX = destination.Width / source.Width;
            var scaleY = destination.Height / source.Height;
            destination = SKRect.Create(
                destination.Left + ((clipped.Left - source.Left) * scaleX),
                destination.Top + ((clipped.Top - source.Top) * scaleY),
                clipped.Width * scaleX,
                clipped.Height * scaleY);
            source = clipped;
        }

        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = _state.ImageSmoothingEnabled ? SKFilterQuality.Medium : SKFilterQuality.None,
            Color = SKColors.White.WithAlpha(ToAlpha(_state.GlobalAlpha)),
            BlendMode = _state.BlendMode,
            ImageFilter = CreateShadowFilter(),
        };

        lock (_sync)
        {
            EnsureBitmapSizeLocked();
            _canvas.SetMatrix(_state.Transform);
            _canvas.DrawImage(image, source, destination, paint);
            Changed();
        }

        Invalidate();
    }

    private SKPaint CreatePaint(PaintStyle style, bool isStroke)
    {
        var paint = new SKPaint
        {
            IsAntialias = true,
            Style = isStroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
            BlendMode = _state.BlendMode,
            ImageFilter = CreateShadowFilter(),
        };

        if (isStroke)
        {
            ApplyStroke(paint);
        }

        var alpha = _state.GlobalAlpha;

        if (style.Gradient is { } gradient)
        {
            // a fill is drawn in device space, a stroke in user space (see Draw)
            paint.Shader = gradient.CreateShader(isStroke ? SKMatrix.Identity : _state.Transform);
            paint.Color = SKColors.White.WithAlpha(ToAlpha(alpha));
        }
        else if (style.Pattern is { } pattern)
        {
            paint.Shader = pattern.CreateShader(isStroke ? SKMatrix.Identity : _state.Transform);
            paint.Color = SKColors.White.WithAlpha(ToAlpha(alpha));
            paint.FilterQuality = _state.ImageSmoothingEnabled ? SKFilterQuality.Medium : SKFilterQuality.None;
        }
        else
        {
            paint.Color = style.Color.WithAlpha((byte)Math.Round(style.Color.Alpha * alpha));
        }

        return paint;
    }

    private SKPaint CreateStrokeOutlinePaint()
    {
        var paint = new SKPaint { Style = SKPaintStyle.Stroke };
        ApplyStroke(paint);
        return paint;
    }

    private void ApplyStroke(SKPaint paint)
    {
        paint.StrokeWidth = _state.LineWidth;
        paint.StrokeCap = _state.LineCap;
        paint.StrokeJoin = _state.LineJoin;
        paint.StrokeMiter = _state.MiterLimit;

        if (_state.LineDash.Length > 0 && _state.LineDash.Any(static value => value > 0f))
        {
            paint.PathEffect = SKPathEffect.CreateDash(_state.LineDash, _state.LineDashOffset);
        }
    }

    private SKImageFilter? CreateShadowFilter()
    {
        var state = _state;

        if (state.ShadowColor.Alpha == 0 || (state.ShadowBlur == 0f && state.ShadowOffsetX == 0f && state.ShadowOffsetY == 0f))
        {
            return null;
        }

        var sigma = state.ShadowBlur / 2f;
        var color = state.ShadowColor.WithAlpha((byte)Math.Round(state.ShadowColor.Alpha * state.GlobalAlpha));
        return SKImageFilter.CreateDropShadow(state.ShadowOffsetX, state.ShadowOffsetY, sigma, sigma, color);
    }

    // Called with the lock held, after drawing. The copy may still be painted by a frame on
    // another thread, so it is left to the garbage collector.
    private void Changed() => _snapshot = null;

    // The page shows the bitmap once it is painted again, which is only a raster of the frame
    // there is (its boxes are where they were).
    private void Invalidate()
    {
        if (Host.Owner?.Context is { } context && context.TryGetDomHarness(out var harness) && harness is InteractiveHtmlRendererState state &&
            Interlocked.Exchange(ref _invalidationRequested, 1) == 0)
        {
            state.OnCanvasChanged(this);
        }
    }

    private void EnsureBitmapSize()
    {
        lock (_sync)
        {
            EnsureBitmapSizeLocked();
        }
    }

    // Setting the width or height of a canvas (even to the same value, which this can't tell)
    // resets its bitmap and the drawing state.
    private void EnsureBitmapSizeLocked()
    {
        var (width, height) = GetSize();

        if (_bitmap.Width != Math.Max(1, width) || _bitmap.Height != Math.Max(1, height))
        {
            ResetLocked();
        }
    }

    private void ResetLocked()
    {
        var (width, height) = GetSize();
        _canvas.Dispose();
        _bitmap.Dispose();
        _bitmap = CreateBitmap(width, height);
        _canvas = new SKCanvas(_bitmap);
        _state = new DrawingState();
        _stateStack.Clear();
        _path.Reset();
        _hasCurrentPoint = false;
        Changed();
    }

    private static SKBitmap CreateBitmap(int width, int height)
    {
        var bitmap = new SKBitmap(Math.Max(1, width), Math.Max(1, height), SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    #endregion

    #region Helpers

    private SKPoint Map(double x, double y) => _state.Transform.MapPoint((float)x, (float)y);

    private SKPath CreateRectPath(double x, double y, double width, double height)
    {
        var path = new SKPath();
        path.MoveTo(Map(x, y));
        path.LineTo(Map(x + width, y));
        path.LineTo(Map(x + width, y + height));
        path.LineTo(Map(x, y + height));
        path.Close();
        return path;
    }

    private void EnsureCurrentPoint(double x, double y)
    {
        if (!_hasCurrentPoint)
        {
            MoveTo(x, y);
        }
    }

    private void AppendConnected(SKPath subpath)
    {
        if (_hasCurrentPoint)
        {
            _path.AddPath(subpath, SKPathAddMode.Extend);
        }
        else
        {
            _path.AddPath(subpath, SKPathAddMode.Append);
            _hasCurrentPoint = subpath.PointCount > 0;
        }
    }

    private static SKMatrix CreateMatrix(double a, double b, double c, double d, double e, double f) => new()
    {
        ScaleX = (float)a,
        SkewY = (float)b,
        SkewX = (float)c,
        ScaleY = (float)d,
        TransX = (float)e,
        TransY = (float)f,
        Persp2 = 1f,
    };

    private static bool AreFinite(params double[] values) => values.All(double.IsFinite);

    private static SKPathFillType ToFillType(string? fillRule) =>
        string.Equals(fillRule, "evenodd", StringComparison.Ordinal) ? SKPathFillType.EvenOdd : SKPathFillType.Winding;

    private static byte ToAlpha(float alpha) => (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f);

    // Text is drawn on one line: the whitespace breaking it is a space.
    private static string NormalizeText(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace('\t', ' ').Replace('\n', ' ').Replace('\f', ' ').Replace('\r', ' ');

    private bool IsRightToLeft() => _state.Direction switch
    {
        "rtl" => true,
        "ltr" => false,
        _ => string.Equals(ResolveDirection(Host), "rtl", StringComparison.OrdinalIgnoreCase),
    };

    private float GetAlignmentOffset(float width)
    {
        var isRightToLeft = IsRightToLeft();

        return _state.TextAlign switch
        {
            "center" => -width / 2f,
            "right" => -width,
            "end" => isRightToLeft ? 0f : -width,
            "start" => isRightToLeft ? -width : 0f,
            _ => 0f,
        };
    }

    // How far the alphabetic baseline is below the given position.
    private float GetBaselineShift(SKFontMetrics metrics) => _state.TextBaseline switch
    {
        "top" => -metrics.Ascent,
        "hanging" => -metrics.Ascent * 0.8f,
        "middle" => (-metrics.Ascent - metrics.Descent) / 2f,
        "bottom" or "ideographic" => -metrics.Descent,
        _ => 0f,
    };

    private RenderFont CreateFont()
    {
        var font = _state.ParsedFont;
        return font with { Faces = GetFonts(), Language = ResolveLanguage(Host) };
    }

    // The @font-face rules of the document, e.g. a web font loaded for a game; they are looked
    // at again from time to time, as faces arrive later.
    private FontFaceSet GetFonts()
    {
        var now = Environment.TickCount64;

        if (_fonts is null || now - _fontsLoadedAt > 1000)
        {
            _fonts = Host.Owner is { } document ? FontFaceLoader.Load(document) : FontFaceSet.Empty;
            _fontsLoadedAt = now;
        }

        return _fonts;
    }

    /// <summary>
    /// The CSS <c>font</c> shorthand of the context (<c>[style] [variant] [weight] [stretch] size[/line-height] family</c>).
    /// </summary>
    private static bool TryParseFont(string value, out RenderFont font)
    {
        font = default;
        var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var isItalic = false;
        var weight = 400f;

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index].ToLowerInvariant();

            if (token is "italic" or "oblique")
            {
                isItalic = true;
            }
            else if (token is "normal" or "small-caps" or "condensed" or "expanded" or "semi-condensed" or "semi-expanded")
            {
                continue;
            }
            else if (token is "bold" or "bolder")
            {
                weight = 700f;
            }
            else if (token is "lighter")
            {
                weight = 300f;
            }
            else if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericWeight))
            {
                weight = numericWeight;
            }
            else
            {
                // the size, possibly with a line height, and then the families
                var size = token.Split('/')[0];

                if (!TryParseFontSize(size, out var pixels) || index + 1 >= tokens.Length)
                {
                    return false;
                }

                var family = string.Join(' ', tokens[(index + 1)..]);
                font = new RenderFont(family, pixels, weight, isItalic, 0f);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseFontSize(string token, out float pixels)
    {
        pixels = 0f;

        (string Unit, float Factor)[] units = [("px", 1f), ("pt", 4f / 3f), ("pc", 16f), ("rem", 16f), ("em", 10f), ("in", 96f), ("cm", 96f / 2.54f), ("mm", 96f / 25.4f), ("%", 0.1f)];

        foreach (var (unit, factor) in units)
        {
            if (token.EndsWith(unit, StringComparison.Ordinal) &&
                float.TryParse(token[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0f)
            {
                pixels = number * factor;
                return true;
            }
        }

        pixels = token switch
        {
            "xx-small" => 9f,
            "x-small" => 10f,
            "small" => 13f,
            "medium" => 16f,
            "large" => 18f,
            "x-large" => 24f,
            "xx-large" => 32f,
            "xxx-large" => 48f,
            _ => 0f,
        };

        return pixels > 0f;
    }

    private static string? ResolveLanguage(IHtmlCanvasElement host)
    {
        for (IElement? element = host; element is not null; element = element.ParentElement)
        {
            if (element.GetAttribute("lang") is { } language && !string.IsNullOrWhiteSpace(language))
            {
                return language.Trim();
            }
        }

        return null;
    }

    private static string? ResolveDirection(IHtmlCanvasElement host)
    {
        for (IElement? element = host; element is not null; element = element.ParentElement)
        {
            if (element.GetAttribute("dir") is { } direction && direction is "ltr" or "rtl")
            {
                return direction;
            }
        }

        return null;
    }

    private static PaintStyle ParseStyle(object? value, PaintStyle current) => value switch
    {
        CanvasGradient gradient => new PaintStyle(SKColors.Black, gradient, null),
        CanvasPattern pattern => new PaintStyle(SKColors.Black, null, pattern),
        string text when TryParseColor(text, out var color) => new PaintStyle(color, null, null),
        _ => current,
    };

    internal static bool TryParseColor(string? value, out SKColor color)
    {
        color = SKColors.Black;

        if (string.IsNullOrWhiteSpace(value) || !HtmlRenderer.TryParseColor(value.Trim(), out var parsed))
        {
            return false;
        }

        color = new SKColor(parsed.R, parsed.G, parsed.B, parsed.A);
        return true;
    }

    // As canvas serializes colors: #rrggbb when opaque, else rgba().
    private static string SerializeColor(SKColor color) => color.Alpha == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}")
        : string.Create(CultureInfo.InvariantCulture, $"rgba({color.Red}, {color.Green}, {color.Blue}, {(color.Alpha / 255d).ToString("0.###", CultureInfo.InvariantCulture)})");

    private static bool TryGetBlendMode(string value, out SKBlendMode mode)
    {
        mode = value switch
        {
            "source-over" => SKBlendMode.SrcOver,
            "source-in" => SKBlendMode.SrcIn,
            "source-out" => SKBlendMode.SrcOut,
            "source-atop" => SKBlendMode.SrcATop,
            "destination-over" => SKBlendMode.DstOver,
            "destination-in" => SKBlendMode.DstIn,
            "destination-out" => SKBlendMode.DstOut,
            "destination-atop" => SKBlendMode.DstATop,
            "lighter" => SKBlendMode.Plus,
            "copy" => SKBlendMode.Src,
            "xor" => SKBlendMode.Xor,
            "multiply" => SKBlendMode.Multiply,
            "screen" => SKBlendMode.Screen,
            "overlay" => SKBlendMode.Overlay,
            "darken" => SKBlendMode.Darken,
            "lighten" => SKBlendMode.Lighten,
            "color-dodge" => SKBlendMode.ColorDodge,
            "color-burn" => SKBlendMode.ColorBurn,
            "hard-light" => SKBlendMode.HardLight,
            "soft-light" => SKBlendMode.SoftLight,
            "difference" => SKBlendMode.Difference,
            "exclusion" => SKBlendMode.Exclusion,
            "hue" => SKBlendMode.Hue,
            "saturation" => SKBlendMode.Saturation,
            "color" => SKBlendMode.Color,
            "luminosity" => SKBlendMode.Luminosity,
            _ => (SKBlendMode)(-1),
        };

        return (int)mode >= 0;
    }

    /// <summary>
    /// The image of a source of drawImage or createPattern: an <c>img</c> element that is loaded
    /// (one that is not draws nothing), or a canvas.
    /// </summary>
    private bool TryGetImage(object? value, out SKImage? image, out double width, out double height)
    {
        image = value switch
        {
            IHtmlCanvasElement canvas when ReferenceEquals(canvas, Host) => GetSnapshot(),
            IHtmlCanvasElement canvas => CanvasRenderingService.GetContext(canvas)?.GetSnapshot(),
            IHtmlImageElement element => HtmlRenderer.TryDecodeLoadedImage(element),
            CanvasPattern pattern => pattern.Image,
            _ => null,
        };

        width = image?.Width ?? 0;
        height = image?.Height ?? 0;
        return image is not null && width > 0 && height > 0;
    }

    #endregion

    private readonly record struct PaintStyle(SKColor Color, CanvasGradient? Gradient, CanvasPattern? Pattern)
    {
        public object Value => (object?)Gradient ?? (object?)Pattern ?? SerializeColor(Color);
    }

    private sealed class DrawingState
    {
        public SKMatrix Transform = SKMatrix.Identity;
        public PaintStyle Fill = new(SKColors.Black, null, null);
        public PaintStyle Stroke = new(SKColors.Black, null, null);
        public float LineWidth = 1f;
        public SKStrokeCap LineCap = SKStrokeCap.Butt;
        public SKStrokeJoin LineJoin = SKStrokeJoin.Miter;
        public float MiterLimit = 10f;
        public float[] LineDash = [];
        public float LineDashOffset;
        public string Font = DefaultFont;
        public RenderFont ParsedFont = new("sans-serif", 10f, 400f, false, 0f);
        public string TextAlign = "start";
        public string TextBaseline = "alphabetic";
        public string Direction = "inherit";
        public float GlobalAlpha = 1f;
        public string CompositeOperation = "source-over";
        public SKBlendMode BlendMode = SKBlendMode.SrcOver;
        public float ShadowBlur;
        public SKColor ShadowColor = SKColors.Transparent;
        public float ShadowOffsetX;
        public float ShadowOffsetY;
        public bool ImageSmoothingEnabled = true;

        public DrawingState Clone() => (DrawingState)MemberwiseClone();
    }
}
