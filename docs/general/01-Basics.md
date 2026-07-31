---
title: "Getting Started"
section: "AngleSharp.Renderer"
---
# Getting Started

## Requirements

AngleSharp.Renderer builds on top of AngleSharp and AngleSharp.Css. Install the packages you need via NuGet:

```ps1
Install-Package AngleSharp
Install-Package AngleSharp.Css
Install-Package AngleSharp.Renderer
```

## Rendering Pipeline

The renderer works in three steps:

1. AngleSharp parses the HTML document.
2. AngleSharp.Css provides computed styles and the render tree.
3. AngleSharp.Renderer converts the tree into a display list and rasterizes it with Skia.

```cs
using AngleSharp;
using AngleSharp.Renderer;

var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering());
var document = await context.OpenAsync(request => request.Content("<html><body>Hello</body></html>"));

var renderer = new HtmlRenderer();
var image = renderer.RenderToPng(document);
```

## Canvas Support

The renderer can also be configured to expose a basic 2D drawing context for `<canvas>` elements. Register the rendering service once on the browsing context with `Configuration.Default.WithCss().WithRendering()` and then resolve the context through `canvas.GetContext("2d")`.

```cs
using AngleSharp;
using AngleSharp.Renderer;

var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering());
var document = await context.OpenAsync(request => request.Content("<html><body><canvas width='120' height='80'></canvas></body></html>"));

var canvas = document.QuerySelector("canvas") as IHtmlCanvasElement;
var canvasContext = canvas?.GetContext("2d") as Canvas2DRenderingContext;

canvasContext?.SetFillStyle("#ff0000");
canvasContext?.FillRect(10f, 10f, 80f, 40f);
```

This implementation is intentionally lightweight and bitmap-backed, but it supports common operations such as rectangles, paths, text, clear operations, and save/restore state.

## Text And Fonts

The renderer supports basic text styling such as font size, weight, italic, alignment, spacing, and decoration.
Generic font families like `serif`, `sans-serif`, and `monospace` are mapped to installed local fonts so they render as visibly different faces.

If you want predictable output in visual tests, specify an explicit font family and keep the host environment consistent.

## Visual Testing

The test suite uses PNG snapshots under `verification-assets/`. Missing baselines are created automatically unless strict mode is enabled with `ANGLESHARP_SNAPSHOT_STRICT=1`.
