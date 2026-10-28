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
/// <param name="FontStretch">The CSS <c>font-stretch</c> as a percentage (100 = normal).</param>
/// <param name="IsRightToLeft">
/// Whether the text is a right-to-left run (an odd bidi embedding level). The text is still in
/// logical order; shaping puts the glyphs in visual order and mirrors them.
/// </param>
/// <param name="FontFeatures">
/// The OpenType features to shape with, in HarfBuzz syntax separated by commas (e.g.
/// <c>liga=0,tnum</c>), from <c>font-feature-settings</c>, <c>font-kerning</c> and
/// <c>font-variant-*</c>; <see langword="null"/> for the font's defaults.
/// </param>
/// <param name="Language">
/// The BCP 47 language of the text (from <c>lang</c>), which selects language-specific glyph
/// forms when shaping; <see langword="null"/> when unknown.
/// </param>
/// <param name="ContextBefore">
/// Text that logically precedes this text but is shaped elsewhere (across an inline element
/// boundary), so contextual forms such as Arabic joining continue across it. It is not measured
/// or painted.
/// </param>
/// <param name="ContextAfter">Text that logically follows this text but is shaped elsewhere.</param>
public readonly record struct RenderFont(
    string FontFamily,
    float FontSize,
    float FontWeight,
    bool IsItalic,
    float LetterSpacing,
    FontFaceSet? Faces = null,
    float FontStretch = 100f,
    bool IsRightToLeft = false,
    string? FontFeatures = null,
    string? Language = null,
    string? ContextBefore = null,
    string? ContextAfter = null);
