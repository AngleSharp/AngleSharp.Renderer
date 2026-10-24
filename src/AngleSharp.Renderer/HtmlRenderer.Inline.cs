namespace AngleSharp.Renderer;

using System.Globalization;
using System.Linq;
using System.Text;

using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Flow layout of a block container's children and the inline formatting context (line boxes).
/// </summary>
/// <remarks>
/// A block container's children are split into runs: block-level children are stacked by
/// <see cref="LayoutNode"/>, while each maximal run of inline-level content (text, inline
/// elements, inline-blocks, replaced elements, <c>&lt;br&gt;</c>) is flattened into a list of
/// <see cref="InlinePiece"/>s and broken into line boxes by <see cref="LayoutInlineFormattingContext"/>.
/// Whitespace collapsing, break opportunities and baselines are resolved across element
/// boundaries, so nested inline styles keep their own font, color and decorations.
/// </remarks>
public sealed partial class HtmlRenderer
{
    /// <summary>
    /// Lays out the in-flow children of a block container (or of the page itself), merging every
    /// run of inline-level siblings into shared line boxes.
    /// </summary>
    private static void LayoutFlowChildren(
        IReadOnlyList<IRenderNode> children,
        float contentX,
        float contentY,
        float contentWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        float containingHeight,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight,
        List<(int StartIndex, int Count)> negativeZIndexRanges)
    {
        var run = new List<IRenderNode>();
        var runHasInlineContent = false;

        void FlushRun(ref float cursorY, ref float previousBlockMarginBottom, ref bool suppressNextBlockTopMargin, FloatArea floats, ref bool textIndentConsumed)
        {
            if (run.Count == 0)
            {
                return;
            }

            if (run.All(static node => node is TextRenderNode) && !floats.HasFloatsBelow(cursorY))
            {
                // A run of bare text keeps the dedicated paragraph path, which also implements
                // `text-overflow: ellipsis` and `-webkit-line-clamp`.
                foreach (var textNode in run.Cast<TextRenderNode>())
                {
                    LayoutTextNode(textNode.Ref, contentX, contentWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed, textStyle, context, displayList, maxY);
                }
            }
            else
            {
                LayoutInlineFormattingContext(
                    run,
                    contentX,
                    contentWidth,
                    ref cursorY,
                    ref previousBlockMarginBottom,
                    ref suppressNextBlockTopMargin,
                    floats,
                    ref textIndentConsumed,
                    textStyle,
                    context,
                    displayList,
                    maxY,
                    containingHeight,
                    positionedContainingX,
                    positionedContainingY,
                    positionedContainingWidth,
                    positionedContainingHeight,
                    negativeZIndexRanges);
            }

            run.Clear();
            runHasInlineContent = false;
        }

        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var classification = ClassifyFlowChild(child, context);

            if (classification == FlowChildKind.Ignored)
            {
                continue;
            }

            if (classification == FlowChildKind.Inline)
            {
                run.Add(child);
                runHasInlineContent = true;
                continue;
            }

            // An absolutely positioned child does not interrupt a line in progress; it is laid out
            // at the static position of the line it appears in.
            if (classification == FlowChildKind.OutOfFlow && runHasInlineContent)
            {
                run.Add(child);
                continue;
            }

            FlushRun(ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed);

            var blockTextIndentConsumed = false;
            var childRangeStart = displayList.Commands.Count;

            LayoutNode(
                node: child,
                containingX: contentX,
                containingY: contentY,
                containingWidth: contentWidth,
                cursorY: ref cursorY,
                previousBlockMarginBottom: ref previousBlockMarginBottom,
                suppressNextBlockTopMargin: ref suppressNextBlockTopMargin,
                floats: floats,
                textIndentConsumed: ref blockTextIndentConsumed,
                textStyle: textStyle,
                context: context,
                displayList: displayList,
                maxY: maxY,
                containingHeight: containingHeight,
                positionedContainingX: positionedContainingX,
                positionedContainingY: positionedContainingY,
                positionedContainingWidth: positionedContainingWidth,
                positionedContainingHeight: positionedContainingHeight);

            RecordColumnBreakCandidate(cursorY);

            if (IsNegativeZIndexChild(child, context) && !IsStickyElement(child, context))
            {
                negativeZIndexRanges.Add((childRangeStart, displayList.Commands.Count - childRangeStart));
            }

            // `text-indent` only applies to the first formatted line of the container.
            if (classification == FlowChildKind.Block)
            {
                textIndentConsumed = true;
            }

            // Content below the viewport is not laid out, unless a `bottom`-sticky box further
            // down could still be pulled up into view.
            if (cursorY > maxY && !ContainsBottomStickyBox(children.Skip(index + 1), context))
            {
                return;
            }
        }

