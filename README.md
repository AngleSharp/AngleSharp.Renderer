![logo](https://raw.githubusercontent.com/AngleSharp/AngleSharp.Renderer/main/header.png)

# AngleSharp.Renderer

[![CI](https://github.com/AngleSharp/AngleSharp.Renderer/actions/workflows/ci.yml/badge.svg)](https://github.com/AngleSharp/AngleSharp.Renderer/actions/workflows/ci.yml)
[![GitHub Tag](https://img.shields.io/github/tag/AngleSharp/AngleSharp.Renderer.svg?style=flat-square)](https://github.com/AngleSharp/AngleSharp.Renderer/releases)
[![NuGet Count](https://img.shields.io/nuget/dt/AngleSharp.Renderer.svg?style=flat-square)](https://www.nuget.org/packages/AngleSharp.Renderer/)
[![Issues Open](https://img.shields.io/github/issues/AngleSharp/AngleSharp.Renderer.svg?style=flat-square)](https://github.com/AngleSharp/AngleSharp.Renderer/issues)
[![CLA Assistant](https://cla-assistant.io/readme/badge/AngleSharp/AngleSharp.Renderer?style=flat-square)](https://cla-assistant.io/AngleSharp/AngleSharp.Renderer)

AngleSharp.Renderer extends the core AngleSharp library with some more powerful rendering capabilities. This repository is the home of the source for the AngleSharp.Renderer NuGet package.

## Current Status

The project now contains a first draft implementation with:

- A backend-agnostic rendering core and display-list model
- A SkiaSharp backend that renders PNG output
- A basic DOM-driven text layout pass (block flow, heading scaling, word wrapping)

This is an initial vertical slice and not a full browser-grade layout engine yet.

## Quick Start

```csharp
using AngleSharp;
using AngleSharp.Renderer;

var context = BrowsingContext.New(Configuration.Default);
var document = await context.OpenAsync(req => req.Content(@"
<html>
	<body>
		<h1>Hello Renderer</h1>
		<p>This is a first draft image render from AngleSharp.Renderer.</p>
	</body>
</html>"));

var renderer = new HtmlRenderer();
var image = renderer.RenderToPng(document, new HtmlRenderOptions
{
		Width = 800,
		Height = 450,
});

await File.WriteAllBytesAsync("render.png", image.Data);
```

## Design Direction

The current architecture is intentionally split into two layers:

- Core: DOM/CSS integration, layout model, display-list generation
- Backend: rasterization (currently SkiaSharp)

This keeps future work open for additional backends and interactive rendering scenarios.

## .NET Foundation

This project is supported by the [.NET Foundation](https://dotnetfoundation.org).

## License

AngleSharp.Renderer is released using the MIT license. For more information see the [license file](./LICENSE).
