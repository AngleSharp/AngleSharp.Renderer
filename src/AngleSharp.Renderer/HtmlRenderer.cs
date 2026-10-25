namespace AngleSharp.Renderer;

using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Css.RenderTree;
using AngleSharp.Css.Values;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;
using AngleSharp.Renderer.Skia.Svg;
using AngleSharp.Text;

using SkiaSharp;

/// <summary>
/// Renders HTML documents into image output.
/// </summary>
public sealed partial class HtmlRenderer
{
    // Per-document cache keeps image payloads stable across repeated renders of the same DOM instance.
    private static readonly ConditionalWeakTable<IDocument, DocumentImageCache> s_imageCacheByDocument = new();
    // Inline <svg> markup has no URL to key a per-document cache on, so it is cached per element instead.
    private static readonly ConditionalWeakTable<IElement, InlineSvgCacheEntry> s_inlineSvgCacheByElement = new();

    /// <summary>
    /// A rasterized inline SVG with the fingerprint of everything it was drawn from (its markup and
    /// the page styles reaching it), so a changed attribute, class or page rule re-rasterizes it.
    /// </summary>
    private sealed record InlineSvgCacheEntry(string Fingerprint, CachedImageResource Resource);

    /// <summary>
    /// What one layout pass resolves from the page and needs again and again, while neither the
    /// DOM nor the styles change during a pass: how the page styles its inline SVGs (a cascade for
    /// every element of an SVG, which is laid out many times - trial layouts, intrinsic sizes, line
    /// breaking), and the rules of its `style` elements declaring a raw authored property.
    /// </summary>
    private sealed class LayoutPass
    {
        public Dictionary<IWindow, IStyleCollection> StyleCollections { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<IElement, (SvgPageStyle? PageStyle, string Fingerprint)> Styles { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<(IDocument Document, string Property), List<AuthoredRule>> AuthoredRules { get; } = new();

        /// <summary>The computed styles of the render tree's elements.</summary>
        public Dictionary<IElement, ICssStyleDeclaration> ComputedStyles { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A rule of a `style` element declaring a property (see ResolveRawAuthoredPropertyValue).</summary>
    private sealed record AuthoredRule(string[] Selectors, string Value);

    /// <summary>
    /// `::before`/`::after` pseudo-elements whose `content` is a single `url()` image, which makes
    /// them replaced elements (CSS Generated Content). Filled per render by <see cref="ProcessCounters"/>;
    /// pseudo-elements are rebuilt with every render tree, so stale entries die with them.
    /// </summary>
    private static readonly ConditionalWeakTable<IElement, string> s_generatedContentImages = new();
    private static readonly AsyncLocal<LayoutCapture?> s_layoutCapture = new();
    private static readonly AsyncLocal<LayoutPass?> s_layoutPass = new();
    private static readonly ITextMeasurer s_defaultTextMeasurer = new SkiaTextMeasurer();


    private readonly IRenderBackend _backend;
    private readonly ITextMeasurer _textMeasurer;

    private sealed class DocumentImageCache
    {
        public Dictionary<string, CachedImageResource?> Resources { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CachedImageResource(byte[] Bytes, string MimeType, int NaturalWidth, int NaturalHeight);

    internal readonly record struct ElementLayoutMetrics(
        float BorderBoxX,
        float BorderBoxY,
        float BorderBoxWidth,
        float BorderBoxHeight,
        float BorderLeft,
        float BorderRight,
        float BorderTop,
        float BorderBottom,
        float PaddingLeft,
        float PaddingRight,
        float PaddingTop,
        float PaddingBottom);

    private readonly record struct LayoutContext(
        int Width,
        int Height,
        RenderColor TextColor,
        float Padding,
        string FontFamily,
        float FontSize,
        float LineHeightMultiplier,
        float ParagraphSpacing,
        ITextMeasurer TextMeasurer,
        FontFaceSet Fonts,
        Dictionary<IElement, Dictionary<string, string>> StyleMapCache,
        IStyleCollection? StyleCollection,
        Dictionary<string, Stack<int>> Counters,
        Dictionary<IElement, TableStructure> TableStructures,
        Stack<List<StickyBox>> StickyScopes,
        Dictionary<(IElement Element, float Width), float> MeasuredHeights,
        Dictionary<IElement, WritingModeKind> BoxSystems,
        Dictionary<IElement, WritingModeKind> ContentSystems,
        Dictionary<IElement, ICssStyleDeclaration> SpecifiedStyles);

    private sealed class LayoutCapture
    {
        private readonly Dictionary<IElement, ElementLayoutMetrics> _metricsByElement = new(ReferenceEqualityComparer.Instance);

        /// <summary>The baseline of the last line of text laid out while this capture was active.</summary>
        public float? LastBaselineY { get; set; }

        /// <summary>The baseline of the first line of text laid out while this capture was active.</summary>
        public float? FirstBaselineY { get; set; }

        public void Record(IElement element, ElementLayoutMetrics metrics)
        {
            _metricsByElement[element] = metrics;
        }

        public IReadOnlyDictionary<IElement, ElementLayoutMetrics> Snapshot()
        {
            return _metricsByElement;
        }
    }

    internal static IReadOnlyDictionary<IElement, ElementLayoutMetrics> CaptureLayoutMetrics(IDocument document, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        var context = CreateLayoutContext(document, renderDevice, s_defaultTextMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        var capture = new LayoutCapture();
        var previous = s_layoutCapture.Value;
        s_layoutCapture.Value = capture;

        try
        {
            _ = BuildDisplayList(document, viewport, context, renderDevice, measureFullExtent: true);
            return capture.Snapshot();
        }
        finally
        {
            s_layoutCapture.Value = previous;
        }
    }

    private static void RecordLayoutMetrics(
        IElement element,
        float borderBoxX,
        float borderBoxY,
        float borderBoxWidth,
        float borderBoxHeight,
        float borderLeft,
        float borderRight,
        float borderTop,
        float borderBottom,
        float paddingLeft,
        float paddingRight,
        float paddingTop,
        float paddingBottom)
    {
        if (s_stickyProbe is { } probe && ReferenceEquals(probe.Element, element))
        {
            probe.Metrics = new ElementLayoutMetrics(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, borderLeft, borderRight, borderTop, borderBottom, paddingLeft, paddingRight, paddingTop, paddingBottom);
        }

        var capture = s_layoutCapture.Value;
        if (capture is null)
        {
            return;
        }

        capture.Record(element, new ElementLayoutMetrics(
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom));
    }

    /// <summary>
    /// Creates a new renderer with a default Skia backend.
    /// </summary>
    public HtmlRenderer()
        : this(new SkiaRenderBackend())
    {
    }

    /// <summary>
    /// Creates a new renderer with a specific backend. A backend that measures text itself is
    /// used for layout as well, so that layout and rasterization agree on advance widths.
    /// </summary>
    /// <param name="backend">The backend used for rasterization.</param>
    public HtmlRenderer(IRenderBackend backend)
        : this(backend, (backend as ITextMeasurer) ?? s_defaultTextMeasurer)
    {
    }

    /// <summary>
    /// Creates a new renderer with a specific backend and text measurer.
    /// </summary>
    /// <param name="backend">The backend used for rasterization.</param>
    /// <param name="textMeasurer">The measurer used to compute advance widths during layout.</param>
    public HtmlRenderer(IRenderBackend backend, ITextMeasurer textMeasurer)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(textMeasurer);
        _backend = backend;
        _textMeasurer = textMeasurer;
    }

    private static LayoutContext CreateLayoutContext(IDocument document, IRenderDevice renderDevice, ITextMeasurer textMeasurer)
    {
        var width = Math.Max(1, (int)Math.Round(Convert.ToDouble(renderDevice.ViewPortWidth)));
        var height = Math.Max(1, (int)Math.Round(Convert.ToDouble(renderDevice.ViewPortHeight)));
        var defaultFontSize = Math.Max(1f, (float)renderDevice.FontSize);
        var defaultFontFamily = "sans-serif";
        var defaultLineHeight = 1.35f;
        var defaultTextColor = RenderColor.Black;

        var rootElement = document.Body ?? document.DocumentElement;
        if (rootElement is not null)
        {
            var styleMap = CreateStyleMap(rootElement.ComputeCurrentStyle(), rootElement);
            defaultFontSize = ParseLength(styleMap, "font-size", defaultFontSize, defaultFontSize, allowAuto: false);
            defaultFontFamily = styleMap.TryGetValue("font-family", out var family) && !string.IsNullOrWhiteSpace(family)
                ? family.Trim('\'', '"', ' ')
                : defaultFontFamily;
            defaultLineHeight = ParseLineHeight(styleMap, defaultLineHeight);
            defaultTextColor = ParseColor(styleMap.TryGetValue("color", out var colorValue) ? colorValue : null, defaultTextColor);
        }

        // Built once per render and reused for every element (see GetExplicitDeclarations) rather
        // than rebuilt from scratch on every single ResolveExplicitPropertyValue call the way it
        // used to be - GetStyleCollection(window, device) constructs a brand new StyleCollection
        // object (walks every stylesheet, including the UA sheet) each time it is called, and
        // CreateStyleMap alone used to call it up to 8 times per element (grid-template-columns/
        // rows, column-gap/row-gap/gap, grid-column/row, background-image), confirmed via
        // AngleSharp.Renderer.Benchmarks to be the next-largest cost after per-element style-map
        // caching. Mirrors the exact per-call device-resolution fallback
        // ResolveExplicitPropertyValue itself used (window.Document.Context.GetService<IRenderDevice>()
        // ?? new DefaultRenderDevice()) so this produces the identical collection that logic would
        // have built on its own, just once instead of thousands of times.
        var explicitStyleWindow = document.DefaultView;
        var styleCollection = explicitStyleWindow is not null
            ? explicitStyleWindow.GetStyleCollection(explicitStyleWindow.Document.Context.GetService<IRenderDevice>() ?? new DefaultRenderDevice())
            : null;

        return new LayoutContext(
            Width: width,
            Height: height,
            TextColor: defaultTextColor,
            Padding: 0f,
            FontFamily: defaultFontFamily,
            FontSize: defaultFontSize,
            LineHeightMultiplier: defaultLineHeight,
            ParagraphSpacing: 0f,
            TextMeasurer: textMeasurer,
            Fonts: FontFaceLoader.Load(document),
            // Scoped to this one BuildDisplayList/RenderToPng/CaptureLayoutMetrics call only - a
            // fresh, empty cache every time CreateLayoutContext runs (never persisted across
            // calls) is what keeps this safe for interactive re-renders: a hover/transition/
            // animation-driven style change between two successive renders of the same document
            // must never be answered from a stale map computed on a previous call. See
            // GetOrCreateStyleMap's own remarks for why caching within a single call is safe.
            StyleMapCache: new Dictionary<IElement, Dictionary<string, string>>(ReferenceEqualityComparer.Instance),
            StyleCollection: styleCollection,
            // CSS counters (`counter-reset`/`counter-increment`/`counter-set`, read by `counter()`/
            // `counters()` in `content`) are scoped to a single render exactly like StyleMapCache
            // above - a fresh, empty stack set every call, never persisted across renders, so a
            // counter's value can never leak between two separate BuildDisplayList calls for the
            // same document (interactive re-renders included).
            Counters: new Dictionary<string, Stack<int>>(),
            // The cell grid of each table, built once per render (see GetTableStructure).
            TableStructures: new Dictionary<IElement, TableStructure>(ReferenceEqualityComparer.Instance),
            // One scope per container being laid out; see LayoutStickyAwareElement.
            StickyScopes: new Stack<List<StickyBox>>(),
            // Trial-layout heights per element and width, for this render only.
            MeasuredHeights: new Dictionary<(IElement Element, float Width), float>(),
            // The vertical writing-mode systems of boxes and their content (ResolveWritingModeSystems).
            BoxSystems: new Dictionary<IElement, WritingModeKind>(ReferenceEqualityComparer.Instance),
            ContentSystems: new Dictionary<IElement, WritingModeKind>(ReferenceEqualityComparer.Instance),
            // The cascaded declarations of the render tree's elements (see GetExplicitDeclarations).
            SpecifiedStyles: new Dictionary<IElement, ICssStyleDeclaration>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>
    /// Renders the given document to a PNG image.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <returns>The rendered PNG image.</returns>
    public RenderedImage RenderToPng(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var renderDevice = document.Context.GetService<IRenderDevice>();
        return RenderToPng(document, renderDevice!);
    }

    /// <summary>
    /// Renders the given document to a PNG image.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="renderDevice">The render device used for viewport and typography defaults.</param>
    /// <returns>The rendered PNG image.</returns>
    public RenderedImage RenderToPng(IDocument document, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        var context = CreateLayoutContext(document, renderDevice, _textMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        var displayList = BuildDisplayList(document, viewport, context, renderDevice, ResolveRootScrollOffsetY(document));

        return _backend.RenderToPng(displayList, viewport);
    }

    /// <summary>
    /// Builds a display list from the given document.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <returns>The generated display list.</returns>
    public DisplayList BuildDisplayList(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var renderDevice = document.Context.GetService<IRenderDevice>();
        return BuildDisplayList(document, renderDevice!);
    }

    /// <summary>
    /// Builds a display list from the given document.
    /// </summary>
    /// <param name="document">The source document.</param>
    /// <param name="renderDevice">The render device used for viewport and typography defaults.</param>
    /// <returns>The generated display list.</returns>
    public DisplayList BuildDisplayList(IDocument document, IRenderDevice renderDevice)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderDevice);

        var context = CreateLayoutContext(document, renderDevice, _textMeasurer);
        var viewport = new RenderViewport(context.Width, context.Height);
        return BuildDisplayList(document, viewport, context, renderDevice, ResolveRootScrollOffsetY(document));
    }

    /// <summary>
    /// Resolves the page's vertical scroll offset from the interactive DOM harness, if one has
    /// been created for the document's browsing context (via <c>IBrowsingContext.GetDomHarness()</c>
    /// - typically through <see cref="IDomHarness.PaintToPng"/> or direct use of the CSSOM-view
    /// scroll APIs). Returns 0 for the overwhelmingly common case of a document that was never
    /// wired up for interactive use, so rendering stays unaffected unless a caller has actually
    /// opted into scroll state existing at all. <see cref="CaptureLayoutMetrics"/> deliberately
    /// does not call this - <c>getBoundingClientRect</c>/<c>scrollHeight</c>/max-scroll
    /// calculations need the document's true, unscrolled layout to stay correct (and clamping a
    /// newly-set scroll position depends on exactly that), so metrics capture always lays out at
    /// scroll offset 0 regardless of whatever is currently scrolled into view for painting.
    /// </summary>
    private static float ResolveRootScrollOffsetY(IDocument document)
    {
        var scrollingElement = document.DocumentElement;

        if (scrollingElement is null || !document.Context.TryGetDomHarness(out var harness) || harness is null)
        {
            return 0f;
        }

        // double.MaxValue as the clamp ceiling means this only floors at 0, never re-clamps to an
        // upper bound - the stored value was already correctly clamped against the document's
        // true scrollable extent when it was set via the public scrollTop/scrollTo DOM APIs
        // (which measure with CaptureLayoutMetrics, unaffected by this method).
        return (float)harness.GetScrollTop(scrollingElement, double.MaxValue);
    }

    private static DisplayList BuildDisplayList(IDocument document, RenderViewport viewport, LayoutContext context, IRenderDevice renderDevice, float scrollOffsetY = 0f, bool measureFullExtent = false)
    {
        var previous = s_layoutPass.Value;
        s_layoutPass.Value = new LayoutPass();

        try
        {
            return BuildDisplayListCore(document, viewport, context, renderDevice, scrollOffsetY, measureFullExtent);
        }
        finally
        {
            s_layoutPass.Value = previous;
        }
    }

    private static DisplayList BuildDisplayListCore(IDocument document, RenderViewport viewport, LayoutContext context, IRenderDevice renderDevice, float scrollOffsetY, bool measureFullExtent)
    {
        var displayList = new DisplayList { Fonts = context.Fonts };
        displayList.FillRect(new RenderRect(0f, 0f, viewport.Width, viewport.Height), RenderColor.White);

        var window = document.DefaultView;
        if (window is null)
        {
            return displayList;
        }

        PrepareDocumentForRendering(document);

        var renderTree = window.Render(renderDevice);
        CollectSpecifiedStyles(renderTree, context);
        ResolveWritingModeSystems(renderTree, WritingModeKind.HorizontalTb, context);

        // CSS counters (`counter-reset`/`counter-increment`/`counter-set`, read back by
        // `counter()`/`counters()` in generated `content`) are resolved in a dedicated pre-pass
        // over the already-built render tree, entirely independent of the actual layout walk below
        // - plain recursive function calls give correct, automatic push/pop scoping for free (a
        // counter-reset's own new instance is naturally "popped" back to whatever the enclosing
        // scope had once this call returns), which the layout pass itself cannot offer cheaply
        // given how many early-return paths LayoutElement has. See ProcessCounters' own remarks.
        ProcessCounters(renderTree, context);

        var body = document.Body;
        var root = body is null ? renderTree : renderTree.Find(body) ?? renderTree;
        var rootElementNode = document.DocumentElement is { } documentElement ? renderTree.Find(documentElement) as ElementRenderNode : null;
        var canvasBackground = PropagateToViewport(rootElementNode, root as ElementRenderNode, context);

        var contentX = context.Padding;
        // A positive scroll offset moves the page's content up relative to the fixed viewport
        // surface - painted the same way as an unscrolled page, just starting from a Y position
        // that can be negative. Content scrolled above or below the surface's fixed pixel bounds
        // simply falls outside what a raster surface of that size can hold; no explicit clip is
        // needed to hide it; Skia only ever writes pixels that exist within the surface itself.
        var contentY = context.Padding - scrollOffsetY;
        var contentWidth = viewport.Width - (2f * context.Padding);

        if (contentWidth <= 0f)
        {
            return displayList;
        }

        var textStyle = new RenderTextStyle(context.FontSize, context.TextColor, context.FontFamily, context.LineHeightMultiplier, 400f, false, false, false, context.TextColor, global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Solid, TextAlign.Left, 0f, 0f, 0f, [], WhiteSpaceMode.Normal, WordBreakMode.Normal, OverflowWrapMode.Normal, TextOverflowMode.Clip, 0, TextDirection.Ltr);
        var cursorY = contentY;
        var previousBlockMarginBottom = 0f;
        var suppressNextBlockTopMargin = false;
        var floats = new FloatArea();
        var textIndentConsumed = false;

        // Normal painting stops laying out content once it has gone past the visible viewport -
        // there is no need to spend time measuring what will never be rasterized. Measuring the
        // page's true scrollable extent (for scrollTop/scrollHeight/max-scroll purposes) needs the
        // opposite: the full page, regardless of how tall it is relative to the viewport.
        var maxY = measureFullExtent ? float.MaxValue : viewport.Height - context.Padding;

        // A `position: sticky` page child is laid out right here, in plain document order, like
        // any other page child - its own cursorY/margin-collapse threading must stay in sequence
        // for its layout to be correct. But once actually stuck, its final on-screen position
        // routinely overlaps *later* siblings it would otherwise paint behind in that same document
        // order - a real, confirmed bug caught by rendering the sticky-header visual test and
        // seeing the header completely painted over by the content scrolling underneath it. Each
        // sticky child's own command range is recorded here (by index, since DisplayList is a flat
        // list) and relocated to the end, in original relative order, only after every page child
        // has been laid out - see the loop below.
        var negativeZIndexRanges = new List<(int StartIndex, int Count)>();
        context.StickyScopes.Push([]);

        var pageSystem = body is null ? WritingModeKind.HorizontalTb : GetContentSystem(body, context);

        if (IsVerticalMode(pageSystem))
        {
            // A vertical page: its content runs down the viewport and is placed by a transform.
            var pageStyle = textStyle with { WritingMode = pageSystem, FlowMode = pageSystem };
            var inlineSize = Math.Max(0f, viewport.Height - (2f * context.Padding));
            var flowStart = displayList.Commands.Count;
            LayoutOrthogonalFlowContent(OrderChildrenForPainting(root.Children, context).ToList(), pageStyle, inlineSize, context, displayList);
            displayList.InsertRange(flowStart, [new PushTransformCommand(ResolveOrthogonalFlowTransform(pageSystem, WritingModeKind.HorizontalTb, contentX, contentY, contentWidth, inlineSize))]);
            displayList.PopTransform();
            context.StickyScopes.Peek().Clear();
        }
        else
        {
            // The root element is laid out as an ordinary block (so are <body>'s margins,
            // padding, border and width); without one, the body's children fill the page.
            LayoutFlowChildren(
                rootElementNode is not null && GeneratesBoxes(rootElementNode) ? [rootElementNode] : OrderChildrenForPainting(root.Children, context).ToList(),
                contentX,
                contentY,
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
                // the root's containing block is the viewport (CSS 2.1, 10.1), so `height: 100%` on it fills the viewport
                containingHeight: Math.Max(0f, viewport.Height - (2f * context.Padding)),
                positionedContainingX: float.NaN,
                positionedContainingY: float.NaN,
                positionedContainingWidth: float.NaN,
                positionedContainingHeight: float.NaN,
                negativeZIndexRanges);
        }

        // Sticky page children stick within the page's content (at least the viewport tall).
        ResolveStickyBoxes(
            displayList,
            context.StickyScopes.Pop(),
            new RenderRect(contentX, contentY, contentWidth, Math.Max(cursorY, viewport.Height - context.Padding) - contentY),
            context);

        // The canvas background (CSS Backgrounds 3 §2.11.2) paints under everything, over the whole
        // canvas: the viewport, or the content where it extends further down.
        if (canvasBackground is { } propagated)
        {
            var canvasBuffer = new DisplayList();
            var canvasHeight = Math.Max(viewport.Height + scrollOffsetY, cursorY + scrollOffsetY);
            PaintBoxBackground(canvasBuffer, propagated.StyleMap, propagated.Box, 0f, -scrollOffsetY, viewport.Width, canvasHeight, default, default);
            displayList.InsertRange(1, canvasBuffer.Commands);
        }

        // The viewport is the document's scroll container, so the scrolling element (<html>)
        // reports the viewport as its client area; GetScrollExtents (ElementCssomViewExtensions)
        // measures how far its descendants extend past it. Recording its laid-out box instead
        // would make the client and scroll heights identical, so nothing would ever look
        // scrollable. Only a page without a laid-out root synthesizes <body>'s box the same way.
        // This is a no-op outside of CaptureLayoutMetrics.
        var clientHeight = Math.Max(0f, viewport.Height - (2f * context.Padding));

        if (body is not null && (rootElementNode is null || IsVerticalMode(pageSystem)))
        {
            RecordLayoutMetrics(body, contentX, contentY, contentWidth, clientHeight, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
        }

        if (document.DocumentElement is not null)
        {
            RecordLayoutMetrics(document.DocumentElement, contentX, contentY, contentWidth, clientHeight, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
        }

        return displayList;
    }

    /// <summary>
    /// What the root element and <c>&lt;body&gt;</c> hand to the viewport. Their background
    /// becomes the canvas background (CSS Backgrounds 3 §2.11.2): the root's, or the body's when
    /// the root has none, which the element it came from then no longer paints itself. Likewise
    /// their `overflow` applies to the viewport (CSS Overflow 3 §3.3), which already clips to its
    /// bounds and does not scroll by itself, so the boxes simply never clip.
    /// </summary>
    private static (Dictionary<string, string> StyleMap, BoxStyle Box)? PropagateToViewport(ElementRenderNode? rootNode, ElementRenderNode? bodyNode, LayoutContext context)
    {
        if (rootNode is null)
        {
            return null;
        }

        var rootStyle = GetOrCreateStyleMap(context, rootNode.Ref, rootNode.ComputedStyle);
        var bodyStyle = bodyNode is not null && !ReferenceEquals(bodyNode, rootNode) &&
                        string.Equals(bodyNode.Ref.LocalName, "body", StringComparison.OrdinalIgnoreCase)
            ? GetOrCreateStyleMap(context, bodyNode.Ref, bodyNode.ComputedStyle)
            : null;

        var rootOverflowVisible = IsOverflowVisible(rootStyle);
        RemoveOverflow(rootStyle);

        if (bodyStyle is not null && rootOverflowVisible)
        {
            RemoveOverflow(bodyStyle);
        }

        var rootBox = ResolveBoxStyle(rootStyle, rootNode.Ref);
        var (sourceStyle, sourceBox) = HasBackground(rootBox) || bodyStyle is null
            ? (rootStyle, rootBox)
            : (bodyStyle, ResolveBoxStyle(bodyStyle, bodyNode!.Ref));

        if (!HasBackground(sourceBox))
        {
            return null;
        }

        // The canvas keeps its own copy; the box it came from paints no background of its own.
        var canvasStyle = new Dictionary<string, string>(sourceStyle);
        sourceStyle.Remove("background-color");
        sourceStyle.Remove("background-image");

        return (canvasStyle, sourceBox);

        static bool HasBackground(BoxStyle box) => box.BackgroundColor.A > 0 || box.BackgroundLayers is { Count: > 0 };

        static bool IsOverflowVisible(Dictionary<string, string> styleMap) =>
            new[] { "overflow", "overflow-x", "overflow-y" }.All(key =>
                !styleMap.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) ||
                value.Trim().Equals("visible", StringComparison.OrdinalIgnoreCase));

        static void RemoveOverflow(Dictionary<string, string> styleMap)
        {
            styleMap.Remove("overflow");
            styleMap.Remove("overflow-x");
            styleMap.Remove("overflow-y");
        }
    }

    private static void LayoutNode(
        IRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        bool isFlexItem = false,
        bool isRowDirection = true,
        float? flexMainSize = null,
        float? flexCrossSize = null,
        float containingHeight = float.NaN,
        float positionedContainingX = float.NaN,
        float positionedContainingY = float.NaN,
        float positionedContainingWidth = float.NaN,
        float positionedContainingHeight = float.NaN,
        float forcedBorderBoxWidth = float.NaN,
        bool isAtomicInline = false,
        float forcedBorderBoxHeight = float.NaN,
        float contentOffsetY = 0f,
        bool mainAxisMarginsPlaced = false)
    {
        switch (node)
        {
            case TextRenderNode textNode:
                LayoutTextNode(textNode.Ref, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed, textStyle, context, displayList, maxY);
                return;
            case ElementRenderNode element:
                LayoutStickyAwareElement(element, containingX, containingY, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed, textStyle, context, displayList, maxY, isFlexItem, isRowDirection, flexMainSize, flexCrossSize, containingHeight, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight, forcedBorderBoxWidth, isAtomicInline, forcedBorderBoxHeight, contentOffsetY, mainAxisMarginsPlaced);
                return;
            default:
                return;
        }
    }

    private static void LayoutElement(
        ElementRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        bool isFlexItem = false,
        bool isRowDirection = true,
        float? flexMainSize = null,
        float? flexCrossSize = null,
        float containingHeight = float.NaN,
        float positionedContainingX = float.NaN,
        float positionedContainingY = float.NaN,
        float positionedContainingWidth = float.NaN,
        float positionedContainingHeight = float.NaN,
        float forcedBorderBoxWidth = float.NaN,
        bool isAtomicInline = false,
        float forcedBorderBoxHeight = float.NaN,
        float contentOffsetY = 0f,
        bool mainAxisMarginsPlaced = false)
    {
        var element = node.Ref;
        var computedStyle = node.ComputedStyle;
        var styleMap = GetOrCreateStyleMap(context, node.Ref, node.ComputedStyle);

        if (!GeneratesBoxes(node))
        {
            return;
        }

        var tagName = element.LocalName;

        if (string.Equals(tagName, "br", StringComparison.OrdinalIgnoreCase))
        {
            cursorY += inheritedTextStyle.FontSize * inheritedTextStyle.LineHeightMultiplier;
            return;
        }

        var formControlKind = ResolveFormControlKind(node);

        var display = GetDisplay(styleMap);

        if (string.Equals(display, "table", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(display, "inline-table", StringComparison.OrdinalIgnoreCase))
        {
            LayoutTable(node, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, inheritedTextStyle, context, displayList, maxY, positionedContainingX, positionedContainingY, positionedContainingWidth, positionedContainingHeight, forcedBorderBoxWidth);
            return;
        }

        // A ::before/::after pseudo-element shares its host's tagName (PseudoElement.LocalName/TagName
        // proxy through), so IsReplacedElement never judges a pseudo by tag - only an image in its
        // own `content` makes it replaced.
        var renderAsBlock = ShouldRenderAsBlock(computedStyle) || IsReplacedElement(element);
        var isInlineBlock = IsInlineBlock(computedStyle);
        var currentTextStyle = ResolveTextStyle(styleMap, inheritedTextStyle);

        if (formControlKind != FormControlKind.None)
        {
            ApplyFormControlDefaults(formControlKind, element, styleMap, currentTextStyle, context);
        }

        // Atomic inlines and flex items lay out their own contents as a block, whatever their
        // inline display value. Floats and absolutely positioned boxes arrive blockified in the
        // computed `display` (CSS Display 3 §2.7); flex items are not, as AngleSharp.Css does not
        // know about the formatting context they participate in.
        if (!renderAsBlock && (isInlineBlock || isAtomicInline || isFlexItem || IsAtomicInlineDisplay(computedStyle)))
        {
            renderAsBlock = true;
        }

        if (!renderAsBlock)
        {
            // An inline element reached on its own (for example as a grid item) still gets a
            // proper inline formatting context, so its own box and nested styles are kept.
            LayoutInlineFormattingContext(
                [node],
                containingX,
                containingWidth,
                ref cursorY,
                ref previousBlockMarginBottom,
                ref suppressNextBlockTopMargin,
                floats,
                ref textIndentConsumed,
                inheritedTextStyle,
                context,
                displayList,
                maxY,
                containingHeight,
                positionedContainingX,
                positionedContainingY,
                positionedContainingWidth,
                positionedContainingHeight,
                []);
            return;
        }

        // On a box, `vertical-align` only positions the box itself within its line; it must not
        // also shift the text inside it.
        currentTextStyle = currentTextStyle with { VerticalAlignOffset = 0f };

        var floatSide = GetFloat(styleMap);
        var isFloat = floatSide is "left" or "right";
        var isOutOfFlow = IsOutOfFlowPositioned(styleMap);
        // An orthogonal flow (see HtmlRenderer.WritingModes.cs) is always a formatting context of its own.
        var establishesContext = EstablishesBlockFormattingContext(styleMap, isFloat, isOutOfFlow, isAtomicInline, isFlexItem) ||
                                 IsVerticalMode(currentTextStyle.WritingMode) != IsVerticalMode(inheritedTextStyle.FlowMode);

        // `clear` moves a box below the floats on the side(s) it must not sit next to.
        var clearance = isOutOfFlow ? float.NaN : floats.GetClearance(GetClear(styleMap));

        if (!float.IsNaN(clearance) && clearance > cursorY)
        {
            cursorY = clearance;
            previousBlockMarginBottom = 0f;
        }

        // An ordinary block spans the floats beside it (only its lines avoid them); an in-flow box
        // with a formatting context of its own sits next to them as a whole.
        var flowContainingX = containingX;
        var flowContainingWidth = containingWidth;

        if (establishesContext && !isFloat && !isOutOfFlow && !isAtomicInline && !isFlexItem)
        {
            var (bandLeft, bandRight) = floats.GetBand(cursorY, currentTextStyle.FontSize * currentTextStyle.LineHeightMultiplier, containingX, containingX + containingWidth);
            flowContainingX = bandLeft;
            flowContainingWidth = Math.Max(0f, bandRight - bandLeft);
        }

        // A box whose width is already decided (a table cell, an atomic inline) is laid out even
        // when that width is zero: its height still counts.
        if (flowContainingWidth <= 0f && float.IsNaN(forcedBorderBoxWidth))
        {
            return;
        }

        var box = ResolveBoxStyle(styleMap, element);
        var marginTop = ParseLength(styleMap, "margin-top", flowContainingWidth, box.Margin.Top, allowAuto: false);
        var marginBottom = ParseLength(styleMap, "margin-bottom", flowContainingWidth, box.Margin.Bottom, allowAuto: false);
        var marginLeft = ParseLength(styleMap, "margin-left", flowContainingWidth, box.Margin.Left, allowAuto: true);
        var marginRight = ParseLength(styleMap, "margin-right", flowContainingWidth, box.Margin.Right, allowAuto: true);

        // The parent already took this box's top margin (and those collapsing through it) as its own.
        var topMarginAbsorbed = suppressNextBlockTopMargin;

        if (suppressNextBlockTopMargin)
        {
            marginTop = 0f;
            suppressNextBlockTopMargin = false;
        }

        var borderTop = box.BorderWidth.Top;
        var borderRight = box.BorderWidth.Right;
        var borderBottom = box.BorderWidth.Bottom;
        var borderLeft = box.BorderWidth.Left;

        var paddingTop = ParseLength(styleMap, "padding-top", flowContainingWidth, box.Padding.Top, allowAuto: false);
        var paddingRight = ParseLength(styleMap, "padding-right", flowContainingWidth, box.Padding.Right, allowAuto: false);
        var paddingBottom = ParseLength(styleMap, "padding-bottom", flowContainingWidth, box.Padding.Bottom, allowAuto: false);
        var paddingLeft = ParseLength(styleMap, "padding-left", flowContainingWidth, box.Padding.Left, allowAuto: false);

        var position = GetPosition(styleMap);
        var isAbsolute = string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase);
        var isFixed = string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase);
        var isRelative = string.Equals(position, "relative", StringComparison.OrdinalIgnoreCase);

        if (isAbsolute || isFixed)
        {
            marginTop = 0f;
            marginBottom = 0f;
            marginLeft = 0f;
            marginRight = 0f;
        }

        // a flex container places its items' border boxes after their main-axis margins itself
        if (mainAxisMarginsPlaced)
        {
            if (isRowDirection)
            {
                marginLeft = 0f;
                marginRight = 0f;
            }
            else
            {
                marginTop = 0f;
                marginBottom = 0f;
            }
        }

        // `auto` margins of atomic inlines and floats compute to zero: there is no free space on
        // a line to center them in.
        if (isAtomicInline || isFloat)
        {
            marginLeft = float.IsNaN(marginLeft) ? 0f : marginLeft;
            marginRight = float.IsNaN(marginRight) ? 0f : marginRight;
        }

        // A box with its own formatting context never collapses margins with its children.
        var collapseWithFirstChild = borderTop <= 0f && paddingTop <= 0f && !establishesContext;
        var effectiveMarginTop = marginTop;
        var collapsesWithFirstChild = false;

        if (collapseWithFirstChild &&
            TryGetFirstCollapsibleChildTopMargin(node, flowContainingWidth, context, out var firstChildTopMargin))
        {
            effectiveMarginTop = topMarginAbsorbed ? 0f : CollapseMargins(marginTop, firstChildTopMargin);
            collapsesWithFirstChild = true;
        }

        var specifiedContentWidth = ResolveFlexibleContentDimension(
            styleMap,
            flowContainingWidth,
            float.NaN,
            borderLeft + borderRight + paddingLeft + paddingRight,
            isFlexItem,
            isRowDirection,
            flexMainSize,
            flexCrossSize,
            propertyName: "width");

        if (float.IsNaN(specifiedContentWidth) && !isFlexItem)
        {
            // A definite height plus `aspect-ratio` overrides a block-level box's normal
            // "auto width fills the container" default - matching real browsers, and the same
            // used-width/used-height "transferred size" relationship `aspect-ratio` already
            // establishes in the other direction (see ResolveAspectRatioContentHeight, called
            // further down once `height` is auto instead). Only applies when `width` is genuinely
            // unauthored (not merely resolved to NaN through a flex/grid override, which already
            // has its own, more specific sizing story this must not second-guess).
            var heightReferenceForAspectRatio = containingHeight;
            var explicitContentHeight = ResolveAuthoredDimension(
                styleMap,
                "height",
                heightReferenceForAspectRatio,
                float.NaN,
                borderTop + borderBottom + paddingTop + paddingBottom);

            if (!float.IsNaN(explicitContentHeight))
            {
                specifiedContentWidth = ResolveAspectRatioContentWidth(
                    styleMap,
                    explicitContentHeight,
                    borderLeft + borderRight + paddingLeft + paddingRight,
                    borderTop + borderBottom + paddingTop + paddingBottom);
            }
        }

        var horizontalBorderAndPadding = borderLeft + borderRight + paddingLeft + paddingRight;

        if (!float.IsNaN(forcedBorderBoxWidth))
        {
            specifiedContentWidth = Math.Max(0f, forcedBorderBoxWidth - horizontalBorderAndPadding);
        }
        else if (float.IsNaN(specifiedContentWidth) && !isFlexItem)
        {
            if (IsReplacedElement(element) &&
                TryResolveReplacedContentWidth(node, styleMap, flowContainingWidth, out var replacedWidth))
            {
                // A replaced element is as wide as its image, not as wide as its container.
                specifiedContentWidth = replacedWidth;
            }
            else if (isFloat || isAbsolute || isFixed)
            {
                specifiedContentWidth = ResolveOutOfFlowAutoContentWidth(
                    node,
                    styleMap,
                    isFixed,
                    isAbsolute,
                    flowContainingWidth,
                    float.IsNaN(positionedContainingWidth) ? containingWidth : positionedContainingWidth,
                    horizontalBorderAndPadding,
                    marginLeft,
                    marginRight,
                    inheritedTextStyle,
                    context);
            }
            else if (element is not IPseudoElement && string.Equals(element.LocalName, "button", StringComparison.OrdinalIgnoreCase))
            {
                // a button is as wide as its content (fit-content), also when it is block-level (HTML 15.5.2)
                var fitContentWidth = ResolveShrinkToFitBorderBoxWidth(node, flowContainingWidth, inheritedTextStyle, context, out _, out _);
                specifiedContentWidth = Math.Max(0f, fitContentWidth - horizontalBorderAndPadding);
            }
        }

        var authoredMarginLeft = marginLeft;
        var authoredMarginRight = marginRight;

        ResolveHorizontalMetrics(
            flowContainingWidth,
            specifiedContentWidth,
            borderLeft,
            borderRight,
            paddingLeft,
            paddingRight,
            ref marginLeft,
            ref marginRight,
            out var contentWidth);

        if (!isFlexItem && float.IsNaN(forcedBorderBoxWidth))
        {
            var clampedContentWidth = ClampToMinMaxWidth(styleMap, contentWidth, flowContainingWidth, horizontalBorderAndPadding);

            if (Math.Abs(clampedContentWidth - contentWidth) > 0.001f)
            {
                // A clamped width behaves like an authored one, so `margin: auto` centers the box.
                marginLeft = authoredMarginLeft;
                marginRight = authoredMarginRight;
                ResolveHorizontalMetrics(
                    flowContainingWidth,
                    clampedContentWidth,
                    borderLeft,
                    borderRight,
                    paddingLeft,
                    paddingRight,
                    ref marginLeft,
                    ref marginRight,
                    out contentWidth);
            }
        }

        // Only an in-flow block's margins absorb the space its width leaves over; a float or an
        // atomic inline keeps its authored margins (auto already counts as zero for them).
        if (isFloat || isAtomicInline)
        {
            marginLeft = authoredMarginLeft;
            marginRight = authoredMarginRight;
        }

        var collapsedMarginTop = (isAbsolute || isFixed) ? 0f : CollapseMargins(previousBlockMarginBottom, effectiveMarginTop);

        var flowBorderBoxX = flowContainingX + marginLeft;
        var flowBorderBoxY = cursorY + collapsedMarginTop;

        if (isFloat)
        {
            // A float goes as high as it can at its side of the containing block, next to the
            // floats already there; its margins never collapse.
            var marginBoxWidth = marginLeft + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight + marginRight;
            var (floatX, floatY) = floats.Place(marginBoxWidth, cursorY, floatSide == "left", containingX, containingX + containingWidth);
            flowBorderBoxX = floatX + marginLeft;
            flowBorderBoxY = floatY + marginTop;
        }

        var leftOffset = ParseLength(styleMap, "left", flowContainingWidth, 0f, allowAuto: true);
        var topOffset = ParseLength(styleMap, "top", flowContainingWidth, 0f, allowAuto: true);
        var rightOffset = ParseLength(styleMap, "right", flowContainingWidth, float.NaN, allowAuto: true);
        var bottomOffset = ParseLength(styleMap, "bottom", float.IsNaN(positionedContainingHeight) ? flowContainingWidth : positionedContainingHeight, float.NaN, allowAuto: true);

        if (float.IsNaN(leftOffset))
        {
            leftOffset = 0f;
        }

        if (float.IsNaN(topOffset))
        {
            topOffset = 0f;
        }

        var absoluteContainingX = float.IsNaN(positionedContainingX) ? containingX : positionedContainingX;
        var absoluteContainingY = float.IsNaN(positionedContainingY) ? containingY : positionedContainingY;
        var absoluteContainingWidth = float.IsNaN(positionedContainingWidth) ? containingWidth : positionedContainingWidth;

        // with both offsets of an axis auto, an absolutely positioned box stays where it would be in flow
        var hasStaticX = isAbsolute && float.IsNaN(rightOffset) && float.IsNaN(ParseLength(styleMap, "left", flowContainingWidth, float.NaN, allowAuto: true));
        var hasStaticY = isAbsolute && float.IsNaN(bottomOffset) && float.IsNaN(ParseLength(styleMap, "top", flowContainingWidth, float.NaN, allowAuto: true));
        var borderBoxX = isFixed
            ? context.Padding + leftOffset
            : isAbsolute
                ? hasStaticX ? flowBorderBoxX : absoluteContainingX + leftOffset
                : flowBorderBoxX + (isRelative ? leftOffset : 0f);
        // `position: sticky` is laid out at its natural position; its container shifts it once
        // its own box is known (see ResolveStickyBoxes).
        var borderBoxY = isFixed
            ? context.Padding + topOffset
            : isAbsolute
                ? hasStaticY ? flowBorderBoxY : absoluteContainingY + topOffset
                : flowBorderBoxY + (isRelative ? topOffset : 0f);
        var contentX = borderBoxX + borderLeft + paddingLeft;
        var contentY = borderBoxY + borderTop + paddingTop;
        var verticalBorderAndPadding = borderTop + borderBottom + paddingTop + paddingBottom;
        var heightReference = containingHeight;
        var resolvedContentHeight = ResolveFlexibleContentDimension(
            styleMap,
            heightReference,
            float.NaN,
            verticalBorderAndPadding,
            isFlexItem,
            isRowDirection,
            flexMainSize,
            flexCrossSize,
            propertyName: "height");

        if (float.IsNaN(resolvedContentHeight))
        {
            resolvedContentHeight = ResolveAspectRatioContentHeight(
                styleMap,
                contentWidth,
                borderLeft + borderRight + paddingLeft + paddingRight,
                verticalBorderAndPadding);
        }

        // A table cell is stretched to the rows it spans.
        if (!float.IsNaN(forcedBorderBoxHeight))
        {
            resolvedContentHeight = Math.Max(0f, forcedBorderBoxHeight - verticalBorderAndPadding);
        }

        // A replaced element without an authored height is as tall as its (scaled) image.
        if (float.IsNaN(resolvedContentHeight) && IsReplacedElement(element) &&
            TryResolveReplacedElementImage(node, styleMap, flowContainingWidth, 0f, 0f, out _, out var replacedRect, out var replacedClip))
        {
            resolvedContentHeight = replacedClip?.Height ?? replacedRect.Height;
        }

        var minimumContentHeightForPositioning = ResolveAuthoredDimension(styleMap, "min-height", heightReference, 0f, verticalBorderAndPadding);
        var positionedContentHeight = float.IsNaN(resolvedContentHeight)
            ? minimumContentHeightForPositioning > 0f ? minimumContentHeightForPositioning : float.NaN
            : Math.Max(resolvedContentHeight, minimumContentHeightForPositioning);

        var positionedBorderBoxWidth = borderLeft + paddingLeft + contentWidth + paddingRight + borderRight;
        var positionedBorderBoxHeight = float.IsNaN(positionedContentHeight)
            ? float.NaN
            : borderTop + paddingTop + positionedContentHeight + paddingBottom + borderBottom;

        if (isAbsolute && !styleMap.ContainsKey("left") && !float.IsNaN(rightOffset))
        {
            borderBoxX = absoluteContainingX + absoluteContainingWidth - rightOffset - positionedBorderBoxWidth;
        }

        if (isAbsolute && !styleMap.ContainsKey("top") && !float.IsNaN(bottomOffset) && !float.IsNaN(positionedContainingHeight) && !float.IsNaN(positionedBorderBoxHeight))
        {
            borderBoxY = absoluteContainingY + positionedContainingHeight - bottomOffset - positionedBorderBoxHeight;
        }

        contentX = borderBoxX + borderLeft + paddingLeft;
        contentY = borderBoxY + borderTop + paddingTop;
        var childPositionedContainingX = borderBoxX + borderLeft;
        var childPositionedContainingY = borderBoxY + borderTop;
        var childPositionedContainingWidth = positionedBorderBoxWidth - borderLeft - borderRight;
        var childPositionedContainingHeight = float.IsNaN(positionedBorderBoxHeight)
            ? float.NaN
            : positionedBorderBoxHeight - borderTop - borderBottom;

        // The box's own background/border/shadow/outline must paint behind its children, but an
        // auto-sized box's height is only known after its children are laid out (and therefore
        // appended to the display list) - so their commands are built into a scratch buffer here
        // and spliced in before this index once the box's final size is known, rather than simply
        // appended (which would paint them on top of - and hide - the children).
        var boxPaintInsertIndex = displayList.Commands.Count;

        if (string.Equals(display, "list-item", StringComparison.OrdinalIgnoreCase))
        {
            currentTextStyle = PaintListItemMarker(currentTextStyle.IsHidden ? new DisplayList() : displayList, element, styleMap, currentTextStyle, context, borderBoxX, contentX, contentY);
        }

        if (formControlKind != FormControlKind.None)
        {
            // Children are suppressed or absent for every kind PaintFormControl actually paints
            // content for (TextLike/Select/Button/Checkbox/Radio/Color), so their box never grows
            // past its own specified height the way an ordinary auto-sized box can - the specified
            // height (falling back to a single line, for an author-supplied "auto") is therefore
            // also this box's final content height, safe to resolve here rather than waiting for
            // the auto-height computation later in this method.
            var formControlSpecifiedHeight = resolvedContentHeight;
            var formControlContentHeight = float.IsNaN(formControlSpecifiedHeight)
                ? currentTextStyle.FontSize * currentTextStyle.LineHeightMultiplier
                : formControlSpecifiedHeight;
            PaintFormControl(currentTextStyle.IsHidden ? new DisplayList() : displayList, element, formControlKind, currentTextStyle, ResolveAccentColor(styleMap), context, contentX, contentY, contentWidth, formControlContentHeight);
        }

        // A table cell's content is shifted within the cell by its `vertical-align`.
        var childCursorY = contentY + contentOffsetY;
        var childPreviousBlockMarginBottom = 0f;
        var childSuppressNextBlockTopMargin = collapsesWithFirstChild;
        var childFloats = establishesContext ? new FloatArea() : floats;
        var negativeZIndexRanges = new List<(int StartIndex, int Count)>();

        // An <svg> root's children are foreign-namespaced SVG elements (circle, text, title, ...),
        // not HTML flow content; it is rasterized as a single replaced element below, so its
        // subtree must never be walked as if it were normal inline/block content. A <select>'s
        // <option> children and a <button>'s label are painted directly by PaintFormControl above
        // instead (a <select> shows only its selected option, never every option stacked; a
        // <button>'s own label is measured up front to size the button, so it is painted the same
        // self-contained way rather than through normal child text flow) - <textarea> is
        // deliberately excluded from this list, since its child text node flowing normally through
        // the ordinary block child-layout path below is exactly what a browser's own <textarea>
        // content does, and needed no special-casing at all once the box itself got its default
        // border/padding/background chrome.
        // A ::before/::after pseudo-element's own tagName proxies through to its host (see the
        // IsReplacedElement/ResolveFormControlKind guards above) - an <svg>'s own generated-content
        // pseudo is not itself the foreign-namespaced SVG subtree, and must still get to paint its own
        // single synthetic text child below rather than being emptied out like the real <svg> root is.
        var orderedChildren = element is not IPseudoElement &&
                               (string.Equals(tagName, "svg", StringComparison.OrdinalIgnoreCase) ||
                               formControlKind is FormControlKind.Select or FormControlKind.Button ||
                               (formControlKind == FormControlKind.TextArea && ResolveChangedTextAreaValue(element) is not null))
            ? []
            : OrderChildrenForLayout(node.Children, context).ToList();
        // a control painting its own label (or selected option) has no children to lay out as items
        var layoutsItems = formControlKind is not (FormControlKind.Select or FormControlKind.Button);

        if (layoutsItems && IsFlexContainer(styleMap))
        {
            LayoutFlexContainer(
                node,
                contentX,
                contentY,
                contentWidth,
                ref cursorY,
                ref previousBlockMarginBottom,
                ref suppressNextBlockTopMargin,
                floats,
                ref textIndentConsumed,
                currentTextStyle,
                context,
                displayList,
                maxY,
                styleMap,
                borderLeft,
                borderTop,
                borderRight,
                borderBottom,
                paddingLeft,
                paddingRight,
                paddingTop,
                paddingBottom,
                box,
                flowBorderBoxX,
                flowBorderBoxY,
                borderBoxX,
                borderBoxY,
                // the height the box got from its own flex container (along either axis) or otherwise
                float.IsNaN(resolvedContentHeight) ? null : resolvedContentHeight,
                containingHeight);
            return;
        }

        if (layoutsItems && IsGridContainer(styleMap))
        {
            LayoutGridContainer(
                node,
                contentX,
                contentY,
                contentWidth,
                ref cursorY,
                ref previousBlockMarginBottom,
                ref suppressNextBlockTopMargin,
                floats,
                ref textIndentConsumed,
                currentTextStyle,
                context,
                displayList,
                maxY,
                styleMap,
                borderLeft,
                borderTop,
                borderRight,
                borderBottom,
                paddingLeft,
                paddingRight,
                paddingTop,
                paddingBottom,
                box,
                flowBorderBoxX,
                flowBorderBoxY,
                borderBoxX,
                borderBoxY);
            return;
        }

        var isOrthogonalFlow = IsVerticalMode(currentTextStyle.WritingMode) != IsVerticalMode(inheritedTextStyle.FlowMode) &&
                               formControlKind == FormControlKind.None &&
                               !IsReplacedElement(element);

        if (isOrthogonalFlow)
        {
            // An orthogonal flow (a vertical box in horizontal content or the reverse): its content
            // runs along this box's height and is placed by a transform; an auto width is the
            // content's block size.
            var flowStyle = currentTextStyle with { FlowMode = currentTextStyle.WritingMode };
            var inlineSize = ResolveOrthogonalInlineSize(node, styleMap, flowStyle, resolvedContentHeight, heightReference, verticalBorderAndPadding, context);
            var flowStart = displayList.Commands.Count;
            var blockSize = LayoutOrthogonalFlowContent(orderedChildren, flowStyle, inlineSize, context, displayList);

            if (float.IsNaN(specifiedContentWidth) && float.IsNaN(forcedBorderBoxWidth))
            {
                contentWidth = ClampToMinMaxWidth(styleMap, blockSize, flowContainingWidth, horizontalBorderAndPadding);
            }

            displayList.InsertRange(flowStart, [new PushTransformCommand(ResolveOrthogonalFlowTransform(currentTextStyle.WritingMode, inheritedTextStyle.FlowMode, contentX, contentY + contentOffsetY, contentWidth, inlineSize))]);
            displayList.PopTransform();
            resolvedContentHeight = inlineSize;
            childCursorY = contentY + inlineSize;

            // Sticky boxes in there were registered in the flow's own coordinates.
            context.StickyScopes.Peek().Clear();
        }
        else if (TryResolveColumnLayout(styleMap, contentWidth, currentTextStyle.FontSize, out var columnLayout))
        {
            var columnHeight = LayoutMultiColumnChildren(
                orderedChildren,
                columnLayout,
                styleMap,
                contentX,
                contentY,
                resolvedContentHeight,
                ref textIndentConsumed,
                currentTextStyle,
                context,
                displayList,
                childPositionedContainingX,
                childPositionedContainingY,
                childPositionedContainingWidth,
                childPositionedContainingHeight);
            childCursorY = contentY + columnHeight;
        }
        else
        {
            LayoutFlowChildren(
                orderedChildren,
                contentX,
                contentY + contentOffsetY,
                contentWidth,
                ref childCursorY,
                ref childPreviousBlockMarginBottom,
                ref childSuppressNextBlockTopMargin,
                childFloats,
                ref textIndentConsumed,
                currentTextStyle,
                context,
                displayList,
                maxY,
                resolvedContentHeight,
                childPositionedContainingX,
                childPositionedContainingY,
                childPositionedContainingWidth,
                childPositionedContainingHeight,
                negativeZIndexRanges);
        }

        // A box with its own formatting context grows to contain the floats inside it.
        if (establishesContext && !float.IsNaN(childFloats.Bottom))
        {
            childCursorY = Math.Max(childCursorY, childFloats.Bottom);
        }

        var autoContentHeight = Math.Max(0f, childCursorY - contentY);
        var specifiedContentHeight = resolvedContentHeight;
        var contentHeight = float.IsNaN(specifiedContentHeight) ? autoContentHeight : specifiedContentHeight;
        var minimumContentHeight = ResolveAuthoredDimension(styleMap, "min-height", heightReference, 0f, verticalBorderAndPadding);
        var maximumContentHeight = ResolveAuthoredDimension(styleMap, "max-height", heightReference, float.NaN, verticalBorderAndPadding);
        contentHeight = Math.Max(contentHeight, minimumContentHeight);
        if (!float.IsNaN(maximumContentHeight) && float.IsNaN(forcedBorderBoxHeight))
        {
            contentHeight = Math.Min(contentHeight, maximumContentHeight);
        }

        var borderBoxWidth = borderLeft + paddingLeft + contentWidth + paddingRight + borderRight;
        var borderBoxHeight = borderTop + paddingTop + contentHeight + paddingBottom + borderBottom;

        var canCollapseWithLastChild = borderBottom <= 0f &&
                                      paddingBottom <= 0f &&
                                      float.IsNaN(specifiedContentHeight);

        var effectiveMarginBottom = marginBottom;

        if (canCollapseWithLastChild)
        {
            effectiveMarginBottom = CollapseMargins(marginBottom, childPreviousBlockMarginBottom);
        }

        RecordLayoutMetrics(
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom);

        var clipsOverflow = ShouldClipOverflow(styleMap);

        // Percentage radii refer to the border box, which is only known now.
        box = box with { BorderRadius = ResolveBorderRadii(styleMap, borderBoxWidth, borderBoxHeight) };
        var boxPaintBuffer = new DisplayList();

        // A `visibility: hidden` box keeps its place and its scopes (which visible descendants
        // still paint in) but paints none of its own decorations.
        var decorationBuffer = currentTextStyle.IsHidden ? new DisplayList() : boxPaintBuffer;
        var effects = OpenElementEffectScopes(
            boxPaintBuffer,
            decorationBuffer,
            styleMap,
            element,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            new EdgeSizes(borderTop, borderRight, borderBottom, borderLeft),
            new EdgeSizes(paddingTop, paddingRight, paddingBottom, paddingLeft),
            box.BorderRadius,
            currentTextStyle.FontSize,
            BlendsInside(displayList, boxPaintInsertIndex));
        var createsStackingContext = HasExplicitZIndex(styleMap) || effects.CreatesStackingContext;

        PaintBoxBackground(decorationBuffer, styleMap, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, new EdgeSizes(borderTop, borderRight, borderBottom, borderLeft), new EdgeSizes(paddingTop, paddingRight, paddingBottom, paddingLeft));
        PaintBoxShadows(decorationBuffer, box.BoxShadows, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);
        PaintBoxBorder(decorationBuffer, styleMap, node.Ref, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        PaintOutline(decorationBuffer, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        if (clipsOverflow)
        {
            // Per spec, overflow clips at the padding edge - content can extend into the padding
            // but not past it - so the clip rect is the padding box, not the border box. Border
            // and outline are unaffected because they were already appended above, outside this
            // clip scope's push.
            var clipRect = new RenderRect(
                borderBoxX + borderLeft,
                borderBoxY + borderTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom));
            boxPaintBuffer.PushClip(clipRect, box.BorderRadius.ClampToBox(borderBoxWidth, borderBoxHeight));
        }

        var paintInsertionIndex = boxPaintInsertIndex;

        if (!createsStackingContext && negativeZIndexRanges.Count > 0)
        {
            paintInsertionIndex = negativeZIndexRanges.Max(range => range.StartIndex + range.Count);
        }

        displayList.InsertRange(paintInsertionIndex, boxPaintBuffer.Commands);
        ResolveStickyBoxes(displayList, context.StickyScopes.Peek(), new RenderRect(contentX, contentY, contentWidth, contentHeight), context);

        if (TryResolveReplacedElementImage(node, styleMap, flowContainingWidth, borderBoxX + borderLeft + paddingLeft, borderBoxY + borderTop + paddingTop, out var image, out var imageRect, out var objectFitClipRect))
        {
            if (objectFitClipRect is { } objectFitClip)
            {
                displayList.PushClip(objectFitClip, RenderCornerRadii.Zero);
            }

            (currentTextStyle.IsHidden ? new DisplayList() : displayList).DrawImage(imageRect, image!);

            if (objectFitClipRect is not null)
            {
                displayList.PopClip();
            }
        }

        if (clipsOverflow)
        {
            displayList.PopClip();
        }

        CloseElementEffectScopes(displayList, effects);

        if (isFloat)
        {
            floats.Add(
                flowBorderBoxX - marginLeft,
                flowBorderBoxY - marginTop,
                flowBorderBoxX + borderBoxWidth + marginRight,
                flowBorderBoxY + borderBoxHeight + marginBottom,
                isLeft: floatSide == "left");
            previousBlockMarginBottom = 0f;
            suppressNextBlockTopMargin = false;
            return;
        }

        if (isAbsolute || isFixed)
        {
            previousBlockMarginBottom = 0f;
            suppressNextBlockTopMargin = false;
            return;
        }

        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + context.ParagraphSpacing;
    }

    /// <summary>
    /// The whole-element effect scopes a box opened, outermost first: transform, blend mode,
    /// opacity, filter, clip-path, mask.
    /// </summary>
    private readonly record struct ElementEffectScopes(bool Transform, bool BlendMode, bool Isolation, bool Opacity, bool Filter, bool ClipPath, bool Mask, bool BackdropFilter)
    {
        public bool CreatesStackingContext => Transform || BlendMode || Isolation || Opacity || Filter || ClipPath || Mask || BackdropFilter;
    }

    /// <summary>
    /// Whether the commands from the given index on blend with what is behind them (`mix-blend-mode`).
    /// </summary>
    private static bool BlendsInside(DisplayList displayList, int startIndex)
    {
        var commands = displayList.Commands;

        for (var index = startIndex; index < commands.Count; index++)
        {
            if (commands[index] is PushBlendModeCommand { Mode: not RenderBlendMode.Normal })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens the scopes that apply to an element as a whole (background, border, outline and
    /// every descendant) into <paramref name="buffer"/>, which is spliced in before the box's own
    /// decorations, and paints its <c>backdrop-filter</c>. Every block-level container (blocks,
    /// flex, grid, tables) goes through here so they all honor the same effects;
    /// <see cref="CloseElementEffectScopes"/> closes them once the children are emitted.
    /// </summary>
    private static ElementEffectScopes OpenElementEffectScopes(
        DisplayList buffer,
        DisplayList decorationBuffer,
        Dictionary<string, string> styleMap,
        IElement element,
        float borderBoxX,
        float borderBoxY,
        float borderBoxWidth,
        float borderBoxHeight,
        EdgeSizes border,
        EdgeSizes padding,
        RenderCornerRadii radii,
        float fontSize,
        bool blendsInside = true)
    {
        var transform = ParseCssTransform(styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, fontSize);
        var filterFunctions = ParseCssFilter(styleMap);
        var opacity = ParseCssOpacity(styleMap);
        var clipPathShape = ParseClipPath(styleMap, borderBoxWidth, borderBoxHeight) is { } boxShape
            ? OffsetClipShape(boxShape, borderBoxX, borderBoxY)
            : null;
        var blendMode = ParseBlendMode(styleMap, "mix-blend-mode");
        var isIsolated = styleMap.TryGetValue("isolation", out var isolationValue) &&
                         string.Equals(isolationValue.Trim(), "isolate", StringComparison.OrdinalIgnoreCase);
        var maskLayers = ResolveMaskLayers(styleMap, element, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, border, padding, radii);
        var backdropFunctions = ParseCssFilter(styleMap, "backdrop-filter");
        var scopes = new ElementEffectScopes(
            Transform: !transform.IsIdentity,
            // isolation only bounds the blend modes inside, so without any it needs no group of its own
            // (which would also hide what is painted behind it from a backdrop-filter inside)
            BlendMode: blendMode != RenderBlendMode.Normal || (isIsolated && blendsInside),
            Isolation: isIsolated,
            Opacity: opacity < 1f,
            Filter: filterFunctions.Count > 0,
            ClipPath: clipPathShape is not null,
            Mask: maskLayers.Count > 0,
            BackdropFilter: backdropFunctions.Count > 0);

        // A transform applies to the whole element - background, border, outline, and every
        // descendant all paint under it - so it is the outermost scope, wrapping even the
        // background (unlike the overflow clip, which excludes the border/outline).
        if (scopes.Transform)
        {
            buffer.PushTransform(transform);
        }

        // `backdrop-filter` filters what is already painted behind the box, before the box's own
        // groups open (inside them there would be nothing to filter yet).
        if (scopes.BackdropFilter)
        {
            decorationBuffer.DrawBackdropFilter(
                new RenderRect(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight),
                radii.ClampToBox(borderBoxWidth, borderBoxHeight),
                backdropFunctions);
        }

        // `mix-blend-mode` blends the element's finished group (already faded by its opacity)
        // onto the backdrop, so it wraps the opacity scope; `isolation: isolate` alone opens a
        // normal group that bounds the blend modes inside it. `opacity` composites the already
        // filtered content, so it wraps `filter`, which works in the element's transformed space.
        if (scopes.BlendMode)
        {
            buffer.PushBlendMode(blendMode);
        }

        if (scopes.Opacity)
        {
            buffer.PushOpacity(opacity);
        }

        if (scopes.Filter)
        {
            buffer.PushFilter(filterFunctions);
        }

        // `clip-path` clips the filtered/composited result in the element's own transformed
        // space, and a mask is the innermost scope: the masked group is everything the element
        // paints.
        if (scopes.ClipPath)
        {
            buffer.PushClipShape(clipPathShape!);
        }

        if (scopes.Mask)
        {
            buffer.PushMask(maskLayers);
        }

        return scopes;
    }

    private static void CloseElementEffectScopes(DisplayList displayList, ElementEffectScopes scopes)
    {
        if (scopes.Mask)
        {
            displayList.PopMask();
        }

        if (scopes.ClipPath)
        {
            displayList.PopClip();
        }

        if (scopes.Filter)
        {
            displayList.PopFilter();
        }

        if (scopes.Opacity)
        {
            displayList.PopOpacity();
        }

        if (scopes.BlendMode)
        {
            displayList.PopBlendMode();
        }

        if (scopes.Transform)
        {
            displayList.PopTransform();
        }
    }

    private readonly record struct FlexItemLayoutInfo(
        IRenderNode Node,
        Dictionary<string, string> Style,
        float Order,
        float FlexGrow,
        float FlexShrink,
        float BaseMainSize,
        float CrossSize,
        string AlignSelf,
        float MinMainSize = 0f,
        float MaxMainSize = float.PositiveInfinity,
        float MainBorderAndPadding = 0f,
        bool HasDefiniteCrossSize = false,
        float MainMarginStart = 0f,
        float MainMarginEnd = 0f)
    {
        /// <summary>The flex base size clamped by the item's min and max main size.</summary>
        public float HypotheticalMainSize => Math.Max(MinMainSize, Math.Min(MaxMainSize, BaseMainSize));

        /// <summary>The margins along the main axis, where an auto margin (NaN) counts as zero.</summary>
        public float MainMargins => UsedMargin(MainMarginStart) + UsedMargin(MainMarginEnd);

        /// <summary>The number of auto margins along the main axis.</summary>
        public int AutoMainMargins => (float.IsNaN(MainMarginStart) ? 1 : 0) + (float.IsNaN(MainMarginEnd) ? 1 : 0);

        public static float UsedMargin(float margin) => float.IsNaN(margin) ? 0f : margin;
    }

    private readonly record struct GridPlacement(int LineIndex, int Span);

    /// <summary>
    /// A CSS Grid track's own sizing function, kept unresolved (unlike a plain pixel size) until
    /// <see cref="LayoutGridContainer"/>'s own two-pass sizing algorithm can run: <see cref="Auto"/>
    /// columns are sized from their items' intrinsic widths (<see cref="ResolveIntrinsicGridColumns"/>)
    /// and <see cref="Auto"/> rows from their items' measured heights, and only once every <see cref="Auto"/>/<see cref="Fixed"/> track is settled can the free space
    /// left over be distributed across <see cref="Fraction"/> (`fr`) tracks.
    /// </summary>
    private sealed class GridTrackSize
    {
        public GridTrackSizeKind Kind;

        /// <summary>The resolved pixel size for <see cref="GridTrackSizeKind.Fixed"/>, or the
        /// working/final grown size for <see cref="GridTrackSizeKind.Auto"/> and (once resolved)
        /// <see cref="GridTrackSizeKind.Fraction"/>.</summary>
        public float Pixels;

        /// <summary>The `fr` count, for <see cref="GridTrackSizeKind.Fraction"/> only.</summary>
        public float FractionValue;

        /// <summary>A `minmax()` floor, honored for both <see cref="GridTrackSizeKind.Auto"/> and
        /// <see cref="GridTrackSizeKind.Fraction"/> tracks.</summary>
        public float? MinPixels;

        /// <summary>A `minmax()`/`fit-content()` ceiling, honored for
        /// <see cref="GridTrackSizeKind.Auto"/> tracks only - a flexible track's own maximum is
        /// always itself (its `fr` share), matching spec.</summary>
        public float? MaxPixels;

        /// <summary>How an <see cref="GridTrackSizeKind.Auto"/> track's growth limit is sized.</summary>
        public GridTrackMaxSizing MaxSizing;

        /// <summary>A `max-content` minimum: the base size follows max-content instead of min-content
        /// contributions (only consulted without a <see cref="MinPixels"/> floor).</summary>
        public bool MinIsMaxContent;

        public static GridTrackSize Fixed(float pixels) => new() { Kind = GridTrackSizeKind.Fixed, Pixels = Math.Max(0f, pixels) };

        public static GridTrackSize Auto(float? min = null, float? max = null) => new()
        {
            Kind = GridTrackSizeKind.Auto,
            MinPixels = min,
            MaxPixels = max,
            MaxSizing = max is null ? GridTrackMaxSizing.Auto : GridTrackMaxSizing.Fixed,
        };

        public static GridTrackSize Fraction(float fr, float? min = null) => new() { Kind = GridTrackSizeKind.Fraction, FractionValue = Math.Max(0f, fr), MinPixels = min };
    }

    private enum GridTrackSizeKind
    {
        Fixed,
        Auto,
        Fraction,
    }

    /// <summary>
    /// The maximum of a content-sized track: `auto` (max-content, and the track stretches into
    /// leftover space), `min-content`, `max-content`, `fit-content(limit)` (max-content capped at
    /// <see cref="GridTrackSize.MaxPixels"/>) or a fixed length.
    /// </summary>
    private enum GridTrackMaxSizing
    {
        Auto,
        MinContent,
        MaxContent,
        FitContent,
        Fixed,
    }

    private static void LayoutFlexContainer(
        ElementRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        Dictionary<string, string> styleMap,
        float borderLeft,
        float borderTop,
        float borderRight,
        float borderBottom,
        float paddingLeft,
        float paddingRight,
        float paddingTop,
        float paddingBottom,
        BoxStyle box,
        float flowBorderBoxX,
        float flowBorderBoxY,
        float borderBoxX,
        float borderBoxY,
        float? usedContentHeight,
        float containingHeight)
    {
        // See the matching comment in LayoutElement: the container's own background/border must
        // paint behind its items, but its auto-sized height is only known after they are laid out
        // (and appended), so their paint commands are spliced in before this index instead.
        var boxPaintInsertIndex = displayList.Commands.Count;

        var flexDirection = GetFlexDirection(styleMap);
        var isRowDirection = !string.Equals(flexDirection, "column", StringComparison.OrdinalIgnoreCase) && !string.Equals(flexDirection, "column-reverse", StringComparison.OrdinalIgnoreCase);
        var isReverseDirection = string.Equals(flexDirection, "row-reverse", StringComparison.OrdinalIgnoreCase) || string.Equals(flexDirection, "column-reverse", StringComparison.OrdinalIgnoreCase);
        var justifyContent = GetJustifyContent(styleMap);
        var alignItems = GetAlignItems(styleMap);
        var flexWrap = GetFlexWrap(styleMap);
        var alignContent = GetAlignContent(styleMap);
        var hasExplicitGap = styleMap.ContainsKey("gap") || styleMap.ContainsKey("column-gap") || styleMap.ContainsKey("row-gap");
        var ignoresWhitespaceFlexItems = string.Equals(flexWrap, "wrap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(justifyContent, "space-around", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(justifyContent, "space-evenly", StringComparison.OrdinalIgnoreCase);
        // percentage heights of the items refer to the container's height, if it is definite (else they are auto)
        var definiteContentHeight = ResolveAuthoredDimension(styleMap, "height", containingHeight, float.NaN, borderTop + borderBottom + paddingTop + paddingBottom);
        var children = OrderChildrenForLayout(FlattenDisplayContents(node.Children, context), context).ToList();
        // absolutely positioned children do not take part in the flex layout
        var outOfFlowChildren = children
            .OfType<ElementRenderNode>()
            .Where(child => IsOutOfFlowPositioned(GetOrCreateStyleMap(context, child.Ref, child.ComputedStyle)))
            .ToList();
        var flexItems = children
            .Where(child => child is ElementRenderNode element && !outOfFlowChildren.Contains(element) ||
                            child is TextRenderNode { Ref.Data: var data } &&
                            !string.IsNullOrWhiteSpace(data))
            .Select(child => CreateFlexItemLayoutInfo(child, isRowDirection, containingWidth, definiteContentHeight, inheritedTextStyle, context))
            .OrderBy(item => item.Order)
            .ToList();

        var columnGapValue = ResolveColumnGap(styleMap, containingWidth) ?? 0f;
        var rowGapValue = ResolveRowGap(styleMap, ResolveGapHeightReference(styleMap, containingWidth, paddingTop + paddingBottom + borderTop + borderBottom)) ?? 0f;
        var mainGap = isRowDirection ? columnGapValue : rowGapValue;
        var crossGap = isRowDirection ? rowGapValue : columnGapValue;
        var horizontalBorderAndPadding = borderLeft + borderRight + paddingLeft + paddingRight;
        var verticalBorderAndPadding = borderTop + borderBottom + paddingTop + paddingBottom;

        var containerMainSize = isRowDirection
            ? containingWidth
            : ResolveAuthoredDimension(styleMap, "height", containingHeight, float.NaN, verticalBorderAndPadding);

        // a column that got its height from its own flex container (e.g. stretched) distributes it
        if (!isRowDirection && float.IsNaN(containerMainSize) && usedContentHeight.HasValue)
        {
            containerMainSize = usedContentHeight.Value;
        }

        if (float.IsNaN(containerMainSize))
        {
            containerMainSize = flexItems.Sum(item => item.HypotheticalMainSize + item.MainMargins) + Math.Max(0, flexItems.Count - 1) * mainGap;
        }
        else if (containerMainSize <= 0f)
        {
            containerMainSize = containingWidth;
        }


        var specifiedCrossSize = isRowDirection
            ? ResolveAuthoredDimension(styleMap, "height", containingHeight, float.NaN, verticalBorderAndPadding)
            : containingWidth;
        var minimumCrossSize = isRowDirection
            ? ResolveAuthoredDimension(styleMap, "min-height", containingHeight, 0f, verticalBorderAndPadding)
            : ResolveAuthoredDimension(styleMap, "min-width", containingWidth, 0f, horizontalBorderAndPadding);
        var containerCrossSize = float.IsNaN(specifiedCrossSize)
            ? Math.Max(minimumCrossSize, usedContentHeight ?? 0f)
            : Math.Max(specifiedCrossSize, minimumCrossSize);

        var contentWidth = containingWidth;
        var contentHeight = containerCrossSize;

        var lines = new List<List<FlexItemLayoutInfo>>();
        var currentLine = new List<FlexItemLayoutInfo>();
        var currentLineMainSize = 0f;

        foreach (var item in flexItems)
        {
            if (string.Equals(flexWrap, "wrap", StringComparison.OrdinalIgnoreCase) && currentLine.Count > 0 && currentLineMainSize + mainGap + item.HypotheticalMainSize + item.MainMargins > containerMainSize && containerMainSize > 0f)
            {
                lines.Add(currentLine);
                currentLine = new List<FlexItemLayoutInfo>();
                currentLineMainSize = 0f;
            }

            currentLine.Add(item);
            currentLineMainSize += item.HypotheticalMainSize + item.MainMargins;
        }

        if (currentLine.Count > 0)
        {
            lines.Add(currentLine);
        }

        if (string.Equals(flexWrap, "wrap", StringComparison.OrdinalIgnoreCase) && lines.Count > 1 && containerCrossSize > 0f)
        {
            var availableCrossSpace = Math.Max(0f, containerCrossSize - Math.Max(0, lines.Count - 1) * crossGap);
            var minimumLineCrossSize = availableCrossSpace / lines.Count;
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.Count > 0 && line.All(item => !item.HasDefiniteCrossSize))
                {
                    line = line.Select(item => item with { CrossSize = Math.Max(item.CrossSize, minimumLineCrossSize) }).ToList();
                    lines[lineIndex] = line;
                }
            }
        }

        // in a column, only items that are not stretched across a single line need their fit-content width
        float ColumnCrossAvailable(FlexItemLayoutInfo item) => !isRowDirection
            && (lines.Count > 1 || !string.Equals(string.Equals(item.AlignSelf, "auto", StringComparison.OrdinalIgnoreCase) ? alignItems : item.AlignSelf, "stretch", StringComparison.OrdinalIgnoreCase))
            ? containingWidth
            : float.NaN;

        // each estimate is a trial layout, needed for the line sizes and again for the item itself
        var crossSizeEstimates = new Dictionary<(IRenderNode Node, float MainSize), float>();

        float EstimateCrossSize(FlexItemLayoutInfo item, float mainSize)
        {
            if (!crossSizeEstimates.TryGetValue((item.Node, mainSize), out var estimate))
            {
                estimate = EstimateFlexItemCrossSize(item.Node, mainSize, isRowDirection, inheritedTextStyle, context, ColumnCrossAvailable(item));
                crossSizeEstimates[(item.Node, mainSize)] = estimate;
            }

            return estimate;
        }

        var lineCrossSizes = lines.Select(line => line.Count > 0
            ? line.Max(item => item.HasDefiniteCrossSize
                ? item.CrossSize
                : Math.Max(item.CrossSize, EstimateCrossSize(item, item.HypotheticalMainSize)))
            : 0f).ToList();
        var totalCrossSize = lineCrossSizes.Sum() + Math.Max(0, lines.Count - 1) * crossGap;
        var remainingCrossSize = Math.Max(0f, containerCrossSize - totalCrossSize);
        var crossSpacing = 0f;
        var currentCrossOffset = 0f;

        switch (alignContent)
        {
            case "center":
                currentCrossOffset = remainingCrossSize / 2f;
                break;
            case "flex-end":
                currentCrossOffset = remainingCrossSize;
                break;
            case "space-between":
                crossSpacing = lines.Count > 1 ? remainingCrossSize / Math.Max(1, lines.Count - 1) : 0f;
                break;
            case "space-around":
                crossSpacing = lines.Count > 0 ? remainingCrossSize / Math.Max(1, lines.Count) : 0f;
                currentCrossOffset = crossSpacing / 2f;
                break;
            case "space-evenly":
                crossSpacing = lines.Count > 0 ? remainingCrossSize / Math.Max(1, lines.Count + 1) : 0f;
                currentCrossOffset = crossSpacing;
                break;
            default:
                currentCrossOffset = 0f;
                break;
        }

        var childPreviousBlockMarginBottom = 0f;
        var totalLineMainSize = 0f;
        var paintOrder = new PaintOrder();
        var finalLineCrossSizes = new List<float>(lines.Count);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineGap = Math.Max(0, line.Count - 1) * mainGap;
            var lineMargins = line.Sum(item => item.MainMargins);
            var lineItems = new List<(FlexItemLayoutInfo Item, float FinalMainSize)>(line.Count);
            var finalMainSizes = ResolveFlexibleLengths(line, containerMainSize - lineGap - lineMargins);
            var lineMainSize = lineGap + lineMargins;

            for (var itemIndex = 0; itemIndex < line.Count; itemIndex++)
            {
                lineItems.Add((line[itemIndex], finalMainSizes[itemIndex]));
                lineMainSize += finalMainSizes[itemIndex];
            }

            var spacerCount = Math.Max(0, lineItems.Count - 1);
            var lineMainSpacing = 0f;
            var lineMainStart = 0f;
            // auto margins take the free space before justify-content gets it
            var autoMarginCount = line.Sum(item => item.AutoMainMargins);
            var autoMarginSize = autoMarginCount > 0 ? Math.Max(0f, containerMainSize - lineMainSize) / autoMarginCount : 0f;
            lineMainSize += autoMarginSize * autoMarginCount;

            switch (autoMarginCount > 0 ? "flex-start" : justifyContent)
            {
                case "center":
                    lineMainStart = Math.Max(0f, containerMainSize - lineMainSize) / 2f;
                    break;
                case "flex-end":
                    lineMainStart = Math.Max(0f, containerMainSize - lineMainSize);
                    break;
                case "space-between":
                    lineMainSpacing = lineItems.Count > 1 ? Math.Max(0f, containerMainSize - lineMainSize) / spacerCount : 0f;
                    break;
                case "space-around":
                    lineMainSpacing = lineItems.Count > 0 ? Math.Max(0f, containerMainSize - lineMainSize) / lineItems.Count : 0f;
                    lineMainStart = lineMainSpacing / 2f;
                    break;
                case "space-evenly":
                    lineMainSpacing = lineItems.Count > 0 ? Math.Max(0f, containerMainSize - lineMainSize) / (lineItems.Count + 1) : 0f;
                    lineMainStart = lineMainSpacing;
                    break;
                default:
                    lineMainStart = 0f;
                    break;
            }

            var lineCrossSize = lineItems.Count > 0
                ? lineItems.Max(entry => entry.Item.HasDefiniteCrossSize
                    ? entry.Item.CrossSize
                    : Math.Max(entry.Item.CrossSize, EstimateCrossSize(entry.Item, entry.FinalMainSize)))
                : 0f;
            finalLineCrossSizes.Add(lineCrossSize);
            var lineCrossPosition = currentCrossOffset;
            var lineCrossStart = 0f;

            if (string.Equals(alignItems, "center", StringComparison.OrdinalIgnoreCase))
            {
                lineCrossStart = containerCrossSize > 0f && lineCrossSize < containerCrossSize ? (containerCrossSize - lineCrossSize) / 2f : 0f;
            }
            else if (string.Equals(alignItems, "flex-end", StringComparison.OrdinalIgnoreCase))
            {
                lineCrossStart = containerCrossSize > 0f && lineCrossSize < containerCrossSize ? containerCrossSize - lineCrossSize : 0f;
            }
            else if (string.Equals(alignItems, "stretch", StringComparison.OrdinalIgnoreCase))
            {
                lineCrossStart = 0f;
            }

            var mainOffset = isReverseDirection ? containerMainSize - lineMainStart - lineMainSize : lineMainStart;
            var currentMainOffset = 0f;

            for (var itemIndex = 0; itemIndex < lineItems.Count; itemIndex++)
            {
                var (item, finalMainSize) = lineItems[itemIndex];
                var resolvedCrossSize = item.CrossSize;
                var estimatedCrossSize = item.HasDefiniteCrossSize
                    ? 0f
                    : EstimateCrossSize(item, finalMainSize);
                resolvedCrossSize = Math.Max(resolvedCrossSize, estimatedCrossSize);
                if (string.Equals(alignItems, "stretch", StringComparison.OrdinalIgnoreCase) && !item.HasDefiniteCrossSize && lineCrossSize > 0f)
                {
                    resolvedCrossSize = containerCrossSize > 0f ? containerCrossSize : lineCrossSize;
                }

                var itemCrossPosition = lineCrossPosition + lineCrossStart;
                var alignSelf = item.AlignSelf;
                var hasExplicitAlignSelf = !string.Equals(alignSelf, "auto", StringComparison.OrdinalIgnoreCase);
                var effectiveAlignment = string.Equals(alignSelf, "auto", StringComparison.OrdinalIgnoreCase)
                    ? alignItems
                    : alignSelf;

                if (hasExplicitAlignSelf && string.Equals(effectiveAlignment, "center", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = containerCrossSize > resolvedCrossSize ? (containerCrossSize - resolvedCrossSize) / 2f : 0f;
                }
                else if (hasExplicitAlignSelf && string.Equals(effectiveAlignment, "flex-end", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = Math.Max(0f, containerCrossSize - resolvedCrossSize);
                }
                else if (hasExplicitAlignSelf && string.Equals(effectiveAlignment, "flex-start", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = 0f;
                }
                else if (string.Equals(effectiveAlignment, "center", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = lineCrossPosition + Math.Max(0f, (lineCrossSize - resolvedCrossSize) / 2f) + lineCrossStart;
                }
                else if (string.Equals(effectiveAlignment, "flex-end", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = lineCrossPosition + Math.Max(0f, lineCrossSize - resolvedCrossSize) + lineCrossStart;
                }
                else if (string.Equals(effectiveAlignment, "stretch", StringComparison.OrdinalIgnoreCase) && resolvedCrossSize <= 0f && containerCrossSize > 0f)
                {
                    resolvedCrossSize = containerCrossSize;
                    itemCrossPosition = 0f;
                }
                else if (!string.Equals(effectiveAlignment, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    itemCrossPosition = lineCrossPosition + lineCrossStart;
                }

                var marginStart = float.IsNaN(item.MainMarginStart) ? autoMarginSize : item.MainMarginStart;
                var marginEnd = float.IsNaN(item.MainMarginEnd) ? autoMarginSize : item.MainMarginEnd;
                var itemOffset = (isReverseDirection
                    ? mainOffset + currentMainOffset
                    : lineMainStart + currentMainOffset) + marginStart;
                var childX = isRowDirection ? containingX + itemOffset : containingX + itemCrossPosition;
                var childY = isRowDirection ? containingY + itemCrossPosition : containingY + itemOffset;
                var childWidth = isRowDirection ? finalMainSize : resolvedCrossSize;
                var childHeight = isRowDirection ? resolvedCrossSize : finalMainSize;

                if (item.Node is TextRenderNode textNode)
                {
                    // laid out on its own in its anonymous flex item
                    var textCursorY = childY;
                    var textWidth = isRowDirection ? finalMainSize : resolvedCrossSize > 0f ? resolvedCrossSize : containingWidth;
                    LayoutWrappedText(PrepareFlexItemText(textNode, inheritedTextStyle, context), childX, textWidth, ref textCursorY, inheritedTextStyle, context, displayList, maxY, 0f);
                }
                else if (item.Node is ElementRenderNode elementChild)
                {
                    var childBox = ResolveBoxStyle(item.Style, elementChild.Ref);
                    var childHorizontalExtras = childBox.BorderWidth.Left + childBox.BorderWidth.Right + childBox.Padding.Left + childBox.Padding.Right;
                    var childVerticalExtras = childBox.BorderWidth.Top + childBox.BorderWidth.Bottom + childBox.Padding.Top + childBox.Padding.Bottom;
                    var childContentWidth = Math.Max(0f, childWidth - childHorizontalExtras);
                    var childContentHeight = Math.Max(0f, childHeight - childVerticalExtras);
                    var childCursor = isRowDirection ? containingY + itemCrossPosition : containingY + itemOffset;
                    var childBlockCursor = childCursor;
                    var childPreviousBottom = 0f;
                    var childSuppressMargin = false;
                    var childTextIndent = false;
                    var itemFloats = new FloatArea();
                    var itemRangeStart = displayList.Commands.Count;

                    LayoutNode(
                        node: elementChild,
                        containingX: childX,
                        containingY: childY,
                        containingWidth: childContentWidth,
                        cursorY: ref childBlockCursor,
                        previousBlockMarginBottom: ref childPreviousBottom,
                        suppressNextBlockTopMargin: ref childSuppressMargin,
                        floats: itemFloats,
                        textIndentConsumed: ref childTextIndent,
                        textStyle: inheritedTextStyle,
                        context: context,
                        displayList: displayList,
                        maxY: maxY,
                        isFlexItem: true,
                        isRowDirection: isRowDirection,
                        flexMainSize: isRowDirection ? childContentWidth : childContentHeight,
                        flexCrossSize: isRowDirection ? childContentHeight : childContentWidth,
                        mainAxisMarginsPlaced: true);
                    paintOrder.Track(elementChild, displayList, itemRangeStart, context);
                }

                currentMainOffset += marginStart + finalMainSize + marginEnd + lineMainSpacing + (itemIndex + 1 < lineItems.Count ? mainGap : 0f);
            }

            currentCrossOffset += lineCrossSize + crossSpacing + (lineIndex + 1 < lines.Count ? crossGap : 0f);
            totalLineMainSize = Math.Max(totalLineMainSize, lineMainSize);
        }

        var autoContentHeight = isRowDirection
            ? finalLineCrossSizes.Sum() + Math.Max(0, lines.Count - 1) * crossGap
            : Math.Max(0f, containerMainSize);
        var specifiedContentHeight = ResolveAuthoredDimension(styleMap, "height", containingHeight, float.NaN, verticalBorderAndPadding);
        var minimumContentHeight = ResolveAuthoredDimension(styleMap, "min-height", containingHeight, 0f, verticalBorderAndPadding);
        var maximumContentHeight = ResolveAuthoredDimension(styleMap, "max-height", containingHeight, float.NaN, verticalBorderAndPadding);
        // only a row's cross size is a height; a column's is its width
        contentHeight = float.IsNaN(specifiedContentHeight)
            ? isRowDirection ? Math.Max(autoContentHeight, containerCrossSize) : autoContentHeight
            : specifiedContentHeight;
        contentHeight = Math.Max(contentHeight, minimumContentHeight);
        if (!float.IsNaN(maximumContentHeight))
        {
            contentHeight = Math.Min(contentHeight, maximumContentHeight);
        }

        LayoutOutOfFlowChildren(contentHeight);
        paintOrder.Apply(displayList);
        var borderBoxWidth = borderLeft + paddingLeft + containingWidth + paddingRight + borderRight;
        var borderBoxHeight = borderTop + paddingTop + contentHeight + paddingBottom + borderBottom;
        var canCollapseWithLastChild = borderBottom <= 0f && paddingBottom <= 0f && float.IsNaN(specifiedContentHeight);
        var effectiveMarginBottom = ParseLength(styleMap, "margin-bottom", containingWidth, box.Margin.Bottom, allowAuto: false);

        if (canCollapseWithLastChild)
        {
            effectiveMarginBottom = CollapseMargins(effectiveMarginBottom, childPreviousBlockMarginBottom);
        }

        // Percentage radii refer to the border box, which is only known now.
        box = box with { BorderRadius = ResolveBorderRadii(styleMap, borderBoxWidth, borderBoxHeight) };
        var boxPaintBuffer = new DisplayList();
        var decorationBuffer = inheritedTextStyle.IsHidden ? new DisplayList() : boxPaintBuffer;
        var effects = OpenElementEffectScopes(
            boxPaintBuffer,
            decorationBuffer,
            styleMap,
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            new EdgeSizes(borderTop, borderRight, borderBottom, borderLeft),
            new EdgeSizes(paddingTop, paddingRight, paddingBottom, paddingLeft),
            box.BorderRadius,
            inheritedTextStyle.FontSize,
            BlendsInside(displayList, boxPaintInsertIndex));

        if (box.BackgroundPaint is RenderColorPaint colorPaint && colorPaint.Color.A == 0)
        {
            decorationBuffer.FillRect(new RenderRect(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight), RenderColor.Transparent);
        }
        else
        {
            PaintBoxBackground(decorationBuffer, styleMap, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, new EdgeSizes(borderTop, borderRight, borderBottom, borderLeft), new EdgeSizes(paddingTop, paddingRight, paddingBottom, paddingLeft));
        }

        PaintBoxShadows(decorationBuffer, box.BoxShadows, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);

        RecordLayoutMetrics(
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom);

        PaintBoxBorder(decorationBuffer, styleMap, node.Ref, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        PaintOutline(decorationBuffer, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        var clipsOverflow = ShouldClipOverflow(styleMap);

        if (clipsOverflow)
        {
            var clipRect = new RenderRect(
                borderBoxX + borderLeft,
                borderBoxY + borderTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom));
            boxPaintBuffer.PushClip(clipRect, box.BorderRadius.ClampToBox(borderBoxWidth, borderBoxHeight));
        }

        displayList.InsertRange(boxPaintInsertIndex, boxPaintBuffer.Commands);
        ResolveStickyBoxes(
            displayList,
            context.StickyScopes.Peek(),
            new RenderRect(
                borderBoxX + borderLeft + paddingLeft,
                borderBoxY + borderTop + paddingTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight - paddingLeft - paddingRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom - paddingTop - paddingBottom)),
            context);

        if (TryResolveReplacedElementImage(node, styleMap, containingWidth, borderBoxX + borderLeft + paddingLeft, borderBoxY + borderTop + paddingTop, out var image, out var imageRect, out var objectFitClipRect))
        {
            if (objectFitClipRect is { } objectFitClip)
            {
                displayList.PushClip(objectFitClip, RenderCornerRadii.Zero);
            }

            (inheritedTextStyle.IsHidden ? new DisplayList() : displayList).DrawImage(imageRect, image!);

            if (objectFitClipRect is not null)
            {
                displayList.PopClip();
            }
        }

        if (clipsOverflow)
        {
            displayList.PopClip();
        }

        CloseElementEffectScopes(displayList, effects);
        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + context.ParagraphSpacing;
        suppressNextBlockTopMargin = false;

        // laid out against the container's padding box, from where they would be as its only item
        void LayoutOutOfFlowChildren(float usedContentHeight)
        {
            var contentY = borderBoxY + borderTop + paddingTop;

            foreach (var child in outOfFlowChildren)
            {
                var childCursorY = contentY;

                if (isRowDirection && alignItems is "center" or "flex-end")
                {
                    var freeSpace = Math.Max(0f, usedContentHeight - MeasureBorderBoxHeight(child, containingWidth, inheritedTextStyle, context));
                    childCursorY += alignItems == "center" ? freeSpace / 2f : freeSpace;
                }

                var childPreviousBottom = 0f;
                var childSuppressMargin = false;
                var childTextIndent = false;
                var childRangeStart = displayList.Commands.Count;

                LayoutNode(
                    child,
                    containingX,
                    contentY,
                    containingWidth,
                    ref childCursorY,
                    ref childPreviousBottom,
                    ref childSuppressMargin,
                    new FloatArea(),
                    ref childTextIndent,
                    inheritedTextStyle,
                    context,
                    displayList,
                    maxY,
                    positionedContainingX: borderBoxX + borderLeft,
                    positionedContainingY: borderBoxY + borderTop,
                    positionedContainingWidth: paddingLeft + containingWidth + paddingRight,
                    positionedContainingHeight: paddingTop + usedContentHeight + paddingBottom);
                paintOrder.Track(child, displayList, childRangeStart, context);
            }
        }
    }

    private static float ResolveFlexibleContentDimension(
        Dictionary<string, string> styleMap,
        float relativeTo,
        float defaultValue,
        float borderAndPaddingSum,
        bool isFlexItem,
        bool isRowDirection,
        float? flexMainSize,
        float? flexCrossSize,
        string propertyName)
    {
        if (!isFlexItem)
        {
            return ResolveAuthoredDimension(styleMap, propertyName, relativeTo, defaultValue, borderAndPaddingSum);
        }

        // flexMainSize/flexCrossSize already arrive as content-box sizes - LayoutFlexContainer's
        // own algorithm resolves them from ResolveFlexBaseSize/ResolveFlexCrossSize, which already
        // apply this same box-sizing conversion using this item's own border/padding before the
        // flex-grow/shrink distribution ever runs - so they must not be adjusted a second time
        // here. Only the "no container override" fallback (this property wasn't driven by the flex
        // algorithm at all) re-reads the raw authored value and needs the adjustment applied fresh.
        if (string.Equals(propertyName, "width", StringComparison.OrdinalIgnoreCase))
        {
            return isRowDirection
                ? (flexMainSize.HasValue ? flexMainSize.Value : ResolveAuthoredDimension(styleMap, propertyName, relativeTo, defaultValue, borderAndPaddingSum))
                : (flexCrossSize.HasValue ? flexCrossSize.Value : ResolveAuthoredDimension(styleMap, propertyName, relativeTo, defaultValue, borderAndPaddingSum));
        }

        return isRowDirection
            ? (flexCrossSize.HasValue ? flexCrossSize.Value : ResolveAuthoredDimension(styleMap, propertyName, relativeTo, defaultValue, borderAndPaddingSum))
            : (flexMainSize.HasValue ? flexMainSize.Value : ResolveAuthoredDimension(styleMap, propertyName, relativeTo, defaultValue, borderAndPaddingSum));
    }

    private static void LayoutGridContainer(
        ElementRenderNode node,
        float containingX,
        float containingY,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        Dictionary<string, string> styleMap,
        float borderLeft,
        float borderTop,
        float borderRight,
        float borderBottom,
        float paddingLeft,
        float paddingRight,
        float paddingTop,
        float paddingBottom,
        BoxStyle box,
        float flowBorderBoxX,
        float flowBorderBoxY,
        float borderBoxX,
        float borderBoxY)
    {
        // See the matching comment in LayoutElement: the container's own background/border must
        // paint behind its items, but its auto-sized height is only known after they are laid out
        // (and appended), so their paint commands are spliced in before this index instead.
        var boxPaintInsertIndex = displayList.Commands.Count;

        var columnGap = ResolveColumnGap(styleMap, containingWidth);
        var rowGap = ResolveRowGap(styleMap, ResolveGapHeightReference(styleMap, containingWidth, paddingTop + paddingBottom + borderTop + borderBottom));
        var resolvedColumnGap = columnGap ?? 0f;
        var resolvedRowGap = rowGap ?? 0f;
        var gridItems = FlattenDisplayContents(node.Children, context)
            .Where(child => child is ElementRenderNode || (child is TextRenderNode textNode && NormalizeWhitespace(textNode.Ref.Data).Length > 0))
            .ToList();

        var explicitGridTemplateColumns = styleMap.GetValueOrDefault("grid-template-columns") ?? string.Empty;
        var columnLineNames = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var rowLineNames = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var columns = ParseGridTrackListStructured(explicitGridTemplateColumns, containingWidth, resolvedColumnGap, gridItems.Count, columnLineNames);

        if (columns.Count == 0)
        {
            columns.Add(GridTrackSize.Fixed(containingWidth));
        }

        var gridVerticalBorderAndPadding = borderTop + borderBottom + paddingTop + paddingBottom;
        var containerHeight = ResolveDefiniteContentHeight(styleMap, containingWidth, gridVerticalBorderAndPadding);
        var explicitGridTemplateRows = styleMap.GetValueOrDefault("grid-template-rows") ?? string.Empty;
        var rows = ParseGridTrackListStructured(explicitGridTemplateRows, containerHeight, lineNames: rowLineNames);
        var hasExplicitRowTracks = rows.Count > 0;
        var explicitRowCount = rows.Count;
        var autoRowDefinitions = styleMap.TryGetValue("grid-auto-rows", out var autoRowsValue)
            ? ParseGridTrackListStructured(autoRowsValue, containerHeight)
            : [];

        if (!hasExplicitRowTracks)
        {
            // Implicit rows default to content-sized (Auto), the same way a real browser sizes
            // them - grown below from each item's own estimated height, exactly like an Auto
            // column. The row count itself is still only a starting guess (one row per
            // ceil(itemCount / columnCount)); ResolveGridItemPlacement grows this list further for
            // any item that lands past it (an explicit grid-row/an oversized span).
            var rowCount = Math.Max(1, (int)Math.Ceiling((double)gridItems.Count / Math.Max(1, columns.Count)));

            for (var i = 0; i < rowCount; i++)
            {
                rows.Add(autoRowDefinitions.Count > 0 ? CloneGridTrackSize(autoRowDefinitions[i % autoRowDefinitions.Count]) : GridTrackSize.Auto());
            }
        }

        // Pass 1: resolve every item's placement once (reused unchanged in pass 2 below, so a
        // wrapping auto-placement cursor can never land an item in a different cell the second
        // time around), collect the intrinsic width contributions of items in Auto columns and
        // grow Auto rows to their items' authored heights (content heights are measured below).
        var itemPlacements = new List<(ElementRenderNode Element, GridPlacement Column, GridPlacement Row)>();
        var columnContributions = new List<(GridPlacement Column, IntrinsicWidths Widths)>();
        var namedAreas = ParseGridTemplateAreas(styleMap.TryGetValue("grid-template-areas", out var areasValue) ? areasValue : null);

        // Every area also names the lines around it: `name-start` and `name-end`.
        foreach (var (areaName, namedArea) in namedAreas)
        {
            AddImplicitGridLine(columnLineNames, areaName + "-start", namedArea.Column);
            AddImplicitGridLine(columnLineNames, areaName + "-end", namedArea.Column + namedArea.ColumnSpan);
            AddImplicitGridLine(rowLineNames, areaName + "-start", namedArea.Row);
            AddImplicitGridLine(rowLineNames, areaName + "-end", namedArea.Row + namedArea.RowSpan);
        }

        var columnLines = new GridLines(columnLineNames, columns.Count + 1);
        var rowLines = new GridLines(rowLineNames, hasExplicitRowTracks ? explicitRowCount + 1 : 1);

        static void AddImplicitGridLine(Dictionary<string, List<int>> names, string name, int line)
        {
            if (!names.TryGetValue(name, out var indices))
            {
                names[name] = indices = [];
            }

            if (!indices.Contains(line))
            {
                indices.Add(line);
                indices.Sort();
            }
        }
        var occupiedCells = new HashSet<(int Column, int Row)>();
        var currentColumn = 0;
        var currentRow = 0;

        bool CanPlace(int column, int row, int columnSpan, int rowSpan) =>
            Enumerable.Range(column, columnSpan).All(c => Enumerable.Range(row, rowSpan).All(r => !occupiedCells.Contains((c, r))));

        void MarkPlaced(GridPlacement column, GridPlacement row)
        {
            for (var c = column.LineIndex; c < column.LineIndex + column.Span; c++)
            {
                for (var r = row.LineIndex; r < row.LineIndex + row.Span; r++)
                {
                    occupiedCells.Add((c, r));
                }
            }
        }

        var placementItems = gridItems;
        var reservedRowPlacements = new Dictionary<ElementRenderNode, (GridPlacement Column, GridPlacement Row)>();

        foreach (var rowItem in placementItems.OfType<ElementRenderNode>())
        {
            var rowStyle = GetOrCreateStyleMap(context, rowItem.Ref, rowItem.ComputedStyle);
            var hasRowPlacement = HasGridPlacement(rowStyle, "grid-row", rowLines);
            var hasColumnPlacement = HasGridPlacement(rowStyle, "grid-column", columnLines);
            if (hasRowPlacement && !hasColumnPlacement)
            {
                var rowPlacement = ResolveGridPlacementFromMap(rowStyle, "grid-row", 0, rowLines);
                var column = 0;
                while (!CanPlace(column, rowPlacement.LineIndex, 1, rowPlacement.Span))
                {
                    column++;
                }

                var reserved = (Column: new GridPlacement(column, 1), Row: rowPlacement);
                reservedRowPlacements[rowItem] = reserved;
                MarkPlaced(reserved.Column, reserved.Row);
            }
        }

        foreach (var child in placementItems)
        {
            if (child is not ElementRenderNode elementChild)
            {
                continue;
            }

            var childStyleMap = GetOrCreateStyleMap(context, elementChild.Ref, elementChild.ComputedStyle);
            if (childStyleMap.TryGetValue("grid-area", out var areaName) && namedAreas.TryGetValue(areaName.Trim().Trim('"', '\''), out var area))
            {
                itemPlacements.Add((elementChild, new GridPlacement(area.Column, area.ColumnSpan), new GridPlacement(area.Row, area.RowSpan)));
                while (columns.Count < area.Column + area.ColumnSpan)
                {
                    columns.Add(GridTrackSize.Auto());
                }

                while (rows.Count < area.Row + area.RowSpan)
                {
                    rows.Add(GridTrackSize.Auto());
                }

                var areaColumn = new GridPlacement(area.Column, area.ColumnSpan);
                var areaRow = new GridPlacement(area.Row, area.RowSpan);
                MarkPlaced(areaColumn, areaRow);

                // sized like any other item: content-sized columns fit it, rows its authored height
                if (Enumerable.Range(areaColumn.LineIndex, areaColumn.Span).Any(i => columns[i].Kind == GridTrackSizeKind.Auto))
                {
                    columnContributions.Add((areaColumn, ComputeOuterIntrinsicWidths(elementChild, inheritedTextStyle, containingWidth, context)));
                }

                GrowGridTrackSpan(rows, areaRow, ResolveGridItemAuthoredHeight(childStyleMap, containingWidth), resolvedRowGap);

                currentColumn = 0;
                currentRow = 0;
                continue;
            }
            var placementColumn = ResolveGridPlacementFromMap(childStyleMap, "grid-column", currentColumn, columnLines);
            var placementRow = ResolveGridPlacementFromMap(childStyleMap, "grid-row", currentRow, rowLines);
            var hasExplicitColumn = HasGridPlacement(childStyleMap, "grid-column", columnLines);
            var hasExplicitRow = HasGridPlacement(childStyleMap, "grid-row", rowLines);
            var effectivePlacementColumn = new GridPlacement(Math.Max(0, hasExplicitColumn ? placementColumn.LineIndex : currentColumn), placementColumn.Span);
            var effectivePlacementRow = new GridPlacement(Math.Max(0, hasExplicitRow ? placementRow.LineIndex : currentRow), placementRow.Span);

            if (!reservedRowPlacements.ContainsKey(elementChild) && (!hasExplicitColumn || !hasExplicitRow))
            {
                var isDense = styleMap.TryGetValue("grid-auto-flow", out var autoFlowValue) && autoFlowValue.Contains("dense", StringComparison.OrdinalIgnoreCase);
                var searchRow = hasExplicitRow ? effectivePlacementRow.LineIndex : isDense ? 0 : currentRow;
                var searchColumn = hasExplicitColumn ? effectivePlacementColumn.LineIndex : 0;

                while (!CanPlace(searchColumn, searchRow, effectivePlacementColumn.Span, effectivePlacementRow.Span))
                {
                    if (hasExplicitColumn)
                    {
                        searchRow++;
                    }
                    else
                    {
                        searchColumn++;
                        if (searchColumn + effectivePlacementColumn.Span > columns.Count)
                        {
                            searchColumn = 0;
                            searchRow++;
                        }
                    }
                }

                effectivePlacementColumn = new GridPlacement(searchColumn, effectivePlacementColumn.Span);
                effectivePlacementRow = new GridPlacement(searchRow, effectivePlacementRow.Span);
            }

            if (hasExplicitRow && !hasExplicitColumn)
            {
                effectivePlacementColumn = new GridPlacement(0, effectivePlacementColumn.Span);
            }

            if (hasExplicitColumn && !hasExplicitRow)
            {
                foreach (var rowOnlyItem in placementItems.OfType<ElementRenderNode>())
                {
                    var rowOnlyStyle = GetOrCreateStyleMap(context, rowOnlyItem.Ref, rowOnlyItem.ComputedStyle);
                    var columnRaw = rowOnlyStyle.GetValueOrDefault("grid-column") ?? string.Empty;
                    if (!HasGridPlacement(rowOnlyStyle, "grid-row", rowLines) || !string.IsNullOrWhiteSpace(columnRaw) || HasGridPlacement(rowOnlyStyle, "grid-area"))
                    {
                        continue;
                    }

                    var rowOnlyPlacement = ResolveGridPlacementFromMap(rowOnlyStyle, "grid-row", 0, rowLines);
                    var columnsOverlap = effectivePlacementColumn.LineIndex < 1 + effectivePlacementColumn.Span &&
                        0 < effectivePlacementColumn.LineIndex + effectivePlacementColumn.Span;
                    if (columnsOverlap)
                    {
                        effectivePlacementRow = new GridPlacement(Math.Max(effectivePlacementRow.LineIndex, rowOnlyPlacement.LineIndex + rowOnlyPlacement.Span), effectivePlacementRow.Span);
                    }
                }
            }

            itemPlacements.Add((elementChild, effectivePlacementColumn, effectivePlacementRow));
            MarkPlaced(effectivePlacementColumn, effectivePlacementRow);

            if (hasExplicitColumn && !hasExplicitRow)
            {
                currentRow = Math.Max(currentRow, effectivePlacementRow.LineIndex);
                currentColumn = effectivePlacementColumn.LineIndex + effectivePlacementColumn.Span;
                if (currentColumn >= columns.Count)
                {
                    currentColumn = 0;
                    currentRow++;
                }
            }

            var authoredItemHeight = ResolveGridItemAuthoredHeight(childStyleMap, containingWidth);
            var effectiveColumnCount = Math.Max(columns.Count, effectivePlacementColumn.LineIndex + effectivePlacementColumn.Span);
            var effectiveRowCount = Math.Max(rows.Count, effectivePlacementRow.LineIndex + effectivePlacementRow.Span);

            while (columns.Count < effectiveColumnCount)
            {
                columns.Add(GridTrackSize.Auto());
            }

            while (rows.Count < effectiveRowCount)
            {
                rows.Add(autoRowDefinitions.Count > 0 ? CloneGridTrackSize(autoRowDefinitions[rows.Count % autoRowDefinitions.Count]) : GridTrackSize.Auto());
            }

            if (Enumerable.Range(effectivePlacementColumn.LineIndex, effectivePlacementColumn.Span).Any(i => i >= 0 && columns[i].Kind == GridTrackSizeKind.Auto))
            {
                columnContributions.Add((effectivePlacementColumn, ComputeOuterIntrinsicWidths(elementChild, inheritedTextStyle, containingWidth, context)));
            }

            GrowGridTrackSpan(rows, effectivePlacementRow, authoredItemHeight, resolvedRowGap);

            currentColumn++;
            if (currentColumn >= columns.Count)
            {
                currentColumn = 0;
                currentRow++;
            }
        }

        // Between passes: distribute each axis's remaining free space across its own `fr` tracks.
        // Row `fr` tracks only grow when the container has a definite height to distribute - `fr`
        // rows have no real meaning against an otherwise auto-sized container (there is no "free
        // space" to speak of), a deliberate, documented scope cut rather than an attempt at the
        // spec's own intrinsic-sizing fallback for that case.
        var justifyContent = styleMap.GetValueOrDefault("justify-content")?.Trim().ToLowerInvariant();
        ResolveIntrinsicGridColumns(columns, columnContributions, resolvedColumnGap, containingWidth, stretchAutoTracks: justifyContent is null or "" or "normal" or "stretch");
        ResolveGridFractionTracks(columns, resolvedColumnGap, containingWidth);

        var resolvedColumnSizes = columns.Select(track => track.Pixels).ToList();
        var fractionRowContent = new Dictionary<int, float>();

        foreach (var (element, placementColumn, placementRow) in itemPlacements)
        {
            if (placementRow.LineIndex >= rows.Count || rows[placementRow.LineIndex].Kind == GridTrackSizeKind.Fixed)
            {
                continue;
            }

            var isFractionRow = rows[placementRow.LineIndex].Kind == GridTrackSizeKind.Fraction;

            if (isFractionRow && (placementRow.Span != 1 || !float.IsNaN(containerHeight)))
            {
                continue;
            }

            var itemStyle = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);
            if (!isFractionRow && !float.IsNaN(ParseLength(itemStyle, "height", containingWidth, float.NaN, allowAuto: true)))
            {
                continue;
            }

            var cellWidth = GetGridTrackSpanSize(
                resolvedColumnSizes,
                placementColumn.LineIndex,
                placementColumn.Span,
                resolvedColumnGap,
                containingWidth);
            var measuredHeight = MeasureGridItemHeight(element, cellWidth, inheritedTextStyle, context, maxY);

            if (isFractionRow)
            {
                fractionRowContent[placementRow.LineIndex] = Math.Max(fractionRowContent.GetValueOrDefault(placementRow.LineIndex), measuredHeight);
            }
            else
            {
                GrowGridTrackSpan(rows, placementRow, measuredHeight, resolvedRowGap);
            }
        }

        if (!float.IsNaN(containerHeight))
        {
            ResolveGridFractionTracks(rows, resolvedRowGap, containerHeight);

            // auto rows fill a definite height like auto columns do (align-content: normal)
            if (styleMap.GetValueOrDefault("align-content")?.Trim().ToLowerInvariant() is null or "" or "normal" or "stretch")
            {
                StretchAutoGridRows(rows, resolvedRowGap, containerHeight);
            }
        }
        else
        {
            ResolveIndefiniteGridFractionTracks(rows, fractionRowContent);
        }

        var columnSizes = columns.Select(t => t.Pixels).ToList();
        var rowSizes = rows.Select(t => t.Pixels).ToList();

        // Pass 2: lay out every item for real, against the now-fully-resolved track sizes.
        foreach (var child in gridItems)
        {
            if (child is TextRenderNode textNode)
            {
                LayoutTextNode(textNode.Ref, containingX, containingWidth, ref cursorY, ref previousBlockMarginBottom, ref suppressNextBlockTopMargin, floats, ref textIndentConsumed, inheritedTextStyle, context, displayList, maxY);
                continue;
            }

            if (child is not ElementRenderNode elementChild)
            {
                continue;
            }

            var (_, effectivePlacementColumn, effectivePlacementRow) = itemPlacements.First(p => ReferenceEquals(p.Element, elementChild));

            var contentX = borderBoxX + borderLeft + paddingLeft;
            var contentY = borderBoxY + borderTop + paddingTop;
            var cellX = contentX + GetGridTrackOffset(columnSizes, effectivePlacementColumn.LineIndex, resolvedColumnGap);
            var cellY = contentY + GetGridTrackOffset(rowSizes, effectivePlacementRow.LineIndex, resolvedRowGap);
            var cellWidth = GetGridTrackSpanSize(columnSizes, effectivePlacementColumn.LineIndex, effectivePlacementColumn.Span, resolvedColumnGap, containingWidth);
            var cellHeight = GetGridTrackSpanSize(rowSizes, effectivePlacementRow.LineIndex, effectivePlacementRow.Span, resolvedRowGap, containingWidth);
            var childStyleMap = GetOrCreateStyleMap(context, elementChild.Ref, elementChild.ComputedStyle);
            var childBox = ResolveBoxStyle(childStyleMap, elementChild.Ref);
            var childHorizontalExtras = childBox.BorderWidth.Left + childBox.BorderWidth.Right + childBox.Padding.Left + childBox.Padding.Right;
            var childVerticalExtras = childBox.BorderWidth.Top + childBox.BorderWidth.Bottom + childBox.Padding.Top + childBox.Padding.Bottom;
            var childContentWidth = ResolveAuthoredDimension(childStyleMap, "width", cellWidth, float.NaN, childHorizontalExtras);
            var childContentHeight = ResolveAuthoredDimension(childStyleMap, "height", cellHeight, float.NaN, childVerticalExtras);
            var cellContentWidth = Math.Max(0f, cellWidth - childHorizontalExtras);
            var cellContentHeight = Math.Max(0f, cellHeight - childVerticalExtras);
            var resolvedChildContentWidth = float.IsNaN(childContentWidth) ? cellContentWidth : childContentWidth;
            var resolvedChildContentHeight = float.IsNaN(childContentHeight) ? cellContentHeight : childContentHeight;
            var justifySelf = ResolveGridSelfAlignment(childStyleMap, "justify-self", styleMap, "justify-items");
            var alignSelf = ResolveGridSelfAlignment(childStyleMap, "align-self", styleMap, "align-items");

            // An item that is not stretched is as wide as its content (shrink-to-fit) and as tall.
            if (float.IsNaN(childContentWidth) && justifySelf != GridSelfAlignment.Stretch)
            {
                var fitWidth = Math.Min(cellWidth, ResolveShrinkToFitBorderBoxWidth(elementChild, cellWidth, inheritedTextStyle, context, out _, out _));
                childContentWidth = Math.Max(0f, fitWidth - childHorizontalExtras);
                resolvedChildContentWidth = childContentWidth;
            }

            var childBorderBoxWidth = float.IsNaN(childContentWidth) ? cellWidth : childContentWidth + childHorizontalExtras;

            if (float.IsNaN(childContentHeight) && alignSelf != GridSelfAlignment.Stretch)
            {
                var measured = TrialLayout(elementChild, childBorderBoxWidth, inheritedTextStyle, context, childBorderBoxWidth, isAtomicInline: false).Metrics?.BorderBoxHeight ?? 0f;
                childContentHeight = Math.Max(0f, measured - childVerticalExtras);
                resolvedChildContentHeight = childContentHeight;
            }

            var childBorderBoxHeight = float.IsNaN(childContentHeight) ? cellHeight : childContentHeight + childVerticalExtras;
            var alignedCellX = cellX + AlignInGridCell(justifySelf, cellWidth, childBorderBoxWidth);
            var alignedCellY = cellY + AlignInGridCell(alignSelf, cellHeight, childBorderBoxHeight);

            static float AlignInGridCell(GridSelfAlignment alignment, float cellSize, float itemSize) => alignment switch
            {
                GridSelfAlignment.Center => (cellSize - itemSize) / 2f,
                GridSelfAlignment.End => cellSize - itemSize,
                _ => 0f,
            };

            var childCursor = alignedCellY;
            var childPreviousBlockMarginBottom = 0f;
            var childSuppressNextBlockTopMargin = false;
            var childFloats = new FloatArea();
            var childTextIndentConsumed = false;

            LayoutNode(
                node: elementChild,
                containingX: alignedCellX,
                containingY: alignedCellY,
                containingWidth: resolvedChildContentWidth,
                cursorY: ref childCursor,
                previousBlockMarginBottom: ref childPreviousBlockMarginBottom,
                suppressNextBlockTopMargin: ref childSuppressNextBlockTopMargin,
                floats: childFloats,
                textIndentConsumed: ref childTextIndentConsumed,
                textStyle: inheritedTextStyle,
                context: context,
                displayList: displayList,
                maxY: maxY,
                isFlexItem: true,
                isRowDirection: true,
                flexMainSize: resolvedChildContentWidth,
                flexCrossSize: resolvedChildContentHeight);
        }

        var gridContentWidth = GetGridContentSize(columnSizes, resolvedColumnGap, containingWidth);
        var gridContentHeight = GetGridContentSize(rowSizes, resolvedRowGap, 0f);
        var borderBoxWidth = borderLeft + paddingLeft + Math.Max(containingWidth, gridContentWidth) + paddingRight + borderRight;

        // An authored height is the height; taller content overflows, as in a block.
        var resolvedGridContentHeight = float.IsNaN(containerHeight) ? gridContentHeight : containerHeight;
        resolvedGridContentHeight = Math.Max(resolvedGridContentHeight, ResolveAuthoredDimension(styleMap, "min-height", containingWidth, 0f, gridVerticalBorderAndPadding));
        var maximumGridContentHeight = ResolveAuthoredDimension(styleMap, "max-height", containingWidth, float.NaN, gridVerticalBorderAndPadding);

        if (!float.IsNaN(maximumGridContentHeight))
        {
            resolvedGridContentHeight = Math.Min(resolvedGridContentHeight, maximumGridContentHeight);
        }
        var borderBoxHeight = borderTop + paddingTop + resolvedGridContentHeight + paddingBottom + borderBottom;
        var canCollapseWithLastChild = borderBottom <= 0f && paddingBottom <= 0f;
        var effectiveMarginBottom = ParseLength(styleMap, "margin-bottom", containingWidth, box.Margin.Bottom, allowAuto: false);

        if (canCollapseWithLastChild)
        {
            effectiveMarginBottom = CollapseMargins(effectiveMarginBottom, previousBlockMarginBottom);
        }

        // Percentage radii refer to the border box, which is only known now.
        box = box with { BorderRadius = ResolveBorderRadii(styleMap, borderBoxWidth, borderBoxHeight) };
        var boxPaintBuffer = new DisplayList();
        var decorationBuffer = inheritedTextStyle.IsHidden ? new DisplayList() : boxPaintBuffer;
        var effects = OpenElementEffectScopes(
            boxPaintBuffer,
            decorationBuffer,
            styleMap,
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            new EdgeSizes(borderTop, borderRight, borderBottom, borderLeft),
            new EdgeSizes(paddingTop, paddingRight, paddingBottom, paddingLeft),
            box.BorderRadius,
            inheritedTextStyle.FontSize,
            BlendsInside(displayList, boxPaintInsertIndex));

        if (box.BackgroundPaint is RenderColorPaint colorPaint && colorPaint.Color.A == 0)
        {
            decorationBuffer.FillRect(new RenderRect(borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight), RenderColor.Transparent);
        }
        else
        {
            PaintBoxBackground(decorationBuffer, styleMap, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, new EdgeSizes(borderTop, borderRight, borderBottom, borderLeft), new EdgeSizes(paddingTop, paddingRight, paddingBottom, paddingLeft));
        }

        PaintBoxShadows(decorationBuffer, box.BoxShadows, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight, box.BorderRadius);

        RecordLayoutMetrics(
            node.Ref,
            borderBoxX,
            borderBoxY,
            borderBoxWidth,
            borderBoxHeight,
            borderLeft,
            borderRight,
            borderTop,
            borderBottom,
            paddingLeft,
            paddingRight,
            paddingTop,
            paddingBottom);

        PaintBoxBorder(decorationBuffer, styleMap, node.Ref, box, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);
        PaintOutline(decorationBuffer, styleMap, borderBoxX, borderBoxY, borderBoxWidth, borderBoxHeight);

        var clipsOverflow = ShouldClipOverflow(styleMap);

        if (clipsOverflow)
        {
            var clipRect = new RenderRect(
                borderBoxX + borderLeft,
                borderBoxY + borderTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom));
            boxPaintBuffer.PushClip(clipRect, box.BorderRadius.ClampToBox(borderBoxWidth, borderBoxHeight));
        }

        displayList.InsertRange(boxPaintInsertIndex, boxPaintBuffer.Commands);
        ResolveStickyBoxes(
            displayList,
            context.StickyScopes.Peek(),
            new RenderRect(
                borderBoxX + borderLeft + paddingLeft,
                borderBoxY + borderTop + paddingTop,
                Math.Max(0f, borderBoxWidth - borderLeft - borderRight - paddingLeft - paddingRight),
                Math.Max(0f, borderBoxHeight - borderTop - borderBottom - paddingTop - paddingBottom)),
            context);

        if (clipsOverflow)
        {
            displayList.PopClip();
        }

        CloseElementEffectScopes(displayList, effects);
        cursorY = flowBorderBoxY + borderBoxHeight;
        previousBlockMarginBottom = effectiveMarginBottom + context.ParagraphSpacing;
        suppressNextBlockTopMargin = false;
    }

    private static float GetGridTrackOffset(IReadOnlyList<float> tracks, int index, float gap)
    {
        if (index <= 0)
        {
            return 0f;
        }

        var offset = 0f;
        for (var current = 0; current < index && current < tracks.Count; current++)
        {
            offset += tracks[current];
            offset += gap;
        }

        return offset;
    }

    private static float GetGridTrackSpanSize(IReadOnlyList<float> tracks, int index, int span, float gap, float fallback)
    {
        var totalSize = 0f;
        var spanCount = Math.Max(1, span);

        for (var current = 0; current < spanCount; current++)
        {
            var trackIndex = index + current;
            totalSize += GetGridTrackSize(tracks, trackIndex, fallback);

            if (current < spanCount - 1)
            {
                totalSize += gap;
            }
        }

        return totalSize;
    }

    /// <summary>
    /// Parses `grid-template-columns`/`grid-template-rows` into a track-sizing-function list,
    /// delegating the outer function/list grammar to AngleSharp.Css's own `GridParser.ParseTrackList`
    /// (mirroring the `transform`/`filter`/gradient precedent: AngleSharp.Css computes the pure-CSS
    /// structure - `repeat()`, `minmax()`, `fit-content()`, the `fr` unit - this renderer still owns
    /// turning that into its own backend-agnostic <see cref="GridTrackSize"/> list and, later, the
    /// actual pixel sizes). Returns an empty list when unset or `none`, matching CSS's own initial
    /// value - the caller falls back to a single implicit track, the same default a real browser
    /// gives an unstyled grid container.
    /// </summary>
    private static List<GridTrackSize> ParseGridTrackListStructured(string rawValue, float relativeTo, float gap = 0f, int itemCount = 0, Dictionary<string, List<int>>? lineNames = null)
    {
        var tracks = new List<GridTrackSize>();
        var trimmed = rawValue?.Trim() ?? string.Empty;

        if (trimmed.Length == 0 || string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
        {
            return tracks;
        }

        var source = new StringSource(trimmed);
        var parsed = source.ParseTrackList();

        if (parsed is not null)
        {
            AppendGridTracks(parsed, relativeTo, tracks, gap, itemCount, lineNames);
            return tracks;
        }

        // AngleSharp.Css's own GridParser does not currently recognize the `auto-fill`/`auto-fit`
        // repeat-count keywords at all (confirmed empirically: ParseTrackList returns null for the
        // whole value, not just that one token) - a genuine upstream gap, worth reporting there,
        // but one this renderer still needs to work around locally to support the single most
        // common real-world use of `repeat()` (`repeat(auto-fill/auto-fit, ...)` as the entire
        // track list). TryParseAutoRepeatTrackList hand-parses just that one shape; anything else
        // that also failed to parse (a genuinely malformed value) still falls through to the empty
        // list the caller already treats as "no explicit tracks".
        if (TryParseAutoRepeatTrackList(trimmed, relativeTo, gap, itemCount, tracks, lineNames))
        {
            return tracks;
        }

        return tracks;
    }

    /// <summary>
    /// Hand-parses `repeat(auto-fill, &lt;track-list&gt;)`/`repeat(auto-fit, &lt;track-list&gt;)` as
    /// the entire `grid-template-columns`/`-rows` value - locating the keyword by splitting on the
    /// first top-level comma (one that is not itself nested inside the repeated track list's own
    /// parentheses, e.g. `minmax(100px, 1fr)`'s comma), then delegating the repeated track list's
    /// own structural parsing back to AngleSharp.Css's `GridParser` exactly as the normal path does -
    /// only the outer auto-fill/auto-fit keyword recognition itself is hand-rolled.
    /// </summary>
    private static bool TryParseAutoRepeatTrackList(string trimmed, float relativeTo, float gap, int itemCount, List<GridTrackSize> tracks, Dictionary<string, List<int>>? lineNames)
    {
        if (!trimmed.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        var inner = trimmed[7..^1];
        var depth = 0;
        var commaIndex = -1;

        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    commaIndex = i;
                    break;
            }

            if (commaIndex >= 0)
            {
                break;
            }
        }

        if (commaIndex < 0)
        {
            return false;
        }

        var keyword = inner[..commaIndex].Trim();
        var isAutoFill = string.Equals(keyword, "auto-fill", StringComparison.OrdinalIgnoreCase);
        var isAutoFit = string.Equals(keyword, "auto-fit", StringComparison.OrdinalIgnoreCase);

        if (!isAutoFill && !isAutoFit)
        {
            return false;
        }

        var trackListText = inner[(commaIndex + 1)..].Trim();
        var trackSource = new StringSource(trackListText);
        var trackDefinition = trackSource.ParseTrackList();

        if (trackDefinition is null)
        {
            return false;
        }

        var groupMinSize = ResolveGridRepeatGroupMinSize(trackDefinition, relativeTo);

        if (groupMinSize <= 0f || float.IsNaN(relativeTo) || relativeTo <= 0f)
        {
            AppendGridTracks(trackDefinition, relativeTo, tracks, gap, itemCount, lineNames);
            return true;
        }

        var fillCount = Math.Max(1, (int)Math.Floor((relativeTo + gap) / (groupMinSize + gap)));

        if (isAutoFit && itemCount > 0)
        {
            fillCount = Math.Max(1, Math.Min(fillCount, itemCount));
        }

        for (var i = 0; i < fillCount; i++)
        {
            AppendGridTracks(trackDefinition, relativeTo, tracks, gap, itemCount, lineNames);
        }

        return true;
    }

    private static void AppendGridTracks(ICssValue value, float relativeTo, List<GridTrackSize> tracks, float gap, int itemCount, Dictionary<string, List<int>>? lineNames = null)
    {
        switch (value)
        {
            // `[name]` names the line before the next track (0-based line index = tracks so far).
            case CssLineNamesValue names:
                if (lineNames is not null)
                {
                    foreach (var name in names.Names)
                    {
                        if (!lineNames.TryGetValue(name, out var indices))
                        {
                            lineNames[name] = indices = [];
                        }

                        indices.Add(tracks.Count);
                    }
                }

                break;

            // CssRepeatValue/CssFitContentValue are both `internal` in AngleSharp.Css (unlike
            // CssMinMaxValue, which is public) - matched via the public ICssFunctionValue interface
            // (Name/Arguments) they both implement instead of the concrete type.
            case ICssFunctionValue repeatFunc when string.Equals(repeatFunc.Name, "repeat", StringComparison.OrdinalIgnoreCase) && repeatFunc.Arguments.Length == 2:
                var count = ResolveGridRepeatCount(repeatFunc.Arguments[0], repeatFunc.Arguments[1], relativeTo, gap, itemCount);

                for (var i = 0; i < count; i++)
                {
                    AppendGridTracks(repeatFunc.Arguments[1], relativeTo, tracks, gap, itemCount, lineNames);
                }

                break;

            case CssTupleValue<ICssValue> tuple:
                foreach (var item in tuple.Items)
                {
                    if (item is not null)
                    {
                        AppendGridTracks(item, relativeTo, tracks, gap, itemCount, lineNames);
                    }
                }

                break;

            default:
                tracks.Add(ConvertGridTrackSize(value, relativeTo));
                break;
        }
    }

    /// <summary>
    /// `repeat(auto-fill, ...)`/`repeat(auto-fit, ...)` resolve their true fill count from the
    /// container's own available space, the axis gap, and the repeated track group's own minimum
    /// size - the same formula browsers use: <c>floor((available + gap) / (groupMinSize + gap))</c>,
    /// clamped to at least one repetition. `auto-fit` additionally collapses empty trailing tracks:
    /// the full spec algorithm does this after item placement (an empty track becomes a literal
    /// zero-width, gap-less track), which this renderer approximates by capping the fill count at
    /// the actual item count up front - close enough for the common single-row/auto-placement case,
    /// though it does not reclaim space for an item that itself spans multiple tracks. `auto-fill`
    /// keeps every computed track (empty or not), which is the whole difference between the two.
    /// </summary>
    private static int ResolveGridRepeatCount(ICssValue countValue, ICssValue trackDefinition, float availableSize, float gap, int itemCount)
    {
        var text = countValue.CssText.Trim();
        var isAutoFill = string.Equals(text, "auto-fill", StringComparison.OrdinalIgnoreCase);
        var isAutoFit = string.Equals(text, "auto-fit", StringComparison.OrdinalIgnoreCase);

        if (isAutoFill || isAutoFit)
        {
            if (float.IsNaN(availableSize) || availableSize <= 0f)
            {
                return 1;
            }

            var groupMinSize = ResolveGridRepeatGroupMinSize(trackDefinition, availableSize);

            if (groupMinSize <= 0f)
            {
                return 1;
            }

            var fillCount = Math.Max(1, (int)Math.Floor((availableSize + gap) / (groupMinSize + gap)));

            if (isAutoFit && itemCount > 0)
            {
                fillCount = Math.Max(1, Math.Min(fillCount, itemCount));
            }

            return fillCount;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return Math.Clamp(count, 1, 1000);
        }

        return 1;
    }

    /// <summary>
    /// The minimum pixel footprint of one repetition of a `repeat(auto-fill/auto-fit, ...)`'s own
    /// track list - a fixed length contributes its own size, `minmax(min, max)` contributes `min`
    /// when it is itself a plain length (0 otherwise), and an `fr`/`auto` track contributes 0 (it
    /// has no definite minimum to anchor the fill-count formula to) - a deliberate approximation of
    /// the spec's own (considerably more involved) automatic-repeat-count algorithm, matching the
    /// overwhelmingly common real-world pattern of `repeat(auto-fill/auto-fit, minmax(&lt;length&gt;, ...))`.
    /// </summary>
    private static float ResolveGridRepeatGroupMinSize(ICssValue trackDefinition, float relativeTo) => trackDefinition switch
    {
        CssTupleValue<ICssValue> tuple => tuple.Items.Where(item => item is not null).Sum(item => ResolveGridTrackMinContribution(item!, relativeTo)),
        _ => ResolveGridTrackMinContribution(trackDefinition, relativeTo),
    };

    private static float ResolveGridTrackMinContribution(ICssValue value, float relativeTo) => value switch
    {
        CssMinMaxValue minMax => minMax.Minimum is CssLengthValue ? ResolveGridTrackLength(minMax.Minimum, relativeTo) : 0f,
        CssLengthValue => ResolveGridTrackLength(value, relativeTo),
        _ => 0f,
    };

    private static GridTrackSize ConvertGridTrackSize(ICssValue value, float relativeTo)
    {
        switch (value)
        {
            case CssFractionValue fraction:
                return GridTrackSize.Fraction((float)fraction.Value);

            case CssMinMaxValue minMax:
                return ConvertGridMinMax(minMax, relativeTo);

            case ICssFunctionValue fitContentFunc when string.Equals(fitContentFunc.Name, "fit-content", StringComparison.OrdinalIgnoreCase) && fitContentFunc.Arguments.Length == 1:
                var fitContent = GridTrackSize.Auto(max: ResolveGridTrackLength(fitContentFunc.Arguments[0], relativeTo));
                fitContent.MaxSizing = GridTrackMaxSizing.FitContent;
                return fitContent;

            case CssLengthValue:
                return GridTrackSize.Fixed(ResolveGridTrackLength(value, relativeTo));

            default:
                // "auto"/"min-content"/"max-content"; anything unrecognized is content-sized like "auto".
                var track = GridTrackSize.Auto();
                track.MaxSizing = ResolveGridContentKeyword(value);
                track.MinIsMaxContent = track.MaxSizing == GridTrackMaxSizing.MaxContent;
                return track;
        }
    }

    private static GridTrackMaxSizing ResolveGridContentKeyword(ICssValue? value) => value?.CssText.Trim().ToLowerInvariant() switch
    {
        "min-content" => GridTrackMaxSizing.MinContent,
        "max-content" => GridTrackMaxSizing.MaxContent,
        _ => GridTrackMaxSizing.Auto,
    };

    /// <summary>
    /// `minmax(min, max)` where `max` is a `&lt;flex&gt;` (`fr`) is the common, important case
    /// (`minmax(100px, 1fr)` - "at least 100px, then grow to fill") and gets real support: the
    /// track participates in free-space distribution like any other `fr` track, but never shrinks
    /// below `min`. Any other combination (`minmax(100px, 300px)`, `minmax(min-content, 1fr)`, ...)
    /// is approximated as a content-sized track clamped to whichever bounds were themselves plain
    /// lengths/percentages - not the spec's own iterative clamping algorithm, but consistent with
    /// this renderer's existing item-estimate-based approximation for content sizing in general.
    /// </summary>
    private static GridTrackSize ConvertGridMinMax(CssMinMaxValue minMax, float relativeTo)
    {
        if (minMax.Maximum is CssFractionValue maxFraction)
        {
            var floorPixels = minMax.Minimum is CssLengthValue ? ResolveGridTrackLength(minMax.Minimum, relativeTo) : (float?)null;
            return GridTrackSize.Fraction((float)maxFraction.Value, floorPixels);
        }

        var min = minMax.Minimum is CssLengthValue ? ResolveGridTrackLength(minMax.Minimum, relativeTo) : (float?)null;
        var max = minMax.Maximum is CssLengthValue ? ResolveGridTrackLength(minMax.Maximum, relativeTo) : (float?)null;
        var track = GridTrackSize.Auto(min, max);

        if (max is null)
        {
            track.MaxSizing = ResolveGridContentKeyword(minMax.Maximum);
        }

        track.MinIsMaxContent = min is null && ResolveGridContentKeyword(minMax.Minimum) == GridTrackMaxSizing.MaxContent;
        return track;
    }

    /// <summary>
    /// Resolves one already-structured track-size sub-value's own length/percentage into pixels,
    /// reading its `.CssText` (e.g. "50%", "20px") the same "re-parse the structured value's own
    /// serialized text with this renderer's existing semantic parsers" pattern already established
    /// for `filter`/gradients - <see cref="ParseLengthValue"/> itself has no percentage handling
    /// (every other caller resolves percentages against a styleMap-driven containing dimension it
    /// doesn't have here), so this adds that one case directly rather than reusing it verbatim.
    /// </summary>
    private static float ResolveGridTrackLength(ICssValue value, float relativeTo)
    {
        var text = value.CssText.Trim();

        if (text.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return Math.Max(0f, relativeTo * percent / 100f);
        }

        var pixels = ParseLengthValue(text, float.NaN, allowAuto: false);
        return float.IsNaN(pixels) ? 0f : Math.Max(0f, pixels);
    }

    private static float? ParseGridGap(Dictionary<string, string> styleMap, string propertyName, float relativeTo, int tokenIndex)
    {
        if (!styleMap.TryGetValue(propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var tokens = rawValue.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var token = tokenIndex >= 0 && tokenIndex < tokens.Length ? tokens[tokenIndex] : tokens[^1];

        if (token.EndsWith('%') &&
            float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
        {
            return percentage / 100f * relativeTo;
        }

        var parsed = ParseLengthValue(token, float.NaN, allowAuto: false);
        return float.IsNaN(parsed) ? null : parsed;
    }

    /// <summary>
    /// <c>column-gap</c>, else the second (or only) value of the <c>gap</c> shorthand. Percentages
    /// refer to the container's content-box width.
    /// </summary>
    private static float? ResolveColumnGap(Dictionary<string, string> styleMap, float contentWidth) =>
        ParseGridGap(styleMap, "column-gap", contentWidth, 0) ?? ParseGridGap(styleMap, "gap", contentWidth, 1);

    /// <summary>
    /// <c>row-gap</c>, else the first value of the <c>gap</c> shorthand. Percentages refer to the
    /// container's content-box height, and to zero while that height is indefinite.
    /// </summary>
    private static float? ResolveRowGap(Dictionary<string, string> styleMap, float definiteContentHeight) =>
        ParseGridGap(styleMap, "row-gap", definiteContentHeight, 0) ?? ParseGridGap(styleMap, "gap", definiteContentHeight, 0);

    private static float ResolveGapHeightReference(Dictionary<string, string> styleMap, float containingWidth, float verticalBorderAndPadding)
    {
        var height = ResolveDefiniteContentHeight(styleMap, containingWidth, verticalBorderAndPadding);
        return float.IsNaN(height) ? 0f : height;
    }

    /// <summary>
    /// The named lines of one grid axis (from <c>[name]</c> in the track list and the implicit
    /// <c>area-start</c>/<c>area-end</c> lines of <c>grid-template-areas</c>) and its explicit
    /// line count, which negative line numbers count back from.
    /// </summary>
    private sealed record GridLines(Dictionary<string, List<int>> Names, int ExplicitLineCount)
    {
        public static readonly GridLines None = new(new Dictionary<string, List<int>>(StringComparer.Ordinal), 0);
    }

    private static GridPlacement ResolveGridPlacementFromMap(Dictionary<string, string> childStyleMap, string propertyName, int fallbackIndex, GridLines? lines = null)
    {
        var (start, span) = ParseGridLinePlacement(childStyleMap.GetValueOrDefault(propertyName), lines ?? GridLines.None);
        return new GridPlacement(start ?? Math.Max(0, fallbackIndex), Math.Max(1, span));
    }

    private static bool HasGridPlacement(Dictionary<string, string> styleMap, string propertyName, GridLines? lines = null) =>
        ParseGridLinePlacement(styleMap.GetValueOrDefault(propertyName), lines ?? GridLines.None).Start is not null;

    /// <summary>
    /// Parses a <c>grid-column</c>/<c>grid-row</c> value (<c>start / end</c>, either side a line
    /// number, a negative line number, a line name optionally with an index, <c>span n</c> or
    /// <c>auto</c>) into a 0-based start line (null when auto-placed) and a span.
    /// </summary>
    private static (int? Start, int Span) ParseGridLinePlacement(string? rawValue, GridLines lines)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return (null, 1);
        }

        var parts = rawValue.Split('/', 2);
        var startTokens = SplitGridLineTokens(parts[0]);
        var endTokens = parts.Length > 1 ? SplitGridLineTokens(parts[1]) : null;

        // A lone name stands for both edges, so `grid-column: a` fills the area `a`.
        if (endTokens is null && startTokens.Length == 1 && !int.TryParse(startTokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
            !string.Equals(startTokens[0], "auto", StringComparison.OrdinalIgnoreCase))
        {
            endTokens = startTokens;
        }

        var start = ResolveGridLine(startTokens, lines, "-start");
        var end = endTokens is null ? null : ResolveGridLine(endTokens, lines, "-end");
        var startSpan = ResolveGridSpan(startTokens);
        var endSpan = endTokens is null ? null : ResolveGridSpan(endTokens);

        if (start is { } bothStart && end is { } bothEnd)
        {
            return bothStart == bothEnd ? (bothStart, 1) : (Math.Min(bothStart, bothEnd), Math.Abs(bothEnd - bothStart));
        }

        if (start is { } onlyStart)
        {
            return (onlyStart, endSpan ?? 1);
        }

        if (end is { } onlyEnd)
        {
            var span = startSpan ?? 1;
            return (Math.Max(0, onlyEnd - span), span);
        }

        return (null, startSpan ?? endSpan ?? 1);
    }

    private static string[] SplitGridLineTokens(string part) =>
        part.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int? ResolveGridLine(string[] tokens, GridLines lines, string nameSuffix)
    {
        if (tokens.Any(token => string.Equals(token, "span", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        int? number = null;
        string? name = null;

        foreach (var token in tokens)
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                number = parsed;
            }
            else if (!string.Equals(token, "auto", StringComparison.OrdinalIgnoreCase))
            {
                name = token;
            }
        }

        if (name is null)
        {
            return number switch
            {
                > 0 => number.Value - 1,
                < 0 => Math.Max(0, lines.ExplicitLineCount + number.Value),
                _ => null,
            };
        }

        // `name-start`/`name-end` (the implicit lines of an area) win over a plain `name`.
        if (!lines.Names.TryGetValue(name + nameSuffix, out var indices) && !lines.Names.TryGetValue(name, out indices))
        {
            return null;
        }

        var nth = number ?? 1;
        return nth switch
        {
            > 0 when nth <= indices.Count => indices[nth - 1],
            < 0 when -nth <= indices.Count => indices[indices.Count + nth],
            _ => null,
        };
    }

    private static int? ResolveGridSpan(string[] tokens)
    {
        var spanIndex = Array.FindIndex(tokens, token => string.Equals(token, "span", StringComparison.OrdinalIgnoreCase));

        if (spanIndex < 0)
        {
            return null;
        }

        return tokens.Skip(spanIndex + 1).Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : (int?)null)
            .FirstOrDefault(count => count is > 0) ?? 1;
    }

    /// <summary>
    /// <c>justify-self</c>/<c>align-self</c>, where <c>auto</c> defers to the container's
    /// <c>justify-items</c>/<c>align-items</c>; <c>normal</c> stretches.
    /// </summary>
    private static GridSelfAlignment ResolveGridSelfAlignment(Dictionary<string, string> itemStyle, string selfProperty, Dictionary<string, string> containerStyle, string itemsProperty)
    {
        var value = itemStyle.GetValueOrDefault(selfProperty)?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(value) || value == "auto")
        {
            value = containerStyle.GetValueOrDefault(itemsProperty)?.Trim().ToLowerInvariant() ?? string.Empty;
        }

        var keyword = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

        return keyword switch
        {
            "start" or "flex-start" or "self-start" or "left" or "baseline" => GridSelfAlignment.Start,
            "end" or "flex-end" or "self-end" or "right" => GridSelfAlignment.End,
            "center" => GridSelfAlignment.Center,
            _ => GridSelfAlignment.Stretch,
        };
    }

    private enum GridSelfAlignment
    {
        Stretch,
        Start,
        Center,
        End,
    }

    /// <summary>
    /// <c>fr</c> rows of a grid without a definite height: every <c>fr</c> unit gets the largest
    /// size any of those rows needs per unit for its content (the spec's flex fraction for
    /// indefinite free space), so the rows keep their ratios and still fit their items.
    /// </summary>
    private static void ResolveIndefiniteGridFractionTracks(List<GridTrackSize> tracks, Dictionary<int, float> contentSizes)
    {
        var flexFraction = 0f;

        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];

            if (track.Kind != GridTrackSizeKind.Fraction)
            {
                continue;
            }

            var needed = Math.Max(track.MinPixels ?? 0f, contentSizes.GetValueOrDefault(index));

            if (track.FractionValue >= 1f)
            {
                flexFraction = Math.Max(flexFraction, needed / track.FractionValue);
            }
            else
            {
                flexFraction = Math.Max(flexFraction, needed);
            }
        }

        foreach (var track in tracks)
        {
            if (track.Kind == GridTrackSizeKind.Fraction)
            {
                track.Pixels = Math.Max(track.MinPixels ?? 0f, flexFraction * track.FractionValue);
            }
        }
    }

    /// <summary>
    /// An authored height that is definite for layout purposes: percentages have no definite
    /// base here and count as auto.
    /// </summary>
    private static float ResolveDefiniteContentHeight(Dictionary<string, string> styleMap, float containingWidth, float verticalBorderAndPadding) =>
        styleMap.TryGetValue("height", out var rawHeight) && rawHeight.TrimEnd().EndsWith('%')
            ? float.NaN
            : ResolveAuthoredDimension(styleMap, "height", containingWidth, float.NaN, verticalBorderAndPadding);

    /// <summary>
    /// The item's authored <c>height</c> (at least its <c>min-height</c>), which sizes its Auto row
    /// up front; items without one are measured once the column widths are known.
    /// </summary>
    private static float ResolveGridItemAuthoredHeight(Dictionary<string, string> styleMap, float fallbackSize)
    {
        var height = styleMap.TryGetValue("height", out var value) && !string.IsNullOrWhiteSpace(value)
            ? ParseLengthValue(value, fallbackSize, allowAuto: false)
            : float.NaN;
        var authored = float.IsNaN(height) ? 0f : Math.Max(0f, height);

        return Math.Max(authored, ParseLength(styleMap, "min-height", fallbackSize, 0f, allowAuto: false));
    }

    private static float MeasureGridItemHeight(ElementRenderNode element, float containingWidth, RenderTextStyle inheritedTextStyle, LayoutContext context, float maxY)
    {
        var displayList = new DisplayList();
        var cursorY = 0f;
        var previousBlockMarginBottom = 0f;
        var suppressNextBlockTopMargin = false;
        var floats = new FloatArea();
        var textIndentConsumed = false;

        LayoutNode(
            element,
            0f,
            0f,
            containingWidth,
            ref cursorY,
            ref previousBlockMarginBottom,
            ref suppressNextBlockTopMargin,
            floats,
            ref textIndentConsumed,
            inheritedTextStyle,
            context,
            displayList,
            maxY);

        return cursorY;
    }

    /// <summary>
    /// Grows an <see cref="GridTrackSizeKind.Auto"/> track to fit an item's own estimated size,
    /// the same "grow, never shrink" approximation for content-based sizing this renderer already
    /// used before structured `grid-template-columns`/`rows` parsing - now scoped to `Auto` tracks
    /// specifically (a `Fixed` track already has its final size; a `Fraction` track's only comes
    /// from <see cref="ResolveGridFractionTracks"/>, once every `Auto`/`Fixed` track is settled).
    /// </summary>
    /// <summary>
    /// Sizes a grid's content-sized columns from their items' min-content/max-content
    /// contributions (CSS Grid §12.5, simplified): each track gets a base size (min-content, or its
    /// `minmax()` floor) and a growth limit (max-content, or its fixed / `fit-content()` maximum).
    /// Items are taken by increasing span; one spanning several tracks spreads what those tracks
    /// still lack equally over the content-sized ones, and one spanning an `fr` track is ignored.
    /// Free space then grows tracks up to their growth limits ("maximize tracks"); what is left
    /// stretches `auto`-maximum tracks equally when no `fr` track takes it and `justify-content`
    /// does not pack the tracks.
    /// </summary>
    private static void ResolveIntrinsicGridColumns(List<GridTrackSize> columns, List<(GridPlacement Column, IntrinsicWidths Widths)> contributions, float gap, float availableWidth, bool stretchAutoTracks)
    {
        var baseSizes = new float[columns.Count];
        var limits = new float[columns.Count];

        foreach (var (placement, widths) in contributions.OrderBy(c => c.Column.Span))
        {
            var first = Math.Max(0, placement.LineIndex);
            var last = Math.Min(columns.Count, placement.LineIndex + placement.Span);
            var autoTracks = Enumerable.Range(first, last - first).Where(i => columns[i].Kind == GridTrackSizeKind.Auto).ToList();

            if (autoTracks.Count == 0 || Enumerable.Range(first, last - first).Any(i => columns[i].Kind == GridTrackSizeKind.Fraction))
            {
                continue;
            }

            var spacing = Math.Max(0, last - first - 1) * gap + Enumerable.Range(first, last - first).Where(i => columns[i].Kind == GridTrackSizeKind.Fixed).Sum(i => columns[i].Pixels);
            var track = columns[autoTracks[0]];
            var single = last - first == 1;
            var minContribution = single && track.MinIsMaxContent ? widths.Max : widths.Min;
            var maxContribution = single && track.MaxSizing == GridTrackMaxSizing.MinContent ? widths.Min : widths.Max;

            SpreadExcess(baseSizes, minContribution);
            SpreadExcess(limits, maxContribution);

            void SpreadExcess(float[] sizes, float contribution)
            {
                var excess = contribution - spacing - autoTracks.Sum(i => sizes[i]);

                if (excess > 0f)
                {
                    foreach (var i in autoTracks)
                    {
                        sizes[i] += excess / autoTracks.Count;
                    }
                }
            }
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var track = columns[i];

            if (track.Kind != GridTrackSizeKind.Auto)
            {
                continue;
            }

            var baseSize = track.MinPixels ?? baseSizes[i];
            var limit = track.MaxSizing switch
            {
                GridTrackMaxSizing.Fixed => track.MaxPixels ?? limits[i],
                GridTrackMaxSizing.FitContent => Math.Min(limits[i], track.MaxPixels ?? limits[i]),
                _ => limits[i],
            };

            track.Pixels = baseSize;
            limits[i] = Math.Max(baseSize, limit);
        }

        var freeSpace = availableWidth - Math.Max(0, columns.Count - 1) * gap - columns.Sum(t => t.Kind == GridTrackSizeKind.Fraction ? t.MinPixels ?? 0f : t.Pixels);

        while (freeSpace > 0.01f)
        {
            var growable = Enumerable.Range(0, columns.Count).Where(i => columns[i].Kind == GridTrackSizeKind.Auto && columns[i].Pixels < limits[i] - 0.01f).ToList();

            if (growable.Count == 0)
            {
                break;
            }

            var share = freeSpace / growable.Count;

            foreach (var i in growable)
            {
                var growth = Math.Min(share, limits[i] - columns[i].Pixels);
                columns[i].Pixels += growth;
                freeSpace -= growth;
            }
        }

        if (stretchAutoTracks && freeSpace > 0.01f && columns.All(t => t.Kind != GridTrackSizeKind.Fraction))
        {
            var stretchable = columns.Where(t => t.Kind == GridTrackSizeKind.Auto && t.MaxSizing == GridTrackMaxSizing.Auto).ToList();

            foreach (var track in stretchable)
            {
                track.Pixels += freeSpace / stretchable.Count;
            }
        }
    }

    private static void StretchAutoGridRows(List<GridTrackSize> rows, float gap, float availableHeight)
    {
        var freeSpace = availableHeight - rows.Sum(t => t.Pixels) - (gap * Math.Max(0, rows.Count - 1));

        if (freeSpace > 0.01f && rows.All(t => t.Kind != GridTrackSizeKind.Fraction))
        {
            var stretchable = rows.Where(t => t.Kind == GridTrackSizeKind.Auto && t.MaxSizing == GridTrackMaxSizing.Auto).ToList();

            foreach (var track in stretchable)
            {
                track.Pixels += freeSpace / stretchable.Count;
            }
        }
    }

    /// <summary>
    /// Grows the tracks an item spans to fit it: an item spanning a single track grows that one,
    /// one spanning several spreads what those tracks still lack equally over the content-sized
    /// ones, and one spanning an `fr` track is left to it (CSS Grid 1, 11.5).
    /// </summary>
    private static void GrowGridTrackSpan(List<GridTrackSize> tracks, GridPlacement placement, float itemEstimate, float gap)
    {
        if (placement.Span <= 1)
        {
            GrowGridTrackSize(tracks, placement.LineIndex, itemEstimate);
            return;
        }

        var spanned = Enumerable.Range(placement.LineIndex, placement.Span).Where(i => i >= 0 && i < tracks.Count).ToList();

        if (spanned.Any(i => tracks[i].Kind == GridTrackSizeKind.Fraction))
        {
            return;
        }

        var growable = spanned.Where(i => tracks[i].Kind == GridTrackSizeKind.Auto).ToList();
        var missing = itemEstimate - spanned.Sum(i => tracks[i].Pixels) - (gap * (spanned.Count - 1));

        if (growable.Count == 0 || float.IsNaN(missing) || missing <= 0f)
        {
            return;
        }

        foreach (var index in growable)
        {
            GrowGridTrackSize(tracks, index, tracks[index].Pixels + (missing / growable.Count));
        }
    }

    private static void GrowGridTrackSize(List<GridTrackSize> tracks, int index, float itemEstimate)
    {
        while (tracks.Count <= index)
        {
            tracks.Add(GridTrackSize.Auto());
        }

        var track = tracks[index];

        if (track.Kind != GridTrackSizeKind.Auto)
        {
            return;
        }

        var candidate = Math.Max(track.Pixels, itemEstimate);

        if (track.MaxPixels is { } max)
        {
            candidate = Math.Min(candidate, max);
        }

        if (track.MinPixels is { } min)
        {
            candidate = Math.Max(candidate, min);
        }

        track.Pixels = candidate;
    }

    /// <summary>
    /// Distributes one axis's remaining free space across its own `fr` tracks, proportional to
    /// each one's own `fr` count - the CSS Grid spec's own "distribute free space by flex factor"
    /// step, simplified (not the full spec algorithm's iterative handling of a `minmax()` track
    /// whose floor alone already exceeds its fair share - a rare, defensible approximation gap).
    /// A `minmax(floor, 1fr)` track's floor is reserved as already-used space before the remaining
    /// free space is computed, then added back on top of that track's own distributed share.
    /// </summary>
    private static void ResolveGridFractionTracks(List<GridTrackSize> tracks, float gap, float availableSpace)
    {
        var totalFraction = tracks.Where(t => t.Kind == GridTrackSizeKind.Fraction).Sum(t => t.FractionValue);

        if (totalFraction <= 0f)
        {
            return;
        }

        var usedSpace = tracks.Sum(t => t.Kind == GridTrackSizeKind.Fraction ? (t.MinPixels ?? 0f) : t.Pixels);
        var gapSpace = Math.Max(0, tracks.Count - 1) * gap;
        var freeSpace = Math.Max(0f, availableSpace - usedSpace - gapSpace);

        foreach (var track in tracks)
        {
            if (track.Kind == GridTrackSizeKind.Fraction)
            {
                track.Pixels = (track.MinPixels ?? 0f) + (freeSpace * (track.FractionValue / totalFraction));
            }
        }
    }

    private static GridTrackSize CloneGridTrackSize(GridTrackSize track) => track.Kind switch
    {
        GridTrackSizeKind.Fixed => GridTrackSize.Fixed(track.Pixels),
        GridTrackSizeKind.Fraction => GridTrackSize.Fraction(track.FractionValue, track.MinPixels),
        _ => new GridTrackSize
        {
            Kind = GridTrackSizeKind.Auto,
            MinPixels = track.MinPixels,
            MaxPixels = track.MaxPixels,
            MaxSizing = track.MaxSizing,
            MinIsMaxContent = track.MinIsMaxContent,
        },
    };

    private static Dictionary<string, (int Row, int Column, int RowSpan, int ColumnSpan)> ParseGridTemplateAreas(string? value)
    {
        var areas = new Dictionary<string, (int Row, int Column, int RowSpan, int ColumnSpan)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return areas;
        }

        var rows = value.Split('"', StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Trim())
            .Where(row => row.Length > 0)
            .ToArray();

        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var names = rows[rowIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var columnIndex = 0; columnIndex < names.Length; columnIndex++)
            {
                var name = names[columnIndex];
                if (name == ".")
                {
                    continue;
                }

                if (areas.TryGetValue(name, out var existing))
                {
                    areas[name] = (
                        Math.Min(existing.Row, rowIndex),
                        Math.Min(existing.Column, columnIndex),
                        Math.Max(existing.Row + existing.RowSpan, rowIndex + 1) - Math.Min(existing.Row, rowIndex),
                        Math.Max(existing.Column + existing.ColumnSpan, columnIndex + 1) - Math.Min(existing.Column, columnIndex));
                }
                else
                {
                    areas[name] = (rowIndex, columnIndex, 1, 1);
                }
            }
        }

        return areas;
    }

    private static float GetGridTrackSize(IReadOnlyList<float> tracks, int index, float fallback)
    {
        if (index >= 0 && index < tracks.Count)
        {
            return tracks[index];
        }

        return fallback;
    }

    private static float GetGridContentSize(IReadOnlyList<float> tracks, float gap, float fallbackSize)
    {
        if (tracks.Count <= 1)
        {
            return Math.Max(fallbackSize, tracks.Sum());
        }

        var trackSize = tracks.Sum();
        var gapSize = (tracks.Count - 1) * gap;
        return Math.Max(fallbackSize, trackSize + gapSize);
    }

    private static void LayoutTextNode(
        IText textNode,
        float containingX,
        float containingWidth,
        ref float cursorY,
        ref float previousBlockMarginBottom,
        ref bool suppressNextBlockTopMargin,
        FloatArea floats,
        ref bool textIndentConsumed,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY)
    {
        var text = ExpandTabs(context, ApplyTextTransform(NormalizeWhitespace(textNode.Data, textStyle.WhiteSpace), textStyle.TextTransform, previousEndsWord: false), textStyle);

        if (text.Length == 0)
        {
            return;
        }

        previousBlockMarginBottom = 0f;
        suppressNextBlockTopMargin = false;

        var (bandLeft, bandRight) = floats.GetBand(cursorY, textStyle.FontSize * textStyle.LineHeightMultiplier, containingX, containingX + containingWidth);
        LayoutWrappedText(text, bandLeft, bandRight - bandLeft, ref cursorY, textStyle, context, displayList, maxY, textIndentConsumed ? 0f : textStyle.TextIndent + textStyle.MarkerIndent);
        textIndentConsumed = true;
    }

    private static void LayoutWrappedText(
        string text,
        float x,
        float maxWidth,
        ref float cursorY,
        RenderTextStyle textStyle,
        LayoutContext context,
        DisplayList displayList,
        float maxY,
        float firstLineIndent)
    {
        var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;
        var forcedBreakLines = new HashSet<int>();
        var lines = WrapTextRespectingWhiteSpace(context, text, maxWidth, textStyle, forcedBreakLines);

        // `-webkit-line-clamp: N` truncates to at most N lines, with an ellipsis appended to the
        // last kept line whenever there was more text than fit - the multi-line counterpart to
        // `text-overflow: ellipsis`'s single-line truncation just above. The clamped-away lines
        // are dropped entirely (not laid out at all, not just clipped/hidden), matching how a real
        // browser's `-webkit-line-clamp` removes them from the box's own content height too.
        var isClampedLastLine = false;

        if (textStyle.LineClamp > 0 && lines.Count > textStyle.LineClamp)
        {
            lines = [.. lines.Take(textStyle.LineClamp)];
            isClampedLastLine = true;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            RecordColumnContentStart(cursorY);
            cursorY += lineHeight;
            RecordColumnBreakCandidate(cursorY);

            if (cursorY > maxY)
            {
                return;
            }

            // An empty line (a blank `pre`/`pre-wrap`/`pre-line` row from a run of consecutive
            // forced breaks) still needs to advance cursorY above, but has nothing to measure/paint
            // - unless it is itself the clamped-to line, which still needs its own ellipsis.
            if (line.Length == 0 && !(isClampedLastLine && index == lines.Count - 1))
            {
                continue;
            }

            var lineWidth = MeasureTextWidth(context, line, textStyle) + (CountSpaces(line) * textStyle.WordSpacing);
            var lineMaxWidth = index == 0 ? Math.Max(0f, maxWidth - firstLineIndent) : maxWidth;
            var isLastLine = index == lines.Count - 1;

            // `text-overflow: ellipsis` is scoped to the single-line case - by far the dominant
            // real-world usage (`overflow: hidden; white-space: nowrap; text-overflow: ellipsis`) -
            // rather than truncating the last of several wrapped lines, which the CSS spec itself
            // does not define without the non-standard `-webkit-line-clamp` extension handled
            // separately above; a genuinely multi-line, non-clamped result here is left as-is.
            if ((isClampedLastLine && isLastLine) ||
                (textStyle.TextOverflow == TextOverflowMode.Ellipsis && lines.Count == 1 && lineWidth > lineMaxWidth + 0.01f))
            {
                line = TruncateWithEllipsis(context, line, lineMaxWidth, textStyle);
                lineWidth = MeasureTextWidth(context, line, textStyle);
            }

            var lineAlign = ResolveLineAlign(textStyle, isLastLine || forcedBreakLines.Contains(index));
            var needsReordering = BidiAlgorithm.RequiresReordering(line, textStyle.Direction == TextDirection.Rtl);
            var spaceCount = CountSpaces(line);

            // `text-align: justify` widens every space of the line; reordered (bidi) lines and
            // lines without a space stay start-aligned.
            var justifyExtra = lineAlign == TextAlign.Justify && !needsReordering && spaceCount > 0 && lineWidth < lineMaxWidth
                ? (lineMaxWidth - lineWidth) / spaceCount
                : 0f;
            var lineX = x + (index == 0 ? firstLineIndent : 0f) + (justifyExtra > 0f ? 0f : ResolveTextAlignmentOffset(ResolveStartIfJustify(lineAlign, textStyle.Direction), lineMaxWidth, lineWidth));
            var baselineY = cursorY - lineHeight + ResolveTextBaselineOffset(lineHeight, textStyle.FontSize) + textStyle.VerticalAlignOffset;
            RecordTextBaseline(cursorY - lineHeight + ResolveTextBaselineOffset(lineHeight, textStyle.FontSize));

            // Lines with right-to-left text are painted as runs in visual order (UAX #9).
            if (needsReordering)
            {
                var runX = lineX;

                foreach (var run in GetVisualRuns(line, textStyle.Direction == TextDirection.Rtl))
                {
                    if (!string.IsNullOrWhiteSpace(run))
                    {
                        DrawStyledText(context, displayList, run, runX, baselineY, textStyle);
                    }

                    runX += MeasureTextWidth(context, run, textStyle) + (CountSpaces(run) * textStyle.WordSpacing);
                }

                continue;
            }

            if ((textStyle.WordSpacing != 0f || justifyExtra > 0f) && spaceCount > 0)
            {
                // A text run cannot express word spacing, so each word is painted on its own.
                var spaceAdvance = MeasureTextWidth(context, " ", textStyle) + textStyle.WordSpacing + justifyExtra;
                var wordX = lineX;

                foreach (var word in line.Split(' '))
                {
                    if (word.Length > 0)
                    {
                        DrawStyledText(context, displayList, word, wordX, baselineY, textStyle);
                        wordX += MeasureTextWidth(context, word, textStyle);
                    }

                    wordX += spaceAdvance;
                }

                continue;
            }

            DrawStyledText(context, displayList, line, lineX, baselineY, textStyle);
        }
    }

    private static int CountSpaces(string text)
    {
        var count = 0;

        foreach (var c in text)
        {
            if (c == ' ')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// One line of text as runs in visual order (UAX #9): each run is a maximal stretch at one
    /// embedding level, right-to-left runs already reversed and mirrored.
    /// </summary>
    private static List<string> GetVisualRuns(string line, bool isRightToLeft)
    {
        var levels = BidiAlgorithm.GetLevels(line, isRightToLeft);
        var order = BidiAlgorithm.GetVisualOrder(levels);
        var runs = new List<string>();
        var position = 0;

        while (position < order.Length)
        {
            var level = levels[order[position]];
            var end = position;

            while (end + 1 < order.Length && levels[order[end + 1]] == level && Math.Abs(order[end + 1] - order[end]) == 1)
            {
                end++;
            }

            var first = Math.Min(order[position], order[end]);
            var last = Math.Max(order[position], order[end]);
            var run = new string(line.AsSpan(first, last - first + 1).ToArray().Where(static c => !BidiAlgorithm.IsFormattingCharacter(c)).ToArray());
            runs.Add((level & 1) == 1 ? ReverseForDisplay(run) : run);
            position = end + 1;
        }

        return runs;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into display lines, honoring <c>white-space</c>'s two layout-
    /// relevant effects <see cref="NormalizeWhitespace(string, WhiteSpaceMode)"/> itself cannot: an
    /// explicit `\n` (only ever present in the input at all under <c>pre</c>/<c>pre-wrap</c>/
    /// <c>pre-line</c>/<c>break-spaces</c> - every other mode already collapsed it away) is always a
    /// forced break, laid out as its own paragraph rather than merely a wrappable space; and
    /// <c>nowrap</c>/<c>pre</c> never word-wrap at all, so each paragraph becomes exactly one
    /// (possibly overflowing) line regardless of <paramref name="maxWidth"/> - the box's own
    /// `overflow` clipping, if any, still applies to whatever ends up painted past its edge, since
    /// this only changes layout, not painting.
    /// </summary>
    private static IReadOnlyList<string> WrapTextRespectingWhiteSpace(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle, HashSet<int>? forcedBreakLines = null)
    {
        var noWrap = IsNoWrapWhiteSpace(textStyle.WhiteSpace);
        var paragraphs = text.Split('\n');

        if (paragraphs.Length == 1)
        {
            return noWrap ? [StripSoftHyphens(text)] : WrapText(context, text, maxWidth, textStyle);
        }

        var lines = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            if (noWrap || paragraph.Length == 0)
            {
                lines.Add(StripSoftHyphens(paragraph));
            }
            else
            {
                lines.AddRange(WrapText(context, paragraph, maxWidth, textStyle));
            }

            forcedBreakLines?.Add(lines.Count - 1);
        }

        return lines;
    }

    private static bool IsNoWrapWhiteSpace(WhiteSpaceMode whiteSpace) =>
        whiteSpace is WhiteSpaceMode.Nowrap or WhiteSpaceMode.Pre;

    /// <summary>
    /// The children of a flex or grid container as items: a child with `display: none` is none, and
    /// one with `display: contents` generates no box of its own, so its children take its place
    /// (CSS Display 3, 2.5) - e.g. a hidden child must not occupy a grid cell.
    /// </summary>
    private static IEnumerable<IRenderNode> FlattenDisplayContents(IEnumerable<IRenderNode> children, LayoutContext context)
    {
        foreach (var child in children)
        {
            if (!GeneratesBoxes(child))
            {
                continue;
            }

            if (child is ElementRenderNode element &&
                string.Equals(GetDisplay(GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle))?.Trim(), "contents", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var grandChild in FlattenDisplayContents(element.Children, context))
                {
                    yield return grandChild;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private static string? GetDisplay(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("display", out var display) ? display : null;
    }

    private static bool IsFlexContainer(Dictionary<string, string> styleMap)
    {
        var display = GetDisplay(styleMap);
        return string.Equals(display, "flex", StringComparison.OrdinalIgnoreCase) || string.Equals(display, "inline-flex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGridContainer(Dictionary<string, string> styleMap)
    {
        var display = GetDisplay(styleMap);
        return string.Equals(display, "grid", StringComparison.OrdinalIgnoreCase) || string.Equals(display, "inline-grid", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFlexDirection(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("flex-direction", out var direction) && !string.IsNullOrWhiteSpace(direction)
            ? direction.Trim().ToLowerInvariant()
            : "row";
    }

    private static string GetJustifyContent(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("justify-content", out var value) && !string.IsNullOrWhiteSpace(value)
            ? NormalizeFlexAlignment(value, "flex-start")
            : "flex-start";
    }

    private static string GetAlignItems(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("align-items", out var value) && !string.IsNullOrWhiteSpace(value)
            ? NormalizeFlexAlignment(value, "stretch")
            : "stretch";
    }

    /// <summary>
    /// Maps the keywords of CSS Box Alignment (e.g. <c>end</c>, <c>safe center</c>) to the flex
    /// ones they are equivalent to for flex items, and <c>normal</c> to the given default.
    /// </summary>
    private static string NormalizeFlexAlignment(string value, string normal)
    {
        var normalized = value.Trim().ToLowerInvariant();

        if (normalized.StartsWith("safe ", StringComparison.Ordinal) || normalized.StartsWith("unsafe ", StringComparison.Ordinal))
        {
            normalized = normalized[(normalized.IndexOf(' ') + 1)..].Trim();
        }

        return normalized switch
        {
            "start" or "self-start" or "left" => "flex-start",
            "end" or "self-end" or "right" => "flex-end",
            "normal" => normal,
            _ => normalized,
        };
    }

    private static string GetFlexWrap(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("flex-wrap", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "nowrap";
    }

    private static string GetAlignContent(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("align-content", out var value) && !string.IsNullOrWhiteSpace(value)
            ? NormalizeFlexAlignment(value, "stretch")
            : "stretch";
    }

    // Only the longhands are read: AngleSharp.Css serializes the `flex` shorthand lossily (a lone
    // `flex-basis: 40px` reads back as `flex: 40px`, which would mean `1 1 40px`).
    private static float GetFlexGrow(Dictionary<string, string> styleMap) =>
        styleMap.TryGetValue("flex-grow", out var value) && !string.IsNullOrWhiteSpace(value)
            ? Math.Max(0f, ParseLengthValue(value.Trim(), 0f, allowAuto: false))
            : 0f;

    private static float GetFlexShrink(Dictionary<string, string> styleMap) =>
        styleMap.TryGetValue("flex-shrink", out var value) && !string.IsNullOrWhiteSpace(value)
            ? Math.Max(0f, ParseLengthValue(value.Trim(), 1f, allowAuto: false))
            : 1f;

    private static float GetFlexOrder(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("order", out var value) && !string.IsNullOrWhiteSpace(value)
            ? ParseLengthValue(value.Trim(), 0f, allowAuto: false)
            : 0f;
    }

    private static string GetAlignSelf(Dictionary<string, string> styleMap, string fallback)
    {
        if (!styleMap.TryGetValue("align-self", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = NormalizeFlexAlignment(value, "stretch");
        return normalized == "auto" ? fallback : normalized;
    }

    private static FlexItemLayoutInfo CreateFlexItemLayoutInfo(IRenderNode child, bool isRowDirection, float relativeTo, float heightRelativeTo, RenderTextStyle inheritedTextStyle, LayoutContext context)
    {
        if (child is TextRenderNode textChild)
        {
            // a run of text is wrapped in an anonymous flex item, sized by its content
            var text = PrepareFlexItemText(textChild, inheritedTextStyle, context);
            var textMainSize = isRowDirection ? ComputeTextIntrinsicWidths(text, inheritedTextStyle, context, out _) : default;
            var textBaseSize = isRowDirection ? textMainSize.Max : MeasureFlexItemTextHeight(text, relativeTo, inheritedTextStyle, context);
            var textMinSize = isRowDirection ? textMainSize.Min : textBaseSize;
            return new FlexItemLayoutInfo(child, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 0f, 0f, 1f, textBaseSize, 0f, "auto", textMinSize);
        }

        if (child is not ElementRenderNode elementChild)
        {
            return new FlexItemLayoutInfo(child, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), 0f, 0f, 1f, 0f, 0f, "auto");
        }

        var childStyle = GetOrCreateStyleMap(context, elementChild.Ref, elementChild.ComputedStyle);
        // Needed so ResolveFlexBaseSize/ResolveFlexCrossSize can convert an authored border-box
        // width/height/flex-basis into this renderer's content-box convention using *this item's*
        // own border/padding - not the container's - before flex-grow/shrink ever runs, the same
        // way LayoutElement resolves its own specifiedContentWidth/Height (see
        // ResolveAuthoredDimension's remarks). The resulting BaseMainSize/CrossSize therefore
        // already represent content-box sizes, so LayoutElement's own isFlexItem branch in
        // ResolveFlexibleContentDimension must not (and does not) adjust them a second time.
        var childBox = ResolveBoxStyle(childStyle, elementChild.Ref);
        var horizontalBorderAndPadding = childBox.BorderWidth.Left + childBox.BorderWidth.Right + childBox.Padding.Left + childBox.Padding.Right;
        var verticalBorderAndPadding = childBox.BorderWidth.Top + childBox.BorderWidth.Bottom + childBox.Padding.Top + childBox.Padding.Bottom;
        var mainBorderAndPadding = isRowDirection ? horizontalBorderAndPadding : verticalBorderAndPadding;
        var crossBorderAndPadding = isRowDirection ? verticalBorderAndPadding : horizontalBorderAndPadding;
        // widths refer to the container's width, heights to its height
        var mainRelativeTo = isRowDirection ? relativeTo : heightRelativeTo;
        var crossRelativeTo = isRowDirection ? heightRelativeTo : relativeTo;
        var crossSize = ResolveFlexCrossSize(childStyle, isRowDirection, crossRelativeTo, crossBorderAndPadding);
        // `auto` (and keywords like `fit-content`) leave the cross size to the content or to stretching
        var hasDefiniteCrossSize = !float.IsNaN(ResolveAuthoredDimension(childStyle, isRowDirection ? "height" : "width", crossRelativeTo, float.NaN, crossBorderAndPadding));
        var mainProperty = isRowDirection ? "width" : "height";
        var specifiedMainSize = ResolveAuthoredDimension(childStyle, mainProperty, mainRelativeTo, float.NaN, mainBorderAndPadding);

        // The content size along the main axis: max-content (or the image's width) for rows, the
        // laid-out height for columns; the min-content width feeds the automatic minimum.
        var childTextStyle = ResolveTextStyle(childStyle, inheritedTextStyle);
        float contentMainSize;
        float contentMinimum;

        if (isRowDirection)
        {
            var intrinsic = TryResolveReplacedContentWidth(elementChild, childStyle, relativeTo, out var replacedWidth)
                ? new IntrinsicWidths(replacedWidth, replacedWidth)
                : ComputeContentIntrinsicWidths(elementChild, childTextStyle, relativeTo, context);
            contentMainSize = intrinsic.Max;
            contentMinimum = intrinsic.Min;
        }
        else
        {
            var measureWidth = crossSize > 0f ? crossSize + crossBorderAndPadding : relativeTo;
            contentMainSize = Math.Max(0f, MeasureBorderBoxHeight(elementChild, measureWidth, inheritedTextStyle, context) - mainBorderAndPadding);
            contentMinimum = contentMainSize;
        }

        // `flex-basis` when definite, else the main size property, else the content size.
        var baseMainSize = ResolveFlexBaseSize(childStyle, mainRelativeTo, mainBorderAndPadding);

        if (float.IsNaN(baseMainSize))
        {
            baseMainSize = float.IsNaN(specifiedMainSize) ? contentMainSize : specifiedMainSize;
        }

        var minProperty = isRowDirection ? "min-width" : "min-height";
        var maxMainSize = ResolveAuthoredDimension(childStyle, isRowDirection ? "max-width" : "max-height", mainRelativeTo, float.NaN, mainBorderAndPadding);
        var minRaw = childStyle.GetValueOrDefault(minProperty)?.Trim();
        float minMainSize;

        if (string.IsNullOrEmpty(minRaw) || string.Equals(minRaw, "auto", StringComparison.OrdinalIgnoreCase))
        {
            // `min-width: auto`: a flex item does not shrink below its content (or below its
            // specified size, when that is smaller); scroll containers have no such minimum.
            minMainSize = ShouldClipOverflow(childStyle)
                ? 0f
                : float.IsNaN(specifiedMainSize) ? contentMinimum : Math.Min(contentMinimum, specifiedMainSize);

            if (!float.IsNaN(maxMainSize))
            {
                minMainSize = Math.Min(minMainSize, maxMainSize);
            }
        }
        else
        {
            minMainSize = ResolveAuthoredDimension(childStyle, minProperty, mainRelativeTo, 0f, mainBorderAndPadding);
        }

        return new FlexItemLayoutInfo(
            child,
            childStyle,
            GetFlexOrder(childStyle),
            GetFlexGrow(childStyle),
            GetFlexShrink(childStyle),
            baseMainSize + mainBorderAndPadding,
            crossSize + crossBorderAndPadding,
            GetAlignSelf(childStyle, "auto"),
            minMainSize + mainBorderAndPadding,
            float.IsNaN(maxMainSize) ? float.PositiveInfinity : maxMainSize + mainBorderAndPadding,
            mainBorderAndPadding,
            hasDefiniteCrossSize,
            // margins refer to the container's width on both axes; auto ones are NaN
            ParseLength(childStyle, isRowDirection ? "margin-left" : "margin-top", relativeTo, isRowDirection ? childBox.Margin.Left : childBox.Margin.Top, allowAuto: true),
            ParseLength(childStyle, isRowDirection ? "margin-right" : "margin-bottom", relativeTo, isRowDirection ? childBox.Margin.Right : childBox.Margin.Bottom, allowAuto: true));
    }

    /// <summary>
    /// The definite flex basis (content-box), or NaN for <c>auto</c>/<c>content</c>.
    /// </summary>
    private static float ResolveFlexBaseSize(Dictionary<string, string> styleMap, float relativeTo, float mainAxisBorderAndPadding)
    {
        var flexBasisValue = styleMap.TryGetValue("flex-basis", out var longhand) && !string.IsNullOrWhiteSpace(longhand)
            ? longhand
            : "auto";

        if (flexBasisValue.Trim().ToLowerInvariant() is "auto" or "content")
        {
            return float.NaN;
        }

        var flexBasisMap = new Dictionary<string, string>(styleMap, StringComparer.OrdinalIgnoreCase)
        {
            ["flex-basis"] = flexBasisValue,
        };
        return ResolveAuthoredDimension(flexBasisMap, "flex-basis", relativeTo, float.NaN, mainAxisBorderAndPadding);
    }

    /// <summary>
    /// The CSS "resolve flexible lengths" algorithm for one flex line: the free space is shared by
    /// <c>flex-grow</c>, or taken by <c>flex-shrink</c> weighted with each item's base size, and
    /// items that hit their min or max size are frozen there while the rest is shared again.
    /// Sizes are border-box sizes.
    /// </summary>
    private static float[] ResolveFlexibleLengths(IReadOnlyList<FlexItemLayoutInfo> line, float availableSpace)
    {
        var count = line.Count;
        var targets = new float[count];
        var frozen = new bool[count];
        var growing = line.Sum(item => item.HypotheticalMainSize) < availableSpace;

        for (var index = 0; index < count; index++)
        {
            var item = line[index];
            var factor = growing ? item.FlexGrow : item.FlexShrink;

            if (factor <= 0f || (growing && item.BaseMainSize > item.HypotheticalMainSize) || (!growing && item.BaseMainSize < item.HypotheticalMainSize))
            {
                frozen[index] = true;
                targets[index] = item.HypotheticalMainSize;
            }
        }

        var initialFreeSpace = availableSpace - Enumerable.Range(0, count).Sum(index => frozen[index] ? targets[index] : line[index].BaseMainSize);

        for (var iteration = 0; iteration <= count && frozen.Contains(false); iteration++)
        {
            var freeSpace = availableSpace - Enumerable.Range(0, count).Sum(index => frozen[index] ? targets[index] : line[index].BaseMainSize);
            var factorSum = Enumerable.Range(0, count).Where(index => !frozen[index]).Sum(index => growing ? line[index].FlexGrow : line[index].FlexShrink);

            if (factorSum < 1f && Math.Abs(initialFreeSpace * factorSum) < Math.Abs(freeSpace))
            {
                freeSpace = initialFreeSpace * factorSum;
            }

            var scaledShrinkSum = Enumerable.Range(0, count).Where(index => !frozen[index])
                .Sum(index => line[index].FlexShrink * Math.Max(0f, line[index].BaseMainSize - line[index].MainBorderAndPadding));
            var totalViolation = 0f;
            var violations = new float[count];

            for (var index = 0; index < count; index++)
            {
                if (frozen[index])
                {
                    continue;
                }

                var item = line[index];
                var target = item.BaseMainSize;

                if (growing && factorSum > 0f)
                {
                    target += freeSpace * item.FlexGrow / factorSum;
                }
                else if (!growing && scaledShrinkSum > 0f)
                {
                    target -= Math.Abs(freeSpace) * item.FlexShrink * Math.Max(0f, item.BaseMainSize - item.MainBorderAndPadding) / scaledShrinkSum;
                }

                var clamped = Math.Max(item.MinMainSize, Math.Min(item.MaxMainSize, Math.Max(item.MainBorderAndPadding, target)));
                violations[index] = clamped - target;
                totalViolation += violations[index];
                targets[index] = clamped;
            }

            for (var index = 0; index < count; index++)
            {
                if (!frozen[index] && (Math.Abs(totalViolation) < 0.001f || (totalViolation > 0f && violations[index] > 0f) || (totalViolation < 0f && violations[index] < 0f)))
                {
                    frozen[index] = true;
                }
            }
        }

        return targets;
    }

    private static float ResolveFlexCrossSize(Dictionary<string, string> styleMap, bool isRowDirection, float relativeTo, float crossAxisBorderAndPadding)
    {
        var crossSize = isRowDirection
            ? ResolveAuthoredDimension(styleMap, "height", relativeTo, float.NaN, crossAxisBorderAndPadding)
            : ResolveAuthoredDimension(styleMap, "width", relativeTo, float.NaN, crossAxisBorderAndPadding);

        return float.IsNaN(crossSize) ? 0f : crossSize;
    }

    /// <summary>
    /// The content cross size of a flex item: its height at the given main size for rows, its
    /// fit-content width within <paramref name="crossAvailable"/> for columns (NaN when the item
    /// is stretched anyway, which skips the measurement).
    /// </summary>
    private static float EstimateFlexItemCrossSize(IRenderNode node, float mainSize, bool isRowDirection, RenderTextStyle inheritedTextStyle, LayoutContext context, float crossAvailable = float.NaN) => node switch
    {
        ElementRenderNode element when isRowDirection => MeasureBorderBoxHeight(element, mainSize, inheritedTextStyle, context),
        TextRenderNode text when isRowDirection => MeasureFlexItemTextHeight(PrepareFlexItemText(text, inheritedTextStyle, context), mainSize, inheritedTextStyle, context),
        _ when float.IsNaN(crossAvailable) => 0f,
        ElementRenderNode element => ResolveShrinkToFitBorderBoxWidth(element, crossAvailable, inheritedTextStyle, context, out _, out _),
        TextRenderNode text => MeasureFlexItemTextWidth(PrepareFlexItemText(text, inheritedTextStyle, context), crossAvailable, inheritedTextStyle, context),
        _ => 0f,
    };

    private static float MeasureFlexItemTextWidth(string text, float available, RenderTextStyle textStyle, LayoutContext context)
    {
        var widths = ComputeTextIntrinsicWidths(text, textStyle, context, out _);
        return Math.Min(widths.Max, Math.Max(widths.Min, available));
    }

    /// <summary>
    /// The text of an anonymous flex item, without the collapsible white space at its start and
    /// end (which would be removed at the start and end of its lines).
    /// </summary>
    private static string PrepareFlexItemText(TextRenderNode node, RenderTextStyle textStyle, LayoutContext context)
    {
        var text = ExpandTabs(context, ApplyTextTransform(NormalizeWhitespace(node.Ref.Data, textStyle.WhiteSpace), textStyle.TextTransform, previousEndsWord: false), textStyle);
        return textStyle.WhiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.Nowrap or WhiteSpaceMode.PreLine ? text.Trim(CollapsibleWhiteSpace) : text;
    }

    private static float MeasureFlexItemTextHeight(string text, float width, RenderTextStyle textStyle, LayoutContext context)
    {
        var lines = WrapTextRespectingWhiteSpace(context, text, width, textStyle, new HashSet<int>()).Count;
        var shownLines = textStyle.LineClamp > 0 ? Math.Min(lines, textStyle.LineClamp) : lines;
        return shownLines * textStyle.FontSize * textStyle.LineHeightMultiplier;
    }

    /// <summary>
    /// The border-box height of an element laid out at the given border-box width, by a trial
    /// layout, cached for the render (nested flex containers would otherwise multiply them).
    /// </summary>
    private static float MeasureBorderBoxHeight(ElementRenderNode element, float borderBoxWidth, RenderTextStyle inheritedTextStyle, LayoutContext context)
    {
        var key = (element.Ref, borderBoxWidth);

        if (!context.MeasuredHeights.TryGetValue(key, out var height))
        {
            height = TrialLayout(element, borderBoxWidth, inheritedTextStyle, context, borderBoxWidth, isAtomicInline: false).Metrics?.BorderBoxHeight ?? 0f;
            context.MeasuredHeights[key] = height;
        }

        return height;
    }

    /// <summary>
    /// Whether the computed `display` makes a box block-level. An unset `display` is CSS's initial
    /// value, `inline`: whatever an element is by default has to come from the (UA) stylesheet,
    /// never from its tag name.
    /// </summary>
    private static bool ShouldRenderAsBlock(ICssStyleDeclaration computedStyle)
    {
        var display = computedStyle.GetDisplay()?.Trim().ToLowerInvariant();

        return display switch
        {
            null or "" => false,
            "none" => false,
            "inline" => false,
            "inline-flex" => false,
            "inline-grid" => false,
            "inline-table" => false,
            "contents" => false,
            _ => true,
        };
    }

    private static bool IsInlineBlock(ICssStyleDeclaration computedStyle)
    {
        var display = computedStyle.GetDisplay();
        return string.Equals(display?.Trim(), "inline-block", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>visibility</c> (inherited): <c>hidden</c>, and <c>collapse</c> outside tables, keep the
    /// box in layout but paint nothing of it; a descendant may become visible again.
    /// </summary>
    private static bool ResolveIsHidden(Dictionary<string, string> styleMap, bool inherited) =>
        styleMap.TryGetValue("visibility", out var visibility) && !string.IsNullOrWhiteSpace(visibility)
            ? visibility.Trim().ToLowerInvariant() is "hidden" or "collapse"
            : inherited;

    /// <summary>
    /// Whether a node takes part in layout at all. AngleSharp.Css's <c>IsVisible</c> also rejects
    /// <c>visibility: hidden</c>, but a hidden box still occupies its space; it is only not
    /// painted (<see cref="RenderTextStyle.IsHidden"/>). The <c>hidden</c> attribute is the user
    /// agent stylesheet's <c>display: none</c>.
    /// </summary>
    private static bool GeneratesBoxes(IRenderNode node) =>
        node.Ref.Owner is not null &&
        (node is not ElementRenderNode element ||
         !string.Equals(element.ComputedStyle.GetDisplay()?.Trim(), "none", StringComparison.OrdinalIgnoreCase));

    private static RenderTextStyle ResolveTextStyle(Dictionary<string, string> styleMap, RenderTextStyle inherited)
    {
        var fontSize = ParseLength(styleMap, "font-size", inherited.FontSize, inherited.FontSize, allowAuto: false);
        var fontFamily = styleMap.TryGetValue("font-family", out var family) && !string.IsNullOrWhiteSpace(family)
            ? family.Trim('\'', '"', ' ')
            : inherited.FontFamily;

        var lineHeight = ParseLineHeight(styleMap, inherited.LineHeightMultiplier);
        var color = ParseColor(styleMap.TryGetValue("color", out var colorValue) ? colorValue : null, inherited.Color);
        var fontWeight = ParseFontWeight(styleMap, inherited.FontWeight);
        var isItalic = ParseFontStyle(styleMap, inherited.IsItalic);
        var (underline, strikeThrough) = ParseTextDecoration(styleMap, inherited.Underline, inherited.StrikeThrough);
        var decorationColor = ParseColor(styleMap.TryGetValue("text-decoration-color", out var decorationColorValue) ? decorationColorValue : null, color);
        var decorationStyle = ParseTextDecorationStyle(styleMap, inherited.DecorationStyle);
        var direction = ParseTextDirection(styleMap, inherited.Direction);
        var textAlign = ParseTextAlign(styleMap, inherited.TextAlign, direction);
        var letterSpacing = ParseLength(styleMap, "letter-spacing", inherited.FontSize, inherited.LetterSpacing, allowAuto: false);
        var textIndent = ParseLength(styleMap, "text-indent", inherited.FontSize, inherited.TextIndent, allowAuto: false);
        var verticalAlignOffset = ParseVerticalAlign(styleMap, fontSize);
        var textShadows = ParseTextShadows(styleMap.TryGetValue("text-shadow", out var textShadowValue) ? textShadowValue : null, inherited.TextShadows);
        var whiteSpace = ParseWhiteSpace(styleMap, inherited.WhiteSpace);
        var wordBreak = ParseWordBreak(styleMap, inherited.WordBreak);
        var overflowWrap = ParseOverflowWrap(styleMap, inherited.OverflowWrap);
        var textOverflow = ParseTextOverflow(styleMap);
        var lineClamp = ParseLineClamp(styleMap);
        var textTransform = ParseTextTransform(styleMap, inherited.TextTransform);
        var wordSpacing = ParseWordSpacing(styleMap, inherited.FontSize, inherited.WordSpacing);
        var textAlignLast = ParseTextAlignLast(styleMap, inherited.TextAlignLast, direction);
        var hyphens = ParseHyphens(styleMap, inherited.Hyphens);
        var tabSize = ParseTabSize(styleMap, fontSize, inherited.TabSize, inherited.TabSizeIsLength, out var tabSizeIsLength);
        var variantCaps = ParseFontVariantCaps(styleMap, inherited.VariantCaps);
        var fontStretch = ParseFontStretch(styleMap, inherited.FontStretch);

        return new RenderTextStyle(fontSize, color, fontFamily, lineHeight, fontWeight, isItalic, underline, strikeThrough, decorationColor, decorationStyle, textAlign, letterSpacing, textIndent, verticalAlignOffset, textShadows, whiteSpace, wordBreak, overflowWrap, textOverflow, lineClamp, direction, textTransform, wordSpacing, textAlignLast, hyphens, tabSize, tabSizeIsLength, variantCaps, fontStretch,
            IsHidden: ResolveIsHidden(styleMap, inherited.IsHidden),
            WritingMode: ParseWritingMode(styleMap.GetValueOrDefault("writing-mode"), inherited.WritingMode),
            TextOrientation: ParseTextOrientation(styleMap.GetValueOrDefault("text-orientation"), inherited.TextOrientation),
            // The system text is laid out in only changes at an orthogonal flow (LayoutElement).
            FlowMode: inherited.FlowMode);
    }

    /// <summary>
    /// `direction` (`ltr`/`rtl`) is inherited, the same as `white-space`/`word-break` above. It is
    /// the paragraph level for <see cref="BidiAlgorithm"/> and resolves `start`/`end` alignment;
    /// `unicode-bidi` on inline boxes is handled by <c>GetBidiControls</c>.
    /// </summary>
    private static TextDirection ParseTextDirection(Dictionary<string, string> styleMap, TextDirection inherited)
    {
        if (!styleMap.TryGetValue("direction", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return string.Equals(value.Trim(), "rtl", StringComparison.OrdinalIgnoreCase) ? TextDirection.Rtl : TextDirection.Ltr;
    }

    /// <summary>
    /// <c>word-break</c> is inherited, the same as <c>white-space</c> above.
    /// </summary>
    private static WordBreakMode ParseWordBreak(Dictionary<string, string> styleMap, WordBreakMode inherited)
    {
        if (!styleMap.TryGetValue("word-break", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return string.Equals(value.Trim(), "break-all", StringComparison.OrdinalIgnoreCase)
            ? WordBreakMode.BreakAll
            : WordBreakMode.Normal;
    }

    /// <summary>
    /// <c>overflow-wrap</c>, falling back to its legacy <c>word-wrap</c> alias when the modern
    /// property was not itself authored - both are inherited, the same as <c>white-space</c> above.
    /// </summary>
    private static OverflowWrapMode ParseOverflowWrap(Dictionary<string, string> styleMap, OverflowWrapMode inherited)
    {
        var raw = styleMap.TryGetValue("overflow-wrap", out var value) ? value : null;
        var legacyRaw = styleMap.TryGetValue("word-wrap", out var legacyValue) ? legacyValue : null;

        if (IsBreakingOverflowWrap(raw) || IsBreakingOverflowWrap(legacyRaw))
        {
            return OverflowWrapMode.BreakWord;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.IsNullOrWhiteSpace(legacyRaw) ? inherited : OverflowWrapMode.Normal;
        }

        return OverflowWrapMode.Normal;
    }

    private static bool IsBreakingOverflowWrap(string? value) =>
        value?.Trim().ToLowerInvariant() is "break-word" or "anywhere";

    /// <summary>
    /// <c>text-overflow</c>, unlike every other property resolved in <see cref="ResolveTextStyle"/>,
    /// is deliberately never inherited - every element re-derives it fresh from its own style map,
    /// defaulting to <see cref="TextOverflowMode.Clip"/> even when an ancestor set
    /// `text-overflow: ellipsis`. It also has no effect unless this element's own `overflow`
    /// clips (per spec, and matching <see cref="ShouldClipOverflow"/>'s existing "either axis"
    /// simplification) - `ellipsis` on a box that does not clip is simply ignored, same as a real
    /// browser.
    /// </summary>
    private static TextOverflowMode ParseTextOverflow(Dictionary<string, string> styleMap)
    {
        if (!ShouldClipOverflow(styleMap))
        {
            return TextOverflowMode.Clip;
        }

        return styleMap.TryGetValue("text-overflow", out var value) && string.Equals(value.Trim(), "ellipsis", StringComparison.OrdinalIgnoreCase)
            ? TextOverflowMode.Ellipsis
            : TextOverflowMode.Clip;
    }

    /// <summary>
    /// `-webkit-line-clamp: &lt;N&gt;` (a long-standing, still-`-webkit-`-prefixed-only de facto
    /// standard for multi-line truncation with an ellipsis on the final visible line - there is no
    /// unprefixed equivalent in wide use, and it is what `text-overflow: ellipsis` itself has no
    /// standard multi-line behavior without) - like `text-overflow`, never inherited (re-derived
    /// fresh per element) and only meaningful on a box that itself clips overflow; unlike
    /// `text-overflow`, this renderer does not additionally require `display: -webkit-box`/
    /// `-webkit-box-orient: vertical` (the other two properties real browsers also require) - a
    /// deliberate, documented simplification, since this renderer has no multi-column/flex-like
    /// `-webkit-box` layout mode of its own for those to meaningfully interact with; `overflow:
    /// hidden` plus a positive integer is treated as sufficient signal on its own. Returns 0
    /// (unclamped) when unset, non-positive, or not a plain integer.
    /// </summary>
    private static int ParseLineClamp(Dictionary<string, string> styleMap)
    {
        if (!ShouldClipOverflow(styleMap))
        {
            return 0;
        }

        if (!styleMap.TryGetValue("-webkit-line-clamp", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineClamp) && lineClamp > 0
            ? lineClamp
            : 0;
    }

    /// <summary>
    /// <c>white-space</c> is inherited (confirmed empirically - an unset value on a child reports
    /// empty, not the CSS initial <c>normal</c>, the same "never serialized when nothing in the
    /// cascade set it explicitly" behavior already documented for <c>list-style-type</c> - so
    /// <paramref name="inherited"/> is the correct fallback, not a hardcoded <c>Normal</c>).
    /// </summary>
    private static WhiteSpaceMode ParseWhiteSpace(Dictionary<string, string> styleMap, WhiteSpaceMode inherited)
    {
        if (!styleMap.TryGetValue("white-space", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "nowrap" => WhiteSpaceMode.Nowrap,
            "pre" => WhiteSpaceMode.Pre,
            "pre-wrap" => WhiteSpaceMode.PreWrap,
            "pre-line" => WhiteSpaceMode.PreLine,
            "break-spaces" => WhiteSpaceMode.BreakSpaces,
            _ => WhiteSpaceMode.Normal,
        };
    }

    private static float ParseVerticalAlign(Dictionary<string, string> styleMap, float fontSize)
    {
        if (!styleMap.TryGetValue("vertical-align", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 0f;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "super" => -fontSize * 1.5f,
            "sub" => fontSize * 0.8f,
            "middle" => -fontSize * 0.15f,
            "text-top" => -fontSize * 0.25f,
            "text-bottom" => fontSize * 0.1f,
            _ when ParseLengthValue(normalized, float.NaN, allowAuto: false) is var offset && !float.IsNaN(offset) => offset,
            _ => 0f,
        };
    }

    private static global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle ParseTextDecorationStyle(Dictionary<string, string> styleMap, global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle defaultValue)
    {
        if (!styleMap.TryGetValue("text-decoration-style", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "dashed" => global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Dashed,
            "dotted" => global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Dotted,
            "wavy" => global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Wavy,
            _ => global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle.Solid,
        };
    }

    private static TextAlign ParseTextAlign(Dictionary<string, string> styleMap, TextAlign defaultValue, TextDirection direction)
    {
        var startAlign = direction == TextDirection.Rtl ? TextAlign.Right : TextAlign.Left;
        var endAlign = direction == TextDirection.Rtl ? TextAlign.Left : TextAlign.Right;

        if (!styleMap.TryGetValue("text-align", out var value) || string.IsNullOrWhiteSpace(value))
        {
            // `text-align`'s own CSS initial value is `start`, which - unlike this map's other
            // properties - genuinely depends on `direction` rather than always meaning "left":
            // `defaultValue` here is really only ever `TextAlign.Left`, the hardcoded assumption
            // every call site made before `direction` was resolved at all, so it is superseded by
            // `startAlign` whenever this element's own resolved direction is `rtl`.
            return direction == TextDirection.Rtl ? startAlign : defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "center" => TextAlign.Center,
            "right" => TextAlign.Right,
            "end" => endAlign,
            "left" => TextAlign.Left,
            "start" => startAlign,
            "justify" => TextAlign.Justify,
            _ => defaultValue,
        };
    }

    /// <summary>
    /// `text-align-last` (inherited; `auto` = null, i.e. follow `text-align`, with `justify`
    /// falling back to start alignment). See <see cref="ResolveLineAlign"/>.
    /// </summary>
    private static TextAlign? ParseTextAlignLast(Dictionary<string, string> styleMap, TextAlign? inherited, TextDirection direction)
    {
        if (!styleMap.TryGetValue("text-align-last", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "center" => TextAlign.Center,
            "right" => TextAlign.Right,
            "left" => TextAlign.Left,
            "end" => direction == TextDirection.Rtl ? TextAlign.Left : TextAlign.Right,
            "start" => direction == TextDirection.Rtl ? TextAlign.Right : TextAlign.Left,
            "justify" => TextAlign.Justify,
            _ => null,
        };
    }

    /// <summary>
    /// The alignment of one line: the last line of a block (and every line ended by a forced
    /// break) follows `text-align-last`; `text-align: justify` aligns those to the start.
    /// </summary>
    private static TextAlign ResolveLineAlign(RenderTextStyle style, bool isLastLine)
    {
        if (!isLastLine)
        {
            return style.TextAlign;
        }

        if (style.TextAlignLast is { } last)
        {
            return last;
        }

        return ResolveStartIfJustify(style.TextAlign, style.Direction);
    }

    private static float ParseFontWeight(Dictionary<string, string> styleMap, float defaultValue)
    {
        if (!styleMap.TryGetValue("font-weight", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        return normalized switch
        {
            "normal" => 400f,
            "bold" => 700f,
            // Relative weights resolve against the inherited weight (CSS Fonts 4, 2.2.1).
            "bolder" => defaultValue < 350f ? 400f : defaultValue < 550f ? 700f : 900f,
            "lighter" => defaultValue < 100f ? defaultValue : defaultValue < 550f ? 100f : defaultValue < 750f ? 400f : 700f,
            _ when float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight) => weight,
            _ => defaultValue,
        };
    }

    private static bool ParseFontStyle(Dictionary<string, string> styleMap, bool defaultValue)
    {
        if (!styleMap.TryGetValue("font-style", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "italic" or "oblique";
    }

    private static (bool Underline, bool StrikeThrough) ParseTextDecoration(Dictionary<string, string> styleMap, bool defaultUnderline, bool defaultStrikeThrough)
    {
        var underline = defaultUnderline;
        var strikeThrough = defaultStrikeThrough;

        if (styleMap.TryGetValue("text-decoration-line", out var lineValue) || styleMap.TryGetValue("text-decoration", out lineValue))
        {
            var tokens = lineValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var token in tokens)
            {
                var normalized = token.Trim().ToLowerInvariant();

                if (normalized == "underline")
                {
                    underline = true;
                }
                else if (normalized is "line-through" or "strikethrough")
                {
                    strikeThrough = true;
                }
                else if (normalized == "none")
                {
                    underline = false;
                    strikeThrough = false;
                }
            }
        }

        return (underline, strikeThrough);
    }

    private static float ParseLineHeight(Dictionary<string, string> styleMap, float defaultValue)
    {
        if (!styleMap.TryGetValue("line-height", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();

        if (normalized.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(normalized[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100f;
        }

        if (float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var unitless))
        {
            return unitless;
        }

        return defaultValue;
    }

    private static void PrepareDocumentForRendering(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var element in document.All.OfType<IElement>())
        {
            var styleAttribute = element.GetAttribute("style");

            if (string.IsNullOrWhiteSpace(styleAttribute))
            {
                continue;
            }

            var currentStyle = styleAttribute;
            var changed = false;

            // a transform using custom properties stays: only the computed value has them substituted
            if (TryExtractTransformDeclaration(currentStyle, out var transformValue, out var updatedStyle) && UsesExplicitValue(transformValue))
            {
                currentStyle = updatedStyle;
                changed = true;
                element.SetAttribute("data-render-transform", transformValue);
            }

            if (changed)
            {
                element.SetAttribute("style", currentStyle);
            }
        }
    }

    /// <summary>
    /// Extracts a `transform` declaration out of an inline `style` attribute before AngleSharp.Css
    /// ever sees it (via a `data-render-transform` attribute this renderer reads back in
    /// `CreateStyleMap` instead) - a workaround for a genuine upstream crash, not an unsupported-
    /// value gap: AngleSharp.Css's own `CssTranslateValue.Compute()` throws a
    /// `NullReferenceException` - confirmed via a failing test with a minimal repro, not assumed -
    /// for *any* `translate`/`translateX`/`translateY` function, and that crash happens eagerly
    /// while building the render tree (`RenderTreeBuilder.RenderElement` computing the *entire*
    /// style declaration at once), before this renderer's own code ever runs. Extraction therefore
    /// has to happen unconditionally for every `transform` declaration - not only ones containing
    /// `translate` - both to keep this single code path simple and because relying on exactly
    /// which other functions are crash-free would be fragile against a future AngleSharp.Css
    /// version. `rotate()`/`scale()` were separately confirmed *not* to crash, but are extracted
    /// the same way regardless, for that same reason. `background-image` gradients used to need
    /// this identical extraction pattern too, until AngleSharp.Css's own `GradientParser` shipped
    /// and computed style started round-tripping the raw gradient text correctly - see the
    /// `filter`/gradient paragraphs in AGENTS.md for the general "each upstream gap gets its own
    /// fix, not a shared local workaround" policy this follows.
    /// </summary>
    private static bool TryExtractTransformDeclaration(string styleAttribute, out string transformValue, out string updatedStyle)
    {
        transformValue = string.Empty;
        updatedStyle = styleAttribute;

        if (!styleAttribute.Contains("transform", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var declarations = styleAttribute.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var remaining = new List<string>();

        foreach (var declaration in declarations)
        {
            var separator = declaration.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var property = declaration[..separator].Trim();
            var value = declaration[(separator + 1)..].Trim();

            // Exact match only - "transform" must not also swallow "transform-origin", which is
            // safe to leave for AngleSharp.Css's own (uncrashing) computation.
            if (string.Equals(property, "transform", StringComparison.OrdinalIgnoreCase))
            {
                transformValue = value;
                continue;
            }

            remaining.Add(declaration);
        }

        if (string.IsNullOrWhiteSpace(transformValue))
        {
            return false;
        }

        updatedStyle = string.Join(";", remaining);
        return true;
    }

    /// <summary>
    /// Resolves `background-image` from the cascaded but *uncomputed* declaration rather than
    /// <c>ComputeCurrentStyle()</c>'s normal computed value, a deliberate, narrow exception to how
    /// every other property in this map is read. AngleSharp.Css's `.Compute()` step eagerly
    /// resolves a `CssPoint2D`'s percentage/keyword components (a gradient's `at &lt;position&gt;`,
    /// and equally a radial gradient's explicit percentage size) into absolute pixels using
    /// whatever `IRenderDimensions` happens to be current at CSSOM-compute time - the *viewport*,
    /// not the element's own box, since a gradient's box is not a concept the general CSSOM compute
    /// pass has any way to know about. Confirmed empirically: a `radial-gradient(red, blue)` (no
    /// `at` clause at all - the default, and by far the most common way one is written) on a
    /// 200x100px box inside a 300x200px viewport computed to `at 150px 150px` - both axes resolved
    /// against the *viewport's* 300px width, not each axis against the box's own matching
    /// dimension, which would already be wrong even before <see cref="CssPoint2D"/>'s own separate
    /// `Compute()` bug (its `y` local is assigned from `_x.Compute(context)` instead of
    /// `_y.Compute(context)` - reported upstream) compounds it further. There is no way to recover
    /// the original percentage/keyword/position from that already-resolved-against-the-wrong-thing
    /// pixel text, so this renderer cannot use the computed value for `background-image` at all -
    /// `StyleCollectionExtensions.GetDeclarations` (`ComputeExplicitStyle` under the hood) gives the
    /// same cascaded, colour-normalized value with none of this resolution applied, matching exactly
    /// what a plain `style.GetPropertyValue("background-image")` gives for every other case
    /// (`url(...)`, or a gradient with only non-percentage arguments) where the two do not diverge.
    /// Falls back to the ordinary computed value if no window/device is available to build the
    /// style collection from (mirrors the same fallback <c>ComputeCurrentStyle()</c> itself uses
    /// internally when an element has no `Owner.DefaultView`).
    /// </summary>
    private static string ResolveExplicitBackgroundImage(IElement? element, ICssStyleDeclaration computedStyle) =>
        ResolveExplicitPropertyValue(element, computedStyle, "background-image");

    /// <summary>
    /// Reads <paramref name="propertyName"/> from the cascaded but *uncomputed* declaration rather
    /// than <c>ComputeCurrentStyle()</c>'s normal computed value - the same technique
    /// <see cref="ResolveExplicitBackgroundImage"/> established for `background-image`'s gradient
    /// percentages, reused here for the CSS Grid properties that hit the identical class of bug:
    /// `grid-template-columns`/`grid-template-rows` can hold percentage tracks, which AngleSharp.Css's
    /// `.Compute()` step eagerly resolves against whatever `IRenderDimensions` happens to be current
    /// at CSSOM-compute time (the *viewport*, confirmed empirically - `grid-template-rows: 30%` on an
    /// element with no defined height inside a 600x300 viewport computed to `180px`, i.e. 30% of the
    /// 600px *width*, not the 300px height a row percentage should track) rather than the grid
    /// container's own box, which is not a concept the general CSSOM compute pass has any way to
    /// know about - the same root cause `ResolveExplicitBackgroundImage`'s own remarks document in
    /// full. There is no way to recover the original percentage from that already-resolved-against-
    /// the-wrong-thing pixel text, so the computed value cannot be used for these properties at all.
    /// </summary>
    private static string ResolveExplicitPropertyValue(IElement? element, ICssStyleDeclaration computedStyle, string propertyName)
    {
        var window = element?.Owner?.DefaultView;

        if (window is not null)
        {
            var device = window.Document.Context.GetService<IRenderDevice>() ?? new DefaultRenderDevice();
            var styleCollection = window.GetStyleCollection(device);
            var explicitValue = styleCollection.GetDeclarations(element!).GetPropertyValue(propertyName);

            if (UsesExplicitValue(explicitValue))
            {
                return explicitValue;
            }
        }

        return computedStyle.GetPropertyValue(propertyName);
    }

    /// <summary>
    /// A single element's style map is rebuilt by <see cref="CreateStyleMap"/> more than once per
    /// render in several places - a flex/grid item's own placement/size estimation pass, an
    /// inline-block's line-flow-prediction pass, a margin-collapse lookahead at the first visible
    /// child, and <see cref="OrderChildrenForPainting"/>'s own z-index-bucketing pass all read the
    /// same element's style ahead of that same element's own, authoritative layout call later -
    /// confirmed, via <c>AngleSharp.Renderer.Benchmarks</c>, to be the single largest source of
    /// managed allocation in this renderer (~1MB per element laid out on a realistic page, almost
    /// entirely from <see cref="CreateStyleMap"/>'s own ~90 individual property reads run more
    /// than once for the same element). This wraps it with a cache keyed by element identity
    /// (<see cref="ReferenceEqualityComparer"/>, matching <see cref="LayoutCapture"/>'s own
    /// precedent for keying by <see cref="IElement"/>) so each element's style is actually computed
    /// only once per render, regardless of how many places along the way ask for it.
    ///
    /// Safe specifically because layout never mutates the DOM or a node's computed style while
    /// laying it out - nothing between two reads of the same element's style within one
    /// <see cref="BuildDisplayList(IDocument, IRenderDevice)"/>/<see cref="RenderToPng(IDocument, IRenderDevice)"/>
    /// call could ever make a second, differently-answered call actually necessary. Correctness
    /// across *separate* calls (the interactive `:hover`/`transition`/`animation`/caret-blink
    /// case, where the same document really can compute different style between one render and
    /// the next) relies entirely on <c>CreateLayoutContext</c> creating a brand new, empty cache
    /// every single call - never reusing one from a previous call - so this cache's own lifetime
    /// is never longer than the styles it caches remain valid for.
    /// </summary>
    private static Dictionary<string, string> GetOrCreateStyleMap(LayoutContext context, IElement? element, ICssStyleDeclaration style)
    {
        if (element is null)
        {
            return CreateStyleMap(style, element, context);
        }

        if (context.StyleMapCache.TryGetValue(element, out var cached))
        {
            return cached;
        }

        var map = CreateStyleMap(style, element, context);

        if (context.BoxSystems.TryGetValue(element, out var boxSystem))
        {
            RemapStyleMapToLogical(map, boxSystem);
        }

        context.StyleMapCache[element] = map;
        return map;
    }

    /// <summary>
    /// Resolves an element's cascaded-but-uncomputed declaration once (via the render-scoped
    /// <see cref="LayoutContext.StyleCollection"/>, itself built once per render - see
    /// <c>CreateLayoutContext</c>'s own remarks) so <see cref="CreateStyleMap"/> can read all of
    /// its several explicit-declaration properties (grid-template-columns/rows, the gap
    /// properties, grid-column/row, background-image) from that one object instead of calling
    /// <see cref="ResolveExplicitPropertyValue"/> - which used to re-run the full ancestor-cascade
    /// walk (<c>StyleCollectionExtensions.GetDeclarations</c>) completely independently for each
    /// property name - up to 8 times for the very same element. Returns <see langword="null"/>
    /// (falling all the way back to <see cref="ResolveExplicitPropertyValue"/>'s own per-call
    /// resolution in <see cref="CreateStyleMap"/>) whenever no context/style collection is
    /// available, exactly matching what that function already did for the same case.
    /// </summary>
    /// <summary>
    /// The cascaded declarations of the element, merged with the inheritable ones of its ancestors
    /// (<c>StyleCollectionExtensions.GetDeclarations</c>). The render tree already holds them as each
    /// node's specified style - computing them again runs the cascade for the element and every one
    /// of its ancestors, which made it the largest cost of laying out deep pages.
    /// </summary>
    private static ICssStyleDeclaration? GetExplicitDeclarations(LayoutContext context, IElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (context.SpecifiedStyles.TryGetValue(element, out var specifiedStyle))
        {
            return specifiedStyle;
        }

        return context.StyleCollection?.GetDeclarations(element);
    }

    private static void CollectSpecifiedStyles(IRenderNode node, LayoutContext context)
    {
        if (node is ElementRenderNode element)
        {
            context.SpecifiedStyles[element.Ref] = element.SpecifiedStyle;

            if (s_layoutPass.Value is { } pass)
            {
                pass.ComputedStyles[element.Ref] = element.ComputedStyle;
            }
        }

        foreach (var child in node.Children)
        {
            CollectSpecifiedStyles(child, context);
        }
    }

    /// <summary>
    /// The computed style of the element, taken from the render tree of the current layout pass.
    /// </summary>
    private static ICssStyleDeclaration ComputeStyle(IStyleCollection styleCollection, IElement element) =>
        s_layoutPass.Value is { } pass && pass.ComputedStyles.TryGetValue(element, out var computed)
            ? computed
            : styleCollection.ComputeDeclarations(element);

    private static string ReadExplicitOrComputed(ICssStyleDeclaration explicitDeclarations, ICssStyleDeclaration computedStyle, string propertyName)
    {
        var explicitValue = explicitDeclarations.GetPropertyValue(propertyName);
        return UsesExplicitValue(explicitValue) ? explicitValue : computedStyle.GetPropertyValue(propertyName);
    }

    /// <summary>
    /// A cascaded value is only usable as long as it does not depend on custom properties, which
    /// only the computed value has substituted (e.g. grid-template-columns: var(--columns)).
    /// </summary>
    private static bool UsesExplicitValue(string? explicitValue) =>
        !string.IsNullOrWhiteSpace(explicitValue) && explicitValue.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>
    /// Backs every element's style map. Behaves exactly like a plain
    /// <see cref="Dictionary{TKey, TValue}"/> of string to string for every existing consumer -
    /// it *is* one, via inheritance, so no call site anywhere else in this file needs to change -
    /// but additionally carries, for a handful of length-typed properties, the already-parsed
    /// <see cref="CssLengthValue"/> straight from AngleSharp.Css, letting <see cref="ParseLength"/>
    /// skip its own string tokenizing/re-parsing entirely for the common case. See
    /// <see cref="ParseLength"/>'s own remarks for why this is safe - the two code paths are
    /// provably computing the same thing from the same underlying value, just skipping the
    /// string round-trip in the middle for the fast one.
    /// </summary>
    private sealed class StyleMap : Dictionary<string, string>
    {
        private Dictionary<string, CssLengthValue>? _rawLengths;

        public StyleMap()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public void SetRawLength(string propertyName, CssLengthValue value) =>
            (_rawLengths ??= new Dictionary<string, CssLengthValue>(StringComparer.OrdinalIgnoreCase))[propertyName] = value;

        public bool TryGetRawLength(string propertyName, out CssLengthValue value)
        {
            if (_rawLengths is not null)
            {
                return _rawLengths.TryGetValue(propertyName, out value);
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Invalidates a captured raw length, if any - required whenever something overwrites this
        /// map's *string* entry for a property directly (bypassing <see cref="AddLengthProperty"/>),
        /// the way <see cref="ApplyActiveTransitionAndAnimationOverrides"/> does for a property
        /// that is mid-`transition`/`animation`. Without this, <see cref="ParseLength"/>'s fast
        /// path would keep answering from the stale, pre-override raw value instead of the freshly
        /// interpolated string - a real bug caught by
        /// <c>BuildDisplayList_HoverTransitionInterpolatesWidthAsANumericLength</c> failing before
        /// this call was added.
        /// </summary>
        public void ClearRawLength(string propertyName) => _rawLengths?.Remove(propertyName);
    }

    /// <summary>
    /// Reads a length-typed property the same way <c>AddIfPresent(map, name,
    /// style.GetXxx())</c> already did for each of these properties (same string stored, same
    /// `.CssText` cost for now - only <see cref="ParseLength"/>'s own re-parsing is skipped in
    /// this pass, not AngleSharp.Css's own serialization), but also captures the already-parsed
    /// <see cref="CssLengthValue"/> - when the property's raw value actually is one - into the
    /// map's fast-path side channel.
    /// </summary>
    private static void AddLengthProperty(StyleMap map, ICssStyleDeclaration style, string propertyName, ICssStyleDeclaration? explicitDeclarations = null)
    {
        // The explicit declaration is only needed for percentages (which `.Compute()` resolves
        // against the viewport); any other unit (`em`, `rem`, `vw`, ...) needs the computed value.
        var explicitProperty = explicitDeclarations?.GetProperty(propertyName);

        if (explicitProperty?.RawValue?.CssText is not { } explicitText || !explicitText.Contains('%') || !UsesExplicitValue(explicitText))
        {
            explicitDeclarations = null;
            explicitProperty = null;
        }

        var property = explicitProperty ?? style.GetProperty(propertyName);

        if (property?.RawValue is CssLengthValue length)
        {
            map.SetRawLength(propertyName, length);
        }

        var rawValue = property?.RawValue?.CssText;
        var explicitValue = explicitDeclarations?.GetPropertyValue(propertyName);
        AddIfPresent(map, propertyName, string.IsNullOrWhiteSpace(rawValue)
            ? string.IsNullOrWhiteSpace(explicitValue) ? property?.Value : explicitValue
            : rawValue);
    }

    private static Dictionary<string, string> CreateStyleMap(ICssStyleDeclaration style, IElement? element = null, LayoutContext? context = null)
    {
        var map = new StyleMap();
        var explicitDeclarations = context.HasValue ? GetExplicitDeclarations(context.Value, element) : null;
        var inlineStyle = element?.GetAttribute("style");

        var displayValue = style.GetDisplay();
        if (string.IsNullOrWhiteSpace(displayValue))
        {
            displayValue = ParseStyleAttributeValue(inlineStyle, "display");
        }

        // Only a grid/flex *container* itself can ever consult its own grid-template-columns/rows,
        // gap, or flex-direction/justify-content/align-items/flex-wrap/align-content - none of
        // those five flex properties or three grid/gap properties are ever read off anything but
        // the element that is itself display:grid/inline-grid or display:flex/inline-flex (see
        // LayoutGridContainer/LayoutFlexContainer's own property reads). Skipping the GetPropertyValue/
        // explicit-declaration calls for them entirely on the overwhelming majority of elements that
        // are neither avoids real, confirmed cost - each is its own CSSOM string-serialization call
        // (or, for the grid/gap trio, a read off the shared explicit-declaration object). grid-column/
        // grid-row/align-self/flex-grow/flex-shrink/flex-basis/order are deliberately NOT included in
        // this skip - those are *item*-level properties read off a child by its *parent's* container
        // layout, and a flex/grid item's own display is typically unset/block, not flex/grid, so
        // there is no cheap way to know from this element's own display alone whether some ancestor
        // will need them.
        var isGridContainer = string.Equals(displayValue, "grid", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(displayValue, "inline-grid", StringComparison.OrdinalIgnoreCase);
        var isFlexContainer = string.Equals(displayValue, "flex", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(displayValue, "inline-flex", StringComparison.OrdinalIgnoreCase);

        AddIfPresent(map, "display", displayValue);
        AddIfPresent(map, "visibility", style.GetVisibility());
        AddIfPresent(map, "writing-mode", style.GetPropertyValue("writing-mode"));
        AddIfPresent(map, "text-orientation", style.GetPropertyValue("text-orientation"));
        AddLengthProperty(map, style, "width", explicitDeclarations);
        AddLengthProperty(map, style, "height", explicitDeclarations);
        AddLengthProperty(map, style, "min-width", explicitDeclarations);
        AddLengthProperty(map, style, "max-width", explicitDeclarations);
        AddLengthProperty(map, style, "min-height", explicitDeclarations);
        AddLengthProperty(map, style, "max-height", explicitDeclarations);
        AddIfPresent(map, "box-sizing", style.GetBoxSizing());
        AddIfPresent(map, "object-fit", style.GetPropertyValue("object-fit"));
        AddIfPresent(map, "object-position", style.GetPropertyValue("object-position"));
        AddIfPresent(map, "clip-path", style.GetPropertyValue("clip-path"));
        var explicitAspectRatio = explicitDeclarations?.GetPropertyValue("aspect-ratio");
        AddIfPresent(map, "aspect-ratio", UsesExplicitValue(explicitAspectRatio)
            ? explicitAspectRatio
            : style.GetPropertyValue("aspect-ratio"));
        AddIfPresent(map, "position", style.GetPropertyValue("position"));
        AddLengthProperty(map, style, "left");
        AddLengthProperty(map, style, "top");
        AddLengthProperty(map, style, "right");
        AddLengthProperty(map, style, "bottom");
        AddIfPresent(map, "float", style.GetPropertyValue("float"));
        AddIfPresent(map, "clear", style.GetPropertyValue("clear"));
        AddIfPresent(map, "z-index", style.GetPropertyValue("z-index"));

        AddLengthProperty(map, style, "margin-top");
        AddLengthProperty(map, style, "margin-right");
        AddLengthProperty(map, style, "margin-bottom");
        AddLengthProperty(map, style, "margin-left");

        AddLengthProperty(map, style, "padding-top");
        AddLengthProperty(map, style, "padding-right");
        AddLengthProperty(map, style, "padding-bottom");
        AddLengthProperty(map, style, "padding-left");

        AddLengthProperty(map, style, "border-top-width");
        AddLengthProperty(map, style, "border-right-width");
        AddLengthProperty(map, style, "border-bottom-width");
        AddLengthProperty(map, style, "border-left-width");
        AddIfPresent(map, "border-collapse", style.GetPropertyValue("border-collapse"));
        AddIfPresent(map, "border-spacing", style.GetPropertyValue("border-spacing"));
        AddIfPresent(map, "table-layout", style.GetPropertyValue("table-layout"));
        AddIfPresent(map, "caption-side", style.GetPropertyValue("caption-side"));
        AddIfPresent(map, "empty-cells", style.GetPropertyValue("empty-cells"));

        AddIfPresent(map, "border-top-style", style.GetBorderTopStyle());
        AddIfPresent(map, "border-right-style", style.GetBorderRightStyle());
        AddIfPresent(map, "border-bottom-style", style.GetBorderBottomStyle());
        AddIfPresent(map, "border-left-style", style.GetBorderLeftStyle());

        AddIfPresent(map, "border-top-color", style.GetBorderTopColor());
        AddIfPresent(map, "border-right-color", style.GetBorderRightColor());
        AddIfPresent(map, "border-bottom-color", style.GetBorderBottomColor());
        AddIfPresent(map, "border-left-color", style.GetBorderLeftColor());

        AddIfPresent(map, "border-top-left-radius", style.GetBorderTopLeftRadius());
        AddIfPresent(map, "border-top-right-radius", style.GetBorderTopRightRadius());
        AddIfPresent(map, "border-bottom-right-radius", style.GetBorderBottomRightRadius());
        AddIfPresent(map, "border-bottom-left-radius", style.GetBorderBottomLeftRadius());

        AddIfPresent(map, "box-shadow", style.GetBoxShadow());
        AddIfPresent(map, "text-shadow", style.GetTextShadow());

        AddIfPresent(map, "list-style-type", style.GetPropertyValue("list-style-type"));
        AddIfPresent(map, "list-style-position", style.GetPropertyValue("list-style-position"));
        AddIfPresent(map, "counter-reset", style.GetPropertyValue("counter-reset"));
        AddIfPresent(map, "counter-increment", style.GetPropertyValue("counter-increment"));
        AddIfPresent(map, "counter-set", style.GetPropertyValue("counter-set"));

        AddIfPresent(map, "overflow-x", style.GetPropertyValue("overflow-x"));
        AddIfPresent(map, "overflow-y", style.GetPropertyValue("overflow-y"));

        AddIfPresent(map, "outline-width", style.GetPropertyValue("outline-width"));
        AddIfPresent(map, "outline-style", style.GetPropertyValue("outline-style"));
        AddIfPresent(map, "outline-color", style.GetPropertyValue("outline-color"));
        AddIfPresent(map, "outline-offset", style.GetPropertyValue("outline-offset"));

        AddIfPresent(map, "background-color", style.GetBackgroundColor());
        // grid-template-columns/rows, grid-column/row, and the gap properties are all read from
        // the explicit/cascaded declaration rather than computed style, for the same reason
        // `background-image` is (see ResolveExplicitPropertyValue's own remarks): a percentage
        // track/gap gets eagerly resolved against the wrong reference dimension by AngleSharp.Css's
        // `.Compute()` step. grid-column/grid-row additionally used to crash computed style
        // entirely for the common `<line> / span <n>` form (CssTupleValue<T>.Compute() calling
        // .Compute() on the omitted end line's null entry) - fixed upstream, but reading the
        // explicit declaration sidesteps that whole bug class regardless.
        if (isGridContainer)
        {
            var explicitGridTemplateColumns = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-template-columns") : ResolveExplicitPropertyValue(element, style, "grid-template-columns");
            AddIfPresent(map, "grid-template-columns", string.IsNullOrWhiteSpace(explicitGridTemplateColumns) ? ParseStyleAttributeValue(inlineStyle, "grid-template-columns") : explicitGridTemplateColumns);
            var explicitGridTemplateRows = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-template-rows") : ResolveExplicitPropertyValue(element, style, "grid-template-rows");
            AddIfPresent(map, "grid-template-rows", string.IsNullOrWhiteSpace(explicitGridTemplateRows) ? ParseStyleAttributeValue(inlineStyle, "grid-template-rows") : explicitGridTemplateRows);
            var explicitGridTemplateAreas = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-template-areas") : ResolveExplicitPropertyValue(element, style, "grid-template-areas");
            AddIfPresent(map, "grid-template-areas", string.IsNullOrWhiteSpace(explicitGridTemplateAreas) ? ParseStyleAttributeValue(inlineStyle, "grid-template-areas") : explicitGridTemplateAreas);
            AddIfPresent(map, "align-content", explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "align-content") : ResolveExplicitPropertyValue(element, style, "align-content"));
            var explicitGridAutoRows = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-auto-rows") : ResolveExplicitPropertyValue(element, style, "grid-auto-rows");
            AddIfPresent(map, "grid-auto-rows", string.IsNullOrWhiteSpace(explicitGridAutoRows) ? ParseStyleAttributeValue(inlineStyle, "grid-auto-rows") : explicitGridAutoRows);
            var explicitGridAutoFlow = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-auto-flow") : ResolveExplicitPropertyValue(element, style, "grid-auto-flow");
            AddIfPresent(map, "grid-auto-flow", string.IsNullOrWhiteSpace(explicitGridAutoFlow) ? ParseStyleAttributeValue(inlineStyle, "grid-auto-flow") : explicitGridAutoFlow);
            var explicitJustifyItems = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "justify-items") : ResolveExplicitPropertyValue(element, style, "justify-items");
            AddIfPresent(map, "justify-items", string.IsNullOrWhiteSpace(explicitJustifyItems) ? ParseStyleAttributeValue(inlineStyle, "justify-items") : explicitJustifyItems);
            AddIfPresent(map, "justify-content", explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "justify-content") : ResolveExplicitPropertyValue(element, style, "justify-content"));
            var explicitAlignItems = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "align-items") : ResolveExplicitPropertyValue(element, style, "align-items");
            AddIfPresent(map, "align-items", string.IsNullOrWhiteSpace(explicitAlignItems) ? ParseStyleAttributeValue(inlineStyle, "align-items") : explicitAlignItems);
        }

        if (isGridContainer || isFlexContainer)
        {
            var explicitColumnGap = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "column-gap") : ResolveExplicitPropertyValue(element, style, "column-gap");
            AddIfPresent(map, "column-gap", string.IsNullOrWhiteSpace(explicitColumnGap) ? ParseStyleAttributeValue(inlineStyle, "column-gap") : explicitColumnGap);
            var explicitRowGap = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "row-gap") : ResolveExplicitPropertyValue(element, style, "row-gap");
            AddIfPresent(map, "row-gap", string.IsNullOrWhiteSpace(explicitRowGap) ? ParseStyleAttributeValue(inlineStyle, "row-gap") : explicitRowGap);
            var explicitGap = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "gap") : ResolveExplicitPropertyValue(element, style, "gap");
            AddIfPresent(map, "gap", string.IsNullOrWhiteSpace(explicitGap) ? ParseStyleAttributeValue(inlineStyle, "gap") : explicitGap);
        }

        var explicitGridColumn = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-column") : ResolveExplicitPropertyValue(element, style, "grid-column");
        AddIfPresent(map, "grid-column", string.IsNullOrWhiteSpace(explicitGridColumn) ? ParseStyleAttributeValue(inlineStyle, "grid-column") : explicitGridColumn);
        var explicitGridRow = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-row") : ResolveExplicitPropertyValue(element, style, "grid-row");
        AddIfPresent(map, "grid-row", string.IsNullOrWhiteSpace(explicitGridRow) ? ParseStyleAttributeValue(inlineStyle, "grid-row") : explicitGridRow);
        var explicitGridArea = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "grid-area") : ResolveExplicitPropertyValue(element, style, "grid-area");
        AddIfPresent(map, "grid-area", string.IsNullOrWhiteSpace(explicitGridArea) ? ParseStyleAttributeValue(inlineStyle, "grid-area") : explicitGridArea);

        if (isFlexContainer)
        {
            AddIfPresent(map, "flex-direction", explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "flex-direction") : ResolveExplicitPropertyValue(element, style, "flex-direction"));
            AddIfPresent(map, "justify-content", explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "justify-content") : ResolveExplicitPropertyValue(element, style, "justify-content"));
            AddIfPresent(map, "align-items", explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "align-items") : ResolveExplicitPropertyValue(element, style, "align-items"));
            AddIfPresent(map, "flex-wrap", explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "flex-wrap") : ResolveExplicitPropertyValue(element, style, "flex-wrap"));
            AddIfPresent(map, "align-content", explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "align-content") : ResolveExplicitPropertyValue(element, style, "align-content"));
        }

        AddIfPresent(map, "align-self", string.IsNullOrWhiteSpace(style.GetPropertyValue("align-self")) ? ParseStyleAttributeValue(inlineStyle, "align-self") : style.GetPropertyValue("align-self"));
        AddIfPresent(map, "justify-self", string.IsNullOrWhiteSpace(style.GetPropertyValue("justify-self")) ? ParseStyleAttributeValue(inlineStyle, "justify-self") : style.GetPropertyValue("justify-self"));
        AddIfPresent(map, "flex-grow", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-grow")) ? ParseStyleAttributeValue(inlineStyle, "flex-grow") : style.GetPropertyValue("flex-grow"));
        AddIfPresent(map, "flex-shrink", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-shrink")) ? ParseStyleAttributeValue(inlineStyle, "flex-shrink") : style.GetPropertyValue("flex-shrink"));
        AddIfPresent(map, "flex-basis", string.IsNullOrWhiteSpace(style.GetPropertyValue("flex-basis")) ? ParseStyleAttributeValue(inlineStyle, "flex-basis") : style.GetPropertyValue("flex-basis"));
        AddIfPresent(map, "order", string.IsNullOrWhiteSpace(style.GetPropertyValue("order")) ? ParseStyleAttributeValue(inlineStyle, "order") : style.GetPropertyValue("order"));

        var resolvedBackgroundImage = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "background-image") : ResolveExplicitBackgroundImage(element, style);
        AddIfPresent(map, "background-image", string.IsNullOrWhiteSpace(resolvedBackgroundImage)
            ? ParseStyleAttributeValue(inlineStyle, "background-image")
            : resolvedBackgroundImage);

        AddIfPresent(map, "background-repeat", string.IsNullOrWhiteSpace(style.GetPropertyValue("background-repeat")) ? ParseStyleAttributeValue(inlineStyle, "background-repeat") : style.GetPropertyValue("background-repeat"));
        AddIfPresent(map, "background-position", string.IsNullOrWhiteSpace(style.GetPropertyValue("background-position")) ? ParseStyleAttributeValue(inlineStyle, "background-position") : style.GetPropertyValue("background-position"));
        AddIfPresent(map, "background-size", string.IsNullOrWhiteSpace(style.GetPropertyValue("background-size")) ? ParseStyleAttributeValue(inlineStyle, "background-size") : style.GetPropertyValue("background-size"));
        AddIfPresent(map, "background-clip", style.GetPropertyValue("background-clip"));
        AddIfPresent(map, "backdrop-filter", style.GetPropertyValue("backdrop-filter"));

        // Like `background-image`, image-valued sources are read from the cascaded declaration
        // (a gradient's percentages would otherwise be resolved against the viewport); the
        // companion properties only matter when there is an image at all.
        var borderImageSource = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "border-image-source") : style.GetPropertyValue("border-image-source");

        if (!string.IsNullOrWhiteSpace(borderImageSource) && !string.Equals(borderImageSource.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            AddIfPresent(map, "border-image-source", borderImageSource);
            AddIfPresent(map, "border-image-slice", style.GetPropertyValue("border-image-slice"));
            AddIfPresent(map, "border-image-width", style.GetPropertyValue("border-image-width"));
            AddIfPresent(map, "border-image-outset", style.GetPropertyValue("border-image-outset"));
            AddIfPresent(map, "border-image-repeat", style.GetPropertyValue("border-image-repeat"));
        }

        var maskImage = explicitDeclarations is not null ? ReadExplicitOrComputed(explicitDeclarations, style, "mask-image") : style.GetPropertyValue("mask-image");

        if (!string.IsNullOrWhiteSpace(maskImage) && !string.Equals(maskImage.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            AddIfPresent(map, "mask-image", maskImage);
            AddIfPresent(map, "mask-size", style.GetPropertyValue("mask-size"));
            AddIfPresent(map, "mask-position", style.GetPropertyValue("mask-position"));
            AddIfPresent(map, "mask-repeat", style.GetPropertyValue("mask-repeat"));
            AddIfPresent(map, "mask-mode", style.GetPropertyValue("mask-mode"));
            AddIfPresent(map, "mask-clip", style.GetPropertyValue("mask-clip"));
            AddIfPresent(map, "mask-origin", style.GetPropertyValue("mask-origin"));
        }
        AddIfPresent(map, "background-origin", style.GetPropertyValue("background-origin"));
        AddIfPresent(map, "background-blend-mode", style.GetPropertyValue("background-blend-mode"));
        AddIfPresent(map, "mix-blend-mode", style.GetPropertyValue("mix-blend-mode"));
        AddIfPresent(map, "isolation", style.GetPropertyValue("isolation"));
        AddIfPresent(map, "column-count", style.GetPropertyValue("column-count"));
        AddIfPresent(map, "column-width", style.GetPropertyValue("column-width"));

        // The rest of the multi-column properties only matter on a multi-column container, so
        // they are skipped (like the flex/grid container-only reads above) for everything else.
        if (map.ContainsKey("column-count") || map.ContainsKey("column-width"))
        {
            AddIfPresent(map, "column-rule-width", style.GetPropertyValue("column-rule-width"));
            AddIfPresent(map, "column-rule-style", style.GetPropertyValue("column-rule-style"));
            AddIfPresent(map, "column-rule-color", style.GetPropertyValue("column-rule-color"));

            if (!isGridContainer && !isFlexContainer)
            {
                AddIfPresent(map, "column-gap", style.GetPropertyValue("column-gap"));
            }
        }
        // Only read AngleSharp.Css's computed `transform` when the authored one uses custom
        // properties. Inline declarations are extracted to `data-render-transform` before style
        // computation; stylesheet declarations are read from the cascaded-but-uncomputed
        // declaration so rotate/scale/translate all retain their authored function list without
        // entering CssTranslateValue.Compute().
        var rawTransformValue = element?.GetAttribute("data-render-transform");
        if (string.IsNullOrWhiteSpace(rawTransformValue) && explicitDeclarations is not null)
        {
            rawTransformValue = explicitDeclarations.GetPropertyValue("transform");

            // e.g. transform: translateX(var(--offset)), which only the computed value has substituted
            if (!UsesExplicitValue(rawTransformValue) && !string.IsNullOrWhiteSpace(rawTransformValue))
            {
                rawTransformValue = style.GetPropertyValue("transform");
            }
        }

        AddIfPresent(map, "transform", rawTransformValue);
        AddIfPresent(map, "translate", style.GetPropertyValue("translate"));
        AddIfPresent(map, "rotate", style.GetPropertyValue("rotate"));
        AddIfPresent(map, "scale", style.GetPropertyValue("scale"));
        var explicitTransformOrigin = explicitDeclarations?.GetPropertyValue("transform-origin");
        AddIfPresent(map, "transform-origin", UsesExplicitValue(explicitTransformOrigin)
            ? explicitTransformOrigin
            : (string.IsNullOrWhiteSpace(style.GetPropertyValue("transform-origin")) ? ParseStyleAttributeValue(inlineStyle, "transform-origin") : style.GetPropertyValue("transform-origin")));
        // AngleSharp.Css now parses `filter` into a structured CssFilterValue and preserves it
        // through computed style (fixed upstream - it used to always report an empty string here),
        // so this can now read the ordinary computed-style value directly, the same as any other
        // property.
        AddIfPresent(map, "filter", style.GetPropertyValue("filter"));
        AddIfPresent(map, "opacity", style.GetOpacity());
        AddLengthProperty(map, style, "font-size");
        AddIfPresent(map, "font-family", style.GetFontFamily());
        AddIfPresent(map, "font-weight", style.GetPropertyValue("font-weight"));
        AddIfPresent(map, "font-style", style.GetPropertyValue("font-style"));
        AddIfPresent(map, "text-decoration", style.GetPropertyValue("text-decoration"));
        AddIfPresent(map, "text-decoration-line", style.GetPropertyValue("text-decoration-line"));
        AddIfPresent(map, "text-decoration-color", style.GetPropertyValue("text-decoration-color"));
        AddIfPresent(map, "text-decoration-style", style.GetPropertyValue("text-decoration-style"));
        AddIfPresent(map, "text-align", style.GetPropertyValue("text-align"));
        AddIfPresent(map, "text-align-last", style.GetPropertyValue("text-align-last"));
        AddIfPresent(map, "direction", style.GetPropertyValue("direction"));
        AddIfPresent(map, "unicode-bidi", style.GetPropertyValue("unicode-bidi"));
        AddLengthProperty(map, style, "text-indent");
        AddIfPresent(map, "vertical-align", style.GetVerticalAlign());
        AddLengthProperty(map, style, "letter-spacing");
        AddIfPresent(map, "word-spacing", style.GetPropertyValue("word-spacing"));
        AddIfPresent(map, "hyphens", style.GetPropertyValue("hyphens"));
        AddIfPresent(map, "tab-size", style.GetPropertyValue("tab-size"));
        AddIfPresent(map, "font-variant-caps", style.GetPropertyValue("font-variant-caps"));
        AddIfPresent(map, "font-stretch", style.GetPropertyValue("font-stretch"));
        AddIfPresent(map, "text-transform", style.GetPropertyValue("text-transform"));
        AddIfPresent(map, "line-height", style.GetLineHeight());
        AddIfPresent(map, "color", style.GetColor());
        AddIfPresent(map, "accent-color", style.GetPropertyValue("accent-color"));
        AddIfPresent(map, "white-space", style.GetPropertyValue("white-space"));
        AddIfPresent(map, "text-overflow", style.GetPropertyValue("text-overflow"));
        // Not a property AngleSharp.Css's own declaration factory necessarily recognizes (a
        // long-standing `-webkit-`-only vendor extension, never standardized under an unprefixed
        // name) - read via the same raw cascaded-declaration fallback `background-image`/gradients
        // already use for a related reason, so it works regardless of whether the computed style
        // pipeline itself understands the property.
        AddIfPresent(map, "-webkit-line-clamp", ResolveRawAuthoredPropertyValue(element, inlineStyle, "-webkit-line-clamp"));
        AddIfPresent(map, "word-break", style.GetPropertyValue("word-break"));
        var rawOverflowWrap = ResolveRawAuthoredPropertyValue(element, inlineStyle, "overflow-wrap");
        AddIfPresent(map, "overflow-wrap", !string.IsNullOrWhiteSpace(rawOverflowWrap)
            ? rawOverflowWrap
            : explicitDeclarations is not null
                ? ReadExplicitOrComputed(explicitDeclarations, style, "overflow-wrap")
                : style.GetPropertyValue("overflow-wrap"));
        AddIfPresent(map, "word-wrap", explicitDeclarations is not null
            ? ReadExplicitOrComputed(explicitDeclarations, style, "word-wrap")
            : style.GetPropertyValue("word-wrap"));

        ResolveCurrentColor(map);
        ApplyActiveTransitionAndAnimationOverrides(map, style, element);

        return map;
    }

    /// <summary>
    /// Replaces <c>currentcolor</c>, which AngleSharp.Css keeps as a keyword in computed values
    /// (e.g. MDN's icons: <c>background-color: currentcolor</c> with a mask), by the element's color.
    /// </summary>
    private static void ResolveCurrentColor(Dictionary<string, string> map)
    {
        if (!map.TryGetValue("color", out var color) || string.IsNullOrWhiteSpace(color) || IndexOfCurrentColor(color, 0) >= 0)
        {
            return;
        }

        foreach (var key in map.Keys.ToList())
        {
            var value = map[key];
            var index = IndexOfCurrentColor(value, 0);

            if (index < 0 || string.Equals(key, "color", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = new StringBuilder();
            var start = 0;

            while (index >= 0)
            {
                resolved.Append(value, start, index - start).Append(color);
                start = index + CurrentColorKeyword.Length;
                index = IndexOfCurrentColor(value, start);
            }

            map[key] = resolved.Append(value, start, value.Length - start).ToString();
        }
    }

    private const string CurrentColorKeyword = "currentcolor";

    private static int IndexOfCurrentColor(string value, int start)
    {
        var index = value.IndexOf(CurrentColorKeyword, start, StringComparison.OrdinalIgnoreCase);

        // only the keyword itself, not a part of another identifier
        while (index >= 0 && (IsIdentifierChar(value, index - 1) || IsIdentifierChar(value, index + CurrentColorKeyword.Length)))
        {
            index = value.IndexOf(CurrentColorKeyword, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return index;

        static bool IsIdentifierChar(string text, int position) =>
            position >= 0 && position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '-' or '_');
    }

    // Overrides every style-map entry that is currently mid-`transition` or mid-`animation` with
    // its interpolated value, so the rest of this renderer's normal per-property parsing
    // (ParseLength, ParseColor, ...) never has to know either is even happening - it just sees
    // whatever value would otherwise be in the map, substituted for the eased in-between one. A
    // no-op (and effectively free - one ConditionalWeakTable lookup) for the overwhelming majority
    // of documents, which were never wired up for interactive use via
    // IBrowsingContext.GetDomHarness() at all. `animation` takes precedence over `transition` for a
    // property both are currently affecting - a reasonable simplification of the CSS cascade's own
    // "animations, then transitions" ordering (transitions technically animate on top of whatever
    // the animation produces, which this renderer does not attempt to layer precisely).
    private static void ApplyActiveTransitionAndAnimationOverrides(Dictionary<string, string> map, ICssStyleDeclaration style, IElement? element)
    {
        if (element?.Owner is null || !element.Owner.Context.TryGetDomHarness(out var harness) || harness is null)
        {
            return;
        }

        // Looking up an animated value runs the element's cascade again (per property), so it is
        // only done for elements whose computed style declares an animation at all.
        var isAnimated = SplitTopLevelCommaList(style.GetPropertyValue("animation-name"))
            .Any(name => !string.IsNullOrWhiteSpace(name) && !name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase));

        // Iterates the full interpolatable whitelist, not just map.Keys - a property that only
        // ever appears inside a @keyframes block (never as a base/inherited declaration, e.g.
        // `opacity` set solely by an animation) would otherwise have no key in the map at all for
        // this loop to find and override.
        var styleMap = map as StyleMap;

        foreach (var property in CssValueInterpolation.InterpolatableProperties.Keys)
        {
            var animatedValue = isAnimated ? harness.GetAnimatedValue(element, property) : null;

            if (animatedValue is not null)
            {
                map[property] = animatedValue;
                styleMap?.ClearRawLength(property);
                continue;
            }

            var transitioningValue = harness.GetTransitioningValue(element, property);

            if (transitioningValue is not null)
            {
                map[property] = transitioningValue;
                styleMap?.ClearRawLength(property);
            }
        }
    }

    private static void AddIfPresent(Dictionary<string, string> map, string property, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            map[property] = value;
        }
    }

    private static string? ParseStyleAttributeValue(string? styleAttribute, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(styleAttribute))
        {
            return null;
        }

        foreach (var declaration in styleAttribute.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = declaration.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var candidateProperty = declaration[..separatorIndex].Trim();
            if (string.Equals(candidateProperty, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(separatorIndex + 1)..].Trim();
            }
        }

        return null;
    }

    private static string? ResolveRawAuthoredPropertyValue(IElement? element, string? inlineStyle, string propertyName)
    {
        var inlineValue = ParseStyleAttributeValue(inlineStyle, propertyName);
        if (!string.IsNullOrWhiteSpace(inlineValue))
        {
            return inlineValue;
        }

        if (element?.Owner is null)
        {
            return null;
        }

        string? matchedValue = null;

        foreach (var rule in GetAuthoredRules(element.Owner, propertyName))
        {
            foreach (var selector in rule.Selectors)
            {
                try
                {
                    if (element.Matches(selector))
                    {
                        matchedValue = rule.Value;
                        break;
                    }
                }
                catch
                {
                    // Ignore selectors unsupported by AngleSharp's matcher.
                }
            }
        }

        return matchedValue;
    }

    /// <summary>
    /// The rules of the document's `style` elements declaring the property, in source order.
    /// Scanning every style sheet's text for every element made pages with large inline style
    /// sheets take minutes to lay out, so the rules are collected once per layout pass.
    /// </summary>
    private static List<AuthoredRule> GetAuthoredRules(IDocument document, string propertyName)
    {
        var pass = s_layoutPass.Value;

        if (pass is not null && pass.AuthoredRules.TryGetValue((document, propertyName), out var cached))
        {
            return cached;
        }

        var rules = new List<AuthoredRule>();

        foreach (var styleElement in document.QuerySelectorAll("style"))
        {
            var source = styleElement.TextContent ?? string.Empty;
            var cursor = 0;

            while (cursor < source.Length)
            {
                var blockStart = source.IndexOf('{', cursor);
                if (blockStart < 0)
                {
                    break;
                }

                var blockEnd = source.IndexOf('}', blockStart + 1);
                if (blockEnd < 0)
                {
                    break;
                }

                var selectorText = source[cursor..blockStart].Trim();
                var declarations = source[(blockStart + 1)..blockEnd];
                var candidateValue = ParseStyleAttributeValue(declarations, propertyName);

                if (!selectorText.StartsWith('@') && !string.IsNullOrWhiteSpace(candidateValue))
                {
                    rules.Add(new AuthoredRule(selectorText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), candidateValue));
                }

                cursor = blockEnd + 1;
            }
        }

        pass?.AuthoredRules.Add((document, propertyName), rules);
        return rules;
    }

    private static bool TryGetFirstCollapsibleChildTopMargin(ElementRenderNode node, float containingWidth, LayoutContext context, out float marginTop)
    {
        foreach (var child in node.Children)
        {
            if (child is TextRenderNode textNode)
            {
                if (NormalizeWhitespace(textNode.Ref.Data).Length > 0)
                {
                    marginTop = 0f;
                    return false;
                }

                continue;
            }

            if (child is not ElementRenderNode childElement)
            {
                continue;
            }

            if (!GeneratesBoxes(childElement))
            {
                continue;
            }

            var childStyle = GetOrCreateStyleMap(context, childElement.Ref, childElement.ComputedStyle);
            var isFloat = IsFloated(childStyle);
            var isOutOfFlow = IsOutOfFlowPositioned(childStyle);

            // Floats and absolutely positioned boxes take no part in margin collapsing.
            if (isFloat || isOutOfFlow)
            {
                continue;
            }

            // Inline-level content (atomic inlines included) starts a line, which separates the margins.
            if (ClassifyFlowChild(childElement, context) != FlowChildKind.Block)
            {
                marginTop = 0f;
                return false;
            }

            marginTop = ParseLength(childStyle, "margin-top", containingWidth, 0f, allowAuto: false);

            // The child's margin itself collapses through to its own first child when nothing
            // separates them, so a whole chain of first children shares one margin.
            var childBox = ResolveBoxStyle(childStyle, childElement.Ref);

            if (childBox.BorderWidth.Top <= 0f &&
                ParseLength(childStyle, "padding-top", containingWidth, childBox.Padding.Top, allowAuto: false) <= 0f &&
                !EstablishesBlockFormattingContext(childStyle, isFloat, isOutOfFlow, isAtomicInline: false, isFlexItem: false) &&
                !IsOrthogonalFlow(childElement.Ref, context) &&
                TryGetFirstCollapsibleChildTopMargin(childElement, containingWidth, context, out var grandchildMarginTop))
            {
                marginTop = CollapseMargins(marginTop, grandchildMarginTop);
            }

            return true;
        }

        marginTop = 0f;
        return false;
    }

    private static IEnumerable<IRenderNode> OrderChildrenForPainting(IEnumerable<IRenderNode> children, LayoutContext context)
    {
        var negatives = new List<(IRenderNode Node, int Z, int Index)>();
        var flow = new List<(IRenderNode Node, int Index)>();
        var nonNegatives = new List<(IRenderNode Node, int Z, int Index)>();

        var index = 0;

        foreach (var child in children)
        {
            if (child is ElementRenderNode elementChild)
            {
                var childStyleMap = GetOrCreateStyleMap(context, elementChild.Ref, elementChild.ComputedStyle);

                if (IsOutOfFlowPositioned(childStyleMap) || HasExplicitZIndex(childStyleMap))
                {
                    var z = ParseZIndex(childStyleMap);

                    if (z < 0)
                    {
                        negatives.Add((child, z, index));
                    }
                    else
                    {
                        nonNegatives.Add((child, z, index));
                    }

                    index++;
                    continue;
                }
            }

            flow.Add((child, index));
            index++;
        }

        foreach (var entry in negatives.OrderBy(m => m.Z).ThenBy(m => m.Index))
        {
            yield return entry.Node;
        }

        foreach (var entry in flow.OrderBy(m => m.Index))
        {
            yield return entry.Node;
        }

        foreach (var entry in nonNegatives.OrderBy(m => m.Z).ThenBy(m => m.Index))
        {
            yield return entry.Node;
        }
    }

    /// <summary>
    /// The children in the order they are laid out: the ones painted below the in-flow content
    /// (negative z-index) first, all others in document order. Unlike with
    /// <see cref="OrderChildrenForPainting"/>, positioned children keep their place, which their
    /// static position (and the position of a z-indexed in-flow box) depends on; their commands
    /// are moved above the in-flow ones by <see cref="PaintOrder"/> once laid out.
    /// </summary>
    private static IEnumerable<IRenderNode> OrderChildrenForLayout(IEnumerable<IRenderNode> children, LayoutContext context)
    {
        var list = children as IReadOnlyCollection<IRenderNode> ?? children.ToList();
        var negatives = list.Where(child => IsNegativeZIndexChild(child, context)).ToList();

        foreach (var child in OrderChildrenForPainting(negatives, context))
        {
            yield return child;
        }

        foreach (var child in list)
        {
            if (!IsNegativeZIndexChild(child, context))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Moves the commands of the children painted above the in-flow content (positioned or
    /// z-indexed, with a z-index of at least 0) to the end, ordered by z-index and then by
    /// document order (CSS 2.2 Appendix E), once all siblings are laid out.
    /// </summary>
    private sealed class PaintOrder
    {
        private readonly List<(int Z, int Order, RenderCommand First, int Count)> _raised = [];

        public void Track(IRenderNode child, DisplayList displayList, int start, LayoutContext context)
        {
            var count = displayList.Commands.Count - start;

            if (count > 0 && child is ElementRenderNode element && !IsStickyElement(child, context))
            {
                var styleMap = GetOrCreateStyleMap(context, element.Ref, element.ComputedStyle);

                if ((IsOutOfFlowPositioned(styleMap) || HasExplicitZIndex(styleMap)) && ParseZIndex(styleMap) >= 0)
                {
                    _raised.Add((ParseZIndex(styleMap), _raised.Count, displayList.Commands[start], count));
                }
            }
        }

        public void Apply(DisplayList displayList)
        {
            foreach (var (_, _, first, count) in _raised.OrderBy(m => m.Z).ThenBy(m => m.Order))
            {
                var commands = displayList.Commands;

                for (var index = commands.Count - 1; index >= 0; index--)
                {
                    if (ReferenceEquals(commands[index], first))
                    {
                        displayList.MoveRangeToEnd(index, Math.Min(count, commands.Count - index));
                        break;
                    }
                }
            }
        }
    }

    private static bool IsOutOfFlowPositioned(Dictionary<string, string> styleMap)
    {
        var position = GetPosition(styleMap);
        return string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExplicitZIndex(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("z-index", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return !string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNegativeZIndexChild(IRenderNode child, LayoutContext context)
    {
        if (child is not ElementRenderNode elementChild)
        {
            return false;
        }

        var styleMap = GetOrCreateStyleMap(context, elementChild.Ref, elementChild.ComputedStyle);
        return HasExplicitZIndex(styleMap) && ParseZIndex(styleMap) < 0;
    }

    private static bool IsStickyPositioned(Dictionary<string, string> styleMap) =>
        string.Equals(GetPosition(styleMap), "sticky", StringComparison.OrdinalIgnoreCase);

    private static int ParseZIndex(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("z-index", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var normalized = value.Trim().ToLowerInvariant();

        if (string.Equals(normalized, "auto", StringComparison.Ordinal))
        {
            return 0;
        }

        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)
            ? z
            : 0;
    }

    private static BoxStyle ResolveBoxStyle(Dictionary<string, string> styleMap, IElement element)
    {
        var margin = new EdgeSizes(
            Top: ParseLength(styleMap, "margin-top", 0f, 0f, allowAuto: false),
            Right: ParseLength(styleMap, "margin-right", 0f, 0f, allowAuto: true),
            Bottom: ParseLength(styleMap, "margin-bottom", 0f, 0f, allowAuto: false),
            Left: ParseLength(styleMap, "margin-left", 0f, 0f, allowAuto: true));

        var padding = new EdgeSizes(
            Top: ParseLength(styleMap, "padding-top", 0f, 0f, allowAuto: false),
            Right: ParseLength(styleMap, "padding-right", 0f, 0f, allowAuto: false),
            Bottom: ParseLength(styleMap, "padding-bottom", 0f, 0f, allowAuto: false),
            Left: ParseLength(styleMap, "padding-left", 0f, 0f, allowAuto: false));

        var borderWidth = new EdgeSizes(
            Top: ParseLength(styleMap, "border-top-width", 0f, 0f, allowAuto: false),
            Right: ParseLength(styleMap, "border-right-width", 0f, 0f, allowAuto: false),
            Bottom: ParseLength(styleMap, "border-bottom-width", 0f, 0f, allowAuto: false),
            Left: ParseLength(styleMap, "border-left-width", 0f, 0f, allowAuto: false));

        var borderStyle = ResolveBorderStyles(styleMap);

        borderWidth = ApplyBorderStyleToWidths(borderWidth, borderStyle);

        var backgroundColor = ParseColor(styleMap.TryGetValue("background-color", out var background) ? background : null, RenderColor.Transparent);
        var backgroundLayers = ParseBackgroundLayers(styleMap, element);
        RenderPaint backgroundPaint = backgroundLayers.Count > 0 ? backgroundLayers[0].Paint : new RenderColorPaint(backgroundColor);
        var borderColor = new EdgeBorderColor(
            Top: ParseColor(styleMap.TryGetValue("border-top-color", out var topColor) ? topColor : null, RenderColor.Black),
            Right: ParseColor(styleMap.TryGetValue("border-right-color", out var rightColor) ? rightColor : null, RenderColor.Black),
            Bottom: ParseColor(styleMap.TryGetValue("border-bottom-color", out var bottomColor) ? bottomColor : null, RenderColor.Black),
            Left: ParseColor(styleMap.TryGetValue("border-left-color", out var leftColor) ? leftColor : null, RenderColor.Black));

        var borderRadius = ResolveBorderRadii(styleMap, float.NaN, float.NaN);

        var boxShadows = ParseBoxShadows(styleMap.TryGetValue("box-shadow", out var boxShadowValue) ? boxShadowValue : null);

        return new BoxStyle(margin, padding, borderWidth, borderStyle, backgroundPaint, borderColor, borderRadius, boxShadows, backgroundColor, backgroundLayers);
    }

    /// <summary>
    /// Parses a `box-shadow` value into its comma-separated layers, in CSS authoring order
    /// (first-listed shadow paints topmost among shadows). AngleSharp.Css's computed style
    /// normalizes every layer's color to `rgba(r, g, b, a)` regardless of how it was authored
    /// (`red`, `#f00`, ...), which <see cref="ExtractColorToken"/> relies on to split a layer's
    /// color from its lengths without being confused by the commas inside `rgba(...)`.
    /// </summary>
    private static IReadOnlyList<RenderBoxShadow> ParseBoxShadows(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var layers = SplitTopLevelCommaList(value);
        var shadows = new List<RenderBoxShadow>(layers.Length);

        foreach (var layer in layers)
        {
            if (ParseSingleBoxShadow(layer) is { } shadow)
            {
                shadows.Add(shadow);
            }
        }

        return shadows;
    }

    private static RenderBoxShadow? ParseSingleBoxShadow(string layer)
    {
        var trimmed = layer.Trim();
        var inset = false;

        if (trimmed.StartsWith("inset", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
        {
            inset = true;
            trimmed = trimmed[5..].TrimStart();
        }
        else if (trimmed.EndsWith("inset", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[^6])))
        {
            inset = true;
            trimmed = trimmed[..^5].TrimEnd();
        }

        var (colorToken, remainder) = ExtractColorToken(trimmed);
        var color = ParseColor(colorToken, RenderColor.Black);
        var tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 2)
        {
            return null;
        }

        var offsetX = ParseLengthValue(tokens[0], 0f, allowAuto: false);
        var offsetY = ParseLengthValue(tokens[1], 0f, allowAuto: false);
        var blurRadius = tokens.Length > 2 ? Math.Max(0f, ParseLengthValue(tokens[2], 0f, allowAuto: false)) : 0f;
        var spreadRadius = tokens.Length > 3 ? ParseLengthValue(tokens[3], 0f, allowAuto: false) : 0f;

        return new RenderBoxShadow(offsetX, offsetY, blurRadius, spreadRadius, color, inset);
    }

    /// <summary>
    /// Parses a `text-shadow` value into its comma-separated layers, in CSS authoring order
    /// (first-listed shadow paints topmost among shadows). Unlike `box-shadow`, a layer has no
    /// `inset` keyword and no spread component.
    /// </summary>
    private static IReadOnlyList<RenderTextShadow> ParseTextShadows(string? value, IReadOnlyList<RenderTextShadow> inherited)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return inherited;
        }

        if (string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var layers = SplitTopLevelCommaList(value);
        var shadows = new List<RenderTextShadow>(layers.Length);

        foreach (var layer in layers)
        {
            if (ParseSingleTextShadow(layer) is { } shadow)
            {
                shadows.Add(shadow);
            }
        }

        return shadows;
    }

    private static RenderTextShadow? ParseSingleTextShadow(string layer)
    {
        var (colorToken, remainder) = ExtractColorToken(layer.Trim());
        var color = ParseColor(colorToken, RenderColor.Black);
        var tokens = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 2)
        {
            return null;
        }

        var offsetX = ParseLengthValue(tokens[0], 0f, allowAuto: false);
        var offsetY = ParseLengthValue(tokens[1], 0f, allowAuto: false);
        var blurRadius = tokens.Length > 2 ? Math.Max(0f, ParseLengthValue(tokens[2], 0f, allowAuto: false)) : 0f;

        return new RenderTextShadow(offsetX, offsetY, blurRadius, color);
    }

    /// <summary>
    /// Splits a shadow layer's color from its offset/blur/spread lengths. AngleSharp.Css always
    /// normalizes a shadow's color to `rgba(r, g, b, a)`, so the color is located by its
    /// parenthesized function call rather than by naive whitespace splitting, which would
    /// otherwise be misled by the spaces after the commas inside `rgba(...)`. Falls back to
    /// treating the last whitespace-separated token as the color for any value that reaches this
    /// parser without going through AngleSharp.Css's own normalization.
    /// </summary>
    private static (string? ColorToken, string Remainder) ExtractColorToken(string value)
    {
        var functionStart = value.IndexOf("rgba(", StringComparison.OrdinalIgnoreCase);

        if (functionStart < 0)
        {
            var lastSpace = value.TrimEnd().LastIndexOf(' ');
            return lastSpace < 0
                ? (value.Length > 0 ? value.Trim() : null, string.Empty)
                : (value[(lastSpace + 1)..].Trim(), value[..lastSpace]);
        }

        var functionEnd = value.IndexOf(')', functionStart);

        if (functionEnd < 0)
        {
            return (value[functionStart..].Trim(), value[..functionStart]);
        }

        var colorToken = value[functionStart..(functionEnd + 1)];
        var remainder = value[..functionStart] + value[(functionEnd + 1)..];
        return (colorToken, remainder);
    }

    /// <summary>
    /// The four corner radii of a box. A percentage refers to the border box (horizontal against
    /// its width, vertical against its height), so it only resolves once the box size is known;
    /// before that (<see cref="float.NaN"/> sizes) it counts as zero.
    /// </summary>
    private static RenderCornerRadii ResolveBorderRadii(Dictionary<string, string> styleMap, float borderBoxWidth, float borderBoxHeight)
    {
        var (topLeftX, topLeftY) = ParseCornerRadius(styleMap, "border-top-left-radius", borderBoxWidth, borderBoxHeight);
        var (topRightX, topRightY) = ParseCornerRadius(styleMap, "border-top-right-radius", borderBoxWidth, borderBoxHeight);
        var (bottomRightX, bottomRightY) = ParseCornerRadius(styleMap, "border-bottom-right-radius", borderBoxWidth, borderBoxHeight);
        var (bottomLeftX, bottomLeftY) = ParseCornerRadius(styleMap, "border-bottom-left-radius", borderBoxWidth, borderBoxHeight);

        return new RenderCornerRadii(
            topLeftX, topLeftY,
            topRightX, topRightY,
            bottomRightX, bottomRightY,
            bottomLeftX, bottomLeftY);
    }

    /// <summary>
    /// Parses one corner's computed radius: one value (circular) or two (horizontal, then
    /// vertical, for an elliptical corner).
    /// </summary>
    private static (float X, float Y) ParseCornerRadius(Dictionary<string, string> styleMap, string propertyName, float borderBoxWidth, float borderBoxHeight)
    {
        if (!styleMap.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return (0f, 0f);
        }

        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return (0f, 0f);
        }

        var x = ParseRadiusComponent(parts[0], borderBoxWidth);
        var y = parts.Length > 1 ? ParseRadiusComponent(parts[1], borderBoxHeight) : ParseRadiusComponent(parts[0], borderBoxHeight);

        return (x, y);
    }

    private static float ParseRadiusComponent(string token, float reference)
    {
        if (token.EndsWith('%'))
        {
            return !float.IsNaN(reference) && float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage)
                ? Math.Max(0f, percentage / 100f * reference)
                : 0f;
        }

        return Math.Max(0f, ParseLengthValue(token, 0f, allowAuto: false));
    }

    private static EdgeBorderStyle ResolveBorderStyles(Dictionary<string, string> styleMap)
    {
        var top = styleMap.TryGetValue("border-top-style", out var topStyle)
            ? ParseBorderStyleToken(topStyle)
            : BorderStyleKind.Solid;
        var right = styleMap.TryGetValue("border-right-style", out var rightStyle)
            ? ParseBorderStyleToken(rightStyle)
            : BorderStyleKind.Solid;
        var bottom = styleMap.TryGetValue("border-bottom-style", out var bottomStyle)
            ? ParseBorderStyleToken(bottomStyle)
            : BorderStyleKind.Solid;
        var left = styleMap.TryGetValue("border-left-style", out var leftStyle)
            ? ParseBorderStyleToken(leftStyle)
            : BorderStyleKind.Solid;

        return new EdgeBorderStyle(top, right, bottom, left);
    }

    private static BorderStyleKind ParseBorderStyleToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BorderStyleKind.Solid;
        }

        var normalized = token.Trim().ToLowerInvariant();

        return normalized switch
        {
            "none" => BorderStyleKind.None,
            "hidden" => BorderStyleKind.Hidden,
            "dashed" => BorderStyleKind.Dashed,
            "dotted" => BorderStyleKind.Dotted,
            "double" => BorderStyleKind.Double,
            "groove" => BorderStyleKind.Groove,
            "ridge" => BorderStyleKind.Ridge,
            "inset" => BorderStyleKind.Inset,
            "outset" => BorderStyleKind.Outset,
            _ => BorderStyleKind.Solid,
        };
    }

    private static EdgeSizes ApplyBorderStyleToWidths(EdgeSizes widths, EdgeBorderStyle styles)
    {
        return widths with
        {
            Top = IsPaintedBorderStyle(styles.Top) ? widths.Top : 0f,
            Right = IsPaintedBorderStyle(styles.Right) ? widths.Right : 0f,
            Bottom = IsPaintedBorderStyle(styles.Bottom) ? widths.Bottom : 0f,
            Left = IsPaintedBorderStyle(styles.Left) ? widths.Left : 0f,
        };
    }

    private static bool IsPaintedBorderStyle(BorderStyleKind style) => style != BorderStyleKind.None && style != BorderStyleKind.Hidden;

    

    private static string GetPosition(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("position", out var position)
            ? position.Trim().ToLowerInvariant()
            : string.Empty;
    }

    private static string GetFloat(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("float", out var value)
            ? value.Trim().ToLowerInvariant()
            : string.Empty;
    }

    private static string GetClear(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("clear", out var value)
            ? value.Trim().ToLowerInvariant()
            : string.Empty;
    }

    /// <summary>
    /// The used content width of a float or an absolutely/fixed positioned box whose <c>width</c>
    /// is auto. Such a box shrinks to fit its content rather than filling its container, except
    /// that a positioned box with both <c>left</c> and <c>right</c> set stretches between them.
    /// </summary>
    private static float ResolveOutOfFlowAutoContentWidth(
        ElementRenderNode node,
        Dictionary<string, string> styleMap,
        bool isFixed,
        bool isAbsolute,
        float flowContainingWidth,
        float positionedContainingWidth,
        float horizontalBorderAndPadding,
        float marginLeft,
        float marginRight,
        RenderTextStyle inheritedTextStyle,
        LayoutContext context)
    {
        var available = flowContainingWidth;

        if (isAbsolute || isFixed)
        {
            var reference = isFixed ? Math.Max(0f, context.Width - (2f * context.Padding)) : positionedContainingWidth;
            var left = ParseLength(styleMap, "left", reference, float.NaN, allowAuto: true);
            var right = ParseLength(styleMap, "right", reference, float.NaN, allowAuto: true);

            if (!float.IsNaN(left) && !float.IsNaN(right))
            {
                var margins = (float.IsNaN(marginLeft) ? 0f : marginLeft) + (float.IsNaN(marginRight) ? 0f : marginRight);
                return Math.Max(0f, reference - left - right - margins - horizontalBorderAndPadding);
            }

            available = Math.Max(0f, reference - (float.IsNaN(left) ? 0f : left) - (float.IsNaN(right) ? 0f : right));
        }

        var borderBoxWidth = ResolveShrinkToFitBorderBoxWidth(node, available, inheritedTextStyle, context, out _, out _);
        return Math.Max(0f, borderBoxWidth - horizontalBorderAndPadding);
    }

    private static bool IsBorderBox(Dictionary<string, string> styleMap)
    {
        return styleMap.TryGetValue("box-sizing", out var value) &&
            string.Equals(value.Trim(), "border-box", StringComparison.OrdinalIgnoreCase);
    }

    private static void PaintOutline(DisplayList displayList, Dictionary<string, string> styleMap, float x, float y, float width, float height)
    {
        if (!styleMap.TryGetValue("outline-width", out var outlineWidthRaw) ||
            !styleMap.TryGetValue("outline-style", out var outlineStyleRaw))
        {
            return;
        }

        var style = ParseBorderStyleToken(outlineStyleRaw);

        if (!IsPaintedBorderStyle(style))
        {
            return;
        }

        var outlineWidth = ParseLengthValue(outlineWidthRaw, 0f, allowAuto: false);

        if (outlineWidth <= 0f)
        {
            return;
        }

        var color = ParseColor(
            styleMap.TryGetValue("outline-color", out var outlineColor) ? outlineColor : null,
            RenderColor.Black);

        // `outline-offset` moves the outline away from (or, when negative, into) the border box.
        // A negative offset can never shrink the outline's inner edge past the box's own center.
        var offset = styleMap.TryGetValue("outline-offset", out var outlineOffsetRaw)
            ? ParseLengthValue(outlineOffsetRaw, 0f, allowAuto: false)
            : 0f;

        if (float.IsNaN(offset))
        {
            offset = 0f;
        }

        offset = Math.Max(offset, -Math.Min(width, height) / 2f);
        var outset = outlineWidth + offset;

        PaintBorder(
            displayList,
            color,
            x - outset,
            y - outset,
            width + (2f * outset),
            height + (2f * outset),
            new EdgeSizes(outlineWidth, outlineWidth, outlineWidth, outlineWidth));
    }

    /// <summary>
    /// Whether the element's content is an image: `&lt;img&gt;`, `&lt;svg&gt;`, `input[type=image]`, or a
    /// pseudo-element whose `content` is a single `url()`. A pseudo-element proxies its host's tag
    /// name and attributes, so it is only ever judged by its generated content.
    /// </summary>
    private static bool IsReplacedElement(IElement element) => element is IPseudoElement
        ? s_generatedContentImages.TryGetValue(element, out _)
        : string.Equals(element.LocalName, "img", StringComparison.OrdinalIgnoreCase) ||
          string.Equals(element.LocalName, "svg", StringComparison.OrdinalIgnoreCase) ||
          (IsImageInput(element) && HasLoadableImageSource(element));

    private static bool HasLoadableImageSource(IElement element) =>
        TryGetOrLoadImageResource(element, element.GetAttribute("src"), out var imageResource) && imageResource is not null;

    private static bool IsImageInput(IElement element) =>
        element is not IPseudoElement &&
        string.Equals(element.LocalName, "input", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(element.GetAttribute("type")?.Trim(), "image", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveReplacedElementImage(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect, out RenderRect? clipRect) =>
        TryResolveImage(node, styleMap, containingWidth, x, y, out image, out rect, out clipRect) ||
        TryResolveInlineSvg(node, styleMap, containingWidth, x, y, out image, out rect, out clipRect);

    private static bool TryResolveImage(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect, out RenderRect? clipRect)
    {
        image = null;
        rect = default;
        clipRect = null;

        // node.Ref.LocalName/GetAttribute("src") proxy through to the host for a ::before/::after
        // pseudo-element (PseudoElement.cs, AngleSharp.Css), so a pseudo is only ever an image
        // through its own `content: url()`, never through its host's `src`.
        CachedImageResource? imageResource;

        if (node.Ref is IPseudoElement)
        {
            if (!s_generatedContentImages.TryGetValue(node.Ref, out var contentUrl) ||
                !TryGetOrLoadBackgroundImageResource(node.Ref, contentUrl, out imageResource) || imageResource is null)
            {
                return false;
            }
        }
        else if (!string.Equals(node.Ref.LocalName, "img", StringComparison.OrdinalIgnoreCase) && !IsImageInput(node.Ref))
        {
            return false;
        }
        else if (!TryGetOrLoadImageResource(node.Ref, node.Ref.GetAttribute("src"), out imageResource) || imageResource is null)
        {
            return false;
        }

        return TryResolveReplacedElementRect(imageResource, styleMap, containingWidth, x, y, out image, out rect, out clipRect);
    }

    private static bool TryResolveInlineSvg(ElementRenderNode node, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect, out RenderRect? clipRect)
    {
        image = null;
        rect = default;
        clipRect = null;

        // Same reasoning as the identical guard in TryResolveImage above - an <svg>'s own
        // generated-content pseudo aliases its host's LocalName and must not be treated as the SVG.
        if (node.Ref is IPseudoElement || !string.Equals(node.Ref.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryGetOrLoadInlineSvgResource(node.Ref, out var imageResource) || imageResource is null)
        {
            return false;
        }

        return TryResolveReplacedElementRect(imageResource, styleMap, containingWidth, x, y, out image, out rect, out clipRect);
    }

    private static bool TryResolveReplacedElementRect(CachedImageResource imageResource, Dictionary<string, string> styleMap, float containingWidth, float x, float y, out RenderedImage? image, out RenderRect rect, out RenderRect? clipRect)
    {
        image = null;
        rect = default;
        clipRect = null;

        var width = ParseLength(styleMap, "width", containingWidth, float.NaN, allowAuto: true);
        var height = ParseLength(styleMap, "height", containingWidth, float.NaN, allowAuto: true);

        var naturalWidth = imageResource.NaturalWidth;
        var naturalHeight = imageResource.NaturalHeight;

        if (float.IsNaN(width) && float.IsNaN(height))
        {
            width = naturalWidth;
            height = naturalHeight;
        }
        else if (float.IsNaN(width) && !float.IsNaN(height) && naturalWidth > 0f)
        {
            width = (height / naturalHeight) * naturalWidth;
        }
        else if (!float.IsNaN(width) && float.IsNaN(height) && naturalHeight > 0f)
        {
            height = (width / naturalWidth) * naturalHeight;
        }

        if (float.IsNaN(width) || float.IsNaN(height) || width <= 0f || height <= 0f)
        {
            width = naturalWidth;
            height = naturalHeight;
        }

        var boxRect = new RenderRect(x, y, width, height);
        var (paintRect, needsClip) = ResolveObjectFitRect(boxRect, naturalWidth, naturalHeight, styleMap);

        image = new RenderedImage(imageResource.Bytes, (int)Math.Max(1, Math.Round(width)), (int)Math.Max(1, Math.Round(height)), imageResource.MimeType);
        rect = paintRect;
        clipRect = needsClip ? boxRect : null;
        return true;
    }

    /// <summary>
    /// `object-fit`/`object-position` resolve a replaced element's own paint rect - where the
    /// natural image is actually drawn - separately from its layout box (`box`, already resolved
    /// from `width`/`height` the ordinary way). `fill` (the CSS default, and the only mode this
    /// renderer previously supported) stretches the image to exactly cover the box with no
    /// cropping/letterboxing, so it is left as a no-op fast path identical to the pre-existing
    /// behavior. The other four modes uniformly scale the natural image and reposition it per
    /// `object-position` (defaulting to centered, unlike `background-position`'s top-left
    /// default) - `contain` scales to the smaller axis (letterboxing, never cropping), `cover`
    /// scales to the larger axis (filling the box, cropping via the returned clip flag whenever
    /// the scaled image exceeds the box on either axis), `none` uses the natural size outright,
    /// and `scale-down` is `none` unless `contain` would end up smaller. The caller is expected to
    /// clip to `box` around the paint whenever this returns <see langword="true"/> - this method
    /// only decides *whether* clipping is needed, not how to perform it, keeping this renderer's
    /// existing `PushClip`/`PopClip` display-list primitives as the single place clip painting
    /// itself is implemented.
    /// </summary>
    private static (RenderRect PaintRect, bool NeedsClip) ResolveObjectFitRect(RenderRect box, float naturalWidth, float naturalHeight, Dictionary<string, string> styleMap)
    {
        var objectFit = styleMap.TryGetValue("object-fit", out var fitValue) ? fitValue.Trim().ToLowerInvariant() : "fill";

        if (objectFit is not ("contain" or "cover" or "none" or "scale-down") ||
            naturalWidth <= 0f || naturalHeight <= 0f || box.Width <= 0f || box.Height <= 0f)
        {
            return (box, false);
        }

        var containScale = Math.Min(box.Width / naturalWidth, box.Height / naturalHeight);
        var coverScale = Math.Max(box.Width / naturalWidth, box.Height / naturalHeight);

        var scale = objectFit switch
        {
            "contain" => containScale,
            "cover" => coverScale,
            "none" => 1f,
            "scale-down" => Math.Min(1f, containScale),
            _ => 1f,
        };

        var paintWidth = naturalWidth * scale;
        var paintHeight = naturalHeight * scale;

        var (positionXFraction, positionXOffset, positionYFraction, positionYOffset) = ParseObjectPosition(styleMap);

        var paintX = box.X + ((box.Width - paintWidth) * positionXFraction) + positionXOffset;
        var paintY = box.Y + ((box.Height - paintHeight) * positionYFraction) + positionYOffset;

        var paintRect = new RenderRect(paintX, paintY, paintWidth, paintHeight);
        var needsClip = paintRect.X < box.X - 0.01f ||
            paintRect.Y < box.Y - 0.01f ||
            paintRect.X + paintRect.Width > box.X + box.Width + 0.01f ||
            paintRect.Y + paintRect.Height > box.Y + box.Height + 0.01f;

        return (paintRect, needsClip);
    }

    /// <summary>
    /// Parses `object-position`, reusing `background-position`'s own single-axis keyword/
    /// percentage/length grammar (<see cref="ParsePositionComponent"/>) - the two properties share
    /// an identical grammar, differing only in their CSS initial value: `background-position`
    /// defaults to `0% 0%` (top-left) while `object-position` defaults to `50% 50%` (centered).
    /// </summary>
    private static (float PositionXFraction, float PositionXOffset, float PositionYFraction, float PositionYOffset) ParseObjectPosition(Dictionary<string, string> styleMap)
    {
        var center = new RenderBackgroundPositionComponent(0.5f, 0f);

        if (!styleMap.TryGetValue("object-position", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return (center.Percentage, center.OffsetPixels, center.Percentage, center.OffsetPixels);
        }

        var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (center.Percentage, center.OffsetPixels, center.Percentage, center.OffsetPixels);
        }

        RenderBackgroundPositionComponent xComponent;
        RenderBackgroundPositionComponent yComponent;

        if (tokens.Length == 1)
        {
            var only = tokens[0].ToLowerInvariant();

            if (only is "top" or "bottom")
            {
                xComponent = center;
                yComponent = ParsePositionComponent(only);
            }
            else
            {
                xComponent = ParsePositionComponent(only);
                yComponent = center;
            }
        }
        else
        {
            xComponent = ParsePositionComponent(tokens[0]);
            yComponent = ParsePositionComponent(tokens[1]);
        }

        return (xComponent.Percentage, xComponent.OffsetPixels, yComponent.Percentage, yComponent.OffsetPixels);
    }

    private static bool TryGetOrLoadInlineSvgResource(IElement element, out CachedImageResource? imageResource)
    {
        var pass = s_layoutPass.Value;
        SvgPageStyle? pageStyle;
        string fingerprint;

        if (pass is not null && pass.Styles.TryGetValue(element, out var resolved))
        {
            (pageStyle, fingerprint) = resolved;
        }
        else
        {
            pageStyle = ResolveSvgPageStyle(element);
            fingerprint = BuildInlineSvgFingerprint(element, pageStyle);
            pass?.Styles.Add(element, (pageStyle, fingerprint));
        }

        if (s_inlineSvgCacheByElement.TryGetValue(element, out var cached) && cached.Fingerprint == fingerprint)
        {
            imageResource = cached.Resource;
            return true;
        }

        // The already-parsed element is walked directly - AngleSharp parsed this SVG once, as
        // part of the host document, and it is never serialized back to text and re-parsed.
        imageResource = null;

        if (!SvgRasterizer.TryRasterizeElement(element, out var pngBytes, out var naturalWidth, out var naturalHeight, pageStyle))
        {
            return false;
        }

        imageResource = new CachedImageResource(pngBytes, "image/png", naturalWidth, naturalHeight);
        s_inlineSvgCacheByElement.AddOrUpdate(element, new InlineSvgCacheEntry(fingerprint, imageResource));
        return true;
    }

    /// <summary>
    /// How the host page styles an inline SVG: the root's computed values of the inherited SVG
    /// properties (everything but `opacity`), and each element's cascaded page declarations of the
    /// properties the SVG renderer reads.
    /// </summary>
    private static SvgPageStyle? ResolveSvgPageStyle(IElement svgRoot)
    {
        var window = svgRoot.Owner?.DefaultView;

        if (window is null)
        {
            return null;
        }

        var styleCollection = GetSvgStyleCollection(window);
        // the same as ComputeCurrentStyle, which would gather (and index) all style rules once more
        var rootStyle = ComputeStyle(styleCollection, svgRoot);
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in SvgPaintState.PresentationProperties)
        {
            var value = property == "color" ? rootStyle.GetColor() : rootStyle.GetPropertyValue(property);

            if (property != "opacity" && !string.IsNullOrWhiteSpace(value))
            {
                inherited[property] = value;
            }
        }

        var declarations = new Dictionary<IElement, IReadOnlyDictionary<string, string>>(ReferenceEqualityComparer.Instance);

        foreach (var element in svgRoot.DescendantsAndSelf<IElement>())
        {
            // Only what the page declares on the element itself: inherited values must not beat
            // the element's own presentation attributes (GetDeclarations merges in the ancestors).
            var cascaded = styleCollection.ComputeExplicitStyle(element);
            Dictionary<string, string>? values = null;

            foreach (var property in SvgPaintState.PresentationProperties)
            {
                var value = cascaded.GetPropertyValue(property);

                // custom properties are only substituted in the computed value, e.g. fill: var(--text)
                if (!UsesExplicitValue(value) && !string.IsNullOrWhiteSpace(value))
                {
                    value = ComputeStyle(styleCollection, element).GetPropertyValue(property);
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    (values ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[property] = value;
                }
            }

            if (values is not null)
            {
                declarations[element] = values;
            }
        }

        return new SvgPageStyle(inherited, declarations);
    }

    private static IStyleCollection GetSvgStyleCollection(IWindow window)
    {
        var pass = s_layoutPass.Value;

        if (pass is null || !pass.StyleCollections.TryGetValue(window, out var styleCollection))
        {
            var device = window.Document.Context.GetService<IRenderDevice>() ?? new DefaultRenderDevice();
            styleCollection = window.GetStyleCollection(device);
            pass?.StyleCollections.Add(window, styleCollection);
        }

        return styleCollection;
    }

    private static string BuildInlineSvgFingerprint(IElement svgRoot, SvgPageStyle? pageStyle)
    {
        var builder = new StringBuilder(svgRoot.OuterHtml);

        if (pageStyle is not null)
        {
            foreach (var (property, value) in pageStyle.Inherited.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                builder.Append('\n').Append(property).Append(':').Append(value);
            }

            var index = 0;

            foreach (var element in svgRoot.DescendantsAndSelf<IElement>())
            {
                if (pageStyle.Declarations.TryGetValue(element, out var values))
                {
                    builder.Append('\n').Append(index);

                    foreach (var (property, value) in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                    {
                        builder.Append(';').Append(property).Append(':').Append(value);
                    }
                }

                index++;
            }
        }

        return builder.ToString();
    }

    private static bool TryGetOrLoadImageResource(IElement element, string? source, out CachedImageResource? imageResource)
    {
        imageResource = null;

        return !string.IsNullOrWhiteSpace(source) &&
               TryGetOrLoadCachedResource(element, source.Trim(), TryLoadImageResource, out imageResource);
    }

    /// <summary>
    /// Resolves a `background-image: url(...)` reference to a decoded, cached image, sharing the
    /// same per-document <see cref="DocumentImageCache"/> (keyed by the same trimmed URL string) as
    /// an `&lt;img src&gt;` reference to the exact same URL - one fetch serves both. Unlike
    /// <see cref="TryGetOrLoadImageResource"/>, there is no <see cref="ILoadableElement"/> download
    /// already in flight to consult first: a CSS property value has no DOM-level load of its own,
    /// so <see cref="TryLoadBackgroundImageResource"/> always resolves the URL itself (data URI, or
    /// the network - and only the network - when the browsing context has an
    /// <see cref="IDocumentLoader"/> configured), mirroring how <c>@font-face url()</c> sources are
    /// handled in <c>FontFaceLoader</c>.
    /// </summary>
    private static bool TryGetOrLoadBackgroundImageResource(IElement element, string url, out CachedImageResource? imageResource) =>
        TryGetOrLoadCachedResource(element, url.Trim(), TryLoadBackgroundImageResource, out imageResource);

    private delegate bool ImageResourceLoader(IElement element, string source, out CachedImageResource? imageResource);

    private static bool TryGetOrLoadCachedResource(IElement element, string cacheKey, ImageResourceLoader loader, out CachedImageResource? imageResource)
    {
        imageResource = null;
        var cache = GetImageCache(element);

        if (cache is not null)
        {
            lock (cache.Resources)
            {
                if (cache.Resources.TryGetValue(cacheKey, out imageResource))
                {
                    return imageResource is not null;
                }
            }
        }

        var loaded = loader(element, cacheKey, out imageResource);

        if (cache is not null)
        {
            lock (cache.Resources)
            {
                cache.Resources[cacheKey] = loaded ? imageResource : null;
            }
        }

        return loaded;
    }

    private static DocumentImageCache? GetImageCache(IElement element)
    {
        var owner = element.Owner;
        return owner is null ? null : s_imageCacheByDocument.GetValue(owner, static _ => new DocumentImageCache());
    }

    private static bool TryLoadImageResource(IElement element, string? source, out CachedImageResource? imageResource)
    {
        imageResource = null;

        byte[]? bytes = null;
        string? mimeType = null;

        if (element is ILoadableElement loadableElement && loadableElement.CurrentDownload is { Task: not null } download)
        {
            IResponse? response;

            try
            {
                response = download.Task.GetAwaiter().GetResult();
            }
            catch
            {
                response = null;
            }

            // AngleSharp disposes the response once it has been processed; WithRendering()'s image
            // service keeps its bytes. Without that service the stream may already be closed, and
            // the image then counts as missing rather than failing the whole paint.
            if (response is not null && ImageResourceService.TryGetCaptured(response, out var capturedBytes, out var capturedMimeType))
            {
                bytes = capturedBytes;
                mimeType = capturedMimeType;
            }
            else if (response?.Content is not null)
            {
                try
                {
                    using var sourceStream = response.Content;
                    using var memoryStream = new MemoryStream();
                    sourceStream.CopyTo(memoryStream);
                    bytes = memoryStream.ToArray();
                    mimeType = response.Headers?.TryGetValue("Content-Type", out var contentType) == true && !string.IsNullOrWhiteSpace(contentType)
                        ? contentType
                        : "image/unknown";
                }
                catch (Exception exception) when (exception is ObjectDisposedException or NotSupportedException or IOException)
                {
                    bytes = null;
                }
            }
        }

        if (bytes is null && TryParseDataUri(source, out var dataUriBytes, out var dataUriMimeType))
        {
            bytes = dataUriBytes;
            mimeType = dataUriMimeType;
        }

        return bytes is not null && TryDecodeImageBytes(bytes, mimeType, out imageResource);
    }

    /// <summary>
    /// Resolves a `background-image: url(...)` value: a data URI decodes inline, exactly like an
    /// `&lt;img src&gt;` data URI; anything else is only ever fetched when the browsing context was
    /// configured with an <see cref="IDocumentLoader"/> - matching how images and `@font-face`
    /// sources are already handled, a renderer should not silently reach out to the network. The
    /// fetch is synchronous (blocking on the download's Task) so the resource is
    /// available - loaded and decoded - within the very same <c>BuildDisplayList</c> call that
    /// requested it, the same way an `&lt;img&gt;`'s already-in-flight <c>CurrentDownload</c> is
    /// awaited: this renderer has no separate "repaint later once loaded" pass to defer to.
    /// </summary>
    private static bool TryLoadBackgroundImageResource(IElement element, string url, out CachedImageResource? imageResource)
    {
        imageResource = null;

        if (TryParseDataUri(url, out var dataUriBytes, out var dataUriMimeType))
        {
            return dataUriBytes is not null && TryDecodeImageBytes(dataUriBytes, dataUriMimeType, out imageResource);
        }

        var document = element.Owner;
        var loader = document?.Context.GetService<IDocumentLoader>();

        if (document is null || loader is null)
        {
            return false;
        }

        try
        {
            var target = new Url(document.BaseUrl, url);
            var download = loader.FetchAsync(DocumentRequest.Get(target, source: document, referer: document.BaseUri));
            var response = download.Task.GetAwaiter().GetResult();

            if (response?.Content is null)
            {
                return false;
            }

            using var content = response.Content;
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();

            if (bytes.Length == 0)
            {
                return false;
            }

            var mimeType = response.Headers?.TryGetValue("Content-Type", out var contentType) == true && !string.IsNullOrWhiteSpace(contentType)
                ? contentType
                : "image/unknown";

            return TryDecodeImageBytes(bytes, mimeType, out imageResource);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeImageBytes(byte[] bytes, string? mimeType, out CachedImageResource? imageResource)
    {
        imageResource = null;

        if (bytes.Length == 0)
        {
            return false;
        }

        if (SvgRasterizer.IsSvg(bytes))
        {
            if (!SvgRasterizer.TryRasterizeMarkup(bytes, out var rasterizedBytes, out var svgNaturalWidth, out var svgNaturalHeight))
            {
                return false;
            }

            imageResource = new CachedImageResource(rasterizedBytes, "image/png", svgNaturalWidth, svgNaturalHeight);
            return true;
        }

        using var skImage = SKImage.FromEncodedData(bytes);
        if (skImage is null)
        {
            return false;
        }

        imageResource = new CachedImageResource(bytes, mimeType ?? "image/unknown", skImage.Width, skImage.Height);
        return true;
    }

    private static bool TryParseDataUri(string? source, out byte[]? bytes, out string? mimeType)
    {
        bytes = null;
        mimeType = null;

        if (string.IsNullOrWhiteSpace(source) || !source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = source.IndexOf(',');
        if (commaIndex <= 5)
        {
            return false;
        }

        var header = source[5..commaIndex];
        var isBase64 = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var mediaType = isBase64 ? header[..^7] : header;
        var payload = source[(commaIndex + 1)..];

        if (string.IsNullOrEmpty(mediaType))
        {
            mimeType = "image/unknown";
        }
        else if (mediaType.StartsWith(";", StringComparison.Ordinal))
        {
            mimeType = "image/unknown";
        }
        else
        {
            mimeType = mediaType;
        }

        try
        {
            bytes = isBase64
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch
        {
            bytes = null;
            return false;
        }

        return bytes.Length > 0;
    }

    /// <summary>
    /// Identifies which kind of default styling/content-painting a form control needs. `None`
    /// covers every non-form element, so callers can skip all of the form-control machinery with a
    /// single check. `Hidden` (`input[type=hidden]`) never lays out or paints at all - handled by
    /// an early return in <c>LayoutElement</c> rather than here, since AngleSharp.Css's UA
    /// stylesheet does not give it `display: none` the way a real browser's does (every `&lt;input&gt;`
    /// type computes to plain `inline-block` in this AngleSharp.Css version, verified empirically).
    /// </summary>
    private enum FormControlKind
    {
        /// <summary>Not a form control.</summary>
        None,

        /// <summary>
        /// A single-line text value box: <c>text</c>, and every other textual `&lt;input&gt;` type this
        /// renderer treats identically (`search`, `url`, `tel`, `email`, `number`, the date/time
        /// family, and any unrecognized/absent type - matching the HTML spec's "unknown type falls
        /// back to text" rule), plus `password` (masked with bullet characters).
        /// </summary>
        TextLike,

        /// <summary>`input[type=checkbox]`.</summary>
        Checkbox,

        /// <summary>`input[type=radio]`.</summary>
        Radio,

        /// <summary>`input[type=color]`.</summary>
        Color,

        /// <summary>
        /// A clickable, label-centered button box: `&lt;button&gt;` and
        /// `input[type=button|submit|reset]`.
        /// </summary>
        Button,

        /// <summary>`&lt;select&gt;` - shows only its selected `&lt;option&gt;`'s text, never every option.</summary>
        Select,

        /// <summary>`input[type=range]` - a track with a positioned thumb, no text/value shown.</summary>
        Range,

        /// <summary>`input[type=file]` - a button-like box showing a fixed "Choose File" label.</summary>
        File,

        /// <summary>
        /// The date/time family - `date`, `time`, `datetime-local`, `month`, `week` - showing its
        /// value (or the segmented placeholder) formatted the way an en-US browser does, next to a
        /// calendar or clock picker icon.
        /// </summary>
        DateTime,

        /// <summary>`&lt;textarea&gt;` - gets the same box chrome as a text input, but keeps its real
        /// child text node flowing through the ordinary block child-layout path for its content.</summary>
        TextArea,

        /// <summary>`input[type=hidden]` - never laid out or painted; see the type's own remarks.</summary>
        Hidden,
    }

    /// <summary>
    /// The accent color `accent-color: auto` stands for (Chrome/Edge's default), used for checked
    /// checkboxes and radios and the range thumb.
    /// </summary>
    private static readonly RenderColor FormControlAccentColor = new(26, 115, 232, 255);

    /// <summary>The element's `accent-color` (inherited), or the default for `auto`/unset.</summary>
    private static RenderColor ResolveAccentColor(Dictionary<string, string> styleMap) =>
        styleMap.TryGetValue("accent-color", out var value) && !string.IsNullOrWhiteSpace(value) &&
        !value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? ParseColor(value, FormControlAccentColor)
            : FormControlAccentColor;

    /// <summary>
    /// The bundled sans-serif font's own ascent, as a fraction of font-size, measured empirically
    /// via <c>SKPaint.FontMetrics</c> (14.8515625px ascent at font-size 16 -> 0.928) - used to
    /// center a form control's single-line text on its real visual ink rather than on the taller
    /// CSS line-height box. See <see cref="PaintFormControl"/> for why: the "baseline at the bottom
    /// of the line-height box" convention the rest of this renderer uses for stacked body text
    /// leaves no room for descenders/leading below the baseline, which is invisible across many
    /// stacked lines but visibly pushed single-line form-control text toward the bottom of its box.
    /// </summary>
    private const float FormControlTextAscentRatio = 0.928f;

    /// <summary>Companion to <see cref="FormControlTextAscentRatio"/> - measured descent 3.7734375px at font-size 16 -> 0.236.</summary>
    private const float FormControlTextDescentRatio = 0.236f;

    private static float ResolveTextBaselineOffset(float lineHeight, float fontSize) =>
        Math.Max(0f, (lineHeight - (fontSize * (FormControlTextAscentRatio + FormControlTextDescentRatio))) / 2f) +
        (fontSize * FormControlTextAscentRatio);

    /// <summary>
    /// "⌄" (DOWNWARDS ARROWHEAD, a thin chevron) rather than a custom-drawn triangle - <c>
    /// DisplayList</c> has no generic polygon-fill primitive, so any dropdown indicator has to come
    /// from a real font glyph. A plain ASCII "v" (this constant's original value) is guaranteed to
    /// exist in every bundled font but reads as a literal letter, not an icon; U+2304 was verified
    /// - by rendering several candidate glyphs and inspecting the actual pixels, not assumed from a
    /// coverage table - to exist in the bundled DejaVu Sans as a proper thin chevron shape close to
    /// a real browser's own native indicator, unlike, for example, U+23D7 which the bundled font
    /// has no glyph for at all (renders as a hollow "tofu" box). Shared between the default-width
    /// measurement in <see cref="ApplyFormControlDefaults"/> and the actual paint in
    /// <see cref="PaintFormControl"/> so the two can never disagree about how much room it needs.
    /// </summary>
    private const string FormControlSelectArrowGlyph = "⌄";

    /// <summary>
    /// A downward correction applied only to <see cref="FormControlSelectArrowGlyph"/>'s own
    /// baseline, on top of the ordinary text baseline every other form-control label already
    /// centers on via <see cref="FormControlTextAscentRatio"/>/<see cref="FormControlTextDescentRatio"/>.
    /// Those two ratios approximate *ordinary latin text's* ink extents (built from measuring a
    /// word like "Second"), but "⌄" is a short symbol glyph whose own ink sits much closer to the
    /// baseline - measured via <c>SKPaint.MeasureText</c>'s tight bounding box at font-size 16:
    /// "⌄" spans roughly 6px above the baseline to 1px below it (a 7px-tall glyph, vertical ink
    /// center ~2.5px above baseline), while "Second" spans roughly 14px above to 2px below (a
    /// 16px-tall run, vertical ink center ~6px above baseline). Painting both at the *same*
    /// baseline - which is what correctly centers the text label - therefore left the arrow's own,
    /// much shorter ink sitting visibly low in the box: a real, confirmed bug, not a hypothetical
    /// one (caught from a direct visual report, not assumed). This ratio is the measured difference
    /// between those two ink-centers as a fraction of font-size (~-3px at font-size 16, i.e.
    /// -3/16), shifting the arrow's baseline up just enough that its own ink centers where the
    /// text's ink already does.
    /// </summary>
    private const float FormControlSelectArrowVerticalOffsetRatio = -0.19f;

    /// <summary>
    /// The width, in pixels, of a focused text-like input's caret - a fixed value rather than a
    /// font-size fraction, matching how a real browser's own caret stays a thin ~1-2px line
    /// regardless of font size rather than scaling with it.
    /// </summary>
    private const float FormControlCaretWidth = 1.5f;

    /// <summary>
    /// One full fade cycle of the caret's blink animation, in milliseconds - see
    /// <see cref="PaintFormControlCaret"/> for how this is used.
    /// </summary>
    private const double FormControlCaretBlinkPeriodMs = 1000d;

    private static FormControlKind ResolveFormControlKind(ElementRenderNode node)
    {
        var element = node.Ref;

        // A ::before/::after pseudo-element's LocalName/GetAttribute proxy straight through to its
        // host (PseudoElement.cs, in AngleSharp.Css) - an <input>/<select>/<textarea>/<button>'s own
        // generated-content pseudo would otherwise be misidentified as that same form control and
        // get its default chrome/value painted a second time.
        if (element is IPseudoElement)
        {
            return FormControlKind.None;
        }

        var tagName = element.LocalName;

        if (string.Equals(tagName, "textarea", StringComparison.OrdinalIgnoreCase))
        {
            return FormControlKind.TextArea;
        }

        if (string.Equals(tagName, "select", StringComparison.OrdinalIgnoreCase))
        {
            return FormControlKind.Select;
        }

        if (string.Equals(tagName, "button", StringComparison.OrdinalIgnoreCase))
        {
            // A <button> with elements inside (icons, spans, ::before/::after, ...) shows them like
            // any other box, only a plain text one gets the label this renderer paints for button-like inputs.
            return node.Children.Any(child => child is ElementRenderNode) ? FormControlKind.None : FormControlKind.Button;
        }

        if (!string.Equals(tagName, "input", StringComparison.OrdinalIgnoreCase))
        {
            return FormControlKind.None;
        }

        var type = element.GetAttribute("type")?.Trim().ToLowerInvariant();

        return type switch
        {
            "checkbox" => FormControlKind.Checkbox,
            "radio" => FormControlKind.Radio,
            "color" => FormControlKind.Color,
            "range" => FormControlKind.Range,
            "date" or "time" or "datetime-local" or "month" or "week" => FormControlKind.DateTime,
            "file" => FormControlKind.File,
            "button" or "submit" or "reset" => FormControlKind.Button,
            "hidden" => FormControlKind.Hidden,
            // An image button is a replaced element; without a loadable image it falls back to a
            // button showing its `alt` text, as browsers do.
            "image" => HasLoadableImageSource(element) ? FormControlKind.None : FormControlKind.Button,
            _ => FormControlKind.TextLike,
        };
    }

    /// <summary>
    /// Fills in a form control's intrinsic size - what its element and content dictate, like an
    /// image's natural size - for whichever of `width`/`height` the author left unset. The
    /// control's default look (border, padding, background, margins, the round radio) is not
    /// set here: it belongs to the default stylesheet and arrives through the computed style.
    /// </summary>
    private static void ApplyFormControlDefaults(
        FormControlKind kind,
        IElement element,
        Dictionary<string, string> styleMap,
        RenderTextStyle textStyle,
        LayoutContext context)
    {
        void SetDefault(string property, string value)
        {
            if (!styleMap.TryGetValue(property, out var existing) || string.IsNullOrWhiteSpace(existing))
            {
                styleMap[property] = value;
            }
        }

        // A single line's worth of content height. Where the baseline sits inside that box is a
        // separate matter (see FormControlTextAscentRatio/DescentRatio in PaintFormControl).
        var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;

        switch (kind)
        {
            case FormControlKind.TextLike:
                SetDefault("width", "150px");
                SetDefault("height", FormatPixelValue(lineHeight));
                break;

            case FormControlKind.Select:
            {
                // Wide enough for the selected option's label plus the dropdown arrow.
                var selectLabel = ResolveFormControlLabel(FormControlKind.Select, element);
                var selectLabelWidth = MeasureTextWidth(context, selectLabel, textStyle);
                var arrowWidth = MeasureTextWidth(context, FormControlSelectArrowGlyph, textStyle);
                var arrowGap = textStyle.FontSize * 0.5f;
                SetDefault("width", FormatPixelValue(Math.Max(30f, selectLabelWidth + arrowGap + arrowWidth)));
                SetDefault("height", FormatPixelValue(lineHeight));
                break;
            }

            case FormControlKind.TextArea:
                // Only a floor: content that wraps past two lines still grows the box.
                SetDefault("width", "150px");
                SetDefault("height", FormatPixelValue(lineHeight * 2f));
                break;

            case FormControlKind.Color:
                SetDefault("width", "36px");
                SetDefault("height", FormatPixelValue(lineHeight));
                break;

            case FormControlKind.Range:
                SetDefault("width", "150px");
                SetDefault("height", FormatPixelValue(Math.Max(16f, lineHeight * 0.6f)));
                break;

            case FormControlKind.DateTime:
            {
                // Wide enough for the placeholder (or the value, if longer) plus the picker icon.
                var type = element.GetAttribute("type")?.Trim().ToLowerInvariant() ?? "date";
                var textWidth = Math.Max(
                    MeasureTextWidth(context, FormatDateTimeInputValue(type, null), textStyle),
                    MeasureTextWidth(context, ResolveFormControlLabel(kind, element), textStyle));
                var iconWidth = textStyle.FontSize * FormControlPickerIconScale;
                SetDefault("width", FormatPixelValue(textWidth + (textStyle.FontSize * 0.5f) + iconWidth));
                SetDefault("height", FormatPixelValue(lineHeight));
                break;
            }

            case FormControlKind.File:
            case FormControlKind.Button:
            {
                // As wide as the label the control paints itself.
                var label = ResolveFormControlLabel(kind, element);
                SetDefault("width", FormatPixelValue(Math.Max(20f, MeasureTextWidth(context, label, textStyle))));
                SetDefault("height", FormatPixelValue(lineHeight));
                break;
            }

            case FormControlKind.Checkbox:
            case FormControlKind.Radio:
            {
                var boxSize = FormatPixelValue(Math.Max(10f, textStyle.FontSize * 0.9f));
                SetDefault("width", boxSize);
                SetDefault("height", boxSize);
                break;
            }
        }
    }

    /// <summary>The picker icon of a date/time input, as a fraction of the font size.</summary>
    private const float FormControlPickerIconScale = 0.9f;

    /// <summary>
    /// A date/time input's displayed text: its value in en-US form (`05/03/2024`, `01:05 PM`,
    /// `May 2024`, `Week 18, 2024`), or the segmented placeholder when the value is missing or
    /// invalid, as browsers do.
    /// </summary>
    private static string FormatDateTimeInputValue(string type, string? value)
    {
        const string DatePlaceholder = "mm/dd/yyyy";
        const string TimePlaceholder = "--:-- --";
        var trimmed = value?.Trim() ?? string.Empty;
        var culture = CultureInfo.InvariantCulture;

        string? FormatDate(string text) =>
            DateTime.TryParseExact(text, "yyyy-MM-dd", culture, DateTimeStyles.None, out var date) ? date.ToString("MM/dd/yyyy", culture) : null;

        string? FormatTime(string text) =>
            DateTime.TryParseExact(text, ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFF"], culture, DateTimeStyles.None, out var time)
                ? time.ToString(time.Second != 0 || time.Millisecond != 0 ? "hh:mm:ss tt" : "hh:mm tt", culture)
                : null;

        switch (type)
        {
            case "time":
                return FormatTime(trimmed) ?? TimePlaceholder;

            case "datetime-local":
            {
                var separator = trimmed.IndexOfAny(['T', ' ']);
                var date = separator > 0 ? FormatDate(trimmed[..separator]) : null;
                var time = separator > 0 ? FormatTime(trimmed[(separator + 1)..]) : null;
                return date is not null && time is not null ? $"{date}, {time}" : $"{DatePlaceholder}, {TimePlaceholder}";
            }

            case "month":
                return DateTime.TryParseExact(trimmed, "yyyy-MM", culture, DateTimeStyles.None, out var month)
                    ? month.ToString("MMMM yyyy", culture)
                    : "--------- ----";

            case "week":
            {
                var weekIndex = trimmed.IndexOf("-W", StringComparison.OrdinalIgnoreCase);
                return weekIndex == 4 &&
                       int.TryParse(trimmed[..4], NumberStyles.None, culture, out var year) &&
                       int.TryParse(trimmed[(weekIndex + 2)..], NumberStyles.None, culture, out var week) &&
                       trimmed.Length == 8 && week is >= 1 and <= 53 && year > 0
                    ? string.Create(culture, $"Week {week:00}, {year:0000}")
                    : "Week --, ----";
            }

            default:
                return FormatDate(trimmed) ?? DatePlaceholder;
        }
    }

    /// <summary>
    /// A date/time input's picker icon in the text color: a calendar (an outlined page with a
    /// filled header) or, for `time`, a clock (a circle with two hands).
    /// </summary>
    private static void PaintPickerIcon(DisplayList displayList, RenderRect rect, RenderColor color, bool isClock)
    {
        var stroke = Math.Max(1f, rect.Width / 10f);
        var none = new RenderCornerRadii(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

        if (isClock)
        {
            var radius = rect.Width / 2f;
            displayList.StrokeRoundedRect(rect, color, stroke, new RenderCornerRadii(radius, radius, radius, radius, radius, radius, radius, radius));
            var centerX = rect.X + radius;
            var centerY = rect.Y + radius;
            displayList.FillRect(new RenderRect(centerX - (stroke / 2f), rect.Y + (rect.Height * 0.25f), stroke, radius * 0.5f + (stroke / 2f)), color);
            displayList.FillRect(new RenderRect(centerX - (stroke / 2f), centerY - (stroke / 2f), rect.Width * 0.25f + (stroke / 2f), stroke), color);
            return;
        }

        var page = new RenderRect(rect.X, rect.Y + (rect.Height * 0.1f), rect.Width, rect.Height * 0.9f);
        displayList.StrokeRoundedRect(page, color, stroke, none);
        displayList.FillRect(new RenderRect(page.X, page.Y, page.Width, page.Height * 0.3f), color);
    }

    private static string FormatPixelValue(float pixels) =>
        string.Create(CultureInfo.InvariantCulture, $"{pixels:0.##}px");

    /// <summary>
    /// Resolves the text a form control shows as its own content - a typed `value`, a button's
    /// label, or a select's currently-selected option - independently of normal child-text flow,
    /// since <see cref="PaintFormControl"/> paints it directly rather than relying on any child
    /// nodes being laid out (an `&lt;input&gt;` has none at all; a `&lt;select&gt;`'s/`&lt;button&gt;`'s are
    /// deliberately excluded from `orderedChildren` in `LayoutElement`).
    /// </summary>
    private static string ResolveFormControlLabel(FormControlKind kind, IElement element)
    {
        switch (kind)
        {
            case FormControlKind.Button:
            {
                if (string.Equals(element.LocalName, "button", StringComparison.OrdinalIgnoreCase))
                {
                    var text = NormalizeWhitespace(element.TextContent ?? string.Empty);
                    return text.Length > 0 ? text : "Button";
                }

                var value = ResolveInputValue(element);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                return element.GetAttribute("type")?.Trim().ToLowerInvariant() switch
                {
                    "image" => element.GetAttribute("alt") is { Length: > 0 } alt ? alt : "Submit",
                    "submit" => "Submit",
                    "reset" => "Reset",
                    _ => "Button",
                };
            }

            case FormControlKind.TextLike:
            {
                var value = ResolveInputValue(element);

                // A password field's own value never paints as plain text, matching the one
                // input type where masking is part of the type's defining behavior rather than an
                // optional/overridable styling choice.
                return string.Equals(element.GetAttribute("type")?.Trim(), "password", StringComparison.OrdinalIgnoreCase) && value.Length > 0
                    ? new string('•', value.Length)
                    : value;
            }

            case FormControlKind.Select:
            {
                // The options' live selectedness (initially their `selected` attribute), last one
                // wins as for multiple `selected` attributes - falling back to the first <option>
                // when none is selected, exactly like a browser's own initial-selection default.
                var options = element.Children.Where(child => string.Equals(child.LocalName, "option", StringComparison.OrdinalIgnoreCase)).ToList();
                var selected = options.LastOrDefault(IsOptionSelected) ?? options.FirstOrDefault();
                return selected is not null ? NormalizeWhitespace(selected.TextContent ?? string.Empty) : string.Empty;
            }

            case FormControlKind.DateTime:
                return FormatDateTimeInputValue(element.GetAttribute("type")?.Trim().ToLowerInvariant() ?? "date", ResolveInputValue(element));

            case FormControlKind.File:
                // No filename-tracking model (this renderer does not simulate a real file picker
                // dialog or an `IElement.Files` selection) - always the fixed label a real browser
                // shows before any file has been chosen.
                return "Choose File";

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Paints a form control's own visible content - typed/selected text, or a checkbox/radio's
    /// checked-state fill, or a color swatch - directly into <paramref name="displayList"/> at the
    /// same point <see cref="PaintListItemMarker"/> paints a list marker, so it lands after the
    /// box's own background/border (spliced in later at the already-captured
    /// <c>boxPaintInsertIndex</c>) but is otherwise unaffected by however tall the box's children
    /// end up making it - none of these controls have real children of their own to wait on.
    /// </summary>
    private static void PaintFormControl(
        DisplayList displayList,
        IElement element,
        FormControlKind kind,
        RenderTextStyle textStyle,
        RenderColor accentColor,
        LayoutContext context,
        float contentX,
        float contentY,
        float contentWidth,
        float contentHeight)
    {
        switch (kind)
        {
            case FormControlKind.TextLike:
            case FormControlKind.Select:
            case FormControlKind.Button:
            case FormControlKind.File:
            case FormControlKind.DateTime:
            {
                var label = ResolveFormControlLabel(kind, element);

                // Centers the glyphs' own visual ink, not the CSS line box: LayoutWrappedText's
                // "content-box top plus one full line height" baseline convention places the
                // baseline as if every pixel of the line-height were ascent, with none left over
                // for descenders or leading - fine for stacked body-text lines (the next line's own
                // top absorbs the difference), but for a single line centered in a form control's
                // padded box it visibly pushed text toward the bottom. FormControlTextAscentRatio/
                // DescentRatio approximate the bundled sans-serif font's real metrics at a
                // representative size (measured via SKPaint.FontMetrics: ascent 14.85px, descent
                // 3.77px at font-size 16, i.e. ~0.928/~0.236 of the em) the same way
                // ParseVerticalAlign already approximates super/sub/middle offsets as fontSize
                // fractions rather than querying per-font metrics through ITextMeasurer (which only
                // ever exposes advance width, by design - see ITextMeasurer's own remarks). Computed
                // unconditionally (not just when there is a label) since a focused, empty TextLike
                // input still needs a baseline/left-edge position for its caret below.
                var visualTextHeight = textStyle.FontSize * (FormControlTextAscentRatio + FormControlTextDescentRatio);
                var verticalCenteringOffset = Math.Max(0f, (contentHeight - visualTextHeight) / 2f);
                var baselineY = contentY + verticalCenteringOffset + (textStyle.FontSize * FormControlTextAscentRatio) + textStyle.VerticalAlignOffset;
                RecordTextBaseline(baselineY - textStyle.VerticalAlignOffset);
                var labelWidth = label.Length > 0 ? MeasureTextWidth(context, label, textStyle) : 0f;

                if (label.Length > 0)
                {
                    // A text input's typed value and a select's selected option are both
                    // left-aligned, matching how browsers show them; a button's and a file input's
                    // own (fixed) label are both centered in their box instead.
                    var labelX = kind is FormControlKind.Button or FormControlKind.File
                        ? contentX + Math.Max(0f, (contentWidth - labelWidth) / 2f)
                        : contentX;

                    DrawStyledText(context, displayList, label, labelX, baselineY, textStyle, decorate: false);

                    if (kind == FormControlKind.Select)
                    {
                        var arrowWidth = MeasureTextWidth(context, FormControlSelectArrowGlyph, textStyle);
                        var arrowX = contentX + Math.Max(labelWidth, contentWidth - arrowWidth);
                        var arrowBaselineY = baselineY + (textStyle.FontSize * FormControlSelectArrowVerticalOffsetRatio);
                        DrawStyledText(context, displayList, FormControlSelectArrowGlyph, arrowX, arrowBaselineY, textStyle, decorate: false);
                    }
                }

                else if (kind == FormControlKind.TextLike && ResolvePlaceholder(element) is { } placeholder)
                {
                    DrawStyledText(context, displayList, placeholder, contentX, baselineY, ResolvePlaceholderTextStyle(element, textStyle, context), decorate: false);
                }

                if (kind == FormControlKind.TextLike)
                {
                    PaintFormControlCaret(displayList, element, textStyle, contentX + labelWidth, baselineY);
                }

                if (kind == FormControlKind.DateTime)
                {
                    var iconSize = Math.Min(contentHeight, textStyle.FontSize * FormControlPickerIconScale);
                    var iconRect = new RenderRect(contentX + contentWidth - iconSize, contentY + ((contentHeight - iconSize) / 2f), iconSize, iconSize);
                    var isClock = string.Equals(element.GetAttribute("type")?.Trim(), "time", StringComparison.OrdinalIgnoreCase);
                    PaintPickerIcon(displayList, iconRect, textStyle.Color, isClock);
                }

                break;
            }

            case FormControlKind.Checkbox:
            {
                if (IsInputChecked(element))
                {
                    // Sized off the box's own smaller dimension so a checkbox whose width and
                    // height default to the same font-relative size (the common case) still gets a
                    // perfectly square fill even if either is overridden asymmetrically, and inset
                    // independently per axis so the fill is centered on both axes rather than only
                    // assuming a square box.
                    var size = Math.Max(0f, Math.Min(contentWidth, contentHeight) * 0.6f);
                    var insetX = (contentWidth - size) / 2f;
                    var insetY = (contentHeight - size) / 2f;
                    displayList.FillRect(new RenderRect(contentX + insetX, contentY + insetY, size, size), accentColor);
                }

                break;
            }

            case FormControlKind.Radio:
            {
                if (IsInputChecked(element))
                {
                    var size = Math.Max(0f, Math.Min(contentWidth, contentHeight) * 0.5f);
                    var insetX = (contentWidth - size) / 2f;
                    var insetY = (contentHeight - size) / 2f;
                    var radii = new RenderCornerRadii(
                        size / 2f, size / 2f, size / 2f, size / 2f,
                        size / 2f, size / 2f, size / 2f, size / 2f);
                    displayList.FillRect(new RenderRect(contentX + insetX, contentY + insetY, size, size), accentColor, radii);
                }

                break;
            }

            case FormControlKind.Color:
            {
                // The swatch is painted as its own fill, independent of the box's own
                // background-color (already defaulted to white above, as the swatch's "frame") -
                // deliberately not overridable via CSS background-color, matching how a real
                // browser's native color swatch ignores it too; only the frame around it (border,
                // padding, size) goes through the ordinarily-overridable box model. It fills the
                // entire content box (rather than a fixed line-height-tall rect within it) so it is
                // always centered regardless of how tall the box's own content height ends up being.
                var color = ParseColor(ResolveInputValue(element), RenderColor.Black);
                displayList.FillRect(new RenderRect(contentX, contentY, contentWidth, contentHeight), color);
                break;
            }

            case FormControlKind.Range:
            {
                // `min`/`max`/`value` default to 0/100/(min+max)/2 per the HTML spec's own
                // input[type=range] defaults, used whenever the corresponding attribute is absent
                // or unparsable rather than left at 0 (which would visually look like an
                // always-empty slider for the common case of an author never authoring `value`).
                var min = ParseRangeAttribute(element, "min", 0d);
                var max = ParseRangeAttribute(element, "max", 100d);

                if (max <= min)
                {
                    max = min + 1d;
                }

                var value = Math.Clamp(
                    double.TryParse(ResolveInputValue(element), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue) ? parsedValue : (min + max) / 2d,
                    min,
                    max);
                var fraction = (float)((value - min) / (max - min));

                var trackHeight = Math.Max(2f, contentHeight * 0.2f);
                var trackY = contentY + ((contentHeight - trackHeight) / 2f);
                displayList.FillRect(
                    new RenderRect(contentX, trackY, contentWidth, trackHeight),
                    new RenderColor(0xc0, 0xc0, 0xc0),
                    new RenderCornerRadii(trackHeight / 2f, trackHeight / 2f, trackHeight / 2f, trackHeight / 2f, trackHeight / 2f, trackHeight / 2f, trackHeight / 2f, trackHeight / 2f));

                var thumbSize = Math.Max(trackHeight, Math.Min(contentHeight, 16f));
                var thumbX = contentX + (fraction * contentWidth) - (thumbSize / 2f);
                var thumbY = contentY + ((contentHeight - thumbSize) / 2f);
                displayList.FillRect(
                    new RenderRect(thumbX, thumbY, thumbSize, thumbSize),
                    accentColor,
                    new RenderCornerRadii(thumbSize / 2f, thumbSize / 2f, thumbSize / 2f, thumbSize / 2f, thumbSize / 2f, thumbSize / 2f, thumbSize / 2f, thumbSize / 2f));
                break;
            }

            case FormControlKind.TextArea:
            {
                // A <textarea>'s real child text node flows through the ordinary block
                // child-layout path (see this method's own remarks) - only its focused caret is
                // painted here, at the end of its own text, on whichever wrapped line that text
                // ends up ending on. Reproduces the exact same wrapping the real content is laid
                // out with (same text, same `white-space`-aware splitting, same content width) so
                // the caret never drifts out of sync with what is actually painted.
                // A value changed by script or input no longer matches the child text, so it is
                // painted here instead (its children are not laid out, see LayoutElement).
                var changedValue = ResolveChangedTextAreaValue(element);

                if (changedValue is null && !element.IsFocused && ResolvePlaceholder(element) is null)
                {
                    break;
                }

                var normalizedText = NormalizeWhitespace(changedValue ?? element.TextContent ?? string.Empty, textStyle.WhiteSpace);
                var lines = WrapTextRespectingWhiteSpace(context, normalizedText, contentWidth, textStyle);
                var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;

                if (normalizedText.Length == 0 && ResolvePlaceholder(element) is { } placeholder)
                {
                    var placeholderStyle = ResolvePlaceholderTextStyle(element, textStyle, context);
                    var placeholderLines = WrapTextRespectingWhiteSpace(context, NormalizeWhitespace(placeholder, textStyle.WhiteSpace), contentWidth, textStyle);
                    displayList.PushClip(new RenderRect(contentX, contentY, contentWidth, contentHeight), RenderCornerRadii.Zero);

                    for (var lineIndex = 0; lineIndex < placeholderLines.Count; lineIndex++)
                    {
                        var lineBaselineY = contentY + (lineIndex * lineHeight) + ResolveTextBaselineOffset(lineHeight, textStyle.FontSize) + textStyle.VerticalAlignOffset;
                        DrawStyledText(context, displayList, placeholderLines[lineIndex], contentX, lineBaselineY, placeholderStyle, decorate: false);
                    }

                    displayList.PopClip();
                }
                else if (changedValue is not null)
                {
                    displayList.PushClip(new RenderRect(contentX, contentY, contentWidth, contentHeight), RenderCornerRadii.Zero);

                    for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                    {
                        var lineBaselineY = contentY + (lineIndex * lineHeight) + ResolveTextBaselineOffset(lineHeight, textStyle.FontSize) + textStyle.VerticalAlignOffset;
                        DrawStyledText(context, displayList, lines[lineIndex], contentX, lineBaselineY, textStyle, decorate: false);
                    }

                    displayList.PopClip();
                }

                if (!element.IsFocused)
                {
                    break;
                }

                var lastLineIndex = Math.Max(0, lines.Count - 1);
                var lastLine = lines.Count > 0 ? lines[^1] : string.Empty;
                var lastLineWidth = lastLine.Length > 0 ? MeasureTextWidth(context, lastLine, textStyle) : 0f;

                var caretBaselineY = contentY + (lastLineIndex * lineHeight) + ResolveTextBaselineOffset(lineHeight, textStyle.FontSize) + textStyle.VerticalAlignOffset;
                PaintFormControlCaret(displayList, element, textStyle, contentX + lastLineWidth, caretBaselineY);
                break;
            }
        }
    }


    /// <summary>
    /// An input's live value (<see cref="IHtmlInputElement.Value"/>, which starts out as the
    /// `value` attribute), so what script or user input set is what is shown.
    /// </summary>
    private static string ResolveInputValue(IElement element) =>
        element is IHtmlInputElement input ? input.Value ?? string.Empty : element.GetAttribute("value") ?? string.Empty;

    /// <summary>
    /// The `placeholder` of a text field or textarea (null when absent or empty). Line breaks in
    /// an input's placeholder are stripped, as the HTML spec requires.
    /// </summary>
    private static string? ResolvePlaceholder(IElement element)
    {
        var placeholder = element.GetAttribute("placeholder");

        if (string.IsNullOrEmpty(placeholder))
        {
            return null;
        }

        return element is IHtmlInputElement ? placeholder.Replace("\r", string.Empty).Replace("\n", string.Empty) : placeholder;
    }

    /// <summary>
    /// The style placeholder text paints with: the computed `::placeholder` style (inheriting from
    /// the control; its default look, `color: darkgray`, comes from the UA sheet). Without a style
    /// collection (no window) it is the control's own text style.
    /// </summary>
    private static RenderTextStyle ResolvePlaceholderTextStyle(IElement element, RenderTextStyle textStyle, LayoutContext context) =>
        context.StyleCollection is { } styleCollection
            ? ResolveTextStyle(CreateStyleMap(styleCollection.ComputeDeclarations(element, "::placeholder")), textStyle)
            : textStyle;

    private static bool IsInputChecked(IElement element) =>
        element is IHtmlInputElement input ? input.IsChecked : element.HasAttribute("checked");

    private static bool IsOptionSelected(IElement option) =>
        option is IHtmlOptionElement htmlOption ? htmlOption.IsSelected : option.HasAttribute("selected");

    /// <summary>
    /// A <c>&lt;textarea&gt;</c>'s live value when it no longer matches its child text (null
    /// otherwise, and the child text keeps flowing as ordinary content).
    /// </summary>
    private static string? ResolveChangedTextAreaValue(IElement element) =>
        element is IHtmlTextAreaElement textArea && !string.Equals(textArea.Value, textArea.DefaultValue, StringComparison.Ordinal)
            ? textArea.Value ?? string.Empty
            : null;

    private static double ParseRangeAttribute(IElement element, string attributeName, double defaultValue)
    {
        var raw = element.GetAttribute(attributeName);
        return !string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    /// <summary>
    /// Paints a text-insertion caret for a focused <see cref="FormControlKind.TextLike"/> input,
    /// right after its current value (or at the content edge, for an empty one) - this renderer
    /// has no concept of a selection/cursor offset within an input's own value (AngleSharp does
    /// not track one either), so the caret always sits at the end of the text, matching by far the
    /// most common "user is appending" case a static render would want to depict. Real focus comes
    /// straight from the DOM (<see cref="IElement.IsFocused"/>) rather than being tracked
    /// separately by this renderer - AngleSharp.Css's own `:focus` pseudo-class forcing already
    /// delegates to it (see AGENTS.md's `:hover` section for the equivalent story with
    /// <c>SetPseudoClass</c>), so a plain <c>element.Focus()</c> or
    /// <c>element.SetPseudoClass("focus")</c> is already enough to make a caret appear, with no
    /// extra wiring needed here. `&lt;textarea&gt;` reuses this same helper (see the
    /// <see cref="FormControlKind.TextArea"/> case in <see cref="PaintFormControl"/>) - its own
    /// caret position is computed by re-wrapping its text the same way its real content is laid
    /// out, since unlike a single-line input's fixed baseline, a textarea's caret depends on
    /// whichever wrapped line its text currently ends on.
    /// </summary>
    private static void PaintFormControlCaret(
        DisplayList displayList,
        IElement element,
        RenderTextStyle textStyle,
        float caretX,
        float baselineY)
    {
        if (!element.IsFocused)
        {
            return;
        }

        var alpha = 1f;

        // The fade needs a live clock to animate against; without one (a plain, non-interactive
        // RenderToPng/BuildDisplayList call, or a harness whose AdvanceTime was simply never
        // called) it paints fully opaque rather than frozen at some arbitrary phase - the same
        // "no harness means no animation, not a stuck mid-animation frame" fallback `transition`/
        // `animation` already use (see ApplyActiveTransitionAndAnimationOverrides).
        if (element.Owner is not null && element.Owner.Context.TryGetDomHarness(out var harness) && harness is not null)
        {
            // A smooth cosine "breathe" - continuously sweeping through fully visible, partway
            // faded, and fully invisible - rather than a hard on/off blink, so sampling the virtual
            // clock at any instant shows a plausible in-between frame instead of only ever a flat
            // "on" or "off" pixel value.
            var cyclePosition = harness.CurrentTime.TotalMilliseconds % FormControlCaretBlinkPeriodMs / FormControlCaretBlinkPeriodMs;
            alpha = (float)((Math.Cos(cyclePosition * 2 * Math.PI) + 1) / 2);
        }

        var caretTop = baselineY - (textStyle.FontSize * FormControlTextAscentRatio);
        var caretBottom = baselineY + (textStyle.FontSize * FormControlTextDescentRatio);
        var alphaByte = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
        var caretColor = new RenderColor(textStyle.Color.R, textStyle.Color.G, textStyle.Color.B, alphaByte);

        displayList.FillRect(new RenderRect(caretX, caretTop, FormControlCaretWidth, caretBottom - caretTop), caretColor);
    }

    /// <summary>
    /// Paints a `display: list-item` element's marker (bullet or number) and, for
    /// `list-style-position: inside`, returns a copy of <paramref name="textStyle"/> whose
    /// <see cref="RenderTextStyle.MarkerIndent"/> makes room for it on the first line. That is a
    /// separate field from the inherited <c>TextIndent</c>: <see cref="ResolveTextStyle"/> builds
    /// every descendant's style afresh, so the marker space never leaks into nested blocks.
    /// </summary>
    private static RenderTextStyle PaintListItemMarker(
        DisplayList displayList,
        IElement element,
        Dictionary<string, string> styleMap,
        RenderTextStyle textStyle,
        LayoutContext context,
        float borderBoxX,
        float contentX,
        float contentY)
    {
        var listStyleType = ResolveListStyleType(styleMap);

        if (string.Equals(listStyleType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return textStyle;
        }

        var isInside = styleMap.TryGetValue("list-style-position", out var positionValue) &&
            string.Equals(positionValue.Trim(), "inside", StringComparison.OrdinalIgnoreCase);

        // Aligns the marker with the first line of the li's own content, assuming that content
        // starts as normal inline text at the default vertical-align - the common case, but an
        // approximation when a li's first child is itself a block (its own first line may sit at
        // a different offset than this).
        var lineHeight = textStyle.FontSize * textStyle.LineHeightMultiplier;
        var markerBaselineY = contentY + ResolveTextBaselineOffset(lineHeight, textStyle.FontSize) + textStyle.VerticalAlignOffset;

        // Not derived from any spec metric - a small, fixed visual gap between the marker and the
        // content that follows it, matching the general proportions browsers use.
        const float MarkerGap = 6f;

        if (IsShapeListStyleType(listStyleType))
        {
            var markerSize = Math.Max(2f, textStyle.FontSize * 0.35f);
            var markerTop = markerBaselineY - (textStyle.FontSize * 0.52f);
            var markerLeft = isInside ? contentX : borderBoxX - MarkerGap - markerSize;
            var markerRect = new RenderRect(markerLeft, markerTop, markerSize, markerSize);
            var circularRadii = new RenderCornerRadii(
                markerSize / 2f, markerSize / 2f, markerSize / 2f, markerSize / 2f,
                markerSize / 2f, markerSize / 2f, markerSize / 2f, markerSize / 2f);

            switch (listStyleType)
            {
                case "circle":
                    displayList.StrokeRoundedRect(markerRect, textStyle.Color, Math.Max(1f, textStyle.FontSize * 0.08f), circularRadii);
                    break;
                case "square":
                    displayList.FillRect(markerRect, textStyle.Color);
                    break;
                default: // disc
                    displayList.FillRect(markerRect, textStyle.Color, circularRadii);
                    break;
            }

            return isInside ? textStyle with { MarkerIndent = markerSize + MarkerGap } : textStyle;
        }

        var markerText = FormatListMarkerText(ResolveListItemOrdinal(element), listStyleType);
        var markerWidth = MeasureTextWidth(context, markerText, textStyle);
        var markerX = isInside ? contentX : borderBoxX - MarkerGap - markerWidth;

        DrawStyledText(context, displayList, markerText, markerX, markerBaselineY, textStyle, decorate: false);

        return isInside ? textStyle with { MarkerIndent = markerWidth + MarkerGap } : textStyle;
    }

    /// <summary>
    /// Whether an element's `overflow` clips its content. `scroll` and `auto` are treated the
    /// same as `hidden`: a rendered PNG has no scrollbars or interactivity, so anything a browser
    /// would let the user scroll to reveal is, here, simply clipped away like `hidden`.
    /// </summary>
    private static bool ShouldClipOverflow(Dictionary<string, string> styleMap) =>
        IsClippingOverflowValue(ResolveOverflowAxis(styleMap, "overflow-x")) ||
        IsClippingOverflowValue(ResolveOverflowAxis(styleMap, "overflow-y"));

    /// <summary>
    /// Resolves one overflow axis. AngleSharp.Css's `overflow` shorthand decomposes into
    /// `overflow-x`/`overflow-y` in its own computed style, so the longhand can always be
    /// read directly.
    /// </summary>
    private static string ResolveOverflowAxis(Dictionary<string, string> styleMap, string longhandProperty) =>
        styleMap.TryGetValue(longhandProperty, out var axisValue) && !string.IsNullOrWhiteSpace(axisValue)
            ? axisValue.Trim().ToLowerInvariant()
            : "visible";

    private static bool IsClippingOverflowValue(string overflowValue) =>
        overflowValue is "hidden" or "scroll" or "auto";

    // AngleSharp.Css's UA stylesheet sets list-style-type: disc directly on ol/ul/dir/menu/dd
    // (fixed upstream), so this default is dead for the common case - it is still needed, and
    // still correct, for an element given `display: list-item` explicitly (any other tag), which
    // that UA rule does not cover and which still computes an empty list-style-type - confirmed
    // empirically, not assumed.
    private static string ResolveListStyleType(Dictionary<string, string> styleMap) =>
        styleMap.TryGetValue("list-style-type", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "disc";

    private static bool IsShapeListStyleType(string listStyleType) =>
        listStyleType is "disc" or "circle" or "square";

    /// <summary>
    /// Formats an ordinal into the marker text for a numbered `list-style-type`. Any keyword this
    /// renderer does not specifically implement (and plain `decimal`) falls back to a decimal
    /// number, matching how browsers treat an unsupported `list-style-type` value.
    /// </summary>
    private static string FormatListMarkerText(int ordinal, string listStyleType) => listStyleType switch
    {
        "decimal-leading-zero" => (ordinal is >= 0 and < 10 ? "0" + ordinal.ToString(CultureInfo.InvariantCulture) : ordinal.ToString(CultureInfo.InvariantCulture)) + ".",
        "lower-alpha" or "lower-latin" => FormatAlphaListMarker(ordinal, upper: false) + ".",
        "upper-alpha" or "upper-latin" => FormatAlphaListMarker(ordinal, upper: true) + ".",
        "lower-roman" => FormatRomanListMarker(ordinal, upper: false) + ".",
        "upper-roman" => FormatRomanListMarker(ordinal, upper: true) + ".",
        _ => ordinal.ToString(CultureInfo.InvariantCulture) + ".",
    };

    /// <summary>
    /// Formats a 1-based ordinal as a base-26 letter sequence (a, b, ..., z, aa, ab, ...),
    /// matching `list-style-type: lower-alpha`/`upper-alpha`.
    /// </summary>
    private static string FormatAlphaListMarker(int ordinal, bool upper)
    {
        if (ordinal < 1)
        {
            return ordinal.ToString(CultureInfo.InvariantCulture);
        }

        var baseChar = upper ? 'A' : 'a';
        var chars = new Stack<char>();
        var remaining = ordinal;

        while (remaining > 0)
        {
            remaining--;
            chars.Push((char)(baseChar + (remaining % 26)));
            remaining /= 26;
        }

        return new string(chars.ToArray());
    }

    private static readonly (int Value, string Symbol)[] s_romanNumeralValues =
    [
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    ];

    /// <summary>
    /// Formats an ordinal as a Roman numeral, matching `list-style-type: lower-roman`/`upper-roman`.
    /// Roman numerals have no standard representation outside 1-3999, so values outside that range
    /// fall back to a plain decimal number, matching typical browser behavior.
    /// </summary>
    private static string FormatRomanListMarker(int ordinal, bool upper)
    {
        if (ordinal is < 1 or > 3999)
        {
            return ordinal.ToString(CultureInfo.InvariantCulture);
        }

        var builder = new StringBuilder();
        var remaining = ordinal;

        foreach (var (value, symbol) in s_romanNumeralValues)
        {
            while (remaining >= value)
            {
                builder.Append(symbol);
                remaining -= value;
            }
        }

        var result = builder.ToString();
        return upper ? result : result.ToLowerInvariant();
    }

    /// <summary>
    /// Resolves CSS `counter-reset`/`counter-increment`/`counter-set` and rewrites any `::before`/
    /// `::after` pseudo-element's generated content that reads it back via `counter()`/
    /// `counters()` - a full, general implementation deliberately layered independently of
    /// `ResolveListItemOrdinal`'s own narrower `&lt;ol&gt;`/`&lt;li&gt;`-specific counting model,
    /// which remains unchanged and does not use this machinery.
    ///
    /// AngleSharp.Css's own generated-content pipeline (`RenderTreeBuilder.RenderElement`) already
    /// synthesizes a `::before`/`::after` pseudo's single text child from `ContentDeclaration.Stringify`,
    /// but that method always stringifies a `counter()`/`counters()` segment to an empty string (a
    /// documented, deliberate scope cut in AngleSharp.Css itself - see this file's own remarks on
    /// generated content) - by the time this renderer's layout pass ever sees that synthesized text,
    /// the fact that it came from `counter()` at all is already lost, with nothing left to
    /// substitute a computed value into. Recovering it requires re-reading the pseudo-element's own
    /// *raw*, uncomputed `content` declaration text directly (still available via
    /// `ComputeCurrentStyle().GetPropertyValue("content")`, since that only serializes the parsed
    /// value back to text - it does not stringify it) and re-deriving the final string here,
    /// independently of AngleSharp.Css's own (necessarily different, since it has no counter model)
    /// resolution.
    ///
    /// A single pre-order pass over the already-built render tree, entirely separate from the
    /// actual layout walk (see the call site in <c>BuildDisplayList</c>) - counter scoping (a
    /// `counter-reset` on one element must not leak its own new instance to siblings outside that
    /// element's own subtree, but must still be visible again once that subtree ends) is
    /// notoriously easy to get wrong if threaded through <c>LayoutElement</c> itself, which has
    /// many early-return paths that would each need to remember to pop whatever this element
    /// pushed. A plain recursive function call sidesteps that entirely: whatever this call pushes
    /// onto <paramref name="context"/>'s counter stacks is always popped again right before this
    /// call itself returns, regardless of how the recursive `foreach` over children unwinds.
    /// </summary>
    private static void ProcessCounters(IRenderNode node, LayoutContext context, GeneratedContentState? state = null, IElement? host = null)
    {
        if (node is not ElementRenderNode elementNode)
        {
            return;
        }

        state ??= new GeneratedContentState();

        var styleMap = GetOrCreateStyleMap(context, elementNode.Ref, elementNode.ComputedStyle);

        // A pseudo-element's computed style reports its host's own `counter-increment`/
        // `counter-reset`/`counter-set` even when only a plain (non-`::before`/`::after`) selector
        // declared it (confirmed empirically: a `.item { counter-increment: item }` rule with no
        // pseudo-element in its own selector at all still shows up here for `.item`'s own `::before`)
        // - applying it a second time here would double-count every such counter. Mutations are
        // therefore only ever applied for a real element, never a pseudo-element, a deliberate,
        // narrow scope cut rather than an attempt to distinguish "genuinely redeclared on the
        // pseudo's own selector" from "inherited from the host" after the fact.
        var introducedCounterNames = elementNode.Ref is IPseudoElement
            ? []
            : ApplyCounterDeclarations(styleMap, context.Counters);

        if (elementNode.Ref is IPseudoElement)
        {
            var rawContent = elementNode.ComputedStyle.GetPropertyValue("content");

            if (TryResolveContentImageUrl(rawContent) is { } imageUrl)
            {
                s_generatedContentImages.AddOrUpdate(elementNode.Ref, imageUrl);
            }
            else
            {
                s_generatedContentImages.Remove(elementNode.Ref);

                if (TryEvaluateGeneratedContent(rawContent, host ?? elementNode.Ref, elementNode.ComputedStyle, context.Counters, state, out var segments))
                {
                    ApplyGeneratedContent(elementNode, segments);
                }
            }
        }

        foreach (var child in elementNode.Children)
        {
            ProcessCounters(child, context, state, elementNode.Ref);
        }

        // Pop in reverse order of introduction - matters only for the rare case of the same
        // counter name appearing more than once in a single element's own `counter-reset` list,
        // where popping out of order would restore the wrong intermediate value.
        for (var i = introducedCounterNames.Count - 1; i >= 0; i--)
        {
            var name = introducedCounterNames[i];

            if (context.Counters.TryGetValue(name, out var stack) && stack.Count > 0)
            {
                stack.Pop();

                if (stack.Count == 0)
                {
                    context.Counters.Remove(name);
                }
            }
        }
    }

    /// <summary>
    /// Applies this element's own `counter-reset`/`counter-set`/`counter-increment` declarations,
    /// in that order (matching the CSS Lists spec's own processing order for a single element),
    /// and returns the counter names this element's own `counter-reset` introduced a *new* stacked
    /// instance for - the only ones <see cref="ProcessCounters"/> needs to pop again once this
    /// element's subtree is done. `counter-set`/`counter-increment` on a name with no existing
    /// instance anywhere both create one too (per spec), which is also tracked here so it gets
    /// popped at the same point - otherwise it would permanently leak into every following sibling.
    /// </summary>
    private static List<string> ApplyCounterDeclarations(Dictionary<string, string> styleMap, Dictionary<string, Stack<int>> counters)
    {
        var introduced = new List<string>();

        void CreateOrUpdate(string name, int value, bool replaceTop)
        {
            if (counters.TryGetValue(name, out var stack) && stack.Count > 0)
            {
                if (replaceTop)
                {
                    stack.Pop();
                    stack.Push(value);
                }
                else
                {
                    stack.Push(value);
                    introduced.Add(name);
                }

                return;
            }

            var newStack = new Stack<int>();
            newStack.Push(value);
            counters[name] = newStack;
            introduced.Add(name);
        }

        if (styleMap.TryGetValue("counter-reset", out var resetRaw))
        {
            foreach (var (name, value) in ParseCounterDeclarationList(resetRaw, defaultValue: 0))
            {
                CreateOrUpdate(name, value, replaceTop: false);
            }
        }

        if (styleMap.TryGetValue("counter-set", out var setRaw))
        {
            foreach (var (name, value) in ParseCounterDeclarationList(setRaw, defaultValue: 0))
            {
                CreateOrUpdate(name, value, replaceTop: true);
            }
        }

        if (styleMap.TryGetValue("counter-increment", out var incrementRaw))
        {
            foreach (var (name, incrementBy) in ParseCounterDeclarationList(incrementRaw, defaultValue: 1))
            {
                if (counters.TryGetValue(name, out var stack) && stack.Count > 0)
                {
                    var current = stack.Pop();
                    stack.Push(current + incrementBy);
                }
                else
                {
                    CreateOrUpdate(name, incrementBy, replaceTop: false);
                }
            }
        }

        return introduced;
    }

    /// <summary>
    /// Parses `counter-reset`/`counter-set`/`counter-increment`'s shared grammar - a whitespace-
    /// separated (not comma-separated) list of `&lt;counter-name&gt; &lt;integer&gt;?` pairs, or
    /// the keyword `none` - into (name, value) pairs, substituting <paramref name="defaultValue"/>
    /// for any name with no following integer, exactly as the property's own initial/omitted value
    /// dictates (0 for reset/set, 1 for increment).
    /// </summary>
    private static IEnumerable<(string Name, int Value)> ParseCounterDeclarationList(string raw, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? pendingName = null;

        foreach (var token in tokens)
        {
            if (pendingName is not null && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitValue))
            {
                yield return (pendingName, explicitValue);
                pendingName = null;
                continue;
            }

            if (pendingName is not null)
            {
                yield return (pendingName, defaultValue);
            }

            pendingName = token;
        }

        if (pendingName is not null)
        {
            yield return (pendingName, defaultValue);
        }
    }

    /// <summary>
    /// Puts evaluated generated content into a pseudo-element's render node: the text of its one
    /// synthesized text child, or - when images are mixed in - a sequence of text nodes and
    /// anonymous replaced `img` nodes (a detached element styled with initial values, which is
    /// what an anonymous image box is). AngleSharp.Css builds a pseudo's children as a mutable
    /// list; should that ever change, the images are dropped and only the text remains.
    /// </summary>
    private static void ApplyGeneratedContent(ElementRenderNode pseudoNode, List<GeneratedContentSegment> segments)
    {
        var text = string.Concat(segments.Select(segment => segment.Text));

        if (segments.Any(segment => segment.ImageUrl is not null) &&
            pseudoNode.Children is List<IRenderNode> children &&
            pseudoNode.Ref.Owner is { } document)
        {
            children.Clear();

            foreach (var segment in segments)
            {
                if (segment.ImageUrl is { } url)
                {
                    var image = document.CreateElement("img");
                    image.SetAttribute("src", url);
                    var style = image.ComputeCurrentStyle();
                    children.Add(new ElementRenderNode(image, pseudoNode, new List<IRenderNode>(), style, style));
                }
                else
                {
                    children.Add(new TextRenderNode(document.CreateTextNode(segment.Text!), pseudoNode));
                }
            }

            return;
        }

        if (pseudoNode.Children.OfType<TextRenderNode>().FirstOrDefault() is { } textChild)
        {
            textChild.Ref.Data = text;
        }
    }

    /// <summary>
    /// Document-order state of generated content: the current quote nesting depth, which every
    /// `open-quote`/`no-open-quote` raises and every `close-quote`/`no-close-quote` lowers.
    /// </summary>
    private sealed class GeneratedContentState
    {
        public int QuoteDepth;
    }

    /// <summary>
    /// The image URL of a `content` value that is exactly one `url()` - the case that turns a
    /// pseudo-element into a replaced element. Anything else (no image, or an image mixed with
    /// text) returns <see langword="null"/>.
    /// </summary>
    private static string? TryResolveContentImageUrl(string? rawContent)
    {
        var trimmed = rawContent?.Trim();

        if (trimmed is null || !trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(')'))
        {
            return null;
        }

        var url = StripQuotes(trimmed[4..^1]);
        return url.Length == 0 || url.Contains('"') || url.Contains('\'') ? null : url;
    }

    /// <summary>
    /// Re-derives a `::before`/`::after` pseudo-element's generated content from its own *raw*
    /// `content` declaration, as text and image segments, for the parts AngleSharp.Css's own
    /// generated-content resolution cannot handle, because they need document state:
    /// `counter()`/`counters()`, the quote keywords (`open-quote`, `close-quote`, `no-open-quote`,
    /// `no-close-quote`, nested through <paramref name="state"/> and picked from `quotes`) and
    /// `url()` images among text. Strings and `attr()` may be mixed in. A value without any of
    /// these, or one this parser does not recognize, returns <see langword="false"/> so the caller keeps AngleSharp.Css's own
    /// text (a rejected value is rejected whole, before it can change the quote depth). Only the
    /// `decimal` counter style is supported - a second `counter()` argument is skipped.
    /// </summary>
    private static bool TryEvaluateGeneratedContent(string? rawContent, IElement host, ICssStyleDeclaration style, Dictionary<string, Stack<int>> counters, GeneratedContentState state, out List<GeneratedContentSegment> result)
    {
        result = [];

        if (string.IsNullOrWhiteSpace(rawContent) ||
            !(rawContent.Contains("counter", StringComparison.OrdinalIgnoreCase) ||
              rawContent.Contains("quote", StringComparison.OrdinalIgnoreCase) ||
              rawContent.Contains("url(", StringComparison.OrdinalIgnoreCase)) ||
            !TryTokenizeContent(rawContent, out var tokens))
        {
            return false;
        }

        var segments = result;
        var builder = new StringBuilder();

        void Flush()
        {
            if (builder.Length > 0)
            {
                segments.Add(new GeneratedContentSegment(builder.ToString(), null));
                builder.Clear();
            }
        }
        IReadOnlyList<(string Open, string Close)>? quotes = null;

        foreach (var (kind, value, arguments) in tokens)
        {
            switch (kind)
            {
                case ContentTokenKind.String:
                    builder.Append(value);
                    break;

                case ContentTokenKind.Function when value == "counter" && arguments.Length > 0:
                    builder.Append((counters.TryGetValue(arguments[0], out var stack) && stack.Count > 0 ? stack.Peek() : 0).ToString(CultureInfo.InvariantCulture));
                    break;

                case ContentTokenKind.Function when value == "counters" && arguments.Length > 0:
                    builder.Append(counters.TryGetValue(arguments[0], out var nested) && nested.Count > 0
                        ? string.Join(arguments.Length > 1 ? arguments[1] : string.Empty, nested.Reverse())
                        : "0");
                    break;

                case ContentTokenKind.Function when value == "attr" && arguments.Length > 0:
                    builder.Append(host.GetAttribute(arguments[0]) ?? string.Empty);
                    break;

                case ContentTokenKind.Function when value == "url" && arguments.Length > 0:
                    Flush();
                    segments.Add(new GeneratedContentSegment(null, arguments[0]));
                    break;

                case ContentTokenKind.Keyword when value == "open-quote":
                    quotes ??= ResolveQuotes(style.GetPropertyValue("quotes"), host);

                    if (quotes.Count > 0)
                    {
                        builder.Append(quotes[Math.Min(state.QuoteDepth, quotes.Count - 1)].Open);
                    }

                    state.QuoteDepth++;
                    break;

                case ContentTokenKind.Keyword when value == "close-quote":
                    if (state.QuoteDepth > 0)
                    {
                        state.QuoteDepth--;
                        quotes ??= ResolveQuotes(style.GetPropertyValue("quotes"), host);

                        if (quotes.Count > 0)
                        {
                            builder.Append(quotes[Math.Min(state.QuoteDepth, quotes.Count - 1)].Close);
                        }
                    }

                    break;

                case ContentTokenKind.Keyword when value == "no-open-quote":
                    state.QuoteDepth++;
                    break;

                case ContentTokenKind.Keyword when value == "no-close-quote":
                    state.QuoteDepth = Math.Max(0, state.QuoteDepth - 1);
                    break;
            }
        }

        Flush();
        return true;
    }

    /// <summary>A run of generated text, or (with <see cref="ImageUrl"/>) one generated image.</summary>
    private readonly record struct GeneratedContentSegment(string? Text, string? ImageUrl);

    private enum ContentTokenKind
    {
        String,
        Function,
        Keyword,
    }

    /// <summary>
    /// Splits a serialized `content` value into strings, functions (`counter()`, `counters()`,
    /// `attr()`, `url()`) and quote keywords; any other token fails the whole value.
    /// </summary>
    private static bool TryTokenizeContent(string rawContent, out List<(ContentTokenKind Kind, string Value, string[] Arguments)> tokens)
    {
        tokens = [];
        var i = 0;

        while (i < rawContent.Length)
        {
            if (char.IsWhiteSpace(rawContent[i]))
            {
                i++;
                continue;
            }

            if (rawContent[i] is '"' or '\'')
            {
                var closingQuoteIndex = rawContent.IndexOf(rawContent[i], i + 1);

                if (closingQuoteIndex < 0)
                {
                    return false;
                }

                tokens.Add((ContentTokenKind.String, rawContent[(i + 1)..closingQuoteIndex], []));
                i = closingQuoteIndex + 1;
                continue;
            }

            var start = i;

            while (i < rawContent.Length && (char.IsLetterOrDigit(rawContent[i]) || rawContent[i] == '-'))
            {
                i++;
            }

            var name = rawContent[start..i].ToLowerInvariant();

            if (name.Length == 0)
            {
                return false;
            }

            if (i >= rawContent.Length || rawContent[i] != '(')
            {
                if (name is not ("open-quote" or "close-quote" or "no-open-quote" or "no-close-quote"))
                {
                    return false;
                }

                tokens.Add((ContentTokenKind.Keyword, name, []));
                continue;
            }

            // A quoted argument (a `url("...")`, a `counters()` separator) may itself contain `)`.
            var closeParenIndex = i + 1;

            while (closeParenIndex < rawContent.Length && rawContent[closeParenIndex] != ')')
            {
                if (rawContent[closeParenIndex] is '"' or '\'')
                {
                    var end = rawContent.IndexOf(rawContent[closeParenIndex], closeParenIndex + 1);
                    closeParenIndex = end < 0 ? rawContent.Length : end;
                }

                closeParenIndex++;
            }

            if (closeParenIndex >= rawContent.Length || name is not ("counter" or "counters" or "attr" or "url"))
            {
                return false;
            }

            // AngleSharp.Css's own serialization of a function's arguments is not guaranteed to
            // preserve the author's original comma - `counters(item, ".")` round-trips through
            // `GetPropertyValue` as `counters(item .)`, space-separated with no comma at all
            // (confirmed empirically, not assumed) - so arguments have to be tokenized respecting
            // quoted strings and treating both `,` and whitespace as equally valid separators.
            var arguments = TokenizeFunctionArguments(rawContent[(i + 1)..closeParenIndex])
                .Select(StripQuotes)
                .ToArray();

            tokens.Add((ContentTokenKind.Function, name, arguments));

            i = closeParenIndex + 1;
        }

        return true;
    }

    /// <summary>
    /// The quote pairs of a `quotes` value, outermost first: the listed strings, none for `none`,
    /// or (`auto`, which AngleSharp.Css reports for an unset value as an empty string) the
    /// conventional pairs of the host's content language, English by default.
    /// </summary>
    private static IReadOnlyList<(string Open, string Close)> ResolveQuotes(string? quotesValue, IElement host)
    {
        var trimmed = quotesValue?.Trim() ?? string.Empty;

        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (trimmed.Length > 0 && !trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var strings = TokenizeFunctionArguments(trimmed).Select(StripQuotes).ToList();
            var pairs = new List<(string Open, string Close)>();

            for (var i = 0; i + 1 < strings.Count; i += 2)
            {
                pairs.Add((strings[i], strings[i + 1]));
            }

            if (pairs.Count > 0)
            {
                return pairs;
            }
        }

        var language = host.Closest("[lang]")?.GetAttribute("lang")?.Trim().ToLowerInvariant() ?? string.Empty;
        var primary = language.Split('-', '_')[0];

        return primary switch
        {
            "de" or "cs" or "sk" or "sl" or "lt" or "is" => [("\u201E", "\u201C"), ("\u201A", "\u2018")],
            "fr" or "es" or "it" or "pt" or "ca" or "el" => [("\u00AB", "\u00BB"), ("\u201C", "\u201D")],
            "ru" or "uk" or "be" => [("\u00AB", "\u00BB"), ("\u201E", "\u201C")],
            "pl" or "hu" or "ro" or "hr" or "bg" => [("\u201E", "\u201D"), ("\u00AB", "\u00BB")],
            "sv" or "fi" => [("\u201D", "\u201D"), ("\u2019", "\u2019")],
            "da" => [("\u00BB", "\u00AB"), ("\u203A", "\u2039")],
            "ja" => [("\u300C", "\u300D"), ("\u300E", "\u300F")],
            _ => [("\u201C", "\u201D"), ("\u2018", "\u2019")],
        };
    }

    private static string StripQuotes(string token)
    {
        var trimmed = token.Trim();
        return trimmed.Length >= 2 && (trimmed[0] is '"' or '\'') && trimmed[^1] == trimmed[0]
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>
    /// Splits a CSS function's already-extracted argument text into individual argument tokens,
    /// treating both `,` and plain whitespace as valid separators (see the call site's own remarks
    /// for why) and keeping a quoted string's own contents - including any comma or whitespace
    /// inside it - together as a single token rather than splitting on separators found within it.
    /// </summary>
    private static List<string> TokenizeFunctionArguments(string args)
    {
        var tokens = new List<string>();
        var i = 0;

        while (i < args.Length)
        {
            while (i < args.Length && (char.IsWhiteSpace(args[i]) || args[i] == ','))
            {
                i++;
            }

            if (i >= args.Length)
            {
                break;
            }

            if (args[i] is '"' or '\'')
            {
                var quote = args[i];
                var closingQuoteIndex = args.IndexOf(quote, i + 1);

                if (closingQuoteIndex < 0)
                {
                    tokens.Add(args[i..]);
                    break;
                }

                tokens.Add(args[i..(closingQuoteIndex + 1)]);
                i = closingQuoteIndex + 1;
                continue;
            }

            var start = i;

            while (i < args.Length && args[i] != ',' && !char.IsWhiteSpace(args[i]))
            {
                i++;
            }

            tokens.Add(args[start..i]);
        }

        return tokens;
    }

    /// <summary>
    /// Resolves a `&lt;li&gt;`'s 1-based ordinal from its position among its parent's direct
    /// `&lt;li&gt;` children in document order - not from a render-tree traversal order, which
    /// can reorder for z-index/absolute positioning, and not from any generic CSS counter, since
    /// only the common `&lt;ol&gt;`/`&lt;li&gt;` counting model (`start`, `reversed`, per-item
    /// `value`) is implemented. A `&lt;li&gt;` outside any `&lt;ol&gt;`/`&lt;ul&gt;` still resolves
    /// to 1, matching how browsers render a bare `&lt;li&gt;`.
    /// </summary>
    private static int ResolveListItemOrdinal(IElement liElement)
    {
        var parent = liElement.ParentElement;

        if (parent is null)
        {
            return 1;
        }

        var isOrderedList = string.Equals(parent.LocalName, "ol", StringComparison.OrdinalIgnoreCase);
        var reversed = isOrderedList && parent.HasAttribute("reversed");
        var counter = isOrderedList && int.TryParse(parent.GetAttribute("start"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitStart)
            ? explicitStart
            : reversed
                ? parent.Children.Count(c => string.Equals(c.LocalName, "li", StringComparison.OrdinalIgnoreCase))
                : 1;

        foreach (var child in parent.Children)
        {
            if (!string.Equals(child.LocalName, "li", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(child.GetAttribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var overrideValue))
            {
                counter = overrideValue;
            }

            if (ReferenceEquals(child, liElement))
            {
                return counter;
            }

            counter += reversed ? -1 : 1;
        }

        return counter;
    }

    private static void PaintTextShadows(DisplayList displayList, IReadOnlyList<RenderTextShadow> shadows, string text, float x, float y, RenderTextStyle textStyle)
    {
        if (shadows.Count == 0 || string.IsNullOrEmpty(text))
        {
            return;
        }

        // The first-listed shadow paints topmost among shadows (though always behind the text
        // itself), so shadows are added back-to-front: last-authored first, first-authored last.
        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            var shadow = shadows[i];
            displayList.DrawTextShadow(text, x + shadow.OffsetX, y + shadow.OffsetY, shadow.Color, textStyle.FontSize, textStyle.FontFamily, textStyle.FontWeight, textStyle.IsItalic, shadow.BlurRadius, textStyle.LetterSpacing, textStyle.FontStretch);
        }
    }

    private static void PaintBoxShadows(DisplayList displayList, IReadOnlyList<RenderBoxShadow> shadows, float x, float y, float width, float height, RenderCornerRadii radii)
    {
        if (shadows.Count == 0 || width <= 0f || height <= 0f)
        {
            return;
        }

        var clampedRadii = radii.ClampToBox(width, height);
        var borderBoxRect = new RenderRect(x, y, width, height);

        // The first-listed shadow paints topmost among shadows (though always behind the box
        // itself), so shadows are added back-to-front: last-authored first, first-authored last.
        for (var i = shadows.Count - 1; i >= 0; i--)
        {
            displayList.DrawBoxShadow(borderBoxRect, clampedRadii, shadows[i]);
        }
    }

    /// <summary>
    /// Paints a box's background: the color first, clipped like the bottom image layer, then the
    /// image layers from the bottom up. Each layer is clipped to its `background-clip` box (border
    /// box by default) and sized and positioned in its `background-origin` box (padding box by
    /// default); radii shrink with the inset, like the inner edge of a rounded border. Layers with
    /// a `background-blend-mode` blend only with the layers and color below them, so the whole
    /// background is then painted as an isolated group. `background-clip: text` is not supported
    /// and paints like `border-box`.
    /// </summary>
    private static void PaintBoxBackground(DisplayList displayList, Dictionary<string, string> styleMap, BoxStyle box, float x, float y, float width, float height, EdgeSizes border, EdgeSizes padding)
    {
        var layers = box.BackgroundLayers ?? [];

        (RenderRect Rect, RenderCornerRadii Radii) ResolveArea(string? keyword, string fallback) =>
            ResolveBoxArea(keyword, fallback, x, y, width, height, border, padding, box.BorderRadius);

        var colorClip = layers.Count > 0
            ? layers[^1].Clip
            : (GetLayerValue(styleMap, "background-clip", 0) ?? string.Empty).ToLowerInvariant();
        var blends = layers.Any(static layer => layer.BlendMode != RenderBlendMode.Normal);

        if (blends)
        {
            displayList.PushBlendMode(RenderBlendMode.Normal);
        }

        if (box.BackgroundColor.A > 0)
        {
            var (colorRect, colorRadii) = ResolveArea(colorClip, "border-box");
            PaintBackground(displayList, new RenderColorPaint(box.BackgroundColor), colorRect.X, colorRect.Y, colorRect.Width, colorRect.Height, colorRadii);
        }

        for (var index = layers.Count - 1; index >= 0; index--)
        {
            var layer = layers[index];
            var (clipRect, clipRadii) = ResolveArea(layer.Clip, "border-box");
            var (originRect, _) = ResolveArea(layer.Origin, "padding-box");

            if (layer.BlendMode != RenderBlendMode.Normal)
            {
                displayList.PushBlendMode(layer.BlendMode);
            }

            PaintBackground(displayList, layer.Paint, clipRect.X, clipRect.Y, clipRect.Width, clipRect.Height, clipRadii, originRect);

            if (layer.BlendMode != RenderBlendMode.Normal)
            {
                displayList.PopBlendMode();
            }
        }

        if (blends)
        {
            displayList.PopBlendMode();
        }
    }

    /// <summary>
    /// The border box, padding box or content box (`border-box`, `padding-box`, `content-box`;
    /// <paramref name="fallback"/> when unset) with its corner radii.
    /// </summary>
    private static (RenderRect Rect, RenderCornerRadii Radii) ResolveBoxArea(string? keyword, string fallback, float x, float y, float width, float height, EdgeSizes border, EdgeSizes padding, RenderCornerRadii radii)
    {
        var inset = (string.IsNullOrEmpty(keyword) ? fallback : keyword) switch
        {
            "padding-box" => border,
            "content-box" => new EdgeSizes(border.Top + padding.Top, border.Right + padding.Right, border.Bottom + padding.Bottom, border.Left + padding.Left),
            _ => new EdgeSizes(0f, 0f, 0f, 0f),
        };
        var rect = new RenderRect(
            x + inset.Left,
            y + inset.Top,
            Math.Max(0f, width - inset.Left - inset.Right),
            Math.Max(0f, height - inset.Top - inset.Bottom));

        return (rect, InsetRadii(radii.ClampToBox(width, height), inset, 1f));
    }

    /// <summary>
    /// The CSS mask layers of a box (`mask-image` with its companion properties), topmost first.
    /// Unlike backgrounds, masks are clipped to and positioned in the border box by default.
    /// </summary>
    private static List<RenderMaskLayer> ResolveMaskLayers(Dictionary<string, string> styleMap, IElement element, float x, float y, float width, float height, EdgeSizes border, EdgeSizes padding, RenderCornerRadii radii)
    {
        var result = new List<RenderMaskLayer>();

        if (!styleMap.ContainsKey("mask-image"))
        {
            return result;
        }

        foreach (var layer in ParseImageLayers(styleMap, element, "mask", url => TryGetOrLoadBackgroundImageResource(element, url, out var resource) ? resource : null))
        {
            var (area, _) = ResolveBoxArea(layer.Clip, "border-box", x, y, width, height, border, padding, radii);
            var (paintArea, _) = ResolveBoxArea(layer.Origin, "border-box", x, y, width, height, border, padding, radii);
            var useLuminance = string.Equals(GetLayerValue(styleMap, "mask-mode", layer.Index), "luminance", StringComparison.OrdinalIgnoreCase);
            result.Add(new RenderMaskLayer(layer.Paint, area, paintArea, useLuminance));
        }

        return result;
    }

    /// <summary>
    /// Paints a box's border: its `border-image` when it has one, else the styled border.
    /// </summary>
    private static void PaintBoxBorder(DisplayList displayList, Dictionary<string, string> styleMap, IElement element, BoxStyle box, float x, float y, float width, float height)
    {
        if (ResolveBorderImage(styleMap, element, box.BorderWidth, width, height) is { } borderImage)
        {
            var outset = ParseBorderImageEdges(styleMap, "border-image-outset", "0", box.BorderWidth, width, height);
            displayList.DrawBorderImage(
                new RenderRect(x - outset.Left, y - outset.Top, width + outset.Left + outset.Right, height + outset.Top + outset.Bottom),
                borderImage);
            return;
        }

        PaintBorder(displayList, box.BorderColor, x, y, width, height, box.BorderWidth, box.BorderRadius, box.BorderStyle);
    }

    /// <summary>
    /// Resolves `border-image` (source, slice, width and repeat) for a box of the given size, or
    /// returns <see langword="null"/> when there is no usable source image.
    /// </summary>
    private static RenderBorderImage? ResolveBorderImage(Dictionary<string, string> styleMap, IElement element, EdgeSizes borderWidths, float width, float height)
    {
        if (!styleMap.TryGetValue("border-image-source", out var source) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        source = source.Trim();
        RenderPaint? sourcePaint = null;

        if (TryExtractCssUrl(source, out var url))
        {
            if (TryGetOrLoadBackgroundImageResource(element, url, out var resource) && resource is not null)
            {
                var image = new RenderedImage(resource.Bytes, resource.NaturalWidth, resource.NaturalHeight, resource.MimeType);
                sourcePaint = new RenderImagePaint(image, false, false, RenderBackgroundPositionComponent.Zero, RenderBackgroundPositionComponent.Zero, RenderBackgroundSize.Auto);
            }
        }
        else if (ParseGradientPaint(source, RenderColor.Transparent, ParseLength(styleMap, "font-size", 16f, 16f, allowAuto: false)) is RenderGradientPaint gradient)
        {
            sourcePaint = gradient;
        }

        if (sourcePaint is null)
        {
            return null;
        }

        // `border-image-slice`: one to four numbers (image pixels) or percentages, plus `fill`.
        var sliceTokens = (styleMap.TryGetValue("border-image-slice", out var slice) && !string.IsNullOrWhiteSpace(slice) ? slice : "100%")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fill = sliceTokens.Any(static token => string.Equals(token, "fill", StringComparison.OrdinalIgnoreCase));
        var slices = ExpandEdges(sliceTokens.Where(static token => !string.Equals(token, "fill", StringComparison.OrdinalIgnoreCase)).ToArray(), "100%")
            .Select(static token => token.EndsWith('%')
                ? new RenderBorderImageSlice(ParseFloat(token[..^1]) / 100f, true)
                : new RenderBorderImageSlice(ParseFloat(token), false))
            .ToArray();

        var widths = ParseBorderImageEdges(styleMap, "border-image-width", "1", borderWidths, width, height);
        var repeatTokens = (styleMap.TryGetValue("border-image-repeat", out var repeat) && !string.IsNullOrWhiteSpace(repeat) ? repeat : "stretch")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var repeatX = ParseBorderImageRepeat(repeatTokens[0]);
        var repeatY = repeatTokens.Length > 1 ? ParseBorderImageRepeat(repeatTokens[1]) : repeatX;

        return new RenderBorderImage(sourcePaint, slices[0], slices[1], slices[2], slices[3], fill, widths.Top, widths.Right, widths.Bottom, widths.Left, repeatX, repeatY);

        static float ParseFloat(string token) =>
            float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !float.IsNaN(number) ? Math.Max(0f, number) : 0f;
    }

    /// <summary>
    /// `border-image-width`/`border-image-outset`: one to four values, each a number (a multiple
    /// of that side's border width), a length, a percentage of the box (width for left/right,
    /// height for top/bottom) or `auto` (the border width).
    /// </summary>
    private static EdgeSizes ParseBorderImageEdges(Dictionary<string, string> styleMap, string propertyName, string initialValue, EdgeSizes borderWidths, float width, float height)
    {
        var tokens = ExpandEdges(
            (styleMap.TryGetValue(propertyName, out var value) && !string.IsNullOrWhiteSpace(value) ? value : initialValue).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            initialValue);

        float Resolve(string token, float borderWidth, float boxSize)
        {
            if (string.Equals(token, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return borderWidth;
            }

            if (token.EndsWith('%') && float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
            {
                return Math.Max(0f, percentage / 100f * boxSize);
            }

            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiple))
            {
                return Math.Max(0f, multiple * borderWidth);
            }

            var length = ParseLengthValue(token, 0f, allowAuto: false);
            return float.IsNaN(length) ? 0f : Math.Max(0f, length);
        }

        return new EdgeSizes(
            Resolve(tokens[0], borderWidths.Top, height),
            Resolve(tokens[1], borderWidths.Right, width),
            Resolve(tokens[2], borderWidths.Bottom, height),
            Resolve(tokens[3], borderWidths.Left, width));
    }

    /// <summary>The CSS one-to-four value shorthand expansion into top, right, bottom, left.</summary>
    private static string[] ExpandEdges(string[] tokens, string fallback) => tokens.Length switch
    {
        0 => [fallback, fallback, fallback, fallback],
        1 => [tokens[0], tokens[0], tokens[0], tokens[0]],
        2 => [tokens[0], tokens[1], tokens[0], tokens[1]],
        3 => [tokens[0], tokens[1], tokens[2], tokens[1]],
        _ => [tokens[0], tokens[1], tokens[2], tokens[3]],
    };

    private static RenderBorderImageRepeat ParseBorderImageRepeat(string token) => token.ToLowerInvariant() switch
    {
        "repeat" => RenderBorderImageRepeat.Repeat,
        "round" => RenderBorderImageRepeat.Round,
        "space" => RenderBorderImageRepeat.Space,
        _ => RenderBorderImageRepeat.Stretch,
    };

    private static RenderBlendMode ParseBlendMode(Dictionary<string, string> styleMap, string propertyName) =>
        ParseBlendModeKeyword(styleMap.TryGetValue(propertyName, out var value) ? value : null);

    private static RenderBlendMode ParseBlendModeKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RenderBlendMode.Normal;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "multiply" => RenderBlendMode.Multiply,
            "screen" => RenderBlendMode.Screen,
            "overlay" => RenderBlendMode.Overlay,
            "darken" => RenderBlendMode.Darken,
            "lighten" => RenderBlendMode.Lighten,
            "color-dodge" => RenderBlendMode.ColorDodge,
            "color-burn" => RenderBlendMode.ColorBurn,
            "hard-light" => RenderBlendMode.HardLight,
            "soft-light" => RenderBlendMode.SoftLight,
            "difference" => RenderBlendMode.Difference,
            "exclusion" => RenderBlendMode.Exclusion,
            "hue" => RenderBlendMode.Hue,
            "saturation" => RenderBlendMode.Saturation,
            "color" => RenderBlendMode.Color,
            "luminosity" => RenderBlendMode.Luminosity,
            _ => RenderBlendMode.Normal,
        };
    }

    private static void PaintBackground(DisplayList displayList, RenderPaint paint, float x, float y, float width, float height, RenderCornerRadii radii = default, RenderRect? paintArea = null)
    {
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        var clampedRadii = radii.ClampToBox(width, height);

        if (paint is RenderColorPaint colorPaint)
        {
            if (colorPaint.Color.A == 0)
            {
                return;
            }

            displayList.FillRect(new RenderRect(x, y, width, height), colorPaint.Color, clampedRadii);
            return;
        }

        if (paint is RenderGradientPaint or RenderImagePaint)
        {
            var rect = new RenderRect(x, y, width, height);

            if (paintArea is { } area && area != rect && area.Width > 0f && area.Height > 0f)
            {
                displayList.FillRect(rect, paint, clampedRadii, area);
            }
            else
            {
                displayList.FillRect(rect, paint, clampedRadii);
            }
        }
    }

    private static void PaintBorder(DisplayList displayList, RenderColor color, float x, float y, float width, float height, EdgeSizes border, RenderCornerRadii radii = default, EdgeBorderStyle styles = default) =>
        PaintBorder(displayList, new EdgeBorderColor(color, color, color, color), x, y, width, height, border, radii, styles);

    private static void PaintBorder(DisplayList displayList, EdgeBorderColor color, float x, float y, float width, float height, EdgeSizes border, RenderCornerRadii radii = default, EdgeBorderStyle styles = default)
    {
        if ((color.Top.A == 0 && color.Right.A == 0 && color.Bottom.A == 0 && color.Left.A == 0) || width <= 0f || height <= 0f)
        {
            return;
        }

        var clampedRadii = radii.ClampToBox(width, height);
        var hasUniformColor = color.Top == color.Right && color.Top == color.Bottom && color.Top == color.Left;

        // A uniform-width border with rounded corners can be drawn as a single stroked ring. A
        // mixed-width border still uses the four existing edge rectangles, clipped to the rounded
        // outer border shape so each independently-sized corner follows its authored radius.
        if (hasUniformColor && !clampedRadii.IsZero && styles.Top == BorderStyleKind.Solid && styles.Right == BorderStyleKind.Solid && styles.Bottom == BorderStyleKind.Solid && styles.Left == BorderStyleKind.Solid && border.Top > 0f && border.Top == border.Right && border.Top == border.Bottom && border.Top == border.Left)
        {
            var half = border.Top / 2f;
            var strokeRect = new RenderRect(x + half, y + half, width - border.Top, height - border.Top);
            var strokeRadii = new RenderCornerRadii(
                Math.Max(0f, clampedRadii.TopLeftX - half), Math.Max(0f, clampedRadii.TopLeftY - half),
                Math.Max(0f, clampedRadii.TopRightX - half), Math.Max(0f, clampedRadii.TopRightY - half),
                Math.Max(0f, clampedRadii.BottomRightX - half), Math.Max(0f, clampedRadii.BottomRightY - half),
                Math.Max(0f, clampedRadii.BottomLeftX - half), Math.Max(0f, clampedRadii.BottomLeftY - half));

            displayList.StrokeRoundedRect(strokeRect, color.Top, border.Top, strokeRadii);
            return;
        }

        var hasVisibleBorder =
            (border.Top > 0f && IsPaintedBorderStyle(styles.Top)) ||
            (border.Right > 0f && IsPaintedBorderStyle(styles.Right)) ||
            (border.Bottom > 0f && IsPaintedBorderStyle(styles.Bottom)) ||
            (border.Left > 0f && IsPaintedBorderStyle(styles.Left));

        if (!hasVisibleBorder)
        {
            return;
        }

        // Rounded corners with differing widths, colors or styles: each edge is the part of the
        // ring between the outer and the inner rounded rectangle that lies in its own trapezoid,
        // so widths blend along the curve and colors meet on the corner diagonals. Dashed and
        // dotted edges keep the straight-segment path below, clipped to the outer curve.
        if (!clampedRadii.IsZero && !HasBrokenStyle(styles))
        {
            PaintRoundedBorderRing(displayList, color, x, y, width, height, border, clampedRadii, styles);
            return;
        }

        var clipsRoundedBorder = !clampedRadii.IsZero;

        if (clipsRoundedBorder)
        {
            displayList.PushClip(new RenderRect(x, y, width, height), clampedRadii);
        }

        PaintBorderEdge(displayList, color.Top, new RenderRect(x, y, width, border.Top), styles.Top, horizontal: true, isTopOrLeft: true);
        PaintBorderEdge(displayList, color.Right, new RenderRect(x + width - border.Right, y, border.Right, height), styles.Right, horizontal: false, isTopOrLeft: false);
        PaintBorderEdge(displayList, color.Bottom, new RenderRect(x, y + height - border.Bottom, width, border.Bottom), styles.Bottom, horizontal: true, isTopOrLeft: false);
        PaintBorderEdge(displayList, color.Left, new RenderRect(x, y, border.Left, height), styles.Left, horizontal: false, isTopOrLeft: true);

        if (clipsRoundedBorder)
        {
            displayList.PopClip();
        }
    }

    private static bool HasBrokenStyle(EdgeBorderStyle styles) =>
        styles.Top is BorderStyleKind.Dashed or BorderStyleKind.Dotted ||
        styles.Right is BorderStyleKind.Dashed or BorderStyleKind.Dotted ||
        styles.Bottom is BorderStyleKind.Dashed or BorderStyleKind.Dotted ||
        styles.Left is BorderStyleKind.Dashed or BorderStyleKind.Dotted;

    private static void PaintRoundedBorderRing(DisplayList displayList, EdgeBorderColor color, float x, float y, float width, float height, EdgeSizes border, RenderCornerRadii radii, EdgeBorderStyle styles)
    {
        var right = x + width;
        var bottom = y + height;
        var innerLeft = x + border.Left;
        var innerTop = y + border.Top;
        var innerRight = right - border.Right;
        var innerBottom = bottom - border.Bottom;

        var sides = new (float Width, BorderStyleKind Style, RenderColor Color, bool IsTopOrLeft, (float X, float Y)[] Region)[]
        {
            (border.Top, styles.Top, color.Top, true, [(x, y), (right, y), (innerRight, innerTop), (innerLeft, innerTop)]),
            (border.Right, styles.Right, color.Right, false, [(right, y), (right, bottom), (innerRight, innerBottom), (innerRight, innerTop)]),
            (border.Bottom, styles.Bottom, color.Bottom, false, [(right, bottom), (x, bottom), (innerLeft, innerBottom), (innerRight, innerBottom)]),
            (border.Left, styles.Left, color.Left, true, [(x, bottom), (x, y), (innerLeft, innerTop), (innerLeft, innerBottom)]),
        };

        foreach (var side in sides)
        {
            if (side.Width <= 0f || !IsPaintedBorderStyle(side.Style) || side.Color.A == 0)
            {
                continue;
            }

            displayList.PushClipShape(new RenderClipPolygon(side.Region));

            // A border style is a set of bands across the border's thickness, each a fraction of
            // the full width (from the outer edge inwards) with its own color.
            foreach (var (from, to, bandColor) in GetBorderBands(side.Style, side.Color, side.IsTopOrLeft))
            {
                displayList.FillRing(
                    InsetBox(x, y, width, height, border, from),
                    InsetRadii(radii, border, from),
                    InsetBox(x, y, width, height, border, to),
                    InsetRadii(radii, border, to),
                    bandColor);
            }

            displayList.PopClip();
        }
    }

    private static RenderRect InsetBox(float x, float y, float width, float height, EdgeSizes border, float fraction) => new(
        x + (border.Left * fraction),
        y + (border.Top * fraction),
        Math.Max(0f, width - ((border.Left + border.Right) * fraction)),
        Math.Max(0f, height - ((border.Top + border.Bottom) * fraction)));

    private static RenderCornerRadii InsetRadii(RenderCornerRadii radii, EdgeSizes border, float fraction) => new(
        Math.Max(0f, radii.TopLeftX - (border.Left * fraction)),
        Math.Max(0f, radii.TopLeftY - (border.Top * fraction)),
        Math.Max(0f, radii.TopRightX - (border.Right * fraction)),
        Math.Max(0f, radii.TopRightY - (border.Top * fraction)),
        Math.Max(0f, radii.BottomRightX - (border.Right * fraction)),
        Math.Max(0f, radii.BottomRightY - (border.Bottom * fraction)),
        Math.Max(0f, radii.BottomLeftX - (border.Left * fraction)),
        Math.Max(0f, radii.BottomLeftY - (border.Bottom * fraction)));

    /// <summary>
    /// The bands a border style paints across the border's thickness, as fractions of the width
    /// measured from the outer edge. `inset`/`outset` shade the top/left and bottom/right edges
    /// differently; `groove`/`ridge` split each edge into two oppositely shaded halves.
    /// </summary>
    private static IEnumerable<(float From, float To, RenderColor Color)> GetBorderBands(BorderStyleKind style, RenderColor color, bool isTopOrLeft)
    {
        var dark = ShadeBorderColor(color);

        switch (style)
        {
            case BorderStyleKind.Double:
                yield return (0f, 1f / 3f, color);
                yield return (2f / 3f, 1f, color);
                break;
            case BorderStyleKind.Inset:
                yield return (0f, 1f, isTopOrLeft ? dark : color);
                break;
            case BorderStyleKind.Outset:
                yield return (0f, 1f, isTopOrLeft ? color : dark);
                break;
            case BorderStyleKind.Groove:
                yield return (0f, 0.5f, isTopOrLeft ? dark : color);
                yield return (0.5f, 1f, isTopOrLeft ? color : dark);
                break;
            case BorderStyleKind.Ridge:
                yield return (0f, 0.5f, isTopOrLeft ? color : dark);
                yield return (0.5f, 1f, isTopOrLeft ? dark : color);
                break;
            default:
                yield return (0f, 1f, color);
                break;
        }
    }

    /// <summary>
    /// The darker shade the 3D border styles use for their shadowed edges.
    /// </summary>
    private static RenderColor ShadeBorderColor(RenderColor color) =>
        new((byte)(color.R * 2 / 3), (byte)(color.G * 2 / 3), (byte)(color.B * 2 / 3), color.A);

    private static void PaintBorderEdge(DisplayList displayList, RenderColor color, RenderRect rect, BorderStyleKind style, bool horizontal, bool isTopOrLeft)
    {
        if (!IsPaintedBorderStyle(style) || rect.Width <= 0f || rect.Height <= 0f)
        {
            return;
        }

        var thickness = horizontal ? rect.Height : rect.Width;

        if (style is BorderStyleKind.Dashed or BorderStyleKind.Dotted)
        {
            // Dots are round and a thickness apart; dashes are three thicknesses long.
            var isDotted = style == BorderStyleKind.Dotted;
            var segment = Math.Max(1f, isDotted ? thickness : thickness * 3f);
            var length = horizontal ? rect.Width : rect.Height;
            var radius = isDotted ? thickness / 2f : 0f;
            var radii = new RenderCornerRadii(radius, radius, radius, radius, radius, radius, radius, radius);

            for (var offset = 0f; offset < length; offset += segment * 2f)
            {
                var size = Math.Min(segment, length - offset);
                var dash = horizontal
                    ? new RenderRect(rect.X + offset, rect.Y, size, rect.Height)
                    : new RenderRect(rect.X, rect.Y + offset, rect.Width, size);

                if (isDotted)
                {
                    displayList.FillRect(dash, color, radii);
                }
                else
                {
                    displayList.FillRect(dash, color);
                }
            }

            return;
        }

        if (style == BorderStyleKind.Double && thickness < 3f)
        {
            style = BorderStyleKind.Solid;
        }

        // Straight edges paint the same bands as rounded ones, as strips across the thickness;
        // the bottom/right edges count their bands from the outside too, i.e. from the far side.
        foreach (var (from, to, bandColor) in GetBorderBands(style, color, isTopOrLeft))
        {
            var start = isTopOrLeft ? from : 1f - to;
            var size = (to - from) * thickness;
            var band = horizontal
                ? new RenderRect(rect.X, rect.Y + (start * thickness), rect.Width, size)
                : new RenderRect(rect.X + (start * thickness), rect.Y, size, rect.Height);
            displayList.FillRect(band, bandColor);
        }
    }

    private static float CollapseMargins(float previousMarginBottom, float currentMarginTop)
    {
        var positivePart = Math.Max(0f, previousMarginBottom) + Math.Max(0f, currentMarginTop);
        var negativePart = Math.Min(0f, previousMarginBottom) + Math.Min(0f, currentMarginTop);

        if (positivePart > 0f && negativePart < 0f)
        {
            return positivePart + negativePart;
        }

        if (positivePart > 0f)
        {
            return Math.Max(previousMarginBottom, currentMarginTop);
        }

        return Math.Min(previousMarginBottom, currentMarginTop);
    }

    private static void ResolveHorizontalMetrics(
        float containingWidth,
        float specifiedContentWidth,
        float borderLeft,
        float borderRight,
        float paddingLeft,
        float paddingRight,
        ref float marginLeft,
        ref float marginRight,
        out float contentWidth)
    {
        var hasAutoWidth = float.IsNaN(specifiedContentWidth);
        var hasAutoLeft = float.IsNaN(marginLeft);
        var hasAutoRight = float.IsNaN(marginRight);

        var usedMarginLeft = hasAutoLeft ? 0f : marginLeft;
        var usedMarginRight = hasAutoRight ? 0f : marginRight;
        var horizontalExtras = borderLeft + borderRight + paddingLeft + paddingRight;

        if (hasAutoWidth)
        {
            contentWidth = containingWidth - horizontalExtras - usedMarginLeft - usedMarginRight;

            if (contentWidth < 0f)
            {
                contentWidth = 0f;
            }

            marginLeft = usedMarginLeft;
            marginRight = usedMarginRight;
            return;
        }

        contentWidth = Math.Max(0f, specifiedContentWidth);
        var underflow = containingWidth - horizontalExtras - contentWidth - usedMarginLeft - usedMarginRight;

        if (hasAutoLeft && hasAutoRight)
        {
            var half = underflow / 2f;
            marginLeft = half;
            marginRight = half;
            return;
        }

        if (hasAutoLeft)
        {
            marginLeft = underflow;
            marginRight = usedMarginRight;
            return;
        }

        if (hasAutoRight)
        {
            marginLeft = usedMarginLeft;
            marginRight = underflow;
            return;
        }

        marginLeft = usedMarginLeft;
        marginRight = usedMarginRight + underflow;
    }

    /// <summary>
    /// Resolves an authored `width`/`height`/`flex-basis` length exactly like
    /// <see cref="ParseLength"/> does (an unset/`auto` value falls back to
    /// <paramref name="defaultValue"/>), then - only when this element's own `box-sizing` computes
    /// to `border-box` - subtracts <paramref name="borderAndPaddingSum"/> (this same axis's own
    /// border width plus padding) from an actually-authored value, so the result always represents
    /// this renderer's pre-existing content-box convention regardless of which box model the
    /// author wrote against. CSS's default box model is content-box, where an authored `width`/
    /// `height` already *is* the content size - this renderer always assumed that unconditionally
    /// before `box-sizing` existed anywhere in its style map, so a huge, previously-unflagged gap:
    /// `box-sizing: border-box` (set globally by nearly every real-world CSS reset - Bootstrap,
    /// Tailwind's preflight, `* { box-sizing: border-box }`) authors a `width`/`height` that
    /// instead describes the *border* box, and every downstream computation in this renderer
    /// (`ResolveHorizontalMetrics`, `borderBoxWidth`/`borderBoxHeight`, the flex/grow-shrink and
    /// grid track-sizing algorithms, ...) universally assumes it is handed a content size. This is
    /// the one place that distinction gets resolved, so every other computation can stay unaware
    /// of `box-sizing` entirely, exactly as before.
    /// </summary>
    private static float ResolveAuthoredDimension(Dictionary<string, string> styleMap, string propertyName, float relativeTo, float defaultValue, float borderAndPaddingSum)
    {
        var specified = ParseLength(styleMap, propertyName, relativeTo, float.NaN, allowAuto: true);

        if (float.IsNaN(specified))
        {
            return defaultValue;
        }

        if (borderAndPaddingSum <= 0f || !IsBorderBox(styleMap))
        {
            return specified;
        }

        return Math.Max(0f, specified - borderAndPaddingSum);
    }

    private static float ResolveAspectRatioContentHeight(
        Dictionary<string, string> styleMap,
        float contentWidth,
        float horizontalBorderAndPadding,
        float verticalBorderAndPadding)
    {
        var ratio = ParseAspectRatio(styleMap);

        if (float.IsNaN(ratio))
        {
            return float.NaN;
        }

        if (!IsBorderBox(styleMap))
        {
            return contentWidth / ratio;
        }

        var borderBoxWidth = contentWidth + horizontalBorderAndPadding;
        return Math.Max(0f, (borderBoxWidth / ratio) - verticalBorderAndPadding);
    }

    /// <summary>
    /// The inverse of <see cref="ResolveAspectRatioContentHeight"/> - derives a used content width
    /// from an already-definite content height plus `aspect-ratio`, for the case where `width` is
    /// auto but `height` is not (e.g. `height: 100px; aspect-ratio: 2 / 1;` with no `width`
    /// authored at all). Shares the exact same ratio parsing as the height direction.
    /// </summary>
    private static float ResolveAspectRatioContentWidth(
        Dictionary<string, string> styleMap,
        float contentHeight,
        float horizontalBorderAndPadding,
        float verticalBorderAndPadding)
    {
        var ratio = ParseAspectRatio(styleMap);

        if (float.IsNaN(ratio))
        {
            return float.NaN;
        }

        if (!IsBorderBox(styleMap))
        {
            return contentHeight * ratio;
        }

        var borderBoxHeight = contentHeight + verticalBorderAndPadding;
        return Math.Max(0f, (borderBoxHeight * ratio) - horizontalBorderAndPadding);
    }

    /// <summary>
    /// Parses the `aspect-ratio` property's `&lt;width&gt; / &lt;height&gt;` (or bare
    /// `&lt;width&gt;`, `/ 1` implied) syntax into a single width-over-height ratio, ignoring the
    /// property's own optional leading `auto` keyword (this renderer has no intrinsic-size concept
    /// for non-replaced elements to fall back to, so `auto` and a bare ratio behave identically
    /// here). Returns NaN when unset or malformed, the same "no override" signal every other
    /// aspect-ratio consumer already expects.
    /// </summary>
    private static float ParseAspectRatio(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("aspect-ratio", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return float.NaN;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("auto ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..].Trim();
        }

        var parts = normalized.Split('/', StringSplitOptions.TrimEntries);
        var hasNumerator = float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator);
        var denominator = 1f;
        var hasDenominator = parts.Length == 1 ||
            (parts.Length == 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator));

        if (!hasNumerator || !hasDenominator || numerator <= 0f || denominator <= 0f)
        {
            return float.NaN;
        }

        return numerator / denominator;
    }

    /// <summary>
    /// Reads a length property the same way regardless of which representation the map happens
    /// to carry for it. When <paramref name="styleMap"/> is a <see cref="StyleMap"/> with a raw
    /// <see cref="CssLengthValue"/> already captured for <paramref name="propertyName"/> (see
    /// <see cref="AddLengthProperty"/>), this skips straight to the same arithmetic the string
    /// path below performs, without ever tokenizing a string - safe because a *computed* length's
    /// raw value is always exactly `Unit.Px`, `Unit.Percent`, or the NaN auto/normal sentinel
    /// (confirmed by reading `CssLengthValue.ICssValue.Compute()`: it converts every other unit to
    /// `Px` before this renderer ever sees a computed value), which is the exact same closed set
    /// the string path below already handles (it only ever recognizes `"auto"`, a `%` suffix, and
    /// `px`/bare-unitless text - never em/rem/vw/vh, because AngleSharp.Css's own `.Compute()` step
    /// already resolved those before serializing to a string). Any raw value shape outside that set
    /// (there should be none for computed style, but the check costs nothing) simply falls through
    /// to the untouched string path, so this can never produce a different answer than before -
    /// only a faster one for the common case.
    ///
    /// The NaN case (auto *or* normal - `CssLengthValue`'s own `Equals` treats every NaN-valued
    /// length as equal regardless of unit, so the two are not distinguishable from the raw value
    /// alone) resolves the same way the string path's own "auto" handling does:
    /// `allowAuto ? NaN : defaultValue`. This matches the string path exactly for every call site
    /// in this file today - the one property here whose initial value is literally the *word*
    /// "normal" (`letter-spacing`) is always called with `allowAuto: false`, where both
    /// interpretations already agree (the string path treats any unparseable text, "normal"
    /// included, as `defaultValue` unconditionally - see the `ParseLengthValue` call below).
    /// </summary>
    private static float ParseLength(Dictionary<string, string> styleMap, string propertyName, float relativeTo, float defaultValue, bool allowAuto)
    {
        if (styleMap is StyleMap fastMap && fastMap.TryGetRawLength(propertyName, out var raw))
        {
            if (double.IsNaN(raw.Value))
            {
                return allowAuto ? float.NaN : defaultValue;
            }

            if (raw.Type == CssLengthValue.Unit.Px)
            {
                return (float)raw.Value;
            }

            if (raw.Type == CssLengthValue.Unit.Percent)
            {
                return (float)(raw.Value / 100.0 * relativeTo);
            }

            // Any other unit should not occur for already-computed style - fall through to the
            // string path below rather than risk a wrong answer for a shape this has not verified.
        }

        if (!styleMap.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var parsed = value.Trim().ToLowerInvariant();

        if (allowAuto && parsed == "auto")
        {
            return float.NaN;
        }

        if (parsed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(parsed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            return (pct / 100f) * relativeTo;
        }

        return ParseLengthValue(parsed, defaultValue, allowAuto: false);
    }

    internal static float ParseLengthValue(string value, float defaultValue, bool allowAuto = true)
    {
        if (allowAuto && string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
        {
            return float.NaN;
        }

        if (TryParsePixelValue(value, out var pixels))
        {
            return pixels;
        }

        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var unitless))
        {
            return unitless;
        }

        return defaultValue;
    }

    private static bool TryParsePixelValue(string value, out float pixels)
    {
        var trimmed = value.Trim().ToLowerInvariant();

        if (trimmed.EndsWith("px", StringComparison.Ordinal))
        {
            return float.TryParse(trimmed[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out pixels);
        }

        pixels = 0f;
        return false;
    }

    /// <summary>
    /// One `background-image` layer with the companion properties that apply to it.
    /// </summary>
    private readonly record struct BackgroundLayer(RenderPaint Paint, string Clip, string Origin, RenderBlendMode BlendMode, int Index);

    /// <summary>
    /// The value of a comma-separated layer property for layer <paramref name="index"/>; a list
    /// shorter than the number of layers repeats, as CSS specifies.
    /// </summary>
    private static string? GetLayerValue(Dictionary<string, string> styleMap, string propertyName, int index)
    {
        if (!styleMap.TryGetValue(propertyName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var items = SplitTopLevelCommaList(value);
        return items.Length == 0 ? null : items[index % items.Length].Trim();
    }

    /// <summary>
    /// The `background-image` layers, topmost first. A layer that is `none`, or whose image fails
    /// to load, paints nothing.
    /// </summary>
    private static List<BackgroundLayer> ParseBackgroundLayers(Dictionary<string, string> styleMap, IElement element) =>
        ParseImageLayers(styleMap, element, "background", url => TryGetOrLoadBackgroundImageResource(element, url, out var resource) ? resource : null);

    /// <summary>
    /// The layers of `background-image` or `mask-image` (<paramref name="prefix"/>), each with
    /// its own `-size`, `-position`, `-repeat`, `-clip`, `-origin` and (backgrounds only)
    /// `-blend-mode` taken from the matching item of those lists.
    /// </summary>
    private static List<BackgroundLayer> ParseImageLayers(Dictionary<string, string> styleMap, IElement element, string prefix, Func<string, CachedImageResource?> loadImage)
    {
        var layers = new List<BackgroundLayer>();

        if (!styleMap.TryGetValue($"{prefix}-image", out var images) || string.IsNullOrWhiteSpace(images))
        {
            return layers;
        }

        var items = SplitTopLevelCommaList(images);

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index].Trim();

            if (item.Length == 0 || string.Equals(item, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var size = ParseBackgroundSize(GetLayerValue(styleMap, $"{prefix}-size", index));
            var (positionX, positionY) = ParseBackgroundPosition(GetLayerValue(styleMap, $"{prefix}-position", index));
            var (repeatX, repeatY) = ParseBackgroundRepeat(GetLayerValue(styleMap, $"{prefix}-repeat", index));
            RenderPaint? paint = null;

            if (TryExtractCssUrl(item, out var url))
            {
                if (loadImage(url) is { } resource)
                {
                    var image = new RenderedImage(resource.Bytes, resource.NaturalWidth, resource.NaturalHeight, resource.MimeType);
                    paint = new RenderImagePaint(
                        image,
                        repeatX != RenderBackgroundRepeat.NoRepeat,
                        repeatY != RenderBackgroundRepeat.NoRepeat,
                        positionX,
                        positionY,
                        size,
                        repeatX is RenderBackgroundRepeat.Space or RenderBackgroundRepeat.Round ? repeatX : null,
                        repeatY is RenderBackgroundRepeat.Space or RenderBackgroundRepeat.Round ? repeatY : null);
                }
            }
            else if (ParseGradientPaint(item, RenderColor.Transparent, ParseLength(styleMap, "font-size", 16f, 16f, allowAuto: false)) is RenderGradientPaint gradient)
            {
                // A gradient fills the whole area unless it is explicitly sized, positioned or
                // repeated differently.
                var isDefaultTile = size.Kind == RenderBackgroundSizeKind.Auto &&
                                    positionX == RenderBackgroundPositionComponent.Zero &&
                                    positionY == RenderBackgroundPositionComponent.Zero &&
                                    repeatX == RenderBackgroundRepeat.Repeat &&
                                    repeatY == RenderBackgroundRepeat.Repeat;
                paint = isDefaultTile ? gradient : gradient with { Tile = new RenderBackgroundTile(size, positionX, positionY, repeatX, repeatY) };
            }

            if (paint is null)
            {
                continue;
            }

            layers.Add(new BackgroundLayer(
                paint,
                (GetLayerValue(styleMap, $"{prefix}-clip", index) ?? string.Empty).ToLowerInvariant(),
                (GetLayerValue(styleMap, $"{prefix}-origin", index) ?? string.Empty).ToLowerInvariant(),
                ParseBlendModeKeyword(GetLayerValue(styleMap, $"{prefix}-blend-mode", index)),
                index));
        }

        return layers;
    }

    private static bool TryExtractCssUrl(string value, out string url)
    {
        url = string.Empty;

        if (!value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var close = value.LastIndexOf(')');

        if (close < 4)
        {
            return false;
        }

        url = value[4..close].Trim().Trim('\'', '"').Trim();
        return url.Length > 0;
    }

    /// <summary>
    /// Parses one layer of `background-repeat`/`mask-repeat`: one keyword for both axes, two
    /// keywords (one per axis), or `repeat-x`/`repeat-y`. The initial value is `repeat`.
    /// </summary>
    private static (RenderBackgroundRepeat X, RenderBackgroundRepeat Y) ParseBackgroundRepeat(string? value)
    {
        var tokens = (value ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (RenderBackgroundRepeat.Repeat, RenderBackgroundRepeat.Repeat);
        }

        static RenderBackgroundRepeat ToRepeat(string token) => token.ToLowerInvariant() switch
        {
            "no-repeat" => RenderBackgroundRepeat.NoRepeat,
            "space" => RenderBackgroundRepeat.Space,
            "round" => RenderBackgroundRepeat.Round,
            _ => RenderBackgroundRepeat.Repeat,
        };

        return tokens[0].ToLowerInvariant() switch
        {
            "repeat-x" => (RenderBackgroundRepeat.Repeat, RenderBackgroundRepeat.NoRepeat),
            "repeat-y" => (RenderBackgroundRepeat.NoRepeat, RenderBackgroundRepeat.Repeat),
            _ when tokens.Length >= 2 => (ToRepeat(tokens[0]), ToRepeat(tokens[1])),
            _ => (ToRepeat(tokens[0]), ToRepeat(tokens[0])),
        };
    }

    /// <summary>
    /// Parses `background-position`. Only the common one- and two-value forms are recognized
    /// (keywords, percentages, lengths); the four-value `&lt;side&gt; &lt;offset&gt;` edge-relative
    /// syntax is not. The CSS initial value is `0% 0%` (top-left).
    /// </summary>
    private static (RenderBackgroundPositionComponent X, RenderBackgroundPositionComponent Y) ParseBackgroundPosition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (RenderBackgroundPositionComponent.Zero, RenderBackgroundPositionComponent.Zero);
        }

        var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (RenderBackgroundPositionComponent.Zero, RenderBackgroundPositionComponent.Zero);
        }

        if (tokens.Length == 1)
        {
            // A single token positions the X axis (or both, for `center`); the Y axis defaults to
            // centered, matching the CSS single-value `background-position` rule.
            var only = tokens[0].ToLowerInvariant();

            if (only is "top" or "bottom")
            {
                return (new RenderBackgroundPositionComponent(0.5f, 0f), ParsePositionComponent(only));
            }

            return (ParsePositionComponent(only), new RenderBackgroundPositionComponent(0.5f, 0f));
        }

        return (ParsePositionComponent(tokens[0]), ParsePositionComponent(tokens[1]));
    }

    private static RenderBackgroundPositionComponent ParsePositionComponent(string token)
    {
        return token.ToLowerInvariant() switch
        {
            "left" or "top" => RenderBackgroundPositionComponent.Zero,
            "right" or "bottom" => new RenderBackgroundPositionComponent(1f, 0f),
            "center" => new RenderBackgroundPositionComponent(0.5f, 0f),
            _ => ParsePositionLength(token),
        };
    }

    private static RenderBackgroundPositionComponent ParsePositionLength(string token)
    {
        var trimmed = token.Trim();

        if (trimmed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return new RenderBackgroundPositionComponent(percent / 100f, 0f);
        }

        if (TryParsePixelValue(trimmed, out var pixels))
        {
            return new RenderBackgroundPositionComponent(0f, pixels);
        }

        return RenderBackgroundPositionComponent.Zero;
    }

    /// <summary>
    /// Parses CSS `clip-path`'s basic-shape functional notations - `circle()`, `ellipse()`,
    /// `inset()`, `polygon()` - into a <see cref="RenderClipShape"/> already fully resolved
    /// against the element's own border box (this renderer's reference box for `clip-path`;
    /// `margin-box`/`padding-box`/`content-box`/`fill-box`/etc. box-selection keywords are not
    /// recognized, a deliberate, documented scope cut). Hand-rolled directly in this renderer
    /// rather than delegated to AngleSharp.Css (unlike `transform`/`filter`/gradients) - reflecting
    /// over that assembly finds no `clip-path`/basic-shape parser to delegate to at all, and each
    /// of these four grammars is small enough that a small local parser is proportionate. `url(#id)`
    /// (an SVG clip-path reference), `path()`, and the box-selection keywords alone are not
    /// recognized and simply disable clipping (as if `clip-path` were unset) rather than clipping
    /// to nothing - the same "recognized shapes only, everything else silently unsupported" scope
    /// cut already established for SVG filter primitives and 3D transform functions elsewhere in
    /// this renderer.
    /// </summary>
    private static RenderClipShape? ParseClipPath(Dictionary<string, string> styleMap, float boxWidth, float boxHeight)
    {
        if (!styleMap.TryGetValue("clip-path", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (TryExtractShapeArguments(text, "circle", out var circleArgs))
        {
            return ParseCircleClipShape(circleArgs, boxWidth, boxHeight);
        }

        if (TryExtractShapeArguments(text, "ellipse", out var ellipseArgs))
        {
            return ParseEllipseClipShape(ellipseArgs, boxWidth, boxHeight);
        }

        if (TryExtractShapeArguments(text, "inset", out var insetArgs))
        {
            return ParseInsetClipShape(insetArgs, boxWidth, boxHeight);
        }

        if (TryExtractShapeArguments(text, "polygon", out var polygonArgs))
        {
            return ParsePolygonClipShape(polygonArgs, boxWidth, boxHeight);
        }

        return null;
    }

    /// <summary>
    /// Moves a shape resolved against the border box (with its origin at the box's corner) to
    /// the position of the box, as the clip is applied in the coordinates of the page.
    /// </summary>
    private static RenderClipShape OffsetClipShape(RenderClipShape shape, float x, float y) => shape switch
    {
        RenderClipCircle circle => circle with { CenterX = circle.CenterX + x, CenterY = circle.CenterY + y },
        RenderClipEllipse ellipse => ellipse with { CenterX = ellipse.CenterX + x, CenterY = ellipse.CenterY + y },
        RenderClipInset inset => inset with { Rect = inset.Rect with { X = inset.Rect.X + x, Y = inset.Rect.Y + y } },
        RenderClipPolygon polygon => new RenderClipPolygon(polygon.Points.Select(point => (point.X + x, point.Y + y)).ToList()),
        _ => shape,
    };

    private static bool TryExtractShapeArguments(string text, string functionName, out string arguments)
    {
        arguments = string.Empty;
        var prefix = functionName + "(";

        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !text.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        arguments = text[prefix.Length..^1].Trim();
        return true;
    }

    private static (string ShapeArgs, string PositionArgs) SplitAtKeyword(string args)
    {
        var atIndex = args.IndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        return atIndex < 0 ? (args.Trim(), string.Empty) : (args[..atIndex].Trim(), args[(atIndex + 4)..].Trim());
    }

    private static (float CenterX, float CenterY) ResolveClipShapePosition(string positionArgs, float boxWidth, float boxHeight)
    {
        if (positionArgs.Length == 0)
        {
            return (boxWidth / 2f, boxHeight / 2f);
        }

        var tokens = positionArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var xComponent = tokens.Length > 0 ? ParsePositionComponent(tokens[0]) : new RenderBackgroundPositionComponent(0.5f, 0f);
        var yComponent = tokens.Length > 1 ? ParsePositionComponent(tokens[1]) : new RenderBackgroundPositionComponent(0.5f, 0f);

        return (
            (xComponent.Percentage * boxWidth) + xComponent.OffsetPixels,
            (yComponent.Percentage * boxHeight) + yComponent.OffsetPixels);
    }

    private static RenderClipShape ParseCircleClipShape(string args, float boxWidth, float boxHeight)
    {
        var (radiusText, positionArgs) = SplitAtKeyword(args);
        var (centerX, centerY) = ResolveClipShapePosition(positionArgs, boxWidth, boxHeight);

        var closestSide = Math.Min(Math.Min(centerX, boxWidth - centerX), Math.Min(centerY, boxHeight - centerY));
        var farthestSide = Math.Max(Math.Max(centerX, boxWidth - centerX), Math.Max(centerY, boxHeight - centerY));
        var diagonalReference = (float)Math.Sqrt(((boxWidth * boxWidth) + (boxHeight * boxHeight)) / 2.0);

        var radius = radiusText.ToLowerInvariant() switch
        {
            "" or "closest-side" => closestSide,
            "farthest-side" => farthestSide,
            _ => ResolveClipShapeLength(radiusText, diagonalReference, closestSide),
        };

        return new RenderClipCircle(centerX, centerY, Math.Max(0f, radius));
    }

    private static RenderClipShape ParseEllipseClipShape(string args, float boxWidth, float boxHeight)
    {
        var (radiiText, positionArgs) = SplitAtKeyword(args);
        var (centerX, centerY) = ResolveClipShapePosition(positionArgs, boxWidth, boxHeight);

        var closestSideX = Math.Min(centerX, boxWidth - centerX);
        var closestSideY = Math.Min(centerY, boxHeight - centerY);
        var farthestSideX = Math.Max(centerX, boxWidth - centerX);
        var farthestSideY = Math.Max(centerY, boxHeight - centerY);

        var radiiTokens = radiiText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var radiusXText = radiiTokens.Length > 0 ? radiiTokens[0].ToLowerInvariant() : string.Empty;
        var radiusYText = radiiTokens.Length > 1 ? radiiTokens[1].ToLowerInvariant() : string.Empty;

        var radiusX = radiusXText switch
        {
            "" or "closest-side" => closestSideX,
            "farthest-side" => farthestSideX,
            _ => ResolveClipShapeLength(radiusXText, boxWidth, closestSideX),
        };

        var radiusY = radiusYText switch
        {
            "" or "closest-side" => closestSideY,
            "farthest-side" => farthestSideY,
            _ => ResolveClipShapeLength(radiusYText, boxHeight, closestSideY),
        };

        return new RenderClipEllipse(centerX, centerY, Math.Max(0f, radiusX), Math.Max(0f, radiusY));
    }

    private static RenderClipShape ParseInsetClipShape(string args, float boxWidth, float boxHeight)
    {
        var roundIndex = args.IndexOf(" round ", StringComparison.OrdinalIgnoreCase);
        var offsetsText = roundIndex < 0 ? args : args[..roundIndex];
        var radiusText = roundIndex < 0 ? string.Empty : args[(roundIndex + 7)..].Trim();

        var offsetTokens = offsetsText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var top = offsetTokens.Length > 0 ? ResolveClipShapeLength(offsetTokens[0], boxHeight, 0f) : 0f;
        var right = offsetTokens.Length > 1 ? ResolveClipShapeLength(offsetTokens[1], boxWidth, 0f) : top;
        var bottom = offsetTokens.Length > 2 ? ResolveClipShapeLength(offsetTokens[2], boxHeight, 0f) : top;
        var left = offsetTokens.Length > 3 ? ResolveClipShapeLength(offsetTokens[3], boxWidth, 0f) : right;

        var rect = new RenderRect(left, top, Math.Max(0f, boxWidth - left - right), Math.Max(0f, boxHeight - top - bottom));

        // Only a single, uniform corner radius is supported (applied to all four corners) - a
        // deliberate, documented scope cut compared to `inset()`'s full per-corner/elliptical
        // `round <border-radius>` grammar, mirroring how a mixed-width `border` similarly falls
        // back to a simpler approximation elsewhere in this renderer rather than attempting every
        // combination the full spec grammar allows.
        var radii = RenderCornerRadii.Zero;

        if (radiusText.Length > 0)
        {
            var uniformRadius = Math.Max(0f, ResolveClipShapeLength(radiusText.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], Math.Min(boxWidth, boxHeight), 0f));
            radii = new RenderCornerRadii(uniformRadius, uniformRadius, uniformRadius, uniformRadius, uniformRadius, uniformRadius, uniformRadius, uniformRadius);
        }

        return new RenderClipInset(rect, radii.ClampToBox(rect.Width, rect.Height));
    }

    private static RenderClipShape ParsePolygonClipShape(string args, float boxWidth, float boxHeight)
    {
        var segments = args.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var points = new List<(float X, float Y)>();

        foreach (var segment in segments)
        {
            // The optional leading `nonzero`/`evenodd` fill-rule keyword shares a segment with the
            // first vertex (`"polygon(nonzero, 0 0, ...)"` splits its first comma-separated part as
            // `"nonzero 0 0"`) - this renderer's polygon clip fills unconditionally (Skia's default
            // fill rule), so the keyword itself is simply skipped rather than affecting rendering.
            var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !token.Equals("nonzero", StringComparison.OrdinalIgnoreCase) && !token.Equals("evenodd", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (tokens.Length < 2)
            {
                continue;
            }

            var x = ResolveClipShapeLength(tokens[0], boxWidth, 0f);
            var y = ResolveClipShapeLength(tokens[1], boxHeight, 0f);
            points.Add((x, y));
        }

        return new RenderClipPolygon(points);
    }

    /// <summary>
    /// Resolves a single `clip-path` shape length token - a percentage (against
    /// <paramref name="percentageReference"/>, which differs per shape/axis: a circle's radius
    /// resolves against the box diagonal, an ellipse's/`inset()`'s/`polygon()`'s against the
    /// relevant single axis) or a plain pixel length - falling back to
    /// <paramref name="fallback"/> for anything unparseable (an unsupported keyword like
    /// `closest-corner`, or a malformed token) rather than defaulting to zero, which would
    /// otherwise silently collapse the shape.
    /// </summary>
    private static float ResolveClipShapeLength(string token, float percentageReference, float fallback)
    {
        var trimmed = token.Trim();

        if (trimmed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return percent / 100f * percentageReference;
        }

        return TryParsePixelValue(trimmed, out var pixels) ? pixels : fallback;
    }

    /// <summary>
    /// Parses the CSS `transform` property plus `transform-origin` into a single, fully-resolved
    /// <see cref="RenderTransform2D"/> - delegating each individual transform function's own
    /// parsing and matrix computation entirely to AngleSharp.Css's own
    /// <see cref="TransformParser"/>/<see cref="ICssTransformFunctionValue.ComputeMatrix"/>,
    /// rather than re-implementing CSS transform function parsing, length/percentage/angle
    /// resolution, or the underlying trigonometry in this renderer. This method's own
    /// responsibility is limited to: looping <see cref="TransformParser.ParseTransform"/> across
    /// the space-separated function list (it parses one function per call and advances the source
    /// itself - including skipping the whitespace between calls, confirmed empirically rather than
    /// assumed - returning <see langword="null"/> cleanly once the source is exhausted, which is
    /// what ends the loop), multiplying the resulting per-function matrices together in CSS's own
    /// left-to-right composition order (see <see cref="RenderTransform2D.Multiply"/>), converting
    /// AngleSharp.Css's own <see cref="TransformMatrix"/> into this renderer's flat 2D `a,b,c,d,e,f`
    /// representation (see <see cref="ConvertToRenderTransform"/> for the exact field mapping,
    /// which is not the naive one), and wrapping the composed result around `transform-origin` -
    /// AngleSharp.Css's own `ComputeMatrix` takes no origin parameter, so that translate/apply/
    /// translate-back is this renderer's to do regardless of how the individual functions resolve.
    ///
    /// Resolved eagerly here (unlike a gradient's or background-image's own paint-time-deferred
    /// geometry) because a transform's inputs - percentage `translate()` values, the default
    /// `transform-origin` - only ever need the element's own already-known border-box dimensions,
    /// never anything (like an image's natural size) that is not available until paint time.
    ///
    /// This renderer never special-cases individual transform functions or guards against
    /// AngleSharp.Css bugs locally - `ConvertToRenderTransform` always trusts whatever
    /// <see cref="ICssTransformFunctionValue.ComputeMatrix"/> returns, unconditionally. A
    /// `TransformMatrix` is a genuinely general matrix regardless of which function produced it;
    /// for a function whose own effect is entirely 2D, the third dimension simply stays at its own
    /// identity value, so there is nothing to specialize for a "2D case" - the same six components
    /// (M11/M12/M21/M22/Tx/Ty) are read the same way for every function. Any bug in what
    /// AngleSharp.Css itself computes for a given function is AngleSharp.Css's own bug to fix -
    /// this renderer reports and reproduces it there (with a failing test in that project's own
    /// suite) rather than working around it here, matching this project's own "we do not touch the
    /// CSS ourselves" policy. `translate`/`translateX`/`translateY` need one exception to "just call
    /// AngleSharp.Css normally", though: the raw value is parsed directly here rather than through
    /// AngleSharp.Css's own computed-style cascade, because that cascade path has a separate,
    /// confirmed crash for exactly those three functions - see `TryExtractTransformDeclaration`'s
    /// own remarks for why that cascade path has to be avoided entirely, independent of this method.
    ///
    /// `TransformParser` is AngleSharp.Css's own general-purpose transform-function parser and
    /// recognizes 3D functions (`translate3d`, `rotate3d`, `matrix3d`, `perspective`, ...) too -
    /// this method does not filter them out before parsing. For one of them, `translateZ`,
    /// `ComputeMatrix`'s 2D component (M11/M12/M21/M22/Tx/Ty) happens to come back as pure identity
    /// (only its Z-only shift is non-zero, which this renderer never reads), so it is harmlessly a
    /// no-op through the exact same path real 2D functions use - confirmed with a structural test,
    /// not assumed. A 3D function whose effect genuinely depends on a Z axis this renderer has no
    /// projection for at all (`rotate3d`, `perspective`, ...) would not be meaningfully
    /// representable even with a bug-free `ComputeMatrix`; this renderer's flat, backend-agnostic
    /// display list simply has no camera/projection concept to give such a function real meaning -
    /// a deliberate, permanent scope cut, unrelated to any upstream bug.
    /// </summary>
    private static RenderTransform2D ParseCssTransform(Dictionary<string, string> styleMap, float borderBoxX, float borderBoxY, float borderBoxWidth, float borderBoxHeight, float fontSize)
    {
        // The individual transform properties apply before `transform`, in the order translate,
        // rotate, scale (CSS Transforms 2), all around the same `transform-origin`.
        var value = string.Join(' ', new[]
        {
            ToTranslateFunction(styleMap),
            ToRotateFunction(styleMap),
            ToScaleFunction(styleMap),
            styleMap.TryGetValue("transform", out var transformValue) && !string.Equals(transformValue.Trim(), "none", StringComparison.OrdinalIgnoreCase) ? transformValue.Trim() : null,
        }.Where(static function => !string.IsNullOrWhiteSpace(function)));

        if (value.Length == 0)
        {
            return RenderTransform2D.Identity;
        }

        var dimensions = new CssTransformRenderDimensions(borderBoxWidth, borderBoxHeight, fontSize);
        var source = new StringSource(value);
        var functionsMatrix = RenderTransform2D.Identity;

        while (TransformParser.ParseTransform(source) is { } functionValue)
        {
            var next = ConvertToRenderTransform(functionValue, dimensions);
            functionsMatrix = RenderTransform2D.Multiply(functionsMatrix, next);
        }

        if (functionsMatrix.IsIdentity)
        {
            return RenderTransform2D.Identity;
        }

        // transform-origin shifts the whole composed function chain to pivot around a point other
        // than the box's own top-left corner (the origin every individual function's matrix
        // otherwise implicitly pivots/scales/skews around): translate to the origin, apply the
        // functions, translate back. The origin itself is resolved relative to the box (a fraction/
        // offset of its own width/height), but PushTransformCommand's matrix is concatenated onto
        // the canvas *before* any of this element's own commands run - which still carry their
        // ordinary absolute page coordinates (borderBoxX/Y, not box-local 0..width/0..height) - so
        // the pivot point has to be expressed in that same absolute space (borderBoxX/Y + the
        // relative origin), not just the relative offset within the box. Getting this wrong was a
        // real bug caught while building this feature: verified by hand-checking the resulting
        // matrix against a box positioned away from the page origin, where a box-relative-only
        // pivot rotated the box around the wrong point entirely (only appearing correct for a box
        // that happened to sit at page position (0, 0), where relative and absolute coincide).
        var (originOffsetX, originOffsetY) = ParseTransformOrigin(styleMap, borderBoxWidth, borderBoxHeight);
        var originX = borderBoxX + originOffsetX;
        var originY = borderBoxY + originOffsetY;
        var toOrigin = RenderTransform2D.Translate(originX, originY);
        var fromOrigin = RenderTransform2D.Translate(-originX, -originY);
        return RenderTransform2D.Multiply(toOrigin, RenderTransform2D.Multiply(functionsMatrix, fromOrigin));
    }

    /// <summary>
    /// An element's current authored `transform`, read the way <see cref="CreateStyleMap"/> reads
    /// it (the extracted inline declaration, else the cascaded one) - the computed value only when
    /// it uses custom properties.
    /// </summary>
    internal static string? ReadAuthoredTransform(IElement element)
    {
        var inline = element.GetAttribute("data-render-transform");

        if (!string.IsNullOrWhiteSpace(inline))
        {
            return inline;
        }

        var window = element.Owner?.DefaultView;

        if (window is null)
        {
            return null;
        }

        var device = window.Document.Context.GetService<IRenderDevice>() ?? new DefaultRenderDevice();
        var styleCollection = window.GetStyleCollection(device);
        var cascaded = styleCollection.GetDeclarations(element).GetPropertyValue("transform");

        // custom properties are only substituted in the computed value
        return UsesExplicitValue(cascaded) || string.IsNullOrWhiteSpace(cascaded)
            ? cascaded
            : styleCollection.ComputeDeclarations(element).GetPropertyValue("transform");
    }

    /// <summary>
    /// The 2D matrix of a transform list that needs no box to resolve (no percentages), for
    /// interpolating mismatched lists; <see langword="null"/> otherwise.
    /// </summary>
    internal static RenderTransform2D? TryComputeTransformMatrix(string value)
    {
        if (value.Contains('%'))
        {
            return null;
        }

        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return RenderTransform2D.Identity;
        }

        var dimensions = new CssTransformRenderDimensions(0d, 0d, 16d);
        var source = new StringSource(value);
        var matrix = RenderTransform2D.Identity;
        var any = false;

        while (TransformParser.ParseTransform(source) is { } functionValue)
        {
            matrix = RenderTransform2D.Multiply(matrix, ConvertToRenderTransform(functionValue, dimensions));
            any = true;
        }

        return any ? matrix : null;
    }

    private static string[] GetIndividualTransformParts(Dictionary<string, string> styleMap, string propertyName) =>
        styleMap.TryGetValue(propertyName, out var value) && !string.IsNullOrWhiteSpace(value) && !string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)
            ? value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

    /// <summary>`translate: x [y [z]]` as the equivalent transform function.</summary>
    private static string? ToTranslateFunction(Dictionary<string, string> styleMap)
    {
        var parts = GetIndividualTransformParts(styleMap, "translate");

        return parts.Length switch
        {
            0 => null,
            1 => $"translate({parts[0]})",
            2 => $"translate({parts[0]}, {parts[1]})",
            _ => $"translate3d({parts[0]}, {parts[1]}, {parts[2]})",
        };
    }

    /// <summary>`rotate: [x | y | z | &lt;vector&gt;] angle` as the equivalent transform function.</summary>
    private static string? ToRotateFunction(Dictionary<string, string> styleMap)
    {
        var parts = GetIndividualTransformParts(styleMap, "rotate");

        return parts.Length switch
        {
            0 => null,
            1 => $"rotate({parts[0]})",
            2 => parts[0].ToLowerInvariant() switch
            {
                "x" => $"rotateX({parts[1]})",
                "y" => $"rotateY({parts[1]})",
                "z" => $"rotate({parts[1]})",
                _ => null,
            },
            4 => $"rotate3d({parts[0]}, {parts[1]}, {parts[2]}, {parts[3]})",
            _ => null,
        };
    }

    /// <summary>`scale: x [y [z]]` as the equivalent transform function.</summary>
    private static string? ToScaleFunction(Dictionary<string, string> styleMap)
    {
        var parts = GetIndividualTransformParts(styleMap, "scale");

        return parts.Length switch
        {
            0 => null,
            1 => $"scale({parts[0]})",
            2 => $"scale({parts[0]}, {parts[1]})",
            _ => $"scale3d({parts[0]}, {parts[1]}, {parts[2]})",
        };
    }

    /// <summary>
    /// Converts one already-parsed CSS transform function's own <see cref="TransformMatrix"/> into
    /// this renderer's flat 2D <see cref="RenderTransform2D"/>, unconditionally - this renderer
    /// always trusts the matrix AngleSharp.Css computes directly, with no NaN/exception guarding
    /// and no 2D-vs-3D special-casing of its own. A <see cref="TransformMatrix"/> is a genuinely
    /// general matrix regardless of which CSS function produced it; for a function whose own effect
    /// is entirely 2D, the third dimension simply stays at its own identity value, so reading off
    /// the same six 2D-relevant components (M11/M12/M21/M22/Tx/Ty) works uniformly for every
    /// function - there is nothing here for this renderer to specialize. Any bug in what
    /// AngleSharp.Css itself computes belongs to AngleSharp.Css, not to a defensive workaround in
    /// this method - report and fix it there (with a reproducing test in its own suite) instead.
    /// </summary>
    private static RenderTransform2D ConvertToRenderTransform(ICssTransformFunctionValue functionValue, IRenderDimensions dimensions)
    {
        var matrix = functionValue.ComputeMatrix(dimensions);

        // AngleSharp.Css's TransformMatrix uses a row-vector convention -
        // x' = M11*x + M12*y + Tx, y' = M21*x + M22*y + Ty - which is *not* the naive mapping onto
        // CSS's own column-vector matrix(a,b,c,d,e,f) (x' = a*x + c*y + e, y' = b*x + d*y + f) a
        // reader might expect. Verified empirically (not assumed) against a known skew(10deg,5deg)
        // result: M12 came back as tan(10deg) - the coefficient of y in the x' equation, i.e. CSS's
        // "c" - and M21 came back as tan(5deg) - the coefficient of x in the y' equation, i.e. CSS's
        // "b" - the opposite of what matching M12 to "b" and M21 to "c" by position would give.
        return new RenderTransform2D(
            (float)matrix.M11,
            (float)matrix.M21,
            (float)matrix.M12,
            (float)matrix.M22,
            (float)matrix.Tx,
            (float)matrix.Ty);
    }

    private readonly struct CssTransformRenderDimensions(double renderWidth, double renderHeight, double fontSize) : IRenderDimensions
    {
        public double RenderWidth { get; } = renderWidth;

        public double RenderHeight { get; } = renderHeight;

        public double FontSize { get; } = fontSize;
    }

    /// <summary>
    /// Parses `transform-origin` into pixel coordinates relative to the element's own border box -
    /// the CSS initial value, "50% 50%", is the box's own center. Reuses `ParsePositionComponent`'s
    /// keyword/percentage/length tokenizing (the identical single-axis grammar `background-position`
    /// already uses: `left`/`center`/`right`/`top`/`bottom`, a percentage, or a length) rather than
    /// re-deriving it, but resolves its `Percentage`/`OffsetPixels` breakdown with
    /// `transform-origin`'s own formula (a position *within* the box) instead of
    /// `background-position`'s (a position within the *remaining* space after subtracting the
    /// image's own size) - the two properties share a grammar but not a resolution formula.
    /// </summary>
    private static (float X, float Y) ParseTransformOrigin(Dictionary<string, string> styleMap, float borderBoxWidth, float borderBoxHeight)
    {
        var xComponent = new RenderBackgroundPositionComponent(0.5f, 0f);
        var yComponent = new RenderBackgroundPositionComponent(0.5f, 0f);

        if (styleMap.TryGetValue("transform-origin", out var value) && !string.IsNullOrWhiteSpace(value))
        {
            var tokens = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 1)
            {
                var only = tokens[0].ToLowerInvariant();

                if (only is "top" or "bottom")
                {
                    yComponent = ParsePositionComponent(only);
                }
                else
                {
                    xComponent = ParsePositionComponent(only);
                }
            }
            else if (tokens.Length >= 2)
            {
                xComponent = ParsePositionComponent(tokens[0]);
                yComponent = ParsePositionComponent(tokens[1]);
            }
        }

        var x = (xComponent.Percentage * borderBoxWidth) + xComponent.OffsetPixels;
        var y = (yComponent.Percentage * borderBoxHeight) + yComponent.OffsetPixels;
        return (x, y);
    }

    /// <summary>
    /// Parses CSS `opacity` via AngleSharp.Css's own typed `GetOpacity()` accessor - unlike
    /// `filter`, this is an ordinary numeric property with no function-value syntax, so
    /// AngleSharp.Css computes it normally with nothing for this renderer to work around. The CSS
    /// initial value is `1` (fully opaque); a value outside `0..1` is clamped, per spec.
    /// </summary>
    private static float ParseCssOpacity(Dictionary<string, string> styleMap)
    {
        if (!styleMap.TryGetValue("opacity", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return 1f;
        }

        return float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity)
            ? Math.Clamp(opacity, 0f, 1f)
            : 1f;
    }

    /// <summary>
    /// Parses `filter` via AngleSharp.Css's own `FilterParser.ParseFilter`/`ICssFilterFunctionValue`
    /// (fixed upstream - AngleSharp.Css previously had no structured `filter` support at all, so
    /// this renderer had to hand-parse the raw text itself, mirroring the CSS-gradient precedent;
    /// it now mirrors the `transform` precedent instead) - AngleSharp.Css handles the outer
    /// function-list syntax (name/parenthesis extraction, including nested parens) and gives each
    /// function's own name plus its raw, still-unparsed argument text as a single
    /// <c>Arguments</c> entry; this renderer still interprets what
    /// each function *means* (its own argument grammar, and the CSS Filter Effects spec's per-function
    /// color-matrix/blur-radius semantics - see <see cref="ConvertToRenderFilterFunction"/>), since
    /// that is a rendering concern AngleSharp.Css does not (and should not) resolve for it, the same
    /// division of labor `transform`'s `ConvertToRenderTransform` already established for
    /// `ComputeMatrix`. `url(#filterId)` (an SVG filter reference) and any other unrecognized
    /// function are silently skipped rather than aborting the whole chain, mirroring how an
    /// unsupported SVG `&lt;filter&gt;` primitive passes its input through unchanged in
    /// `SvgFilterBuilder` instead of breaking that chain.
    /// </summary>
    private static IReadOnlyList<RenderFilterFunction> ParseCssFilter(Dictionary<string, string> styleMap, string propertyName = "filter")
    {
        if (!styleMap.TryGetValue(propertyName, out var value) ||
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (FilterParser.ParseFilter(new StringSource(value)) is not CssFilterValue parsed)
        {
            return [];
        }

        var functions = new List<RenderFilterFunction>();

        foreach (var cssFunction in parsed.Functions)
        {
            if (ConvertToRenderFilterFunction(cssFunction, out var function))
            {
                functions.Add(function);
            }
        }

        return functions;
    }

    private static bool ConvertToRenderFilterFunction(ICssFilterFunctionValue cssFunction, out RenderFilterFunction function)
    {
        function = null!;
        var argsText = cssFunction.Arguments.Length > 0 ? cssFunction.Arguments[0].CssText.Trim() : string.Empty;

        switch (cssFunction.Name.ToLowerInvariant())
        {
            case "blur":
                function = RenderFilterFunction.Blur(Math.Max(0f, ParseLengthValue(argsText, defaultValue: 0f, allowAuto: false)));
                return true;
            case "brightness":
                function = RenderFilterFunction.Brightness(Math.Max(0f, ParseFilterAmount(argsText, 1f)));
                return true;
            case "contrast":
                function = RenderFilterFunction.Contrast(Math.Max(0f, ParseFilterAmount(argsText, 1f)));
                return true;
            case "grayscale":
                function = RenderFilterFunction.Grayscale(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "invert":
                function = RenderFilterFunction.Invert(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "opacity":
                function = RenderFilterFunction.Opacity(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "saturate":
                function = RenderFilterFunction.Saturate(Math.Max(0f, ParseFilterAmount(argsText, 1f)));
                return true;
            case "sepia":
                function = RenderFilterFunction.Sepia(Math.Clamp(ParseFilterAmount(argsText, 1f), 0f, 1f));
                return true;
            case "hue-rotate":
                function = RenderFilterFunction.HueRotate(ParseAngle(argsText));
                return true;
            case "drop-shadow":
                function = ParseDropShadowFilterFunction(argsText);
                return true;
            default:
                return false;
        }
    }

    // `grayscale(90%)` and `grayscale(0.9)` are equivalent per spec - a percentage argument is
    // normalized to the same 0..1 fraction a bare number already is, so every downstream consumer
    // (RenderFilterFunction.Amount) only ever has to handle one representation.
    private static float ParseFilterAmount(string value, float defaultValue)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return defaultValue;
        }

        if (trimmed.EndsWith('%') &&
            float.TryParse(trimmed[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
        {
            return percentage / 100f;
        }

        return float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : defaultValue;
    }

    // `drop-shadow(<offset-x> <offset-y> <blur-radius>? <color>?)` - CSS allows the color argument
    // either first or last, so this classifies each whitespace-separated token by its own shape
    // (a leading digit/sign/decimal point is a length, anything else is a color) rather than
    // assuming a fixed position, then assigns lengths to offsetX/offsetY/blurRadius in the order
    // they were seen.
    private static RenderFilterFunction ParseDropShadowFilterFunction(string argsText)
    {
        var offsetX = 0f;
        var offsetY = 0f;
        var blurRadius = 0f;
        var color = RenderColor.Black;
        var lengthIndex = 0;

        foreach (var token in SplitTopLevelWhitespaceList(argsText))
        {
            var trimmed = token.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            var firstChar = trimmed[0];

            if (char.IsDigit(firstChar) || firstChar is '-' or '+' or '.')
            {
                var length = ParseLengthValue(trimmed, 0f, allowAuto: false);

                switch (lengthIndex)
                {
                    case 0:
                        offsetX = length;
                        break;
                    case 1:
                        offsetY = length;
                        break;
                    default:
                        blurRadius = length;
                        break;
                }

                lengthIndex++;
            }
            else
            {
                color = ParseColor(trimmed, color);
            }
        }

        return RenderFilterFunction.DropShadow(offsetX, offsetY, Math.Max(0f, blurRadius), color);
    }

    // Splits a value on whitespace at paren-depth 0 only, so a nested function call within one of
    // the tokens (e.g. a `rgb(255, 0, 0)` color argument) is never split apart. Used to tokenize
    // `drop-shadow`'s own space-separated argument list - the outer `filter` function list itself
    // is now split by AngleSharp.Css's own FilterParser instead (see ParseCssFilter's remarks).
    private static string[] SplitTopLevelWhitespaceList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var character in value)
        {
            if (character == '(')
            {
                depth++;
                current.Append(character);
            }
            else if (character == ')')
            {
                depth = Math.Max(0, depth - 1);
                current.Append(character);
            }
            else if (char.IsWhiteSpace(character) && depth == 0)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }

    /// <summary>
    /// Parses `background-size`. The CSS initial value is `auto` (the image's own natural size).
    /// </summary>
    private static RenderBackgroundSize ParseBackgroundSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RenderBackgroundSize.Auto;
        }

        var trimmed = value.Trim();

        if (string.Equals(trimmed, "cover", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderBackgroundSize(RenderBackgroundSizeKind.Cover);
        }

        if (string.Equals(trimmed, "contain", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderBackgroundSize(RenderBackgroundSizeKind.Contain);
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return RenderBackgroundSize.Auto;
        }

        var width = ParseSizeAxisToken(tokens[0]);
        // A single-value `background-size` sizes only the width explicitly; the height is always
        // `auto` (proportional to the image's aspect ratio), never a copy of the width token.
        var height = tokens.Length > 1 ? ParseSizeAxisToken(tokens[1]) : new RenderBackgroundSizeAxis(true, false, 0f);

        return new RenderBackgroundSize(RenderBackgroundSizeKind.Explicit, width, height);
    }

    private static RenderBackgroundSizeAxis ParseSizeAxisToken(string token)
    {
        var trimmed = token.Trim();

        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new RenderBackgroundSizeAxis(true, false, 0f);
        }

        if (trimmed.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return new RenderBackgroundSizeAxis(false, true, percent / 100f);
        }

        if (TryParsePixelValue(trimmed, out var pixels))
        {
            return new RenderBackgroundSizeAxis(false, false, pixels);
        }

        return new RenderBackgroundSizeAxis(true, false, 0f);
    }

    // AngleSharp.Css now parses gradients into structured values (`GradientParser.ParseGradient`,
    // mirroring `TransformParser`/`FilterParser` exactly), so this no longer hand-parses the raw
    // `background-image` text itself - it delegates the outer function/argument grammar and reads
    // each already-typed piece (angle, stop color/position, center point) off the result, the same
    // division of labor already established for `transform`/`filter`: AngleSharp.Css computes the
    // pure-CSS structure, this renderer still owns turning it into its own backend-agnostic
    // `RenderGradient`. `ParseGradient` returns null cleanly for anything it does not recognize
    // (including a plain color), so no name-prefix pre-check is needed before calling it.
    private static RenderPaint ParseGradientPaint(string rawValue, RenderColor fallbackColor, float emBase = 16f)
    {
        var source = new StringSource(rawValue.Trim());

        return source.ParseGradient() switch
        {
            CssLinearGradientValue linear => new RenderGradientPaint(ConvertLinearGradient(linear, fallbackColor, emBase)),
            CssRadialGradientValue radial => new RenderGradientPaint(ConvertRadialGradient(radial, fallbackColor, emBase)),
            CssConicGradientValue conic => new RenderGradientPaint(ConvertConicGradient(conic, fallbackColor)),
            _ => new RenderColorPaint(fallbackColor),
        };
    }

    private static RenderGradient ConvertLinearGradient(CssLinearGradientValue gradient, RenderColor fallbackColor, float emBase)
    {
        // `.Angle` already defaults correctly to 180deg ("to bottom") when no direction was
        // authored - confirmed against AngleSharp.Css's own test suite, unlike the conic-gradient
        // equivalent below. Re-parsing its own `.CssText` (rather than reading a numeric degree
        // value directly) reuses the exact same deg/grad/turn/rad-unit handling `filter`'s
        // `hue-rotate` already relies on (`TryParseAngle`), so a keyword direction like "to right"
        // (which AngleSharp.Css resolves to a plain `CssAngleValue` internally, per `Map.GradientAngles`)
        // and an explicit `135deg` both flow through the same one conversion path - both are CSS's
        // own "0deg points up, clockwise" convention. `CreateLinearGradientShader` (unlike its conic
        // counterpart, which applies this same correction itself via a rotation matrix) expects
        // `AngleDegrees` pre-converted to its own "0 points right, clockwise" screen convention, so
        // the -90 shift has to happen here - confirmed by a real, visible bug this surfaced:
        // `to right` (CSS 90deg) rendered as horizontal stripes instead of vertical ones without it.
        var angleDegrees = ParseAngle(gradient.Angle.CssText) - 90f;
        var stops = ConvertGradientStops(gradient.Stops, fallbackColor, isConic: false, emBase);
        return new RenderGradient(RenderGradientKind.Linear, stops, AngleDegrees: angleDegrees, Repeating: gradient.IsRepeating);
    }

    private static RenderGradient ConvertRadialGradient(CssRadialGradientValue gradient, RenderColor fallbackColor, float emBase)
    {
        var (centerX, centerY) = ParsePosition(gradient.Position.CssText);
        var sizeKind = RenderGradientSizeKind.FarthestCorner;
        float? explicitRadiusX = null;
        float? explicitRadiusY = null;

        if (gradient.Mode != CssRadialGradientValue.SizeMode.None)
        {
            sizeKind = gradient.Mode switch
            {
                CssRadialGradientValue.SizeMode.ClosestCorner => RenderGradientSizeKind.ClosestCorner,
                CssRadialGradientValue.SizeMode.ClosestSide => RenderGradientSizeKind.ClosestSide,
                CssRadialGradientValue.SizeMode.FarthestSide => RenderGradientSizeKind.FarthestSide,
                _ => RenderGradientSizeKind.FarthestCorner,
            };
        }
        else if (gradient.MajorRadius.CssText != CssLengthValue.Full.CssText || gradient.MinorRadius.CssText != CssLengthValue.Full.CssText)
        {
            // `Mode.None` alone does not distinguish "no size/radius was authored at all" from "an
            // explicit radius was given" - `CssRadialGradientValue` has no public signal for that
            // beyond this: an unset radius's own getter substitutes `CssLengthValue.Full` (100%)
            // for the `null` it actually holds internally, with no way to tell the two apart from
            // the outside. Comparing against that same sentinel is therefore the only available
            // signal; the one case it cannot distinguish - an *explicit* ellipse radius that
            // legitimately happens to be exactly 100% on both axes - is rare enough (and visually
            // close to `farthest-corner` in most box aspect ratios anyway) to accept as a known,
            // narrow approximation rather than threading extra state through GradientParser for it.
            sizeKind = RenderGradientSizeKind.Explicit;

            if (TryParsePixelValue(gradient.MajorRadius.CssText, out var radiusX))
            {
                explicitRadiusX = radiusX;
            }

            explicitRadiusY = TryParsePixelValue(gradient.MinorRadius.CssText, out var radiusY) ? radiusY : explicitRadiusX;
        }

        var stops = ConvertGradientStops(gradient.Stops, fallbackColor, isConic: false, emBase);
        return new RenderGradient(
            RenderGradientKind.Radial,
            stops,
            CenterX: centerX,
            CenterY: centerY,
            IsCircle: gradient.IsCircle,
            Repeating: gradient.IsRepeating,
            SizeKind: sizeKind,
            ExplicitRadiusX: explicitRadiusX,
            ExplicitRadiusY: explicitRadiusY);
    }

    private static RenderGradient ConvertConicGradient(CssConicGradientValue gradient, RenderColor fallbackColor)
    {
        // Unlike linear's `.Angle`, conic's own default-angle fallback was a confirmed AngleSharp.Css
        // bug (reported and fixed upstream, AngleSharp.Css.Tests/Values/Gradient.cs
        // ConicGradientDefaultAngleIsZeroNotHalfCircle) - it used to report 180deg for an omitted
        // `from` clause instead of the CSS spec's own 0deg default, so this renderer must build
        // against a version with that fix rather than working around it locally.
        var angleDegrees = ParseAngle(gradient.Angle.CssText);
        var (centerX, centerY) = ParsePosition(gradient.Center.CssText);
        var stops = ConvertGradientStops(gradient.Stops, fallbackColor, isConic: true);
        return new RenderGradient(RenderGradientKind.Conic, stops, AngleDegrees: angleDegrees, CenterX: centerX, CenterY: centerY, Repeating: gradient.IsRepeating);
    }

    private static RenderColor ToRenderColor(CssColorValue color) => new(color.R, color.G, color.B, color.A);

    private static readonly string[] PositionKeywords = ["left", "right", "top", "bottom", "center"];

    /// <summary>
    /// Parses a CSS `&lt;position&gt;` value (1-2 tokens, keywords and/or percentages, in either
    /// order for keywords) into fractional (0-1) X/Y coordinates. Absolute lengths (`at 20px
    /// 10px`) and the 4-value edge-offset syntax are not supported and fall back to center.
    /// </summary>
    private static (float X, float Y) ParsePosition(string text)
    {
        var tokens = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            return (0.5f, 0.5f);
        }

        float? x = null;
        float? y = null;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "left":
                    x = 0f;
                    break;
                case "right":
                    x = 1f;
                    break;
                case "top":
                    y = 0f;
                    break;
                case "bottom":
                    y = 1f;
                    break;
            }
        }

        var positionalTokens = tokens.Where(token => !PositionKeywords.Contains(token.ToLowerInvariant())).ToArray();
        var assignedX = x is not null;

        foreach (var token in positionalTokens)
        {
            var value = ParseStopPosition(token);

            if (!assignedX)
            {
                x = value;
                assignedX = true;
            }
            else if (y is null)
            {
                y = value;
            }
        }

        return (x ?? 0.5f, y ?? 0.5f);
    }

    internal static string[] SplitTopLevelCommaList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var character in value)
        {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (character == ',' && depth == 0)
            {
                var part = current.ToString().Trim();
                if (part.Length > 0)
                {
                    parts.Add(part);
                }

                current.Clear();
                continue;
            }

            current.Append(character);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
        {
            parts.Add(last);
        }

        return parts.ToArray();
    }

    /// <summary>
    /// Converts AngleSharp.Css's own <see cref="CssGradientStopValue"/> list (already split and
    /// individually parsed by <see cref="GradientParser"/>) into this renderer's backend-agnostic
    /// stops - the counterpart to the old text-splitting version this replaced, kept as narrow a
    /// change as possible: each stop's own color comes directly off <c>CssGradientStopValue.Color</c>
    /// (a real, already-resolved <see cref="CssColorValue"/> - no re-parsing needed, unlike before),
    /// while its position still goes through <see cref="ParseStopPosition"/> exactly as it always
    /// did, just fed that stop's own <c>Location.CssText</c> instead of a hand-split token.
    /// </summary>
    private static IReadOnlyList<RenderGradientStop> ConvertGradientStops(ICssValue[] rawStops, RenderColor fallbackColor, bool isConic, float emBase = 16f)
    {
        var stops = rawStops.OfType<CssGradientStopValue>().ToArray();

        if (stops.Length == 0)
        {
            return [new RenderGradientStop(0f, fallbackColor)];
        }

        var result = new List<RenderGradientStop>(stops.Length);

        for (var index = 0; index < stops.Length; index++)
        {
            var stop = stops[index];
            var color = ToRenderColor(stop.Color);
            var autoPosition = stops.Length == 1 ? 0f : (index / (float)Math.Max(1, stops.Length - 1));

            if (stop.IsUndetermined)
            {
                result.Add(new RenderGradientStop(autoPosition, color, IsAuto: true));
                continue;
            }

            var positionText = stop.Location.CssText;

            if (!isConic && TryParsePercentagePlusLength(positionText, emBase, out var fraction, out var offsetPixels))
            {
                result.Add(new RenderGradientStop(fraction, color, OffsetPixels: offsetPixels));
                continue;
            }

            if (!isConic && TryParseStopLength(positionText, emBase, out var pixels))
            {
                // An absolute-length stop position ("red 10px") cannot become a fraction until
                // the gradient's own rendered geometry (line length/radius) is known, so the raw
                // pixel value is carried through and resolved by the backend at paint time.
                result.Add(new RenderGradientStop(autoPosition, color, pixels));
            }
            else
            {
                result.Add(new RenderGradientStop(ParseStopPosition(positionText, isConic), color));
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a stop position like <c>calc(100% - 2rem)</c> into its fraction and its length.
    /// </summary>
    private static bool TryParsePercentagePlusLength(string value, float emBase, out float fraction, out float pixels)
    {
        fraction = 0f;
        pixels = 0f;
        var match = PercentagePlusLengthPattern.Match(value.Trim());

        if (!match.Success)
        {
            return false;
        }

        var sign = match.Groups["op"].Value == "-" ? -1f : 1f;
        var first = match.Groups["a"].Value;
        var second = match.Groups["b"].Value;

        if (first.EndsWith('%') && TryParseStopLength(second, emBase, out var length))
        {
            fraction = float.Parse(first[..^1], CultureInfo.InvariantCulture) / 100f;
            pixels = sign * length;
            return true;
        }

        if (second.EndsWith('%') && TryParseStopLength(first, emBase, out length))
        {
            fraction = sign * float.Parse(second[..^1], CultureInfo.InvariantCulture) / 100f;
            pixels = length;
            return true;
        }

        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex PercentagePlusLengthPattern = new(
        @"^calc\(\s*(?<a>-?[0-9]*\.?[0-9]+(%|px|r?em))\s*(?<op>[+-])\s*(?<b>[0-9]*\.?[0-9]+(%|px|r?em))\s*\)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// A stop position given as a length: the cascaded value is not computed yet, so <c>em</c>
    /// refers to the element's font size and <c>rem</c> to the initial one (16px).
    /// </summary>
    private static bool TryParseStopLength(string value, float emBase, out float pixels)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        var (number, factor) = trimmed.EndsWith("rem", StringComparison.Ordinal) ? (trimmed[..^3], 16f)
            : trimmed.EndsWith("em", StringComparison.Ordinal) ? (trimmed[..^2], emBase)
            : trimmed.EndsWith("px", StringComparison.Ordinal) ? (trimmed[..^2], 1f)
            : (string.Empty, 0f);

        if (factor > 0f && float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            pixels = amount * factor;
            return true;
        }

        pixels = 0f;
        return false;
    }

    private static float ParseStopPosition(string rawPosition, bool isConic = false)
    {
        var value = rawPosition.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0f;
        }

        // conic-gradient stops are naturally written as angles ("90deg", "0.25turn"), which are a
        // fraction of the full circle rather than of a linear 0-100% run - resolve those first.
        if (isConic && TryParseAngle(value, out var angleDegrees))
        {
            var normalizedDegrees = ((angleDegrees % 360f) + 360f) % 360f;
            return Math.Clamp(normalizedDegrees / 360f, 0f, 1f);
        }

        if (value.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return Math.Clamp(percent / 100f, 0f, 1f);
        }

        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            return Math.Clamp(numeric, 0f, 1f);
        }

        return 0f;
    }

    private static bool TryParseAngle(string value, out float angleDegrees)
    {
        angleDegrees = 90f;
        var trimmed = value.Trim();

        if (trimmed.EndsWith("deg", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
        {
            angleDegrees = degrees;
            return true;
        }

        if (trimmed.EndsWith("grad", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var gradians))
        {
            angleDegrees = gradians * 0.9f;
            return true;
        }

        if (trimmed.EndsWith("turn", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var turns))
        {
            angleDegrees = turns * 360f;
            return true;
        }

        // Checked after "grad" - "grad" also ends with "rad" and would otherwise be misread here.
        if (trimmed.EndsWith("rad", StringComparison.Ordinal) &&
            float.TryParse(trimmed[..^3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var radians))
        {
            angleDegrees = radians * 180f / (float)Math.PI;
            return true;
        }

        return false;
    }

    private static float ParseAngle(string value)
    {
        return TryParseAngle(value, out var angle) ? angle : 0f;
    }

    internal static RenderColor ParseColor(string? rawColor, RenderColor fallback)
    {
        if (string.IsNullOrWhiteSpace(rawColor))
        {
            return fallback;
        }

        var color = rawColor.Trim().ToLowerInvariant();

        if (color.StartsWith("#", StringComparison.Ordinal))
        {
            return ParseHexColor(color, fallback);
        }

        if ((color.StartsWith("rgb(", StringComparison.Ordinal) || color.StartsWith("rgba(", StringComparison.Ordinal)) && color.EndsWith(')'))
        {
            var start = color.IndexOf('(');
            var content = color[(start + 1)..^1].Trim();

            if (content.Contains(',', StringComparison.Ordinal))
            {
                var commaParts = content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (commaParts.Length >= 3 &&
                    TryParseColorChannel(commaParts[0], out var r) &&
                    TryParseColorChannel(commaParts[1], out var g) &&
                    TryParseColorChannel(commaParts[2], out var b))
                {
                    var alpha = byte.MaxValue;

                    if (commaParts.Length >= 4 && TryParseAlphaChannel(commaParts[3], out var a))
                    {
                        alpha = a;
                    }

                    return new RenderColor(r, g, b, alpha);
                }
            }
            else
            {
                var slashParts = content.Split('/', StringSplitOptions.TrimEntries);
                var rgbParts = slashParts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (rgbParts.Length >= 3 &&
                    TryParseColorChannel(rgbParts[0], out var r) &&
                    TryParseColorChannel(rgbParts[1], out var g) &&
                    TryParseColorChannel(rgbParts[2], out var b))
                {
                    var alpha = byte.MaxValue;

                    if (slashParts.Length > 1 && TryParseAlphaChannel(slashParts[1], out var a))
                    {
                        alpha = a;
                    }

                    return new RenderColor(r, g, b, alpha);
                }
            }
        }

        if (string.Equals(color, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return RenderColor.Transparent;
        }

        // Regular box/text colors are normalized by AngleSharp.Css's own computed-style engine
        // before they ever reach this method (it resolves "red" to an rgb()/hex form itself), so
        // this named-color table only matters for values this renderer parses from raw CSS text
        // itself - chiefly gradient stop colors, since a `background-image: linear-gradient(...)`
        // function's internals are opaque to AngleSharp.Css and are hand-parsed here instead.
        // Without it, every named gradient stop color silently fell back to the same color,
        // producing an invisible (fallback-to-fallback) "gradient".
        if (NamedColors.TryGetValue(color, out var named))
        {
            return named;
        }

        return fallback;
    }

    private static readonly IReadOnlyDictionary<string, RenderColor> NamedColors = new Dictionary<string, RenderColor>(StringComparer.OrdinalIgnoreCase)
    {
        ["aliceblue"] = new RenderColor(240, 248, 255),
        ["antiquewhite"] = new RenderColor(250, 235, 215),
        ["aqua"] = new RenderColor(0, 255, 255),
        ["aquamarine"] = new RenderColor(127, 255, 212),
        ["azure"] = new RenderColor(240, 255, 255),
        ["beige"] = new RenderColor(245, 245, 220),
        ["bisque"] = new RenderColor(255, 228, 196),
        ["black"] = new RenderColor(0, 0, 0),
        ["blanchedalmond"] = new RenderColor(255, 235, 205),
        ["blue"] = new RenderColor(0, 0, 255),
        ["blueviolet"] = new RenderColor(138, 43, 226),
        ["brown"] = new RenderColor(165, 42, 42),
        ["burlywood"] = new RenderColor(222, 184, 135),
        ["cadetblue"] = new RenderColor(95, 158, 160),
        ["chartreuse"] = new RenderColor(127, 255, 0),
        ["chocolate"] = new RenderColor(210, 105, 30),
        ["coral"] = new RenderColor(255, 127, 80),
        ["cornflowerblue"] = new RenderColor(100, 149, 237),
        ["cornsilk"] = new RenderColor(255, 248, 220),
        ["crimson"] = new RenderColor(220, 20, 60),
        ["cyan"] = new RenderColor(0, 255, 255),
        ["darkblue"] = new RenderColor(0, 0, 139),
        ["darkcyan"] = new RenderColor(0, 139, 139),
        ["darkgoldenrod"] = new RenderColor(184, 134, 11),
        ["darkgray"] = new RenderColor(169, 169, 169),
        ["darkgreen"] = new RenderColor(0, 100, 0),
        ["darkgrey"] = new RenderColor(169, 169, 169),
        ["darkkhaki"] = new RenderColor(189, 183, 107),
        ["darkmagenta"] = new RenderColor(139, 0, 139),
        ["darkolivegreen"] = new RenderColor(85, 107, 47),
        ["darkorange"] = new RenderColor(255, 140, 0),
        ["darkorchid"] = new RenderColor(153, 50, 204),
        ["darkred"] = new RenderColor(139, 0, 0),
        ["darksalmon"] = new RenderColor(233, 150, 122),
        ["darkseagreen"] = new RenderColor(143, 188, 143),
        ["darkslateblue"] = new RenderColor(72, 61, 139),
        ["darkslategray"] = new RenderColor(47, 79, 79),
        ["darkslategrey"] = new RenderColor(47, 79, 79),
        ["darkturquoise"] = new RenderColor(0, 206, 209),
        ["darkviolet"] = new RenderColor(148, 0, 211),
        ["deeppink"] = new RenderColor(255, 20, 147),
        ["deepskyblue"] = new RenderColor(0, 191, 255),
        ["dimgray"] = new RenderColor(105, 105, 105),
        ["dimgrey"] = new RenderColor(105, 105, 105),
        ["dodgerblue"] = new RenderColor(30, 144, 255),
        ["firebrick"] = new RenderColor(178, 34, 34),
        ["floralwhite"] = new RenderColor(255, 250, 240),
        ["forestgreen"] = new RenderColor(34, 139, 34),
        ["fuchsia"] = new RenderColor(255, 0, 255),
        ["gainsboro"] = new RenderColor(220, 220, 220),
        ["ghostwhite"] = new RenderColor(248, 248, 255),
        ["gold"] = new RenderColor(255, 215, 0),
        ["goldenrod"] = new RenderColor(218, 165, 32),
        ["gray"] = new RenderColor(128, 128, 128),
        ["green"] = new RenderColor(0, 128, 0),
        ["greenyellow"] = new RenderColor(173, 255, 47),
        ["grey"] = new RenderColor(128, 128, 128),
        ["honeydew"] = new RenderColor(240, 255, 240),
        ["hotpink"] = new RenderColor(255, 105, 180),
        ["indianred"] = new RenderColor(205, 92, 92),
        ["indigo"] = new RenderColor(75, 0, 130),
        ["ivory"] = new RenderColor(255, 255, 240),
        ["khaki"] = new RenderColor(240, 230, 140),
        ["lavender"] = new RenderColor(230, 230, 250),
        ["lavenderblush"] = new RenderColor(255, 240, 245),
        ["lawngreen"] = new RenderColor(124, 252, 0),
        ["lemonchiffon"] = new RenderColor(255, 250, 205),
        ["lightblue"] = new RenderColor(173, 216, 230),
        ["lightcoral"] = new RenderColor(240, 128, 128),
        ["lightcyan"] = new RenderColor(224, 255, 255),
        ["lightgoldenrodyellow"] = new RenderColor(250, 250, 210),
        ["lightgray"] = new RenderColor(211, 211, 211),
        ["lightgreen"] = new RenderColor(144, 238, 144),
        ["lightgrey"] = new RenderColor(211, 211, 211),
        ["lightpink"] = new RenderColor(255, 182, 193),
        ["lightsalmon"] = new RenderColor(255, 160, 122),
        ["lightseagreen"] = new RenderColor(32, 178, 170),
        ["lightskyblue"] = new RenderColor(135, 206, 250),
        ["lightslategray"] = new RenderColor(119, 136, 153),
        ["lightslategrey"] = new RenderColor(119, 136, 153),
        ["lightsteelblue"] = new RenderColor(176, 196, 222),
        ["lightyellow"] = new RenderColor(255, 255, 224),
        ["lime"] = new RenderColor(0, 255, 0),
        ["limegreen"] = new RenderColor(50, 205, 50),
        ["linen"] = new RenderColor(250, 240, 230),
        ["magenta"] = new RenderColor(255, 0, 255),
        ["maroon"] = new RenderColor(128, 0, 0),
        ["mediumaquamarine"] = new RenderColor(102, 205, 170),
        ["mediumblue"] = new RenderColor(0, 0, 205),
        ["mediumorchid"] = new RenderColor(186, 85, 211),
        ["mediumpurple"] = new RenderColor(147, 112, 219),
        ["mediumseagreen"] = new RenderColor(60, 179, 113),
        ["mediumslateblue"] = new RenderColor(123, 104, 238),
        ["mediumspringgreen"] = new RenderColor(0, 250, 154),
        ["mediumturquoise"] = new RenderColor(72, 209, 204),
        ["mediumvioletred"] = new RenderColor(199, 21, 133),
        ["midnightblue"] = new RenderColor(25, 25, 112),
        ["mintcream"] = new RenderColor(245, 255, 250),
        ["mistyrose"] = new RenderColor(255, 228, 225),
        ["moccasin"] = new RenderColor(255, 228, 181),
        ["navajowhite"] = new RenderColor(255, 222, 173),
        ["navy"] = new RenderColor(0, 0, 128),
        ["oldlace"] = new RenderColor(253, 245, 230),
        ["olive"] = new RenderColor(128, 128, 0),
        ["olivedrab"] = new RenderColor(107, 142, 35),
        ["orange"] = new RenderColor(255, 165, 0),
        ["orangered"] = new RenderColor(255, 69, 0),
        ["orchid"] = new RenderColor(218, 112, 214),
        ["palegoldenrod"] = new RenderColor(238, 232, 170),
        ["palegreen"] = new RenderColor(152, 251, 152),
        ["paleturquoise"] = new RenderColor(175, 238, 238),
        ["palevioletred"] = new RenderColor(219, 112, 147),
        ["papayawhip"] = new RenderColor(255, 239, 213),
        ["peachpuff"] = new RenderColor(255, 218, 185),
        ["peru"] = new RenderColor(205, 133, 63),
        ["pink"] = new RenderColor(255, 192, 203),
        ["plum"] = new RenderColor(221, 160, 221),
        ["powderblue"] = new RenderColor(176, 224, 230),
        ["purple"] = new RenderColor(128, 0, 128),
        ["rebeccapurple"] = new RenderColor(102, 51, 153),
        ["red"] = new RenderColor(255, 0, 0),
        ["rosybrown"] = new RenderColor(188, 143, 143),
        ["royalblue"] = new RenderColor(65, 105, 225),
        ["saddlebrown"] = new RenderColor(139, 69, 19),
        ["salmon"] = new RenderColor(250, 128, 114),
        ["sandybrown"] = new RenderColor(244, 164, 96),
        ["seagreen"] = new RenderColor(46, 139, 87),
        ["seashell"] = new RenderColor(255, 245, 238),
        ["sienna"] = new RenderColor(160, 82, 45),
        ["silver"] = new RenderColor(192, 192, 192),
        ["skyblue"] = new RenderColor(135, 206, 235),
        ["slateblue"] = new RenderColor(106, 90, 205),
        ["slategray"] = new RenderColor(112, 128, 144),
        ["slategrey"] = new RenderColor(112, 128, 144),
        ["snow"] = new RenderColor(255, 250, 250),
        ["springgreen"] = new RenderColor(0, 255, 127),
        ["steelblue"] = new RenderColor(70, 130, 180),
        ["tan"] = new RenderColor(210, 180, 140),
        ["teal"] = new RenderColor(0, 128, 128),
        ["thistle"] = new RenderColor(216, 191, 216),
        ["tomato"] = new RenderColor(255, 99, 71),
        ["turquoise"] = new RenderColor(64, 224, 208),
        ["violet"] = new RenderColor(238, 130, 238),
        ["wheat"] = new RenderColor(245, 222, 179),
        ["white"] = new RenderColor(255, 255, 255),
        ["whitesmoke"] = new RenderColor(245, 245, 245),
        ["yellow"] = new RenderColor(255, 255, 0),
        ["yellowgreen"] = new RenderColor(154, 205, 50),
    };

    private static bool TryParseColorChannel(string raw, out byte value)
    {
        var token = raw.Trim();

        if (token.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            percent = Math.Clamp(percent, 0f, 100f);
            value = (byte)Math.Round((percent / 100f) * 255f);
            return true;
        }

        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var channel))
        {
            channel = Math.Clamp(channel, 0f, 255f);
            value = (byte)Math.Round(channel);
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryParseAlphaChannel(string raw, out byte alpha)
    {
        var token = raw.Trim();

        if (token.EndsWith("%", StringComparison.Ordinal) &&
            float.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            percent = Math.Clamp(percent, 0f, 100f);
            alpha = (byte)Math.Round((percent / 100f) * 255f);
            return true;
        }

        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
        {
            if (a <= 1f)
            {
                a *= 255f;
            }

            a = Math.Clamp(a, 0f, 255f);
            alpha = (byte)Math.Round(a);
            return true;
        }

        alpha = byte.MaxValue;
        return false;
    }

    private static RenderColor ParseHexColor(string color, RenderColor fallback)
    {
        if (color.Length == 4)
        {
            var rs = string.Concat(color[1], color[1]);
            var gs = string.Concat(color[2], color[2]);
            var bs = string.Concat(color[3], color[3]);

            if (byte.TryParse(rs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r3) &&
                byte.TryParse(gs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g3) &&
                byte.TryParse(bs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b3))
            {
                return new RenderColor(r3, g3, b3);
            }
        }

        if (color.Length == 7 &&
            byte.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return new RenderColor(r, g, b);
        }

        return fallback;
    }

    /// <summary>
    /// Greedy word-wrapping, extended with `word-break: break-all`/`overflow-wrap: break-word`
    /// support - both read straight off <paramref name="textStyle"/> rather than as separate
    /// parameters, since every caller already carries a fully-resolved <see cref="RenderTextStyle"/>.
    /// `break-all` (<see cref="WrapTextCharacterWise"/>) breaks at any character boundary
    /// everywhere, matching spec precedence over `overflow-wrap` (a word-break-all element ignores
    /// `overflow-wrap` entirely, since breaking is already unrestricted). Otherwise, the ordinary
    /// word-based algorithm below only reaches for character-level breaking (<see cref="SplitOverlongWord"/>)
    /// as the spec's own "last resort": a single word wider than the *entire* line (not just what is
    /// left of the current line) that `overflow-wrap: break-word`/`anywhere` explicitly permits
    /// breaking - a word that merely doesn't fit what's left of the current line still simply wraps
    /// to a new line whole, exactly like `overflow-wrap: normal`.
    /// </summary>
    private static IReadOnlyList<string> WrapText(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        if (textStyle.WordBreak == WordBreakMode.BreakAll)
        {
            return WrapTextCharacterWise(context, StripSoftHyphens(text), maxWidth, textStyle);
        }

        if (textStyle.Hyphens == HyphensMode.None)
        {
            text = StripSoftHyphens(text);
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0f;

        void FlushCurrentLine()
        {
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0f;
            }
        }

        void AppendToken(string token, float tokenWidth)
        {
            var separatorWidth = current.Length == 0 ? 0f : MeasureTextWidth(context, " ", textStyle) + textStyle.WordSpacing;

            if (current.Length > 0 && currentWidth + separatorWidth + tokenWidth > maxWidth)
            {
                FlushCurrentLine();
                separatorWidth = 0f;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
                currentWidth += separatorWidth;
            }

            current.Append(token);
            currentWidth += tokenWidth;
        }

        foreach (var rawWord in words)
        {
            var word = StripSoftHyphens(rawWord);
            var wordWidth = MeasureTextWidth(context, word, textStyle);

            // A word with soft hyphens that does not fit the rest of the line breaks at the last
            // one that leaves room for the visible hyphen.
            if (!ReferenceEquals(word, rawWord))
            {
                var separatorWidth = current.Length == 0 ? 0f : MeasureTextWidth(context, " ", textStyle) + textStyle.WordSpacing;

                if (currentWidth + separatorWidth + wordWidth > maxWidth)
                {
                    var remaining = rawWord;

                    while (TrySplitAtSoftHyphen(context, remaining, maxWidth - currentWidth - separatorWidth, textStyle, current.Length == 0, out var head, out var tail))
                    {
                        AppendToken(head, MeasureTextWidth(context, head, textStyle));
                        FlushCurrentLine();
                        separatorWidth = 0f;
                        remaining = tail;

                        if (MeasureTextWidth(context, remaining, textStyle) <= maxWidth)
                        {
                            break;
                        }
                    }

                    word = StripSoftHyphens(remaining);
                    wordWidth = MeasureTextWidth(context, word, textStyle);
                }
            }

            if (wordWidth > maxWidth && textStyle.OverflowWrap == OverflowWrapMode.Normal && (word.Contains('-') || word.Contains('/')))
            {
                var chunks = SplitBreakableWord(word);

                foreach (var chunk in chunks)
                {
                    AppendToken(chunk, MeasureTextWidth(context, chunk, textStyle));
                }

                continue;
            }

            if (wordWidth > maxWidth && textStyle.OverflowWrap == OverflowWrapMode.BreakWord)
            {
                var chunks = SplitOverlongWord(context, word, maxWidth, textStyle);

                for (var i = 0; i < chunks.Count; i++)
                {
                    if (i == 0)
                    {
                        // The first chunk of a broken word is still an ordinary word boundary -
                        // it gets ordinary inter-word wrapping/spacing against the current line.
                        AppendToken(chunks[i], MeasureTextWidth(context, chunks[i], textStyle));
                    }
                    else
                    {
                        // A mid-word break always continues on a fresh line - there is no space to
                        // share the previous chunk's remaining room with.
                        FlushCurrentLine();
                        current.Append(chunks[i]);
                        currentWidth = MeasureTextWidth(context, chunks[i], textStyle);
                    }
                }

                continue;
            }

            AppendToken(word, wordWidth);
        }

        FlushCurrentLine();

        return lines;
    }

    /// <summary>
    /// Splits a word at the last soft hyphen whose head (with a visible hyphen) fits in
    /// <paramref name="available"/>. On an otherwise empty line the first soft hyphen is taken
    /// even when its head overflows, since no later break can do better.
    /// </summary>
    private static bool TrySplitAtSoftHyphen(LayoutContext context, string word, float available, RenderTextStyle textStyle, bool lineIsEmpty, out string head, out string tail)
    {
        head = string.Empty;
        tail = word;
        var found = false;

        for (var index = word.IndexOf(SoftHyphen); index > 0 && index < word.Length - 1; index = word.IndexOf(SoftHyphen, index + 1))
        {
            var candidate = StripSoftHyphens(word[..index]) + HyphenText;

            if (MeasureTextWidth(context, candidate, textStyle) > available && (found || !lineIsEmpty))
            {
                break;
            }

            head = candidate;
            tail = word[(index + 1)..];
            found = true;

            if (MeasureTextWidth(context, candidate, textStyle) > available)
            {
                break;
            }
        }

        return found;
    }

    private static IReadOnlyList<string> SplitBreakableWord(string word)
    {
        var chunks = new List<string>();
        var start = 0;

        for (var index = 0; index < word.Length; index++)
        {
            if (word[index] is '-' or '/')
            {
                chunks.Add(word[start..(index + 1)]);
                start = index + 1;
            }
        }

        if (start < word.Length)
        {
            chunks.Add(word[start..]);
        }

        return chunks;
    }

    /// <summary>
    /// Splits one overlong word into the largest chunks that each fit within <paramref name="maxWidth"/>,
    /// for <c>overflow-wrap: break-word</c>/<c>anywhere</c>'s "last resort" mid-word break. Always
    /// makes progress (appends at least one character per chunk) even if a single character alone
    /// exceeds <paramref name="maxWidth"/>, so an extreme case (a huge font size in a tiny box) still
    /// terminates rather than looping.
    /// </summary>
    private static List<string> SplitOverlongWord(LayoutContext context, string word, float maxWidth, RenderTextStyle textStyle)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0f;

        foreach (var rune in word.EnumerateRunes())
        {
            var chStr = rune.ToString();
            var chWidth = MeasureTextWidth(context, chStr, textStyle);

            if (current.Length > 0 && currentWidth + chWidth > maxWidth)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentWidth = 0f;
            }

            current.Append(chStr);
            currentWidth += chWidth;
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>
    /// `word-break: break-all` wrapping: every character (not just every word) is a potential break
    /// point, so this greedily fills each line character-by-character instead of word-by-word - a
    /// space is simply a character like any other here (no separator width added around it), which
    /// reproduces ordinary space-based wrapping for free wherever a line happens to break at one,
    /// while still allowing a break mid-word wherever it does not.
    /// </summary>
    private static IReadOnlyList<string> WrapTextCharacterWise(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var currentWidth = 0f;

        foreach (var rune in text.EnumerateRunes())
        {
            var chStr = rune.ToString();
            var chWidth = MeasureTextWidth(context, chStr, textStyle);

            if (current.Length > 0 && currentWidth + chWidth > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0f;
            }

            current.Append(chStr);
            currentWidth += chWidth;
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private const string EllipsisCharacter = "…";

    /// <summary>
    /// Truncates <paramref name="text"/> to the longest prefix (by rune, not raw UTF-16 char, so a
    /// truncation point never lands inside a surrogate pair) whose width plus the ellipsis
    /// character's own width still fits within <paramref name="maxWidth"/>, then appends it - the
    /// approach every real browser's own `text-overflow: ellipsis` uses (truncate, do not scale or
    /// reflow). Binary search over rune count, not a linear scan, since <see cref="MeasureTextWidth"/>
    /// goes through the backend's own font shaping and this runs on already-overflowing text on the
    /// hot layout path.
    /// </summary>
    private static string TruncateWithEllipsis(LayoutContext context, string text, float maxWidth, RenderTextStyle textStyle)
    {
        var ellipsisWidth = MeasureTextWidth(context, EllipsisCharacter, textStyle);

        if (ellipsisWidth > maxWidth || text.Length == 0)
        {
            return EllipsisCharacter;
        }

        var runes = text.EnumerateRunes().ToArray();
        var low = 0;
        var high = runes.Length;

        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var prefixWidth = MeasureTextWidth(context, string.Concat(runes.Take(mid).Select(r => r.ToString())), textStyle);

            if (prefixWidth + ellipsisWidth <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return string.Concat(runes.Take(low).Select(r => r.ToString())) + EllipsisCharacter;
    }

    private static RenderFont ToRenderFont(RenderTextStyle textStyle, FontFaceSet fonts) => new(
        textStyle.FontFamily,
        textStyle.FontSize,
        textStyle.FontWeight,
        textStyle.IsItalic,
        textStyle.LetterSpacing,
        fonts,
        textStyle.FontStretch);

    /// <summary>
    /// Measures text the way <see cref="DrawStyledText"/> paints it: soft hyphens are invisible
    /// and small capitals are measured run by run.
    /// </summary>
    private static float MeasureTextWidth(LayoutContext context, string text, RenderTextStyle textStyle)
    {
        text = StripSoftHyphens(text);

        if (TryMeasureOrientedText(context, text, textStyle, out var orientedWidth))
        {
            return orientedWidth;
        }

        if (textStyle.VariantCaps == FontVariantCapsMode.Normal)
        {
            return context.TextMeasurer.MeasureWidth(text, ToRenderFont(textStyle, context.Fonts));
        }

        var width = 0f;

        foreach (var (run, reduced) in SplitSmallCaps(text, textStyle.VariantCaps))
        {
            width += MeasureTextWidth(context, run, reduced ? WithSmallCapsSize(textStyle) : textStyle with { VariantCaps = FontVariantCapsMode.Normal });
        }

        return width;
    }

    private static TextAlign ResolveStartIfJustify(TextAlign align, TextDirection direction) =>
        align != TextAlign.Justify ? align : direction == TextDirection.Rtl ? TextAlign.Right : TextAlign.Left;

    private static float ResolveTextAlignmentOffset(TextAlign align, float availableWidth, float textWidth)
    {
        if (availableWidth <= textWidth)
        {
            return 0f;
        }

        return align switch
        {
            TextAlign.Center => (availableWidth - textWidth) / 2f,
            TextAlign.Right => availableWidth - textWidth,
            _ => 0f,
        };
    }

    /// <summary>
    /// Collapses whitespace the way <c>white-space: normal</c> always did before this property was
    /// read at all - every call site that is not itself part of text layout (table cell text, form
    /// control labels, and the two "does this text node count as non-empty content" checks used for
    /// child-ordering purposes) still goes through this, a deliberate scope cut: none of those sites
    /// feed a wrapping-aware layout path today, so a real `white-space` value on them would have
    /// nothing to change even if read.
    /// </summary>
    private static string NormalizeWhitespace(string value) => NormalizeWhitespace(value, WhiteSpaceMode.Normal);

    /// <summary>
    /// The white space CSS collapses (CSS Text 3 §4): a no-break space (or any other Unicode
    /// space) is not part of it and keeps its width, unlike for <see cref="char.IsWhiteSpace(char)"/>.
    /// </summary>
    private static bool IsCollapsibleWhiteSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static readonly char[] CollapsibleWhiteSpace = [' ', '\t', '\n', '\r', '\f'];

    private static bool IsCollapsibleWhiteSpaceOnly(string text)
    {
        foreach (var c in text)
        {
            if (!IsCollapsibleWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Collapses or preserves whitespace per the resolved <c>white-space</c> value, matching the CSS
    /// spec's own collapsing/newline-preservation table: <c>normal</c>/<c>nowrap</c> collapse both
    /// runs of horizontal whitespace and newlines into a single space and trim the ends; <c>pre</c>/
    /// <c>pre-wrap</c>/<c>break-spaces</c> preserve every character verbatim (only normalizing
    /// <c>\r\n</c>/<c>\r</c> line endings to a plain <c>\n</c>, the same source-text preprocessing
    /// step HTML always applies regardless of `white-space`); <c>pre-line</c> collapses runs of
    /// horizontal whitespace to a single space but preserves each `\n` as its own forced break.
    /// </summary>
    private static string NormalizeWhitespace(string value, WhiteSpaceMode whiteSpace)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var preserveNewlines = whiteSpace is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap or WhiteSpaceMode.PreLine or WhiteSpaceMode.BreakSpaces;
        var collapseHorizontalWhitespace = whiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.Nowrap or WhiteSpaceMode.PreLine;

        // Collapse \r\n/\r to a plain \n unconditionally, before any mode-specific handling - this
        // is source-text preprocessing HTML always applies regardless of `white-space`, not part of
        // the whitespace-collapsing semantics below. Doing it first (rather than per-character
        // inside the loop) also avoids double-counting a \r\n pair as two separate forced breaks
        // under pre-line's own char-by-char newline handling.
        value = value.Replace("\r\n", "\n").Replace('\r', '\n');

        if (!collapseHorizontalWhitespace)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var inWhitespace = false;

        foreach (var c in value)
        {
            if (preserveNewlines && c == '\n')
            {
                sb.Append('\n');
                inWhitespace = false;
                continue;
            }

            if (IsCollapsibleWhiteSpace(c))
            {
                if (!inWhitespace)
                {
                    sb.Append(' ');
                    inWhitespace = true;
                }

                continue;
            }

            sb.Append(c);
            inWhitespace = false;
        }

        return preserveNewlines ? sb.ToString() : sb.ToString().Trim(CollapsibleWhiteSpace);
    }

    private readonly record struct RenderTextStyle(
        float FontSize,
        RenderColor Color,
        string FontFamily,
        float LineHeightMultiplier,
        float FontWeight,
        bool IsItalic,
        bool Underline,
        bool StrikeThrough,
        RenderColor DecorationColor,
        global::AngleSharp.Renderer.Rendering.RenderTextDecorationStyle DecorationStyle,
        TextAlign TextAlign,
        float LetterSpacing,
        float TextIndent,
        float VerticalAlignOffset,
        IReadOnlyList<global::AngleSharp.Renderer.Rendering.RenderTextShadow> TextShadows,
        WhiteSpaceMode WhiteSpace,
        WordBreakMode WordBreak,
        OverflowWrapMode OverflowWrap,
        TextOverflowMode TextOverflow,
        int LineClamp,
        TextDirection Direction,
        TextTransformMode TextTransform = TextTransformMode.None,
        float WordSpacing = 0f,
        TextAlign? TextAlignLast = null,
        HyphensMode Hyphens = HyphensMode.Manual,
        float TabSize = 8f,
        bool TabSizeIsLength = false,
        FontVariantCapsMode VariantCaps = FontVariantCapsMode.Normal,
        float FontStretch = 100f,
        float MarkerIndent = 0f,
        bool IsHidden = false,
        WritingModeKind WritingMode = WritingModeKind.HorizontalTb,
        TextOrientationKind TextOrientation = TextOrientationKind.Mixed,
        WritingModeKind FlowMode = WritingModeKind.HorizontalTb);

    private enum TextAlign
    {
        Left,
        Center,
        Right,
        Justify,
    }

    /// <summary>
    /// CSS `direction` (`ltr`/`rtl`), see `ParseTextDirection`.
    /// </summary>
    private enum TextDirection
    {
        Ltr,
        Rtl,
    }

    /// <summary>
    /// The CSS <c>white-space</c> keywords this renderer distinguishes. See
    /// <see cref="NormalizeWhitespace(string, WhiteSpaceMode)"/> for collapsing/newline-preservation
    /// and <see cref="LayoutWrappedText"/>/<see cref="LayoutInlineFormattingContext"/> for wrap suppression.
    /// </summary>
    private enum WhiteSpaceMode
    {
        Normal,
        Nowrap,
        Pre,
        PreWrap,
        PreLine,
        BreakSpaces,
    }

    /// <summary>
    /// <c>word-break</c>. <c>KeepAll</c> (meant for CJK text, suppressing breaks between ideographic
    /// characters that <c>Normal</c> would otherwise allow) is folded into <c>Normal</c> - this
    /// renderer has no CJK-aware line-breaking of any kind to differentiate the two, a deliberate,
    /// documented scope cut rather than an oversight.
    /// </summary>
    private enum WordBreakMode
    {
        Normal,
        BreakAll,
    }

    /// <summary>
    /// <c>overflow-wrap</c> (and its legacy <c>word-wrap</c> alias). <c>Anywhere</c> is folded into
    /// <c>BreakWord</c> - the two keywords only differ in how they affect *intrinsic* (min-content)
    /// sizing, a concept this renderer's already-approximate, non-intrinsic text layout does not
    /// model, so both simply mean "break an otherwise-unbreakable word as a last resort" here.
    /// </summary>
    private enum OverflowWrapMode
    {
        Normal,
        BreakWord,
    }

    /// <summary>
    /// <c>text-overflow</c>. Unlike <see cref="WhiteSpaceMode"/>/<see cref="WordBreakMode"/>/
    /// <see cref="OverflowWrapMode"/>, this is deliberately never inherited (it targets this
    /// element's own line box, not a descendant's) - see <see cref="ParseTextOverflow"/>.
    /// </summary>
    private enum TextOverflowMode
    {
        Clip,
        Ellipsis,
    }

    private readonly record struct EdgeSizes(float Top, float Right, float Bottom, float Left);

    private enum BorderStyleKind
    {
        Solid,
        None,
        Hidden,
        Dashed,
        Dotted,
        Double,
        Groove,
        Ridge,
        Inset,
        Outset,
    }

    private readonly record struct EdgeBorderStyle(BorderStyleKind Top, BorderStyleKind Right, BorderStyleKind Bottom, BorderStyleKind Left);

    private readonly record struct EdgeBorderColor(RenderColor Top, RenderColor Right, RenderColor Bottom, RenderColor Left);

    private readonly record struct BoxStyle(
        EdgeSizes Margin,
        EdgeSizes Padding,
        EdgeSizes BorderWidth,
        EdgeBorderStyle BorderStyle,
        RenderPaint BackgroundPaint,
        EdgeBorderColor BorderColor,
        RenderCornerRadii BorderRadius,
        IReadOnlyList<RenderBoxShadow> BoxShadows,
        RenderColor BackgroundColor = default,
        IReadOnlyList<BackgroundLayer>? BackgroundLayers = null);
}
