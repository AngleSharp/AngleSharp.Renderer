# AGENTS.md

Guidance for AI coding agents working in this repository. Keep this file as the quick-start reference for the repo's commands, architecture, conventions, and test workflow.

## What This Repository Is

AngleSharp.Renderer adds rendering capabilities on top of AngleSharp and AngleSharp.Css. The current implementation is a display-list renderer with a SkiaSharp backend that rasterizes HTML/CSS content into PNG images.

## Most Important Commands

Use the solution-level test command for normal validation:

```bash
dotnet test src/AngleSharp.Renderer.sln
```

Use the test project directly when iterating on renderer behavior:

```bash
dotnet test src/AngleSharp.Renderer.Tests/AngleSharp.Renderer.Tests.csproj
```

The repo also includes build scripts that drive the Fallout bootstrapper:

```bash
./build.sh
./build.ps1
./build.cmd
```

The main renderer project targets `net8.0` and `net10.0`; the test project targets both as well, so both runtimes are exercised.

## Architecture

The renderer is intentionally split into a small number of layers:

- `HtmlRenderer` builds a display list from the AngleSharp render tree and computed styles.
- `DisplayList` is the backend-agnostic command model.
- `SkiaRenderBackend` turns the display list into a PNG using SkiaSharp, and implements `ITextMeasurer` so layout measures with the very typefaces it paints with. Both paths resolve fonts through `SkiaTextShaping`, which exists to keep them from drifting apart.
- Font resolution lives in `SkiaTextShaping.CreateTypeface` and walks the CSS family list in order: generic families map to the bundled fonts, named families resolve only when actually installed, and an exhausted list falls back to the bundled sans-serif. Do not resolve named families with `SKTypeface.FromFamilyName` - it substitutes the host's default for an unknown family instead of returning null, which swallows the rest of the fallback list and makes output depend on the machine. Availability goes through a case-insensitive index of the installed families, because Skia's own lookup is case sensitive on Linux but not on Windows.
- `ITextMeasurer` is the seam between layout and rasterization. Line breaking, text alignment and table column widths all go through it; a renderer built with a custom measurer lays out against that measurer. Never reintroduce a font-independent width heuristic here - it silently decouples layout from what is drawn.
- `HtmlRenderOptions` holds viewport and text defaults.
- `AngleSharp.Css` provides the render tree and computed-style data used by the renderer.
- `SvgRasterizer` (in `Skia/`) turns SVG content into PNG bytes without a second SVG parser: it walks the DOM AngleSharp already produced and paints directly with SkiaSharp (`Skia/Svg/`: `SvgElementRenderer` walks the tree, `SvgPathDataParser`/`SvgTransformParser`/`SvgColorParsing` decode the `d`/`transform`/paint attributes, `SvgPaintState` carries inherited fill/stroke/opacity down the tree). The PNG it produces flows into an ordinary `RenderedImage` afterward, so `DrawImageCommand` and `SkiaRenderBackend.DrawImage` need no SVG-specific handling.

Font handling resolves each entry of a `font-family` list in order, and the first usable one wins:

1. Generic families (`serif`, `sans-serif`, `monospace`, plus `cursive`, `fantasy`, `system-ui` and the `ui-*` aliases) always come from the fonts bundled in `Resources/Fonts`. They are keywords, so an `@font-face` rule cannot take them over, and they are what keeps snapshots reproducible.
2. `@font-face` declarations, collected per document by `FontFaceLoader` and carried on `DisplayList.Fonts`. Sources are tried in declaration order; `local()` resolves against the installed fonts and `url()` against `data:` URIs, or the network when - and only when - the browsing context has an  `IDocumentLoader` configured, mirroring how images are handled. WOFF and WOFF2 are rejected up front because Skia cannot decode them.
3. Installed families, which depend on the host and are therefore not safe to assert in snapshots.
4. The bundled sans-serif, as the last resort.

