namespace AngleSharp.Media.Dom;

using AngleSharp.Attributes;
using AngleSharp.Renderer;

/// <summary>
/// The members of CanvasRenderingContext2D that script uses to draw, for the contexts of
/// <see cref="CanvasRenderingService"/> (see <see cref="Canvas2DRenderingContext"/>); other
/// contexts do nothing.
/// </summary>
[DomExposed("CanvasRenderingContext2D")]
public static class CanvasRenderingContext2DExtensions
{
    /// <summary>
    /// Gets the fill style: a CSS color, a gradient or a pattern.
    /// </summary>
    [DomName("fillStyle")]
    [DomAccessor(Accessors.Getter)]
    public static object GetFillStyle(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.FillStyle : "#000000";

    /// <summary>
    /// Sets the fill style: a CSS color, a gradient or a pattern.
    /// </summary>
    [DomName("fillStyle")]
    [DomAccessor(Accessors.Setter)]
    public static void SetFillStyle(this ICanvasRenderingContext2D context, object value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.FillStyle = value;
        }
    }

    /// <summary>
    /// Gets the stroke style: a CSS color, a gradient or a pattern.
    /// </summary>
    [DomName("strokeStyle")]
    [DomAccessor(Accessors.Getter)]
    public static object GetStrokeStyle(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.StrokeStyle : "#000000";

    /// <summary>
    /// Sets the stroke style: a CSS color, a gradient or a pattern.
    /// </summary>
    [DomName("strokeStyle")]
    [DomAccessor(Accessors.Setter)]
    public static void SetStrokeStyle(this ICanvasRenderingContext2D context, object value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.StrokeStyle = value;
        }
    }

    /// <summary>
    /// Gets the width of lines.
    /// </summary>
    [DomName("lineWidth")]
    [DomAccessor(Accessors.Getter)]
    public static double GetLineWidth(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.LineWidth : 0d;

    /// <summary>
    /// Sets the width of lines.
    /// </summary>
    [DomName("lineWidth")]
    [DomAccessor(Accessors.Setter)]
    public static void SetLineWidth(this ICanvasRenderingContext2D context, double value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.LineWidth = value;
        }
    }

    /// <summary>
    /// Gets the ends of lines.
    /// </summary>
    [DomName("lineCap")]
    [DomAccessor(Accessors.Getter)]
    public static string GetLineCap(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.LineCap : string.Empty;

    /// <summary>
    /// Sets the ends of lines.
    /// </summary>
    [DomName("lineCap")]
    [DomAccessor(Accessors.Setter)]
    public static void SetLineCap(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.LineCap = value;
        }
    }

    /// <summary>
    /// Gets the corners of lines.
    /// </summary>
    [DomName("lineJoin")]
    [DomAccessor(Accessors.Getter)]
    public static string GetLineJoin(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.LineJoin : string.Empty;

    /// <summary>
    /// Sets the corners of lines.
    /// </summary>
    [DomName("lineJoin")]
    [DomAccessor(Accessors.Setter)]
    public static void SetLineJoin(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.LineJoin = value;
        }
    }

    /// <summary>
    /// Gets the miter limit.
    /// </summary>
    [DomName("miterLimit")]
    [DomAccessor(Accessors.Getter)]
    public static double GetMiterLimit(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.MiterLimit : 0d;

    /// <summary>
    /// Sets the miter limit.
    /// </summary>
    [DomName("miterLimit")]
    [DomAccessor(Accessors.Setter)]
    public static void SetMiterLimit(this ICanvasRenderingContext2D context, double value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.MiterLimit = value;
        }
    }

    /// <summary>
    /// Gets the offset of the line dash pattern.
    /// </summary>
    [DomName("lineDashOffset")]
    [DomAccessor(Accessors.Getter)]
    public static double GetLineDashOffset(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.LineDashOffset : 0d;

    /// <summary>
    /// Sets the offset of the line dash pattern.
    /// </summary>
    [DomName("lineDashOffset")]
    [DomAccessor(Accessors.Setter)]
    public static void SetLineDashOffset(this ICanvasRenderingContext2D context, double value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.LineDashOffset = value;
        }
    }

