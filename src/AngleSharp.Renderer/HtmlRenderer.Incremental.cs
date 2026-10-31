namespace AngleSharp.Renderer;

using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.RenderTree;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Incremental layout metrics. Scripts often change a box and read its geometry right after,
/// for many boxes in a row (dev.to shows each of its 60 hidden dropdowns, measures and hides it
/// again on every scroll), and every read after a change used to be a full layout. A change
/// confined to the subtree of a box that does not take part in the layout of anything else (an
/// absolutely positioned box, or one that was and is not rendered) only lays out that subtree
/// again, with what its layout started from in the last full layout. So does a change of a
/// block of an ordinary block flow (see <see cref="IsInFlowEntry"/>), as long as it comes out the
/// same from outside: its box, its intrinsic widths where its container is sized by them, and
/// what it leaves for the boxes after it. Else the enclosing block is laid out instead.
/// </summary>
public sealed partial class HtmlRenderer
{
    private static readonly Regex s_selectorTokens = new(@"[.#](?:[\w-]|\\.)+|:[\w-]+", RegexOptions.CultureInvariant);
    private static readonly Regex s_attributeSelectors = new(@"\[\s*([\w-]+)\s*(?:([~|^$*]?=)\s*(?:""([^""]*)""|'([^']*)'|([^\s\]]*))\s*([iIsS])?\s*)?\]", RegexOptions.CultureInvariant);

