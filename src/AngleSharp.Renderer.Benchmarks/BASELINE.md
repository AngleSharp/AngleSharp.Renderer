# Performance Baseline

Recorded 2026-09-11 against a local build of this branch (`AngleSharp` 1.8.1, `AngleSharp.Css`
1.1.2 - both via `PackageReference`, no local `ProjectReference`), using BenchmarkDotNet's `short`
job (3 warmup + 3 measured iterations) rather than its full default job, to keep this initial
baseline run fast; the numbers below are stable (sub-1% StdDev) and consistent between two
independent invocations, so they are trustworthy as a baseline despite the shorter job - see
"Reproducing/updating this baseline" below for a more statistically rigorous run.

Machine: Apple M5 Pro (18 cores), macOS Tahoe 26.6.2, .NET SDK 10.0.201.

Fixture: `BenchmarkFixture.BuildLargePageHtml()` - see that file for the exact markup/CSS - a
single ~1500-element page (160 cards in a wrapping flexbox, a 16-item CSS Grid section, a floated
sidebar with a list, a 40-row table, a form row, a sticky header) mixing linear/radial/conic
gradients, box-shadow, border-radius, text-overflow ellipsis, `::after` content, 2D transform,
filter (blur/grayscale), opacity, and `position: sticky` - the same combination of flow modes and
paint features a real dashboard-style page would use, not a synthetic worst case for any one
feature. Viewport 1280x6000.

## Results

### .NET 10.0.5 (net10.0)

| Method | Mean | Allocated |
|---|---:|---:|
| Parse HTML+CSS into a DOM (AngleSharp's own cost) | 1.20 ms | 1.96 MB |
| **Layout only: `BuildDisplayList`** | **2,415 ms** | **1,455.88 MB** |
| Full pipeline: layout + Skia rasterization + PNG encoding (`RenderToPng`) | 2,666 ms | 1,460.74 MB |

### .NET 8.0.25 (net8.0)

| Method | Mean | Allocated |
|---|---:|---:|
| Parse HTML+CSS into a DOM (AngleSharp's own cost) | 1.41 ms | 2.04 MB |
| **Layout only: `BuildDisplayList`** | **2,993 ms** | **1,459.94 MB** |
| Full pipeline: layout + Skia rasterization + PNG encoding (`RenderToPng`) | 3,226 ms | 1,465.21 MB |

## Reading these numbers

- Parsing the HTML+CSS document itself is fast (~1-1.5ms) and allocates almost nothing by
  comparison - the cost is overwhelmingly in this renderer's own layout, not in AngleSharp's HTML
  parser or AngleSharp.Css's cascade.
- `BuildDisplayList` (pure layout, no rasterization) is the dominant cost in the full pipeline -
  Skia rasterization + PNG encoding on top of an already-built display list adds only ~250-300ms
  (~10%), not the majority of the time.
- **~1.45 GB allocated for a ~1500-element page is the single most striking number here** - roughly
  1MB of managed allocation *per DOM element* laid out. That ratio, far more than the raw
  millisecond figures, is the strongest signal for where to start investigating: something in the
  layout path is very likely doing repeated, avoidable allocation per element (candidates worth
  checking first: `CreateStyleMap` - a fresh `Dictionary<string,string>` plus many
  `style.GetPropertyValue`/string-parse calls, built from scratch on *every* call, and called more
  than once per element in some paths - flex item base/cross-size estimation, grid item placement
  estimation, inline-block size prediction, and the element's own real layout pass all each build
  their own copy of the same element's style map independently) rather than an inherently slow
  algorithm.
- net10.0 is meaningfully faster than net8.0 on identical code (~2.42s vs ~2.99s, about 19%
  faster) with nearly identical allocation - consistent with JIT/GC improvements across runtime
  versions rather than anything version-specific in this renderer's own code.

## Reproducing/updating this baseline

```bash
cd src/AngleSharp.Renderer.Benchmarks
dotnet run -c Release -f net10.0              # full BenchmarkDotNet default job (most rigorous)
dotnet run -c Release -f net10.0 -- --job short  # faster, still stable for this fixture
```

BenchmarkDotNet refuses to run outside `Release` and warns loudly if it detects a debugger or
unoptimized build - always use `-c Release`. Update the tables above (and the date) after a
meaningful optimization lands, so this file keeps tracking the current baseline rather than one
frozen the day it was first written.
