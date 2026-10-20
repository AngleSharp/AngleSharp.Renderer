# AGENTS.md

Guidance for AI coding agents working in this repository: commands, architecture, conventions, and the non-obvious decisions behind the code. Keep it short - record *why* and *gotchas*, not what the code already says.

## What This Repository Is

AngleSharp.Renderer adds rendering on top of AngleSharp and AngleSharp.Css: a display-list renderer with a SkiaSharp backend that rasterizes HTML/CSS (and SVG) into PNG images.

## Commands

```bash
dotnet test src/AngleSharp.Renderer.sln                                      # full validation
dotnet test src/AngleSharp.Renderer.Tests/AngleSharp.Renderer.Tests.csproj  # iterate on renderer behavior
./build.sh | ./build.ps1 | ./build.cmd                                       # Fallout bootstrapper
```

The renderer and test projects target `net8.0` and `net10.0`; both runtimes are exercised.

## Architecture

- `HtmlRenderer` builds a `DisplayList` from the AngleSharp.Css render tree and computed styles. All layout lives in this partial class: `HtmlRenderer.cs` (block/flex/grid/table layout, style maps, painting), `HtmlRenderer.Inline.cs` (flow children, inline formatting context, bidi reordering, intrinsic sizing), `HtmlRenderer.Text.cs` (`hyphens`, `tab-size`, small caps, `font-stretch`, `DrawStyledText`), `HtmlRenderer.Floats.cs` (floats, `clear`, block formatting contexts), `HtmlRenderer.Columns.cs` (multi-column layout).
- `DisplayList` is the backend-agnostic, flat command model. Scoped effects are push/pop pairs (`PushClip`, `PushTransform`, `PushOpacity`, `PushFilter`, ...) that the backend maps to `Save`/`SaveLayer`/`Restore`. `InsertRange` and `MoveRangeToEnd` exist to reorder already-emitted commands.
- `SkiaRenderBackend` rasterizes the display list and implements `ITextMeasurer`, so layout measures with the very typefaces it paints with.
- `ITextMeasurer` is the seam between layout and rasterization (line breaking, alignment, table widths). Never reintroduce a font-independent width heuristic - it silently decouples layout from what is drawn.
- `SkiaTextShaping` is the single font-resolution path for both measuring and painting (and SVG text). Text is drawn glyph by glyph with `canvas.DrawText` - no HarfBuzz shaping (no Arabic joining, ligatures or `font-feature-settings`).
- `BidiAlgorithm` (UAX #9: levels, `unicode-bidi` embeddings/overrides/isolates, mirroring) and `WebFontDecoder` (WOFF via zlib, WOFF2 via Brotli plus the `glyf`/`loca`/`hmtx` transforms, back to plain sfnt) are self-contained internal helpers.
- `HtmlRenderOptions` holds viewport and text defaults.
- `SvgRasterizer` (`Skia/Svg/`) paints SVG by walking the DOM AngleSharp already produced - no second SVG parser. Its PNG output becomes an ordinary `RenderedImage`.
- `InteractiveHtmlRendererState` / `IDomHarness` back interactive documents (`:hover`, scrolling, focus, a virtual clock for transitions/animations/caret).

### Fonts

`SkiaTextShaping.CreateTypeface` walks the `font-family` list in order; the first usable entry wins:

1. Generic families (`serif`, `sans-serif`, `monospace`, `cursive`, `fantasy`, `system-ui`, `ui-*`) always map to the bundled fonts in `Resources/Fonts`. This is what keeps snapshots reproducible; `@font-face` cannot take them over.
2. `@font-face` rules (`FontFaceLoader`, carried on `DisplayList.Fonts`). `local()` resolves against installed fonts; `url()` against `data:` URIs, or the network only when the browsing context has an `IDocumentLoader`. WOFF/WOFF2 are decoded to sfnt by `WebFontDecoder` first; SVG and EOT fonts are rejected.
3. Installed families - host-dependent, never assert them in snapshots.
4. Bundled sans-serif as the last resort.

Never resolve named families with `SKTypeface.FromFamilyName`: it substitutes the host default for unknown names instead of returning null, swallowing the rest of the fallback list. Availability goes through a case-insensitive index of installed families (Skia's lookup is case-sensitive on Linux only).

## Upstream Policy (important)

**Never work around AngleSharp / AngleSharp.Css bugs locally.** Reproduce them with a failing test in the upstream project's own suite, fix them there, verify locally via a temporary `ProjectReference` to the sibling checkout, then revert to `PackageReference` once released. Wrong output in the meantime is acceptable. Current minimums: `AngleSharp` 1.8.1+, `AngleSharp.Css` 1.1.4-beta.341+ (a pre-release, until 1.1.4 ships), set via `AngleSharpVersion`/`AngleSharpCssVersion` in the `.csproj` and the `ANGLESHARP_VERSION`/`ANGLESHARP_CSS_VERSION` env of `ci.yml` and `update-snapshots.yml` (which override the `.csproj`, so change all three together). Prefer delegating CSS grammar to AngleSharp.Css's public parsers (`TransformParser`, `FilterParser`, `GradientParser`, `GridParser`, `TimingFunctionParser`) and keeping only the conversion to backend-agnostic `Render*` types here. Hand-roll only what upstream genuinely lacks (e.g. `clip-path` basic shapes).

**Styling comes only from the computed style** (the render tree, cascaded with the UA stylesheet), never from tag names: no "`<b>` is bold", "`h1` is 700", "`<span>` is inline", "`<script>` is hidden". An unset `display` is its initial value, `inline`. Tag names are only legitimate for element semantics that CSS does not express (replaced-element content, form-control values, `<select>` options, `ol`/`li` ordinals). When the UA sheet lacks a rule, file it upstream as a failing test.

Upstream status: every issue filed so far is fixed as of AngleSharp.Css 1.1.4, except `justify-content`/`align-content: space-evenly`, which computes as empty (`Styling/FlexSpaceEvenlyComputedStyleTests.cs`; the renderer already lays it out). `BuildDisplayList_NestedFlexContainerGrowsAndDistributesItsItems` fails (and the `test03-flexbox` case renders packed items) until it is fixed.

## Core Layout Invariants

- **Box paints behind children.** An auto-sized box's height is known only after its children are emitted, so `LayoutElement`/`LayoutFlexContainer`/`LayoutGridContainer` capture `boxPaintInsertIndex`, build the box's own background/shadow/border/outline into a scratch `DisplayList`, and splice it in with `InsertRange`. Painting order per box: (transform → blend → opacity → filter → clip-path scopes), background, `box-shadow`, border, outline, then the `overflow` clip, then children. Pops for clip/filter/opacity/transform are appended after children, innermost first.
- **Paint order = layout order.** `OrderChildrenForPainting` decides the order children are *laid out* too (it threads `cursorY`). Do not reorder it to fix paint stacking; relocate emitted commands instead (`MoveRangeToEnd`, as page-level `position: sticky` does).
- **`height` is a floor for in-flow content** (`Math.Max(specified, autoContent)`). Genuine overflow comes from positioned descendants or over-wide children.
- **`box-sizing`** is converted in exactly one place, `ResolveAuthoredDimension`; everything downstream works in content-box sizes. Flex item sizes are already converted before `ResolveFlexibleContentDimension` reads them - do not convert twice. Tables ignore `box-sizing`.
- **Style maps.** `CreateStyleMap` builds a per-element `StyleMap` (string map plus a typed raw-length side channel used by `ParseLength`). Always go through `GetOrCreateStyleMap` (cached per render call in `LayoutContext.StyleMapCache`; the cache must never outlive one `BuildDisplayList`/`RenderToPng`/`CaptureLayoutMetrics` call or interactive state goes stale). Code writing directly into a length entry must call `StyleMap.ClearRawLength` for that key.
- **Explicit vs computed values.** `background-image`, `grid-template-*`, gaps and `grid-column`/`grid-row` are read from the cascaded-but-uncomputed declaration (`ResolveExplicitPropertyValue` via the cached `LayoutContext.StyleCollection`), because AngleSharp.Css's `.Compute()` resolves their percentages against the viewport instead of the element's box.
- **Unset ≠ initial.** AngleSharp.Css often reports an unset property as an empty string rather than its initial value; parsers fall back to the inherited value (inherited properties) or the CSS initial value.
- Adding a new `RenderPaint` (or other) subtype: check every place that pattern-matches on the existing ones (`PaintBackground` once silently dropped `RenderImagePaint`).
- **Flow layout.** `LayoutFlowChildren` (used by `LayoutElement` and the page itself) splits children into block-level children and runs of inline-level content. A run of bare text keeps the paragraph path (`LayoutTextNode`/`LayoutWrappedText`/`WrapText`); any run containing an element goes through `LayoutInlineFormattingContext`: the subtree is flattened into `InlinePiece`s (whitespace collapsed across element boundaries, break opportunities per piece), greedily broken into line boxes, and each line is emitted with one baseline (strut + half-leading per piece + atomic margin boxes). Inline boxes paint per line fragment; block-level boxes inside inline content get a line of their own.
- **Atomic inlines and shrink-to-fit.** Inline-blocks (and inline-flex/grid/table, form controls, `<img>`/`<svg>`) are measured by `TrialLayout`, which lays the element out into a scratch list under a temporary `LayoutCapture` (so real metrics capture is untouched) and reads its border box and last text baseline (`RecordTextBaseline`). Auto widths of atomics, floats and absolutely positioned boxes use `ResolveShrinkToFitBorderBoxWidth` (intrinsic widths from `ComputeContentIntrinsicWidths`), then `LayoutNode(..., forcedBorderBoxWidth, isAtomicInline)`. Nested inline-blocks multiply trial layouts, so keep the measurement paths cheap.
- **Pseudo-elements** proxy `LocalName`/`GetAttribute` to their host. Guard tag-based special cases (`ResolveFormControlKind`, `TryResolveImage`, `TryResolveInlineSvg`, replaced-element/`<svg>` checks) with `IPseudoElement`. A pseudo's `TextContent` (and its host's) never includes generated content; read text from the render tree, as the inline collector does.

## Feature Notes

Only the non-obvious parts; see XML doc remarks in `HtmlRenderer.cs` for detail.

### Box model, positioning, painting
- Floats, `inline-block`, relative/absolute/fixed/sticky positioning, z-index ordering (CSS 2.1 Appendix E: a stacking context's own background paints before its negative-z descendants).
- `position: sticky`: only `top` is supported; stuck Y is `Math.Max(naturalY, viewportTop + top)` because layout already runs in scrolled coordinates. Without an authored `top` it behaves like `static`. Paint order is fixed only for direct page children (their command range is moved to the end); nested sticky elements may be painted over by later siblings.
- Floats: each block formatting context owns a `FloatArea` (placed float margin boxes, absolute coordinates) that is passed down instead of the old offset/bottom pair. Ordinary blocks share their parent's area, so their *box* ignores floats while their lines (`GetBand` per line in the IFC; a bare-text run beside floats goes through the IFC too) wrap around them; boxes that establish a new context (`EstablishesBlockFormattingContext`: floats, positioned boxes, atomics, flex items, `overflow` other than `visible`, `flow-root`, flex/grid/table) get a fresh area, sit beside outer floats as a whole, never collapse margins with children, and grow to contain their own floats. `FloatArea.Place` puts a float as high as possible on its side, never above an earlier float, moving down when it does not fit. Floats and atomics keep their authored margins (no over-constrained margin absorption). `clear: left|right|both` uses `GetClearance`. Not modeled: lines moving down when even their first word does not fit beside a float.
- `min-width`/`max-width` clamp block widths (a clamped width centers with `margin: auto`). Replaced elements without a size use their natural size.
- On a box, `vertical-align` positions the box in its line only; it is reset for the box's own contents.
- `border-radius`: shorthand and longhands, elliptical, as `RenderCornerRadii` (8 floats). `ClampToBox` applies the spec's single global scale factor. A uniform-width rounded border is one `StrokeRoundedRect`; mixed widths fall back to square corners. `outline` never rounds.
- `box-shadow`/`text-shadow`: parsed from AngleSharp.Css's normalized `rgba(...)` output (`ExtractColorToken`). Layers paint back-to-front in reverse order. Outset shadows are clipped out of the border box (`Difference`), inset ones clipped into it. Blur sigma = radius / 2 (a common approximation, not pinned by a conformance test).
- `overflow`: `hidden`/`scroll`/`auto`/`clip` clip at the padding edge (no scrollbars), as one rectangle for both axes, reusing the box radii. Border and outline are never clipped.
- `clip-path`: hand-rolled `circle()`/`ellipse()`/`inset()`/`polygon()` against the border box. No `url()`, `path()`, or box keywords (those disable clipping); `inset() round` takes a single uniform radius.
- `transform`/`transform-origin`: `TransformParser` + `ComputeMatrix`, converted to `RenderTransform2D` (CSS `matrix(a..f)` order). AngleSharp.Css's `TransformMatrix` is row-vector, so `M12`/`M21` are transposed (`M11→A, M21→B, M12→C, M22→D`). Functions post-multiply left to right. The origin pivot must be in absolute canvas coordinates. 3D functions are a permanent scope cut (no projection).
- `filter`: `FilterParser` gives function names; argument meaning (color matrices, blur, `drop-shadow`) is implemented here, independently of `SvgFilterBuilder`. Unknown functions and `url(#id)` are skipped. Painted via `SaveLayer`.
- `opacity`: group composite via `SaveLayer`, so overlapping children blend as one unit. `opacity: 1`, `transform: none` and empty `filter` push nothing.
- `transform`/`opacity`/`filter` are wired in `LayoutElement` only, not on flex/grid containers themselves (items work).
- `mix-blend-mode` and `isolation: isolate` are `PushBlendMode`/`PopBlendMode` scopes (a Skia `SaveLayer` with a blend mode) nested between transform and opacity. `background-blend-mode` blends the single image layer with the background color inside its own isolated group; the background color always paints under an image/gradient. `background-clip` insets the painted `FillRectCommand.Rect` to the padding or content box; `background-origin` (initial `padding-box`) sets `FillRectCommand.PaintArea`, the box an image/gradient shader is sized and positioned in (`SkiaRenderBackend.DrawFillRect` uses `PaintArea ?? Rect`).
- `outline-offset` grows (or, negatively, shrinks) the outline rect.
- Border styles: `dotted` (round dots), `dashed`, `double`, `groove`/`ridge`/`inset`/`outset` (bands across the thickness with a darker shade, `GetBorderBands`). Rounded borders other than uniform solid/dashed/dotted are painted per edge as `FillRingCommand`s (outer minus inner rounded rect, inner radii = outer minus border widths) clipped to the edge's trapezoid, so widths blend along the curve and colors meet on the diagonals; dashed/dotted rounded borders keep straight segments clipped to the outer curve.
- Backgrounds are a list of `BackgroundLayer`s (`ParseImageLayers`, shared with masks): each `background-image` item takes the matching item of `-size`/`-position`/`-repeat`/`-clip`/`-origin`/`-blend-mode` (lists repeat); layers paint bottom-up, the color under them clipped like the bottom layer. `RenderImagePaint.RepeatModeX/Y` carry `space`/`round`; a sized/positioned/repeated gradient gets a `RenderBackgroundTile`. The Skia backend tiles both through a recorded picture shader (`CreateTiledPaint`), which is how `space` gaps and `round` scaling work.
- `border-image` (`PaintBoxBorder`/`ResolveBorderImage`) replaces the border with a `DrawBorderImageCommand`: image or gradient source, slices (numbers or %), widths (numbers × border width, lengths, %, `auto`), outset, `stretch`/`repeat`/`round`/`space`, `fill`. Drawn as a nine-slice in `SkiaRenderBackend.DrawBorderImage`.
- Masks: `mask-image` layers (image/gradient) with `-size`/`-position`/`-repeat`/`-mode`/`-clip`/`-origin` (border box by default) become a `PushMaskCommand`/`PopMaskCommand` scope, innermost of the whole-element scopes; the backend composites the layers with `DstIn` (luminance via a luma color filter). `mask-composite` other than `add`, and `url(#svg-mask)` references, are not supported.
- `backdrop-filter` is a `DrawBackdropFilterCommand` emitted right inside the transform scope. SkiaSharp 2.x has no backdrop save-layer, so the backend snapshots the surface behind the box, filters it and draws it back clipped to the rounded border box; content inside a still-open ancestor layer (opacity, filter, mask) is not part of that backdrop.
- `translate`/`rotate`/`scale` compose before `transform` (`ToTranslateFunction` etc.), around the same `transform-origin`.
- Backgrounds: colors, `url(...)` images (`data:` or via `IDocumentLoader`, synchronous, shared per-document `DocumentImageCache` with `<img>`; failure falls back to `background-color`), `background-repeat`/`-position`/`-size` resolved at paint time in `CreateImagePaint`. `no-repeat` uses `SKShaderTileMode.Decal`, not `Clamp`. `space`/`round` act as `repeat`.
- Gradients: linear/radial/conic and `repeating-*` via `GradientParser`. Gotchas: Skia shader matrices map local → world space; `CreateSweepGradient` angles must stay within `[0, 360]` (rotation goes in the matrix); linear angles are shifted by -90° to Skia's convention in `ConvertLinearGradient`, conic does its own conversion; absolute-length stop positions stay unresolved (`AbsolutePositionPixels`) until the geometry is known. SVG gradients are a separate implementation.
- `aspect-ratio` derives width from a definite height; `object-fit`/`object-position` apply to replaced elements.

### Text
Every text feature exists twice - in the paragraph path (whole lines as strings) and in the inline formatting context (pieces) - so change both. `MeasureTextWidth` and `DrawStyledText` are the only measure/paint entry points: they drop soft hyphens, synthesize small caps (lowercase as uppercase at `SmallCapsScale` 0.7, run by run) and paint `text-shadow` first, so every path (cells, markers, form labels) agrees.
- `white-space`: `NormalizeWhitespace(text, mode)` implements the collapse/preserve table for the paragraph path; the IFC applies the same rules per piece (`\n` as forced breaks, `nowrap`/`pre` without soft wraps). Preserved tabs reach the next `tab-size` stop: an exact-width `IsTab` space piece in the IFC (runs split around it), expanded to whole spaces in the paragraph path (`ExpandTabs`).
- `text-align: justify` widens the line's untrimmed spaces (runs are then painted word by word); the last line and lines ended by a forced break use `text-align-last` (`ResolveLineAlign`), defaulting to start. Bidi-reordered paragraph lines are not justified.
- `hyphens`: soft hyphens (U+00AD) are break opportunities (`auto` = `manual`, no dictionary); a line broken there shows `-`. The IFC splits words into syllable pieces carrying `HyphenWidth`; `BuildLine` only takes a syllable when its hyphen fits too or the rest of the word fits.
- `text-overflow: ellipsis` (not inherited; only when overflow clips) and `-webkit-line-clamp`: the paragraph path truncates a single line by rune; the IFC truncates every overflowing line (`TruncateLineWithEllipsis`, by grapheme, keeping box edges) and stops after the clamped line, adding `…` when content remains.
- `word-break: break-all` wraps per character; `overflow-wrap`/`word-wrap: break-word`/`anywhere` split a word only when it cannot fit even an empty line. `keep-all` = `normal`.
- `text-transform` (inherited; `capitalize` tracks word starts across inline elements) and `word-spacing` (paints word by word, since a text run cannot express it). Runs drop their edge spaces unless decorated.
- `font-weight: bolder`/`lighter` (the UA rule for `b`, `strong`, `th`, headings) resolve against the inherited weight.
- `font-stretch` travels as a percentage on `RenderFont`/`DrawTextCommand` and selects an installed or `@font-face` width class; the bundled fonts have one width and nothing is synthesized.
- Bidi: the IFC builds the line text (atomics as U+FFFC, box edges as `unicode-bidi` controls or U+2060), runs `BidiAlgorithm` and splits pieces into visual fragments (`ReorderLine`); the paragraph path paints reordered lines as visual runs (`GetVisualRuns`). `direction` also resolves `start`/`end`.
- `text-decoration` (line/color/style), `letter-spacing`, `text-indent` (never inherited), `vertical-align` shifts as fixed font-size fractions.

### Lists, counters, generated content
- `display: list-item` uses normal block flow; `PaintListItemMarker` paints shape markers via the `border-radius` machinery and text markers via `DrawText`. Outside markers sit a fixed 6px left of the border box (AngleSharp.Css indents lists with `margin-left`, not padding). Inside markers boost the first line's `TextIndent`.
- Ordinals: `ResolveListItemOrdinal` walks DOM siblings (`start`, `reversed`, `value`). General counters (`counter-reset`/`-increment`/`-set`, `counter()`/`counters()`) are also supported.
- `::before`/`::after` come from AngleSharp.Css's render tree (UA default `display: inline`). `url()`/quote keywords in `content` render as empty.

### Flexbox and Grid
- Flexbox with grow/shrink/basis/wrap/alignment/order.
- Grid: `GridParser` structural parsing; `fr`, `repeat()` (including auto-fill/auto-fit fill counts), `minmax()`, `grid-template-areas`, `grid-area`, `grid-auto-flow`, `grid-auto-rows`. `CssRepeatValue`/`CssFitContentValue` are internal upstream, so they are matched via `ICssFunctionValue.Name`. Sizing is two-pass: placement is resolved once in pass 1 (and reused, to keep the auto-placement cursor consistent) while `Auto` tracks grow; `fr` space is distributed between passes. `fr` rows need a definite container height. Named lines are ignored.

### Multi-column
`column-count`/`column-width`/`column-gap` (`normal` = 1em)/`column-rule` on block containers. Content is laid out once at the column width into a scratch list while recording line/block bottoms (`RecordColumnBreakCandidate`) and line tops (`RecordColumnContentStart`); the smallest column height that fits into the column count without splitting a line is chosen, and each column replays the scratch list under a clip plus translate (margins at a break are truncated). An authored height fills columns sequentially. No `column-span`, `break-*` or `column-fill`.

### Tables
Rows, row groups, cells and columns are found by computed `display` (`table-row`, `table-*-group`, `table-cell`, `table-column(-group)`), not tag names. Cells span columns/rows; a spanning cell's height is shared across its rows. `border-collapse: collapse` paints each cell's top/left edges plus the table frame. Cell `vertical-align` positions the content box (`baseline` = `top`). No `table-layout`, `border-spacing`, captions, or `box-sizing`.

### Form controls
- A form control's look (border, padding, background, margins, round radio) comes from the UA stylesheet. `ApplyFormControlDefaults` only fills in the element-derived intrinsic size (text fields 150px, checkbox/radio from font size, buttons/selects/file inputs from their label) when `width`/`height` are unset.
- `PaintFormControl` paints values, labels, the selected option, checked fills, color swatches, range track/thumb and file inputs; `<option>`s and button labels are excluded from child layout. `<textarea>` flows its text normally. `type=hidden` gets no chrome (hiding it is the UA sheet's job).
- Labels center on ink using `FormControlTextAscentRatio`/`DescentRatio` (font-size fractions measured from the bundled font). The select arrow glyph `⌄` has its own vertical correction.
- A focused text input / textarea shows a caret (focus read from `IElement.IsFocused`) that fades via `IDomHarness.CurrentTime`; without a harness it is fully opaque.

### Interactivity and scrolling
- Page scroll: `RenderToPng`/`BuildDisplayList` shift layout by the root scroll offset only when a harness already exists (`TryGetDomHarness`, never `GetDomHarness`, which throws without an `IRenderDevice`). `CaptureLayoutMetrics` never applies the offset (scroll extents must be scroll-independent) and lays out the full page (`measureFullExtent`).
- `:hover` uses AngleSharp.Css's `SetPseudoClass`, which does not propagate; `UpdateForcedHoverChain` forces the whole ancestor chain and tracks it to un-force later.
- `CssTransitionTracker`/`CssAnimationTracker` run on a virtual clock (`IDomHarness.AdvanceTime`). Only `CssValueInterpolation.InterpolatableProperties` (colors, lengths, `opacity`) interpolate; `all` means that whitelist. Overrides are injected by `ApplyActiveTransitionAndAnimationOverrides` over the whole whitelist (not only existing keys). Transition "from" values must be captured before pseudo-class mutation. Reversals restart with the full duration. Animations start at clock zero; fill-mode/direction/iteration share one effective-time computation; `animation-play-state: paused` is supported.

### SVG
`<img src="*.svg">` (sniffed in `TryLoadImageResource`) and inline `<svg>` (rasterized from the live DOM, cached per element). Rasterized at natural size × `OversampleFactor`, not at CSS size. Supports shapes and full path data, percentages, `g`/`a`/`use`/`symbol`/nested `svg`, `text`/`tspan`, `clipPath`, `mask`, gradients, patterns, filters (`feGaussianBlur`, `feOffset`, `feMerge`, `feColorMatrix`, `feDropShadow`, `feFlood`, `feComposite`, `feMorphology`, linear `feComponentTransfer`; others pass input through), `currentColor`, `preserveAspectRatio`, and an internal `<style>` subset. Page CSS never cascades into SVG. `SvgTransformParser` must apply the rightmost function first (`next.PostConcat(result)`) - verify changes there visually.

## Known Gaps

- Flex/grid intrinsic sizing is approximate (no `min-width: auto` content minimum for shrinking flex items); `flex: 1` keeps its content-based basis.
- Text shaping (HarfBuzz): Arabic joining, ligatures, kerning, `font-feature-settings`, `font-variant-*` beyond caps. Hyphenation dictionaries (`hyphens: auto`). `-webkit-line-clamp` counts lines per inline run, not across nested blocks.
- Not implemented: `writing-mode`, 3D transforms, `background-clip: text`, `mask-composite` other than `add`, named grid lines, the table features listed above.

## Code Conventions

Follow `.editorconfig` and nearby code: 4 spaces (2 in `.csproj`), LF, UTF-8, no trailing whitespace, `var` when the type is obvious, narrow changes, no whole-file reformatting.

## Tests And Snapshots

- Structural tests: `HtmlRendererTests.cs` (and `TableSpanTests.cs`, ...) assert on the display list.
- Visual tests: `VisualConformanceTests.cs` compares PNGs against `verification-assets/`; `CaseRenderingTests.cs` renders full pages from `cases/`. Failures write actual + diff images to `failure-assets/` (diagnostics, not source of truth).
- Missing baselines are auto-created unless `ANGLESHARP_SNAPSHOT_STRICT=1`/`true` (CI always sets it). `ANGLESHARP_SNAPSHOT_UPDATE=1` overwrites the current platform's baselines.
- Update or add tests first when changing renderer behavior.

### Platform matrix

Baselines are per platform (`<name>.linux.png`/`.windows.png`/`.macos.png`) because FreeType, DirectWrite and CoreText anti-alias the same bundled font differently. `SnapshotBaselineCoverageTests` fails everywhere when a variant is missing. `ci.yml` runs on pinned `ubuntu-22.04`/`windows-2022`/`macos-14` (bumping one means regenerating baselines). Flow: change code, refresh your own platform's baselines locally, push, then dispatch the **Update Snapshots** workflow (`update-snapshots.yml`) on the branch for the other two. Its bot push does not retrigger CI.

## Performance Benchmarks

`src/AngleSharp.Renderer.Benchmarks` (BenchmarkDotNet, not part of `dotnet test`) measures parse, `BuildDisplayList`, and `RenderToPng` on one realistic ~1500-element page:

```bash
cd src/AngleSharp.Renderer.Benchmarks
dotnet run -c Release -f net10.0 [-- --job short]
```

Record new numbers plus a `History` entry in `BASELINE.md` whenever an optimization lands. Past wins: style-map caching, caching the explicit-declaration style collection, skipping flex/grid container-only reads for non-containers. A typed `ParseLength` fast path gave no measurable gain because AngleSharp.Css's `.CssText` serialization dominates; skipping the string entry entirely is untested and first needs proof that nothing reads those 17 keys as raw strings.

## Repository Notes

- User-facing docs live under `docs/general/` and `docs/tutorials/`.
- Add release notes to `CHANGELOG.md`.
- Linux uses `SkiaSharp.NativeAssets.Linux.NoDependencies`: no fontconfig, but it scans `/usr/share/fonts/`, so named families depend on the runner image. CI deliberately installs no extra fonts.
