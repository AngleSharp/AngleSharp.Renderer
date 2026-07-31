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

The main renderer project targets `net8.0` and `net10.0`. The test project targets `net8.0`.

## Architecture

The renderer is intentionally split into a small number of layers:

- `HtmlRenderer` builds a display list from the AngleSharp render tree and computed styles.
- `DisplayList` is the backend-agnostic command model.
- `SkiaRenderBackend` turns the display list into a PNG using SkiaSharp.
- `HtmlRenderOptions` holds viewport and text defaults.
- `AngleSharp.Css` provides the render tree and computed-style data used by the renderer.

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
- Missing baselines are auto-created unless `ANGLESHARP_SNAPSHOT_STRICT=1` or `true` is set.
- CI runs visual tests on both Linux and Windows. To reduce drift, the renderer uses bundled deterministic font files for generic font-family resolution.

When changing renderer behavior, update or add tests first, then run the focused test file or the full test project.

## Repository Notes

- The docs live under `docs/general/` and `docs/tutorials/`; they are the best place to document user-facing renderer behavior.
- `AGENTS.md` should remain the primary agent note for this repo.
- The repository already carries a snapshot-based workflow, so visual changes usually require updating the baseline PNGs together with the code change.
- The Linux Skia setup uses native assets from the main project package references.
- Keep an eye on `failure-assets/` after test runs; they are useful diagnostics, not source of truth.
