namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Draws an image at a given rectangle.
/// </summary>
public sealed record DrawImageCommand(RenderRect Rect, RenderedImage Image) : RenderCommand;
