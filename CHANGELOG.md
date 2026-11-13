# 0.11.3

Released on Friday, November 13 2026.

- Improved deeply nested pages: about 70% more levels are laid out before the stack runs low, e.g. 72 instead of 42 nested blocks on a 1.5 MB stack (#58) @michaelthyregod
- Fixed `getContext("2d")` giving a new context with an empty bitmap on every call, and `fill()` only drawing the outline of a path
- Fixed a canvas not showing what was drawn into it: with scripting it is a replaced element painting its bitmap (as it is when the frame is rasterized, so drawing needs no layout), instead of its fallback content (e.g. "Your browser does not support the canvas element")
- Fixed a CSS transition started by a change confined to an out-of-flow box (e.g. a fixed side menu sliding in with `transform`) staying at its start value, as the patched frame did not ask for the frames to come; it also starts with the next frame, not at the time of the last full one
- Fixed a float following text on a line (e.g. a close button with `float: right` at the end of a title) being placed below the line instead of on it when it fits there
- Fixed an absolutely positioned box with `top` and `bottom`, `margin: auto` and a `max-height` (e.g. a dialog with `inset: 0`) not being centered vertically, as the free space was taken before the height was limited
- Fixed `repeat(auto-fit, ...)` keeping one track per grid item, which wrapped items spanning several columns (e.g., a dashboard of tiles with `grid-column: span 2`); it keeps the columns the items span
- Fixed implicit grid rows growing for items spanning several rows in document order, which made a tall item push later ones down; items spanning fewer rows come first, as in CSS Grid
- Fixed a font family declared as several `@font-face` rules split by `unicode-range` (as Google Fonts does) drawing with the first rule's face, which lacked the letters of the text, so the text fell back to the next family; the face whose range covers a character draws it
- Fixed a grid container grown by its flex container (e.g. `flex: 1` in a column) keeping the height of its content, so its auto rows did not take the rest of the height
- Fixed the intrinsic widths of a multi-column container ignoring its columns, so a shrink-to-fit box with `column-width` (e.g. a menu in a row flexbox) was only as wide as its content; it is at least `column-width` per column, with the gaps between columns
- Fixed relatively positioned boxes painting below earlier absolutely positioned siblings: all positioned boxes with `z-index: auto` paint above the in-flow content in tree order (CSS 2.2 Appendix E)
- Fixed an image with a percentage `width` or `max-width` (e.g., `width: 100%`) widening a `1fr` grid track or other intrinsic size to its natural width: its min-content contribution is zero (compressible replaced elements, CSS Sizing 3)
- Fixed `justify-content` (e.g., `place-content: center`) not placing the column of a grid without `grid-template-columns`, which always filled the container
- Fixed a frame painted while an image's response was still being processed taking its stream, which left the image without its natural size (it then was laid out as a broken image)
- Added the drawing API of `CanvasRenderingContext2D` for script (paths with arcs and curves, `fill`/`stroke`/`clip`, transformations, `globalAlpha`, composite operations, shadows, gradients, patterns, `fillText`/`strokeText`/`measureText` with the document's web fonts, `drawImage` of images and canvases, line styles), which before only exposed `save`/`restore` and the size

# 0.11.2

Released on Thursday, November 12 2026.

- Improved deeply nested pages: what is nested too deep for the stack is left out instead of ending the process with a stack overflow (#55) @michaelthyregod
- Improved the performance of nested tables and inline-blocks, which were laid out twice as often with every level (#54) @michaelthyregod
- Improved performance with partial layouting due to an off-screen lookahead which avoids full style-map creation
- Improved the first frame of pages with long flex containers: the lines of a wrapping row and the items of a column that start below the viewport are not laid out
- Improved the performance of flex rows: an item with a definite width only measures its content when its line has to shrink
- Improved the garbage collection while building style maps (about 20% less allocated per frame)
- Fixed adjacent text nodes (e.g. inserted by a script or a framework like Blazor) each starting a line of their own in a block, table cell or flex item: they are laid out as one text, like the single node the parser makes of the same text
- Fixed the intrinsic width of a row flex container counting each of adjacent text nodes as an item of its own (with a gap between them)

# 0.11.1

Released on Sunday, November 8 2026.

- Updated to use a minimum of AngleSharp.Css 1.4.1
- Improved the performance of layouts, e.g., text fields with a placeholder computed the styles of all their ancestors whenever they were painted
- Improved the performance of nested flex and grid containers, whose items were laid out (with everything in them) also when only the height of a box around them was measured
- Improved the garbage collection during layouts: the selectors of the style sheets are only looked at once, equal values of style maps are shared, and transitions and animations are only looked up while any run
- Improved the performance of large documents by reading the computed styles of their elements on all cores (turned off by the AppContext switch `AngleSharp.Renderer.DisableParallelism`)
- Improved hovering and focusing: only what a selector of the state refers to is styled again, a change of its colors, backgrounds, borders, outlines, shadows or text decoration is painted again without a layout, and the frame keeps its hit-test map unless the cursor changed
- Improved updating a frame after a change of the page: flex and grid items, the content of floats and sticky boxes, and blocks after floats that ended above them are laid out on their own as long as they look the same to what is around them, with the rules of the last layout instead of gathering them again; typing into a text field only lays out more if a selector depends on what it changes
- Improved rendering an image of a page (e.g. `RenderToPng`): only the elements laid out in the viewport are styled, unless CSS counters or quotes are used, and the rules of the style sheets are gathered and indexed once per layout instead of twice
- Fixed updating a frame after a change in a box whose size depends on its content (e.g. a tab in a shrink-to-fit bar), which kept the size from before the change instead of what a layout of the page gives
- Fixed the line of text reaching into the bottom of the viewport not being painted in an image of a page (e.g. `RenderToPng`)
- Fixed an inline box going over several lines (e.g. a wrapped link) being hit in the whole rectangle around its lines, covering the links next to it, and `getClientRects` giving that rectangle instead of one per line
- Fixed the `::placeholder` of text fields in a shadow tree ignoring the rules of the shadow root
- Fixed a CSS animation that an element gets after its document's first frame (e.g. by a class a script adds) being over already instead of starting then, and one named again after it was removed not starting again
- Fixed the text nodes next to each other in a flex container being one flex item each, losing the spaces between them (e.g. `Sign in with {provider}` from a template)
- Fixed the `min-width` and `max-width` of a flex item in a column being ignored with a `width` (e.g. `width: 100%; max-width: 28rem`), which made the column too short for its content
- Fixed a button of an automatic height and a `min-height` not centering its label in it
- Fixed an absolutely positioned box placed by its `bottom` in a block of an automatic height and a `min-height` being placed against the `min-height` instead of the block's height, and one of an automatic height and a `min-height` being placed with its `min-height` as height
- Fixed selecting the text of a flex item made of several text nodes, which only reached the first one
- Fixed SVGs being blurred or jagged when scaled (e.g. an 800px icon in a 16px box), in an `<img>`, inline, as a background, mask or `border-image`: they are painted at the resolution they end up at, as in browsers
- Fixed the slices of an SVG `border-image` counting in the pixels of its oversampled raster instead of its natural size
- Improved the performance and memory use of SVGs: their PNG (`RenderedImage.Data`) is only rendered when it is read, and repainting reuses what was rendered for the same size
- Added `NotifyStateChanged` to the DOM harness, for telling the renderer about a change of an element's state it does not see (e.g., the focus)
- Added `PrepareViewportFrame` to the DOM harness, for a frame of a page that was just loaded early on: only what is in the viewport is styled and laid out, and the frame tells by `RenderedFrame.IsPartial` (it is animating) that the next frame lays out the whole page
- Added `RenderedFrame.Metrics` telling what the layout of a frame took (`RenderMetrics`: the time of each phase and how much work they did)
- Added the events of CSS animations (`animationstart`, `animationiteration`, `animationend` and `animationcancel`, as `AnimationEvent`), dispatched once the frame that found them is painted
- Added CSS transitions started by any change of an element's style between two frames (e.g. a class a script adds), not only by hovering, and their events (`transitionrun`, `transitionstart`, `transitionend` and `transitioncancel`, as `TransitionEvent`); a property that is not interpolated still has the events, and shorthands like `padding` transition their longhands

# 0.11.0

Released on Friday, November 6 2026.

- Updated resolution of used pixel values for margins and positioned insets in `getComputedStyle`.
- Fixed `width`, margins and other lengths mixing percentages and lengths, e.g. `calc(50% + 10px)` or `min(240px, 55%)`, being ignored, and math of more than two terms being taken as 0 in grid tracks
- Fixed `scrollHeight` and `scrollWidth` not counting a positioned box escaping a clip for the page, and counting a positioned box for a box that isn't its containing block
- Fixed `ContentHeight` of frames counting what a box clips (e.g. the content of a scroll container), making pages scroll though all of them fits the viewport
- Added `elementFromPoint` and `elementsFromPoint` to documents, and `pageX`, `pageY`, `offsetX` and `offsetY` to mouse events
- Added the used values of `width`, `height`, paddings, border widths and inherited text properties to what `getComputedStyle` gives script
- Added text selection to interactive documents: selecting with the pointer through the harness (`BeginTextSelection`, `ExtendTextSelection`, `EndTextSelection`, also by word and paragraph), `SelectAllText`, `SelectedText`, the setting `IsTextSelectionEnabled`, the DOM's `getSelection()` with `selectstart` and `selectionchange`, `user-select`, and `::selection` as soon as AngleSharp and AngleSharp.Css know it
- Added `ResizeObserver`
- Added `IntersectionObserver`, by default comparing with the viewport of the top-level page, also from the frames in it
- Added `Range.getClientRects()` and `Range.getBoundingClientRect()`.
- Added `element.animate()` and related script-facing Web Animations APIs on the renderer's virtual clock.
- Added `document.fonts` and script-created `FontFace` objects to font matching and layout.

# 0.10.0

Released on Wednesday, November 4 2026.

- Updated to AngleSharp.Css 1.3.2, whose default style gives form controls the font, padding, margins and `box-sizing` of browsers
- Fixed the label of a native select touching its border now that its default padding is 0
- Fixed synthesized small caps: digits, punctuation and spaces keep the full size with `small-caps` and are reduced with `all-small-caps`, `unicase` reduces everything but lowercase letters instead of acting as `small-caps`, `ß` and Latin ligatures become their capitals, letter spacing applies where the size changes, and a decoration crosses spaces in one size
- Fixed spaces between words of a fallback font being as wide as that font's space instead of the first font's
- Fixed the background and border of inline boxes around right-to-left text starting at the left of the page, and the left and right padding, border and margin of such boxes changing places
- Fixed `letter-spacing` after the last character missing from the width of text, making boxes sized by their text too narrow and aligned text off by that much
- Fixed kerning and letter spacing across spaces missing from line breaking, intrinsic widths and inline positions, so a line could wrap though it fit, or overflow, and what followed a text run could be off by a fraction of a pixel
- Fixed `min-content`, `max-content` and `fit-content` being ignored as the `width` of flex and grid items, and as `flex-basis`, `min-width` and `max-width` of flex items in a row
- Fixed `white-space: pre-wrap` and `break-spaces` collapsing repeated spaces and indentation of bare text, and dropping lines of only spaces
- Added `text-decoration-line: overline` and `text-decoration-thickness`
- Fixed text decorations taking the color of nested elements (or the style of the box around them) instead of the box declaring the lines
- Fixed collapsed table borders of equal width using the color of the cell further down or right instead of further up or left, and vertical lines being painted over horizontal ones
- Fixed square borders with differently colored edges not meeting on the corner diagonals (e.g. triangles from `border-color: red transparent transparent`)
- Fixed absolutely positioned boxes being placed against their parent instead of the nearest positioned ancestor (positioned or transformed blocks, flex and grid containers, tables and inline boxes) or the viewport, waiting for the height of that one if it is auto
- Fixed percentages of absolutely positioned and fixed boxes referring to their parent instead of their containing block, and auto margins not centering them between their offsets
- Fixed the root element collapsing its margins with its first child
- Fixed the margins of an empty block adding up instead of collapsing through it, with those of the blocks around it and of its parent
- Fixed the margins of flex items on the cross axis: they count for the size of the line, stretching and alignment, auto ones take the free space (in rows, too), and percentages refer to the container; lines of a multi-line container share the free space of `align-content: stretch`
- Fixed absolutely positioned boxes without offsets after text: an inline-level one is where it would be on the line, a block-level one below the line
- Fixed boxes that are not the containing block of an absolutely positioned or fixed box clipping it, when painting and hit testing
- Fixed `getBoundingClientRect` and `offsetHeight` of the root element reporting the viewport instead of its own box
- Fixed boxes in columns after the first being measured and hit tested where they would be in one column, and boxes across columns having no fragments in `getClientRects`
- Fixed the list `getClientRects` returns not being indexable from scripts
- Fixed boxes of no width dropping what is in them, instead of wrapping its text and laying out what is out of the flow
- Fixed absolutely positioned boxes with an explicit `top: auto` or `left: auto` (e.g. `inset: auto 0 0`) ignoring their `bottom` or `right` offset
- Fixed the bottom margin of the last child missing from the auto height of a box that doesn't collapse with it (formatting contexts, bottom padding or border), negative margins included
- Fixed out-of-flow boxes (e.g. `sr-only` headings) separating the margins of the blocks around them, and taking the top margin suppression from the first in-flow child
- Fixed auto margins of column flex items with padding moving them out of the container, and made them keep the item from stretching
- Fixed `align-self` being ignored for stretching flex items when `align-items` is `stretch`
- Fixed form controls of auto height (or auto width in a column) not being stretched as flex items
- Added `progress` and `meter`, list boxes (`select` with `multiple` or `size`, option groups included, scrolling to their selected option) with `ListBoxExtensions` for hosts to make them interactive, and the name of the chosen file next to the button of a file input
- Added `SelectPopup` and `RenderedFrame.WithOverlay` for hosts to show the list of a dropdown on top of a frame, using the color scheme of the select (its `color-scheme`, or the `color-scheme` meta element, and the preference) and the colors of the select and its options
- Added color emoji: characters presented as emoji use the color font of the host (or a declared web font) before fonts having a monochrome glyph, and stay together with their selectors, skin tones and joined characters
- Fixed `line-height: normal` being 1.35 for every font instead of the line spacing the font states, and inheriting an explicit line height of the parent
- Fixed `caretPositionFromPoint` guessing character widths, ignoring wrapped lines and collapsed white space, and being as tall as the line (a length made it taller than that by the font size): it now measures the text and spans the font
- Fixed buttons having a minimum width of 20px, and empty ones a line
- Fixed selects being as wide as the selected option instead of the widest, not showing options in groups, and `appearance: none` still reserving and painting the arrow; a native one ignores `line-height`
- Fixed the sizes of checkboxes, radio buttons, color, range, file and date/time inputs to the ones of browsers, whatever the font
- Fixed the baselines of controls without text
- Fixed lengths and percentages of `vertical-align` being ignored for atomic inlines
- Fixed text fields to be sized by their font and `size`, and text areas by `cols`, `rows` and room for their scrollbar, instead of a fixed width
- Fixed `width: 100%` text fields contributing no width to a box sized by its content
- Fixed `vertical-align: text-top` and `text-bottom` of atomic inlines, which were aligned with the baseline and grew the line
- Fixed auto grid rows not filling the `min-height` of the grid container
- Fixed grid tracks in `em`, `rem`, viewport units or `calc()` being sized as zero, and `calc()` mixing percentages and lengths in tracks and gaps
- Fixed `column-gap` or `row-gap` alone also applying to the other axis
- Fixed `minmax(<length>, 1fr)` adding its floor to its share of the space, and `fr` values summing to less than 1 taking all of it
- Fixed floated tables and floated flex or grid containers not being floats: they stayed in the flow, and text did not wrap around them
- Fixed floats resetting the margins around them, which collapse with each other as if the float was not there
- Fixed `clear` ignoring the margin that already takes a box below the floats, adding the margin of the box below them, and moving what follows a float with `clear`
- Fixed `vertical-align` of inline boxes: `super` and `sub` were far off and not inherited by the content of the box, lengths moved in the wrong direction, percentages, `top` and `bottom` were ignored, and lines did not grow for what is shifted out of them
- Fixed `position: relative` being ignored on inline boxes
- Fixed lines using the metrics of the bundled font for every font, and a line height smaller than the font moving the text down instead of letting it overflow
- Fixed the size of inline boxes, which is the ascent and descent of their font
- Fixed the baseline of `inline-flex` and `inline-grid` to be the one of the first item
- Fixed background and mask images in data URLs containing quotes (`<svg xmlns="...">`) not loading
- Fixed `Times` and `Times New Roman` not resolving to the metric-compatible fonts on Linux
- Fixed table rows and row groups having no box for `getBoundingClientRect()`
- Fixed `text-align: -webkit-center` and its kin not moving the block-level boxes inside (`<center>`, `<div align>`, `<td align>`), which tables don't inherit
- Added the quirks mode of documents without a doctype: lines only get their height from text, percentage heights look past auto heights, and the height of a table cell includes its padding
- Fixed a `width` on a table cell growing a table with a width of its own, and left over table width going to columns that ask for a width
- Fixed cells with a height not honoring `vertical-align` (`middle`, the default, and `bottom`)
- Fixed the bottom margin of a block in an inline (`<a><div>`) getting lost
- Fixed `getBoundingClientRect()`, `getClientRects()` and the hit test map ignoring transforms
- Fixed `position: absolute` and `fixed` tables staying in the flow
- Fixed plain `fr` tracks getting narrower than their content (the automatic minimum size), and grids reporting overflowing tracks as their width
- Fixed `min()`, `max()` and `clamp()` with percentages in grid tracks
- Fixed `width: min-content`, `max-content` and `fit-content` being ignored
- Fixed shrink-to-fit widths not counting a float next to inline content
- Fixed a relatively positioned inline box painting below the rest of its line
- Fixed the box of a table for scripts not holding its captions
- Fixed broken images showing nothing: inline ones show their `alt` text, and ones with a size of their own are marked
- Added iframes: their documents are laid out with a viewport of their own and painted in their box, hit testing reaches their elements, and changes in them repaint the page

# 0.9.2

Released on Sunday, November 1 2026.

- Improved geometry reads after a change confined to an out-of-flow box (e.g. a script showing a hidden dropdown with `position: absolute`, measuring it, and hiding it again): only that box is laid out again, not the whole document (dev.to's scroll handler measuring its 60 dropdowns went from 64 s to under 3 s)
- Improved geometry reads to keep the layout when the page scrolls or the pointer moves without changing the hovered element, and `scrollTop`/`scrollLeft`/`scrollY` to not lay out the document
- Improved `IDomHarness.UpdateFrame` to show hidden absolutely positioned children of flex and grid containers without a full layout, too
- Improved geometry reads after a frame of an interactive document, which are answered from the frame's layout instead of laying out the document again
- Improved geometry reads and `IDomHarness.UpdateFrame` after changes of in-flow blocks (e.g. the text of a title, the color of an article): the block is laid out again on its own when it stays the same from outside (its box, its intrinsic widths where a flex or grid item is sized by them, its margins, no floats beside the boxes after it), else the next blocks around it are tried before a full layout (dev.to: 34ms instead of 1.2s for a changed title)
- Improved laying out a box on its own to reuse what the layout resolved from the page (e.g. the page styles of inline SVG icons, which built a style collection of their own every time)
- Improved changes of the content of an element on pages with `:has()` selectors, which needed a full layout; only a change flipping the match of a `:has()` on the element or around it does now
- Fixed `getBoundingClientRect`, `getClientRects`, and `caretPositionFromPoint` ignoring the scroll position of the page; boxes in fixed boxes stay, and sticky boxes stick for it
- Fixed the content of a stuck sticky box (e.g. the links of a sticky header) being hit tested and measured where it would be without sticking
- Fixed geometry reads measuring text with another measurer than painting (e.g. images in a line off by a tenth of a pixel)
- Fixed `right` and `bottom` of fixed boxes being ignored (e.g. a button fixed at the bottom right was placed at the top left)
- Fixed absolutely positioned and fixed boxes of an auto height placed by `bottom` staying at the top; they are measured first
- Fixed scroll containers other than the page (`overflow: auto`, `scroll`, `hidden`) not moving their content when scrolled, in painting, hit testing, and geometry (`getBoundingClientRect`, `scrollIntoView`)
- Fixed a box laid out on its own inside a scrolled container (other than the page) being measured where it would be unscrolled
- Fixed changes of style sheets (the text of a `style` element, adding or removing `style` and `link` elements) inside a box out of the flow being taken for changes of that box only
- Fixed content with a negative `z-index` inside a scroll container (or any box clipping its overflow) being neither clipped nor scrolled by it
- Fixed absolutely positioned children of grid containers being laid out as grid items (taking a cell and sizing the tracks); they are placed against the grid's padding box, or the grid area of their `grid-column`/`grid-row`/`grid-area` lines (CSS Grid 1 §9.1)
- Fixed grid items with a `z-index` painting in document order
- Fixed `scrollHeight` and `scrollWidth` counting the content clipped by scroll containers inside, and boxes in fixed boxes
- Fixed the text of an absolutely positioned box moving the baseline of its inline-block (e.g. a dropdown inside an inline-block pushing the following lines down)
- Fixed checkboxes, radios and other form controls as flex items in a row (e.g. a checkbox beside its label in a `display: flex` label) getting no width, so they were not painted
- Added `IDomHarness.UpdateFrame`, bringing the last frame up to date without a layout of the whole document when the changes since were confined to boxes out of the flow: their commands are replaced, removed, or inserted where a box shown since paints (e.g. on dev.to showing a profile dropdown or changing the fixed header takes about 50 ms instead of 1.1 s, with the same pixels), and a frame stays as it is when they were not rendered then and are not now (e.g. a dropdown shown, measured and hidden again)

# 0.9.1

Released on Friday, October 30 2026.

- Improved performance by caching fonts and using direct pixel manipulation
- Improved layout of a single-line column without a definite height (e.g. `body { display: flex; flex-direction: column }`): its items of an automatic height are measured by their actual layout instead of a trial layout of their whole subtree beforehand (dev.to's frames took 26% less time)
- Improved preparing a render, which resolved the body's text style through a new style collection for every explicit property, and let inline SVGs share the render's style collection
- Improved geometry reads (e.g. `getBoundingClientRect`, `offsetTop`, `scrollHeight`) to reuse the layout until the document, scroll, hover, viewport, or a frame changes it, instead of laying out the whole document on every read
- Improved grid layout to measure an item once per render, which nested grids measured again for every pass of the outer grid
- Fixed the items of a column without a definite height ignoring its `min-height` and `max-height`: they now grow into the space a `min-height` leaves (e.g. a `min-height: 100vh` page whose main column has `flex-grow: 1`), are justified within it, and shrink to a `max-height`
- Fixed form controls (e.g. text fields) losing their padding and border to their content under `box-sizing: border-box`
- Fixed the content of a button taller than its content (e.g. an icon button stretched by `inset`) sticking to its top instead of being centered vertically
- Fixed grid items ignoring their margins: `auto` margins now center an item (e.g. `margin: 0 auto; width: 640px`), other margins shrink a stretched item and take space in its row
- Fixed `grid-gap`, `grid-row-gap`, and `grid-column-gap` (the legacy names of the gaps) being ignored
- Fixed a grid collapsing its bottom margin with the margin of the box before it, and a box with a formatting context of its own collapsing its bottom margin with its last child
- Fixed absolutely positioned children of a block of an automatic height (e.g. `top: 50%` or `bottom: 0`) being placed against its width instead of its final height
- Fixed the white space at the start and end of a line counting toward shrink-to-fit widths (e.g. a link as a flex item ending a few pixels short of its container's edge)
- Fixed checkboxes and radio buttons with `appearance: none` still getting the native check painted over their own background
- Fixed SVG background images of an automatic size being drawn three times their size (at the size they were rasterized at)
- Fixed border and outline widths not being snapped to device pixels (e.g. a `1.5px` border is 1px wide in browsers, which made dev.to's text fields a pixel taller)
- Fixed `vertical-align: middle` assuming an x-height of half the font size instead of the font's own (`ITextMeasurer.MeasureXHeight`, which defaults to half the font size)
- Added the CSSOM View members of elements and documents to scripts (they lacked `DomExposed`), with properties like `offsetTop` exposed as properties
- Added `scrollX`, `scrollY`, `pageXOffset`, `pageYOffset`, `scrollTo`, `scroll`, and `scrollBy` to the window

# 0.9.0

Released on Wednesday, October 28 2026.

- Fixed `-webkit-line-clamp` counting lines per inline run instead of across the nested blocks of the clamped box (e.g. paragraphs in a clamped card), which now ends after its last line
- Fixed gaps with font- or viewport-relative units (e.g. Tailwind's `gap-4`, i.e. `gap: 1rem`) being ignored in grid and flex containers
- Fixed images (and other replaced elements) with `border-radius` not being clipped to its curve (e.g. a `rounded-full` avatar painted square)
- Fixed `line-height` lengths (e.g. `16px`, or `1rem` as in Tailwind's `text-xs`) being ignored in favor of the inherited (or default) line height
- Fixed inline-level flex items and non-stretched grid items (e.g. a `<span>`) being as tall as their inline box instead of their line boxes, which let text overflow them at the bottom (e.g. tag pills with padding)
- Fixed words breaking after a hyphen or slash in a box sized to fit them (e.g. a flex item with the max-content width of `c-sharp`), as the width came out of the box arithmetic a fraction of a pixel short
- Added grid track alignment: `justify-content` and `align-content` (`start`, `end`, `center`, `space-between`, `space-around`, `space-evenly`, with `safe`) position the tracks of a grid whose tracks do not fill it
- Added lines moving down past floats: a line whose first word does not fit beside the floats moves down to where one of them ends, until it fits or no float is beside it
- Added `writing-mode` on flex, grid and table containers: a container whose writing mode differs from its parent's lays out its items along its own axes (e.g. a `vertical-rl` flex container runs its main axis downwards)
- Added nested writing modes in the same orientation: a `vertical-lr` element in `vertical-rl` content (or the reverse) stacks its blocks in its own direction
- Added `position: sticky` inside orthogonal flows (e.g. in a `vertical-rl` box), which used to stay static
- Added anonymous table objects: stray `display: table-cell` boxes (e.g. `<div style="display:table-cell">` used for layout) form a row of an anonymous table, rows outside a table get one, and text or other content directly inside a table or row is wrapped in an anonymous row or cell instead of being dropped
- Added percentage table heights (e.g. `table { height: 50% }`), resolved against a definite containing block height
- Added `column-span: all` in multi-column containers: the spanner is laid out across all columns, and the content on each side of it is balanced separately
- Added text shaping with HarfBuzz (via `SkiaSharp.HarfBuzz`): kerning and ligatures (e.g. `fi`, `fl`), Arabic joining and complex scripts such as Devanagari, measured and painted from the same shaped glyphs, so line breaking, alignment and table widths use the shaped advances
- Added `font-feature-settings`, `font-kerning`, `font-variant-ligatures` and `font-variant-numeric` (e.g. `font-feature-settings: "liga" 0` turns ligatures off, `tabular-nums` selects tabular figures); a non-zero `letter-spacing` turns optional ligatures off
- Added Noto Sans Devanagari as a bundled fallback font (its OFL license is included in the package), so Devanagari renders without an installed font
- Changed right-to-left text in the display list: `DrawTextCommand.Text` is now in logical order with `IsRightToLeft` set, and shaping reverses and mirrors it (it used to be reversed and mirrored by layout)
- Changed `text-overflow: ellipsis` to cut at glyph clusters (`ITextMeasurer.GetClusterBoundaries`, which defaults to grapheme clusters for custom measurers), so it never splits a ligature or conjunct
- Improved SVG `text`/`tspan`, which is shaped the same way as HTML text, reordered by the bidi algorithm with `direction` as its paragraph direction (which also decides where `text-anchor: start` and `end` are), and in its `lang`
- Added `lang` to text shaping, so fonts with language-specific forms use them (e.g. the Marathi forms of Noto Sans Devanagari for `lang="mr"`)
- Added joining across element boundaries: an Arabic word split by an inline element (e.g. one letter in `<b>`) keeps its joined forms
- Changed Canvas 2D `fillText` to use the context's `font` (family, size, weight, style) with the same fonts, shaping and bidi reordering as HTML text instead of the host's default typeface
- Added fragmentation in multi-column containers: `break-before`/`break-after` (`column` forces a new column, `avoid`), `break-inside: avoid`, `orphans`/`widows` (lines of a paragraph are no longer left alone at the top or bottom of a column), `column-fill: auto|balance` with a definite height (balanced by default), and blocks without lines splitting across columns

# 0.8.2

Released on Sunday, October 25 2026.

- Improved painting animated elements and every layout: the animations are read from the computed style the renderer already has instead of running the cascade again, and the style collection (only needed for e.g. `::placeholder`) is only built when used
- Improved the layout of pages with many inline SVGs: how the page styles an inline SVG is resolved once per layout pass instead of every time the SVG is measured or laid out
- Improved the layout of deep pages: the cascaded declarations of an element are taken from the render tree instead of running the cascade for the element and all of its ancestors
- Improved the layout of pages with large `style` elements: the rules declaring `-webkit-line-clamp` and `overflow-wrap` are collected once per layout pass instead of once per element
- Improved painting pages with running CSS animations: the animations of an element and their keyframes are looked up once per frame instead of once per animatable property (each running the element's cascade and searching all style sheets)
- Improved the layout of grids and flex containers: the grid templates are taken from the style map instead of running the cascade again, and the cross size of a flex item is estimated once
- Fixed absolutely positioned boxes with `top` and `bottom` (e.g. `inset: 0`) and an auto height having no height instead of filling the space between them, the margins of positioned boxes not offsetting them, and percentages of `top` referring to the width of the containing block
- Fixed an empty `<button>` (or `<input type=button>` without a value) showing the placeholder label "Button" instead of nothing (e.g. an icon button styled by its background)
- Fixed background images and web fonts being requested with the `Accept` header of a document, which some servers refuse (e.g. IIS answers 406 Not Acceptable), and error responses being decoded as the resource
- Fixed overlong words breaking after a slash or hyphen followed by a digit (e.g. the date `8/21/2026`), which UAX #14 does not allow
- Fixed hit testing (hover, clicks, caret positions) taking the most deeply nested element at a point instead of the one painted on top: it now follows the painting order (z-index, positioned boxes above the normal flow, document order), skips content clipped by an ancestor's `overflow`, and ignores `visibility: hidden` and `pointer-events: none` (e.g. an empty panel overflowing onto a navigation took its clicks)
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
