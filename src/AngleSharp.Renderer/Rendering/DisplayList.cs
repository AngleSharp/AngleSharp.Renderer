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
    /// Moves a contiguous range of already-appended commands to the current end of the list,
    /// preserving their own relative order. Used for a stuck `position: sticky` element painted as
    /// a normal, in-sequence page child (so its layout - cursor advancement, margin collapse - stays
    /// correct) whose final on-screen position can overlap *later* siblings once actually stuck;
    /// painting it in plain document order would let those later siblings paint over it. Unlike
    /// <see cref="InsertRange"/> (which shifts everything at and after the target index later), this
    /// physically relocates the range itself - the commands before/after the moved range close the
    /// gap it leaves behind.
    /// </summary>
    /// <param name="startIndex">The first index of the range to move.</param>
    /// <param name="count">How many commands, starting at <paramref name="startIndex"/>, to move.</param>
    public void MoveRangeToEnd(int startIndex, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var moved = _commands.GetRange(startIndex, count);
        _commands.RemoveRange(startIndex, count);
        _commands.AddRange(moved);
    }

    /// <summary>
    /// Begins clipping every command up to the matching <see cref="PopClip"/> to a rectangle
    /// (`overflow: hidden`/`scroll`/`auto`). Pairs must nest like a stack, matching how a backend
    /// implements this as a save/clip/restore scope.
    /// </summary>
    public void PushClip(RenderRect rect, RenderCornerRadii radii) => Add(new PushClipCommand(rect, radii));

    /// <summary>
    /// Ends the clip scope started by the matching <see cref="PushClip"/>.
    /// </summary>
    public void PopClip() => Add(new PopClipCommand());

    /// <summary>
    /// Begins clipping every command up to the matching <see cref="PopClip"/> to an arbitrary CSS
    /// `clip-path` basic shape (`circle()`/`ellipse()`/`inset()`/`polygon()`) - unlike
    /// <see cref="PushClip"/>'s plain rounded rectangle, this wraps the *whole* element (including
    /// its own background/border/outline), matching how `clip-path` behaves in a real browser.
    /// Closed with the same <see cref="PopClip"/>/<see cref="PopClipCommand"/> every other clip
    /// scope already uses, since ending a clip scope is always just a backend "restore", regardless
    /// of what shape was clipped to.
    /// </summary>
    public void PushClipShape(RenderClipShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Add(new PushClipShapeCommand(shape));
    }

    /// <summary>
    /// Begins applying a CSS `transform` to every command up to the matching <see cref="PopTransform"/>
    /// - background, border, outline, and children all paint under it, since `transform` affects
    /// the whole element it is set on, not just its content (unlike `overflow` clipping, which
    /// explicitly excludes the border/outline it is layered under). Pairs must nest like a stack,
    /// matching how a backend implements this as a save/concat/restore scope.
    /// </summary>
    public void PushTransform(RenderTransform2D transform) => Add(new PushTransformCommand(transform));

    /// <summary>
    /// Ends the transform scope started by the matching <see cref="PushTransform"/>.
    /// </summary>
    public void PopTransform() => Add(new PopTransformCommand());

    /// <summary>
    /// Begins applying a CSS `filter` to every command up to the matching <see cref="PopFilter"/> -
    /// background, border, outline, and children all render under it, since `filter` affects the
    /// whole element it is set on, mirroring how <see cref="PushTransform"/> already wraps it.
    /// Pairs must nest like a stack, matching how a backend implements this as a save-layer/restore
    /// scope.
    /// </summary>
    public void PushFilter(IReadOnlyList<RenderFilterFunction> functions) => Add(new PushFilterCommand(functions));

    /// <summary>
    /// Begins compositing every command up to the matching <see cref="PopOpacity"/> as one
    /// semi-transparent group (CSS `opacity`) - background, border, outline, and children all
    /// blend together at <paramref name="alpha"/> as a unit, rather than each individually becoming
    /// partly transparent (which would let overlapping children show through each other at the
    /// seams). Pairs must nest like a stack, matching how a backend implements this as a
    /// save-layer/restore scope.
    /// </summary>
    public void PushOpacity(float alpha) => Add(new PushOpacityCommand(alpha));

    /// <summary>
    /// Ends the opacity scope started by the matching <see cref="PushOpacity"/>.
    /// </summary>
    public void PopOpacity() => Add(new PopOpacityCommand());

    /// <summary>
    /// Starts compositing every following command, up to the matching <see cref="PopBlendMode"/>,
    /// as one group that is blended onto what was painted before it with <paramref name="mode"/>
    /// (CSS `mix-blend-mode`). With <see cref="RenderBlendMode.Normal"/> the scope is simply an
    /// isolated group (CSS `isolation: isolate`), which bounds the backdrop that blend modes
    /// inside it can see. Pairs must nest like a stack.
    /// </summary>
    public void PushBlendMode(RenderBlendMode mode) => Add(new PushBlendModeCommand(mode));

    /// <summary>
    /// Ends the blend scope started by the matching <see cref="PushBlendMode"/>.
    /// </summary>
    public void PopBlendMode() => Add(new PopBlendModeCommand());

    /// <summary>
    /// Ends the filter scope started by the matching <see cref="PushFilter"/>.
    /// </summary>
    public void PopFilter() => Add(new PopFilterCommand());

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
    /// Adds a filled rectangle command whose gradient or image paint is sized and positioned in
    /// <paramref name="paintArea"/> (CSS `background-origin`) while only <paramref name="rect"/>
    /// is painted (CSS `background-clip`).
    /// </summary>
    public void FillRect(RenderRect rect, RenderPaint paint, RenderCornerRadii radii, RenderRect paintArea)
    {
        ArgumentNullException.ThrowIfNull(paint);
        Add(new FillRectCommand(rect, paint, radii, paintArea));
    }

    /// <summary>
    /// Adds a stroked rounded rectangle command - used to paint a `border-radius` box's border
    /// when all four edges share the same width, so the border can be drawn as a single ring
    /// rather than four separate straight edges.
    /// </summary>
    public void StrokeRoundedRect(RenderRect rect, RenderColor color, float strokeWidth, RenderCornerRadii radii) =>
        Add(new StrokeRoundedRectCommand(rect, color, strokeWidth, radii));

    /// <summary>
    /// Fills the ring between two (rounded) rectangles - the area inside <paramref name="outer"/>
    /// but outside <paramref name="inner"/>, which is how a border with rounded corners and
    /// differing edge widths is painted.
    /// </summary>
    public void FillRing(RenderRect outer, RenderCornerRadii outerRadii, RenderRect inner, RenderCornerRadii innerRadii, RenderColor color) =>
        Add(new FillRingCommand(outer, outerRadii, inner, innerRadii, color));

    /// <summary>
    /// Draws a nine-slice `border-image` into <paramref name="area"/> (the border box grown by
    /// `border-image-outset`).
    /// </summary>
    public void DrawBorderImage(RenderRect area, RenderBorderImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Add(new DrawBorderImageCommand(area, image));
    }

    /// <summary>
    /// Replaces what is already painted inside the rounded <paramref name="rect"/> with a filtered
    /// copy of itself (CSS `backdrop-filter`).
    /// </summary>
    public void DrawBackdropFilter(RenderRect rect, RenderCornerRadii radii, IReadOnlyList<RenderFilterFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        Add(new DrawBackdropFilterCommand(rect, radii, functions));
    }

    /// <summary>
    /// Starts a group that is masked by <paramref name="layers"/> (CSS `mask`) when the matching
    /// <see cref="PopMask"/> ends it. The layers are listed topmost first and add up.
    /// </summary>
    public void PushMask(IReadOnlyList<RenderMaskLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        Add(new PushMaskCommand(layers));
    }

    /// <summary>
    /// Ends the mask scope started by the matching <see cref="PushMask"/>.
    /// </summary>
    public void PopMask() => Add(new PopMaskCommand());

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
        float letterSpacing = 0f,
        float fontStretch = 100f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);

        Add(new DrawTextCommand(text, x, y, color, fontSize, fontFamily, fontWeight, isItalic, underline, strikeThrough, decorationColor ?? color, decorationStyle, letterSpacing, fontStretch));
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
        float letterSpacing,
        float fontStretch = 100f)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);

        Add(new DrawTextShadowCommand(text, x, y, color, fontSize, fontFamily, fontWeight, isItalic, blurRadius, letterSpacing, fontStretch));
    }
}