Table spans: a cell covers the columns and rows it spans, and a spanning cell's height is shared across the rows it covers rather than imposed on each of them. With `border-collapse: collapse` each cell paints only its top and left edge and the table adds the frame, so shared edges are drawn once and no rule is painted across a spanning cell. Cell content honours `vertical-align` (`top`, `middle`, `bottom`), defaulting to the `middle` that AngleSharp.Css resolves for cells; `baseline` is treated as `top`, since baselines are not aligned across a row. Note that on a cell `vertical-align` positions the content box, which is a different meaning from the inline shift `ParseVerticalAlign` applies to `super`, `sub` and friends.

`border-radius` (rounded corners): the shorthand and all four longhands (`border-top-left-radius` etc.) are supported, each independently elliptical (`border-top-left-radius: 20px 10px`, or the whole-box `border-radius: 40px / 20px` slash shorthand) and represented backend-agnostically as `RenderCornerRadii` (`Rendering/RenderCornerRadii.cs`) - eight floats, X/Y per corner. `RenderCornerRadii.ClampToBox` implements the CSS corner-overlap-prevention algorithm (https://www.w3.org/TR/css-backgrounds-3/#corner-overlap): a single global scale factor, not a per-axis one, is applied to every radius so adjacent corners along the same edge never overlap - this is what turns a `border-radius` larger than half a box's height into a pill/stadium shape rather than a self-intersecting curve. A background fills with `SKRoundRect`/`DrawRoundRect` (`SkiaRenderBackend.DrawFillRect`) instead of `DrawRect` whenever `Radii` is non-zero. A border with rounded corners is painted differently depending on whether every edge shares the same width: a uniform-width border becomes a single `StrokeRoundedRectCommand` (one stroked ring, inset by half the border width, with the radius reduced by that same half-width since the stroke sits on the border's centerline); a mixed-width border (e.g. `border-right-width` wider than the others) has no single stroke width to give that ring, so it falls back to the existing four-straight-rectangle border path and the corners render square - a deliberate, documented limitation rather than an attempt at four independently-rounded quads. `outline` never rounds regardless of `border-radius`, matching the CSS spec, because `PaintOutline`'s internal `PaintBorder` call never passes the element's radii. Percentage radii resolve to pixels through AngleSharp.Css's computed-style engine before this renderer ever sees them, but that resolution has a verified quirk in the AngleSharp.Css version this repo builds against (1.1.0): both the horizontal and vertical component of a percentage radius resolve against the *containing block's width*, not per-axis against the element's own width and height as the CSS spec technically prescribes - confirmed by rendering the same `%` radius against different viewport widths and observing the resulting pixel value track the viewport, not the element's own box. This is a property of the upstream computed-style engine, not of this renderer's own parsing.

`box-shadow` and `text-shadow`: both are parsed from AngleSharp.Css's `GetBoxShadow()`/`GetTextShadow()` computed-style accessors, which return every comma-separated layer already normalized with the color as `rgba(r, g, b, a)` regardless of how it was authored (`red`, `#f00`, ...) - `ExtractColorToken` (`HtmlRenderer.cs`) relies on that normalization to split a layer's color from its lengths by locating the `rgba(...)` call rather than naively splitting on whitespace, which the spaces after that function's commas would otherwise break. `text-shadow` is an inherited property and AngleSharp.Css already resolves that inheritance before this renderer sees it (a child with no `text-shadow` of its own reports its ancestor's value verbatim, and an explicit `text-shadow: none` reports the literal string `"none"`, not an empty one) - `ParseTextShadows` still takes an `inherited` fallback for defense in depth, mirroring how `color` and the other inherited text properties are already handled in `ResolveTextStyle`. `box-shadow` is not inherited, matching spec, so `ResolveBoxStyle` parses it directly with no inherited fallback. Multiple shadows paint back-to-front in reverse authoring order (`PaintBoxShadows`/`PaintTextShadows`), because CSS defines the first-listed shadow as topmost. A `box-shadow` layer is `RenderBoxShadow` (`Rendering/RenderShadow.cs`) - offset, blur, spread, color, and `inset` - painted via `DrawBoxShadowCommand`/`SkiaRenderBackend.DrawBoxShadow`, which reuses the same `RenderCornerRadii`/`SKRoundRect` machinery `border-radius` already established so a shadow follows the box's own corner rounding. An outset shadow is the border box's `SKRoundRect` grown by `SpreadRadius` (via `SKRoundRect.Inflate`/`Deflate` - verified functionally to adjust the rect *and* clamp each radius down to zero together, exactly matching the CSS spread semantics for free) and offset, blurred with `SKMaskFilter.CreateBlur`, then clipped with `SKClipOperation.Difference` against the *un-inflated* border box so the shadow never bleeds into the box's own interior even when its background is transparent - this clip-out is required by spec, not an approximation. An inset shadow is the mirror image: the border box shrunk by spread and offset, painted as the even-odd "ring" between that shrunk shape and a rect well outside the box, clipped with `SKClipOperation.Intersect` to the border box so it only shows inside. Both cases derive the Gaussian blur sigma as half the CSS blur radius (`shadow.BlurRadius / 2f`), the same approximation browsers commonly use; there is no dedicated conformance test pinning that constant, so treat it as adjustable if a real blurred-shadow comparison ever disagrees with it. `text-shadow` has no spread or inset, and paints as a second, blurred copy of the same text run (`DrawTextShadowCommand`/`SkiaRenderBackend.DrawTextShadow`) offset behind the real glyphs, shaped through the same `SkiaTextShaping` path as ordinary text so a shadow never drifts out of alignment with what it is shadowing. Painting order for a box is background, then `box-shadow` (all layers), then border, then outline - box-shadow must sit on top of the background (not behind it) even though it is visually "behind" the box's own content, because an inset shadow paints inside the box and would otherwise be hidden by an opaque background; this was a real bug caught by rendering the inset-shadow visual test and seeing no shadow at all, fixed by reordering `PaintBoxShadows` after `PaintBackground` at all three box-painting call sites.

