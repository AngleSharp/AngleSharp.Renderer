# 0.7.0

Released on ?.

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
