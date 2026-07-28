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

var context = BrowsingContext.New(Configuration.Default.WithCss());
var document = await context.OpenAsync(request => request.Content("<html><body>Hello</body></html>"));

var renderer = new HtmlRenderer();
var image = renderer.RenderToPng(document);
```

## Text And Fonts

The renderer supports basic text styling such as font size, weight, italic, alignment, spacing, and decoration.
Generic font families like `serif`, `sans-serif`, and `monospace` are mapped to installed local fonts so they render as visibly different faces.

If you want predictable output in visual tests, specify an explicit font family and keep the host environment consistent.

## Visual Testing

The test suite uses PNG snapshots under `verification-assets/`. Missing baselines are created automatically unless strict mode is enabled with `ANGLESHARP_SNAPSHOT_STRICT=1`.
