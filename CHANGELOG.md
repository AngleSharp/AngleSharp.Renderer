# 0.6.0

Released on ?

- Added performance evaluation benchmark
- Improved layout performance by caching each element's computed style and explicit-declaration lookups per render, and skipping grid/flex-container-only property reads on elements that cannot need them (~9.5x faster, ~7.5x less allocation - see `docs/benchmarks.md`)

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
