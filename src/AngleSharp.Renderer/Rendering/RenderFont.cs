namespace AngleSharp.Renderer.Rendering;

/// <summary>
/// Describes the font a text run is laid out and painted with.
/// </summary>
/// <param name="FontFamily">The CSS font-family list, in declaration order.</param>
/// <param name="FontSize">The font size in pixels.</param>
/// <param name="FontWeight">The numeric CSS font weight.</param>
/// <param name="IsItalic">Whether the run is italic or oblique.</param>
/// <param name="LetterSpacing">The additional spacing between characters in pixels.</param>
/// <param name="Faces">The <c>@font-face</c> declarations in scope, if any.</param>
public readonly record struct RenderFont(
    string FontFamily,
    float FontSize,
    float FontWeight,
    bool IsItalic,
    float LetterSpacing,
    FontFaceSet? Faces = null);
