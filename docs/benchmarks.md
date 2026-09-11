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

Element style is now cached per render (`GetOrCreateStyleMap`), instead of being recomputed from
scratch every time a flex/grid item, inline-block prediction, or paint-order check reads the same
element's style ahead of its own real layout pass. Layout allocates ~2.4x less and runs ~2.5x
faster as a result; full render time (layout + rasterization + PNG encoding) follows closely
behind, since Skia rasterization/encoding is only a small fraction of total time.

┌───────────────────────────┬────────┬───────────┐
│           Stage           │  Mean  │ Allocated │
├───────────────────────────┼────────┼───────────┤
│ Parse HTML+CSS            │ 1.2 ms │   2.02 MB │
├───────────────────────────┼────────┼───────────┤
│ Layout (BuildDisplayList) │ 0.98 s │    614 MB │
├───────────────────────────┼────────┼───────────┤
│ Full render (RenderToPng) │ 1.22 s │    619 MB │
└───────────────────────────┴────────┴───────────┘