    /// <summary>
    /// Gets the font as the CSS font shorthand.
    /// </summary>
    [DomName("font")]
    [DomAccessor(Accessors.Getter)]
    public static string GetFont(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.Font : string.Empty;

    /// <summary>
    /// Sets the font as the CSS font shorthand.
    /// </summary>
    [DomName("font")]
    [DomAccessor(Accessors.Setter)]
    public static void SetFont(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Font = value;
        }
    }

    /// <summary>
    /// Gets the horizontal alignment of text.
    /// </summary>
    [DomName("textAlign")]
    [DomAccessor(Accessors.Getter)]
    public static string GetTextAlign(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.TextAlign : string.Empty;

    /// <summary>
    /// Sets the horizontal alignment of text.
    /// </summary>
    [DomName("textAlign")]
    [DomAccessor(Accessors.Setter)]
    public static void SetTextAlign(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.TextAlign = value;
        }
    }

    /// <summary>
    /// Gets the baseline of text.
    /// </summary>
    [DomName("textBaseline")]
    [DomAccessor(Accessors.Getter)]
    public static string GetTextBaseline(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.TextBaseline : string.Empty;

    /// <summary>
    /// Sets the baseline of text.
    /// </summary>
    [DomName("textBaseline")]
    [DomAccessor(Accessors.Setter)]
    public static void SetTextBaseline(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.TextBaseline = value;
        }
    }

    /// <summary>
    /// Gets the direction of text.
    /// </summary>
    [DomName("direction")]
    [DomAccessor(Accessors.Getter)]
    public static string GetDirection(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.Direction : string.Empty;

    /// <summary>
    /// Sets the direction of text.
    /// </summary>
    [DomName("direction")]
    [DomAccessor(Accessors.Setter)]
    public static void SetDirection(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Direction = value;
        }
    }

    /// <summary>
    /// Gets the alpha applied to everything drawn.
    /// </summary>
    [DomName("globalAlpha")]
    [DomAccessor(Accessors.Getter)]
    public static double GetGlobalAlpha(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.GlobalAlpha : 0d;

    /// <summary>
    /// Sets the alpha applied to everything drawn.
    /// </summary>
    [DomName("globalAlpha")]
    [DomAccessor(Accessors.Setter)]
    public static void SetGlobalAlpha(this ICanvasRenderingContext2D context, double value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.GlobalAlpha = value;
        }
    }

    /// <summary>
    /// Gets how what is drawn is composed with what is there.
    /// </summary>
    [DomName("globalCompositeOperation")]
    [DomAccessor(Accessors.Getter)]
    public static string GetGlobalCompositeOperation(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.GlobalCompositeOperation : string.Empty;

    /// <summary>
    /// Sets how what is drawn is composed with what is there.
    /// </summary>
    [DomName("globalCompositeOperation")]
    [DomAccessor(Accessors.Setter)]
    public static void SetGlobalCompositeOperation(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.GlobalCompositeOperation = value;
        }
    }

    /// <summary>
    /// Gets the blur of shadows.
    /// </summary>
    [DomName("shadowBlur")]
    [DomAccessor(Accessors.Getter)]
    public static double GetShadowBlur(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.ShadowBlur : 0d;

    /// <summary>
    /// Sets the blur of shadows.
    /// </summary>
    [DomName("shadowBlur")]
    [DomAccessor(Accessors.Setter)]
    public static void SetShadowBlur(this ICanvasRenderingContext2D context, double value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ShadowBlur = value;
        }
    }

    /// <summary>
    /// Gets the color of shadows.
    /// </summary>
    [DomName("shadowColor")]
    [DomAccessor(Accessors.Getter)]
    public static string GetShadowColor(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.ShadowColor : string.Empty;

    /// <summary>
    /// Sets the color of shadows.
    /// </summary>
    [DomName("shadowColor")]
    [DomAccessor(Accessors.Setter)]
    public static void SetShadowColor(this ICanvasRenderingContext2D context, string value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ShadowColor = value;
        }
    }

