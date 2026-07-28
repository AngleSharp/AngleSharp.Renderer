---
title: "Questions"
section: "AngleSharp.Renderer"
---
# Frequently Asked Questions

## Why do web-safe fonts look the same on my machine?

The renderer maps generic families such as `serif`, `sans-serif`, and `monospace` to installed local fonts. If your system does not have distinct serif/sans/monospace faces available, Skia may fall back to similar-looking defaults.

For stable results, install a font set with visibly different faces or use explicit family names that are present on the target machine.

## Why does a snapshot fail when I add a new baseline?

Visual snapshots compare the current PNG against the baseline in `verification-assets/`. If the baseline is missing, it is created automatically unless strict mode is enabled.

## How do I debug layout issues?

Start with `BuildDisplayList(...)` and inspect the commands before rasterizing. This usually makes it easier to see whether the issue is in layout, text measurement, or final drawing.

## Can I render only the render tree?

Yes. AngleSharp.Css exposes the render tree via `window.Render(...)`, which is helpful when you want to inspect computed styles before handing the document to AngleSharp.Renderer.