Current behavior includes block layout, margins, padding, borders, floats, inline-block, relative/fixed/absolute positioning, z-index ordering, outlines, text styling, text alignment, line-height, letter-spacing, text-indent, vertical-align, border-radius, box-shadow, text-shadow, and generic font-family handling.

SVG support: `<img src="*.svg">` (including `data:` URIs) and inline `<svg>` markup both render, through two loading paths that converge on the same DOM-walking rasterizer (`Skia/Svg/`) - no third-party SVG parser is involved anywhere. An `<img>` SVG source is sniffed and rasterized inside `TryLoadImageResource`, exactly where a PNG/JPEG source is decoded: `SvgRasterizer.TryRasterizeMarkup` parses the bytes with AngleSharp's own HTML/foreign-content parser (wrapped in a throwaway `<html><body>` shell) and is cached per-document by URL like any other image. Inline `<svg>` has no URL and, more importantly, is already sitting in the host document's DOM - `SvgRasterizer.TryRasterizeElement` walks that element directly and is cached per-element in `s_inlineSvgCacheByElement`; it is never serialized back to text and re-parsed. `LayoutElement` empties `orderedChildren` for an `<svg>` root so its foreign-namespaced children are never walked as HTML flow content; both `<img>` and inline `<svg>` are treated as a single replaced element. Rasterization always happens at the SVG's own natural (`viewBox`/`width`/`height`) size oversampled by a fixed factor (`SvgRasterizer.OversampleFactor`), not at the resolved CSS box size - the loader runs before CSS sizing is known, so this is a deliberate blur-vs-memory tradeoff rather than a per-render-size cache.

