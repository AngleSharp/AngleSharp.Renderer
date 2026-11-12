namespace AngleSharp.Renderer;

using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
/// what it leaves for the boxes after it. A flex or grid item is laid out on its own as well, as
/// long as all its container read of it is the same (see <see cref="ItemLayout"/>). Else the
/// enclosing box is laid out instead.
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

    // the pseudo-classes depending on the value of a text field, besides :placeholder-shown (which only needs it to be empty or not)
    private static readonly string[] s_valueStatePseudoClasses = [":valid", ":invalid", ":in-range", ":out-of-range", ":user-valid", ":user-invalid", ":blank"];

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
        ItemLayout? Item = null,
        float AbsorbedTopMargin = 0f);

    /// <summary>
    /// How a flex or grid item was laid out by its container, which is laid out again the same
    /// way as long as nothing the container read of the item changed.
    /// </summary>
    private sealed record ItemLayout(bool IsRowDirection, float? MainSize, float? CrossSize, bool CrossMarginsPlaced, EdgeSizes UsedMargins)
    {
        public FlexItemContribution? Flex { get; init; }

        public GridItemContribution? Grid { get; init; }

        /// <summary>The main size an item got by being laid out (see <see cref="FlexItemLayoutInfo.HasDeferredMainSize"/>).</summary>
        public float DeferredMainSize { get; init; } = float.NaN;
    }

    /// <summary>
    /// All a flex container reads of an item to size and place its items: what it resolved of
    /// the item (without the references to it), and what the item would like as its cross size
    /// at its final main size (NaN if it has a definite one).
    /// </summary>
    private sealed record GridItemContribution(Dictionary<string, string> Properties, float AreaWidth, float MaxY, float MeasuredHeight);

    private sealed record FlexItemContribution(FlexItemLayoutInfo Info, float RelativeTo, float HeightRelativeTo, string? DeferringAlignItems, float FinalMainSize, float EstimatedCrossSize, float CrossAvailable);

    // the top margin of a block with those collapsing through it from its first children (see LayoutElement)
    private static float GetCollapsedTopMargin(ElementRenderNode node, float containingWidth, LayoutContext context)
    {
        var styleMap = GetOrCreateStyleMap(context, node.Ref, node.ComputedStyle);
        var box = ResolveBoxStyle(styleMap, node.Ref);
        var marginTop = ParseLength(styleMap, "margin-top", containingWidth, box.Margin.Top, allowAuto: false);
        var paddingTop = ParseLength(styleMap, "padding-top", containingWidth, box.Padding.Top, allowAuto: false);

        return box.BorderWidth.Top <= 0f && paddingTop <= 0f && !EstablishesBlockFormattingContext(styleMap, false, false, false, false) &&
            TryGetFirstCollapsibleChildTopMargin(node, containingWidth, context, out var firstChildTopMargin)
            ? CollapseMargins(marginTop, firstChildTopMargin)
            : marginTop;
    }

    /// <summary>
    /// Lets a flex or grid item be laid out again on its own (see <see cref="RecordLink"/>, which
    /// decided if its container may be left alone), from the arguments of its layout, which
    /// starts next.
    /// </summary>
    private static void RecordItemEntry(ElementRenderNode node, float x, float y, float width, RenderTextStyle textStyle, float maxY,
        float positionedX, float positionedY, float positionedWidth, float positionedHeight, ItemLayout item, LayoutContext context)
    {
        if (s_layoutCapture.Value is { Entries: not null, Links: { } links } capture)
        {
            if (links.Contains(node.Ref))
            {
                RecordContributionIfRead(node, textStyle, context);
            }

            capture.NextItem = links.Contains(node.Ref)
                ? new LayoutEntry(node, x, y, width, y, false, textStyle, maxY, float.NaN, positionedX, positionedY, positionedWidth, positionedHeight, null, HasBox: true, Item: item)
                : null;
        }
    }

    // what a grid reads of an item to place it and size its tracks, besides its contribution
    private static readonly string[] s_gridItemProperties =
    [
        "display", "grid-area", "grid-column", "grid-column-start", "grid-column-end", "grid-row", "grid-row-start", "grid-row-end", "order", "align-self", "justify-self",
        "width", "min-width", "max-width", "height", "min-height", "max-height", "box-sizing", "aspect-ratio",
        "margin-top", "margin-right", "margin-bottom", "margin-left", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
    ];

    private static Dictionary<string, string> ReadGridItemProperties(Dictionary<string, string> styleMap)
    {
        var properties = new Dictionary<string, string>(s_gridItemProperties.Length, StringComparer.Ordinal);

        foreach (var property in s_gridItemProperties)
        {
            if (styleMap.TryGetValue(property, out var value))
            {
                properties[property] = value;
            }
        }

        return properties;
    }

    private static GridItemContribution CreateGridItemContribution(ElementRenderNode node, Dictionary<string, string> styleMap, float areaWidth, float maxY, LayoutContext context) =>
        new(ReadGridItemProperties(styleMap), areaWidth, maxY, context.GridItemHeights.TryGetValue((node.Ref, areaWidth, maxY), out var height) ? height : float.NaN);

    /// <summary>
    /// A box whose parent's width was read may have been read along with it, without a record of
    /// its own (inline content is measured as text runs), so what it contributes is taken now,
    /// before anything changes: the box may be laid out on its own later (see LayoutSnapshot).
    /// </summary>
    private static void RecordContributionIfRead(ElementRenderNode node, RenderTextStyle textStyle, LayoutContext context)
    {
        if (s_layoutPass.Value is { } pass && !pass.Contributions.ContainsKey(node.Ref) && node.Parent is ElementRenderNode parent &&
            (pass.Contributions.TryGetValue(parent.Ref, out var read) || pass.ContentWidths.TryGetValue(parent.Ref, out read)))
        {
            ComputeOuterIntrinsicWidths(node, textStyle, read.PercentageBase, context);
        }
    }

    private static bool TakeItemEntry(LayoutCapture capture, ElementRenderNode node)
    {
        if (capture.NextItem is { } entry && ReferenceEquals(entry.Node, node))
        {
            capture.NextItem = null;
            capture.Entries![node.Ref] = entry;
            return true;
        }

        return false;
    }

    private static void RecordDeferredMainSize(ElementRenderNode node, float mainSize)
    {
        if (s_layoutCapture.Value is { Entries: { } entries } && entries.TryGetValue(node.Ref, out var entry) && entry.Item is { } item)
        {
            entries[node.Ref] = entry with { Item = item with { DeferredMainSize = mainSize } };
        }
    }

    /// <summary>
    /// What an in-flow block left for the boxes after it: where they start, the margin they
    /// collapse with, and if it left no floats reaching down to them.
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

        // a float or sticky box of a chained block passes the entries on to its content, as an item does (see RecordLink)
        if (hasBox && capture.Links is { } links && (IsFloated(styleMap) || IsStickyPositioned(styleMap)))
        {
            if (isPlain && node.Parent is ElementRenderNode floatParent && IsChained(capture, floatParent))
            {
                links.Add(node.Ref);
            }
            else
            {
                links.Remove(node.Ref);
            }
        }

        // only boxes not taking part in the layout of the others, or blocks of a block flow, laid out the ordinary way
        if (!isPlain || (isInFlow && !IsInFlowEntry(capture, node, styleMap, floats, cursorY, previousBlockMarginBottom, context)))
        {
            capture.Entries!.Remove(node.Ref);
            return;
        }

        if (isInFlow)
        {
            RecordContributionIfRead(node, textStyle, context);
            capture.Entries![node.Ref] = new LayoutEntry(node, containingX, containingY, containingWidth, cursorY, textIndentConsumed, textStyle, maxY,
                containingHeight, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight, null, HasBox: true,
                IsInFlow: true, PreviousBlockMarginBottom: previousBlockMarginBottom, SuppressNextBlockTopMargin: suppressNextBlockTopMargin,
                AbsorbedTopMargin: suppressNextBlockTopMargin ? GetCollapsedTopMargin(node, containingWidth, context) : 0f);
            return;
        }

        var deferContainer = float.IsNaN(positionedContainingHeight)
            ? FindContainingBlockScope(positionedContainingX, positionedContainingY, context)?.Container?.Ref
            : null;

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
    private static bool IsInFlowEntry(LayoutCapture capture, ElementRenderNode node, Dictionary<string, string> styleMap, FloatArea floats, float cursorY, float previousBlockMarginBottom, LayoutContext context)
    {
        // floats ending above it don't get in its way, unless a negative margin takes it back up to them
        if ((!floats.IsEmpty && (floats.HasFloatsBelow(cursorY) || previousBlockMarginBottom < 0f || ParseLength(styleMap, "margin-top", 0f, 0f, allowAuto: false) < 0f)) ||
            node.Parent is not ElementRenderNode parent || IsFloated(styleMap) || IsStickyPositioned(styleMap) ||
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

        /// <summary>
        /// Gets what the layouts since the last frame took (the full one, or the boxes laid out
        /// again since), and starts collecting anew for the next frame.
        /// </summary>
        public RenderMetricsRecorder TakeMetrics()
        {
            var metrics = _pass.Metrics;
            _pass.Metrics = new RenderMetricsRecorder();
            return metrics;
        }
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
        // the boxes whose decorations are painted again with their new style map (see TryRepaint)
        private readonly Dictionary<object, Dictionary<string, string>> _repaints = [];
        // if a repaint changed a cursor or what can be hit (see TryRepaint)
        private bool _repaintsChangeHitTesting;

        /// <summary>
        /// Gets if all that changed since the frame is painted again only (see TryRepaint), which
        /// leaves the boxes and what can be hit where they are.
        /// </summary>
        public bool IsRepaintOnly => _relayouts.Count == 0 && !_repaintsChangeHitTesting && _rendered.Count == 0;

        // the elements whose own text is painted again in a new style (see TryRepaint), with their new
        // style map and if their text has no decorations of an ancestor's (which may change as well)
        private readonly Dictionary<IElement, (Dictionary<string, string> StyleMap, bool HasOwnDecorationsOnly)> _recolors = new(ReferenceEqualityComparer.Instance);
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
            // a partial layout is never updated (see TryUpdate), which compares them
            _relationalMatches = pass.IsCutOff || pass.BuiltNodes is not null ? [] : GetSiblingSelectors().RelationalCompounds
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
        public bool IsUnchangedSinceFrame => _isFrameLayout && _repaints.Count == 0 && _recolors.Count == 0 && _rendered.Values.All(rendered => !rendered.Before && !rendered.Now);

        /// <summary>Gets if this is a frame's layout (see <see cref="TryPatch"/>).</summary>
        public bool IsFrameLayout => _isFrameLayout;

        /// <summary>Gets if the layout ends below the viewport, leaving out the rest of the page.</summary>
        public bool IsPartial => _pass.IsCutOff || _pass.BuiltNodes is not null;

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

            if (!TryApplyRepaints(displayList))
            {
                return false;
            }

            // the patched display list is the frame from now on
            _rendered.Clear();
            _relayouts.Clear();
            _repaints.Clear();
            _recolors.Clear();
            _repaintsChangeHitTesting = false;
            return true;
        }

        // the properties that change what a box paints of its own, but not where anything is
        private static readonly HashSet<string> s_decorationProperties = new(StringComparer.Ordinal)
        {
            "background-color", "background-image", "background-position", "background-size", "background-repeat", "background-origin",
            "background-blend-mode", "background-clip", "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
            "outline-color", "outline-style", "outline-width", "outline-offset", "box-shadow",
        };

        // the properties that change neither the layout nor the paint (but the cursor of the frame's map)
        private static readonly HashSet<string> s_inertProperties = new(StringComparer.Ordinal) { "cursor", "pointer-events" };

        /// <summary>
        /// Styles the subtree of the element anew, and if that only changed what its boxes paint
        /// of their own (e.g. a background or a border color by hovering) or the color of their
        /// text, keeps that to paint again in the frame (see <see cref="TryPatch"/>), instead of
        /// laying out anything.
        /// </summary>
        /// <param name="window">The window of the document.</param>
        /// <param name="element">The element whose style may have changed.</param>
        /// <param name="reachesDescendants">If the styles of its descendants may have changed, too (else only by inheriting changed values).</param>
        /// <returns>True if nothing but that changed, false if the subtree is to be laid out again.</returns>
        private bool TryRepaint(IWindow window, IElement element, bool reachesDescendants)
        {
            if (!_isFrameLayout || !_context.RecordsPaintAnchors || element.ParentElement is not { } parent ||
                !_pass.Nodes.TryGetValue(parent, out var parentNode) || !_context.StyleMapCache.ContainsKey(element))
            {
                return false;
            }

            // the nodes as rendered before, whose pseudo-elements were created with them
            var previousNodes = new Dictionary<IElement, ElementRenderNode>(ReferenceEqualityComparer.Instance);

            foreach (var descendant in element.DescendantsAndSelf<IElement>())
            {
                if (_pass.Nodes.TryGetValue(descendant, out var previousNode))
                {
                    previousNodes[descendant] = previousNode;
                }
            }

            // the children are only styled if they may have changed, the rules are the ones of the layout
            var node = _context.GetStyleCollection(element) is { } styleCollection ? window.RenderPartial(element, parentNode, styleCollection) : window.Render(element, parentNode, _renderDevice);
            var styles = new List<(IElement Element, Dictionary<string, string> StyleMap)>();
            var repaints = new List<(object Key, Dictionary<string, string> StyleMap)>();
            var recolors = new List<(IElement Element, (Dictionary<string, string>, bool) Text)>();
            var changesHitTesting = false;
            if (!Compare(node, reachesDescendants))
            {
                return false;
            }

            foreach (var (changed, styleMap) in styles)
            {
                _context.StyleMapCache[changed] = styleMap;
                _pass.PlaceholderStyles.Remove(changed);
            }

            foreach (var (changed, styleMap) in repaints)
            {
                _repaints[changed] = styleMap;
            }

            foreach (var (changed, text) in recolors)
            {
                _recolors[changed] = text;
            }

            _repaintsChangeHitTesting |= changesHitTesting;

            return true;

            bool Compare(IRenderNode current, bool visitsChildren)
            {
                if (current is ElementRenderNode elementNode)
                {
                    var target = elementNode.Ref;

                    // the styles of the nodes looked at, as CollectSpecifiedStyles takes them for a layout
                    _context.SpecifiedStyles[target] = elementNode.SpecifiedStyle;
                    _pass.ComputedStyles[target] = elementNode.ComputedStyle;
                    _pass.Nodes[target] = elementNode;

                    var after = CreateStyleMap(elementNode.ComputedStyle, target, _context);
                    var isHidden = string.Equals(after.GetValueOrDefault("display")?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
                    var previous = target is IPseudoElement pseudo && elementNode.Parent is { } host && previousNodes.TryGetValue(host.Ref, out var previousHost)
                        ? previousHost.Children.OfType<ElementRenderNode>().FirstOrDefault(child => child.Ref is IPseudoElement other && other.PseudoName == pseudo.PseudoName)?.Ref
                        : target;

                    if (previous is null || !_context.StyleMapCache.TryGetValue(previous, out var before))
                    {
                        // a box that was not laid out (and is not now) paints nothing; any other (e.g. of a
                        // pseudo-element, which is created anew) is laid out
                        return isHidden && target is not IPseudoElement;
                    }

                    if (_context.BoxSystems.TryGetValue(target, out var boxSystem))
                    {
                        RemapStyleMapToLogical(after, boxSystem);
                    }

                    var isDecorated = false;
                    var isRecolored = false;
                    var changesDecorationLines = false;

                    foreach (var name in before.Keys.Union(after.Keys))
                    {
                        if (string.Equals(before.GetValueOrDefault(name), after.GetValueOrDefault(name), StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // what the children inherit changed (custom properties are none of the style map's)
                        visitsChildren |= s_inheritedProperties.Contains(name);

                        if (s_inertProperties.Contains(name))
                        {
                            changesHitTesting = true;
                            continue;
                        }

                        if (s_decorationProperties.Contains(name) && !string.Equals(after.GetValueOrDefault("background-clip")?.Trim(), "text", StringComparison.OrdinalIgnoreCase))
                        {
                            isDecorated = true;
                        }
                        else if ((name == "color" || s_textDecorationProperties.Contains(name)) && CanRestyleText(elementNode, after))
                        {
                            isRecolored = true;
                            changesDecorationLines |= name != "color";
                        }
                        else
                        {
                            return false;
                        }
                    }

                    // the lines of a box are painted across the text of its descendants, too
                    if (changesDecorationLines && elementNode.Children.OfType<ElementRenderNode>().Any(GeneratesBoxes))
                    {
                        return false;
                    }

                    styles.Add((target, after));

                    // a box that is not rendered (e.g. in a hidden menu), and is not now, paints nothing
                    if (isHidden || (target is not IPseudoElement && !_metrics.ContainsKey(target)))
                    {
                        return true;
                    }

                    if (isDecorated)
                    {
                        repaints.Add((GetPaintKey(elementNode), after));
                    }

                    if (isRecolored)
                    {
                        var hasOwnDecorationsOnly = HasNoDecoratingAncestor(target);

                        if (changesDecorationLines && !hasOwnDecorationsOnly)
                        {
                            return false;
                        }

                        recolors.Add((target, (after, hasOwnDecorationsOnly)));
                    }
                }

                if (!visitsChildren)
                {
                    return true;
                }

                foreach (var child in current.Children)
                {
                    if (!Compare(child, visitsChildren: reachesDescendants))
                    {
                        return false;
                    }
                }

                return true;
            }

            // text of the element's own, painted by the layout with its fragments (no shadows, no
            // markers, no small caps, nothing a replaced element or a control paints of its own)
            static bool CanRestyleText(ElementRenderNode node, Dictionary<string, string> styleMap) =>
                node.Ref is not IPseudoElement && !IsReplacedElement(node.Ref) && ResolveFormControlKind(node) == FormControlKind.None &&
                styleMap.GetValueOrDefault("display")?.Contains("list-item", StringComparison.OrdinalIgnoreCase) != true &&
                styleMap.GetValueOrDefault("text-shadow")?.Trim() is null or "" or "none" &&
                styleMap.GetValueOrDefault("font-variant-caps")?.Trim() is null or "" or "normal";

            // if the lines of the element's text are its own (no ancestor declares any)
            bool HasNoDecoratingAncestor(IElement target)
            {
                for (var ancestor = target.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
                {
                    if (!_context.StyleMapCache.TryGetValue(ancestor, out var ancestorStyle) || HasOwnTextDecorationLines(ancestorStyle))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Gets if the inherited property is one of a style map, whose change is looked at in the
        /// children of a box painted again (see TryRepaint).
        /// </summary>
        public static bool IsComparedInChildren(string name) => s_inheritedProperties.Contains(name);

        // the inherited properties of a style map, whose change the children of the box take
        private static readonly HashSet<string> s_inheritedProperties = new(StringComparer.Ordinal)
        {
            "color", "cursor", "visibility", "pointer-events", "font-size", "font-family", "font-weight", "font-style", "font-stretch",
            "font-variant-caps", "font-feature-settings", "font-kerning", "font-variant-ligatures", "font-variant-numeric", "font-variant-east-asian",
            "line-height", "letter-spacing", "word-spacing", "text-align", "text-align-last", "text-indent", "text-transform", "text-shadow",
            "white-space", "word-break", "overflow-wrap", "word-wrap", "hyphens", "tab-size", "direction", "lang", "list-style-type",
            "list-style-position", "writing-mode", "text-orientation", "accent-color", "border-collapse", "border-spacing", "caption-side",
            "empty-cells", "text-decoration", "text-decoration-line", "text-decoration-color", "text-decoration-style", "text-decoration-thickness",
        };

        // the properties of the lines painted with text (see ResolveTextStyle)
        private static readonly HashSet<string> s_textDecorationProperties = new(StringComparer.Ordinal)
        {
            "text-decoration", "text-decoration-line", "text-decoration-color", "text-decoration-style", "text-decoration-thickness",
        };

        /// <summary>
        /// Paints the decorations of the boxes kept by <see cref="TryRepaint"/> in their new style,
        /// in place of their old ones, and the text of the recolored elements in its new color.
        /// </summary>
        /// <returns>True if all of them were found, false if the frame needs a layout.</returns>
        private bool TryApplyRepaints(DisplayList displayList)
        {
            if (_repaints.Count == 0 && _recolors.Count == 0)
            {
                return true;
            }

            var commands = displayList.Commands;
            var repainted = new HashSet<object>();
            var replacements = new List<(int Start, int Count, IReadOnlyList<RenderCommand> Commands)>();
            var fragmentOwner = default(IElement);

            for (var i = 0; i < commands.Count; i++)
            {
                switch (commands[i])
                {
                    case PaintAnchorCommand anchor when _repaints.TryGetValue(anchor.Key, out var styleMap):
                    {
                        var end = i + 1;

                        while (end < commands.Count && commands[end] is not PaintAnchorEndCommand)
                        {
                            end++;
                        }

                        if (end == commands.Count)
                        {
                            return false;
                        }

                        var buffer = new DisplayList { Fonts = displayList.Fonts };
                        var bounds = anchor.BorderBox;
                        var box = ResolveBoxStyle(styleMap, anchor.Element);
                        box = box with { BorderRadius = ResolveBorderRadii(styleMap, bounds.Width, bounds.Height) };
                        var context = _context with { RecordsPaintAnchors = false };
                        PaintBoxDecorations(buffer, context, styleMap, anchor.Element, anchor.Key, box, bounds,
                            new EdgeSizes(anchor.Border.Y, anchor.Border.Width, anchor.Border.Height, anchor.Border.X),
                            new EdgeSizes(anchor.Padding.Y, anchor.Padding.Width, anchor.Padding.Height, anchor.Padding.X),
                            anchor.FillsTransparentBackground);
                        replacements.Add((i + 1, end - i - 1, buffer.Commands.ToList()));
                        repainted.Add(anchor.Key);
                        i = end;
                        break;
                    }
                    case PushTextFragmentCommand { Fragment: var fragment } when _recolors.Count > 0:
                        fragmentOwner = null;

                        for (var segment = 0; segment < fragment.Segments.Length; segment++)
                        {
                            var owner = fragment.Segments[segment].Node.ParentElement;
                            var restyled = owner is not null && _recolors.ContainsKey(owner) ? owner : null;

                            // a run of the text of several elements of which some are restyled
                            if (segment > 0 && restyled != fragmentOwner)
                            {
                                return false;
                            }

                            fragmentOwner = restyled;
                        }

                        break;
                    case PopTextFragmentCommand:
                        fragmentOwner = null;
                        break;
                    case DrawTextCommand text when fragmentOwner is not null:
                        if (RestyleText(text, _recolors[fragmentOwner]) is not { } restyledText)
                        {
                            return false;
                        }

                        replacements.Add((i, 1, [restyledText]));
                        break;
                    case DrawTextShadowCommand when fragmentOwner is not null:
                        return false;
                }
            }

            // the text in the new style: its color, and the lines if they are the element's own (as
            // ResolveTextStyle has them), or null if it has lines of an ancestor
            static DrawTextCommand? RestyleText(DrawTextCommand text, (Dictionary<string, string> StyleMap, bool HasOwnDecorationsOnly) restyle)
            {
                var (styleMap, hasOwnDecorationsOnly) = restyle;
                var color = ParseColor(styleMap.GetValueOrDefault("color"), text.Color);

                if (!hasOwnDecorationsOnly)
                {
                    return text.Underline || text.StrikeThrough || text.Overline ? null : text with { Color = color };
                }

                var (underline, overline, strikeThrough) = ParseTextDecoration(styleMap, false, false, false);
                var declares = HasOwnTextDecorationLines(styleMap);
                return text with
                {
                    Color = color,
                    Underline = underline,
                    Overline = overline,
                    StrikeThrough = strikeThrough,
                    DecorationColor = declares ? ParseColor(styleMap.TryGetValue("text-decoration-color", out var decorationColor) ? decorationColor : null, color) : text.DecorationColor,
                    DecorationStyle = declares ? ParseTextDecorationStyle(styleMap, RenderTextDecorationStyle.Solid) : text.DecorationStyle,
                    DecorationThickness = declares ? ParseLength(styleMap, "text-decoration-thickness", text.FontSize, float.NaN, allowAuto: true) : text.DecorationThickness,
                };
            }

            // a box that is painted nowhere (e.g. in a trial only) is not known to look the same
            if (repainted.Count != _repaints.Count)
            {
                return false;
            }

            for (var i = replacements.Count - 1; i >= 0; i--)
            {
                var (start, count, replacement) = replacements[i];
                displayList.ReplaceRange(start, count, replacement);
            }

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

        internal IReadOnlyDictionary<IElement, SubframeEntry> Subframes => _context.Subframes;

        internal void ResolveSubframes() => HtmlRenderer.ResolveSubframes(_document, _context);

        public static LayoutSnapshot Capture(IDocument document, IRenderDevice renderDevice) =>
            LayOut(document, renderDevice, s_defaultTextMeasurer, 0f, 0, isFrame: false, out _);

        /// <summary>
        /// Lays out the whole document scrolled by <paramref name="scrollOffsetY"/> (unculled, as
        /// geometry reads and hit testing need all of it), on a surface that many rows taller
        /// than the viewport, or, for <paramref name="viewportOnly"/>, just as much of it as
        /// the surface shows (see <see cref="IsPartial"/>).
        /// </summary>
        public static LayoutSnapshot LayOut(IDocument document, IRenderDevice renderDevice, ITextMeasurer textMeasurer, float scrollOffsetY, int surfaceExtraHeight, bool isFrame, out DisplayList displayList, bool viewportOnly = false)
        {
            var pass = new LayoutPass();
            var setupStart = RenderMetricsRecorder.Now;
            var context = CreateLayoutContext(document, renderDevice, textMeasurer) with { RecordsPaintAnchors = isFrame };
            pass.Metrics.Setup += RenderMetricsRecorder.Since(setupStart);
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
            var previous = s_layoutCapture.Value;
            s_layoutCapture.Value = capture;

            try
            {
                displayList = BuildDisplayList(document, viewport, context, renderDevice, scrollOffsetY, measureFullExtent: !viewportOnly, surfaceExtraHeight: surfaceExtraHeight, pass: pass);
                var hitTestingStart = RenderMetricsRecorder.Now;
                var metrics = AddHitTestLayers(capture.Snapshot(), context);
                pass.Metrics.HitTesting += RenderMetricsRecorder.Since(hitTestingStart);
                return new LayoutSnapshot(document, renderDevice, context, pass, capture, metrics, scrollOffsetY, isFrame);
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
        /// <param name="restyled">
        /// The elements whose styles changed since without a change of the DOM, e.g. by hovering
        /// (see <see cref="GetStateEffect"/>): their subtrees are styled and laid out again, too.
        /// </param>
        /// <returns>True if the metrics are up to date, false if a full layout is needed.</returns>
        public bool TryUpdate(IReadOnlyList<IMutationRecord> records, IReadOnlyDictionary<IElement, bool>? restyled = null)
        {
            // how many enclosing roots a change may be laid out with, before a full layout is cheaper
            const int MaxEscalations = 3;
            // how many elements are not worth a full layout, whatever the size of the document
            const int MinElementsLaidOutAnyway = 200;

            // what is below the viewport was never laid out
            if (IsPartial || _document.DefaultView is not { } window)
            {
                return false;
            }

            var roots = new List<IElement>();

            foreach (var record in records)
            {
                if (GetChangedSubtree(record) is not { } changed || !AddRoot(changed))
                {
                    return false;
                }
            }

            // the style maps of what is painted again are created as for a layout of the pass (e.g.
            // with the rules of its style elements read once)
            var passBefore = s_layoutPass.Value;
            s_layoutPass.Value = _pass;

            try
            {
                if (!TryRestyle())
                {
                    return false;
                }
            }
            finally
            {
                s_layoutPass.Value = passBefore;
            }

            bool TryRestyle()
            {
                foreach (var (element, reachesDescendants) in restyled ?? new Dictionary<IElement, bool>())
                {
                    // what has no box (e.g. in a hidden menu) gets none by its own style: the ancestor
                    // hiding it would have to change (which is restyled itself), only the children of a
                    // box with `display: contents` have boxes; what is not rendered at all has none of
                    // its own either (the content of an SVG is painted from the document)
                    if (!_metrics.ContainsKey(element) && element is not AngleSharp.Svg.Dom.ISvgElement &&
                        (!_pass.ComputedStyles.TryGetValue(element, out var computed) ||
                        !string.Equals(computed.GetDisplay()?.Trim(), "contents", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // what left the document since is gone with its subtree; what only paints differently is painted again
                    if (element.Owner == _document && _document.Contains(element) && !TryRepaint(window, element, reachesDescendants) && !AddRoot(element))
                    {
                        return false;
                    }
                }

                return true;
            }

            bool AddRoot(IElement changed)
            {
                if (FindLayoutRoot(changed) is not { } root)
                {
                    return false;
                }

                if (!roots.Any(other => other == root || other.Contains(root)))
                {
                    roots.RemoveAll(root.Contains);
                    roots.Add(root);
                }

                return true;
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
                    var attempted = 0;

                    for (var attempt = 0; !TryLayOutAgain(window, current, out var escalates); attempt++)
                    {
                        if (!escalates || attempt == MaxEscalations || current.ParentElement is not { } parentElement ||
                            FindLayoutRoot(parentElement) is not { } enclosing)
                        {
                            return false;
                        }

                        // the attempts must not cost more than the full layout they try to avoid
                        attempted += current.DescendantsAndSelf<IElement>().Count();
                        var next = attempted + enclosing.DescendantsAndSelf<IElement>().Count();

                        if (next > MinElementsLaidOutAnyway && next * 2 > _document.DocumentElement.DescendantsAndSelf<IElement>().Count())
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
                // what is typed into a text field only matters to the states of its value
                case "value" when element is AngleSharp.Html.Dom.IHtmlInputElement:
                    return !s_valueStatePseudoClasses.Any(selectors.Refers) &&
                        (string.IsNullOrEmpty(previousValue) == string.IsNullOrEmpty(value) || !selectors.Refers(":placeholder-shown"));
                default:
                    return !selectors.RefersToAttributeState;
            }
        }

        private SiblingSelectors GetSiblingSelectors() => _siblingSelectors ??= SiblingSelectors.Create(_document);

        /// <summary>
        /// Gets what a change of the given state (a pseudo-class like hover) of the element does to
        /// the styles of the document (see <see cref="SiblingSelectors.GetStateEffect"/>).
        /// </summary>
        public StateEffect GetStateEffect(IElement element, string pseudoClass)
        {
            // the rules of shadow trees are not looked into: what they may style in the state (an
            // element of the tree or its host, also from outside by `::part()`) is laid out again
            if ((element.ShadowRoot is { } shadowRoot && MentionsState(shadowRoot, pseudoClass, partsOnly: false)) ||
                (element.GetRoot() is IShadowRoot root && (MentionsState(root, pseudoClass, partsOnly: false) || StylesPartsInState(pseudoClass))))
            {
                return StateEffect.Unconfined;
            }

            return GetSiblingSelectors().GetStateEffect(element, pseudoClass);
        }

        // if a style rule of the tree (or one of a shadow tree in it, for parts) refers to the state
        private readonly Dictionary<(INode Root, string PseudoClass, bool PartsOnly), bool> _stateMentions = new();

        private bool MentionsState(INode root, string pseudoClass, bool partsOnly)
        {
            if (!_stateMentions.TryGetValue((root, pseudoClass, partsOnly), out var mentions))
            {
                var sheets = root is IShadowRoot shadowRoot ? shadowRoot.GetEffectiveStyleSheets() : ((IDocument)root).GetEffectiveStyleSheets();
                mentions = sheets.Any(sheet => Mentions(sheet.Rules));
                _stateMentions[(root, pseudoClass, partsOnly)] = mentions;
            }

            return mentions;

            bool Mentions(ICssRuleList rules) => rules.Any(rule => rule switch
            {
                ICssStyleRule style => (style.SelectorText.Contains(":" + pseudoClass, StringComparison.OrdinalIgnoreCase) &&
                    (!partsOnly || style.SelectorText.Contains("::part", StringComparison.OrdinalIgnoreCase))) || Mentions(style.Rules),
                ICssGroupingRule group => Mentions(group.Rules),
                ICssImportRule { Sheet: { } sheet } => Mentions(sheet.Rules),
                _ => false,
            });
        }

        // if a part of a shadow tree may be styled in the state from outside of it
        private bool StylesPartsInState(string pseudoClass)
        {
            return InTree(_document);

            bool InTree(INode root)
            {
                if (MentionsState(root, pseudoClass, partsOnly: true))
                {
                    return true;
                }

                foreach (var element in root.Descendants<IElement>())
                {
                    if (element.ShadowRoot is { } shadowRoot && InTree(shadowRoot))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Gets the computed style of the element as of the layout (kept up to date with the
        /// changes taken in since), if it was rendered.
        /// </summary>
        public ICssStyleDeclaration? GetComputedStyle(IElement element) => _pass.ComputedStyles.GetValueOrDefault(element);

        /// <summary>
        /// Computes the style of the element as it is now, inheriting from the given computed style
        /// of its parent, with the rules gathered for the layout (a cascade of the element only).
        /// </summary>
        public ICssStyleDeclaration? ComputeStyle(IElement element, ICssStyleDeclaration parentStyle) =>
            _context.GetStyleCollection(element) is { } styles ? styles.ComputeDeclarationsWithParent(element, parentStyle) : null;

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

            if (entry.Item is not null && (!_metrics.TryGetValue(root, out laidOutBefore) || GetSiblingSelectors().HasFirstFormatted || !TryGetStickyShift(root, out _, out _) || ReachesOutOfItsSubtree(entry.Node)))
            {
                escalates = true;
                return false;
            }

            if (entry.IsInFlow && (result is not { LeftNoFloats: true } || !_metrics.TryGetValue(root, out laidOutBefore) ||
                GetSiblingSelectors().HasFirstFormatted || !TryGetStickyShift(root, out _, out _) || ReachesOutOfItsSubtree(entry.Node)))
            {
                escalates = true;
                return false;
            }

            // what the layout read of it for sizing the boxes around it, as it can't be asked for
            // anymore: the text in the DOM has changed, as have the styles after a failed attempt
            (float PercentageBase, IntrinsicWidths Widths)? contribution = _pass.Contributions.TryGetValue(root, out var read) ? read : null;
            (float PercentageBase, IntrinsicWidths Widths)? contentWidths = _pass.ContentWidths.TryGetValue(root, out var readContent) ? readContent : null;

            // what the display list shows of it, before it is forgotten
            var hadBox = _rendered.TryGetValue(root, out var rendered) ? rendered.Before : entry.HasBox;
            (RenderCommand First, int Count)? before = _ranges.TryGetValue(root, out var range) ? range : null;

            Forget(entry.Node);
            var styleStart = RenderMetricsRecorder.Now;
            PrepareElementsForRendering(root.DescendantsAndSelf<IElement>());

            // the rules are the ones of the layout (changing them needs a full layout, see GetChangedSubtree)
            var node = _context.GetStyleCollection(root) is { } styleCollection ? window.RenderPartial(root, parent, styleCollection) : window.Render(root, parent, _renderDevice);
            BuildAnonymousTableBoxes(node);
            CollectSpecifiedStyles(node, _context);
            ResolveWritingModeSystems(node, WritingModeKind.HorizontalTb, _context);
            _pass.Metrics.Style += RenderMetricsRecorder.Since(styleStart);
            var layoutStart = RenderMetricsRecorder.Now;
            ProcessCounters(node, _context);

            var styleMap = GetOrCreateStyleMap(_context, root, node.ComputedStyle);

            if (entry.Item is { } item)
            {
                // the container is left alone if it would see the same item
                if (!GeneratesBoxes(node) || IsOutOfFlowPositioned(styleMap) || IsStickyPositioned(styleMap) || ReachesOutOfItsSubtree(node) ||
                    !LooksTheSameToContainer(node, entry, item))
                {
                    escalates = true;
                    return false;
                }
            }
            else if (entry.IsInFlow)
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
            var enclosingSticky = s_stickyElement;

            try
            {
                // laid out inside its sticky ancestors, which moved it after their layouts
                s_stickyElement = FindStickyAncestor(root);

                if (entry.Item is not null)
                {
                    capture.NextItem = entry with { Node = node };
                }

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
                    isFlexItem: entry.Item is not null,
                    isRowDirection: entry.Item?.IsRowDirection ?? true,
                    flexMainSize: entry.Item?.MainSize,
                    flexCrossSize: entry.Item?.CrossSize,
                    containingHeight: entry.ContainingHeight,
                    positionedContainingX: entry.PositionedContainingX,
                    positionedContainingY: entry.PositionedContainingY,
                    positionedContainingWidth: entry.PositionedContainingWidth,
                    positionedContainingHeight: positionedContainingHeight,
                    mainAxisMarginsPlaced: entry.Item is not null,
                    crossAxisMarginsPlaced: entry.Item?.CrossMarginsPlaced ?? false);

                if (entry.Item is { } placedItem)
                {
                    SetUsedMargins(root, placedItem.UsedMargins);
                }

                if (TryGetStickyShift(root, out var stickyX, out var stickyY) && (stickyX != 0f || stickyY != 0f))
                {
                    MoveRecorded(capture, stickyX, stickyY);
                }

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
                s_stickyElement = enclosingSticky;
                s_layoutCapture.Value = previous;
                _pass.Metrics.Layout += RenderMetricsRecorder.Since(layoutStart);
            }

            // an item that got its main size by being laid out has to keep it, and so its box
            if (entry.Item is { } sameItem && ((!float.IsNaN(sameItem.DeferredMainSize) && Math.Max(0f, cursorY - entry.CursorY) != sameItem.DeferredMainSize) ||
                !capture.TryGet(root, out var laidOutAsItem) || !HasSameBox(laidOutAsItem, laidOutBefore) || !ContributesTheSame(contribution, contentWidths, node, entry)))
            {
                escalates = true;
                return false;
            }

            // the boxes after an in-flow block are where they were if it is the same from outside
            if (entry.IsInFlow && (cursorY != result!.CursorY || previousBlockMarginBottom != result.PreviousBlockMarginBottom ||
                suppressNextBlockTopMargin != result.SuppressNextBlockTopMargin || textIndentConsumed != result.TextIndentConsumed || floats.HasFloatsBelow(cursorY) ||
                !capture.TryGet(root, out var laidOut) || !HasSameBox(laidOut, laidOutBefore) ||
                !ContributesTheSame(contribution, contentWidths, node, entry) ||
                (entry.SuppressNextBlockTopMargin && GetCollapsedTopMargin(node, entry.ContainingWidth, _context) != entry.AbsorbedTopMargin)))
            {
                escalates = true;
                return false;
            }

            var hitTestingStart = RenderMetricsRecorder.Now;

            foreach (var (element, metrics) in AddHitTestLayers(capture.Snapshot(), _context))
            {
                _metrics[element] = metrics;
                _pageMetrics[element] = ToPage(metrics);
            }

            _pass.Metrics.HitTesting += RenderMetricsRecorder.Since(hitTestingStart);

            _rendered[root] = (hadBox, GeneratesBoxes(node));
            _relayouts.Add((root, before, displayList.Commands.ToList()));

            return true;
        }

        // what the box around it read of it (if anything) comes out the same
        private bool ContributesTheSame((float PercentageBase, IntrinsicWidths Widths)? contribution, (float PercentageBase, IntrinsicWidths Widths)? contentWidths, ElementRenderNode node, LayoutEntry entry) =>
            (contribution is not { } outer || ComputeOuterIntrinsicWidths(node, entry.TextStyle, outer.PercentageBase, _context) == outer.Widths) &&
            (contentWidths is not { } content || ComputeContentIntrinsicWidths(node, ResolveTextStyle(GetOrCreateStyleMap(_context, node.Ref, node.ComputedStyle), entry.TextStyle, _context), content.PercentageBase, _context) == content.Widths);

        /// <summary>
        /// Whether a flex or grid container would size and place its items as it did: what it reads
        /// of the item is the same, including the size the item would like at its main size.
        /// </summary>
        private bool LooksTheSameToContainer(ElementRenderNode node, LayoutEntry entry, ItemLayout item)
        {
            if (item.Grid is { } grid)
            {
                // measured for its row, if at all, which the row has to get again
                var styleMap = GetOrCreateStyleMap(_context, node.Ref, node.ComputedStyle);
                var properties = ReadGridItemProperties(styleMap);
                return properties.Count == grid.Properties.Count && properties.All(property => grid.Properties.GetValueOrDefault(property.Key) == property.Value) &&
                    (float.IsNaN(grid.MeasuredHeight) || MeasureGridItemHeight(node, grid.AreaWidth, entry.TextStyle, _context, grid.MaxY) == grid.MeasuredHeight);
            }

            if (item.Flex is not { } flex)
            {
                return false;
            }

            var info = CreateFlexItemLayoutInfo(node, item.IsRowDirection, flex.RelativeTo, flex.HeightRelativeTo, entry.TextStyle, _context, flex.DeferringAlignItems);

            return info with { Node = null!, Style = null! } == flex.Info &&
                (float.IsNaN(flex.EstimatedCrossSize) || EstimateFlexItemCrossSize(node, flex.FinalMainSize, item.IsRowDirection, entry.TextStyle, _context, flex.CrossAvailable) == flex.EstimatedCrossSize);
        }

        private static bool HasSameBox(ElementLayoutMetrics first, ElementLayoutMetrics second) =>
            first with { Layer = default, Sticky = null } == second with { Layer = default, Sticky = null };

        // The nearest sticky ancestor, which is what the metrics of a box in it name.
        private IElement? FindStickyAncestor(IElement element)
        {
            for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                if (_context.StyleMapCache.TryGetValue(ancestor, out var styleMap) && IsStickyPositioned(styleMap))
                {
                    return ancestor;
                }
            }

            return null;
        }

        // Sticky boxes move what they contain (and its metrics) after its layout: how far they
        // moved the element, unless not known (e.g. waiting for a writing-mode system).
        private bool TryGetStickyShift(IElement element, out float x, out float y)
        {
            x = 0f;
            y = 0f;

            for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                if (_context.StyleMapCache.TryGetValue(ancestor, out var styleMap) && IsStickyPositioned(styleMap))
                {
                    if (!_stickies.TryGetValue(ancestor, out var sticky))
                    {
                        return false;
                    }

                    x += sticky.OffsetX;
                    y += sticky.OffsetY;
                }
            }

            return true;
        }

        private static void MoveRecorded(LayoutCapture capture, float x, float y)
        {
            foreach (var element in capture.RecordOrder.Distinct<IElement>(ReferenceEqualityComparer.Instance))
            {
                if (capture.TryGet(element, out var recorded) && !recorded.IsViewportAnchored)
                {
                    capture.Replace(element, recorded with { BorderBoxX = recorded.BorderBoxX + x, BorderBoxY = recorded.BorderBoxY + y });
                }
            }
        }

        // What a subtree lays out beyond itself: sticky boxes (stuck by an ancestor), content with
        // a negative z-index (painted below the in-flow content of its stacking context), counters
        // (counted through the whole document), and absolutely positioned boxes placed against an
        // ancestor outside of it (which may wait for the height of that one).
        private bool ReachesOutOfItsSubtree(IRenderNode node) => ReachesOutOfItsSubtree(node, hasContainingBlock: false);

        private bool ReachesOutOfItsSubtree(IRenderNode node, bool hasContainingBlock)
        {
            if (node is ElementRenderNode element)
            {
                var styleMap = GetOrCreateStyleMap(_context, element.Ref, element.ComputedStyle);

                if (!hasContainingBlock && string.Equals(GetPosition(styleMap), "absolute", StringComparison.OrdinalIgnoreCase) && DependsOnContainingBlockHeight(styleMap))
                {
                    return true;
                }

                hasContainingBlock |= ActsAsContainingBlock(styleMap, 0f);

                if (IsStickyPositioned(styleMap) || (HasExplicitZIndex(styleMap) && ParseZIndex(styleMap) < 0) ||
                    IsCounting("counter-increment") || IsCounting("counter-reset") || IsCounting("counter-set") ||
                    (element.Ref is IPseudoElement && element.ComputedStyle.GetPropertyValue("content")?.Contains("counter", StringComparison.OrdinalIgnoreCase) == true))
                {
                    return true;
                }

                bool IsCounting(string property) => styleMap.TryGetValue(property, out var value) && !string.IsNullOrWhiteSpace(value) &&
                    !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var child in node.Children)
            {
                if (ReachesOutOfItsSubtree(child, hasContainingBlock))
                {
                    return true;
                }
            }

            return false;
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
                _pass.PlaceholderStyles.Remove(element);

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
            // a trial holds the layout of its whole subtree, so any change can make one stale
            _context.TrialLayouts.Clear();
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
                        Add(style);
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

        // What a selector tells, kept with it: the selectors of all rules are looked at for every
        // layout of a document, and serializing them (e.g. tens of thousands on GitHub) was a large
        // part of what a layout allocated. A rule given another selector text gets another selector.
        private static readonly ConditionalWeakTable<ISelector, SelectorKind> s_selectorKinds = new();

        /// <param name="FirstFormatted">If it has <c>::first-line</c> or <c>::first-letter</c>.</param>
        /// <param name="Text">The selector, if it has <c>:has()</c> or sibling combinators, else null.</param>
        /// <param name="States">The compounds with a pseudo-class of an element's state (e.g. <c>:hover</c>), if any.</param>
        private sealed record SelectorKind(bool FirstFormatted, string? Text, List<StateCompound>? States = null);

        // the compounds of each pseudo-class of state, by a key an element needs to match them
        private readonly Dictionary<string, StateCompoundIndex> _states = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets what a change of the given state (a pseudo-class like hover or focus) of the element
        /// does: nothing, if no selector refers to it for an element like this one; else the styles of
        /// the element and its descendants may change (confined), or those of other elements too, e.g.
        /// with <c>:hover + .tip</c> or <c>:has(:hover)</c>.
        /// </summary>
        public StateEffect GetStateEffect(IElement element, string pseudoClass)
        {
            var effect = StateEffect.None;

            if (_states.TryGetValue(pseudoClass, out var index))
            {
                foreach (var compound in index.GetCandidates(element))
                {
                    if (compound.Matches(element))
                    {
                        if (!compound.IsConfined)
                        {
                            // e.g. `summary:has(a:hover)`, and hovering a link outside of a summary
                            if (compound.Ancestor is { } ancestor && !HasAncestor(element, ancestor))
                            {
                                continue;
                            }

                            return StateEffect.Unconfined;
                        }

                        effect = compound.ReachesDescendants || effect == StateEffect.Confined ? StateEffect.Confined : StateEffect.Self;
                    }
                }
            }

            return effect;
        }

        private static bool HasAncestor(IElement element, StateCompound compound)
        {
            for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
            {
                if (compound.Matches(ancestor))
                {
                    return true;
                }
            }

            return false;
        }

        private void Add(ICssStyleRule rule)
        {
            var kind = rule.Selector is { } selector
                ? s_selectorKinds.GetValue(selector, _ => Classify(rule.SelectorText, rule))
                : Classify(rule.SelectorText, rule);

            HasFirstFormatted |= kind.FirstFormatted;

            if (kind.Text is { } text)
            {
                Add(text);
            }

            if (kind.States is { } states)
            {
                foreach (var state in states)
                {
                    if (!_states.TryGetValue(state.PseudoClass, out var index))
                    {
                        _states[state.PseudoClass] = index = new StateCompoundIndex();
                    }

                    index.Add(state);
                }
            }
        }

        private static SelectorKind Classify(string? selector, ICssStyleRule rule)
        {
            if (string.IsNullOrEmpty(selector))
            {
                return new SelectorKind(false, null);
            }

            var isFirstFormatted = selector.Contains(":first-line", StringComparison.OrdinalIgnoreCase) || selector.Contains(":first-letter", StringComparison.OrdinalIgnoreCase);

            // a '+' in an argument (e.g. :nth-child(2n+1)) is taken for a combinator, which only costs a full layout
            var isRelevant = selector.Contains(":has(", StringComparison.OrdinalIgnoreCase) || selector.Contains('+') || selector.Contains('~');
            return new SelectorKind(isFirstFormatted, isRelevant ? selector : null, StateCompound.Find(selector, rule));
        }

        private void Add(string selector)
        {
            var hasRelational = selector.Contains(":has(", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>What a change of an element's state does to the styles (see <see cref="SiblingSelectors.GetStateEffect"/>).</summary>
    internal enum StateEffect
    {
        // no selector refers to the state for the element
        None,
        // only the style of the element itself may change
        Self,
        // the styles of the element and its descendants may change
        Confined,
        // the styles of other elements may change, too
        Unconfined,
    }

    /// <summary>
    /// The compounds of a pseudo-class of state by one of the keys an element needs to match them:
    /// an id, else a class, else a tag name, else an attribute; those of none are candidates for all.
    /// </summary>
    private sealed class StateCompoundIndex
    {
        private readonly Dictionary<string, List<StateCompound>> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<StateCompound>> _classes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<StateCompound>> _tags = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<StateCompound>> _attributes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<StateCompound> _others = [];

        public void Add(StateCompound compound)
        {
            var (map, key) = compound.GetKey();

            if (key is null)
            {
                _others.Add(compound);
                return;
            }

            var keyed = map switch { 0 => _ids, 1 => _classes, 2 => _tags, _ => _attributes };

            if (!keyed.TryGetValue(key, out var list))
            {
                keyed[key] = list = [];
            }

            list.Add(compound);
        }

        public IEnumerable<StateCompound> GetCandidates(IElement element)
        {
            if (element.Id is { Length: > 0 } id && _ids.TryGetValue(id, out var byId))
            {
                foreach (var compound in byId)
                {
                    yield return compound;
                }
            }

            if (_classes.Count > 0 && element.HasAttribute("class"))
            {
                var classes = element.ClassList;

                for (var i = 0; i < classes.Length; i++)
                {
                    if (classes[i] is { } name && _classes.TryGetValue(name, out var byClass))
                    {
                        foreach (var compound in byClass)
                        {
                            yield return compound;
                        }
                    }
                }
            }

            if (_tags.TryGetValue(element.LocalName, out var byTag))
            {
                foreach (var compound in byTag)
                {
                    yield return compound;
                }
            }

            if (_attributes.Count > 0)
            {
                foreach (var attribute in element.Attributes)
                {
                    if (_attributes.TryGetValue(attribute.Name, out var byAttribute))
                    {
                        foreach (var compound in byAttribute)
                        {
                            yield return compound;
                        }
                    }
                }
            }

            foreach (var compound in _others)
            {
                yield return compound;
            }
        }
    }

    /// <summary>
    /// A compound selector with a pseudo-class of an element's state, e.g. the <c>.card:hover</c> of
    /// <c>.card:hover .title</c>, by what an element needs to have to match it (any element may if it
    /// has none of these). It is confined if only the element and its descendants are styled by it.
    /// </summary>
    private sealed class StateCompound
    {
        // the pseudo-classes of state that change without the DOM changing
        private static readonly string[] s_pseudoClasses =
        [
            "hover", "active", "focus", "focus-visible", "focus-within", "checked", "indeterminate",
            "placeholder-shown", "valid", "invalid", "user-valid", "user-invalid", "autofill", "default",
        ];

        private static readonly string[] s_legacyPseudoElements = [":before", ":after", ":first-line", ":first-letter"];

        // the text of the compound, the selector up to it (if more), and that without the state (see Matches)
        private string? _text;
        private string? _matchText;
        private ISelector? _selector;
        private bool _isParsed;

        private StateCompound(string pseudoClass, bool isConfined)
        {
            PseudoClass = pseudoClass;
            IsConfined = isConfined;
        }

        public string PseudoClass { get; }

        public bool IsConfined { get; }

        /// <summary>Gets if the compound with the state is the subject of the selector (the element styled).</summary>
        public bool IsSubject { get; private set; }

        /// <summary>The rule of the selector, whose declarations tell what the descendants may inherit.</summary>
        private ICssStyleRule? Rule { get; set; }

        /// <summary>
        /// Gets if the descendants of a matching element may be styled differently with the state,
        /// whatever the element's own style: they are the subject, or they may inherit a custom
        /// property the rule declares (which no style map has). The pseudo-elements of the element,
        /// e.g. by <c>:hover::before</c>, are its children. What else they inherit is only looked
        /// at in the children when it changed (see TryRepaint), as far as a style map has it.
        /// </summary>
        public bool ReachesDescendants => !IsSubject || Rule is null || HasPseudoElement || Rule.Style.Any(static property =>
            property.Name.StartsWith("--", StringComparison.Ordinal) || property.Name is "all" ||
            (property.CanBeInherited && !LayoutSnapshot.IsComparedInChildren(property.Name)));

        public string Source { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the compound with the <c>:has()</c> the compound is in, if any, whose match is an
        /// ancestor of the element in the state (see Analyze).
        /// </summary>
        public StateCompound? Ancestor { get; private set; }

        // if the compound styles a pseudo-element of the element in the state
        private bool HasPseudoElement => _text is { } text &&
            (text.Contains("::", StringComparison.Ordinal) || s_legacyPseudoElements.Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase)));

        private string? Tag { get; set; }

        private List<string> Ids { get; } = [];

        private List<string> Classes { get; } = [];

        private List<string> Attributes { get; } = [];

        /// <summary>Gets one key an element needs to match (0: id, 1: class, 2: tag, 3: attribute), if any.</summary>
        public (int Map, string? Key) GetKey() =>
            Ids.Count > 0 ? (0, Ids[0]) : Classes.Count > 0 ? (1, Classes[0]) : Tag is not null ? (2, Tag) : Attributes.Count > 0 ? (3, Attributes[0]) : (0, null);

        public bool Matches(IElement element)
        {
            if (Tag is not null && !string.Equals(Tag, element.LocalName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var id in Ids)
            {
                if (!string.Equals(element.Id, id, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            foreach (var name in Classes)
            {
                if (!element.ClassList.Contains(name))
                {
                    return false;
                }
            }

            foreach (var name in Attributes)
            {
                if (!element.HasAttribute(name))
                {
                    return false;
                }
            }

            // the keys are only what every match has: what else the compound asks for, e.g. in
            // `:where(.link):hover`, is matched by the selector without the state
            if (!_isParsed)
            {
                _isParsed = true;
                _selector = ParseWithoutState(element);
            }

            return _selector is null || _selector.Match(element, element);
        }

        // the compound's selector without its pseudo-class of state (null if that isn't the same
        // to match in either state, or there is nothing else to match)
        private ISelector? ParseWithoutState(IElement element)
        {
            if ((_matchText ?? _text) is not { } text)
            {
                return null;
            }

            var rest = new StringBuilder();
            var depth = 0;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                if (c == '\\' && i + 1 < text.Length)
                {
                    rest.Append(c).Append(text[++i]);
                    continue;
                }

                if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    depth--;
                }
                else if (depth == 0 && c == ':' && (i == 0 || text[i - 1] != ':') && (i + 1 == text.Length || text[i + 1] != ':') &&
                    string.Compare(text, i + 1, PseudoClass, 0, PseudoClass.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (i + 1 + PseudoClass.Length == text.Length || !IsNameCharacter(text[i + 1 + PseudoClass.Length])))
                {
                    i += PseudoClass.Length;
                    continue;
                }

                rest.Append(c);
            }

            var selector = rest.ToString().Trim();

            // the state in an argument (e.g. `:not(:hover)`) matches differently, a pseudo-element
            // is no element to match
            if (selector.Length == 0 || selector.Contains(":" + PseudoClass, StringComparison.OrdinalIgnoreCase) ||
                selector.Contains("::", StringComparison.Ordinal) || s_legacyPseudoElements.Any(name => selector.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            try
            {
                return element.Owner?.Context.GetService<AngleSharp.Css.Parser.ICssSelectorParser>()?.ParseSelector(selector);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The compounds of the selector (list) with a pseudo-class of state, if any.</summary>
        public static List<StateCompound>? Find(string selector, ICssStyleRule rule)
        {
            if (selector.IndexOf(':') < 0)
            {
                return null;
            }

            List<StateCompound>? result = null;

            foreach (var pseudoClass in s_pseudoClasses)
            {
                if (selector.IndexOf(pseudoClass, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Analyze(selector, pseudoClass, '\0', null, isRelational: false, isOuterSubject: true, ref result);
                }
            }

            foreach (var compound in result ?? [])
            {
                compound.Source = selector;
                compound.Rule = rule;
            }

            return result;
        }

        /// <summary>
        /// Finds the compounds of the selector list with the pseudo-class. An element in that state
        /// only styles itself and its descendants, unless a sibling combinator follows the compound
        /// with it (the compound being the subject of an argument of <c>:is()</c>, the combinator after
        /// that), or it is in the argument of <c>:has()</c>, which styles an ancestor or a sibling.
        /// </summary>
        /// <param name="list">The selector list, e.g. the arguments of <c>:is()</c>.</param>
        /// <param name="pseudoClass">The pseudo-class.</param>
        /// <param name="following">The combinator following the compound around the list, if any.</param>
        /// <param name="around">The compound around the list, whose keys the subjects of the list have, too.</param>
        /// <param name="isRelational">If the list is in the argument of <c>:has()</c>.</param>
        /// <param name="result">The compounds found.</param>
        /// <param name="isOuterSubject">If the compound around the list is the subject of the selector.</param>
        private static void Analyze(string list, string pseudoClass, char following, string? around, bool isRelational, bool isOuterSubject, ref List<StateCompound>? result)
        {
            foreach (var complex in SplitTopLevel(list))
            {
                var compounds = SplitComplex(complex.Trim());

                for (var i = 0; i < compounds.Count; i++)
                {
                    var (text, combinator) = compounds[i];
                    var isSubject = i == compounds.Count - 1;
                    var next = isSubject ? following : combinator;

                    if (HasOwnPseudoClass(text, pseudoClass))
                    {
                        var compound = Create(pseudoClass, text, !isRelational && next is not '+' and not '~');
                        compound.IsSubject = isSubject && isOuterSubject;

                        // the element has to match what is before it, too (`.menu li:hover a`), unless
                        // that is relative to the element of a `:has()`
                        if (!isRelational && i > 0)
                        {
                            compound._matchText = string.Concat(compounds.Take(i).Select(part => part.Text + (part.Combinator == ' ' ? " " : $" {part.Combinator} "))) + text;
                        }

                        if (isSubject && around is not null)
                        {
                            compound.AddKeys(around);
                        }

                        (result ??= []).Add(compound);
                    }

                    foreach (var (name, arguments) in GetFunctions(text))
                    {
                        if (arguments.IndexOf(pseudoClass, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        if (name is "is" or "where" or "matches" or "-webkit-any")
                        {
                            Analyze(arguments, pseudoClass, next, isSubject && around is not null ? around + " " + text : text, isRelational, isSubject && isOuterSubject, ref result);
                        }
                        else if (name is "has")
                        {
                            var first = result?.Count ?? 0;
                            Analyze(arguments, pseudoClass, '\0', null, isRelational: true, isOuterSubject: false, ref result);

                            // an element in the state below the compound only styles one of its
                            // ancestors anew, unless the argument refers to a sibling (e.g. `:has(+ a)`,
                            // serialized as `:has(:scope+a)`)
                            if (result is not null && !SplitTopLevel(arguments).Any(argument => SplitComplex(argument.Trim()).Any(part => part.Combinator is '+' or '~')))
                            {
                                var outer = Create(pseudoClass, text, isConfined: true);

                                for (var j = first; j < result.Count; j++)
                                {
                                    result[j].Ancestor ??= outer;
                                }
                            }
                        }
                        else
                        {
                            // e.g. :not(:hover): the element itself, by the keys of the compound only
                            var compound = Create(pseudoClass, text, !isRelational && next is not '+' and not '~');
                            compound.IsSubject = isSubject && isOuterSubject;

                            if (isSubject && around is not null)
                            {
                                compound.AddKeys(around);
                            }

                            // an argument with combinators (or `:has()`) may refer to the state of another element
                            if (name is not "not" || arguments.Contains(":has(", StringComparison.OrdinalIgnoreCase) ||
                                SplitTopLevel(arguments).Any(argument => SplitComplex(argument.Trim()).Count > 1))
                            {
                                compound = new StateCompound(pseudoClass, isConfined: false);
                            }

                            (result ??= []).Add(compound);
                        }
                    }
                }
            }
        }

        // if the compound has the pseudo-class itself, not in an argument of another one
        private static bool HasOwnPseudoClass(string compound, string pseudoClass)
        {
            var depth = 0;

            for (var i = 0; i < compound.Length; i++)
            {
                var c = compound[i];

                if (c == '\\')
                {
                    i++;
                }
                else if (c is '(' or '[')
                {
                    depth++;
                }
                else if (c is ')' or ']')
                {
                    depth--;
                }
                else if (depth == 0 && c == ':' && (i == 0 || compound[i - 1] != ':') &&
                    string.Compare(compound, i + 1, pseudoClass, 0, pseudoClass.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                    (i + 1 + pseudoClass.Length == compound.Length || !IsNameCharacter(compound[i + 1 + pseudoClass.Length])))
                {
                    return true;
                }
            }

            return false;
        }

        // the functional pseudo-classes of the compound with their arguments, e.g. ("is", ".a, .b")
        private static IEnumerable<(string Name, string Arguments)> GetFunctions(string compound)
        {
            for (var i = 0; i < compound.Length; i++)
            {
                var c = compound[i];

                if (c == '\\')
                {
                    i++;
                }
                else if (c == '[')
                {
                    while (i + 1 < compound.Length && compound[i + 1] != ']')
                    {
                        i++;
                    }
                }
                else if (c == ':' && i + 1 < compound.Length && compound[i + 1] != ':')
                {
                    var start = i + 1;
                    var end = start;

                    while (end < compound.Length && IsNameCharacter(compound[end]) && compound[end] != '(')
                    {
                        end++;
                    }

                    if (end < compound.Length && compound[end] == '(')
                    {
                        var depth = 0;
                        var close = end;

                        for (; close < compound.Length; close++)
                        {
                            if (compound[close] == '(')
                            {
                                depth++;
                            }
                            else if (compound[close] == ')' && --depth == 0)
                            {
                                break;
                            }
                        }

                        yield return (compound[start..end].ToLowerInvariant(), compound[(end + 1)..Math.Min(close, compound.Length)]);
                        i = close;
                    }
                }
            }
        }

        private void AddKeys(string compound)
        {
            var keys = Create(PseudoClass, compound, IsConfined);
            Tag ??= keys.Tag;
            Ids.AddRange(keys.Ids);
            Classes.AddRange(keys.Classes);
            Attributes.AddRange(keys.Attributes);
        }

        // the selectors of a list (split at the commas outside of any parentheses, brackets or strings)
        private static List<string> SplitTopLevel(string selector)
        {
            var parts = new List<string>();
            var depth = 0;
            var quote = '\0';
            var start = 0;

            for (var i = 0; i < selector.Length; i++)
            {
                var c = selector[i];

                if (c == '\\')
                {
                    i++;
                }
                else if (quote != '\0')
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
                else if (c == ',' && depth == 0)
                {
                    parts.Add(selector[start..i]);
                    start = i + 1;
                }
            }

            parts.Add(selector[start..]);
            return parts;
        }

        // the compounds of a complex selector with the combinator following each ('\0' for the subject)
        private static List<(string Text, char Combinator)> SplitComplex(string complex)
        {
            var compounds = new List<(string, char)>();
            var current = new StringBuilder();
            var depth = 0;
            var quote = '\0';
            var combinator = '\0';

            for (var i = 0; i < complex.Length; i++)
            {
                var c = complex[i];

                if (depth == 0 && quote == '\0' && (char.IsWhiteSpace(c) || c is '>' or '+' or '~'))
                {
                    combinator = c is '>' or '+' or '~' ? c : combinator == '\0' ? ' ' : combinator;
                    continue;
                }

                if (combinator != '\0' && current.Length > 0)
                {
                    compounds.Add((current.ToString(), combinator));
                    current.Clear();
                }

                combinator = '\0';
                current.Append(c);

                if (c == '\\' && i + 1 < complex.Length)
                {
                    current.Append(complex[++i]);
                }
                else if (quote != '\0')
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
            }

            if (current.Length > 0)
            {
                compounds.Add((current.ToString(), '\0'));
            }

            return compounds;
        }

        private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or '(' || c > 127;

        // what an element needs to match the compound, from what is outside of any parentheses
        private static StateCompound Create(string pseudoClass, string compound, bool isConfined)
        {
            var result = new StateCompound(pseudoClass, isConfined) { _text = compound };
            var depth = 0;

            for (var i = 0; i < compound.Length; i++)
            {
                var c = compound[i];

                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (depth > 0)
                {
                    continue;
                }
                else if (c is '.' or '#' or '[')
                {
                    var name = ReadName(compound, i + 1, out var end);

                    // a name escaped by a code point is left out: an element without it might match
                    if (name is not null)
                    {
                        (c == '.' ? result.Classes : c == '#' ? result.Ids : result.Attributes).Add(name);
                    }

                    i = end - 1;

                    if (c == '[')
                    {
                        // skip to the end of the attribute selector
                        while (i + 1 < compound.Length && compound[i + 1] != ']')
                        {
                            i++;
                        }
                    }
                }
                else if (i == 0 && char.IsLetter(c))
                {
                    var name = ReadName(compound, 0, out var end);

                    if (name is not null && end < compound.Length && compound[end] == '|')
                    {
                        name = null;
                    }

                    result.Tag = name;
                    i = end - 1;
                }
                else if (c == ':')
                {
                    // the name of a pseudo-class, or a pseudo-element, is no key
                    while (i + 1 < compound.Length && (IsNameCharacter(compound[i + 1]) || compound[i + 1] == ':') && compound[i + 1] != '(')
                    {
                        i++;
                    }
                }
            }

            // what the one selector of `:is(a .link)` asks of its subject, the element has, too
            foreach (var (name, arguments) in GetFunctions(compound))
            {
                if (name is "is" or "where" or "matches" or "-webkit-any" && SplitTopLevel(arguments) is [var single] &&
                    SplitComplex(single.Trim()) is { Count: > 0 } parts)
                {
                    var keys = Create(pseudoClass, parts[^1].Text, isConfined);
                    result.Tag ??= keys.Tag;
                    result.Ids.AddRange(keys.Ids);
                    result.Classes.AddRange(keys.Classes);
                    result.Attributes.AddRange(keys.Attributes);
                }
            }

            return result;
        }

        private static string? ReadName(string text, int start, out int end)
        {
            var name = new StringBuilder();
            var isExact = true;
            end = start;

            while (end < text.Length)
            {
                var c = text[end];

                if (c == '\\' && end + 1 < text.Length)
                {
                    isExact &= !Uri.IsHexDigit(text[end + 1]);
                    name.Append(text[end + 1]);
                    end += 2;
                }
                else if (char.IsLetterOrDigit(c) || c is '-' or '_' || c > 127)
                {
                    name.Append(c);
                    end++;
                }
                else
                {
                    break;
                }
            }

            return isExact && name.Length > 0 ? name.ToString() : null;
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
