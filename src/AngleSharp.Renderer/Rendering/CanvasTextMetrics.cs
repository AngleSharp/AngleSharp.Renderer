namespace AngleSharp.Renderer;

using AngleSharp.Attributes;

/// <summary>
/// The measurements of text in a 2D canvas context, see measureText.
/// </summary>
[DomName("TextMetrics")]
public sealed class CanvasTextMetrics
{
    internal CanvasTextMetrics(double width, double left, double right, double ascent, double descent, double fontAscent, double fontDescent)
    {
        Width = width;
        ActualBoundingBoxLeft = left;
        ActualBoundingBoxRight = right;
        ActualBoundingBoxAscent = ascent;
        ActualBoundingBoxDescent = descent;
        FontBoundingBoxAscent = fontAscent;
        FontBoundingBoxDescent = fontDescent;
    }

    /// <summary>
    /// Gets the advance width of the text.
    /// </summary>
    [DomName("width")]
    public double Width { get; }

    /// <summary>
    /// Gets how far the ink of the text extends to the left of the position it is drawn at.
    /// </summary>
    [DomName("actualBoundingBoxLeft")]
    public double ActualBoundingBoxLeft { get; }

    /// <summary>
    /// Gets how far the ink of the text extends to the right of the position it is drawn at.
    /// </summary>
    [DomName("actualBoundingBoxRight")]
    public double ActualBoundingBoxRight { get; }

    /// <summary>
    /// Gets how far the ink of the text extends above the baseline it is drawn at.
    /// </summary>
    [DomName("actualBoundingBoxAscent")]
    public double ActualBoundingBoxAscent { get; }

    /// <summary>
    /// Gets how far the ink of the text extends below the baseline it is drawn at.
    /// </summary>
    [DomName("actualBoundingBoxDescent")]
    public double ActualBoundingBoxDescent { get; }

    /// <summary>
    /// Gets the ascent of the font above the baseline the text is drawn at.
    /// </summary>
    [DomName("fontBoundingBoxAscent")]
    public double FontBoundingBoxAscent { get; }

    /// <summary>
    /// Gets the descent of the font below the baseline the text is drawn at.
    /// </summary>
    [DomName("fontBoundingBoxDescent")]
    public double FontBoundingBoxDescent { get; }
}
