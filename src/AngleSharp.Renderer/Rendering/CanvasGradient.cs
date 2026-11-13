namespace AngleSharp.Renderer;

using System.Linq;
using AngleSharp.Attributes;
using AngleSharp.Dom;
using SkiaSharp;

/// <summary>
/// A gradient to fill or stroke with in a 2D canvas context.
/// </summary>
[DomName("CanvasGradient")]
public sealed class CanvasGradient
{
    private readonly GradientKind _kind;
    private readonly double[] _geometry;
    private readonly List<(float Offset, SKColor Color)> _stops = [];

    private CanvasGradient(GradientKind kind, params double[] geometry)
    {
        _kind = kind;
        _geometry = geometry;
    }

    internal static CanvasGradient CreateLinear(double x0, double y0, double x1, double y1) => new(GradientKind.Linear, x0, y0, x1, y1);

    internal static CanvasGradient CreateRadial(double x0, double y0, double r0, double x1, double y1, double r1) => new(GradientKind.Radial, x0, y0, r0, x1, y1, r1);

    internal static CanvasGradient CreateConic(double startAngle, double x, double y) => new(GradientKind.Conic, startAngle, x, y);

    /// <summary>
    /// Adds a color at the given offset (between 0 and 1) of the gradient.
    /// </summary>
    /// <param name="offset">The offset of the color.</param>
    /// <param name="color">The CSS color.</param>
    [DomName("addColorStop")]
    public void AddColorStop(double offset, string color)
    {
        if (!(offset >= 0d && offset <= 1d))
        {
            throw new DomException(DomError.IndexSizeError);
        }

        if (!Canvas2DRenderingContext.TryParseColor(color, out var parsed))
        {
            throw new DomException(DomError.Syntax);
        }

        // stops at the same offset keep the order they were added in
        var index = _stops.FindLastIndex(stop => stop.Offset <= offset) + 1;
        _stops.Insert(index, ((float)offset, parsed));
    }

    internal SKShader? CreateShader(SKMatrix localMatrix)
    {
        if (_stops.Count == 0)
        {
            return SKShader.CreateColor(SKColors.Transparent);
        }

        var colors = _stops.Select(static stop => stop.Color).ToArray();
        var offsets = _stops.Select(static stop => stop.Offset).ToArray();
        var g = _geometry.Select(static value => (float)value).ToArray();

        return _kind switch
        {
            GradientKind.Linear when g[0] == g[2] && g[1] == g[3] => SKShader.CreateColor(SKColors.Transparent),
            GradientKind.Linear => SKShader.CreateLinearGradient(new SKPoint(g[0], g[1]), new SKPoint(g[2], g[3]), colors, offsets, SKShaderTileMode.Clamp, localMatrix),
            GradientKind.Radial => SKShader.CreateTwoPointConicalGradient(new SKPoint(g[0], g[1]), g[2], new SKPoint(g[3], g[4]), g[5], colors, offsets, SKShaderTileMode.Clamp, localMatrix),
            _ => SKShader.CreateSweepGradient(new SKPoint(g[1], g[2]), colors, offsets, SKShaderTileMode.Clamp, 0f, 360f,
                SKMatrix.Concat(localMatrix, SKMatrix.CreateRotationDegrees((float)(_geometry[0] * 180d / Math.PI), g[1], g[2]))),
        };
    }

    private enum GradientKind
    {
        Linear,
        Radial,
        Conic,
    }
}
