namespace AngleSharp.Renderer;

using System.Linq;
using System.Text;

using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// <c>writing-mode</c> and <c>text-orientation</c>.
/// </summary>
/// <remarks>
/// Layout always runs horizontally, in the logical coordinates of a <em>system</em>: x along the
/// inline axis, y along the block axis. An element whose writing mode runs along other axes than
/// the system its box sits in (orthogonally, as a vertical element in horizontal content, or with
/// the block direction reversed, as <c>vertical-lr</c> in <c>vertical-rl</c>) starts a new system:
/// its content is laid out horizontally at the origin and its commands are wrapped in the
/// transform that maps that system onto its content box (<see cref="ResolveFlowTransform"/>).
/// The physical properties of every element whose box sits in a vertical system (width/height,
/// margin/padding/border sides, insets, corners) are remapped to the logical ones when its style
/// map is built. Text in a vertical system is drawn sideways by the system's rotation; upright
/// characters are counter-rotated one by one, and <c>vertical-lr</c> (a reflection, not a rotation)
/// flips its sideways runs back. Backgrounds and box shadows inside vertical content turn with it.
/// </remarks>
public sealed partial class HtmlRenderer
{
    private enum WritingModeKind
    {
        HorizontalTb,
        VerticalRl,
        VerticalLr,
        SidewaysRl,
        SidewaysLr,
    }

    private enum TextOrientationKind
    {
        Mixed,
        Upright,
        Sideways,
    }

    private static bool IsVerticalMode(WritingModeKind mode) => mode != WritingModeKind.HorizontalTb;

    /// <summary>
    /// Whether content in <paramref name="mode"/> needs a system of its own inside
    /// <paramref name="system"/>: its axes run differently, either orthogonally or (e.g.
    /// <c>vertical-lr</c> in <c>vertical-rl</c>) with the block direction reversed.
    /// </summary>
    private static bool StartsWritingModeSystem(WritingModeKind mode, WritingModeKind system) =>
        LogicalToPhysical(mode) != LogicalToPhysical(system);

