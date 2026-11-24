---
title: "API Documentation"
section: "AngleSharp.Renderer"
---
# API Documentation

## Configuration

A browsing context needs AngleSharp.Css and a render device. `WithRendering()` adds the services for `<canvas>` (its 2D context) and for `<img>` data. The renderer loads the image of an `<img>` it lays out (the source its render node picked for the device from `srcset`, `sizes` and the sources of a `<picture>`, see `ElementRenderNode.ImageSource`), as AngleSharp's image element requests nothing by itself; a render waits for it, while a frame of an interactive harness paints it once it has loaded, and loads a lazy one (`loading=lazy`, with scripting) only once it is within 1250px of the viewport and of the scroll containers around it. Its natural size (also `naturalWidth` and `naturalHeight`) is divided by the density of the picked candidate (e.g. `2x`). An image not in a document (e.g. `new Image()` of a script) is loaded once it has a source:

```cs
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer;

var device = new DefaultRenderDevice { ViewPortWidth = 1024, ViewPortHeight = 768, FontSize = 16 };
var config = Configuration.Default.WithCss().WithRenderDevice(device).WithRendering();
var context = BrowsingContext.New(config);
```

The render device's `ViewPortWidth` and `ViewPortHeight` are the size of the viewport, and so of the rendered image (800×600 when the context has no render device); `FontSize` is the default font size. Images are rendered at one device pixel per CSS pixel.

## Scripting Without Reflection

AngleSharp.Js finds the DOM of this library by reflecting over its DOM attributes by default. A trimmed or natively compiled (AoT) application can instead hand it the registration generated for this library at build time, `AngleSharp.Renderer.RendererDom.Register` (a `DomRegistration` of AngleSharp). It declares the layout members of the library (`getBoundingClientRect`, `offsetWidth`, ...), the observers, animations and the 2D canvas, which replace the stubs of AngleSharp.Js as they do by reflection:

```cs
var config = Configuration.Default
    .WithCss()
    .WithRendering()
    .WithJs(RendererDom.Register);
```

The registration describes the version of the library in use, so it never has to be kept in sync by hand. Several libraries are combined with `DomRegistrations.Combine`.

## Rendering A Document

`document.RenderToPng()` renders the viewport of a document with the render device of its context:

```cs
var image = document.RenderToPng();
File.WriteAllBytes("page.png", image.Data);
```

`HtmlRenderer` is the same with more control: it takes a render device per call, and its constructors take an `IRenderBackend` (default: `SkiaRenderBackend`) and an `ITextMeasurer` (the backend, if it is one):

```cs
var renderer = new HtmlRenderer();
var image = renderer.RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 320, ViewPortHeight = 200 });
var displayList = renderer.BuildDisplayList(document);
```

A render shows the page at its current scroll position once the document has an interactive harness (see [Interactivity](../general/02-Interactivity.md)), and at the top otherwise. `RenderToPng` is `RenderToImage` with the default options.

## Render Options

`RenderToImage` takes `RenderOptions`, modeled on the screenshots of browser automation tools:

| Option | Default | Meaning |
| --- | --- | --- |
| `FullPage` | `false` | The whole page, from its top to the end of its content, instead of the viewport at its scroll position. The image is as wide as the viewport. |
| `Clip` | none | A `RenderRect` of the page in CSS pixels, from the top left of the page (the scroll position plays no role). Limited to the page and the viewport's width. |
| `DeviceScaleFactor` | `1` | Device pixels per CSS pixel, e.g. `2` for a HiDPI image. The layout stays the same; text, borders and SVG are painted sharp at the higher resolution. |
| `OmitBackground` | `false` | Leaves out the white behind the page, so the image is transparent where the page paints no background. |
| `Format` | `Png` | `Png`, `Jpeg` (transparent areas become white) or `Webp`. |
| `Quality` | `90` | The quality of JPEG and WebP, from 0 to 100. |

```cs
var page = document.RenderToImage(new RenderOptions { FullPage = true });
var retina = document.RenderToImage(new RenderOptions { DeviceScaleFactor = 2f });
var thumbnail = document.RenderToImage(new RenderOptions { Format = RenderImageFormat.Jpeg, Quality = 80 });
var banner = document.RenderToImage(new RenderOptions { Clip = new RenderRect(0, 0, 800, 120) });
```

An element renders its border box, wherever it is on the page; a `Clip` is then relative to the border box:

```cs
var card = document.QuerySelector(".card")!;
var image = card.RenderToImage(new RenderOptions { DeviceScaleFactor = 2f, OmitBackground = true });
```

A capture beyond the viewport is still laid out in the viewport: viewport units and media queries keep their values, and `position: fixed` boxes are painted once, at the top of the page. `HtmlRenderer.RenderToPixels(document, options)` gives the same capture as raw pixels. Rendering with options does not create the interactive harness; when the document has one, the selection is painted as well.

## RenderedImage And RenderedPixels

`RenderedImage` is an encoded image: `Data` (the PNG bytes), `Width`, `Height` and `MimeType`.