/// <summary>
/// Represents a command in the display list.
/// </summary>
public abstract record RenderCommand;

/// <summary>
/// Draws a filled rectangle, optionally with rounded corners (`border-radius`).
/// </summary>
/// <param name="Rect">The painted area (the `background-clip` box).</param>
/// <param name="Paint">The paint to fill with.</param>
/// <param name="Radii">The corner radii of <paramref name="Rect"/>.</param>
/// <param name="PaintArea">
/// The area a gradient or image paint is sized and positioned in (the `background-origin` box),
/// when it differs from <paramref name="Rect"/>. A solid color ignores it.
/// </param>
public sealed record FillRectCommand(RenderRect Rect, RenderPaint Paint, RenderCornerRadii Radii = default, RenderRect? PaintArea = null) : RenderCommand
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
/// Draws a nine-slice `border-image` into <see cref="Area"/>.
/// </summary>
public sealed record DrawBorderImageCommand(RenderRect Area, RenderBorderImage Image) : RenderCommand;

/// <summary>
/// Replaces what is painted inside the rounded <see cref="Rect"/> with a copy filtered by
/// <see cref="Functions"/> (CSS `backdrop-filter`).
/// </summary>
public sealed record DrawBackdropFilterCommand(RenderRect Rect, RenderCornerRadii Radii, IReadOnlyList<RenderFilterFunction> Functions) : RenderCommand;

