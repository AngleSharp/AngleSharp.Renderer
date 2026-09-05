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

Font handling resolves each entry of a `font-family` list in order, and the first usable one wins:

1. Generic families (`serif`, `sans-serif`, `monospace`, plus `cursive`, `fantasy`, `system-ui` and the `ui-*` aliases) always come from the fonts bundled in `Resources/Fonts`. They are keywords, so an `@font-face` rule cannot take them over, and they are what keeps snapshots reproducible.
2. `@font-face` declarations, collected per document by `FontFaceLoader` and carried on `DisplayList.Fonts`. Sources are tried in declaration order; `local()` resolves against the installed fonts and `url()` against `data:` URIs, or the network when - and only when - the browsing context has an  `IDocumentLoader` configured, mirroring how images are handled. WOFF and WOFF2 are rejected up front because Skia cannot decode them.
3. Installed families, which depend on the host and are therefore not safe to assert in snapshots.
4. The bundled sans-serif, as the last resort.

Table spans: a cell covers the columns and rows it spans, and a spanning cell's height is shared across the rows it covers rather than imposed on each of them. With `border-collapse: collapse` each cell paints only its top and left edge and the table adds the frame, so shared edges are drawn once and no rule is painted across a spanning cell. Cell content honours `vertical-align` (`top`, `middle`, `bottom`), defaulting to the `middle` that AngleSharp.Css resolves for cells; `baseline` is treated as `top`, since baselines are not aligned across a row. Note that on a cell `vertical-align` positions the content box, which is a different meaning from the inline shift `ParseVerticalAlign` applies to `super`, `sub` and friends.

Current behavior includes block layout, margins, padding, borders, floats, inline-block, relative/fixed/absolute positioning, z-index ordering, outlines, text styling, text alignment, line-height, letter-spacing, text-indent, vertical-align, and generic font-family handling.

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