For raw pixels, use `renderer.RenderToPixels(document, options)`, rasterize a display list with a backend implementing `IPixelRenderBackend`, or rasterize a frame of the harness with `frame.Rasterize()`. `RenderedPixels` holds premultiplied RGBA bytes, `RowBytes` (`Width * 4`) per row:

```cs
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

var backend = new SkiaRenderBackend();
var pixels = backend.RenderToPixels(renderer.BuildDisplayList(document), new RenderViewport(1024, 768));
```

## DisplayList

`DisplayList` is the backend-agnostic result of layout: a flat list of `RenderCommand`s in paint order (`displayList.Commands`), plus the web fonts it uses (`Fonts`). Inspect it to debug layout without rasterizing, or paint it with a backend of your own.

- Drawing: `FillRectCommand` (backgrounds, borders, outlines; colors, gradients and images as `RenderPaint`), `StrokeRoundedRectCommand`, `FillRingCommand`, `DrawTextCommand`, `DrawTextShadowCommand`, `DrawBoxShadowCommand`, `DrawImageCommand`, `DrawBorderImageCommand`, `DrawBackdropFilterCommand` and `DrawSubframeCommand` (iframes).
- Scopes, as `Push…`/`Pop…` pairs that nest: clips (`PushClipCommand`, `PushClipShapeCommand`), transforms, opacity, filters, blend modes, masks and viewport anchors (fixed boxes).

A custom backend implements `IRenderBackend.RenderToPng(displayList, viewport)` (and `IPixelRenderBackend.RenderToPixels` for raw pixels). Layout measures text with the `ITextMeasurer` given to `HtmlRenderer`; pass the measurer of the fonts your backend paints with, or lines break differently from what is drawn.

## Geometry

With a render device registered, the [CSSOM View](https://drafts.csswg.org/cssom-view/) APIs answer from the layout, in viewport coordinates:

```cs
var rect = element.GetBoundingClientRect();          // also GetClientRects()
var width = element.GetOffsetWidth();                 // GetClientWidth(), GetScrollHeight(), ...
var hit = document.ElementFromPoint(100, 40);         // ElementsFromPoint for the whole stack
var caret = document.CaretPositionFromPoint(100, 40); // text node and offset
var textRect = range.GetBoundingClientRect();         // rects of a DOM range, e.g. a word
```

Scrolling goes through `element.ScrollTo(...)`, `SetScrollTop(...)`, `ScrollIntoView(...)` and `window.ScrollTo(...)`. `ResizeObserver` and `IntersectionObserver` report changes as the frames are laid out.

## Selection

`document.GetSelection()` (and `window.GetSelection()`) is the DOM's [Selection](https://w3c.github.io/selection-api/), painted in rendered images once the document has a harness. See [Selecting Text Headlessly](../general/02-Interactivity.md#selecting-text-headlessly).

## Animations

CSS transitions and animations run on a virtual clock (`harness.AdvanceTime(...)`). The Web Animations API is there as well: `element.Animate(keyframes, options)` returns an `Animation`, and `element.GetAnimations()`/`document.GetAnimations()` list them. See [Interactivity](../general/02-Interactivity.md).

## Fonts

`document.Fonts()` is the document's `FontFaceSet` of the [CSS Font Loading API](https://drafts.csswg.org/css-font-loading/): the faces of its `@font-face` rules, and faces added from code (from a `url(...)` or the bytes of a font). Adding a face loads it (browsers wait until text uses it), and `Ready` settles once the faces being loaded are done:

```cs
var face = new DocumentFontFace(document.DefaultView!, "Brand", fontBytes);
document.Fonts().Add(face);
await document.Fonts().Ready;
```

A face that fails to load stays in the set (its `Status` is `error`) and the text falls back to the next family. Faces of `@font-face` rules are in the set too, but cannot be added or deleted.

Generic families (`serif`, `sans-serif`, `monospace`, ...) always use the bundled fonts, so they render the same on every machine.

## Canvas Rendering Context

With `WithRendering()`, `<canvas>` elements have a 2D context backed by Skia. The members of the [canvas API](https://html.spec.whatwg.org/multipage/canvas.html) are available as methods: rectangles, paths (`Arc`, `ArcTo`, `Ellipse`, `RoundRect`, Bézier curves), `Fill`/`Stroke`/`Clip` with fill rules, text (`FillText`, `StrokeText`, `MeasureText`), `DrawImage`, gradients (linear, radial, conic), patterns, transforms, line dashes, shadows, compositing, hit tests (`IsPointInPath`) and `Save`/`Restore`. Properties are `Get…`/`Set…` methods:

```cs
var canvas = document.QuerySelector("canvas") as IHtmlCanvasElement;
var canvasContext = canvas?.GetContext("2d") as Canvas2DRenderingContext;

canvasContext?.SetFillStyle("#00ff00");
canvasContext?.FillRect(10f, 10f, 80f, 40f);
var png = canvasContext?.ToImage("image/png");
```

A canvas in a page shows its bitmap when the page is rendered.