/// <summary>
/// Begins a group that is masked by <see cref="Layers"/> once the matching
/// <see cref="PopMaskCommand"/> ends it (CSS `mask`). Layers are listed topmost first.
/// </summary>
public sealed record PushMaskCommand(IReadOnlyList<RenderMaskLayer> Layers) : RenderCommand;

/// <summary>
/// Ends the mask scope started by the matching <see cref="PushMaskCommand"/>.
/// </summary>
public sealed record PopMaskCommand : RenderCommand;

/// <summary>
/// Fills the area inside <see cref="Outer"/> but outside <see cref="Inner"/>, both optionally
/// with rounded corners - a border ring whose edges may differ in width.
/// </summary>
public sealed record FillRingCommand(RenderRect Outer, RenderCornerRadii OuterRadii, RenderRect Inner, RenderCornerRadii InnerRadii, RenderColor Color) : RenderCommand;

/// <summary>
/// Paints one `box-shadow` layer relative to an element's border box.
/// </summary>
public sealed record DrawBoxShadowCommand(RenderRect BorderBoxRect, RenderCornerRadii BorderBoxRadii, RenderBoxShadow Shadow) : RenderCommand;

/// <summary>
/// Begins clipping every following command, up to the matching <see cref="PopClipCommand"/>, to
/// <see cref="Rect"/> (`overflow: hidden`/`scroll`/`auto`).
/// </summary>
public sealed record PushClipCommand(RenderRect Rect, RenderCornerRadii Radii) : RenderCommand;

/// <summary>
/// Ends the clip scope started by the matching <see cref="PushClipCommand"/>.
/// </summary>
public sealed record PopClipCommand : RenderCommand;

/// <summary>
/// Begins clipping every following command, up to the matching <see cref="PopClipCommand"/>, to
/// an arbitrary CSS `clip-path` basic shape.
/// </summary>
public sealed record PushClipShapeCommand(RenderClipShape Shape) : RenderCommand;

/// <summary>
/// Begins applying a CSS `transform` to every following command, up to the matching
/// <see cref="PopTransformCommand"/>.
/// </summary>
public sealed record PushTransformCommand(RenderTransform2D Transform) : RenderCommand;

/// <summary>
/// Ends the transform scope started by the matching <see cref="PushTransformCommand"/>.
/// </summary>
public sealed record PopTransformCommand : RenderCommand;

/// <summary>
/// Begins applying a CSS `filter` to every following command, up to the matching
/// <see cref="PopFilterCommand"/> - background, border, outline, and children all render under it,
/// mirroring how <see cref="PushTransformCommand"/> wraps the whole element it is set on.
/// </summary>
public sealed record PushFilterCommand(IReadOnlyList<RenderFilterFunction> Functions) : RenderCommand;

/// <summary>
/// Ends the filter scope started by the matching <see cref="PushFilterCommand"/>.
/// </summary>
public sealed record PopFilterCommand : RenderCommand;

/// <summary>
/// Begins compositing every following command, up to the matching <see cref="PopOpacityCommand"/>,
/// as one semi-transparent group at <see cref="Alpha"/> (CSS `opacity`).
/// </summary>
public sealed record PushOpacityCommand(float Alpha) : RenderCommand;

/// <summary>
/// Ends the opacity scope started by the matching <see cref="PushOpacityCommand"/>.
/// </summary>
public sealed record PopOpacityCommand : RenderCommand;

/// <summary>
/// Begins compositing every following command, up to the matching <see cref="PopBlendModeCommand"/>,
/// as one group blended onto the backdrop with <see cref="Mode"/> (CSS `mix-blend-mode`).
/// </summary>
public sealed record PushBlendModeCommand(RenderBlendMode Mode) : RenderCommand;

/// <summary>
/// Ends the blend scope started by the matching <see cref="PushBlendModeCommand"/>.
/// </summary>
public sealed record PopBlendModeCommand : RenderCommand;

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
    float LetterSpacing,
    float FontStretch = 100f) : RenderCommand
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
    float LetterSpacing,
    float FontStretch = 100f) : RenderCommand
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

    /// <summary>
    /// Draw the decoration as a wave.
    /// </summary>
    Wavy,
}
