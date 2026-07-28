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

## RenderedImage

`RenderedImage` wraps the raster result and exposes the PNG bytes together with width, height, and MIME type.

This makes it easy to save the image, compare it in tests, or pass it to another tool.