    // the pseudo-classes depending on attributes (e.g. input:checked + label)
    private static readonly HashSet<string> s_attributeStatePseudoClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ":checked", ":disabled", ":enabled", ":required", ":optional", ":read-only", ":read-write", ":placeholder-shown",
        ":default", ":open", ":indeterminate", ":valid", ":invalid", ":in-range", ":out-of-range", ":target", ":link",
        ":any-link", ":lang", ":dir", ":empty",
    };

    /// <summary>
    /// Where the layout of an out-of-flow (or not rendered) element started in a full layout:
    /// the arguments of its <see cref="LayoutElement"/> call. <paramref name="DeferContainer"/>
    /// is the block that holds the box back until its height is known (see
    /// <see cref="PositionedScope"/>), if it would. Without <paramref name="HasStaticPosition"/>,
    /// the entry is made up from its parent's <see cref="ContainerEntry"/> for a box that
    /// never reached its layout (e.g. not rendered in inline content), which is only right for a
    /// box placed by its insets alone. An <paramref name="IsInFlow"/> entry is a block of an
    /// ordinary block flow (see <see cref="IsInFlowEntry"/>), which also needs the margins it
    /// collapsed with, and leaves a <paramref name="Result"/> for the boxes after it.
    /// </summary>
    private sealed record LayoutEntry(
        ElementRenderNode Node,
        float ContainingX,
        float ContainingY,
        float ContainingWidth,
        float CursorY,
        bool TextIndentConsumed,
        RenderTextStyle TextStyle,
        float MaxY,
        float ContainingHeight,
        float PositionedContainingX,
        float PositionedContainingY,
        float PositionedContainingWidth,
        float PositionedContainingHeight,
        IElement? DeferContainer,
        bool HasBox,
        bool HasStaticPosition = true,
        bool IsInFlow = false,
        float PreviousBlockMarginBottom = 0f,
        bool SuppressNextBlockTopMargin = false,
        FlowResult? Result = null,
        bool IsSizedByContent = false);

    /// <summary>
    /// What an in-flow block left for the boxes after it: where they start, the margin they
    /// collapse with, and if it left no floats beside them.
    /// </summary>
    private sealed record FlowResult(float CursorY, float PreviousBlockMarginBottom, bool SuppressNextBlockTopMargin, bool TextIndentConsumed, bool LeftNoFloats);

    /// <summary>A child of a flow painted after its in-flow content (positioned or z-indexed).</summary>
    private sealed record RaisedChild(IElement Element, int ZIndex, RenderCommand First, int Count);

    /// <summary>
    /// Where the not rendered children of a flow would paint: among its <paramref name="Raised"/>
    /// children, in the order they paint, or after <paramref name="End"/>, the flow's last command.
    /// </summary>
    private sealed record PaintLayer(IReadOnlyList<RaisedChild> Raised, RenderCommand End);

    /// <summary>
    /// What a block gave its children to start their layouts from: its content box, its text
    /// style, and the containing block of their absolutely positioned boxes.
    /// </summary>
    private sealed record ContainerEntry(
        ElementRenderNode Node,
        float ContentX,
        float ContentY,
        float ContentWidth,
        RenderTextStyle TextStyle,
        float MaxY,
        float ContentHeight,
        float PositionedContainingX,
        float PositionedContainingY,
        float PositionedContainingWidth,
        float PositionedContainingHeight,
        float PaddingBoxHeight = float.NaN,
        bool IsFlow = false);

    private static void RecordLayoutEntry(
        LayoutCapture capture,
        ElementRenderNode node,
        Dictionary<string, string> styleMap,
        float containingX,
        float containingY,
        float containingWidth,
        float cursorY,
        float previousBlockMarginBottom,
        bool suppressNextBlockTopMargin,
        FloatArea floats,
        bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        float maxY,
        bool isPlain,
        float containingHeight,
        float positionedContainingX,
        float positionedContainingY,
        float positionedContainingWidth,
        float positionedContainingHeight)
    {
        var hasBox = GeneratesBoxes(node);

        var isInFlow = hasBox && !IsOutOfFlowPositioned(styleMap);

        // only boxes not taking part in the layout of the others, or blocks of a block flow, laid out the ordinary way
        if (!isPlain || (isInFlow && !IsInFlowEntry(capture, node, styleMap, floats, context)))
        {
            capture.Entries!.Remove(node.Ref);
            return;
        }

        if (isInFlow)
        {
            capture.Entries![node.Ref] = new LayoutEntry(node, containingX, containingY, containingWidth, cursorY, textIndentConsumed, textStyle, maxY,
                containingHeight, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight, null, HasBox: true,
                IsInFlow: true, PreviousBlockMarginBottom: previousBlockMarginBottom, SuppressNextBlockTopMargin: suppressNextBlockTopMargin,
                IsSizedByContent: IsSizedByContent(capture, (ElementRenderNode)node.Parent!, context));
            return;
        }

        var deferContainer = float.IsNaN(positionedContainingHeight) &&
            context.PositionedScopes.TryPeek(out var scope) &&
            scope.Container.Children.Contains(node) ? scope.Container.Ref : null;

        capture.Entries![node.Ref] = new LayoutEntry(
            node,
            containingX,
            containingY,
            containingWidth,
            cursorY,
            textIndentConsumed,
            textStyle,
            maxY,
            containingHeight,
            positionedContainingX,
            positionedContainingY,
            positionedContainingWidth,
            positionedContainingHeight,
            deferContainer,
            hasBox);
    }

    /// <summary>
    /// If a block can be laid out again on its own from where its flow laid it out: a block of
    /// the ordinary flow of a block that is one itself (or the root element, or a flex or grid
    /// item of one, see <see cref="RecordLink"/>), with no floats beside it.
    /// </summary>
    private static bool IsInFlowEntry(LayoutCapture capture, ElementRenderNode node, Dictionary<string, string> styleMap, FloatArea floats, LayoutContext context)
    {
        if (!floats.IsEmpty || node.Parent is not ElementRenderNode parent || IsFloated(styleMap) || IsStickyPositioned(styleMap) ||
            ClassifyFlowChild(node, context) != FlowChildKind.Block)
        {
            return false;
        }

        // laid out through the parent's block flow, in this layout
        if (capture.Containers is not { } containers || !containers.TryGetValue(parent.Ref, out var container) || !container.IsFlow ||
            !ReferenceEquals(container.Node, parent))
        {
            return false;
        }

        return IsChained(capture, parent);
    }

    // a box whose blocks have in-flow entries: the root element, a block with one, or a link
    private static bool IsChained(LayoutCapture capture, ElementRenderNode node) =>
        node.Ref.ParentElement is null || capture.Links?.Contains(node.Ref) == true ||
        capture.Entries!.TryGetValue(node.Ref, out var entry) && entry.IsInFlow && ReferenceEquals(entry.Node, node);

    // If the size of a parent (or of a box around it) depends on the content of its blocks, which
    // then have to keep their intrinsic widths (and so their contributions to the size) when laid
    // out on their own: in a flex or grid item, or in a block of a width like `fit-content`.
    private static bool IsSizedByContent(LayoutCapture capture, ElementRenderNode parent, LayoutContext context) =>
        capture.Links?.Contains(parent.Ref) == true ||
        GetOrCreateStyleMap(context, parent.Ref, parent.ComputedStyle).TryGetValue("width", out var width) && width.Contains("content", StringComparison.OrdinalIgnoreCase) ||
        capture.Entries!.TryGetValue(parent.Ref, out var entry) && entry.IsSizedByContent;

    /// <summary>
    /// Lets a flex or grid item of a chained container pass the in-flow entries on to its
    /// content (see <see cref="IsInFlowEntry"/>), unless it is aligned by its baseline, which a
    /// block inside could move without changing its box.
    /// </summary>
    private static void RecordLink(ElementRenderNode container, ElementRenderNode item, Dictionary<string, string> containerStyleMap, Dictionary<string, string> itemStyleMap, LayoutContext context)
    {
        if (s_layoutCapture.Value is not { Links: { } links } capture)
        {
            return;
        }

        if (IsChained(capture, container) && !IsBaseline(containerStyleMap.GetValueOrDefault("align-items")) && !IsBaseline(itemStyleMap.GetValueOrDefault("align-self")))
        {
            links.Add(item.Ref);
        }
        else
        {
            links.Remove(item.Ref);
        }

        static bool IsBaseline(string? value) => value?.Contains("baseline", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// The layout metrics of a document, kept up to date by laying out again only the boxes that
    /// changed, as long as nothing else can have changed (see <see cref="TryUpdate"/>).
    /// </summary>
    internal sealed class LayoutSnapshot
    {
        private readonly IDocument _document;
        private readonly IRenderDevice _renderDevice;
        // the caches of the last full layout: still right for everything outside the changed
        // subtrees, whose entries are removed before they are laid out again
        private readonly LayoutContext _context;
        private readonly Dictionary<IElement, LayoutEntry> _entries;
        private readonly Dictionary<IElement, ContainerEntry> _containers;
        private readonly HashSet<IElement> _links;
        // what the layout resolved from the page (e.g. the page styles of inline SVGs), which
        // stays right outside the changed subtrees as well
        private readonly LayoutPass _pass;
        private readonly Dictionary<IElement, StickyRecord> _stickies;
        // the boxes as laid out, scrolled by _layoutScrollY (a frame's layout is scrolled), and
        // the same in page coordinates (the same instance when the layout is unscrolled)
        private readonly float _layoutScrollY;
        private readonly Dictionary<IElement, ElementLayoutMetrics> _metrics;
        private readonly Dictionary<IElement, ElementLayoutMetrics> _pageMetrics;
        // for a frame's layout: where the boxes are in its display list, and the boxes laid out
        // again since (see TryPatch)
        private readonly bool _isFrameLayout;
        private readonly Dictionary<IElement, (RenderCommand First, int Count)> _ranges;
        private readonly Dictionary<IElement, PaintLayer> _layers;
        // the boxes laid out again since: if they had a box in the frame and have one now, and
        // each layout with where the box was in the display list before and its commands after
        private readonly Dictionary<IElement, (bool Before, bool Now)> _rendered = new(ReferenceEqualityComparer.Instance);
        private readonly List<(IElement Root, (RenderCommand First, int Count)? Before, IReadOnlyList<RenderCommand> After)> _relayouts = [];
        private SiblingSelectors? _siblingSelectors;
        // the elements each compound with `:has()` matched as laid out (null if it cannot be matched)
        private readonly List<(string Compound, HashSet<IElement>? Matches)> _relationalMatches;

        private LayoutSnapshot(IDocument document, IRenderDevice renderDevice, LayoutContext context, LayoutPass pass, LayoutCapture capture, IReadOnlyDictionary<IElement, ElementLayoutMetrics> metrics, float layoutScrollY, bool isFrameLayout)
        {
            _pass = pass;
            _isFrameLayout = isFrameLayout;
            _document = document;
            _renderDevice = renderDevice;
            _context = context;
            _entries = capture.Entries!;
            _containers = capture.Containers!;
            _links = capture.Links!;
            _stickies = capture.Stickies!;
            _ranges = capture.Ranges!;
            _layers = capture.Layers!;
            _layoutScrollY = layoutScrollY;
            _metrics = new Dictionary<IElement, ElementLayoutMetrics>(metrics, ReferenceEqualityComparer.Instance);
            _pageMetrics = layoutScrollY == 0f ? _metrics : new Dictionary<IElement, ElementLayoutMetrics>(_metrics.Count, ReferenceEqualityComparer.Instance);
            _relationalMatches = GetSiblingSelectors().RelationalCompounds
                .Select(compound => (compound, TryQuerySelectorAll(document, compound)))
                .ToList();

            if (!ReferenceEquals(_pageMetrics, _metrics))
            {
                foreach (var (element, elementMetrics) in _metrics)
                {
                    _pageMetrics[element] = ToPage(elementMetrics);
                }
            }
        }

        /// <summary>Gets the boxes in page coordinates, i.e., unscrolled (except in fixed boxes).</summary>
        public IReadOnlyDictionary<IElement, ElementLayoutMetrics> Metrics => _pageMetrics;

        /// <summary>
        /// Gets if this is a frame's layout that still looks the same: all boxes laid out again
        /// since were not rendered in the frame and are not now.
        /// </summary>
        public bool IsUnchangedSinceFrame => _isFrameLayout && _rendered.Values.All(rendered => !rendered.Before && !rendered.Now);

        /// <summary>Gets if this is a frame's layout (see <see cref="TryPatch"/>).</summary>
        public bool IsFrameLayout => _isFrameLayout;

        /// <summary>
        /// Takes the boxes laid out again since the frame into its display list, layout by
        /// layout: their commands replace those they had, or are inserted where a box shown since
        /// paints (see <see cref="PaintLayer"/>). A layout of a box inside one laid out later is
        /// part of that one already.
        /// </summary>
        /// <param name="displayList">A copy of the frame's display list (or of the last patched one).</param>
        /// <returns>True if the display list shows the document now, false if a new frame is needed.</returns>
        public bool TryPatch(DisplayList displayList)
        {
            if (!_isFrameLayout)
            {
                return false;
            }

            for (var index = 0; index < _relayouts.Count; index++)
            {
                var (root, before, after) = _relayouts[index];

                if (_relayouts.Skip(index + 1).Any(later => later.Root != root && later.Root.Contains(root)))
                {
                    continue;
                }

                if (before is { } range)
                {
                    if (IndexOf(displayList, range.First) is not (>= 0 and var start) || start + range.Count > displayList.Commands.Count)
                    {
                        return false;
                    }

                    displayList.ReplaceRange(start, range.Count, after);
                }
                else if (after.Count > 0)
                {
                    if (FindPaintPosition(displayList, root) is not (>= 0 and var position))
                    {
                        return false;
                    }

                    displayList.InsertRange(position, after);
                }
            }

            // the patched display list is the frame from now on
            _rendered.Clear();
            _relayouts.Clear();
            return true;
        }

        // Where a box shown since the frame paints: among the children of its flow painted after
        // the in-flow content (by z-index, then document order), as they are now, else after the flow.
        private int FindPaintPosition(DisplayList displayList, IElement root)
        {
            var zIndex = _context.StyleMapCache.TryGetValue(root, out var styleMap) ? ParseZIndex(styleMap) : 0;

            if (zIndex < 0 || !_layers.TryGetValue(root, out var layer))
            {
                return -1;
            }

            var end = -1;

            foreach (var raised in layer.Raised)
            {
                // laid out again since (or hidden) or as in the frame
                var (first, count) = _ranges.TryGetValue(raised.Element, out var range) ? range
                    : _entries.ContainsKey(raised.Element) ? (null, 0)
                    : (raised.First, raised.Count);

                if (first is null)
                {
                    continue;
                }

                if (IndexOf(displayList, first) is not (>= 0 and var start))
                {
                    return -1;
                }

                if (raised.ZIndex > zIndex || (raised.ZIndex == zIndex && root.CompareDocumentPosition(raised.Element).HasFlag(DocumentPositions.Following)))
                {
                    return start;
                }

                end = start + count;
            }

            return end >= 0 ? end : IndexOf(displayList, layer.End) is >= 0 and var last ? last + 1 : -1;
        }

        private static int IndexOf(DisplayList list, RenderCommand command)
        {
            var commands = list.Commands;

            for (var index = 0; index < commands.Count; index++)
            {
                if (ReferenceEquals(commands[index], command))
                {
                    return index;
                }
            }

            return -1;
        }

        /// <summary>Gets the boxes as laid out (scrolled like the layout).</summary>
        public IReadOnlyDictionary<IElement, ElementLayoutMetrics> LayoutMetrics => _metrics;

        /// <summary>Gets the viewport size the layout was made for.</summary>
        public (int Width, int Height) Viewport => (_context.Width, _context.Height);

        /// <summary>Gets the style maps of the laid out elements.</summary>
        public IReadOnlyDictionary<IElement, Dictionary<string, string>> StyleMaps => _context.StyleMapCache;

        public static LayoutSnapshot Capture(IDocument document, IRenderDevice renderDevice) =>
            LayOut(document, renderDevice, s_defaultTextMeasurer, 0f, 0, isFrame: false, out _);

        /// <summary>
        /// Lays out the whole document scrolled by <paramref name="scrollOffsetY"/> (unculled, as
        /// geometry reads and hit testing need all of it), on a surface that many rows taller
        /// than the viewport.
        /// </summary>
        public static LayoutSnapshot LayOut(IDocument document, IRenderDevice renderDevice, ITextMeasurer textMeasurer, float scrollOffsetY, int surfaceExtraHeight, bool isFrame, out DisplayList displayList)
        {
            var context = CreateLayoutContext(document, renderDevice, textMeasurer);
            var viewport = new RenderViewport(context.Width, context.Height);
            var capture = new LayoutCapture
            {
                Entries = new Dictionary<IElement, LayoutEntry>(ReferenceEqualityComparer.Instance),
                Containers = new Dictionary<IElement, ContainerEntry>(ReferenceEqualityComparer.Instance),
                Links = new HashSet<IElement>(ReferenceEqualityComparer.Instance),
                Stickies = new Dictionary<IElement, StickyRecord>(ReferenceEqualityComparer.Instance),
                Ranges = new Dictionary<IElement, (RenderCommand First, int Count)>(ReferenceEqualityComparer.Instance),
                Layers = new Dictionary<IElement, PaintLayer>(ReferenceEqualityComparer.Instance),
            };
            var pass = new LayoutPass();
            var previous = s_layoutCapture.Value;
            s_layoutCapture.Value = capture;

            try
            {
                displayList = BuildDisplayList(document, viewport, context, renderDevice, scrollOffsetY, measureFullExtent: true, surfaceExtraHeight: surfaceExtraHeight, pass: pass);
                return new LayoutSnapshot(document, renderDevice, context, pass, capture, AddHitTestLayers(capture.Snapshot(), context), scrollOffsetY, isFrame);
            }
            finally
            {
                s_layoutCapture.Value = previous;
            }
        }

        private ElementLayoutMetrics ToPage(ElementLayoutMetrics metrics) =>
            metrics.IsViewportAnchored ? metrics : metrics with { BorderBoxY = metrics.BorderBoxY + _layoutScrollY };

        /// <summary>
        /// Gets the box of the element (in page coordinates, see <see cref="Metrics"/>) as seen in
        /// the viewport when the page is scrolled by the given offsets: the page moves, boxes in
        /// fixed boxes stay, and sticky boxes (with their content) stick anew.
        /// </summary>
        public ElementLayoutMetrics ToViewport(ElementLayoutMetrics metrics, double scrollX, double scrollY)
        {
            if (metrics.IsViewportAnchored)
            {
                return metrics;
            }

            var offsetX = (float)-scrollX;
            var offsetY = (float)-scrollY;
            // the sticky boxes are as laid out, scrolled by _layoutScrollY
            var viewport = GetStickyViewport(_context);
            viewport = new RenderRect(viewport.X + (float)scrollX, viewport.Y + (float)scrollY - _layoutScrollY, viewport.Width, viewport.Height);

            for (var sticky = metrics.Sticky; sticky is not null && _stickies.TryGetValue(sticky, out var record); sticky = record.Box.EnclosingSticky)
            {
                var (stickX, stickY) = ComputeStickyOffset(record.Box, record.ContainingBlock, viewport);
                offsetX += stickX - record.OffsetX;
                offsetY += stickY - record.OffsetY;
            }

            return metrics with { BorderBoxX = metrics.BorderBoxX + offsetX, BorderBoxY = metrics.BorderBoxY + offsetY };
        }

        /// <summary>
        /// Gets the boxes of all elements as seen in the viewport (see <see cref="ToViewport(ElementLayoutMetrics, double, double)"/>).
        /// </summary>
        public IReadOnlyDictionary<IElement, ElementLayoutMetrics> ToViewport(double scrollX, double scrollY)
        {
            if (scrollX == 0d && scrollY == 0d && _layoutScrollY == 0f && _stickies.Count == 0)
            {
                return _pageMetrics;
            }

            var result = new Dictionary<IElement, ElementLayoutMetrics>(_pageMetrics.Count, ReferenceEqualityComparer.Instance);

            foreach (var (element, metrics) in _pageMetrics)
            {
                result[element] = ToViewport(metrics, scrollX, scrollY);
            }

            return result;
        }

        /// <summary>
        /// Takes the changes of the document into the metrics. Changes are confined to the
        /// subtree of the element they are made to (its inline style, a class or attribute no
        /// selector with a sibling combinator or <c>:has()</c> refers to, or content changing no
        /// match of a <c>:has()</c> around it), and they only change the metrics of that subtree
        /// when its nearest box out of the flow (or not rendered) stays out of it, or its nearest
        /// in-flow block (or one of the next few around it) stays the same from outside. Anything
        /// else needs a full layout.
        /// </summary>
        /// <param name="records">The changes since the metrics were taken.</param>
        /// <returns>True if the metrics are up to date, false if a full layout is needed.</returns>
        public bool TryUpdate(IReadOnlyList<IMutationRecord> records)
        {
            // how many enclosing roots a change may be laid out with, before a full layout is cheaper
            const int MaxEscalations = 3;

            if (_document.DefaultView is not { } window)
            {
                return false;
            }

            var roots = new List<IElement>();

            foreach (var record in records)
            {
                if (GetChangedSubtree(record) is not { } changed || FindLayoutRoot(changed) is not { } root)
                {
                    return false;
                }

                if (!roots.Any(other => other == root || other.Contains(root)))
                {
                    roots.RemoveAll(root.Contains);
                    roots.Add(root);
                }
            }

            var previousPass = s_layoutPass.Value;
            s_layoutPass.Value = _pass;

            try
            {
                var laidOut = new List<IElement>();

                foreach (var root in roots)
                {
                    // laid out with an enclosing root already
                    if (laidOut.Any(other => other.Contains(root)))
                    {
                        continue;
                    }

                    var current = root;

                    for (var attempt = 0; !TryLayOutAgain(window, current, out var escalates); attempt++)
                    {
                        if (!escalates || attempt == MaxEscalations || current.ParentElement is not { } parentElement ||
                            FindLayoutRoot(parentElement) is not { } enclosing)
                        {
                            return false;
                        }

                        current = enclosing;
                    }

                    laidOut.Add(current);
                    UpdateRelationalMatches(current);
                }
            }
            finally
            {
                s_layoutPass.Value = previousPass;
            }

            return true;
        }

        private IElement? GetChangedSubtree(IMutationRecord record)
        {
            // style sheets come and go with style and link elements, which styles everything anew
            if (ChangesStyleSheets(record))
            {
                return null;
            }

            switch (record.Type)
            {
                case "attributes" when record.Target is IElement element && record.AttributeName is { } name:
                    return IsConfinedAttributeChange(element, name, record.PreviousValue) ? element : null;
                case "childList" when record.Target is IElement parent:
                    return KeepsRelationalMatches(parent) ? parent : null;
                case "characterData" when record.Target.Parent is IElement parent:
                    return KeepsRelationalMatches(parent) ? parent : null;
                default:
                    return null;
            }
        }

        private static bool ChangesStyleSheets(IMutationRecord record)
        {
            return IsSheetOwner(record.Target as IElement) || IsSheetOwner(record.Target.Parent as IElement) ||
                ContainsSheetOwner(record.Added) || ContainsSheetOwner(record.Removed);

            static bool IsSheetOwner(IElement? element) => element?.LocalName is "style" or "link";

            static bool ContainsSheetOwner(INodeList? nodes) =>
                nodes is not null && nodes.OfType<IElement>().Any(element => IsSheetOwner(element) || element.QuerySelector("style, link") is not null);
        }

        private bool IsConfinedAttributeChange(IElement element, string name, string? previousValue)
        {
            var selectors = GetSiblingSelectors();

            var value = element.GetAttribute(name);

            // e.g. :not([class*="ltag"] *) is only of interest when it matches one of the values only
            if (!selectors.MatchesAlike(name, previousValue, value))
            {
                return false;
            }

            switch (name)
            {
                case "style":
                    return true;
                case "class":
                    var before = (previousValue ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var after = (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    return !before.Except(after).Concat(after.Except(before)).Any(token => selectors.Refers("." + token));
                case "id":
                    return !selectors.Refers("#" + previousValue) && !selectors.Refers("#" + value);
                default:
                    return !selectors.RefersToAttributeState;
            }
        }

        private SiblingSelectors GetSiblingSelectors() => _siblingSelectors ??= SiblingSelectors.Create(_document);

        // If the content of the element changed without changing which elements a `:has()`
        // matches outside of it: the element and its ancestors, whose `:has()` looks into it.
        private bool KeepsRelationalMatches(IElement element)
        {
            foreach (var (compound, matches) in _relationalMatches)
            {
                if (matches is null)
                {
                    return false;
                }

                for (var current = element; current is not null; current = current.ParentElement)
                {
                    if (TryMatches(current, compound) is not { } isMatch || isMatch != matches.Contains(current))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // the content of a root laid out again may match differently now
        private void UpdateRelationalMatches(IElement root)
        {
            foreach (var (compound, matches) in _relationalMatches)
            {
                foreach (var element in root.DescendantsAndSelf<IElement>())
                {
                    if (TryMatches(element, compound) == true)
                    {
                        matches!.Add(element);
                    }
                    else
                    {
                        matches!.Remove(element);
                    }
                }
            }
        }

        private static HashSet<IElement>? TryQuerySelectorAll(IDocument document, string selector)
        {
            try
            {
                return new HashSet<IElement>(document.QuerySelectorAll(selector), ReferenceEqualityComparer.Instance);
            }
            catch (DomException)
            {
                return null;
            }
        }

        private static bool? TryMatches(IElement element, string selector)
        {
            try
            {
                return element.Matches(selector);
            }
            catch (DomException)
            {
                return null;
            }
        }

        // the nearest element whose layout started in the last full layout (out of the flow, not
        // rendered, or a block of a block flow) below the body, or a not rendered child of a block
        private IElement? FindLayoutRoot(IElement element)
        {
            // laying out the body again is no cheaper than a full layout
            for (var current = element; current is not null && current != _document.Body; current = current.ParentElement)
            {
                if (_entries.ContainsKey(current))
                {
                    return current;
                }

                if (current.ParentElement is { } parent && _containers.TryGetValue(parent, out var container) &&
                    container.Node.Children.OfType<ElementRenderNode>().FirstOrDefault(child => child.Ref == current) is { } node &&
                    !GeneratesBoxes(node))
                {
                    // as laid out from the inline content of the block, as most of them are
                    _entries[current] = new LayoutEntry(node, container.ContentX, container.ContentY, container.ContentWidth, container.ContentY, false,
                        container.TextStyle, container.MaxY, container.ContentHeight, container.PositionedContainingX, container.PositionedContainingY,
                        container.PositionedContainingWidth, container.PositionedContainingHeight,
                        float.IsNaN(container.PositionedContainingHeight) ? parent : null, HasBox: false, HasStaticPosition: false);
                    return current;
                }
            }

            return null;
        }

        // Lays out a root again on its own. An in-flow block has to come out as it was (its box,
        // and what it leaves for the boxes after it), else (or when a box enters the flow) the
        // enclosing root is to be laid out instead (`escalates`).
        private bool TryLayOutAgain(IWindow window, IElement root, out bool escalates)
        {
            var entry = _entries[root];
            escalates = false;

            // a subtree of its own shadow tree, or in a vertical writing mode, is not taken apart
            if (entry.Node.Parent is not { } parent || root.GetRoot() is not IDocument ||
                entry.TextStyle.WritingMode != WritingModeKind.HorizontalTb || entry.TextStyle.FlowMode != WritingModeKind.HorizontalTb)
            {
                return false;
            }

            // an in-flow block with nothing reaching out of its subtree (or into it from above)
            var result = entry.Result;
            ElementLayoutMetrics laidOutBefore = default;

            if (entry.IsInFlow && (result is not { LeftNoFloats: true } || !_metrics.TryGetValue(root, out laidOutBefore) ||
                GetSiblingSelectors().HasFirstFormatted || HasStickyAncestor(root) || ReachesOutOfItsSubtree(entry.Node)))
            {
                escalates = true;
                return false;
            }

            // the margin the parent took as its own (see TryGetFirstCollapsibleChildTopMargin)
            var absorbedTopMargin = entry.IsInFlow && entry.SuppressNextBlockTopMargin ? GetCollapsedTopMargin(entry.Node, entry.ContainingWidth) : 0f;
            var intrinsicWidths = entry.IsSizedByContent ? ComputeOuterIntrinsicWidths(entry.Node, entry.TextStyle, entry.ContainingWidth, _context) : default;

            // what the display list shows of it, before it is forgotten
            var hadBox = _rendered.TryGetValue(root, out var rendered) ? rendered.Before : entry.HasBox;
            (RenderCommand First, int Count)? before = _ranges.TryGetValue(root, out var range) ? range : null;

            Forget(entry.Node);
            PrepareElementsForRendering(root.DescendantsAndSelf<IElement>());

            var node = window.Render(root, parent, _renderDevice);
            BuildAnonymousTableBoxes(node);
            CollectSpecifiedStyles(node, _context);
            ResolveWritingModeSystems(node, WritingModeKind.HorizontalTb, _context);
            ProcessCounters(node, _context);

            var styleMap = GetOrCreateStyleMap(_context, root, node.ComputedStyle);

            if (entry.IsInFlow)
            {
                // the block has to stay one of the flow
                if (!GeneratesBoxes(node) || IsOutOfFlowPositioned(styleMap) || IsFloated(styleMap) || IsStickyPositioned(styleMap) ||
                    ClassifyFlowChild(node, _context) != FlowChildKind.Block || ReachesOutOfItsSubtree(node))
                {
                    escalates = true;
                    return false;
                }
            }
            else if (GeneratesBoxes(node) && !IsOutOfFlowPositioned(styleMap))
            {
                // a box entering the flow moves the boxes after it
                escalates = true;
                return false;
            }
            else if (GeneratesBoxes(node) && !entry.HasStaticPosition && !IsPlacedByInsets(styleMap))
            {
                // without a static position to start from, the box has to be placed by its insets
                return false;
            }

            var positionedContainingHeight = entry.PositionedContainingHeight;
            var textIndentConsumed = entry.TextIndentConsumed;

            // a box waiting for the height of its parent is laid out once that is known (see LayoutElement)
            if (entry.DeferContainer is { } container && DependsOnContainingBlockHeight(styleMap))
            {
                if (!_containers.TryGetValue(container, out var containerEntry) || float.IsNaN(containerEntry.PaddingBoxHeight))
                {
                    return false;
                }

                positionedContainingHeight = containerEntry.PaddingBoxHeight;
                textIndentConsumed = true;
            }

            var capture = new LayoutCapture { Entries = _entries, Containers = _containers, Links = _links, Stickies = _stickies, Ranges = _ranges, Layers = _layers };
            var displayList = new DisplayList { Fonts = _context.Fonts };
            var previous = s_layoutCapture.Value;
            s_layoutCapture.Value = capture;

            var cursorY = entry.CursorY;
            var previousBlockMarginBottom = entry.PreviousBlockMarginBottom;
            var suppressNextBlockTopMargin = entry.SuppressNextBlockTopMargin;
            var floats = new FloatArea();

            try
            {
                LayoutNode(
                    node,
                    entry.ContainingX,
                    entry.ContainingY,
                    entry.ContainingWidth,
                    ref cursorY,
                    ref previousBlockMarginBottom,
                    ref suppressNextBlockTopMargin,
                    floats,
                    ref textIndentConsumed,
                    entry.TextStyle,
                    _context,
                    displayList,
                    entry.MaxY,
                    containingHeight: entry.ContainingHeight,
                    positionedContainingX: entry.PositionedContainingX,
                    positionedContainingY: entry.PositionedContainingY,
                    positionedContainingWidth: entry.PositionedContainingWidth,
                    positionedContainingHeight: positionedContainingHeight);

                // the scroll containers around it move its metrics as they did in the full layout
                for (var ancestor = root.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
                {
                    if (_context.StyleMapCache.TryGetValue(ancestor, out var ancestorStyleMap) && ShouldClipOverflow(ancestorStyleMap) &&
                        GetContentScrollOffset(ancestor) is var scroll && scroll != default)
                    {
                        MoveScrolledContent(capture, 0, ancestor, scroll);
                    }
                }
            }
            finally
            {
                s_layoutCapture.Value = previous;
            }

            // the boxes after an in-flow block are where they were if it is the same from outside
            if (entry.IsInFlow && (cursorY != result!.CursorY || previousBlockMarginBottom != result.PreviousBlockMarginBottom ||
                suppressNextBlockTopMargin != result.SuppressNextBlockTopMargin || textIndentConsumed != result.TextIndentConsumed || !floats.IsEmpty ||
                !capture.TryGet(root, out var laidOut) || !HasSameBox(laidOut, laidOutBefore) ||
                (entry.IsSizedByContent && ComputeOuterIntrinsicWidths(node, entry.TextStyle, entry.ContainingWidth, _context) != intrinsicWidths) ||
                (entry.SuppressNextBlockTopMargin && GetCollapsedTopMargin(node, entry.ContainingWidth) != absorbedTopMargin)))
            {
                escalates = true;
                return false;
            }

            foreach (var (element, metrics) in AddHitTestLayers(capture.Snapshot(), _context))
            {
                _metrics[element] = metrics;
                _pageMetrics[element] = ToPage(metrics);
            }

            _rendered[root] = (hadBox, GeneratesBoxes(node));
            _relayouts.Add((root, before, displayList.Commands.ToList()));

            return true;
        }

        private static bool HasSameBox(ElementLayoutMetrics first, ElementLayoutMetrics second) =>
            first with { Layer = default, Sticky = null } == second with { Layer = default, Sticky = null };

        // the top margin of a block with those collapsing through it from its first children (see LayoutElement)
        private float GetCollapsedTopMargin(ElementRenderNode node, float containingWidth)
        {
            var styleMap = GetOrCreateStyleMap(_context, node.Ref, node.ComputedStyle);
            var box = ResolveBoxStyle(styleMap, node.Ref);
            var marginTop = ParseLength(styleMap, "margin-top", containingWidth, box.Margin.Top, allowAuto: false);
            var paddingTop = ParseLength(styleMap, "padding-top", containingWidth, box.Padding.Top, allowAuto: false);

            return box.BorderWidth.Top <= 0f && paddingTop <= 0f && !EstablishesBlockFormattingContext(styleMap, false, false, false, false) &&
                TryGetFirstCollapsibleChildTopMargin(node, containingWidth, _context, out var firstChildTopMargin)
                ? CollapseMargins(marginTop, firstChildTopMargin)
                : marginTop;
        }

        // sticky boxes move what they contain (and its metrics) after its layout
        private bool HasStickyAncestor(IElement element)
        {
            for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                if (_context.StyleMapCache.TryGetValue(ancestor, out var styleMap) && IsStickyPositioned(styleMap))
                {
                    return true;
                }
            }

            return false;
        }

        // What a subtree lays out beyond itself: sticky boxes (stuck by an ancestor), content with
        // a negative z-index (painted below the in-flow content of its stacking context), and
        // counters (counted through the whole document).
        private bool ReachesOutOfItsSubtree(IRenderNode node)
        {
            if (node is ElementRenderNode element)
            {
                var styleMap = GetOrCreateStyleMap(_context, element.Ref, element.ComputedStyle);

                if (IsStickyPositioned(styleMap) || (HasExplicitZIndex(styleMap) && ParseZIndex(styleMap) < 0) ||
                    IsCounting("counter-increment") || IsCounting("counter-reset") || IsCounting("counter-set") ||
                    (element.Ref is IPseudoElement && element.ComputedStyle.GetPropertyValue("content")?.Contains("counter", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return true;
                }

                bool IsCounting(string property) => styleMap.TryGetValue(property, out var value) && !string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);
            }

            return node.Children.Any(ReachesOutOfItsSubtree);
        }

        private static bool IsPlacedByInsets(Dictionary<string, string> styleMap)
        {
            return string.Equals(GetPosition(styleMap), "absolute", StringComparison.OrdinalIgnoreCase) &&
                (IsSet("top") || IsSet("bottom")) && (IsSet("left") || IsSet("right"));

            bool IsSet(string inset) => styleMap.TryGetValue(inset, out var value) && !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
        }

        // Drops what is known about the elements of the old subtree (pseudo-elements included,
        // which belong to no DOM subtree); its layout starts from the entry of its root again.
        private void Forget(ElementRenderNode root)
        {
            var elements = new HashSet<IElement>(ReferenceEqualityComparer.Instance);
            Collect(root, elements);

            foreach (var element in elements)
            {
                _metrics.Remove(element);
                _pageMetrics.Remove(element);
                _context.StyleMapCache.Remove(element);
                _context.SpecifiedStyles.Remove(element);
                _context.TableStructures.Remove(element);
                _context.BoxSystems.Remove(element);
                _context.ContentSystems.Remove(element);
                _containers.Remove(element);
                _stickies.Remove(element);
                _ranges.Remove(element);
                _pass.Styles.Remove(element);
                _pass.ComputedStyles.Remove(element);
                _pass.PercentageHeights.Remove(element);

                // where the root starts and paints is known from outside the subtree
                if (element != root.Ref)
                {
                    _entries.Remove(element);
                    _layers.Remove(element);
                }
            }

            // whether a subtree has percentage heights is known for the ancestors as well
            for (var ancestor = root.Ref.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                _pass.PercentageHeights.Remove(ancestor);
            }

            RemoveKeys(_context.MeasuredHeights, key => elements.Contains(key.Element));
            RemoveKeys(_context.GridItemHeights, key => elements.Contains(key.Element));

            static void Collect(IRenderNode node, HashSet<IElement> elements)
            {
                if (node is ElementRenderNode element)
                {
                    elements.Add(element.Ref);
                }

                foreach (var child in node.Children)
                {
                    Collect(child, elements);
                }
            }

            static void RemoveKeys<TKey, TValue>(Dictionary<TKey, TValue> dictionary, Func<TKey, bool> predicate)
                where TKey : notnull
            {
                foreach (var key in dictionary.Keys.Where(predicate).ToList())
                {
                    dictionary.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// What the selectors relating an element to others than its descendants (with a sibling
    /// combinator or <c>:has()</c>) refer to: changing anything else of an element only changes
    /// the style of its own subtree.
    /// </summary>
    private sealed class SiblingSelectors
    {
        private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AttributeSelector>> _attributes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets if a selector with <c>:has()</c> exists, which may depend on anything.</summary>
        public bool HasRelational { get; private set; }

        /// <summary>Gets the compound selectors with <c>:has()</c> (without pseudo-elements), whose matches a change may flip.</summary>
        public List<string> RelationalCompounds { get; } = [];

        /// <summary>Gets if a selector with <c>::first-line</c> or <c>::first-letter</c> exists, which styles the first line of a descendant.</summary>
        public bool HasFirstFormatted { get; private set; }

        /// <summary>Gets if such a selector has a pseudo-class depending on an attribute (e.g. <c>:checked</c>).</summary>
        public bool RefersToAttributeState { get; private set; }

        public bool Refers(string token) => _tokens.Contains(token);

        /// <summary>
        /// Gets if the attribute selectors on the attribute match both values or neither, with
        /// and without case (HTML compares the values of some attributes, e.g. type, without).
        /// </summary>
        public bool MatchesAlike(string name, string? before, string? after) =>
            !_attributes.TryGetValue(name, out var selectors) || selectors.All(selector =>
                selector.Matches(before, selector.IgnoresCase) == selector.Matches(after, selector.IgnoresCase) &&
                selector.Matches(before, ignoreCase: true) == selector.Matches(after, ignoreCase: true));

        // the compounds of a selector list, split at the combinators outside of any parentheses,
        // brackets or strings; a pseudo-element is left out (it is matched by its originating element)
        private static IEnumerable<string> SplitCompounds(string selector)
        {
            var depth = 0;
            var quote = '\0';
            var start = 0;

            for (var index = 0; index <= selector.Length; index++)
            {
                var c = index < selector.Length ? selector[index] : ',';

                if (quote != '\0')
                {
                    quote = c == quote ? '\0' : quote;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    depth--;
                }
                else if (depth == 0 && (c is ',' or '>' or '+' or '~' || char.IsWhiteSpace(c)))
                {
                    if (Compound(selector[start..index]) is { Length: > 0 } compound)
                    {
                        yield return compound;
                    }

                    start = index + 1;
                }
            }

            static string Compound(string text)
            {
                var pseudoElement = text.IndexOf("::", StringComparison.Ordinal);
                var compound = (pseudoElement >= 0 ? text[..pseudoElement] : text).Trim();
                return compound.Length == 0 && pseudoElement >= 0 ? "*" : compound;
            }
        }

        public static SiblingSelectors Create(IDocument document)
        {
            var selectors = new SiblingSelectors();

            foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
            {
                selectors.Add(sheet.Rules);
            }

            return selectors;
        }

        private void Add(ICssRuleList rules)
        {
            foreach (var rule in rules)
            {
                switch (rule)
                {
                    case ICssStyleRule style:
                        Add(style.SelectorText);
                        Add(style.Rules);
                        break;
                    case ICssGroupingRule group:
                        Add(group.Rules);
                        break;
                    case ICssImportRule { Sheet: { } sheet }:
                        Add(sheet.Rules);
                        break;
                }
            }
        }

        private void Add(string? selector)
        {
            if (string.IsNullOrEmpty(selector))
            {
                return;
            }

            var hasRelational = selector.Contains(":has(", StringComparison.OrdinalIgnoreCase);
            HasFirstFormatted |= selector.Contains(":first-line", StringComparison.OrdinalIgnoreCase) || selector.Contains(":first-letter", StringComparison.OrdinalIgnoreCase);

            // a '+' in an argument (e.g. :nth-child(2n+1)) is taken for a combinator, which only costs a full layout
            if (!hasRelational && !selector.Contains('+') && !selector.Contains('~'))
            {
                return;
            }

            HasRelational |= hasRelational;

            if (hasRelational)
            {
                foreach (var compound in SplitCompounds(selector).Where(compound => compound.Contains(":has(", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!RelationalCompounds.Contains(compound))
                    {
                        RelationalCompounds.Add(compound);
                    }
                }
            }

            foreach (Match match in s_attributeSelectors.Matches(selector))
            {
                var name = match.Groups[1].Value;
                var expected = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Success ? match.Groups[4].Value : match.Groups[5].Value;

                if (!_attributes.TryGetValue(name, out var list))
                {
                    _attributes[name] = list = [];
                }

                list.Add(new AttributeSelector(match.Groups[2].Value, expected, match.Groups[6].Value is "i" or "I"));
            }

            foreach (Match match in s_selectorTokens.Matches(selector))
            {
                var token = match.Value.Replace("\\", string.Empty);
                _tokens.Add(token);
                RefersToAttributeState |= token[0] == ':' && s_attributeStatePseudoClasses.Contains(token);
            }
        }
    }

    /// <summary>An attribute selector (e.g. <c>[class*="ltag"]</c>), evaluated against a value of the attribute.</summary>
    private sealed record AttributeSelector(string Operator, string Value, bool IgnoresCase)
    {
        public bool Matches(string? actual, bool ignoreCase)
        {
            if (actual is null)
            {
                return false;
            }

            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return Operator switch
            {
                "" => true,
                "=" => string.Equals(actual, Value, comparison),
                "~=" => Value.Length > 0 && !Value.Any(char.IsWhiteSpace) &&
                    actual.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(word => string.Equals(word, Value, comparison)),
                "|=" => string.Equals(actual, Value, comparison) || actual.StartsWith(Value + "-", comparison),
                "^=" => Value.Length > 0 && actual.StartsWith(Value, comparison),
                "$=" => Value.Length > 0 && actual.EndsWith(Value, comparison),
                "*=" => Value.Length > 0 && actual.Contains(Value, comparison),
                _ => true,
            };
        }
    }
}
