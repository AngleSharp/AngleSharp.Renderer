# Benchmarks

All benchmarks have been run using .NET 10 on an Apple M5 Pro with 24 GB of memory.

## Baseline (0.5.0)

┌───────────────────────────┬────────┬───────────┐
│           Stage           │  Mean  │ Allocated │
├───────────────────────────┼────────┼───────────┤
│ Parse HTML+CSS            │ 1.2 ms │   1.96 MB │
├───────────────────────────┼────────┼───────────┤
│ Layout (BuildDisplayList) │ 2.42 s │  1,456 MB │
├───────────────────────────┼────────┼───────────┤
│ Full render (RenderToPng) │ 2.67 s │  1,461 MB │
└───────────────────────────┴────────┴───────────┘

## Current (0.6.0)

Three fixes landed, all eliminating redundant work rather than changing what gets rendered:

1. Element style is now cached per render (`GetOrCreateStyleMap`), instead of being recomputed from
   scratch every time a flex/grid item, inline-block prediction, or paint-order check reads the
   same element's style ahead of its own real layout pass.
2. The handful of properties read from the cascaded-but-uncomputed declaration (grid tracks, gap,
   `background-image` - see `ResolveExplicitPropertyValue`'s own remarks) used to rebuild the
   *entire document's* style collection and re-walk the element's full ancestor chain on every one
   of up to 8 calls per element; both are now built/read once per element instead.
3. Grid/flex-container-only properties (`grid-template-columns`/`-rows`, `gap` and friends,
   `flex-direction`/`justify-content`/`align-items`/`flex-wrap`/`align-content`) are no longer read
   at all for an element that provably cannot be a grid/flex container itself.

Combined, layout allocates ~7.5x less and runs ~9.5x faster than the original (0.5.0) baseline;
full render time (layout + rasterization + PNG encoding) follows closely behind, since
rasterization/encoding was always a small, roughly fixed slice of total time. See
`src/AngleSharp.Renderer.Benchmarks/BASELINE.md` for the full before/after breakdown of each fix.

┌───────────────────────────┬────────┬───────────┐
│           Stage           │  Mean  │ Allocated │
├───────────────────────────┼────────┼───────────┤
│ Parse HTML+CSS            │ 1.5 ms │   1.96 MB │
├───────────────────────────┼────────┼───────────┤
│ Layout (BuildDisplayList) │ 0.26 s │    194 MB │
├───────────────────────────┼────────┼───────────┤
│ Full render (RenderToPng) │ 0.48 s │    199 MB │
└───────────────────────────┴────────┴───────────┘
