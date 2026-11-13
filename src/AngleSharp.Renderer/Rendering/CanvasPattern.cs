namespace AngleSharp.Renderer;

using AngleSharp.Attributes;
using SkiaSharp;

/// <summary>
/// An image repeated to fill or stroke with in a 2D canvas context.
/// </summary>
[DomName("CanvasPattern")]
public sealed class CanvasPattern
{
    private readonly string _repetition;
    private SKMatrix _transform = SKMatrix.Identity;

    internal CanvasPattern(SKImage image, string repetition)
    {
        Image = image;
        _repetition = repetition;
    }

    internal SKImage Image { get; }

    /// <summary>
    /// Sets the transformation of the pattern (the values a to f of a matrix).
    /// </summary>
    public void SetTransform(double a, double b, double c, double d, double e, double f) => _transform = new SKMatrix
    {
        ScaleX = (float)a,
        SkewY = (float)b,
        SkewX = (float)c,
        ScaleY = (float)d,
        TransX = (float)e,
        TransY = (float)f,
        Persp2 = 1f,
    };

    internal SKShader CreateShader(SKMatrix localMatrix)
    {
        var tileX = _repetition is "repeat" or "repeat-x" ? SKShaderTileMode.Repeat : SKShaderTileMode.Decal;
        var tileY = _repetition is "repeat" or "repeat-y" ? SKShaderTileMode.Repeat : SKShaderTileMode.Decal;
        return SKShader.CreateImage(Image, tileX, tileY, SKMatrix.Concat(localMatrix, _transform));
    }
}