    private static WritingModeKind ParseWritingMode(string? value, WritingModeKind inherited) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "horizontal-tb" or "lr" or "lr-tb" or "rl" or "rl-tb" => WritingModeKind.HorizontalTb,
            "vertical-rl" or "tb" or "tb-rl" => WritingModeKind.VerticalRl,
            "vertical-lr" => WritingModeKind.VerticalLr,
            "sideways-rl" => WritingModeKind.SidewaysRl,
            "sideways-lr" => WritingModeKind.SidewaysLr,
            _ => inherited,
        };

    private static TextOrientationKind ParseTextOrientation(string? value, TextOrientationKind inherited) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "mixed" => TextOrientationKind.Mixed,
            "upright" => TextOrientationKind.Upright,
            "sideways" or "sideways-right" => TextOrientationKind.Sideways,
            _ => inherited,
        };

    /// <summary>
    /// The linear part of the map from a mode's logical coordinates to physical ones, in CSS
    /// <c>matrix()</c> order: x' = a·x + c·y, y' = b·x + d·y.
    /// </summary>
    private static (float A, float B, float C, float D) LogicalToPhysical(WritingModeKind mode) => mode switch
    {
        WritingModeKind.VerticalRl or WritingModeKind.SidewaysRl => (0f, 1f, -1f, 0f),
        WritingModeKind.VerticalLr => (0f, 1f, 1f, 0f),
        WritingModeKind.SidewaysLr => (0f, -1f, 1f, 0f),
        _ => (1f, 0f, 0f, 1f),
    };

    /// <summary>
    /// Records the writing mode every element's box is laid out in (the content system of its
    /// parent in the render tree, which also covers pseudo-elements), before any layout. Style
    /// maps built earlier (the root's, for the page background) are dropped so they are rebuilt
    /// with the remapping.
    /// </summary>
    private static void ResolveWritingModeSystems(IRenderNode node, WritingModeKind boxSystem, LayoutContext context)
    {
        var contentSystem = boxSystem;

        if (node is ElementRenderNode element)
        {
            var ownMode = ParseWritingMode(element.ComputedStyle.GetPropertyValue("writing-mode"), WritingModeKind.HorizontalTb);
            contentSystem = StartsWritingModeSystem(ownMode, boxSystem) ? ownMode : boxSystem;

            if (IsVerticalMode(boxSystem))
            {
                context.BoxSystems[element.Ref] = boxSystem;
                context.StyleMapCache.Remove(element.Ref);
            }

            if (IsVerticalMode(contentSystem))
            {
                context.ContentSystems[element.Ref] = contentSystem;
            }
        }

        foreach (var child in node.Children)
        {
            ResolveWritingModeSystems(child, contentSystem, context);
        }
    }

    private static WritingModeKind GetContentSystem(IElement element, LayoutContext context) =>
        context.ContentSystems.TryGetValue(element, out var system) ? system : WritingModeKind.HorizontalTb;

    /// <summary>Whether an element's content is laid out in another system than its box.</summary>
    private static bool StartsWritingModeSystem(IElement element, LayoutContext context) =>
        StartsWritingModeSystem(GetContentSystem(element, context), context.BoxSystems.TryGetValue(element, out var boxSystem) ? boxSystem : WritingModeKind.HorizontalTb);

    /// <summary>
    /// The inline size of an orthogonal flow: the box's (logical) height when it has one, else
    /// shrink-to-fit against the viewport's extent along the flow's inline axis.
    /// </summary>
    private static float ResolveOrthogonalInlineSize(ElementRenderNode node, Dictionary<string, string> styleMap, RenderTextStyle flowStyle, float specifiedHeight, float heightReference, float verticalBorderAndPadding, LayoutContext context)
    {
        var inlineSize = specifiedHeight;

        if (float.IsNaN(inlineSize))
        {
            var viewportExtent = IsVerticalMode(flowStyle.WritingMode) ? context.Height : context.Width;
            var available = Math.Max(0f, viewportExtent - (2f * context.Padding) - verticalBorderAndPadding);
            var intrinsic = ComputeContentIntrinsicWidths(node, flowStyle, available, context);
            inlineSize = Math.Min(Math.Max(intrinsic.Min, available), intrinsic.Max);
        }

        inlineSize = Math.Max(inlineSize, ResolveAuthoredDimension(styleMap, "min-height", heightReference, 0f, verticalBorderAndPadding));
        var maximum = ResolveAuthoredDimension(styleMap, "max-height", heightReference, float.NaN, verticalBorderAndPadding);
        return float.IsNaN(maximum) ? inlineSize : Math.Min(inlineSize, maximum);
    }

    /// <summary>
    /// Lays out the content of a system at its origin, as a block formatting context of the
    /// given inline size, and returns its block size. Its sticky boxes are only placed once the
    /// system is (see <see cref="ResolveFlowStickyBoxes"/>).
    /// </summary>
    private static float LayoutOrthogonalFlowContent(IReadOnlyList<IRenderNode> children, RenderTextStyle flowStyle, float inlineSize, LayoutContext context, DisplayList displayList, out List<DeferredStickyBox> stickyBoxes)
    {
        var cursorY = 0f;
        var previousMarginBottom = 0f;
        var suppressTopMargin = false;
        var textIndentConsumed = false;
        var floats = new FloatArea();
        stickyBoxes = [];
        context.StickyFlows.Push(stickyBoxes);
        context.StickyScopes.Push([]);

        try
        {
            LayoutFlowChildren(children, 0f, 0f, inlineSize, ref cursorY, ref previousMarginBottom, ref suppressTopMargin, floats, ref textIndentConsumed, flowStyle, context, displayList, float.MaxValue, float.NaN, 0f, 0f, inlineSize, float.NaN, []);

            var blockSize = cursorY + previousMarginBottom;
            blockSize = float.IsNaN(floats.Bottom) ? blockSize : Math.Max(blockSize, floats.Bottom);
            ResolveStickyBoxes(displayList, context.StickyScopes.Peek(), new RenderRect(0f, 0f, inlineSize, blockSize), context);
            return blockSize;
        }
        finally
        {
            context.StickyScopes.Pop();
            context.StickyFlows.Pop();
        }
    }

    private static bool IsItemContainer(Dictionary<string, string> styleMap) =>
        IsFlexContainer(styleMap) || IsGridContainer(styleMap) ||
        GetDisplay(styleMap) is "table" or "inline-table";

    /// <summary>
    /// The content of a flex, grid or table container in a system of its own: the container is
    /// laid out again at the origin of that system, as a bare container of the given inline size
    /// (and block size, when definite), so its items follow its own axes; its box (margins,
    /// border, padding, background, positioning) is the block laid out in the enclosing system.
    /// Returns the block size of the content.
    /// </summary>
    private static float LayoutContainerInOwnSystem(ElementRenderNode node, RenderTextStyle flowStyle, float inlineSize, float blockSize, LayoutContext context, DisplayList displayList, out List<DeferredStickyBox> stickyBoxes)
    {
        var element = node.Ref;
        var map = CreateStyleMap(node.ComputedStyle, element, context);

        if (IsVerticalMode(flowStyle.FlowMode))
        {
            RemapStyleMapToLogical(map, flowStyle.FlowMode);
        }

        foreach (var property in map.Keys.Where(IsBoxProperty).ToList())
        {
            map.Remove(property);
            (map as StyleMap)?.ClearRawLength(property);
        }

        map["width"] = FormattableString.Invariant($"{inlineSize}px");

        if (!float.IsNaN(blockSize))
        {
            map["height"] = FormattableString.Invariant($"{blockSize}px");
        }

        var hadMap = context.StyleMapCache.Remove(element, out var boxMap);
        context.StyleMapCache[element] = map;
        stickyBoxes = [];
        context.StickyFlows.Push(stickyBoxes);
        context.StickyScopes.Push([]);

        try
        {
            var cursorY = 0f;
            var previousMarginBottom = 0f;
            var suppressTopMargin = false;
            var textIndentConsumed = false;

            LayoutNode(node, 0f, 0f, inlineSize, ref cursorY, ref previousMarginBottom, ref suppressTopMargin, new FloatArea(), ref textIndentConsumed, flowStyle, context, displayList, float.MaxValue);

            ResolveStickyBoxes(displayList, context.StickyScopes.Peek(), new RenderRect(0f, 0f, inlineSize, cursorY), context);
            return cursorY;
        }
        finally
        {
            context.StickyScopes.Pop();
            context.StickyFlows.Pop();

            if (hadMap)
            {
                context.StyleMapCache[element] = boxMap!;
            }
            else
            {
                context.StyleMapCache.Remove(element);
            }
        }

        // what the box in the enclosing system already does
        static bool IsBoxProperty(string property) =>
            property.StartsWith("margin", StringComparison.Ordinal) ||
            property.StartsWith("padding", StringComparison.Ordinal) ||
            (property.StartsWith("border", StringComparison.Ordinal) && property is not ("border-collapse" or "border-spacing")) ||
            property.StartsWith("background", StringComparison.Ordinal) ||
            property.StartsWith("outline", StringComparison.Ordinal) ||
            property.StartsWith("mask", StringComparison.Ordinal) ||
            property.StartsWith("min-", StringComparison.Ordinal) ||
            property.StartsWith("max-", StringComparison.Ordinal) ||
            property.StartsWith("overflow", StringComparison.Ordinal) ||
            property is "height" or "box-shadow" or "box-sizing" or "position" or "top" or "right" or "bottom" or "left" or "inset" or
                "float" or "clear" or "transform" or "translate" or "rotate" or "scale" or "opacity" or "filter" or "backdrop-filter" or
                "z-index" or "clip-path" or "mix-blend-mode" or "aspect-ratio" or "-webkit-line-clamp" or "visibility";
    }

    /// <summary>
    /// The block size of an orthogonal flow (its width, seen from the content around it), from a
    /// layout into a scratch list that records no metrics.
    /// </summary>
    private static float MeasureOrthogonalBlockSize(ElementRenderNode element, Dictionary<string, string> styleMap, RenderTextStyle textStyle, float percentageBase, LayoutContext context)
    {
        var flowStyle = textStyle with { FlowMode = textStyle.WritingMode };
        var box = ResolveBoxStyle(styleMap, element.Ref);
        var verticalExtras = box.BorderWidth.Top + box.BorderWidth.Bottom + box.Padding.Top + box.Padding.Bottom;
        var specifiedHeight = ResolveAuthoredDimension(styleMap, "height", percentageBase, float.NaN, verticalExtras);
        var inlineSize = ResolveOrthogonalInlineSize(element, styleMap, flowStyle, specifiedHeight, percentageBase, verticalExtras, context);
        var previous = s_layoutCapture.Value;
        s_layoutCapture.Value = new LayoutCapture();

        try
        {
            var scratch = new DisplayList { Fonts = context.Fonts };
            return IsItemContainer(styleMap)
                ? LayoutContainerInOwnSystem(element, flowStyle, inlineSize, float.NaN, context, scratch, out _)
                : LayoutOrthogonalFlowContent(OrderChildrenForPainting(element.Children, context).ToList(), flowStyle, inlineSize, context, scratch, out _);
        }
        finally
        {
            s_layoutCapture.Value = previous;
        }
    }

    private static readonly string[] s_sideFamilies = ["margin-{0}", "padding-{0}", "border-{0}-width", "border-{0}-style", "border-{0}-color", "{0}"];

    /// <summary>
    /// Rewrites the physical properties of an element whose box sits in a vertical system into
    /// the logical ones the (always horizontal) layout reads: <c>width</c> is the block size,
    /// <c>margin-top</c> the block-start margin, and so on.
    /// </summary>
    private static void RemapStyleMapToLogical(Dictionary<string, string> map, WritingModeKind system)
    {
        // For each logical side (top = block-start, right = inline-end, bottom = block-end,
        // left = inline-start), the physical side it reads.
        var (top, right, bottom, left) = system switch
        {
            WritingModeKind.VerticalLr => ("left", "bottom", "right", "top"),
            WritingModeKind.SidewaysLr => ("left", "top", "right", "bottom"),
            _ => ("right", "bottom", "left", "top"),
        };
        var sides = new[] { ("top", top), ("right", right), ("bottom", bottom), ("left", left) };
        var moves = new List<(string Logical, string Physical, bool SwapPair)>();

        foreach (var family in s_sideFamilies)
        {
            foreach (var (logical, physical) in sides)
            {
                moves.Add((string.Format(family, logical), string.Format(family, physical), false));
            }
        }

        foreach (var (logical, physical) in new[] { ("width", "height"), ("height", "width"), ("min-width", "min-height"), ("min-height", "min-width"), ("max-width", "max-height"), ("max-height", "max-width"), ("overflow-x", "overflow-y"), ("overflow-y", "overflow-x") })
        {
            moves.Add((logical, physical, false));
        }

        // A logical corner is where its block side meets its inline side; its two radii swap axes.
        foreach (var (block, inline) in new[] { ("top", "left"), ("top", "right"), ("bottom", "left"), ("bottom", "right") })
        {
            var physicalBlock = sides.First(side => side.Item1 == block).Item2;
            var physicalInline = sides.First(side => side.Item1 == inline).Item2;
            var vertical = physicalBlock is "top" or "bottom" ? physicalBlock : physicalInline;
            var horizontal = physicalBlock is "left" or "right" ? physicalBlock : physicalInline;
            moves.Add(($"border-{block}-{inline}-radius", $"border-{vertical}-{horizontal}-radius", true));
        }

        var snapshot = moves.Select(move => (move.Logical, Value: map.TryGetValue(move.Physical, out var value) ? value : null, move.SwapPair)).ToList();

        foreach (var (logical, value, swapPair) in snapshot)
        {
            (map as StyleMap)?.ClearRawLength(logical);

            if (value is null)
            {
                map.Remove(logical);
                continue;
            }

            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            map[logical] = swapPair && parts.Length == 2 ? $"{parts[1]} {parts[0]}" : value;
        }
    }

    /// <summary>
    /// The transform that places the content of a system, laid out horizontally at the origin
    /// with the given inline and block size, into its content box (given in the logical
    /// coordinates of the system its box sits in): for an orthogonal flow its inline axis runs
    /// along that system's block axis, for a parallel one (<c>vertical-lr</c> in
    /// <c>vertical-rl</c>) its block axis runs the other way.
    /// </summary>
    private static RenderTransform2D ResolveFlowTransform(WritingModeKind mode, WritingModeKind boxSystem, float contentX, float contentY, float inlineSize, float blockSize)
    {
        var own = LogicalToPhysical(mode);
        var outer = LogicalToPhysical(boxSystem);

        // outer⁻¹ · own; the maps are orthogonal, so the inverse is the transpose.
        var a = (outer.A * own.A) + (outer.B * own.B);
        var b = (outer.C * own.A) + (outer.D * own.B);
        var c = (outer.A * own.C) + (outer.B * own.D);
        var d = (outer.C * own.C) + (outer.D * own.D);

        // The corner the logical origin lands on is wherever the mapped box starts.
        var corners = new[] { (0f, 0f), (inlineSize, 0f), (0f, blockSize), (inlineSize, blockSize) }
            .Select(corner => ((a * corner.Item1) + (c * corner.Item2), (b * corner.Item1) + (d * corner.Item2)))
            .ToArray();
        var minX = corners.Min(corner => corner.Item1);
        var minY = corners.Min(corner => corner.Item2);

        return new RenderTransform2D(a, b, c, d, contentX - minX, contentY - minY);
    }

    /// <summary>
    /// Whether a character stands upright in vertical text under <c>text-orientation: mixed</c>:
    /// the scripts and symbols UAX #50 gives an upright orientation (CJK, kana, Hangul, fullwidth
    /// forms, ...); everything else is set sideways.
    /// </summary>
    private static bool IsUprightByDefault(Rune rune)
    {
        var value = rune.Value;
        return value is
            (>= 0x1100 and <= 0x11FF) or
            (>= 0x2E80 and <= 0x2FDF) or
            (>= 0x2FF0 and <= 0x303F) or
            (>= 0x3040 and <= 0x31FF) or
            (>= 0x3200 and <= 0x4DBF) or
            (>= 0x4E00 and <= 0x9FFF) or
            (>= 0xA000 and <= 0xA4CF) or
            (>= 0xA960 and <= 0xA97F) or
            (>= 0xAC00 and <= 0xD7FF) or
            (>= 0xF900 and <= 0xFAFF) or
            (>= 0xFE10 and <= 0xFE1F) or
            (>= 0xFE30 and <= 0xFE4F) or
            (>= 0xFF00 and <= 0xFF60) or
            (>= 0xFFE0 and <= 0xFFE6) or
            (>= 0x1F000 and <= 0x1FAFF) or
            (>= 0x20000 and <= 0x3FFFF);
    }

    /// <summary>
    /// Whether text in this style may contain upright glyphs: it is laid out in a vertical system
    /// (not a sideways one, which sets everything sideways) and is not <c>text-orientation:
    /// sideways</c>.
    /// </summary>
    private static bool MayHaveUprightGlyphs(RenderTextStyle style) =>
        style.FlowMode is WritingModeKind.VerticalRl or WritingModeKind.VerticalLr &&
        style.TextOrientation != TextOrientationKind.Sideways;

    private static bool IsUprightGlyph(Rune rune, RenderTextStyle style) =>
        style.TextOrientation == TextOrientationKind.Upright ? !Rune.IsWhiteSpace(rune) : IsUprightByDefault(rune);

    /// <summary>Splits text into runs of sideways characters and single upright characters.</summary>
    private static IEnumerable<(string Text, bool Upright)> SplitOrientationRuns(string text, RenderTextStyle style)
    {
        var sideways = new StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsUprightGlyph(rune, style))
            {
                sideways.Append(rune.ToString());
                continue;
            }

            if (sideways.Length > 0)
            {
                yield return (sideways.ToString(), false);
                sideways.Clear();
            }

            yield return (rune.ToString(), true);
        }

        if (sideways.Length > 0)
        {
            yield return (sideways.ToString(), false);
        }
    }

    /// <summary>
    /// The inline advance of text that contains upright glyphs: each upright glyph takes one em
    /// (plus letter spacing); the sideways runs measure as usual.
    /// </summary>
    private static bool TryMeasureOrientedText(LayoutContext context, string text, RenderTextStyle style, out float width)
    {
        width = 0f;

        if (!MayHaveUprightGlyphs(style) || !text.EnumerateRunes().Any(rune => IsUprightGlyph(rune, style)))
        {
            return false;
        }

        var sidewaysStyle = style with { TextOrientation = TextOrientationKind.Sideways };

        foreach (var (run, upright) in SplitOrientationRuns(text, style))
        {
            width += upright ? style.FontSize + style.LetterSpacing : MeasureTextWidth(context, run, sidewaysStyle);
        }

        return true;
    }

    /// <summary>
    /// Paints text that contains upright glyphs: sideways runs as usual, each upright glyph
    /// counter-rotated about the centre of its em box so it stands upright on the page.
    /// </summary>
    private static bool TryDrawOrientedText(LayoutContext context, DisplayList displayList, string text, float x, float y, RenderTextStyle style, bool decorate)
    {
        if (!MayHaveUprightGlyphs(style) || !text.EnumerateRunes().Any(rune => IsUprightGlyph(rune, style)))
        {
            return false;
        }

        var sidewaysStyle = style with { TextOrientation = TextOrientationKind.Sideways };
        var map = LogicalToPhysical(style.FlowMode);
        var centerY = y - ((FormControlTextAscentRatio - FormControlTextDescentRatio) / 2f * style.FontSize);

        foreach (var (run, upright) in SplitOrientationRuns(text, style))
        {
            if (!upright)
            {
                DrawStyledText(context, displayList, run, x, y, sidewaysStyle, decorate);
                x += MeasureTextWidth(context, run, sidewaysStyle);
                continue;
            }

            // The inverse of the system's map (its transpose), about the em box's centre, cancels
            // the system's rotation for this glyph.
            var centerX = x + (style.FontSize / 2f);
            var glyphWidth = MeasureTextWidth(context, run, sidewaysStyle);
            displayList.PushTransform(new RenderTransform2D(
                map.A,
                map.C,
                map.B,
                map.D,
                centerX - ((map.A * centerX) + (map.B * centerY)),
                centerY - ((map.C * centerX) + (map.D * centerY))));
            DrawPlainText(displayList, run, centerX - (glyphWidth / 2f), y, sidewaysStyle, decorate: false);
            displayList.PopTransform();
            x += style.FontSize + style.LetterSpacing;
        }

        return true;
    }

    /// <summary>
    /// <c>vertical-lr</c> maps logical to physical coordinates by a reflection, which would mirror
    /// sideways glyphs; they are flipped back about the middle of their glyph box.
    /// </summary>
    private static bool NeedsGlyphFlip(RenderTextStyle style) => style.FlowMode == WritingModeKind.VerticalLr;

    private static RenderTransform2D GlyphFlip(float baselineY, float fontSize)
    {
        var centerY = baselineY - ((FormControlTextAscentRatio - FormControlTextDescentRatio) / 2f * fontSize);
        return new RenderTransform2D(1f, 0f, 0f, -1f, 0f, 2f * centerY);
    }
}
