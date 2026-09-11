# Performance Baseline

Machine: Apple M5 Pro (18 cores), macOS Tahoe 26.6.2, .NET SDK 10.0.201. All numbers use
BenchmarkDotNet's `short` job (3 warmup + 3 measured iterations) rather than its full default job,
to keep runs fast; they are stable (sub-2% StdDev) and consistent between independent invocations,
so they are trustworthy despite the shorter job - see "Reproducing/updating this baseline" below
for a more statistically rigorous run.

Fixture: `BenchmarkFixture.BuildLargePageHtml()` - see that file for the exact markup/CSS - a
single ~1500-element page (160 cards in a wrapping flexbox, a 16-item CSS Grid section, a floated
sidebar with a list, a 40-row table, a form row, a sticky header) mixing linear/radial/conic
gradients, box-shadow, border-radius, text-overflow ellipsis, `::after` content, 2D transform,
filter (blur/grayscale), opacity, and `position: sticky` - the same combination of flow modes and
paint features a real dashboard-style page would use, not a synthetic worst case for any one
feature. Viewport 1280x6000.

## Current numbers (2026-09-11, after the style-map caching + explicit-declaration caching fixes)

Against `AngleSharp` 1.8.1, `AngleSharp.Css` 1.1.2 (both via `PackageReference`).

### .NET 10.0.5 (net10.0)