        FlushRun(ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed);
    }

    private enum FlowChildKind
    {
        Ignored,
        Inline,
        Block,
        OutOfFlow,
    }

    private static FlowChildKind ClassifyFlowChild(IRenderNode child, LayoutContext context)
    {
        if (child is TextRenderNode)
        {
            return FlowChildKind.Inline;
        }

        if (child is not ElementRenderNode element)
        {
            return FlowChildKind.Ignored;
        }

        if (!GeneratesBoxes(element))
        {
            return FlowChildKind.Ignored;
        }

        var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);

        if (IsOutOfFlowPositioned(styleMap))
        {
            return FlowChildKind.OutOfFlow;
        }

        return IsInlineLevelElement(element, styleMap) ? FlowChildKind.Inline : FlowChildKind.Block;
    }

    private static bool IsFloated(Dictionary<string, string> styleMap) => GetFloat(styleMap) is "left" or "right";

    /// <summary>
    /// Whether an element participates in an inline formatting context: <c>display: inline</c>
    /// (including the unstyled semantic inline tags), the atomic inline displays, and the
    /// replaced elements, which are inline unless explicitly made block-level.
    /// </summary>
    private static bool IsInlineLevelElement(ElementRenderNode element, Dictionary<string, string> styleMap)
    {
        if (string.Equals(element.Ref.LocalName, "br", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsAtomicInlineDisplay(element.ComputedStyle))
        {
            return true;
        }

        if (IsReplacedElement(element.Ref))
        {
            var display = GetDisplay(styleMap)?.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(display) || display is "inline" or "inline-block";
        }

        return !ShouldRenderAsBlock(element.ComputedStyle);
    }

    /// <summary>
    /// <c>inline-block</c>, <c>inline-flex</c>, <c>inline-grid</c> and <c>inline-table</c>: boxes
    /// that sit on a line as one unbreakable unit but lay out their own contents as a block.
    /// </summary>
    private static bool IsAtomicInlineDisplay(ICssStyleDeclaration computedStyle)
    {
        var display = computedStyle.GetDisplay()?.Trim();
        return string.Equals(display, "inline-block", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(display, "inline-flex", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(display, "inline-grid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(display, "inline-table", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAtomicInlineElement(ElementRenderNode element, Dictionary<string, string> styleMap) =>
        IsAtomicInlineDisplay(element.ComputedStyle) ||
        ResolveFormControlKind(element.Ref) != FormControlKind.None ||
        (IsReplacedElement(element.Ref) && IsInlineLevelElement(element, styleMap));

    private enum InlinePieceKind
    {
        Text,
        Space,
        Atomic,
        BoxStart,
        BoxEnd,
        ForcedBreak,
        OutOfFlow,
        BlockInInline,
    }

    /// <summary>
    /// An inline (non-atomic) element's box, shared by every piece it contains. Its decorations
    /// are painted per line fragment, the way <c>box-decoration-break: slice</c> does.
    /// </summary>
    private sealed class InlineBox
    {
        public required ElementRenderNode Node { get; init; }

        public required BoxStyle Box { get; init; }

        public required RenderTextStyle TextStyle { get; init; }

        public required InlineBox? Parent { get; init; }

        public float MarginLeft { get; init; }

        public float MarginRight { get; init; }

        public bool IsDecorated { get; init; }

        public float FragmentLeft { get; set; }

        public float FragmentRight { get; set; }

        public bool HasFragment { get; set; }

        public bool StartsOnLine { get; set; }

        public bool EndsOnLine { get; set; }

        public float BoundsLeft { get; set; } = float.MaxValue;

        public float BoundsTop { get; set; } = float.MaxValue;

        public float BoundsRight { get; set; } = float.MinValue;

        public float BoundsBottom { get; set; } = float.MinValue;
    }

    private sealed class InlinePiece
    {
        public InlinePieceKind Kind { get; init; }

        public string Text { get; init; } = string.Empty;

        public RenderTextStyle Style { get; init; }

        /// <summary>Identifies pieces that share one resolved text style, so they can be painted as one run.</summary>
        public int StyleId { get; init; }

        public float Width { get; set; }

        /// <summary>A soft wrap opportunity directly after this piece.</summary>
        public bool BreakAfter { get; set; }

        /// <summary>A collapsible space, dropped at the start and end of a line.</summary>
        public bool Collapsible { get; init; }

        /// <summary>Set once a piece was produced by splitting an overlong word, so it is never split again.</summary>
        public bool IsSplitResult { get; init; }

        /// <summary>The innermost inline box containing this piece, or the box itself for BoxStart/BoxEnd.</summary>
        public InlineBox? Box { get; init; }

        public ElementRenderNode? Element { get; init; }

        public AtomicInlineMeasurement Atomic { get; init; }

        public float X { get; set; }

        public bool Trimmed { get; set; }

        /// <summary>
        /// The bidi formatting characters a BoxStart/BoxEnd stands for (from `unicode-bidi`), or
        /// <see langword="null"/> when the box does not affect the bidi ordering.
        /// </summary>
        public string? BidiControl { get; init; }

        /// <summary>
        /// The width of the hyphen painted when a line breaks at the soft hyphen after this
        /// piece, or 0 when no soft hyphen follows.
        /// </summary>
        public float HyphenWidth { get; init; }

        /// <summary>A preserved tab: a space piece whose width reaches the next tab stop.</summary>
        public bool IsTab { get; init; }

        public InlinePiece WithText(string text, float width) => new()
        {
            Kind = Kind,
            Text = text,
            Style = Style,
            StyleId = StyleId,
            Width = width,
            BreakAfter = BreakAfter,
            Collapsible = Collapsible,
            IsSplitResult = IsSplitResult,
            Box = Box,
            Element = Element,
            Atomic = Atomic,
            Trimmed = Trimmed,
            BidiControl = BidiControl,
            HyphenWidth = HyphenWidth,
            IsTab = IsTab,
        };

        public bool CountsAsContent => Kind is InlinePieceKind.Text or InlinePieceKind.Atomic ||
                                       (Kind is InlinePieceKind.Space && !Collapsible) ||
                                       (Kind is InlinePieceKind.BoxStart or InlinePieceKind.BoxEnd && Box is { IsDecorated: true });
    }

    /// <summary>
    /// The outer size of an atomic inline, measured by a trial layout. <see cref="BorderBoxOffsetY"/>
    /// is where the border box starts below the top of the margin box, and <see cref="Baseline"/>
    /// is the distance from the top of the margin box to the baseline that aligns with the line.
    /// </summary>
    private readonly record struct AtomicInlineMeasurement(
        float BorderBoxWidth,
        float BorderBoxHeight,
        float MarginLeft,
        float MarginRight,
        float BorderBoxOffsetY,
        float MarginBottom,
        float Baseline,
        string VerticalAlign)
    {
        public float OuterWidth => MarginLeft + BorderBoxWidth + MarginRight;

        public float OuterHeight => BorderBoxOffsetY + BorderBoxHeight + MarginBottom;
    }

    /// <summary>
    /// Flattens an inline subtree into pieces, collapsing whitespace across element boundaries.
    /// </summary>
    private sealed class InlineContentCollector
    {
        private readonly LayoutContext _context;
        private readonly float _containingWidth;
        private readonly Dictionary<object, int> _styleIds = new(ReferenceEqualityComparer.Instance);
        private bool _previousWasCollapsibleSpace = true;
        private bool _previousEndsWord;

        public InlineContentCollector(LayoutContext context, float containingWidth)
        {
            _context = context;
            _containingWidth = containingWidth;
        }

        public List<InlinePiece> Pieces { get; } = [];

        public List<InlineBox> Boxes { get; } = [];

        public int GetStyleId(object owner)
        {
            if (!_styleIds.TryGetValue(owner, out var id))
            {
                id = _styleIds.Count + 1;
                _styleIds[owner] = id;
            }

            return id;
        }

        public void Collect(IRenderNode node, RenderTextStyle style, int styleId, InlineBox? parentBox)
        {
            switch (node)
            {
                case TextRenderNode textNode:
                    CollectText(textNode.Ref.Data, style, styleId, parentBox);
                    break;
                case ElementRenderNode element:
                    CollectElement(element, style, parentBox);
                    break;
            }
        }

        private void CollectElement(ElementRenderNode element, RenderTextStyle parentStyle, InlineBox? parentBox)
        {
            if (!GeneratesBoxes(element))
            {
                return;
            }

            if (string.Equals(element.Ref.LocalName, "br", StringComparison.OrdinalIgnoreCase))
            {
                Pieces.Add(new InlinePiece { Kind = InlinePieceKind.ForcedBreak, Style = parentStyle, Box = parentBox });
                _previousWasCollapsibleSpace = true;
                _previousEndsWord = false;
                return;
            }

            var styleMap = GetOrCreateStyleMap(_context, element.Ref, element.ComputedStyle);

            if (IsOutOfFlowPositioned(styleMap))
            {
                Pieces.Add(new InlinePiece { Kind = InlinePieceKind.OutOfFlow, Style = parentStyle, Box = parentBox, Element = element });
                return;
            }

            if (!IsInlineLevelElement(element, styleMap))
            {
                Pieces.Add(new InlinePiece { Kind = InlinePieceKind.BlockInInline, Style = parentStyle, Box = parentBox, Element = element });
                _previousWasCollapsibleSpace = true;
                _previousEndsWord = false;
                return;
            }

            if (IsAtomicInlineElement(element, styleMap))
            {
                var measurement = MeasureAtomicInline(element, _containingWidth, parentStyle, _context);
                var canWrap = !IsNoWrapWhiteSpace(parentStyle.WhiteSpace);

                // Atomic inlines are break opportunities on both sides.
                if (canWrap && Pieces.Count > 0)
                {
                    Pieces[^1].BreakAfter = true;
                }

                Pieces.Add(new InlinePiece
                {
                    Kind = InlinePieceKind.Atomic,
                    Style = parentStyle,
                    Box = parentBox,
                    Element = element,
                    Atomic = measurement,
                    Width = measurement.OuterWidth,
                    BreakAfter = canWrap,
                });
                _previousWasCollapsibleSpace = false;
                _previousEndsWord = false;
                return;
            }

            var style = ResolveTextStyle(styleMap, parentStyle);
            var styleId = GetStyleId(element);
            var children = OrderChildrenForPainting(element.Children, _context);

            if (string.Equals(GetDisplay(styleMap)?.Trim(), "contents", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var child in children)
                {
                    Collect(child, style, styleId, parentBox);
                }

                return;
            }

            var box = ResolveBoxStyle(styleMap, element.Ref);
            var marginLeft = ParseLength(styleMap, "margin-left", _containingWidth, 0f, allowAuto: true);
            var marginRight = ParseLength(styleMap, "margin-right", _containingWidth, 0f, allowAuto: true);
            var isDecorated = box.BorderWidth.Top > 0f || box.BorderWidth.Right > 0f || box.BorderWidth.Bottom > 0f || box.BorderWidth.Left > 0f ||
                              box.Padding.Top > 0f || box.Padding.Right > 0f || box.Padding.Bottom > 0f || box.Padding.Left > 0f ||
                              box.BackgroundPaint is not RenderColorPaint { Color.A: 0 };
            var inlineBox = new InlineBox
            {
                Node = element,
                Box = box,
                TextStyle = style,
                Parent = parentBox,
                MarginLeft = float.IsNaN(marginLeft) ? 0f : marginLeft,
                MarginRight = float.IsNaN(marginRight) ? 0f : marginRight,
                IsDecorated = isDecorated,
            };

            Boxes.Add(inlineBox);
            var (bidiStart, bidiEnd) = GetBidiControls(styleMap, style.Direction);
            Pieces.Add(new InlinePiece
            {
                Kind = InlinePieceKind.BoxStart,
                Style = style,
                StyleId = styleId,
                Box = inlineBox,
                Width = inlineBox.MarginLeft + box.BorderWidth.Left + box.Padding.Left,
                BidiControl = bidiStart,
            });

            foreach (var child in children)
            {
                Collect(child, style, styleId, inlineBox);
            }

            Pieces.Add(new InlinePiece
            {
                Kind = InlinePieceKind.BoxEnd,
                Style = style,
                StyleId = styleId,
                Box = inlineBox,
                Width = box.Padding.Right + box.BorderWidth.Right + inlineBox.MarginRight,
                BidiControl = bidiEnd,
            });
        }

        private void CollectText(string data, RenderTextStyle style, int styleId, InlineBox? box)
        {
            if (data.Length == 0)
            {
                return;
            }

            var whiteSpace = style.WhiteSpace;
            var collapse = whiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.Nowrap or WhiteSpaceMode.PreLine;
            var preserveNewlines = whiteSpace is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap or WhiteSpaceMode.PreLine or WhiteSpaceMode.BreakSpaces;
            var canWrap = !IsNoWrapWhiteSpace(whiteSpace);
            var breakAll = style.WordBreak == WordBreakMode.BreakAll && canWrap;
            var text = ApplyTextTransform(data.Replace("\r\n", "\n").Replace('\r', '\n'), style.TextTransform, _previousEndsWord);
            var word = new StringBuilder();
            var column = 0f;

            void FlushWord()
            {
                if (word.Length == 0)
                {
                    return;
                }

                var value = word.ToString();
                word.Clear();

                // Soft hyphens are break opportunities between syllables (unless `hyphens: none`)
                // and are never painted themselves.
                if (canWrap && !breakAll && style.Hyphens != HyphensMode.None && value.Contains(SoftHyphen))
                {
                    var syllables = value.Split(SoftHyphen, StringSplitOptions.RemoveEmptyEntries);
                    var hyphenWidth = MeasureTextWidth(_context, HyphenText, style);

                    for (var index = 0; index < syllables.Length; index++)
                    {
                        var isLast = index == syllables.Length - 1;
                        Pieces.Add(new InlinePiece
                        {
                            Kind = InlinePieceKind.Text,
                            Text = syllables[index],
                            Style = style,
                            StyleId = styleId,
                            Box = box,
                            Width = MeasureTextWidth(_context, syllables[index], style),
                            BreakAfter = !isLast,
                            HyphenWidth = isLast ? 0f : hyphenWidth,
                        });
                    }

                    return;
                }

                value = StripSoftHyphens(value);

                if (value.Length == 0)
                {
                    return;
                }

                if (breakAll)
                {
                    foreach (var rune in value.EnumerateRunes())
                    {
                        var piece = rune.ToString();
                        Pieces.Add(new InlinePiece { Kind = InlinePieceKind.Text, Text = piece, Style = style, StyleId = styleId, Box = box, Width = MeasureTextWidth(_context, piece, style), BreakAfter = true });
                    }

                    return;
                }

                Pieces.Add(new InlinePiece { Kind = InlinePieceKind.Text, Text = value, Style = style, StyleId = styleId, Box = box, Width = MeasureTextWidth(_context, value, style) });
            }

            void AddSpace(string value, bool collapsible, float? tabWidth = null)
            {
                FlushWord();
                Pieces.Add(new InlinePiece
                {
                    Kind = InlinePieceKind.Space,
                    Text = value,
                    Style = style,
                    StyleId = styleId,
                    Box = box,
                    Width = tabWidth ?? MeasureTextWidth(_context, value, style) + (style.WordSpacing * value.Length),
                    BreakAfter = canWrap,
                    Collapsible = collapsible,
                    IsTab = tabWidth.HasValue,
                });
            }

            foreach (var c in text)
            {
                if (c == '\n' && preserveNewlines)
                {
                    FlushWord();
                    Pieces.Add(new InlinePiece { Kind = InlinePieceKind.ForcedBreak, Style = style, StyleId = styleId, Box = box });
                    _previousWasCollapsibleSpace = true;
                    _previousEndsWord = false;
                    column = 0;
                    continue;
                }

                if (char.IsWhiteSpace(c) && c != ' ')
                {
                    _previousEndsWord = false;

                    if (collapse)
                    {
                        if (!_previousWasCollapsibleSpace)
                        {
                            AddSpace(" ", collapsible: true);
                            _previousWasCollapsibleSpace = true;
                        }

                        continue;
                    }

                    // Preserved white space: a tab advances to the next tab stop (`tab-size`),
                    // counted in space advances from the start of the text.
                    if (c == '\t')
                    {
                        var spaceWidth = MeasureTextWidth(_context, " ", style) + style.WordSpacing;
                        var stop = ResolveTabStopWidth(_context, style);
                        var position = column * spaceWidth;
                        var tabWidth = stop > 0f ? ((MathF.Floor((position + 0.001f) / stop) + 1f) * stop) - position : 0f;
                        AddSpace(" ", collapsible: false, tabWidth);
                        column += spaceWidth > 0f ? tabWidth / spaceWidth : 0f;
                    }
                    else
                    {
                        AddSpace(" ", collapsible: false);
                        column++;
                    }

                    _previousWasCollapsibleSpace = false;
                    continue;
                }

                word.Append(c);
                column++;
                _previousWasCollapsibleSpace = false;
                _previousEndsWord = true;
            }

            FlushWord();
        }
    }

    /// <summary>
    /// Lays out a run of inline-level content as line boxes, starting at <paramref name="cursorY"/>.
    /// </summary>
    private static void LayoutInlineFormattingContext(
        IReadOnlyList<IRenderNode> nodes,
        float contentX,
        float contentWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        float containingHeight,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight,
        List<(int StartIndex, int Count)> negativeZIndexRanges)
    {
        var collector = new InlineContentCollector(context, contentWidth);
        var rootStyleId = collector.GetStyleId(nodes);

        foreach (var node in nodes)
        {
            collector.Collect(node, textStyle, rootStyleId, parentBox: null);
        }

        var pieces = collector.Pieces;

        if (pieces.Count == 0)
        {
            return;
        }

        var lineStartIndex = 0;
        var emittedAnyLine = false;
        var lineTop = cursorY;
        var lineCount = 0;

        while (lineStartIndex < pieces.Count)
        {
            // Each line box is shortened by the floats beside it.
            var (lineLeft, lineRight) = floats.GetBand(lineTop, textStyle.FontSize * textStyle.LineHeightMultiplier, contentX, contentX + contentWidth);
            var lineAvailable = Math.Max(0f, lineRight - lineLeft);
            var indent = !textIndentConsumed ? textStyle.TextIndent + textStyle.MarkerIndent : 0f;
            var available = Math.Max(0f, lineAvailable - indent);

            // A block-level box nested in inline content gets a line of its own.
            if (pieces[lineStartIndex].Kind == InlinePieceKind.BlockInInline)
            {
                var piece = pieces[lineStartIndex];
                var blockCursorY = lineTop;
                var blockPreviousMarginBottom = 0f;
                var blockSuppressTopMargin = false;
                var blockTextIndentConsumed = false;

                LayoutNode(
                    node: piece.Element!,
                    containingX: contentX,
                    containingY: lineTop,
                    containingWidth: contentWidth,
                    cursorY: ref blockCursorY,
                    previousBlockMarginBottom: ref blockPreviousMarginBottom,
                    suppressNextBlockTopMargin: ref blockSuppressTopMargin,
                    floats: floats,
                    textIndentConsumed: ref blockTextIndentConsumed,
                    textStyle: piece.Style,
                    context: context,
                    displayList: displayList,
                    maxY: maxY,
                    containingHeight: containingHeight,
                    positionedContainingX: positionedContainingX,
                    positionedContainingY: positionedContainingY,
                    positionedContainingWidth: positionedContainingWidth,
                    positionedContainingHeight: positionedContainingHeight);

                lineTop = blockCursorY;
                lineStartIndex++;
                emittedAnyLine = true;
                textIndentConsumed = true;
                continue;
            }

            var lineEnd = BuildLine(pieces, lineStartIndex, available, context, out var forcedBreak);
            var linePieces = new List<InlinePiece>(lineEnd - lineStartIndex);

            for (var index = lineStartIndex; index < lineEnd; index++)
            {
                if (!pieces[index].Trimmed)
                {
                    linePieces.Add(pieces[index]);
                }
            }

            var hasLineBox = forcedBreak || linePieces.Any(static piece => piece.CountsAsContent);
            lineStartIndex = forcedBreak ? lineEnd + 1 : lineEnd;

            if (!forcedBreak && lineEnd < pieces.Count && pieces[lineEnd].Kind != InlinePieceKind.BlockInInline)
            {
                AppendHyphenAtSoftBreak(linePieces);
            }

            // `-webkit-line-clamp` ends the content after its last line, which shows an ellipsis
            // when anything was cut; `text-overflow: ellipsis` truncates every overflowing line.
            var isLastLine = forcedBreak || !HasContentBeforeNextBlock(pieces, lineStartIndex);
            var clamped = hasLineBox && textStyle.LineClamp > 0 && lineCount + 1 >= textStyle.LineClamp &&
                          pieces.Skip(lineStartIndex).Any(static piece => !piece.Trimmed && (piece.CountsAsContent || piece.Kind == InlinePieceKind.BlockInInline));

            if (clamped || (hasLineBox && textStyle.TextOverflow == TextOverflowMode.Ellipsis && MeasureLinePieces(linePieces) > available + 0.01f))
            {
                TruncateLineWithEllipsis(linePieces, available, textStyle, context, forceEllipsis: clamped);
            }

            if (!hasLineBox)
            {
                LayoutOutOfFlowPieces(linePieces, contentX, lineTop, contentWidth, context, displayList, maxY, containingHeight, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight);
                continue;
            }

            var lineHeight = EmitLine(
                linePieces,
                lineLeft + indent,
                available,
                lineTop,
                textStyle,
                isLastLine,
                context,
                displayList,
                contentWidth,
                containingHeight,
                positionedContainingX,
                positionedContainingY,
                positionedContainingWidth,
                positionedContainingHeight,
                negativeZIndexRanges);

            LayoutOutOfFlowPieces(linePieces, contentX, lineTop, contentWidth, context, displayList, maxY, containingHeight, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight);

            lineTop += lineHeight;
            emittedAnyLine = true;
            textIndentConsumed = true;
            lineCount++;

            if (lineTop > maxY || clamped)
            {
                break;
            }
        }

        foreach (var inlineBox in collector.Boxes)
        {
            if (inlineBox.BoundsRight >= inlineBox.BoundsLeft)
            {
                RecordLayoutMetrics(
                    inlineBox.Node.Ref,
                    inlineBox.BoundsLeft,
                    inlineBox.BoundsTop,
                    inlineBox.BoundsRight - inlineBox.BoundsLeft,
                    inlineBox.BoundsBottom - inlineBox.BoundsTop,
                    inlineBox.Box.BorderWidth.Left,
                    inlineBox.Box.BorderWidth.Right,
                    inlineBox.Box.BorderWidth.Top,
                    inlineBox.Box.BorderWidth.Bottom,
                    inlineBox.Box.Padding.Left,
                    inlineBox.Box.Padding.Right,
                    inlineBox.Box.Padding.Top,
                    inlineBox.Box.Padding.Bottom);
            }
        }

        if (emittedAnyLine)
        {
            cursorY = Math.Max(cursorY, lineTop);
            previousBlockMarginBottom = 0f;
            suppressNextBlockTopMargin = false;
        }
    }

    /// <summary>
    /// Greedily fills one line starting at <paramref name="start"/> and returns the index after
    /// its last piece. A line always takes at least one breakable unit, so an overlong unit
    /// overflows instead of looping forever.
    /// </summary>
    private static int BuildLine(List<InlinePiece> pieces, int start, float available, LayoutContext context, out bool forcedBreak)
    {
        forcedBreak = false;
        var lineWidth = 0f;
        var lineHasContent = false;
        var index = start;

        while (index < pieces.Count)
        {
            var piece = pieces[index];

            if (piece.Kind == InlinePieceKind.ForcedBreak)
            {
                forcedBreak = true;
                break;
            }

            if (piece.Kind == InlinePieceKind.BlockInInline)
            {
                break;
            }

            // Collapsible spaces at the start of a line vanish.
            if (!lineHasContent && piece.Kind == InlinePieceKind.Space && piece.Collapsible)
            {
                piece.Trimmed = true;
                index++;
                continue;
            }

            // The next unbreakable unit: everything up to and including the next soft wrap
            // opportunity.
            var unitEnd = index;
            var unitWidth = 0f;

            while (unitEnd < pieces.Count && pieces[unitEnd].Kind is not (InlinePieceKind.ForcedBreak or InlinePieceKind.BlockInInline))
            {
                unitWidth += pieces[unitEnd].Width;

                if (pieces[unitEnd].BreakAfter)
                {
                    unitEnd++;
                    break;
                }

                unitEnd++;
            }

            var trailingSpaceWidth = 0f;

            for (var back = unitEnd - 1; back >= index; back--)
            {
                var candidate = pieces[back];

                if (candidate.Kind == InlinePieceKind.Space && IsHangingSpace(candidate))
                {
                    trailingSpaceWidth += candidate.Width;
                    continue;
                }

                if (candidate.Kind is InlinePieceKind.BoxEnd or InlinePieceKind.OutOfFlow)
                {
                    continue;
                }

                break;
            }

            var fits = lineWidth + unitWidth - trailingSpaceWidth <= available + 0.01f;

            // Breaking at a soft hyphen paints a hyphen, which must fit too - unless the rest of
            // the word fits as well, so the line does not break there at all.
            var hyphenWidth = pieces[unitEnd - 1].HyphenWidth;

            if (fits && hyphenWidth > 0f && lineWidth + unitWidth + hyphenWidth > available + 0.01f)
            {
                var wordWidth = unitWidth;

                for (var rest = unitEnd; rest < pieces.Count && pieces[rest].Kind is not (InlinePieceKind.ForcedBreak or InlinePieceKind.BlockInInline or InlinePieceKind.Space); rest++)
                {
                    wordWidth += pieces[rest].Width;

                    if (pieces[rest].BreakAfter && pieces[rest].HyphenWidth == 0f)
                    {
                        break;
                    }
                }

                fits = lineWidth + wordWidth <= available + 0.01f;
            }

            if (!fits && lineHasContent)
            {
                break;
            }

            if (!fits && TrySplitOverlongUnit(pieces, index, unitEnd, available - lineWidth, context))
            {
                continue;
            }

            for (var unitIndex = index; unitIndex < unitEnd; unitIndex++)
            {
                lineHasContent |= pieces[unitIndex].CountsAsContent;
            }

            lineWidth += unitWidth;
            index = unitEnd;
        }

        // Trailing collapsible (and hanging pre-wrap) spaces take no part in alignment.
        for (var back = index - 1; back >= start; back--)
        {
            var piece = pieces[back];

            if (piece.Kind == InlinePieceKind.Space && IsHangingSpace(piece))
            {
                piece.Trimmed = true;
                continue;
            }

            if (piece.Kind is InlinePieceKind.BoxEnd or InlinePieceKind.OutOfFlow || piece.Trimmed)
            {
                continue;
            }

            break;
        }

        return index;
    }

    private static bool IsHangingSpace(InlinePiece piece) =>
        piece.Collapsible || piece.Style.WhiteSpace is WhiteSpaceMode.PreWrap;

    /// <summary>
    /// Breaks the single word of an overlong unit, as a last resort, at hyphens/slashes or (for
    /// <c>overflow-wrap: break-word</c>/<c>anywhere</c>) anywhere. Returns whether anything was split.
    /// </summary>
    private static bool TrySplitOverlongUnit(List<InlinePiece> pieces, int start, int end, float available, LayoutContext context)
    {
        var textIndex = -1;
        var otherWidth = 0f;

        for (var index = start; index < end; index++)
        {
            var piece = pieces[index];

            if (piece.Kind == InlinePieceKind.Text)
            {
                if (textIndex >= 0)
                {
                    return false;
                }

                textIndex = index;
            }
            else if (piece.Kind != InlinePieceKind.Space)
            {
                otherWidth += piece.Width;
            }
        }

        if (textIndex < 0)
        {
            return false;
        }

        var word = pieces[textIndex];

        if (word.IsSplitResult)
        {
            return false;
        }

        var limit = Math.Max(0f, available - otherWidth);
        List<string> chunks;

        if (word.Style.OverflowWrap == OverflowWrapMode.BreakWord)
        {
            chunks = SplitOverlongWord(context, word.Text, limit, word.Style);
        }
        else if (word.Text.Contains('-') || word.Text.Contains('/'))
        {
            chunks = [.. SplitBreakableWord(word.Text)];
        }
        else
        {
            return false;
        }

        if (chunks.Count <= 1)
        {
            return false;
        }

        var replacement = new List<InlinePiece>(chunks.Count);

        for (var index = 0; index < chunks.Count; index++)
        {
            replacement.Add(new InlinePiece
            {
                Kind = InlinePieceKind.Text,
                Text = chunks[index],
                Style = word.Style,
                StyleId = word.StyleId,
                Box = word.Box,
                Width = MeasureTextWidth(context, chunks[index], word.Style),
                BreakAfter = index < chunks.Count - 1 || word.BreakAfter,
                IsSplitResult = true,
            });
        }

        pieces.RemoveAt(textIndex);
        pieces.InsertRange(textIndex, replacement);
        return true;
    }

    /// <summary>
    /// `text-align: justify`: distributes the free space of a line evenly over its word
    /// separators (the untrimmed space pieces), widening them in place. Returns false when the
    /// line has no separator or no free space, in which case it stays start-aligned.
    /// </summary>
    private static bool JustifySpaces(List<InlinePiece> visualOrder, float freeSpace)
    {
        if (freeSpace <= 0f)
        {
            return false;
        }

        var spaces = 0;

        foreach (var piece in visualOrder)
        {
            if (piece.Kind == InlinePieceKind.Space)
            {
                spaces++;
            }
        }

        if (spaces == 0)
        {
            return false;
        }

        var extra = freeSpace / spaces;

        foreach (var piece in visualOrder)
        {
            if (piece.Kind == InlinePieceKind.Space)
            {
                piece.Width += extra;
            }
        }

        return true;
    }

    private static float MeasureLinePieces(List<InlinePiece> linePieces)
    {
        var width = 0f;

        foreach (var piece in linePieces)
        {
            if (piece.Kind != InlinePieceKind.OutOfFlow)
            {
                width += piece.Width;
            }
        }

        return width;
    }

    /// <summary>
    /// Cuts a line so that its content plus an ellipsis (in the block's own font) fits into
    /// <paramref name="available"/>, cutting the text piece at the edge by grapheme. Inline box
    /// edges stay, so boxes still close; atomic inlines past the cut are dropped whole. With
    /// <paramref name="forceEllipsis"/> the ellipsis is shown even when the line fits.
    /// </summary>
    private static void TruncateLineWithEllipsis(List<InlinePiece> linePieces, float available, RenderTextStyle blockStyle, LayoutContext context, bool forceEllipsis)
    {
        if (!forceEllipsis && MeasureLinePieces(linePieces) <= available + 0.01f)
        {
            return;
        }

        var ellipsisWidth = MeasureTextWidth(context, EllipsisCharacter, blockStyle);
        var budget = available - ellipsisWidth;
        var used = 0f;
        var cut = false;
        var insertAt = 0;

        for (var index = 0; index < linePieces.Count; index++)
        {
            var piece = linePieces[index];

            if (piece.Kind is InlinePieceKind.OutOfFlow)
            {
                continue;
            }

            if (piece.Kind is InlinePieceKind.BoxStart or InlinePieceKind.BoxEnd)
            {
                used += piece.Width;

                if (!cut && piece.Kind == InlinePieceKind.BoxStart)
                {
                    insertAt = index + 1;
                }

                continue;
            }

            if (cut)
            {
                linePieces.RemoveAt(index);
                index--;
                continue;
            }

            if (used + piece.Width <= budget + 0.01f)
            {
                used += piece.Width;
                insertAt = index + 1;
                continue;
            }

            cut = true;

            if (piece.Kind == InlinePieceKind.Text)
            {
                var prefix = FitTextPrefix(context, piece.Text, budget - used, piece.Style);

                if (prefix.Length > 0)
                {
                    var prefixWidth = MeasureTextWidth(context, prefix, piece.Style);
                    linePieces[index] = piece.WithText(prefix, prefixWidth);
                    used += prefixWidth;
                    insertAt = index + 1;
                    continue;
                }
            }

            linePieces.RemoveAt(index);
            index--;
        }

        // Spaces directly before the ellipsis are dropped, as at the end of any line.
        while (insertAt > 0 && linePieces[insertAt - 1].Kind == InlinePieceKind.Space)
        {
            linePieces.RemoveAt(insertAt - 1);
            insertAt--;
        }

        var owner = insertAt > 0 ? linePieces[insertAt - 1] : null;
        var style = owner is { Kind: InlinePieceKind.Text } ? owner.Style : blockStyle;
        linePieces.Insert(insertAt, new InlinePiece
        {
            Kind = InlinePieceKind.Text,
            Text = EllipsisCharacter,
            Style = style,
            StyleId = owner is { Kind: InlinePieceKind.Text } ? owner.StyleId : -1,
            Box = owner?.Box,
            Width = MeasureTextWidth(context, EllipsisCharacter, style),
        });
    }

    /// <summary>
    /// The longest grapheme-cluster prefix of <paramref name="text"/> that fits into <paramref name="width"/>.
    /// </summary>
    private static string FitTextPrefix(LayoutContext context, string text, float width, RenderTextStyle style)
    {
        var boundaries = new List<int>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            boundaries.Add(enumerator.ElementIndex + ((string)enumerator.Current).Length);
        }

        var low = 0;
        var high = boundaries.Count;

        while (low < high)
        {
            var mid = (low + high + 1) / 2;

            if (MeasureTextWidth(context, text[..boundaries[mid - 1]], style) <= width + 0.01f)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low == 0 ? string.Empty : text[..boundaries[low - 1]];
    }

    /// <summary>
    /// A line that ends inside a word at a soft hyphen shows a hyphen after its last syllable.
    /// </summary>
    private static void AppendHyphenAtSoftBreak(List<InlinePiece> linePieces)
    {
        for (var index = linePieces.Count - 1; index >= 0; index--)
        {
            var piece = linePieces[index];

            if (piece.Kind is InlinePieceKind.BoxEnd or InlinePieceKind.OutOfFlow)
            {
                continue;
            }

            if (piece.Kind == InlinePieceKind.Text && piece.HyphenWidth > 0f)
            {
                linePieces[index] = piece.WithText(piece.Text + HyphenText, piece.Width + piece.HyphenWidth);
            }

            return;
        }
    }

    /// <summary>
    /// Whether another line box follows before the end of the run or the next block-level box,
    /// i.e. whether the current line is not the last one for `text-align-last`.
    /// </summary>
    private static bool HasContentBeforeNextBlock(List<InlinePiece> pieces, int startIndex)
    {
        for (var index = startIndex; index < pieces.Count; index++)
        {
            var piece = pieces[index];

            if (piece.Kind == InlinePieceKind.BlockInInline)
            {
                return false;
            }

            if (!piece.Trimmed && piece.CountsAsContent)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Positions and paints one line box and returns its height.
    /// </summary>
    private static float EmitLine(
        List<InlinePiece> linePieces,
        float lineLeft,
        float available,
        float lineTop,
        RenderTextStyle blockStyle,
        bool isLastLine,
        LayoutContext context,
        DisplayList displayList,
        float contentWidth,
        float containingHeight,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight,
        List<(int StartIndex, int Count)> negativeZIndexRanges)
    {
        var visualOrder = ReorderLine(linePieces, blockStyle.Direction == TextDirection.Rtl, context);

        var lineWidth = 0f;

        foreach (var piece in visualOrder)
        {
            if (piece.Kind != InlinePieceKind.OutOfFlow)
            {
                lineWidth += piece.Width;
            }
        }

        var align = ResolveLineAlign(blockStyle, isLastLine);
        var justified = align == TextAlign.Justify && JustifySpaces(visualOrder, available - lineWidth);
        var x = lineLeft + (justified ? 0f : ResolveTextAlignmentOffset(ResolveStartIfJustify(align, blockStyle.Direction), available, lineWidth));

        foreach (var piece in visualOrder)
        {
            piece.X = x;

            if (piece.Kind != InlinePieceKind.OutOfFlow)
            {
                x += piece.Width;
            }
        }

        // Vertical metrics: the block's own strut, every text piece's half-leading box, and every
        // atomic inline's margin box, all aligned on one baseline.
        var strutLineHeight = blockStyle.FontSize * blockStyle.LineHeightMultiplier;
        var maxAbove = ResolveTextBaselineOffset(strutLineHeight, blockStyle.FontSize);
        var maxBelow = strutLineHeight - maxAbove;
        var edgeAlignedHeight = 0f;

        foreach (var piece in linePieces)
        {
            switch (piece.Kind)
            {
                case InlinePieceKind.Text:
                case InlinePieceKind.Space:
                {
                    var pieceLineHeight = piece.Style.FontSize * piece.Style.LineHeightMultiplier;
                    var above = ResolveTextBaselineOffset(pieceLineHeight, piece.Style.FontSize);
                    maxAbove = Math.Max(maxAbove, above);
                    maxBelow = Math.Max(maxBelow, pieceLineHeight - above);
                    break;
                }
                case InlinePieceKind.Atomic:
                {
                    var measurement = piece.Atomic;

                    if (measurement.VerticalAlign is "top" or "bottom")
                    {
                        edgeAlignedHeight = Math.Max(edgeAlignedHeight, measurement.OuterHeight);
                        break;
                    }

                    var above = ResolveAtomicAscent(measurement, blockStyle);
                    maxAbove = Math.Max(maxAbove, above);
                    maxBelow = Math.Max(maxBelow, measurement.OuterHeight - above);
                    break;
                }
            }
        }

        var lineHeight = Math.Max(maxAbove + maxBelow, edgeAlignedHeight);
        var baselineY = lineTop + maxAbove;
        RecordColumnContentStart(lineTop);
        RecordColumnBreakCandidate(lineTop + lineHeight);

        PaintInlineBoxFragments(linePieces, baselineY, displayList);

        var index = 0;

        while (index < visualOrder.Count)
        {
            var piece = visualOrder[index];

            if (piece.Kind is InlinePieceKind.Text or InlinePieceKind.Space)
            {
                // Adjacent pieces of the same text style are painted as one run, so glyphs are
                // shaped together and decorations run continuously. A text run cannot express
                // word spacing, so with it every word is painted on its own.
                var runEnd = index + 1;
                var splitAtSpaces = justified || piece.Style.WordSpacing != 0f;

                while (runEnd < visualOrder.Count &&
                       visualOrder[runEnd].Kind is InlinePieceKind.Text or InlinePieceKind.Space &&
                       visualOrder[runEnd].StyleId == piece.StyleId &&
                       ReferenceEquals(visualOrder[runEnd].Box, piece.Box) &&
                       !(splitAtSpaces && (visualOrder[runEnd].Kind == InlinePieceKind.Space || visualOrder[runEnd - 1].Kind == InlinePieceKind.Space)) &&
                       !visualOrder[runEnd].IsTab && !visualOrder[runEnd - 1].IsTab)
                {
                    runEnd++;
                }

                // Spaces at the edges of a run are only visible through text decorations; without
                // any they are left out of the painted text and only advance the pen.
                var first = index;
                var last = runEnd - 1;

                if (!piece.Style.Underline && !piece.Style.StrikeThrough)
                {
                    while (first <= last && visualOrder[first].Kind == InlinePieceKind.Space)
                    {
                        first++;
                    }

                    while (last >= first && visualOrder[last].Kind == InlinePieceKind.Space)
                    {
                        last--;
                    }
                }

                var run = new StringBuilder();

                for (var runIndex = first; runIndex <= last; runIndex++)
                {
                    run.Append(visualOrder[runIndex].Text);
                }

                var text = run.ToString();

                // A run that is nothing but a space has no glyphs to paint (DrawText rejects it).
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var runX = visualOrder[first].X;
                    var textBaseline = baselineY + piece.Style.VerticalAlignOffset;
                    DrawStyledText(context, displayList, text, runX, textBaseline, piece.Style);
                }

                index = runEnd;
                continue;
            }

            if (piece.Kind == InlinePieceKind.Atomic)
            {
                var measurement = piece.Atomic;
                var marginBoxTop = measurement.VerticalAlign switch
                {
                    "top" => lineTop,
                    "bottom" => lineTop + lineHeight - measurement.OuterHeight,
                    _ => baselineY - ResolveAtomicAscent(measurement, blockStyle),
                };

                var atomicCursorY = marginBoxTop;
                var atomicPreviousMarginBottom = 0f;
                var atomicSuppressTopMargin = false;
                var atomicFloats = new FloatArea();
                var atomicTextIndentConsumed = false;
                var rangeStart = displayList.Commands.Count;

                LayoutNode(
                    node: piece.Element!,
                    containingX: piece.X,
                    containingY: marginBoxTop,
                    containingWidth: contentWidth,
                    cursorY: ref atomicCursorY,
                    previousBlockMarginBottom: ref atomicPreviousMarginBottom,
                    suppressNextBlockTopMargin: ref atomicSuppressTopMargin,
                    floats: atomicFloats,
                    textIndentConsumed: ref atomicTextIndentConsumed,
                    textStyle: piece.Style,
                    context: context,
                    displayList: displayList,
                    maxY: float.MaxValue,
                    containingHeight: containingHeight,
                    positionedContainingX: positionedContainingX,
                    positionedContainingY: positionedContainingY,
                    positionedContainingWidth: positionedContainingWidth,
                    positionedContainingHeight: positionedContainingHeight,
                    forcedBorderBoxWidth: measurement.BorderBoxWidth,
                    isAtomicInline: true);

                if (IsNegativeZIndexChild(piece.Element!, context))
                {
                    negativeZIndexRanges.Add((rangeStart, displayList.Commands.Count - rangeStart));
                }
            }

            index++;
        }

        // Recorded after the atomic children so it is this line's baseline - not one they recorded
        // while being laid out - that an enclosing inline-block aligns with.
        RecordTextBaseline(baselineY);
        return lineHeight;
    }

    /// <summary>
    /// The bidi formatting characters that open and close an inline box with `unicode-bidi`
    /// (CSS Writing Modes 3, section 2.4.2): `embed` and `bidi-override` are embeddings, `isolate`
    /// and `isolate-override` are isolates, `plaintext` is a first-strong isolate.
    /// </summary>
    private static (string? Start, string? End) GetBidiControls(Dictionary<string, string> styleMap, TextDirection direction)
    {
        if (!styleMap.TryGetValue("unicode-bidi", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var isRightToLeft = direction == TextDirection.Rtl;
        var embedding = isRightToLeft ? BidiAlgorithm.RightToLeftEmbedding : BidiAlgorithm.LeftToRightEmbedding;
        var isolate = isRightToLeft ? BidiAlgorithm.RightToLeftIsolate : BidiAlgorithm.LeftToRightIsolate;
        var @override = isRightToLeft ? BidiAlgorithm.RightToLeftOverride : BidiAlgorithm.LeftToRightOverride;

        return value.Trim().ToLowerInvariant() switch
        {
            "embed" => (embedding.ToString(), BidiAlgorithm.PopDirectionalFormatting.ToString()),
            "isolate" => (isolate.ToString(), BidiAlgorithm.PopDirectionalIsolate.ToString()),
            "bidi-override" => (@override.ToString(), BidiAlgorithm.PopDirectionalFormatting.ToString()),
            "isolate-override" => ($"{isolate}{@override}", $"{BidiAlgorithm.PopDirectionalFormatting}{BidiAlgorithm.PopDirectionalIsolate}"),
            "plaintext" => ("\u2068", BidiAlgorithm.PopDirectionalIsolate.ToString()),
            _ => (null, null),
        };
    }

    /// <summary>
    /// The pieces of one line in visual order (UAX #9). The line's text is the pieces' text, with
    /// atomic inlines as object replacement characters and box edges as their bidi controls (or a
    /// neutral placeholder); text pieces are split where their embedding level changes, and the
    /// parts at an odd (right-to-left) level are reversed and mirrored.
    /// </summary>
    private static List<InlinePiece> ReorderLine(List<InlinePiece> linePieces, bool isRightToLeft, LayoutContext context)
    {
        var text = new StringBuilder();
        var owners = new List<int>();
        var outOfFlow = new List<InlinePiece>();

        for (var index = 0; index < linePieces.Count; index++)
        {
            var piece = linePieces[index];
            var content = piece.Kind switch
            {
                InlinePieceKind.Text or InlinePieceKind.Space => piece.Text,
                InlinePieceKind.Atomic => BidiAlgorithm.ObjectReplacement.ToString(),
                InlinePieceKind.OutOfFlow => null,
                _ => piece.BidiControl ?? BidiAlgorithm.Placeholder.ToString(),
            };

            if (content is null)
            {
                outOfFlow.Add(piece);
                continue;
            }

            text.Append(content);

            for (var unit = 0; unit < content.Length; unit++)
            {
                owners.Add(index);
            }
        }

        var lineText = text.ToString();

        if (!BidiAlgorithm.RequiresReordering(lineText, isRightToLeft))
        {
            return linePieces;
        }

        var levels = BidiAlgorithm.GetLevels(lineText, isRightToLeft);
        var order = BidiAlgorithm.GetVisualOrder(levels);
        var pieceStarts = new int[linePieces.Count];

        for (var unit = owners.Count - 1; unit >= 0; unit--)
        {
            pieceStarts[owners[unit]] = unit;
        }

        var result = new List<InlinePiece>(linePieces.Count + 4);
        var position = 0;

        while (position < order.Length)
        {
            // A fragment is a run of visually adjacent units of one piece at one level, which is
            // also a contiguous range of that piece's text.
            var owner = owners[order[position]];
            var level = levels[order[position]];
            var end = position;

            while (end + 1 < order.Length && owners[order[end + 1]] == owner && levels[order[end + 1]] == level)
            {
                end++;
            }

            var piece = linePieces[owner];

            if (piece.Kind is InlinePieceKind.Text or InlinePieceKind.Space)
            {
                var first = Math.Min(order[position], order[end]) - pieceStarts[owner];
                var last = Math.Max(order[position], order[end]) - pieceStarts[owner];
                var fragment = piece.Text.Substring(first, last - first + 1);

                if ((level & 1) == 1)
                {
                    fragment = ReverseForDisplay(fragment);
                }

                if (first == 0 && last == piece.Text.Length - 1 && (level & 1) == 0)
                {
                    result.Add(piece);
                }
                else
                {
                    var width = piece.Kind == InlinePieceKind.Space && fragment.Length == piece.Text.Length
                        ? piece.Width
                        : MeasureTextWidth(context, fragment, piece.Style) + (piece.Kind == InlinePieceKind.Space ? piece.Style.WordSpacing * fragment.Length : 0f);
                    result.Add(piece.WithText(fragment, width));
                }
            }
            else if (position == end || !result.Contains(piece))
            {
                result.Add(piece);
            }

            position = end + 1;
        }

        result.AddRange(outOfFlow);
        return result;
    }

    /// <summary>
    /// Right-to-left text in visual order: reversed by grapheme cluster (so combining marks and
    /// surrogate pairs stay intact) and mirrored.
    /// </summary>
    private static string ReverseForDisplay(string text)
    {
        var elements = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);

        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        var builder = new StringBuilder(text.Length);

        for (var index = elements.Count - 1; index >= 0; index--)
        {
            var element = elements[index];
            builder.Append(element.Length == 1 ? BidiAlgorithm.Mirror(element[0]) : element);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Distance from the top of an atomic inline's margin box to the line's baseline.
    /// </summary>
    private static float ResolveAtomicAscent(AtomicInlineMeasurement measurement, RenderTextStyle blockStyle) =>
        measurement.VerticalAlign == "middle"
            ? (measurement.OuterHeight / 2f) + (blockStyle.FontSize * 0.25f)
            : measurement.Baseline;

    /// <summary>
    /// Paints the background and border of every inline box on the current line, outermost first.
    /// A box broken across lines only gets its left edge on its first fragment and its right edge
    /// on its last one.
    /// </summary>
    private static void PaintInlineBoxFragments(List<InlinePiece> linePieces, float baselineY, DisplayList displayList)
    {
        var boxes = new List<InlineBox>();

        foreach (var piece in linePieces)
        {
            for (var box = piece.Box; box is not null; box = box.Parent)
            {
                if (!box.HasFragment)
                {
                    box.HasFragment = true;
                    box.FragmentLeft = float.MaxValue;
                    box.FragmentRight = float.MinValue;
                    box.StartsOnLine = false;
                    box.EndsOnLine = false;
                    boxes.Add(box);
                }

                var left = piece.X;
                var right = piece.X + (piece.Kind == InlinePieceKind.OutOfFlow ? 0f : piece.Width);

                if (ReferenceEquals(piece.Box, box) && piece.Kind == InlinePieceKind.BoxStart)
                {
                    left += box.MarginLeft;
                    box.StartsOnLine = true;
                }

                if (ReferenceEquals(piece.Box, box) && piece.Kind == InlinePieceKind.BoxEnd)
                {
                    right -= box.MarginRight;
                    box.EndsOnLine = true;
                }

                box.FragmentLeft = Math.Min(box.FragmentLeft, Math.Min(left, right));
                box.FragmentRight = Math.Max(box.FragmentRight, Math.Max(left, right));
            }
        }

        // Outer boxes paint first, so nested decorations sit on top of their parents'.
        boxes.Sort(static (a, b) => Depth(a).CompareTo(Depth(b)));

        foreach (var box in boxes)
        {
            box.HasFragment = false;
            var style = box.TextStyle;
            var top = baselineY - (style.FontSize * FormControlTextAscentRatio) - box.Box.Padding.Top - box.Box.BorderWidth.Top;
            var bottom = baselineY + (style.FontSize * FormControlTextDescentRatio) + box.Box.Padding.Bottom + box.Box.BorderWidth.Bottom;
            var width = Math.Max(0f, box.FragmentRight - box.FragmentLeft);
            var height = bottom - top;

            box.BoundsLeft = Math.Min(box.BoundsLeft, box.FragmentLeft);
            box.BoundsRight = Math.Max(box.BoundsRight, box.FragmentLeft + width);
            box.BoundsTop = Math.Min(box.BoundsTop, top);
            box.BoundsBottom = Math.Max(box.BoundsBottom, bottom);

            if (!box.IsDecorated || width <= 0f || box.TextStyle.IsHidden)
            {
                continue;
            }

            var isWhole = box.StartsOnLine && box.EndsOnLine;
            var radii = isWhole ? box.Box.BorderRadius : RenderCornerRadii.Zero;
            var borderWidths = new EdgeSizes(
                box.Box.BorderWidth.Top,
                box.EndsOnLine ? box.Box.BorderWidth.Right : 0f,
                box.Box.BorderWidth.Bottom,
                box.StartsOnLine ? box.Box.BorderWidth.Left : 0f);

            PaintBackground(displayList, box.Box.BackgroundPaint, box.FragmentLeft, top, width, height, radii);
            PaintBorder(displayList, box.Box.BorderColor, box.FragmentLeft, top, width, height, borderWidths, radii, box.Box.BorderStyle);
        }

        static int Depth(InlineBox box)
        {
            var depth = 0;

            for (var current = box.Parent; current is not null; current = current.Parent)
            {
                depth++;
            }

            return depth;
        }
    }

    private static void LayoutOutOfFlowPieces(
        List<InlinePiece> linePieces,
        float contentX,
        float lineTop,
        float contentWidth,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        float containingHeight,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight)
    {
        foreach (var piece in linePieces)
        {
            if (piece.Kind != InlinePieceKind.OutOfFlow)
            {
                continue;
            }

            var staticCursorY = lineTop;
            var previousMarginBottom = 0f;
            var suppressTopMargin = false;
            var localFloats = new FloatArea();
            var textIndentConsumed = false;

            LayoutNode(
                node: piece.Element!,
                containingX: contentX,
                containingY: lineTop,
                containingWidth: contentWidth,
                cursorY: ref staticCursorY,
                previousBlockMarginBottom: ref previousMarginBottom,
                suppressNextBlockTopMargin: ref suppressTopMargin,
                floats: localFloats,
                textIndentConsumed: ref textIndentConsumed,
                textStyle: piece.Style,
                context: context,
                displayList: displayList,
                maxY: maxY,
                containingHeight: containingHeight,
                positionedContainingX: positionedContainingX,
                positionedContainingY: positionedContainingY,
                positionedContainingWidth: positionedContainingWidth,
                positionedContainingHeight: positionedContainingHeight);
        }
    }

    /// <summary>
    /// Measures an atomic inline by laying it out once into a scratch display list: its width is
    /// the authored one or, when <c>width</c> is auto, its shrink-to-fit width.
    /// </summary>
    private static AtomicInlineMeasurement MeasureAtomicInline(ElementRenderNode element, float containingWidth, RenderTextStyle inheritedStyle, LayoutContext context)
    {
        var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);
        var borderBoxWidth = ResolveShrinkToFitBorderBoxWidth(element, containingWidth, inheritedStyle, context, out var marginLeft, out var marginRight);
        var verticalAlign = styleMap.TryGetValue("vertical-align", out var verticalAlignValue)
            ? verticalAlignValue.Trim().ToLowerInvariant()
            : "baseline";

        var trial = TrialLayout(element, containingWidth, inheritedStyle, context, borderBoxWidth, isAtomicInline: true);

        if (trial.Metrics is not { } metrics)
        {
            return new AtomicInlineMeasurement(0f, 0f, 0f, 0f, 0f, 0f, 0f, verticalAlign);
        }

        var box = ResolveBoxStyle(styleMap, element.Ref);
        var marginBottom = ParseLength(styleMap, "margin-bottom", containingWidth, box.Margin.Bottom, allowAuto: false);
        var borderBoxOffsetY = Math.Max(0f, metrics.BorderBoxY);
        var outerHeight = borderBoxOffsetY + metrics.BorderBoxHeight + marginBottom;

        // The baseline of an inline-block is that of its last line box, unless it has none or
        // clips its overflow, in which case it is the bottom margin edge.
        var baseline = trial.LastBaselineY is { } lastBaseline && !ShouldClipOverflow(styleMap)
            ? Math.Clamp(lastBaseline, 0f, outerHeight)
            : outerHeight;

        return new AtomicInlineMeasurement(
            metrics.BorderBoxWidth,
            metrics.BorderBoxHeight,
            marginLeft,
            marginRight,
            borderBoxOffsetY,
            marginBottom,
            baseline,
            verticalAlign);
    }

    /// <summary>
    /// Lays out <paramref name="element"/> at the origin into a throwaway display list, capturing
    /// its own border box and the last text baseline painted inside it. Layout metrics recorded
    /// during the trial never reach a real <see cref="CaptureLayoutMetrics"/> capture.
    /// </summary>
    private static (ElementLayoutMetrics? Metrics, float? LastBaselineY, float? FirstBaselineY) TrialLayout(
        ElementRenderNode element,
        float containingWidth,
        RenderTextStyle inheritedStyle,
        LayoutContext context,
        float forcedBorderBoxWidth,
        bool isAtomicInline)
    {
        var capture = new LayoutCapture();
        var previous = s_layoutCapture.Value;
        s_layoutCapture.Value = capture;

        try
        {
            var cursorY = 0f;
            var previousMarginBottom = 0f;
            var suppressTopMargin = false;
            var localFloats = new FloatArea();
            var textIndentConsumed = false;

            LayoutNode(
                node: element,
                containingX: 0f,
                containingY: 0f,
                containingWidth: containingWidth,
                cursorY: ref cursorY,
                previousBlockMarginBottom: ref previousMarginBottom,
                suppressNextBlockTopMargin: ref suppressTopMargin,
                floats: localFloats,
                textIndentConsumed: ref textIndentConsumed,
                textStyle: inheritedStyle,
                context: context,
                displayList: new DisplayList { Fonts = context.Fonts },
                maxY: float.MaxValue,
                forcedBorderBoxWidth: forcedBorderBoxWidth,
                isAtomicInline: isAtomicInline);

            var snapshot = capture.Snapshot();
            return (snapshot.TryGetValue(element.Ref, out var metrics) ? metrics : null, capture.LastBaselineY, capture.FirstBaselineY);
        }
        finally
        {
            s_layoutCapture.Value = previous;
        }
    }

    /// <summary>
    /// Notes the baseline of a line of text that was just laid out, so a trial layout of an
    /// inline-block can find the baseline it aligns with.
    /// </summary>
    private static void RecordTextBaseline(float baselineY)
    {
        if (s_layoutCapture.Value is { } capture)
        {
            capture.LastBaselineY = baselineY;
            capture.FirstBaselineY ??= baselineY;
        }
    }

    /// <summary>
    /// The border-box width of a box whose <c>width</c> is auto but which must not simply fill its
    /// container (inline-blocks, floats, absolutely positioned boxes): its authored width when it
    /// has one, else <c>min(max(min-content, available), max-content)</c>.
    /// </summary>
    private static float ResolveShrinkToFitBorderBoxWidth(ElementRenderNode element, float containingWidth, RenderTextStyle inheritedStyle, LayoutContext context, out float marginLeft, out float marginRight)
    {
        var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);
        var formControlKind = ResolveFormControlKind(element.Ref);
        var textStyle = ResolveTextStyle(styleMap, inheritedStyle);

        if (formControlKind != FormControlKind.None)
        {
            ApplyFormControlDefaults(formControlKind, element.Ref, styleMap, textStyle, context);
        }

        if (IsTableDisplay(styleMap))
        {
            GetTableStructure(element, context);
        }

        var box = ResolveBoxStyle(styleMap, element.Ref);
        marginLeft = ParseLength(styleMap, "margin-left", containingWidth, 0f, allowAuto: true);
        marginRight = ParseLength(styleMap, "margin-right", containingWidth, 0f, allowAuto: true);
        marginLeft = float.IsNaN(marginLeft) ? 0f : marginLeft;
        marginRight = float.IsNaN(marginRight) ? 0f : marginRight;

        var paddingLeft = ParseLength(styleMap, "padding-left", containingWidth, box.Padding.Left, allowAuto: false);
        var paddingRight = ParseLength(styleMap, "padding-right", containingWidth, box.Padding.Right, allowAuto: false);
        var horizontalExtras = box.BorderWidth.Left + box.BorderWidth.Right + paddingLeft + paddingRight;
        var authored = ResolveAuthoredDimension(styleMap, "width", containingWidth, float.NaN, horizontalExtras);
        float contentWidth;

        if (!float.IsNaN(authored))
        {
            contentWidth = authored;
        }
        else if (TryResolveReplacedContentWidth(element, styleMap, containingWidth, out var replacedWidth))
        {
            contentWidth = replacedWidth;
        }
        else
        {
            var intrinsic = ComputeContentIntrinsicWidths(element, textStyle, containingWidth, context);
            var available = Math.Max(0f, containingWidth - marginLeft - marginRight - horizontalExtras);
            contentWidth = Math.Min(Math.Max(intrinsic.Min, available), intrinsic.Max);
        }

        contentWidth = ClampToMinMaxWidth(styleMap, contentWidth, containingWidth, horizontalExtras);
        return contentWidth + horizontalExtras;
    }

    /// <summary>
    /// The used width of a replaced element (<c>&lt;img&gt;</c>, inline <c>&lt;svg&gt;</c>), which
    /// comes from its natural size when no width is authored.
    /// </summary>
    private static bool TryResolveReplacedContentWidth(ElementRenderNode element, Dictionary<string, string> styleMap, float containingWidth, out float width)
    {
        width = 0f;

        if (!IsReplacedElement(element.Ref) ||
            !TryResolveReplacedElementImage(element, styleMap, containingWidth, 0f, 0f, out _, out var imageRect, out var objectFitClip))
        {
            return false;
        }

        width = objectFitClip?.Width ?? imageRect.Width;
        return true;
    }

    private static float ClampToMinMaxWidth(Dictionary<string, string> styleMap, float contentWidth, float containingWidth, float horizontalExtras)
    {
        var maxWidth = ResolveAuthoredDimension(styleMap, "max-width", containingWidth, float.NaN, horizontalExtras);
        var minWidth = ResolveAuthoredDimension(styleMap, "min-width", containingWidth, float.NaN, horizontalExtras);

        if (!float.IsNaN(maxWidth))
        {
            contentWidth = Math.Min(contentWidth, maxWidth);
        }

        if (!float.IsNaN(minWidth))
        {
            contentWidth = Math.Max(contentWidth, minWidth);
        }

        return Math.Max(0f, contentWidth);
    }

    /// <summary>
    /// The min-content and max-content widths of a box.
    /// </summary>
    private readonly record struct IntrinsicWidths(float Min, float Max)
    {
        public static IntrinsicWidths Zero => new(0f, 0f);
    }

    /// <summary>
    /// The min-content/max-content contribution of an element to its parent: its content's
    /// intrinsic widths (or its authored width) plus its horizontal border, padding and margins.
    /// </summary>
    private static IntrinsicWidths ComputeOuterIntrinsicWidths(ElementRenderNode element, RenderTextStyle inheritedStyle, float percentageBase, LayoutContext context)
    {
        if (!GeneratesBoxes(element))
        {
            return IntrinsicWidths.Zero;
        }

        var formControlKind = ResolveFormControlKind(element.Ref);

        var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);

        if (IsOutOfFlowPositioned(styleMap))
        {
            return IntrinsicWidths.Zero;
        }

        var textStyle = ResolveTextStyle(styleMap, inheritedStyle);

        if (formControlKind != FormControlKind.None)
        {
            ApplyFormControlDefaults(formControlKind, element.Ref, styleMap, textStyle, context);
        }

        if (IsTableDisplay(styleMap))
        {
            // Resolves a collapsed table's borders before its box is read.
            GetTableStructure(element, context);
        }

        var box = ResolveBoxStyle(styleMap, element.Ref);
        var marginLeft = ParseLength(styleMap, "margin-left", percentageBase, 0f, allowAuto: true);
        var marginRight = ParseLength(styleMap, "margin-right", percentageBase, 0f, allowAuto: true);
        var horizontalExtras = box.BorderWidth.Left + box.BorderWidth.Right +
                               ParseLength(styleMap, "padding-left", percentageBase, box.Padding.Left, allowAuto: false) +
                               ParseLength(styleMap, "padding-right", percentageBase, box.Padding.Right, allowAuto: false);
        var margins = (float.IsNaN(marginLeft) ? 0f : marginLeft) + (float.IsNaN(marginRight) ? 0f : marginRight);
        // A percentage width would refer to the very size being computed, so it counts as auto in
        // an intrinsic contribution.
        var authored = styleMap.TryGetValue("width", out var rawWidth) && rawWidth.TrimEnd().EndsWith('%')
            ? float.NaN
            : ResolveAuthoredDimension(styleMap, "width", percentageBase, float.NaN, horizontalExtras);
        IntrinsicWidths content;

        if (!float.IsNaN(authored))
        {
            content = new IntrinsicWidths(authored, authored);
        }
        else if (TryResolveReplacedContentWidth(element, styleMap, percentageBase, out var replacedWidth))
        {
            content = new IntrinsicWidths(replacedWidth, replacedWidth);
        }
        else
        {
            content = ComputeContentIntrinsicWidths(element, textStyle, percentageBase, context);
        }

        var min = ClampToMinMaxWidth(styleMap, content.Min, percentageBase, horizontalExtras);
        var max = ClampToMinMaxWidth(styleMap, content.Max, percentageBase, horizontalExtras);
        return new IntrinsicWidths(min + horizontalExtras + margins, Math.Max(min, max) + horizontalExtras + margins);
    }

    /// <summary>
    /// The intrinsic widths of an element's content: the widest unbreakable piece (min-content)
    /// and the widest line it produces without any soft wrapping (max-content).
    /// </summary>
    private static IntrinsicWidths ComputeContentIntrinsicWidths(ElementRenderNode element, RenderTextStyle textStyle, float percentageBase, LayoutContext context)
    {
        var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);
        var children = OrderChildrenForPainting(element.Children, context).ToList();
        var formControlKind = ResolveFormControlKind(element.Ref);

        if (element.Ref is not IPseudoElement &&
            (string.Equals(element.Ref.LocalName, "svg", StringComparison.OrdinalIgnoreCase) ||
             formControlKind is FormControlKind.Select or FormControlKind.Button))
        {
            children = [];
        }

        if (IsTableDisplay(styleMap))
        {
            return ComputeTableIntrinsicWidths(element, styleMap, textStyle, percentageBase, context);
        }

        // An orthogonal flow is as wide as its content's block size.
        if (IsVerticalMode(textStyle.WritingMode) != IsVerticalMode(textStyle.FlowMode) && formControlKind == FormControlKind.None &&
            !IsReplacedElement(element.Ref))
        {
            var blockSize = MeasureOrthogonalBlockSize(element, styleMap, textStyle, percentageBase, context);
            return new IntrinsicWidths(blockSize, blockSize);
        }

        if (formControlKind == FormControlKind.Button)
        {
            // A button's label is painted by PaintFormControl rather than laid out as children.
            var label = NormalizeWhitespace(element.Ref.TextContent ?? string.Empty);
            var labelWidth = label.Length > 0 ? MeasureTextWidth(context, label, textStyle) : 0f;
            return new IntrinsicWidths(labelWidth, labelWidth);
        }

        if (IsFlexContainer(styleMap) && !(GetFlexDirection(styleMap) ?? string.Empty).StartsWith("column", StringComparison.OrdinalIgnoreCase))
        {
            // A row flex container lays its items side by side.
            var min = 0f;
            var max = 0f;
            var wraps = !string.Equals(GetFlexWrap(styleMap), "nowrap", StringComparison.OrdinalIgnoreCase);
            // `column-gap` or the `gap` shorthand; percentage gaps contribute nothing here.
            var gap = ResolveColumnGap(styleMap, 0f) ?? 0f;
            var itemCount = 0;

            foreach (var child in children)
            {
                var item = child switch
                {
                    ElementRenderNode childElement => ComputeOuterIntrinsicWidths(childElement, textStyle, percentageBase, context),
                    TextRenderNode textNode when !string.IsNullOrWhiteSpace(textNode.Ref.Data) => ComputeTextIntrinsicWidths(textNode.Ref.Data, textStyle, context, out _),
                    _ => IntrinsicWidths.Zero,
                };

                if (item.Max <= 0f)
                {
                    continue;
                }

                min = wraps ? Math.Max(min, item.Min) : min + item.Min;
                max += item.Max;
                itemCount++;
            }

            var gaps = Math.Max(0, itemCount - 1) * (float.IsNaN(gap) ? 0f : gap);
            return new IntrinsicWidths(wraps ? min : min + gaps, max + gaps);
        }

        var resultMin = 0f;
        var resultMax = 0f;
        var lineMax = 0f;

        void FlushLine()
        {
            resultMax = Math.Max(resultMax, lineMax);
            lineMax = 0f;
        }

        void AccumulateInline(IRenderNode node, RenderTextStyle style)
        {
            switch (node)
            {
                case TextRenderNode textNode:
                {
                    var widths = ComputeTextIntrinsicWidths(textNode.Ref.Data, style, context, out var forcedLines);

                    for (var index = 0; index < forcedLines.Count; index++)
                    {
                        if (index > 0)
                        {
                            FlushLine();
                        }

                        lineMax += forcedLines[index];
                    }

                    resultMin = Math.Max(resultMin, widths.Min);
                    break;
                }
                case ElementRenderNode child:
                {
                    if (!GeneratesBoxes(child))
                    {
                        break;
                    }

                    if (string.Equals(child.Ref.LocalName, "br", StringComparison.OrdinalIgnoreCase))
                    {
                        FlushLine();
                        break;
                    }

                    var childStyleMap = GetOrCreateStyleMap(context, child.Ref, child.ComputedStyle);

                    if (IsOutOfFlowPositioned(childStyleMap))
                    {
                        break;
                    }

                    var isInline = IsInlineLevelElement(child, childStyleMap);

                    if (isInline && !IsAtomicInlineElement(child, childStyleMap))
                    {
                        var childStyle = ResolveTextStyle(childStyleMap, style);
                        var childBox = ResolveBoxStyle(childStyleMap, child.Ref);
                        lineMax += childBox.BorderWidth.Left + childBox.BorderWidth.Right + childBox.Padding.Left + childBox.Padding.Right;

                        foreach (var grandChild in child.Children)
                        {
                            AccumulateInline(grandChild, childStyle);
                        }

                        break;
                    }

                    var outer = ComputeOuterIntrinsicWidths(child, style, percentageBase, context);

                    if (isInline)
                    {
                        lineMax += outer.Max;
                    }
                    else
                    {
                        // Block-level (and floated) children sit on lines of their own.
                        FlushLine();
                        resultMax = Math.Max(resultMax, outer.Max);
                    }

                    resultMin = Math.Max(resultMin, outer.Min);
                    break;
                }
            }
        }

        foreach (var child in children)
        {
            AccumulateInline(child, textStyle);
        }

        FlushLine();
        return new IntrinsicWidths(resultMin, Math.Max(resultMin, resultMax));
    }

    /// <summary>
    /// The intrinsic widths of one text node, plus the max-content width of each line its
    /// preserved newlines force.
    /// </summary>
    private static IntrinsicWidths ComputeTextIntrinsicWidths(string data, RenderTextStyle style, LayoutContext context, out List<float> forcedLines)
    {
        forcedLines = [];
        var collapses = style.WhiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.Nowrap or WhiteSpaceMode.PreLine;
        var text = ExpandTabs(context, ApplyTextTransform(NormalizeWhitespace(data, style.WhiteSpace), style.TextTransform, previousEndsWord: false), style);
        var spaceWidth = MeasureTextWidth(context, " ", style) + style.WordSpacing;

        if (text.Trim().Length == 0)
        {
            // A collapsible space between two inline elements still separates them.
            var width = collapses && data.Length > 0 ? spaceWidth : MeasureTextWidth(context, text, style);
            forcedLines.Add(width);
            return new IntrinsicWidths(0f, width);
        }

        var noWrap = IsNoWrapWhiteSpace(style.WhiteSpace);
        var min = 0f;
        var max = 0f;
        var paragraphs = text.Split('\n');

        for (var paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
        {
            var paragraph = paragraphs[paragraphIndex];
            var words = paragraph.Split(' ');
            var paragraphWidth = 0f;

            for (var index = 0; index < words.Length; index++)
            {
                var wordWidth = words[index].Length > 0 ? MeasureTextWidth(context, words[index], style) : 0f;
                paragraphWidth += wordWidth + (index > 0 ? spaceWidth : 0f);

                if (style.WordBreak == WordBreakMode.BreakAll || style.OverflowWrap == OverflowWrapMode.BreakWord)
                {
                    foreach (var rune in words[index].EnumerateRunes())
                    {
                        min = Math.Max(min, MeasureTextWidth(context, rune.ToString(), style));
                    }
                }
                else if (style.Hyphens != HyphensMode.None && words[index].Contains(SoftHyphen))
                {
                    // Every syllable but the last ends at a soft hyphen and carries the hyphen.
                    var syllables = words[index].Split(SoftHyphen);

                    for (var syllable = 0; syllable < syllables.Length; syllable++)
                    {
                        var piece = syllable < syllables.Length - 1 ? syllables[syllable] + HyphenText : syllables[syllable];
                        min = Math.Max(min, MeasureTextWidth(context, piece, style));
                    }
                }
                else
                {
                    min = Math.Max(min, wordWidth);
                }
            }

            // NormalizeWhitespace trims a collapsed text node, but its leading/trailing space
            // still separates it from neighbouring inline content.
            if (collapses && paragraphIndex == 0 && char.IsWhiteSpace(data[0]))
            {
                paragraphWidth += spaceWidth;
            }

            if (collapses && paragraphIndex == paragraphs.Length - 1 && char.IsWhiteSpace(data[^1]))
            {
                paragraphWidth += spaceWidth;
            }

            forcedLines.Add(paragraphWidth);
            max = Math.Max(max, paragraphWidth);
        }

        return new IntrinsicWidths(noWrap ? max : min, max);
    }

    /// <summary>
    /// The CSS <c>text-transform</c> keywords this renderer applies.
    /// </summary>
    private enum TextTransformMode
    {
        None,
        Uppercase,
        Lowercase,
        Capitalize,
    }

    private static TextTransformMode ParseTextTransform(Dictionary<string, string> styleMap, TextTransformMode inherited)
    {
        if (!styleMap.TryGetValue("text-transform", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "uppercase" => TextTransformMode.Uppercase,
            "lowercase" => TextTransformMode.Lowercase,
            "capitalize" => TextTransformMode.Capitalize,
            _ => TextTransformMode.None,
        };
    }

    /// <summary>
    /// <c>word-spacing</c> adds extra space to every space character; <c>normal</c> is zero.
    /// </summary>
    private static float ParseWordSpacing(Dictionary<string, string> styleMap, float fontSize, float inherited)
    {
        if (!styleMap.TryGetValue("word-spacing", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        var trimmed = value.Trim();

        if (string.Equals(trimmed, "normal", StringComparison.OrdinalIgnoreCase))
        {
            return 0f;
        }

        if (trimmed.EndsWith("em", StringComparison.OrdinalIgnoreCase) && !trimmed.EndsWith("rem", StringComparison.OrdinalIgnoreCase) &&
            float.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ems))
        {
            return ems * fontSize;
        }

        var parsed = ParseLengthValue(trimmed, float.NaN, allowAuto: false);
        return float.IsNaN(parsed) ? inherited : parsed;
    }

    /// <summary>
    /// Applies <c>text-transform</c>. <c>capitalize</c> upper-cases the first letter of every
    /// word; <paramref name="previousEndsWord"/> carries word state across inline element
    /// boundaries, so <c>&lt;b&gt;foo&lt;/b&gt;bar</c> stays one word.
    /// </summary>
    private static string ApplyTextTransform(string text, TextTransformMode mode, bool previousEndsWord)
    {
        switch (mode)
        {
            case TextTransformMode.Uppercase:
                return text.ToUpperInvariant();
            case TextTransformMode.Lowercase:
                return text.ToLowerInvariant();
            case TextTransformMode.Capitalize:
            {
                var sb = new StringBuilder(text.Length);
                var atWordStart = !previousEndsWord;

                foreach (var c in text)
                {
                    if (char.IsWhiteSpace(c))
                    {
                        atWordStart = true;
                        sb.Append(c);
                        continue;
                    }

                    sb.Append(atWordStart && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);

                    if (char.IsLetterOrDigit(c))
                    {
                        atWordStart = false;
                    }
                }

                return sb.ToString();
            }
            default:
                return text;
        }
    }
}