Supported elements: `rect`/`circle`/`ellipse`/`line`/`polyline`/`polygon`/`path` (full path-data grammar including elliptical arcs, via `SKPath.ArcTo`'s SVG-shaped overload; every geometry attribute - `x`/`y`/`width`/`height`/`cx`/`cy`/`r`/`rx`/`ry`/`stroke-width` - accepts percentages, resolved against the current `SvgViewport` via `SvgLength`, per axis for axis-specific properties and against the viewport diagonal for the rest), `g`/`a` grouping with `transform`, `use` (cycle-guarded in `SvgRenderContext.ActiveUseReferences`; referencing a `symbol` or nested `svg` establishes a new viewport sized from the `use`'s own `width`/`height`, not just a translated copy), a nested `<svg>` establishing its own sub-viewport (`x`/`y`/`width`/`height`/`viewBox`/`preserveAspectRatio`, clipped to its own box), `text`/`tspan` (via `SkiaTextShaping.CreateTypeface`, the same font-resolution path HTML text uses), `clipPath` (shape-union clipping via `SKCanvas.ClipPath`), `mask` (luminance masking via `SKCanvas.SaveLayer` + `SKColorFilter.CreateLumaColor()` + `SKBlendMode.DstIn`, honouring its own `maskUnits`/`x`/`y`/`width`/`height` region - default `-10%/-10%/120%/120%` of the masked element's bounding box, computed by `SvgGeometry.ComputeBounds` - and `maskContentUnits="objectBoundingBox"`), `linearGradient`/`radialGradient`/`pattern` as `fill`/`stroke` paint servers (`SvgGradientBuilder`/`SvgPatternBuilder`; `objectBoundingBox`/`userSpaceOnUse`, `gradientTransform`/`patternTransform`, `spreadMethod`, `href`/`xlink:href` inheritance chains; a pattern's own content is rendered once into a tile bitmap via the same `SvgElementRenderer.Render` entry point the root `<svg>` uses, then repeated with `SKShader.CreateImage`), `filter` (`SvgFilterBuilder`: a primitive-chain interpreter for `feGaussianBlur`/`feOffset`/`feMerge`/`feColorMatrix`/`feDropShadow`, applied via `SKCanvas.SaveLayer` with an `SKImageFilter`; an unsupported primitive passes its input through unchanged instead of breaking the chain), `currentColor` (resolves against the CSS `color` property, itself inherited like any other paint property - including when set directly on the root `<svg>`, which `SvgElementRenderer.Render` resolves before walking children, since nothing else ever visits the root itself), `preserveAspectRatio` (`meet`/`slice` and all nine alignment keywords - `SvgViewBoxMapping`, shared by the root `<svg>`, nested `<svg>`, and `symbol`), and SVG-internal `<style>` cascading (`SvgStyleSheetParser`/`SvgSelector`: type/`.class`/`#id`/`*` compound selectors joined by the descendant combinator with real id>class>type specificity, `/* */` comments stripped, and top-level `@`-rules - `@media`, `@import`, ... - skipped wholesale since there is no viewport/user-agent context to evaluate a condition against).

Not implemented, and silently skipped rather than approximated: `feFlood`/`feComposite`/`feTurbulence`/`feDisplacementMap`/`feTile`/`feImage`/`feComponentTransfer`/`feConvolveMatrix`/`feDiffuseLighting`/`feSpecularLighting`/`feMorphology` filter primitives (pass their input through unchanged), a `filter`'s own region clipping (`x`/`y`/`width`/`height`/`filterUnits` - the effect is not clipped to the nominal -10%/120% region), `SourceAlpha` as distinct from `SourceGraphic` in a filter chain, percentages in `points` (the SVG spec disallows them there too), text measurement contributing to `SvgGeometry.ComputeBounds` (a `<mask>`/gradient/pattern bounding box that depends on text extent falls back to "no bounds", which skips the mask-region clip rather than guessing), and CSS attribute selectors/pseudo-classes/child/sibling combinators in an SVG `<style>` block. Page CSS never cascades into any SVG subtree - only the SVG's own presentation attributes, its own `<style>`, and its own inline `style=` apply, the same limitation `<canvas>` already has. `SvgTransformParser` composes multi-function `transform` lists by applying the rightmost function to the point first (`next.PostConcat(result)`, not `result.PostConcat(next)`) - getting that backwards silently rotates/translates shapes around the wrong origin instead of failing loudly, so any change there needs a visual test with a non-trivial `transform` (see `RenderToPng_RendersInlineSvgPathAndTransform`), not just a structural one. A `<mask>`'s "hole" is a genuinely transparent region in the SVG's own raster, not a rendering bug - once that PNG composites onto the page, a white page background will show through it exactly like a browser would (verified by temporarily giving the page a non-white background and confirming the hole tracks it). CSS `background-image: url(...)` is not supported for SVG or any other image type; only gradient functions are (see `ParseBackgroundPaint`).

