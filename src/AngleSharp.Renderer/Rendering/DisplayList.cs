namespace AngleSharp.Renderer.Rendering;

using System.Collections.ObjectModel;

/// <summary>
/// Represents an ordered sequence of draw commands.
/// </summary>
public sealed class DisplayList
{
    private readonly List<RenderCommand> _commands = [];

    /// <summary>
    /// Gets the recorded commands.
    /// </summary>
    public ReadOnlyCollection<RenderCommand> Commands => _commands.AsReadOnly();

    /// <summary>
    /// Gets or sets the <c>@font-face</c> declarations the text commands resolve against.
    /// </summary>
    public FontFaceSet Fonts { get; set; } = FontFaceSet.Empty;

    /// <summary>
    /// Adds a command to the list.
    /// </summary>
    /// <param name="command">The command to add.</param>
    public void Add(RenderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands.Add(command);
    }

    /// <summary>
    /// Splices commands into the list at a specific index, shifting everything from that index
    /// onward later. Used to place a box's own background/border/shadow/outline commands before
    /// its children's commands once the box's size is known - an auto-sized box cannot paint its
    /// own background until its children have been laid out to measure it, but CSS requires that
    /// background to paint *behind* those children, not on top of them.
    /// </summary>
    /// <param name="index">The position to insert at; 0 is the very start of the list.</param>
    /// <param name="commands">The commands to insert, in order.</param>
    public void InsertRange(int index, IEnumerable<RenderCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands.InsertRange(index, commands);
    }

    /// <summary>
    /// Adds a filled rectangle command.
    /// </summary>
    public void FillRect(RenderRect rect, RenderColor color) => Add(new FillRectCommand(rect, new RenderColorPaint(color)));

    /// <summary>
    /// Adds a filled rectangle command using a custom paint.
    /// </summary>
    public void FillRect(RenderRect rect, RenderPaint paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        Add(new FillRectCommand(rect, paint));
    }

    /// <summary>
    /// Adds a filled rectangle command with per-corner rounding (`border-radius`).
    /// </summary>
    public void FillRect(RenderRect rect, RenderColor color, RenderCornerRadii radii) =>
        Add(new FillRectCommand(rect, new RenderColorPaint(color), radii));

    /// <summary>
    /// Adds a filled rectangle command with per-corner rounding using a custom paint.
    /// </summary>
    public void FillRect(RenderRect rect, RenderPaint paint, RenderCornerRadii radii)
    {
        ArgumentNullException.ThrowIfNull(paint);
        Add(new FillRectCommand(rect, paint, radii));
    }

    /// <summary>
    /// Adds a stroked rounded rectangle command - used to paint a `border-radius` box's border
    /// when all four edges share the same width, so the border can be drawn as a single ring
    /// rather than four separate straight edges.
    /// </summary>
    public void StrokeRoundedRect(RenderRect rect, RenderColor color, float strokeWidth, RenderCornerRadii radii) =>
        Add(new StrokeRoundedRectCommand(rect, color, strokeWidth, radii));

    /// <summary>
    /// Adds a `box-shadow` layer, painted relative to an element's border box.
    /// </summary>
    public void DrawBoxShadow(RenderRect borderBoxRect, RenderCornerRadii borderBoxRadii, RenderBoxShadow shadow) =>
        Add(new DrawBoxShadowCommand(borderBoxRect, borderBoxRadii, shadow));

    /// <summary>
    /// Adds an image draw command.
    /// </summary>
    public void DrawImage(RenderRect rect, RenderedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Add(new DrawImageCommand(rect, image));
    }

    /// <summary>
    /// Adds a text draw command.
    /// </summary>
    public void DrawText(
        string text,
        float x,
        float y,
        RenderColor color,
        float fontSize,
        string fontFamily,
        float fontWeight = 400f,
        bool isItalic = false,
        bool underline = false,
        bool strikeThrough = false,
        RenderColor? decorationColor = null,
        RenderTextDecorationStyle decorationStyle = RenderTextDecorationStyle.Solid,
        float letterSpacing = 0f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);