    /// <summary>
    /// Gets the horizontal offset of shadows.
    /// </summary>
    [DomName("shadowOffsetX")]
    [DomAccessor(Accessors.Getter)]
    public static double GetShadowOffsetX(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.ShadowOffsetX : 0d;

    /// <summary>
    /// Sets the horizontal offset of shadows.
    /// </summary>
    [DomName("shadowOffsetX")]
    [DomAccessor(Accessors.Setter)]
    public static void SetShadowOffsetX(this ICanvasRenderingContext2D context, double value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ShadowOffsetX = value;
        }
    }

    /// <summary>
    /// Gets the vertical offset of shadows.
    /// </summary>
    [DomName("shadowOffsetY")]
    [DomAccessor(Accessors.Getter)]
    public static double GetShadowOffsetY(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.ShadowOffsetY : 0d;

    /// <summary>
    /// Sets the vertical offset of shadows.
    /// </summary>
    [DomName("shadowOffsetY")]
    [DomAccessor(Accessors.Setter)]
    public static void SetShadowOffsetY(this ICanvasRenderingContext2D context, double value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ShadowOffsetY = value;
        }
    }

    /// <summary>
    /// Gets if scaled images are smoothed.
    /// </summary>
    [DomName("imageSmoothingEnabled")]
    [DomAccessor(Accessors.Getter)]
    public static bool GetImageSmoothingEnabled(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.ImageSmoothingEnabled : false;

    /// <summary>
    /// Sets if scaled images are smoothed.
    /// </summary>
    [DomName("imageSmoothingEnabled")]
    [DomAccessor(Accessors.Setter)]
    public static void SetImageSmoothingEnabled(this ICanvasRenderingContext2D context, bool value)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ImageSmoothingEnabled = value;
        }
    }

    /// <summary>
    /// Fills a rectangle with the fill style.
    /// </summary>
    [DomName("fillRect")]
    public static void FillRect(this ICanvasRenderingContext2D context, double x, double y, double width, double height)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.FillRect(x, y, width, height);
        }
    }

    /// <summary>
    /// Strokes the outline of a rectangle with the stroke style.
    /// </summary>
    [DomName("strokeRect")]
    public static void StrokeRect(this ICanvasRenderingContext2D context, double x, double y, double width, double height)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.StrokeRect(x, y, width, height);
        }
    }

    /// <summary>
    /// Clears a rectangle to transparent black.
    /// </summary>
    [DomName("clearRect")]
    public static void ClearRect(this ICanvasRenderingContext2D context, double x, double y, double width, double height)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ClearRect(x, y, width, height);
        }
    }

    /// <summary>
    /// Begins a new path.
    /// </summary>
    [DomName("beginPath")]
    public static void BeginPath(this ICanvasRenderingContext2D context)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.BeginPath();
        }
    }

    /// <summary>
    /// Closes the current subpath.
    /// </summary>
    [DomName("closePath")]
    public static void ClosePath(this ICanvasRenderingContext2D context)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ClosePath();
        }
    }

    /// <summary>
    /// Starts a new subpath at the given point.
    /// </summary>
    [DomName("moveTo")]
    public static void MoveTo(this ICanvasRenderingContext2D context, double x, double y)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.MoveTo(x, y);
        }
    }

    /// <summary>
    /// Adds a line to the current path.
    /// </summary>
    [DomName("lineTo")]
    public static void LineTo(this ICanvasRenderingContext2D context, double x, double y)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.LineTo(x, y);
        }
    }

    /// <summary>
    /// Adds a quadratic curve to the current path.
    /// </summary>
    [DomName("quadraticCurveTo")]
    public static void QuadraticCurveTo(this ICanvasRenderingContext2D context, double cpx, double cpy, double x, double y)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.QuadraticCurveTo(cpx, cpy, x, y);
        }
    }

    /// <summary>
    /// Adds a cubic curve to the current path.
    /// </summary>
    [DomName("bezierCurveTo")]
    public static void BezierCurveTo(this ICanvasRenderingContext2D context, double cp1x, double cp1y, double cp2x, double cp2y, double x, double y)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.BezierCurveTo(cp1x, cp1y, cp2x, cp2y, x, y);
        }
    }

    /// <summary>
    /// Adds an arc of a circle to the current path.
    /// </summary>
    [DomName("arc")]
    public static void Arc(this ICanvasRenderingContext2D context, double x, double y, double radius, double startAngle, double endAngle, bool anticlockwise = false)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Arc(x, y, radius, startAngle, endAngle, anticlockwise);
        }
    }

    /// <summary>
    /// Adds a rounded corner to the current path.
    /// </summary>
    [DomName("arcTo")]
    public static void ArcTo(this ICanvasRenderingContext2D context, double x1, double y1, double x2, double y2, double radius)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ArcTo(x1, y1, x2, y2, radius);
        }
    }

    /// <summary>
    /// Adds an arc of an ellipse to the current path.
    /// </summary>
    [DomName("ellipse")]
    public static void Ellipse(this ICanvasRenderingContext2D context, double x, double y, double radiusX, double radiusY, double rotation, double startAngle, double endAngle, bool anticlockwise = false)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Ellipse(x, y, radiusX, radiusY, rotation, startAngle, endAngle, anticlockwise);
        }
    }

    /// <summary>
    /// Adds a rectangle to the current path.
    /// </summary>
    [DomName("rect")]
    public static void Rect(this ICanvasRenderingContext2D context, double x, double y, double width, double height)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Rect(x, y, width, height);
        }
    }

    /// <summary>
    /// Adds a rectangle with rounded corners to the current path.
    /// </summary>
    [DomName("roundRect")]
    public static void RoundRect(this ICanvasRenderingContext2D context, double x, double y, double width, double height, double radii = 0d)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.RoundRect(x, y, width, height, radii);
        }
    }

    /// <summary>
    /// Fills the current path with the fill style.
    /// </summary>
    [DomName("fill")]
    public static void Fill(this ICanvasRenderingContext2D context, string fillRule = "nonzero")
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Fill(fillRule);
        }
    }

    /// <summary>
    /// Strokes the current path with the stroke style.
    /// </summary>
    [DomName("stroke")]
    public static void Stroke(this ICanvasRenderingContext2D context)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Stroke();
        }
    }

    /// <summary>
    /// Restricts drawing to the current path.
    /// </summary>
    [DomName("clip")]
    public static void Clip(this ICanvasRenderingContext2D context, string fillRule = "nonzero")
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Clip(fillRule);
        }
    }

    /// <summary>
    /// Gets if the point is inside of the current path.
    /// </summary>
    [DomName("isPointInPath")]
    public static bool IsPointInPath(this ICanvasRenderingContext2D context, double x, double y, string fillRule = "nonzero") =>
        context is Canvas2DRenderingContext canvas ? canvas.IsPointInPath(x, y, fillRule) : false;

    /// <summary>
    /// Gets if the point is on the stroke of the current path.
    /// </summary>
    [DomName("isPointInStroke")]
    public static bool IsPointInStroke(this ICanvasRenderingContext2D context, double x, double y) =>
        context is Canvas2DRenderingContext canvas ? canvas.IsPointInStroke(x, y) : false;

    /// <summary>
    /// Draws filled text.
    /// </summary>
    [DomName("fillText")]
    public static void FillText(this ICanvasRenderingContext2D context, string text, double x, double y, double maxWidth = double.NaN)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.FillText(text, x, y, maxWidth);
        }
    }

    /// <summary>
    /// Draws the outline of text.
    /// </summary>
    [DomName("strokeText")]
    public static void StrokeText(this ICanvasRenderingContext2D context, string text, double x, double y, double maxWidth = double.NaN)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.StrokeText(text, x, y, maxWidth);
        }
    }

    /// <summary>
    /// Measures text in the current font.
    /// </summary>
    [DomName("measureText")]
    public static CanvasTextMetrics? MeasureText(this ICanvasRenderingContext2D context, string text) =>
        context is Canvas2DRenderingContext canvas ? canvas.MeasureText(text) : null;

    /// <summary>
    /// Draws an image at its natural size.
    /// </summary>
    [DomName("drawImage")]
    public static void DrawImage(this ICanvasRenderingContext2D context, object image, double dx, double dy)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.DrawImage(image, dx, dy);
        }
    }

    /// <summary>
    /// Draws an image into a rectangle.
    /// </summary>
    [DomName("drawImage")]
    public static void DrawImage(this ICanvasRenderingContext2D context, object image, double dx, double dy, double dw, double dh)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.DrawImage(image, dx, dy, dw, dh);
        }
    }

    /// <summary>
    /// Draws a part of an image into a rectangle.
    /// </summary>
    [DomName("drawImage")]
    public static void DrawImage(this ICanvasRenderingContext2D context, object image, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.DrawImage(image, sx, sy, sw, sh, dx, dy, dw, dh);
        }
    }

    /// <summary>
    /// Creates a linear gradient.
    /// </summary>
    [DomName("createLinearGradient")]
    public static CanvasGradient? CreateLinearGradient(this ICanvasRenderingContext2D context, double x0, double y0, double x1, double y1) =>
        context is Canvas2DRenderingContext canvas ? canvas.CreateLinearGradient(x0, y0, x1, y1) : null;

    /// <summary>
    /// Creates a radial gradient.
    /// </summary>
    [DomName("createRadialGradient")]
    public static CanvasGradient? CreateRadialGradient(this ICanvasRenderingContext2D context, double x0, double y0, double r0, double x1, double y1, double r1) =>
        context is Canvas2DRenderingContext canvas ? canvas.CreateRadialGradient(x0, y0, r0, x1, y1, r1) : null;

    /// <summary>
    /// Creates a conic gradient.
    /// </summary>
    [DomName("createConicGradient")]
    public static CanvasGradient? CreateConicGradient(this ICanvasRenderingContext2D context, double startAngle, double x, double y) =>
        context is Canvas2DRenderingContext canvas ? canvas.CreateConicGradient(startAngle, x, y) : null;

    /// <summary>
    /// Creates a pattern of an image.
    /// </summary>
    [DomName("createPattern")]
    public static CanvasPattern? CreatePattern(this ICanvasRenderingContext2D context, object image, string repetition) =>
        context is Canvas2DRenderingContext canvas ? canvas.CreatePattern(image, repetition) : null;

    /// <summary>
    /// Translates the current transformation.
    /// </summary>
    [DomName("translate")]
    public static void Translate(this ICanvasRenderingContext2D context, double x, double y)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Translate(x, y);
        }
    }

    /// <summary>
    /// Rotates the current transformation clockwise (in radians).
    /// </summary>
    [DomName("rotate")]
    public static void Rotate(this ICanvasRenderingContext2D context, double angle)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Rotate(angle);
        }
    }

    /// <summary>
    /// Scales the current transformation.
    /// </summary>
    [DomName("scale")]
    public static void Scale(this ICanvasRenderingContext2D context, double x, double y)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Scale(x, y);
        }
    }

    /// <summary>
    /// Multiplies the current transformation with a matrix.
    /// </summary>
    [DomName("transform")]
    public static void Transform(this ICanvasRenderingContext2D context, double a, double b, double c, double d, double e, double f)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Transform(a, b, c, d, e, f);
        }
    }

    /// <summary>
    /// Replaces the current transformation.
    /// </summary>
    [DomName("setTransform")]
    public static void SetTransform(this ICanvasRenderingContext2D context, double a, double b, double c, double d, double e, double f)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.SetTransform(a, b, c, d, e, f);
        }
    }

    /// <summary>
    /// Resets the current transformation.
    /// </summary>
    [DomName("resetTransform")]
    public static void ResetTransform(this ICanvasRenderingContext2D context)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.ResetTransform();
        }
    }

    /// <summary>
    /// Sets the line dash pattern.
    /// </summary>
    [DomName("setLineDash")]
    public static void SetLineDash(this ICanvasRenderingContext2D context, double[] segments)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.SetLineDash(segments);
        }
    }

    /// <summary>
    /// Gets the line dash pattern.
    /// </summary>
    [DomName("getLineDash")]
    public static double[] GetLineDash(this ICanvasRenderingContext2D context) =>
        context is Canvas2DRenderingContext canvas ? canvas.GetLineDash() : [];

    /// <summary>
    /// Resets the bitmap and the drawing state.
    /// </summary>
    [DomName("reset")]
    public static void Reset(this ICanvasRenderingContext2D context)
    {
        if (context is Canvas2DRenderingContext canvas)
        {
            canvas.Reset();
        }
    }
}
