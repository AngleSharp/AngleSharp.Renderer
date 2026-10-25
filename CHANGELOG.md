# 0.8.2

Released on Sunday, October 25 2026.

- Improved the layout of pages with many inline SVGs: how the page styles an inline SVG is resolved once per layout pass instead of every time the SVG is measured or laid out
- Improved the layout of deep pages: the cascaded declarations of an element are taken from the render tree instead of running the cascade for the element and all of its ancestors
- Improved the layout of pages with large `style` elements: the rules declaring `-webkit-line-clamp` and `overflow-wrap` are collected once per layout pass instead of once per element
- Improved painting pages with running CSS animations: the animations of an element and their keyframes are looked up once per frame instead of once per animatable property (each running the element's cascade and searching all style sheets)
- Improved the layout of grids and flex containers: the grid templates are taken from the style map instead of running the cascade again, and the cross size of a flex item is estimated once
- Fixed percentage heights resolving against the containing block's width: they now refer to its height when it is definite (the viewport for the root element), and are `auto` otherwise
- Fixed a row flex container laid out as an item of a column flex container taking its width as its height
- Fixed a column flex container being at least as tall as it is wide, and using the height it gets from its own flex container
- Fixed flex items with an authored `height: auto` (or `fit-content`) being treated as having a definite height
- Fixed children of `display: contents` elements not becoming flex and grid items
- Fixed `<button>` elements with element content (e.g. icons) showing a placeholder label instead of their content, and flex or grid buttons drawing their label twice
- Fixed grid items spanning several rows growing only the first one, and items spanning a flexible (`fr`) row growing the other rows
- Fixed grid items placed by `grid-area` not sizing content-sized tracks
- Fixed cascaded values using `var()` (e.g. `grid-template-columns: var(--columns)`) being used without substitution
- Fixed inline SVGs styled by the page with `var()` (e.g. `fill: var(--text)`) losing those styles
- Fixed `transform` with `var()` being ignored
- Fixed `display: none` children taking a flex item or grid cell
- Fixed `auto` grid rows not stretching to a definite grid height (`align-content: normal`), and `align-content` being ignored for grids
- Fixed flex item margins being taken from their main size instead of being added to it, and `auto` margins of flex items not taking the free space (e.g. `margin-right: auto` pushing the following items to the end)
- Fixed the alignment keywords of CSS Box Alignment (`start`, `end`, `left`, `right`, `safe`/`unsafe`, `normal`) being ignored for flex containers
- Fixed text directly inside a flex container having no size as a flex item, which let the following items overlap it and collapsed a flex container holding only text
- Fixed `<button>` elements with generated content (`::before`/`::after`, e.g. icons) not showing it
- Fixed `currentcolor` in computed values (e.g. `background-color: currentcolor`) being ignored instead of using the element's color
- Fixed `letter-spacing` around spaces missing from the content width of text, which made shrink-to-fit boxes (e.g. flex items) too narrow for their text
- Fixed absolutely positioned children of flex containers being laid out as flex items, and a flex container without items not painting its box or keeping its height
- Fixed absolutely positioned boxes with `auto` offsets (e.g. no `top`) being moved to the corner of their containing block instead of staying at their static position
- Fixed SVG filters (e.g. a drop shadow in an SVG icon) making the whole SVG disappear when running with SkiaSharp 3 (e.g. in an Avalonia app), which no longer has `SKImageFilter.CropRect`
- Fixed the content width of a grid with several columns being the one of its widest item instead of the sum of its columns, which let the items of a shrink-to-fit grid overlap (e.g. the label and icons of GitHub's buttons)
- Fixed block-level `<button>` elements (e.g. `display: flex`) filling their container instead of being as wide as their content
- Fixed positioned or z-indexed boxes being laid out after their siblings (in painting order): e.g. a `position: relative; z-index: 1` box moved below the boxes following it, flex items with a `z-index` changed their order, and absolutely positioned boxes lost their static position; they are now laid out in document order and only painted above the in-flow content
- Fixed `backdrop-filter` inside an element with `isolation: isolate` filtering the page behind that element instead of its content (e.g. GitHub's header buttons showed white): isolation only opens a group when something inside blends
- Fixed items of a column flex container that are not stretched (e.g. `align-items: center`) having no width instead of being as wide as their content, which hid GitHub's repository tabs
- Fixed `text-overflow: ellipsis` cutting text that fits its shrink-to-fit box, as the widths of both differed by a rounding error (e.g. the repository name in GitHub's breadcrumb)
- Fixed `clip-path` shapes being placed at the top left of the page instead of at their box, which clipped away boxes that were not there (e.g. GitHub's loading placeholders)
- Fixed a no-break space being collapsed and trimmed like white space, so a block holding only `&nbsp;` had no height
- Fixed linear gradients spanning the diagonal of their box instead of their gradient line (e.g. the height of the box for `to bottom`), which stretched them and cut their ends
- Fixed gradient stops with `calc()` (e.g. `calc(100% - 2rem)`) or `em`/`rem` positions being placed at the start, stops without a position not being spread between their neighbours, and positions before the previous one not being moved up to it
- Fixed gradients to a transparent color (e.g. `#0000`, `transparent`) passing through grey instead of fading out, as colors were not interpolated premultiplied by alpha (e.g. MDN's sidebar header)
- Added synthetic bold for faces lighter than the requested weight (`font-synthesis: weight`), e.g. variable web fonts: the glyph outlines are stroked by 1/16 em, so it is heavier than the regular text on every platform (Skia's fake bold came out lighter than CoreText's regular rendering on macOS)

# 0.8.1

Released on Friday, October 23 2026.

- Updated `WithRendering()` to also register an image resource service, which keeps loaded `<img>` data for painting (and gives images their natural size)
- Updated the required AngleSharp version to 1.8.5 and AngleSharp.Css version to 1.2.1
- Updated the page layout: `<html>` and `<body>` are laid out as ordinary boxes, so `<body>`'s margin (8px from the default stylesheet), padding, border, width and `max-width` apply; the root's background (or `<body>`'s, when the root has none) paints the canvas, and their `overflow` applies to the viewport
- Fixed a stack overflow when laying out a floated or absolutely positioned inline-level box (`<span style="float: left">`), which is now blockified
- Fixed painting failing with `ObjectDisposedException` for documents with loaded `<img>` elements
- Fixed every paint re-running the cascade per element and property once a DOM harness exists; only elements that declare an `animation-name` are looked up
- Fixed form controls showing their attributes instead of their live state (`value`, `checked`, `selected`, and a `<textarea>`'s value)
- Fixed `width`, `height`, `min-*` and `max-*` in units other than `px` and `%` (`em`, `rem`, `vw`, ...) being ignored
- Fixed top margins collapsing through only one level of first children, and inline-blocks collapsing their margin with their parent's
- Added `placeholder` text for text inputs and `<textarea>`, styled by `::placeholder` (`color: darkgray` from the default stylesheet)
- Added per-character font fallback: characters the resolved font lacks come from the next `font-family` entry that has them, then the bundled fonts, then the installed fonts

# 0.8.0

Released on Wednesday, October 21 2026.

- Improved inline-blocks, floats and absolutely positioned boxes with an auto width, which now shrink to fit their content
- Improved inline backgrounds and borders, which are painted per line fragment
- Improved images, which are now inline boxes sized from their natural size, with the background color painted under background images
- Updated the required AngleSharp.Css version to 1.2.0
- Updated styling to come only from the computed style: removed the tag-name fallbacks for inline display, heading weight, hidden `<script>`/`<style>`/`input[type=hidden]`, `<textarea>` white-space and the form-control look (the missing default-stylesheet rules are reported to AngleSharp.Css)
- Updated `FillRectCommand` to carry an optional `PaintArea` (the box a gradient or image paint is sized in), with a matching `DisplayList.FillRect` overload
- Updated table layout to detect rows and cells by their `display` value instead of tag names
- Updated `RenderGradientPaint` and `RenderImagePaint` to carry optional tiling and repeat modes
- Removed a hard-coded `justify-content` for elements with the `nested` class
- Fixed inline layout: inline content now forms real line boxes, so inline siblings directly in `<body>` share a line, adjacent inline elements are no longer separated by a spurious space, mixed sizes share one baseline, nested inline styles are kept, and `white-space` preservation, `word-break`, `overflow-wrap` and `text-align` apply to mixed inline content
- Fixed `font-weight: bolder`/`lighter` being ignored (so `<b>`, `<strong>` and headings render bold via the default stylesheet)
- Fixed percentage `border-radius` values, which now resolve against the border box
- Fixed `vertical-align` on a box shifting the text inside it
- Fixed `min-width`/`max-width` being ignored for block widths
- Added the border styles `dotted`, `groove`, `ridge`, `inset` and `outset`
- Added correct rounded borders with differing widths, colors or styles (new `FillRingCommand`)
- Added multiple background layers, `background-repeat: space | round`, and sized/positioned/repeated gradient backgrounds
- Added `border-image` (new `DrawBorderImageCommand`)
- Added CSS masks via `mask-image` and its companion properties (new `PushMaskCommand`/`PopMaskCommand`)
- Added `backdrop-filter` (new `DrawBackdropFilterCommand`)
- Added the individual transform properties `translate`, `rotate` and `scale`
- Added `float: right` and `clear`, with floats shared across a block formatting context: text in following blocks wraps around them line by line, and `overflow`/`flow-root` containers enclose their floats
- Added `text-transform` and `word-spacing`
- Added `outline-offset`, `background-clip` and `background-origin` (images and gradients now start inside the border by default, as `background-origin: padding-box` specifies)
- Added `mix-blend-mode`, `isolation` and `background-blend-mode`
- Added intrinsic grid column sizing: `auto`, `min-content`, `max-content`, `fit-content()` and `minmax()` columns are sized from their items' min/max-content widths instead of a character-count estimate, and `auto` columns stretch into leftover space unless `justify-content` packs them
- Added quote generation in `content` (`open-quote`, `close-quote`, `no-open-quote`, `no-close-quote`) with the `quotes` property and language-dependent defaults, and images in `content: url()`, also mixed with text
- Added transitions and animations of `transform`, `translate`, `rotate`, `scale`, `filter` and `backdrop-filter`
- Added `input[type=image]`, the date/time input family (`date`, `time`, `datetime-local`, `month`, `week`) with formatted values and picker icons, and `accent-color`
- Added page CSS for inline SVG (including inheritance from the surrounding HTML) and the remaining SVG filter primitives (`feTurbulence`, `feDiffuseLighting`, `feSpecularLighting`, `feDisplacementMap`, `feConvolveMatrix`, `feTile`, `feImage`, `feBlend`, all `feComponentTransfer` functions, `SourceAlpha`), with filter regions, primitive subregions and `color-interpolation-filters` (linearRGB by default)
- Fixed `feFlood` to fill its whole region instead of recoloring the input
- Added multi-column layout (`column-count`, `column-width`, `column-gap`, `column-rule`)
- Added the Unicode Bidirectional Algorithm (UAX #9) with `unicode-bidi`, replacing the word-order reversal for `direction: rtl`
- Added WOFF and WOFF2 web fonts in `@font-face`
- Added `text-align: justify` and `text-align-last`
- Added soft-hyphen line breaking and `hyphens` (`auto` behaves like `manual`)
- Added `tab-size`
- Added synthesized small capitals for `font-variant: small-caps`/`all-small-caps`
- Added `font-stretch` for installed and `@font-face` fonts (new optional `FontStretch` on `RenderFont`, `DrawTextCommand` and `DrawTextShadowCommand`)
- Added `text-overflow: ellipsis` and `-webkit-line-clamp` for mixed inline content
- Fixed `text-indent` not being inherited by nested blocks
- Updated table layout: the table box (margins, `auto` centering, `clear`, border, padding, background), `border-spacing`, shrink-to-fit auto widths from per-column min/max-content widths, `table-layout: fixed`, cells laid out as real flow content with their own computed borders, padding and backgrounds (no more hard-coded black borders and 4px padding), `vertical-align: baseline` for cells, row/row-group/column backgrounds, captions with `caption-side`, header and footer groups, `empty-cells`, `visibility: collapse` for rows and columns, percentage row and cell heights, a collapsing border model in which the widest border wins each grid line, and tables inside shrink-to-fit boxes
- Fixed percentage `column-gap`/`row-gap`/`gap` in grid and flex containers being ignored
- Added `writing-mode` (`vertical-rl`, `vertical-lr`, `sideways-rl`, `sideways-lr`) with orthogonal flows in either direction, physical box properties mapped onto the logical ones, and `text-orientation` (CJK upright by default under `mixed`)
- Added `position: sticky` with `bottom`, `left` and `right`, confined to the containing block, and painted over later siblings at any nesting depth
- Added named grid lines (including the implicit lines of `grid-template-areas`), negative line numbers, `justify-self`, start/center/end alignment for grid items, and `fr` rows in grids without a definite height
- Added `transform`, `opacity`, `filter`, `mix-blend-mode`, `clip-path`, masks and `backdrop-filter` on flex, grid and table containers
- Improved flex sizing to follow the spec's flexible length resolution: definite `flex-basis` values are no longer widened to the content, items do not shrink below their content (`min-width: auto`), shrinking is weighted by base size, items frozen at `min`/`max` sizes leave the space to the others, and item heights come from a real layout
- Fixed grid containers growing past their authored height
- Fixed percentage widths of children inflating the intrinsic width of shrink-to-fit boxes and flex items
- Fixed the `gap` shorthand being left out of the intrinsic width of flex containers
- Fixed `visibility: hidden` removing elements from layout: hidden boxes now keep their space and only skip painting, and descendants with `visibility: visible` are painted
- Fixed boxes with a decided width of zero (empty table cells, atomic inlines) losing their height

# 0.7.0

Released on Monday, October 19 2026.

- Improved regression test coverage for 3D CSS transform functions (`rotateX`/`rotateY`/`rotate3d`/`translate3d`/`scale3d`/`matrix3d`) (#16)
- Fixed `repeat(auto-fill/auto-fit)` fill-count resolution to use available space, gap, and track minimum size (#20)
- Fixed a pre-existing inline layout bug where plain, unstyled semantic inline elements (`<b>`, `<span>`, `<strong>`, `<em>`, ...) were misidentified as block-level, preventing them from sharing a line with sibling text (#23)
- Added support for `animation-play-state: paused` (#17)
- Added `aspect-ratio` width-from-height derivation for boxes with an auto width and definite height (#14)
- Added `object-fit` (`fill`/`contain`/`cover`/`none`/`scale-down`) and `object-position` support for replaced elements (#13)
- Added rendering support for `input[type=range]` (track + thumb) and `input[type=file]` (#21)
- Added `clip-path` support for ordinary HTML boxes (`circle()`, `ellipse()`, `inset()`, `polygon()`) (#12)
- Added `<textarea>` caret rendering (#22)
- Added multi-line `text-overflow` via `-webkit-line-clamp` support (#18)
- Added general CSS counters `counter-reset`, `counter-increment`, `counter-set`, and `counter()`/`counters()` (#19)
- Added SVG filter primitives `feFlood`, `feComposite`, `feMorphology`, and `feComponentTransfer` (`linear` sub-functions) (#24)
- Added basic RTL/bidi support via `direction: rtl` (#15)

# 0.6.0

Released on Wednesday, September 16 2026.

- Improved layout performance with caching
- Fixed treatment of negative z-indices in stacking contexts
- Fixed display of border-radius with mixed border colors
- Fixed handling of font weights and unstyled headings
- Added performance evaluation benchmark
- Added full real-world rendering scenarios to the test cases

# 0.5.0

Released on Friday, September 11 2026.

- Updated to use the AngleSharp.Css gradient model (#10)
- Updated minimum required AngleSharp.Css version to be 1.1.2
- Improved rendering of overflow with ellipsis (#11)
- Improved rendering with non-default `box-sizing` value
- Added support for CSS grid track sizing (#7)
- Added support for pseudo elements and `content` declaration (#8)
- Added support for `position: sticky` (#9)

# 0.4.0

Released on Thursday, September 10 2026.

- Improved support for clipping and `overflow` declarations
- Improved whitespace handling for rendering
- Fixed painting of scrolled content
- Fixed handling of background images
- Added support for rounded corners
- Added support for transformations and filters
- Added transitions and animations with a virtual clock
- Added support for `text-shadow` declarations
- Added support for `box-shadow` declarations
- Added support for numeric sorted lists (e.g., using `ol`)
- Added support for bullet point lists (e.g., using `ul`)
- Added basic rendering of standard form controls

# 0.3.0

Released on Monday, September 7 2026.

- Improved public API with `GetDomHarness`
- Fixed HTML table rendering (vertical alignment, span, ...)
- Fixed issues when rendering gradients
- Added support for gradients
- Added rendering images
- Added flexbox layout mode
- Added grid layout mode
- Added support for SVG (images, inline, ...)
- Added CSSOM View specification

# 0.2.0

Released on Friday, July 31 2026.

- Added more drawing modes such as tables, floats, ...
- Added `WithRendering` extension to register rendering service

# 0.1.0

Released on Tuesday, July 28 2026.

- Initial release
