---
title: "API Documentation"
section: "AngleSharp.Renderer"
---
# API Documentation

## HtmlRenderer

`HtmlRenderer` is the main entry point. It can build a display list or render directly to PNG.

```cs
var renderer = new HtmlRenderer();
var displayList = renderer.BuildDisplayList(document);
var image = renderer.RenderToPng(document);
```

## Rendering Configuration

Use `Configuration.Default.WithCss().WithRendering()` when you want to enable the renderer’s canvas-aware services. The `WithRendering()` extension registers the canvas rendering service that powers the `2d` context for `<canvas>` elements.

```cs
var config = Configuration.Default.WithCss().WithRendering();
var context = BrowsingContext.New(config);
```

## HtmlRenderOptions

`HtmlRenderOptions` controls the viewport and default rendering behavior.

- `Width` and `Height` define the output size.
- `Padding` offsets the content from the viewport edges.
- `FontFamily` and `FontSize` define the default text style.
- `ParagraphSpacing` and `LineHeightMultiplier` control text layout spacing.

## DisplayList

`DisplayList` captures the renderer output as paint commands. This is useful if you want to inspect layout behavior without rasterizing immediately.

The list currently includes:

- filled rectangles for backgrounds, borders, and outlines,
- text commands with font, alignment, spacing, and decoration metadata.

## Canvas Rendering Context

The renderer package also ships with a basic 2D canvas implementation backed by Skia. Once the rendering service is registered, `<canvas>` elements can be drawn with methods such as `FillRect`, `StrokeRect`, `ClearRect`, `BeginPath`, `MoveTo`, `LineTo`, `ClosePath`, `Fill`, `Stroke`, `FillText`, and `Save`/`Restore`.

```cs
var canvas = document.QuerySelector("canvas") as IHtmlCanvasElement;
var canvasContext = canvas?.GetContext("2d") as Canvas2DRenderingContext;

canvasContext?.SetFillStyle("#00ff00");
canvasContext?.FillRect(10f, 10f, 80f, 40f);
```

## RenderedImage

`RenderedImage` wraps the raster result and exposes the PNG bytes together with width, height, and MIME type.

This makes it easy to save the image, compare it in tests, or pass it to another tool.
