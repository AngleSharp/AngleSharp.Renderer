using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer;

/// <summary>
/// Defines options for HTML-to-image rendering.
/// </summary>
public sealed class HtmlRenderOptions
{
    /// <summary>
    /// Gets or sets the viewport width.
    /// </summary>
    public int Width { get; set; } = 1024;

    /// <summary>
    /// Gets or sets the viewport height.
    /// </summary>
    public int Height { get; set; } = 768;

    /// <summary>
    /// Gets or sets the background color of the output image.
    /// </summary>
    public RenderColor BackgroundColor { get; set; } = RenderColor.White;

    /// <summary>
    /// Gets or sets the foreground text color.
    /// </summary>
    public RenderColor TextColor { get; set; } = RenderColor.Black;

    /// <summary>
    /// Gets or sets the content padding in pixels.
    /// </summary>
    public float Padding { get; set; } = 16f;

    /// <summary>
    /// Gets or sets the fallback font family used by the first draft renderer.
    /// </summary>
    public string FontFamily { get; set; } = "sans-serif";

    /// <summary>
    /// Gets or sets the fallback font size in pixels.
    /// </summary>
    public float FontSize { get; set; } = 16f;

    /// <summary>
    /// Gets or sets the line-height multiplier used during layout.
    /// </summary>
    public float LineHeightMultiplier { get; set; } = 1.35f;

    /// <summary>
    /// Gets or sets the additional spacing inserted between block paragraphs.
    /// </summary>
    public float ParagraphSpacing { get; set; } = 8f;

    /// <summary>
    /// Gets or sets the average character width factor used by draft text measurement.
    /// </summary>
    public float AverageCharacterWidthFactor { get; set; } = 0.55f;
}