        Add(new DrawTextCommand(text, x, y, color, fontSize, fontFamily, fontWeight, isItalic, underline, strikeThrough, decorationColor ?? color, decorationStyle, letterSpacing));
    }

    /// <summary>
    /// Adds a `text-shadow` layer, painted as a blurred copy of a text run behind it.
    /// </summary>
    public void DrawTextShadow(
        string text,
        float x,
        float y,
        RenderColor color,
        float fontSize,
        string fontFamily,
        float fontWeight,
        bool isItalic,
        float blurRadius,
        float letterSpacing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);

        Add(new DrawTextShadowCommand(text, x, y, color, fontSize, fontFamily, fontWeight, isItalic, blurRadius, letterSpacing));
    }
}

/// <summary>
/// Represents a command in the display list.
/// </summary>
public abstract record RenderCommand;

/// <summary>
/// Draws a filled rectangle, optionally with rounded corners (`border-radius`).
/// </summary>
public sealed record FillRectCommand(RenderRect Rect, RenderPaint Paint, RenderCornerRadii Radii = default) : RenderCommand
{
    /// <summary>
    /// Gets the solid color for this command when it uses a simple color paint.
    /// </summary>
    public RenderColor Color => Paint is RenderColorPaint colorPaint ? colorPaint.Color : RenderColor.Transparent;
}

/// <summary>
/// Strokes a rounded rectangle's outline - used for a `border-radius` box's border when every
/// edge shares the same width.
/// </summary>
public sealed record StrokeRoundedRectCommand(RenderRect Rect, RenderColor Color, float StrokeWidth, RenderCornerRadii Radii) : RenderCommand;

/// <summary>
/// Paints one `box-shadow` layer relative to an element's border box.
/// </summary>
public sealed record DrawBoxShadowCommand(RenderRect BorderBoxRect, RenderCornerRadii BorderBoxRadii, RenderBoxShadow Shadow) : RenderCommand;

/// <summary>
/// Draws a single line of text at a baseline position.
/// </summary>
public sealed record DrawTextCommand(
    string Text,
    float X,
    float Y,
    RenderColor Color,
    float FontSize,
    string FontFamily,
    float FontWeight,
    bool IsItalic,
    bool Underline,
    bool StrikeThrough,
    RenderColor DecorationColor,
    RenderTextDecorationStyle DecorationStyle,
    float LetterSpacing) : RenderCommand
{
    /// <summary>
    /// Indicates whether the command should be rendered with a bold typeface.
    /// </summary>
    public bool IsBold => FontWeight >= 600f;
}

/// <summary>
/// Paints a blurred copy of a text run behind the actual text (`text-shadow`). Carries only the
/// fields needed to shape and rasterize glyphs identically to <see cref="DrawTextCommand"/> - a
/// shadow has no underline/strike-through/decoration of its own.
/// </summary>
public sealed record DrawTextShadowCommand(
    string Text,
    float X,
    float Y,
    RenderColor Color,
    float FontSize,
    string FontFamily,
    float FontWeight,
    bool IsItalic,
    float BlurRadius,
    float LetterSpacing) : RenderCommand
{
    /// <summary>
    /// Indicates whether the command should be rendered with a bold typeface.
    /// </summary>
    public bool IsBold => FontWeight >= 600f;
}

/// <summary>
/// Describes the decoration stroke style for text.
/// </summary>
public enum RenderTextDecorationStyle
{
    /// <summary>
    /// Draw the decoration as a continuous line.
    /// </summary>
    Solid,

    /// <summary>
    /// Draw the decoration using dashes.
    /// </summary>
    Dashed,

    /// <summary>
    /// Draw the decoration using dots.
    /// </summary>
    Dotted,
}