CSS gradient support (`background-image`): `linear-gradient`, `radial-gradient`, `conic-gradient`, and their `repeating-*` variants all parse and render, via `ParseGradientPaint`/`ParseLinearGradient`/`ParseRadialGradient`/`ParseConicGradient` in `HtmlRenderer.cs` producing a `RenderGradient`, painted by `SkiaRenderBackend.CreateGradientPaint`. `radial-gradient` honours `circle`/`ellipse` (ellipse - the CSS default - is rendered via a unit-circle shader plus a constructor-time scale/translate matrix, since Skia's radial gradient shader is inherently circular), `at <position>`, and the `<size>` keywords (`closest-side`/`farthest-side`/`closest-corner`/`farthest-corner`, `farthest-corner` being the CSS default - `ComputeRadialRadii`/`EllipseThroughCorner` implement the CSS corner-sizing formula) plus explicit radii. `conic-gradient` honours `from <angle>` and `at <position>`. A `repeating-*-gradient`'s period is realized by rescaling stop positions to fill `[0,1]` and shrinking the shader's own geometric extent (line length / radius / angular span) by the same factor, then using `SKShaderTileMode.Repeat` to tile the rest - see `CreateGradientPaint`'s `repeatScale` computation, shared by all three kinds.

Two non-obvious Skia behaviors drive the linear/radial/conic implementation, both confirmed empirically (not just from docs) because they produced silent, catastrophic failures (a "gradient" that painted as a single flat color) rather than an exception:
- `SKShader.CreateRadialGradient`/`CreateSweepGradient`'s constructor-time `SKMatrix` parameter maps the shader's local space directly into world space (build the gradient in unit-circle/default-angle local space, then let the matrix position and scale it - not the inverse).
- `SKShader.CreateSweepGradient`'s `endAngle` must stay within a plain `[0, 360]` span - passing e.g. `[270, 630]` to rotate the sweep does not treat it as "one full revolution starting at 270"; it silently collapses to a zero-width span, and the whole gradient renders as its first stop's color. The CSS `from <angle>` rotation (and CSS's 0deg-at-top vs. Skia's 0deg-at-right convention) is therefore applied via the same constructor-time rotation matrix instead of shifting the angle span - see `CreateConicGradientShader`.

A gradient stop's position may be a percentage, a bare fraction, or (very commonly, for `repeating-*-gradient` stripes) an absolute length (`red 10px`) - the last of these cannot become a fraction until the gradient's own rendered geometry (line length for linear, resolved radius for radial; conic stops are always angles, never lengths) is known, so `RenderGradientStop.AbsolutePositionPixels` carries it unresolved from parsing through to `CreateGradientPaint`, which divides it by that geometry once computed. `ParseColor` (`HtmlRenderer.cs`) recognizes the full standard CSS named-color table (`red`, `blue`, ...) in addition to hex/`rgb()`/`transparent` - this specifically matters for gradient stop colors, which are parsed from the raw `background-image` value directly rather than through AngleSharp.Css's own color normalization (a `background-image: <gradient>()` value is opaque to AngleSharp.Css, so it is extracted into a `data-render-gradient` attribute by `TryExtractGradientBackground`/`PrepareDocumentForRendering` and hand-parsed); without the named-color table, every stop written with a keyword color silently fell back to the same color, producing an invisible gradient - this was a real, confirmed bug, not a hypothetical one.

SVG gradients (`linearGradient`/`radialGradient` inside an SVG document) are a separate, independent implementation (`SvgGradientBuilder`, see the SVG section above) and do not share code with the CSS gradient path described here, aside from both ultimately building an `SKShader`.

## Code Conventions

Follow the repository's existing C# style, which is defined by `.editorconfig` and the existing source files:

- 4 spaces for code files, 2 spaces for `.csproj` files.
- LF line endings and UTF-8.
- Trim trailing whitespace.
- Use `var` where the type is obvious from the right-hand side.
- Keep changes narrow and consistent with nearby code.
- Preserve the existing file style instead of reformatting whole files.

## Tests And Snapshots

Tests are split between structural assertions and visual conformance checks.

- Structural tests live in `src/AngleSharp.Renderer.Tests/HtmlRendererTests.cs` and verify the display list directly.
- Visual tests live in `src/AngleSharp.Renderer.Tests/VisualConformanceTests.cs` and compare PNG output against baselines in `verification-assets/`.
- Failed visual comparisons write the actual image and a diff image into `failure-assets/`.
- Missing baselines are auto-created unless `ANGLESHARP_SNAPSHOT_STRICT=1` or `true` is set. CI always sets it.
- `ANGLESHARP_SNAPSHOT_UPDATE=1` overwrites the baselines for the current platform instead of comparing.

When changing renderer behavior, update or add tests first, then run the focused test file or the full test project.

### The Platform Matrix

Baselines are per-platform (`<snapshot>.linux.png`, `<snapshot>.windows.png`, `<snapshot>.macos.png`).
They cannot be shared: Skia rasterizes glyphs through FreeType on Linux, DirectWrite on Windows and
CoreText on macOS, so the very same bundled font file yields different anti-aliasing. The bundled
fonts remove *font selection* as a variable, not *font rasterization*.

Because of that, a snapshot can only ever be checked by the platform it was recorded on, and the
usual failure mode is a baseline that silently rots on the platforms the author does not have:

- `SnapshotBaselineCoverageTests` fails on *every* platform as soon as a snapshot is missing a
  platform variant, so an incomplete matrix surfaces locally instead of on a foreign CI leg.
- `ci.yml` runs the whole test suite on Linux, Windows and macOS with strict mode on, and gates
  packaging on all three. The runner images are pinned (`ubuntu-22.04`, `windows-2022`, `macos-14`);
  bumping one is a rasterization change and requires regenerating the baselines.
- `update-snapshots.yml` (`workflow_dispatch`) re-renders the baselines on all three platforms,
  verifies the matrix is complete, and commits them back. This is the only supported way to
  regenerate baselines you cannot produce locally. Note that the bot push does not retrigger CI.

The regular flow for a renderer change is: change the code, run the tests locally to refresh your
own platform's baselines, push, then dispatch **Update Snapshots** on the branch to fill in the
other two.

## Repository Notes

- The docs live under `docs/general/` and `docs/tutorials/`; they are the best place to document user-facing renderer behavior.
- `AGENTS.md` should remain the primary agent note for this repo.
- The repository already carries a snapshot-based workflow, so visual changes usually require updating the baseline PNGs together with the code change.
- The Linux Skia setup uses `SkiaSharp.NativeAssets.Linux.NoDependencies`. It is built without fontconfig, but it is *not* fontless: it scans `/usr/share/fonts/` directly, so whatever that directory holds is what `SKTypeface.FromFamilyName` can resolve. Generic families never reach that path (they come from the bundled fonts), but named families do, which makes them depend on the runner image. CI deliberately does not install extra fonts - determinism for named families has to come from the renderer's fallback, not from curating the runner.
- Keep an eye on `failure-assets/` after test runs; they are useful diagnostics, not source of truth.
