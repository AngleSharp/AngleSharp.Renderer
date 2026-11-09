namespace AngleSharp.Renderer;

using System.Linq;
using System.Text;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Text selection: while laying out for a harness, painted text (and the spaces between runs,
/// which paint nothing) is wrapped in <see cref="PushTextFragmentCommand"/> scopes telling where
/// it came from. Everything else follows from the display list: the transforms and clips around
/// a scope are those of its text, whatever put them there (columns, writing modes, scrolling,
/// sticky boxes), so a <see cref="TextMap"/> finds the text at a point without knowing about any
/// of it, and the highlight painted into a scope is clipped and transformed like its text.
/// </summary>
public sealed partial class HtmlRenderer
{
    // the color of browsers without a system palette to take it from
    private static readonly RenderColor s_selectionBackground = new(0xB4, 0xD5, 0xFE, 0xFF);

    /// <summary>
    /// One painted run of text: what is painted, on which line, and from where in the DOM.
    /// Offsets and caret stops are only worked out for the runs a selection gets to.
    /// </summary>
    internal abstract class TextFragment
    {
        private int[]? _offsets;
        private float? _width;
        private (int[] Boundaries, float[] Stops)? _carets;

        public required string Text { get; init; }

        public required float X { get; init; }

        public required float Top { get; init; }

        public required float Height { get; init; }

        /// <summary>Gets if the text is shaped right to left, its stops then run from the right.</summary>
        public required bool IsRightToLeft { get; init; }

        /// <summary>Gets the rules of the layout, for what only selecting needs of the cascade.</summary>
        public abstract IStyleCollection? Styles { get; }

        protected abstract float MeasureWidth(string text);

        protected abstract IReadOnlyList<int> GetClusterBoundaries(string text);

        /// <summary>The part of the text each source node has, in logical order.</summary>
        public required TextSegment[] Segments { get; init; }

        /// <summary>The width of what paints nothing (a space widened by justification or a tab).</summary>
        public float? FixedWidth { get; init; }

        public float Width => _width ??= FixedWidth ?? MeasureWidth(Text);

        private (int[] Boundaries, float[] Stops) Carets => _carets ??= ComputeCarets();

        private (int[] Boundaries, float[] Stops) ComputeCarets()
        {
            if (FixedWidth is { } fixedWidth)
            {
                var boundaries = Enumerable.Range(0, Text.Length + 1).ToArray();
                return (boundaries, boundaries.Select(index => fixedWidth * index / Math.Max(1, Text.Length)).ToArray());
            }

            var clusters = GetClusterBoundaries(Text).ToArray();
            var stops = new float[clusters.Length];
            var width = Width;

            for (var index = 0; index < clusters.Length; index++)
            {
                var prefix = clusters[index] == Text.Length ? width : MeasureWidth(Text[..clusters[index]]);
                stops[index] = IsRightToLeft ? width - prefix : prefix;
            }

            return (clusters, stops);
        }

        /// <summary>The source offset of each character; the complement for what is not from the source (e.g. an ellipsis).</summary>
        private int[] Offsets => _offsets ??= ComputeOffsets();

        private int[] ComputeOffsets()
        {
            var offsets = new int[Text.Length];
            Array.Fill(offsets, -1);

            foreach (var segment in Segments)
            {
                TextSourceMap.Align(segment.Node.Data, segment.RawStart, Text.AsSpan(segment.Start, segment.Length), offsets.AsSpan(segment.Start, segment.Length));
            }

            return offsets;
        }

        /// <summary>Gets the node and source offset of a character, if it has any.</summary>
        public (IText Node, int Offset)? GetSource(int index)
        {
            if (index < 0 || index >= Text.Length || GetSegment(index) is not { } segment)
            {
                return null;
            }

            var offset = Offsets[index];
            return offset >= 0 ? (segment.Node, offset) : null;
        }

        private TextSegment? GetSegment(int index)
        {
            foreach (var segment in Segments)
            {
                if (index >= segment.Start && index < segment.Start + segment.Length)
                {
                    return segment;
                }
            }

            return null;
        }

        /// <summary>The source position of the caret at a boundary between characters.</summary>
        public (IText Node, int Offset)? GetCaretSource(int boundary)
        {
            if (GetSource(boundary) is { } source)
            {
                return source;
            }

            for (var index = Math.Min(boundary, Text.Length) - 1; index >= 0; index--)
            {
                if (GetSource(index) is { } previous)
                {
                    return (previous.Node, previous.Offset + 1);
                }
            }

            for (var index = boundary + 1; index < Text.Length; index++)
            {
                if (GetSource(index) is { } next)
                {
                    return next;
                }
            }

            return null;
        }

        /// <summary>The boundary nearest to the position in the run, and the character below it.</summary>
        public (int Boundary, int Character) GetCaretAt(float x)
        {
            var (boundaries, stops) = Carets;
            var nearest = 0;

            for (var index = 1; index < stops.Length; index++)
            {
                if (Math.Abs(stops[index] - x) < Math.Abs(stops[nearest] - x))
                {
                    nearest = index;
                }
            }

            // the cluster the position is in, or nearest to
            var character = 0;
            var nearestCluster = float.MaxValue;

            for (var index = 0; index + 1 < stops.Length; index++)
            {
                var left = Math.Min(stops[index], stops[index + 1]);
                var right = Math.Max(stops[index], stops[index + 1]);
                var distance = x < left ? left - x : x > right ? x - right : 0f;

                if (distance < nearestCluster)
                {
                    nearestCluster = distance;
                    character = boundaries[index];
                }
            }

            return (boundaries[nearest], character);
        }

        /// <summary>The horizontal extent of the characters from one index to another, whole clusters.</summary>
        public (float Left, float Right) GetExtent(int start, int end)
        {
            var (boundaries, stops) = Carets;
            var first = 0;
            var last = boundaries.Length - 1;

            while (first + 1 < boundaries.Length && boundaries[first + 1] <= start)
            {
                first++;
            }

            while (last > 0 && boundaries[last - 1] >= end)
            {
                last--;
            }

            return (X + Math.Min(stops[first], stops[last]), X + Math.Max(stops[first], stops[last]));
        }

        /// <summary>Gets the painted character range for one source node.</summary>
        public (int Start, int End)? GetSelectedCharacters(TextSegment segment, int nodeOrder, TextSelectionRange selection)
        {
            var start = -1;
            var end = -1;

            for (var index = segment.Start; index < segment.Start + segment.Length; index++)
            {
                if (GetSource(index) is { } source && ReferenceEquals(source.Node, segment.Node) &&
                    (nodeOrder > selection.Start.Node || nodeOrder == selection.Start.Node && source.Offset >= selection.Start.Offset) &&
                    (nodeOrder < selection.End.Node || nodeOrder == selection.End.Node && source.Offset < selection.End.Offset))
                {
                    start = start < 0 ? index : start;
                    end = index + 1;
                }
            }

            return start < 0 ? null : (start, end);
        }

        /// <summary>Gets the x-coordinate of a source caret in this run.</summary>
        public float? GetCaretX(IText node, int offset)
        {
            var (boundaries, stops) = Carets;

            for (var index = 0; index < boundaries.Length; index++)
            {
                if (GetCaretSource(boundaries[index]) is { } source && ReferenceEquals(source.Node, node) && source.Offset == offset)
                {
                    return X + stops[index];
                }
            }

            return null;
        }
    }

    private sealed class StyledTextFragment(LayoutContext context, RenderTextStyle style) : TextFragment
    {
        public override IStyleCollection? Styles => context.StyleCollection;

        protected override float MeasureWidth(string text) => MeasureTextWidth(context, text, style);

        protected override IReadOnlyList<int> GetClusterBoundaries(string text) =>
            context.TextMeasurer.GetClusterBoundaries(text, ToRenderFont(style, context.Fonts));
    }

    /// <summary>A part of the text of a run that comes from one node, from the given source offset on.</summary>
    internal readonly record struct TextSegment(IText Node, int RawStart, int Start, int Length);

    // what a harness asks for only, and trial layouts are thrown away
    private static bool RecordsTextFragments(LayoutContext context) => context.RecordsTextFragments && s_trialDepth == 0;

    private static TextFragment? CreateTextFragment(LayoutContext context, string text, TextSegment[] segments, float x, float top, float height, RenderTextStyle style, float? fixedWidth = null) =>
        RecordsTextFragments(context) && !style.IsHidden && segments.Length > 0
            ? new StyledTextFragment(context, style) { Text = text, X = x, Top = top, Height = height, IsRightToLeft = style.IsRightToLeftRun, Segments = segments, FixedWidth = fixedWidth }
            : null;

    private static void DrawTextFragment(LayoutContext context, DisplayList displayList, string text, float x, float y, RenderTextStyle style, TextFragment? fragment)
    {
        if (fragment is not null)
        {
            displayList.Add(new PushTextFragmentCommand(fragment));
        }

        // what has a width of its own is a space, which paints nothing
        if (fragment?.FixedWidth is null)
        {
            DrawStyledText(context, displayList, text, x, y, style);
        }

        if (fragment is not null)
        {
            displayList.Add(new PopTextFragmentCommand());
        }
    }

    /// <summary>Records what is painted of a run of inline pieces, and the spaces it leaves out.</summary>
    private static TextFragment? CreateRunFragment(LayoutContext context, List<InlinePiece> visualOrder, int first, int last, string text, float x, float top, float height, RenderTextStyle style)
    {
        if (!RecordsTextFragments(context))
        {
            return null;
        }

        var segments = new List<TextSegment>();
        var position = 0;

        for (var index = first; index <= last; index++)
        {
            // a right-to-left run holds its pieces in reverse
            var piece = visualOrder[style.IsRightToLeftRun ? first + last - index : index];

            if (piece.Source is { } source)
            {
                if (segments.Count > 0 && ReferenceEquals(segments[^1].Node, source) && segments[^1].Start + segments[^1].Length == position)
                {
                    segments[^1] = segments[^1] with { Length = segments[^1].Length + piece.Text.Length };
                }
                else
                {
                    segments.Add(new TextSegment(source, piece.SourceStart, position, piece.Text.Length));
                }
            }

            position += piece.Text.Length;
        }

        return CreateTextFragment(context, text, [.. segments], x, top, height, style);
    }

    /// <summary>
    /// The segments of text painted from a run of text nodes (see <see cref="GetTextRunData"/>),
    /// from the given position in the text of the run on: each character belongs to the node its
    /// source offset is in.
    /// </summary>
    private static TextSegment[] SplitTextRun(IText first, string runData, int rawStart, string text)
    {
        var offsets = new int[text.Length];
        TextSourceMap.Align(runData, rawStart, text, offsets);
        var nodes = new List<(IText Node, int Start)>();
        var start = 0;

        for (INode? sibling = first; sibling is not null; sibling = sibling.NextSibling)
        {
            if (sibling is IText node)
            {
                nodes.Add((node, start));
                start += node.Data.Length;
            }
            else if (sibling is not IComment)
            {
                break;
            }
        }

        var segments = new List<TextSegment>();

        for (var index = 0; index < text.Length; index++)
        {
            // what is not from the source (e.g. an ellipsis) belongs to where it would be
            var offset = offsets[index] >= 0 ? offsets[index] : ~offsets[index];
            var nodeIndex = nodes.Count - 1;

            while (nodeIndex > 0 && nodes[nodeIndex].Start > offset)
            {
                nodeIndex--;
            }

            var (node, nodeStart) = nodes[nodeIndex];

            if (segments.Count > 0 && ReferenceEquals(segments[^1].Node, node))
            {
                segments[^1] = segments[^1] with { Length = segments[^1].Length + 1 };
            }
            else
            {
                segments.Add(new TextSegment(node, Math.Min(offset - nodeStart, node.Data.Length), index, 1));
            }
        }

        return [.. segments];
    }

    /// <summary>A space paints nothing, but can be selected.</summary>
    private static void RecordSpaceFragment(LayoutContext context, DisplayList displayList, InlinePiece piece, float lineTop, float lineHeight)
    {
        if (RecordsTextFragments(context) && piece.Source is { } source && !piece.Trimmed && piece.Width > 0f)
        {
            RecordSpaceFragment(context, displayList, source, piece.SourceStart, piece.Text, piece.X + (piece.Box?.RelativeX ?? 0f), lineTop + (piece.Box?.RelativeY ?? 0f), lineHeight, piece.Style, piece.Width);
        }
    }

    private static void RecordSpaceFragment(LayoutContext context, DisplayList displayList, IText source, int rawStart, string text, float x, float top, float height, RenderTextStyle style, float width)
    {
        if (CreateTextFragment(context, text, [new TextSegment(source, rawStart, 0, text.Length)], x, top, height, style, width) is { } fragment)
        {
            displayList.Add(new PushTextFragmentCommand(fragment));
            displayList.Add(new PopTextFragmentCommand());
        }
    }

    /// <summary>
    /// Paints the selection into a copy of the display list: the highlight goes below the
    /// glyphs of each selected run, and when <c>::selection</c> has them take a color of their own, they
    /// are painted again clipped to the highlight.
    /// </summary>
    internal static DisplayList PaintSelection(DisplayList source, TextMap map, TextSelectionRange selection)
    {
        var painted = new DisplayList { Fonts = source.Fonts };
        var commands = source.Commands;

        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            painted.Add(command);

            if (command is not PushTextFragmentCommand { Fragment: var fragment } || map.GetSelectedCharacters(fragment, selection) is not { } characters)
            {
                continue;
            }

            var end = index + 1;

            while (commands[end] is not PopTextFragmentCommand)
            {
                end++;
            }

            var (left, right) = fragment.GetExtent(characters.Start, characters.End);
            var (background, color) = map.GetSelectionColors(fragment);
            painted.FillRect(new RenderRect(left, fragment.Top, right - left, fragment.Height), background);
            var glyphs = commands.Skip(index + 1).Take(end - index - 1).ToList();

            if (color is { } textColor && glyphs.Count > 0)
            {
                const float Far = 1e7f;
                var isWhole = characters.Start == 0 && characters.End == fragment.Text.Length;

                if (!isWhole)
                {
                    PaintClipped(painted, new RenderRect(-Far, -Far, left + Far, 2 * Far), glyphs);
                    PaintClipped(painted, new RenderRect(right, -Far, Far, 2 * Far), glyphs);
                    painted.PushClip(new RenderRect(left, -Far, right - left, 2 * Far), RenderCornerRadii.Zero);
                }

                foreach (var glyph in glyphs)
                {
                    if (glyph is DrawTextCommand text)
                    {
                        painted.Add(text with { Color = textColor });
                    }
                    else if (glyph is not DrawTextShadowCommand)
                    {
                        painted.Add(glyph);
                    }
                }

                if (!isWhole)
                {
                    painted.PopClip();
                }
            }
            else
            {
                glyphs.ForEach(painted.Add);
            }

            index = end - 1;
        }

        return painted;

        static void PaintClipped(DisplayList target, RenderRect clip, List<RenderCommand> glyphs)
        {
            target.PushClip(clip, RenderCornerRadii.Zero);
            glyphs.ForEach(target.Add);
            target.PopClip();
        }
    }

    /// <summary>
    /// The list with the selection of the document painted, for the renders that do not go
    /// through frames (which keep their list as laid out, see <see cref="RenderedFrame"/>).
    /// </summary>
    private static DisplayList WithSelectionPainted(IDocument document, LayoutContext context, DisplayList displayList)
    {
        if (context.RecordsTextFragments && Dom.Selection.TryGet(document) is { Range: { IsCollapsed: false } range })
        {
            var map = new TextMap(displayList, 0, document);
            return PaintSelection(displayList, map, map.Resolve(range));
        }

        return displayList;
    }

    /// <summary>Gets if layouts of the document are to record where their text comes from.</summary>
    private static bool ShouldRecordTextFragments(IDocument document) =>
        document.Context is { } browsingContext && browsingContext.TryGetDomHarness(out var harness) &&
        harness is InteractiveHtmlRendererState state && (state.IsTextSelectionEnabled || state.IsRangeGeometryEnabled);

    /// <summary>A selection in terms of the text nodes in tree order; its end is exclusive.</summary>
    internal readonly record struct TextSelectionRange((int Node, int Offset) Start, (int Node, int Offset) End);

    /// <summary>What can be selected where in a display list.</summary>
    internal sealed class TextMap
    {
        private readonly record struct Entry(TextFragment Fragment, RenderTransform2D Transform, RenderTransform2D Inverse, bool IsAnchored, RenderRect Bounds, RenderRect? Clip);

        internal readonly record struct RangeTextRect(INode Node, int Offset, RenderRect Rect, bool IsAnchored);

        private readonly List<Entry> _entries = [];
        private readonly IDocument _document;
        private IStyleCollection? _styles;
        private readonly Dictionary<IElement, string> _userSelect = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IElement, (RenderColor, RenderColor?)> _selectionColors = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IElement, IElement?> _blocks = new(ReferenceEqualityComparer.Instance);
        private List<IText>? _texts;
        private Dictionary<IText, int>? _textOrder;

        /// <summary>Gets where the viewport starts in the list.</summary>
        public int OverscanTop { get; }

        public TextMap(DisplayList displayList, int overscanTop, IDocument document)
        {
            OverscanTop = overscanTop;
            _document = document;

            var transforms = new Stack<RenderTransform2D>();
            var clips = new Stack<RenderRect?>();
            var transform = RenderTransform2D.Identity;
            RenderRect? clip = null;
            var anchorDepth = 0;

            foreach (var command in displayList.Commands)
            {
                switch (command)
                {
                    case PushTransformCommand push:
                        transforms.Push(transform);
                        transform = RenderTransform2D.Multiply(transform, push.Transform);
                        break;
                    case PopTransformCommand:
                        transform = transforms.Pop();
                        break;
                    case PushClipCommand push:
                        clips.Push(clip);
                        clip = Intersect(clip, transform.MapBounds(push.Rect));
                        break;
                    case PushClipShapeCommand:
                        clips.Push(clip);
                        break;
                    case PopClipCommand:
                        clip = clips.Pop();
                        break;
                    case PushViewportAnchorCommand:
                        anchorDepth++;
                        break;
                    case PopViewportAnchorCommand:
                        anchorDepth--;
                        break;
                    case PushTextFragmentCommand { Fragment: var fragment }:
                        _styles ??= fragment.Styles;
                        Add(fragment, transform, clip, anchorDepth > 0);
                        break;
                }
            }
        }

        private void Add(TextFragment fragment, RenderTransform2D transform, RenderRect? clip, bool isAnchored)
        {
            var determinant = (transform.A * transform.D) - (transform.B * transform.C);

            if (MathF.Abs(determinant) < 1e-6f)
            {
                return;
            }

            // measured when first needed: most of the page is never asked for its exact extent
            var bounds = transform.MapBounds(new RenderRect(fragment.X - 1e5f, fragment.Top, 2e5f, fragment.Height));

            if (clip is { } clipped && (bounds.Y + bounds.Height <= clipped.Y || bounds.Y >= clipped.Y + clipped.Height))
            {
                return;
            }

            var inverse = new RenderTransform2D(
                transform.D / determinant,
                -transform.B / determinant,
                -transform.C / determinant,
                transform.A / determinant,
                ((transform.C * transform.F) - (transform.D * transform.E)) / determinant,
                ((transform.B * transform.E) - (transform.A * transform.F)) / determinant);
            _entries.Add(new Entry(fragment, transform, inverse, isAnchored, bounds, clip));
        }

        private static RenderRect Intersect(RenderRect? current, RenderRect rect)
        {
            if (current is not { } other)
            {
                return rect;
            }

            var left = Math.Max(other.X, rect.X);
            var top = Math.Max(other.Y, rect.Y);
            var right = Math.Min(other.X + other.Width, rect.X + rect.Width);
            var bottom = Math.Min(other.Y + other.Height, rect.Y + rect.Height);
            return new RenderRect(left, top, Math.Max(0f, right - left), Math.Max(0f, bottom - top));
        }

        /// <summary>
        /// Finds the selectable text nearest to a point, preferring what is inside of
        /// <paramref name="scope"/>, then what is inside of its ancestors: the line nearest to the
        /// point, and on it the nearest character.
        /// </summary>
        /// <param name="x">The horizontal position in the list.</param>
        /// <param name="y">The vertical position in the list.</param>
        /// <param name="scrollDelta">How far the page scrolled since the list was laid out.</param>
        /// <param name="scope">The element at the point, if any.</param>
        public TextCaret? Locate(float x, float y, float scrollDelta, IElement? scope)
        {
            for (var element = scope; element is not null; element = element.ParentElement)
            {
                if (LocateIn(x, y, scrollDelta, element) is { } caret)
                {
                    return caret;
                }
            }

            return LocateIn(x, y, scrollDelta, null);
        }

        private TextCaret? LocateIn(float x, float y, float scrollDelta, IElement? scope)
        {
            TextFragment? best = null;
            var bestX = 0f;
            var (bestDistanceY, bestDistanceX) = (float.MaxValue, float.MaxValue);

            foreach (var entry in _entries)
            {
                var fragment = entry.Fragment;

                if (fragment.Segments.All(segment => !GetTextOrder().ContainsKey(segment.Node)) ||
                    (scope is not null && !fragment.Segments.Any(segment => IsInside(segment.Node, scope))) ||
                    GetUserSelect(fragment.Segments[0].Node.ParentElement) == "none")
                {
                    continue;
                }

                var pointY = entry.IsAnchored ? y : y + scrollDelta;
                var (localX, localY) = Map(entry.Inverse, x, pointY);
                var distanceY = localY < fragment.Top ? fragment.Top - localY : localY > fragment.Top + fragment.Height ? localY - fragment.Top - fragment.Height : 0f;

                if (distanceY > bestDistanceY)
                {
                    continue;
                }

                var distanceX = localX < fragment.X ? fragment.X - localX : localX > fragment.X + fragment.Width ? localX - fragment.X - fragment.Width : 0f;

                // what paints later is on top, among what contains the point
                if (distanceY < bestDistanceY || distanceX <= bestDistanceX)
                {
                    best = fragment;
                    bestX = localX;
                    (bestDistanceY, bestDistanceX) = (distanceY, distanceX);
                }
            }

            if (best is null)
            {
                return null;
            }

            var (boundary, character) = best.GetCaretAt(bestX - best.X);

            return best.GetCaretSource(boundary) is { } caret
                ? new TextCaret(caret.Node, caret.Offset, best.GetSource(character) ?? caret)
                : null;
        }

        private static (float X, float Y) Map(RenderTransform2D transform, float x, float y) =>
            ((transform.A * x) + (transform.C * y) + transform.E, (transform.B * x) + (transform.D * y) + transform.F);

        private static bool IsInside(INode node, IElement scope)
        {
            if (scope is IPseudoElement)
            {
                return false;
            }

            for (var current = node.Parent; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, scope))
                {
                    return true;
                }
            }

            return false;
        }

        private Dictionary<IText, int> GetTextOrder()
        {
            if (_textOrder is null)
            {
                _texts = [];
                _textOrder = new Dictionary<IText, int>(ReferenceEqualityComparer.Instance);
                Visit(_document);
            }

            return _textOrder;

            void Visit(INode node)
            {
                if (node is IElement { ShadowRoot: { } shadowRoot })
                {
                    Visit(shadowRoot);
                }

                foreach (var child in node.ChildNodes)
                {
                    if (child is IText text)
                    {
                        _textOrder![text] = _texts!.Count;
                        _texts.Add(text);
                    }
                    else
                    {
                        Visit(child);
                    }
                }
            }
        }

        /// <summary>Gets the selection in terms of the text nodes.</summary>
        public TextSelectionRange Resolve(IRange range) =>
            new(ResolveBoundary(range.Head, range.Start), ResolveBoundary(range.Tail, range.End));

        /// <summary>Gets the visible text rectangles intersected by a range.</summary>
        public IReadOnlyList<RangeTextRect> GetRangeRects(IRange range)
        {
            var selection = Resolve(range);
            var rects = new List<RangeTextRect>();

            if (selection.Start == selection.End && range.Head is IText caretNode)
            {
                foreach (var entry in _entries)
                {
                    if (entry.Fragment.GetCaretX(caretNode, range.Start) is { } x)
                    {
                        var rect = entry.Transform.MapBounds(new RenderRect(x, entry.Fragment.Top, 0f, entry.Fragment.Height));

                        if (Intersects(entry.Clip, rect))
                        {
                            rects.Add(new RangeTextRect(caretNode, range.Start, rect, entry.IsAnchored));
                            return rects;
                        }
                    }
                }

                return rects;
            }

            foreach (var entry in _entries)
            {
                foreach (var segment in entry.Fragment.Segments)
                {
                    if (!GetTextOrder().TryGetValue(segment.Node, out var nodeOrder) ||
                        entry.Fragment.GetSelectedCharacters(segment, nodeOrder, selection) is not { } characters)
                    {
                        continue;
                    }

                    var (left, right) = entry.Fragment.GetExtent(characters.Start, characters.End);
                    var rect = entry.Transform.MapBounds(new RenderRect(left, entry.Fragment.Top, right - left, entry.Fragment.Height));
                    var offset = entry.Fragment.GetSource(characters.Start)?.Offset ?? segment.RawStart;

                    if (Intersects(entry.Clip, rect))
                    {
                        rects.Add(new RangeTextRect(segment.Node, offset, rect, entry.IsAnchored));
                    }
                }
            }

            return rects;
        }

        private static bool Intersects(RenderRect? clip, RenderRect rect) =>
            clip is not { } clipped ||
            rect.X + rect.Width >= clipped.X && rect.X <= clipped.X + clipped.Width &&
            rect.Y + rect.Height >= clipped.Y && rect.Y <= clipped.Y + clipped.Height;

        private (int Node, int Offset) ResolveBoundary(INode node, int offset)
        {
            var order = GetTextOrder();

            if (node is IText text && order.TryGetValue(text, out var index))
            {
                return (index, offset);
            }

            // the first text at the boundary or after it
            var child = node is IText ? null : node.ChildNodes.Length > offset ? node.ChildNodes[offset] : null;
            var texts = _texts!;
            var low = 0;
            var high = texts.Count;

            while (low < high)
            {
                var middle = (low + high) / 2;
                var isAtOrAfter = child is not null
                    ? IsAtOrAfter(child, texts[middle])
                    : IsAfterSubtree(node, texts[middle]);

                if (isAtOrAfter)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return (low, 0);

            static bool IsAtOrAfter(INode reference, INode candidate)
            {
                if (ReferenceEquals(reference, candidate))
                {
                    return true;
                }

                var position = reference.CompareDocumentPosition(candidate);
                return (position & (DocumentPositions.Following | DocumentPositions.ContainedBy)) != 0;
            }

            static bool IsAfterSubtree(INode reference, INode candidate)
            {
                var position = reference.CompareDocumentPosition(candidate);
                return (position & DocumentPositions.Following) != 0 && (position & DocumentPositions.ContainedBy) == 0;
            }
        }

        /// <summary>Gets which characters of the run are selected.</summary>
        public (int Start, int End)? GetSelectedCharacters(TextFragment fragment, TextSelectionRange selection)
        {
            var order = GetTextOrder();

            if (GetUserSelect(fragment.Segments[0].Node.ParentElement) == "none")
            {
                return null;
            }

            var start = -1;
            var end = -1;

            foreach (var segment in fragment.Segments)
            {
                if (!order.TryGetValue(segment.Node, out var node) || node < selection.Start.Node || node > selection.End.Node)
                {
                    continue;
                }

                for (var index = segment.Start; index < segment.Start + segment.Length; index++)
                {
                    if (fragment.GetSource(index) is { } source && Compare((node, source.Offset), selection.Start) >= 0 && Compare((node, source.Offset), selection.End) < 0)
                    {
                        start = start < 0 ? index : start;
                        end = index + 1;
                    }
                }
            }

            return start < 0 ? null : (start, end);
        }

        private static int Compare((int Node, int Offset) first, (int Node, int Offset) second) =>
            first.Node != second.Node ? first.Node.CompareTo(second.Node) : first.Offset.CompareTo(second.Offset);

        /// <summary>
        /// Gets the selected text as it is shown: transformed and with its white space collapsed,
        /// a line break between blocks and a tab between table cells.
        /// </summary>
        public string GetText(TextSelectionRange selection)
        {
            var order = GetTextOrder();
            var pieces = new List<(int Node, int Offset, TextFragment Fragment, string Text)>();

            // the columns of a box each paint all of its content, clipped to what is theirs
            var seen = new HashSet<TextFragment>(ReferenceEqualityComparer.Instance);

            foreach (var entry in _entries)
            {
                if (!seen.Add(entry.Fragment) || GetSelectedCharacters(entry.Fragment, selection) is not { } characters)
                {
                    continue;
                }

                var text = new StringBuilder();
                (IText Node, int Offset)? first = null;

                for (var index = characters.Start; index < characters.End; index++)
                {
                    if (entry.Fragment.GetSource(index) is { } source)
                    {
                        first ??= source;
                        text.Append(entry.Fragment.Text[index]);
                    }
                }

                if (first is { } start)
                {
                    pieces.Add((order[start.Node], start.Offset, entry.Fragment, text.ToString()));
                }
            }

            pieces.Sort(static (a, b) => Compare((a.Node, a.Offset), (b.Node, b.Offset)));
            var result = new StringBuilder();

            for (var index = 0; index < pieces.Count; index++)
            {
                if (index > 0)
                {
                    result.Append(GetSeparator(pieces[index - 1].Fragment, pieces[index].Fragment));
                }

                result.Append(pieces[index].Text);
            }

            return result.ToString();
        }

        private string GetSeparator(TextFragment previous, TextFragment next)
        {
            var previousNode = previous.Segments[^1].Node;
            var nextNode = next.Segments[0].Node;
            var previousBlock = GetBlock(previousNode.ParentElement);
            var nextBlock = GetBlock(nextNode.ParentElement);

            if (!ReferenceEquals(previousBlock, nextBlock))
            {
                if (MathF.Abs(previous.Top - next.Top) < 0.01f && IsTableCell(previousBlock) && IsTableCell(nextBlock))
                {
                    return "\t";
                }

                // as `innerText` has it, for the margins of paragraphs
                return previousBlock?.LocalName == "p" || nextBlock?.LocalName == "p" ? "\n\n" : "\n";
            }

            if (MathF.Abs(previous.Top - next.Top) < 0.01f)
            {
                return string.Empty;
            }

            return HasForcedBreakBetween(previousNode, nextNode) ? "\n" : " ";
        }

        private static bool HasForcedBreakBetween(IText previous, IText next)
        {
            if (ReferenceEquals(previous, next))
            {
                return previous.Data.Contains('\n') && IsPreservingNewlines(previous.ParentElement);
            }

            for (var node = FollowingNode(previous); node is not null && !ReferenceEquals(node, next); node = FollowingNode(node))
            {
                if (node is IHtmlBreakRowElement)
                {
                    return true;
                }
            }

            return false;
        }

        private static INode? FollowingNode(INode node)
        {
            if (node.FirstChild is { } child)
            {
                return child;
            }

            for (var current = node; current is not null; current = current.Parent)
            {
                if (current.NextSibling is { } sibling)
                {
                    return sibling;
                }
            }

            return null;
        }

        private static bool IsPreservingNewlines(IElement? element) =>
            element?.ComputeCurrentStyle()?.GetPropertyValue("white-space")?.Trim().ToLowerInvariant() is "pre" or "pre-wrap" or "pre-line" or "break-spaces";

        private bool IsTableCell(IElement? element) => element is not null && GetDisplay(element) == "table-cell";

        private string GetDisplay(IElement element) =>
            (_styles?.ComputeDeclarations(element) ?? element.ComputeCurrentStyle())?.GetPropertyValue("display")?.Trim().ToLowerInvariant() ?? string.Empty;

        /// <summary>Gets the nearest ancestor that is not inline-level.</summary>
        public IElement? GetBlock(IElement? element)
        {
            if (element is null)
            {
                return null;
            }

            if (!_blocks.TryGetValue(element, out var block))
            {
                block = GetDisplay(element) is "" or "inline" or "contents" ? GetBlock(element.ParentElement) ?? element : element;
                _blocks[element] = block;
            }

            return block;
        }

        /// <summary>Gets the first and last selectable positions in the given element.</summary>
        public (TextCaret Start, TextCaret End)? GetExtent(IElement element)
        {
            var order = GetTextOrder();
            ((int Node, int Offset) Key, IText Node, int Offset)? first = null;
            ((int Node, int Offset) Key, IText Node, int Offset)? last = null;

            foreach (var entry in _entries)
            {
                var fragment = entry.Fragment;

                if (!fragment.Segments.Any(segment => IsInside(segment.Node, element)) || GetUserSelect(fragment.Segments[0].Node.ParentElement) == "none")
                {
                    continue;
                }

                for (var index = 0; index < fragment.Text.Length; index++)
                {
                    if (fragment.GetSource(index) is { } source && order.TryGetValue(source.Node, out var node) && IsInside(source.Node, element))
                    {
                        var key = (node, source.Offset);

                        if (first is null || Compare(key, first.Value.Key) < 0)
                        {
                            first = (key, source.Node, source.Offset);
                        }

                        if (last is null || Compare(key, last.Value.Key) > 0)
                        {
                            last = (key, source.Node, source.Offset + 1);
                        }
                    }
                }
            }

            return first is { } start && last is { } end
                ? (new TextCaret(start.Node, start.Offset, (start.Node, start.Offset)), new TextCaret(end.Node, end.Offset, (end.Node, end.Offset)))
                : null;
        }

        /// <summary>
        /// Gets the used value of `user-select` of the element: `auto` is what its parent has,
        /// and what form controls show cannot be selected.
        /// </summary>
        public string GetUserSelect(IElement? element)
        {
            if (element is null)
            {
                return "auto";
            }

            if (_userSelect.TryGetValue(element, out var value))
            {
                return value;
            }

            if (element is IHtmlInputElement or IHtmlTextAreaElement or IHtmlSelectElement or IHtmlButtonElement or IHtmlOptionElement)
            {
                value = "none";
            }
            else
            {
                value = (_styles?.ComputeDeclarations(element) ?? element.ComputeCurrentStyle())?.GetPropertyValue("user-select")?.Trim().ToLowerInvariant() ?? string.Empty;

                if (value is not ("none" or "text" or "all"))
                {
                    value = GetUserSelect(element.ParentElement);
                }
            }

            _userSelect[element] = value;
            return value;
        }

        /// <summary>Gets the outermost ancestor with `user-select: all` that the element is inside of.</summary>
        public IElement? GetSelectAllRoot(IElement? element)
        {
            IElement? root = null;

            for (var current = element; current is not null && GetUserSelect(current) == "all"; current = current.ParentElement)
            {
                root = current;
            }

            return root;
        }

        /// <summary>Gets the colors of <c>::selection</c> for the text of the run, the background and the text.</summary>
        public (RenderColor Background, RenderColor? Color) GetSelectionColors(TextFragment fragment)
        {
            if (fragment.Segments[0].Node.ParentElement is not { } element)
            {
                return (s_selectionBackground, null);
            }

            if (!_selectionColors.TryGetValue(element, out var colors))
            {
                colors = (s_selectionBackground, null);

                if (_styles is { } styles && element.Pseudo("selection") is { } pseudo)
                {
                    var style = styles.ComputeExplicitStyle(pseudo);
                    var background = style.GetPropertyValue("background-color");
                    var color = style.GetPropertyValue("color");
                    colors = (
                        string.IsNullOrEmpty(background) ? s_selectionBackground : ParseColor(background, s_selectionBackground),
                        string.IsNullOrEmpty(color) ? null : ParseColor(color, RenderColor.Black));
                }

                _selectionColors[element] = colors;
            }

            return colors;
        }
    }

    /// <summary>A position in the text at a point, and the character the point is on.</summary>
    internal readonly record struct TextCaret(IText Node, int Offset, (IText Node, int Offset) Character);
}