| Method | Mean | Allocated |
|---|---:|---:|
| Parse HTML+CSS into a DOM (AngleSharp's own cost) | 1.52 ms | 1.96 MB |
| **Layout only: `BuildDisplayList`** | **268 ms** | **200.91 MB** |
| Full pipeline: layout + Skia rasterization + PNG encoding (`RenderToPng`) | 494 ms | 205.82 MB |

### .NET 8.0.25 (net8.0)

| Method | Mean | Allocated |
|---|---:|---:|
| Parse HTML+CSS into a DOM (AngleSharp's own cost) | 1.46 ms | 2.04 MB |
| **Layout only: `BuildDisplayList`** | **351 ms** | **201.37 MB** |
| Full pipeline: layout + Skia rasterization + PNG encoding (`RenderToPng`) | 581 ms | 206.63 MB |

**Cumulative improvement over the original, pre-optimization baseline: ~9x faster, ~7.25x less
allocation** on `BuildDisplayList` (net10.0: 2,415 ms / 1,456 MB &rarr; 268 ms / 201 MB).

## History

### 2026-09-11, initial baseline (before any optimization)

| Method (net10.0) | Mean | Allocated |
|---|---:|---:|
| Parse HTML+CSS into a DOM | 1.20 ms | 1.96 MB |
| Layout only: `BuildDisplayList` | 2,415 ms | 1,455.88 MB |
| Full pipeline: `RenderToPng` | 2,666 ms | 1,460.74 MB |

**~1.45 GB allocated for a ~1500-element page was the single most striking number** - roughly 1MB
of managed allocation *per DOM element* laid out, far more suspicious than the raw millisecond
figures. The leading suspect was `CreateStyleMap` - a fresh `Dictionary<string,string>` plus ~90
individual `style.GetPropertyValue`/string-parse calls, built completely from scratch on *every*
call - being invoked more than once for the same element in several places: flex item base/cross-
size estimation (`CreateFlexItemLayoutInfo`), grid item placement estimation
(`LayoutGridContainer`'s pass 1), inline-block line-flow-prediction (`TryMeasureInlineBlockBoxSize`),
a margin-collapse lookahead at the first visible child (`TryGetFirstCollapsibleChildTopMargin`),
and - the single biggest offender, since it runs for essentially every container's every child
throughout the whole tree - `OrderChildrenForPainting`'s own z-index-bucketing pass, all reading
the same element's style ahead of that same element's own, later, authoritative layout call.

### Fix: cache each element's style map for the lifetime of one render

`LayoutContext` (the struct already threaded through every layout call) gained a
`Dictionary<IElement, Dictionary<string, string>> StyleMapCache`, keyed by element identity
(`ReferenceEqualityComparer`, the same convention `LayoutCapture` already used for keying by
`IElement`). A new `GetOrCreateStyleMap(context, element, style)` wraps `CreateStyleMap` with a
cache check/populate and replaced all 13 call sites - so each element's ~90-property style map is
now actually computed once per render, no matter how many places along the way ask for it.
Correctness depends entirely on the cache's lifetime being scoped to exactly one
`BuildDisplayList`/`RenderToPng`/`CaptureLayoutMetrics` call: `CreateLayoutContext` constructs a
brand new, empty cache on every single call, never reusing one from a previous call, which is what
keeps this safe for the interactive `:hover`/`transition`/`animation`/caret-blink case (where the
very same document can legitimately compute different style between one render and the next - a
cache that outlived a single call would silently serve stale style there).

**Result: ~2.5x faster and ~2.4x less allocation** on `BuildDisplayList` (net10.0: 2,415 ms / 1,456
MB &rarr; 978 ms / 614 MB; net8.0: 2,993 ms / 1,460 MB &rarr; 1,174 ms / 616 MB), confirming the
duplicate-style-map-rebuild hypothesis was the dominant cost, not a red herring.

### Fix: cache the explicit-declaration style collection and read it once per element

With per-element style-map rebuilds gone, the next-largest cost was inside `CreateStyleMap` itself:
`grid-template-columns`/`-rows`, the three gap properties, `grid-column`/`-row`, and
`background-image` are all deliberately read from the element's cascaded-but-*uncomputed*
declaration rather than `ComputeCurrentStyle()` (see `ResolveExplicitPropertyValue`'s own remarks -
a percentage in any of these gets eagerly resolved against the wrong reference dimension by
AngleSharp.Css's `.Compute()` step otherwise). That path was expensive in two independent ways,
confirmed by reading `AngleSharp.Css.StyleCollectionExtensions` directly rather than assumed: (1)
`window.GetStyleCollection(device)` builds a brand new `StyleCollection` - walking every stylesheet,
including the UA sheet - from scratch on *every single call*, and (2) `GetDeclarations(element)`
re-walks the element's *entire ancestor chain* and re-cascades from scratch, also on every call.
`CreateStyleMap` was calling this combination up to 8 times per element (once per property above) -
each one independently rebuilding both the whole-document style collection and the same element's
own full ancestor cascade, only to read a single property off the result.

`LayoutContext` gained an `IStyleCollection? StyleCollection`, built once in `CreateLayoutContext`
(mirroring exactly the per-call device-resolution fallback `ResolveExplicitPropertyValue` used, so
the result is identical, just computed once instead of thousands of times). `GetExplicitDeclarations`
resolves an element's cascaded declaration once per `CreateStyleMap` call via that shared collection,
and `ReadExplicitOrComputed` reads each of the 8 properties off that one object - collapsing 8
independent full-document-collection-builds-plus-ancestor-walks per element down to one. Falls back
to the original `ResolveExplicitPropertyValue`/`ResolveExplicitBackgroundImage` (unchanged) whenever
no `LayoutContext` is available, which is only true for the single one-time root-element style read
in `CreateLayoutContext` itself, before the context (and therefore this cache) exists.

**Result: a further ~3.5x faster and ~3.1x less allocation** on `BuildDisplayList` (net10.0: 978 ms
/ 614 MB &rarr; 268 ms / 201 MB; net8.0: 1,174 ms / 616 MB &rarr; 351 ms / 201 MB) - confirming this
really was the next-dominant cost, not a smaller contributor.

## Reading these numbers

- Parsing the HTML+CSS document itself is fast (~1-1.5ms) and allocates almost nothing by
  comparison - the cost is overwhelmingly in this renderer's own layout, not in AngleSharp's HTML
  parser or AngleSharp.Css's cascade.
- `BuildDisplayList` (pure layout, no rasterization) is still the dominant cost in the full
  pipeline, though rasterization/encoding is now a proportionally larger slice (~225ms on top of
  ~268ms of layout) than it was before either fix, since layout itself shrank so much.
- Remaining allocation (~201 MB, ~135KB per element) is still dominated by `CreateStyleMap` itself,
  which now runs exactly once per element (no more duplicate rebuilds, no more duplicate
  explicit-declaration walks) but still allocates a full `Dictionary<string, string>` plus a
  separate string for each of its ~90 properties every time. Eliminating that - e.g. moving off a
  string-keyed dictionary entirely in favor of typed fields - is the natural next target, but is a
  substantially larger, riskier refactor than either fix above: essentially every
  `ParseLength`/`GetPropertyValue`-style call site throughout `HtmlRenderer.cs` reads from that
  dictionary by string key today, so this would touch the whole file rather than a handful of
  call sites. Deliberately not attempted in the same pass as either fix above.
- net10.0 is meaningfully faster than net8.0 on identical code with nearly identical allocation,
  consistently across the original baseline and both fixes - consistent with JIT/GC improvements
  across runtime versions rather than anything version-specific in this renderer's own code.

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
