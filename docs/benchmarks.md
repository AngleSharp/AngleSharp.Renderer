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

Two fixes landed, both eliminating redundant work rather than changing what gets rendered:

1. Element style is now cached per render (`GetOrCreateStyleMap`), instead of being recomputed from
   scratch every time a flex/grid item, inline-block prediction, or paint-order check reads the
   same element's style ahead of its own real layout pass.
2. The handful of properties read from the cascaded-but-uncomputed declaration (grid tracks, gap,
   `background-image` - see `ResolveExplicitPropertyValue`'s own remarks) used to rebuild the
   *entire document's* style collection and re-walk the element's full ancestor chain on every one
   of up to 8 calls per element; both are now built/read once per element instead.

Combined, layout allocates ~7.25x less and runs ~9x faster than the original (0.5.0) baseline; full
render time (layout + rasterization + PNG encoding) follows closely behind, since rasterization/
encoding was always a small, roughly fixed slice of total time.

┌───────────────────────────┬────────┬───────────┐
│           Stage           │  Mean  │ Allocated │
├───────────────────────────┼────────┼───────────┤
│ Parse HTML+CSS            │ 1.5 ms │   1.96 MB │
├───────────────────────────┼────────┼───────────┤
│ Layout (BuildDisplayList) │ 0.27 s │    201 MB │
├───────────────────────────┼────────┼───────────┤
│ Full render (RenderToPng) │ 0.49 s │    206 MB │
└───────────────────────────┴────────┴───────────┘