/// <summary>
/// Follows painted text back to its source, through what layout does to the white space
/// and the letters of a text node.
/// </summary>
internal static class TextSourceMap
{
    private const int ResyncDistance = 16;
    private const char SoftHyphen = '­';

    /// <summary>
    /// Goes through the painted text along with the source: from the source position on, gives
    /// each character its source offset (or the complement of where it would be, for what is
    /// not from the source: an ellipsis, a hyphen, the rest of an expanded tab).
    /// </summary>
    /// <returns>The source position after the text.</returns>
    public static int Align(string raw, int position, ReadOnlySpan<char> text, Span<int> offsets)
    {
        for (var index = 0; index < text.Length; index++)
        {
            var offset = AlignCharacter(raw, ref position, text[index]);

            if (!offsets.IsEmpty)
            {
                offsets[index] = offset;
            }
        }

        return position;
    }

    private static int AlignCharacter(string raw, ref int position, char character)
    {
        if (char.IsWhiteSpace(character))
        {
            if (position < raw.Length && char.IsWhiteSpace(raw[position]))
            {
                return position++;
            }

            return ~position;
        }

        // what was collapsed or is not painted
        var next = position;

        while (next < raw.Length && (char.IsWhiteSpace(raw[next]) || (raw[next] == SoftHyphen && character != SoftHyphen)))
        {
            next++;
        }

        for (var candidate = next; candidate < raw.Length && candidate <= next + ResyncDistance; candidate++)
        {
            if (IsSameLetter(raw[candidate], character))
            {
                position = candidate + 1;
                return candidate;
            }
        }

        return ~position;
    }

    // text-transform changes the case only, and does not change the length with invariant casing
    private static bool IsSameLetter(char first, char second) =>
        first == second || char.ToUpperInvariant(first) == char.ToUpperInvariant(second);
}
