---
title: "Examples"
section: "AngleSharp.Renderer"
---
# Example Code

## Render A Page To PNG

```cs
using AngleSharp;
using AngleSharp.Renderer;

var context = BrowsingContext.New(Configuration.Default.WithCss());
var document = await context.OpenAsync(request => request.Content("""
<html>
	<body>
		<p style="font-family:serif; font-size:24px;">Hello renderer</p>
	</body>
</html>
"""));

var renderer = new HtmlRenderer();
var image = renderer.RenderToPng(document, new HtmlRenderOptions
{
		Width = 320,
		Height = 200,
		FontSize = 16f,
});

File.WriteAllBytes("render.png", image.Data);
```

## Draw To A Canvas

```cs
using AngleSharp;
using AngleSharp.Renderer;
using AngleSharp.Html.Dom;

var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering());
var document = await context.OpenAsync(request => request.Content("""
<html>
	<body>
		<canvas width="120" height="80"></canvas>
	</body>
</html>
"""));

var canvas = document.QuerySelector("canvas") as IHtmlCanvasElement;
var canvasContext = canvas?.GetContext("2d") as Canvas2DRenderingContext;

canvasContext?.SetFillStyle("#ff0000");
canvasContext?.FillRect(10f, 10f, 100f, 50f);
canvasContext?.SetFillStyle("#00ff00");
canvasContext?.FillRect(30f, 20f, 50f, 30f);

var png = canvasContext?.ToImage("image/png");
File.WriteAllBytes("canvas.png", png ?? Array.Empty<byte>());
```

This example highlights the new canvas path and shows how the renderer can produce PNG output from 2D drawing operations.

## Compare Different Font Families

```html
<p style="font-family:serif;">Serif sample</p>
<p style="font-family:sans-serif;">Sans sample</p>
<p style="font-family:monospace;">Mono sample</p>
```

Use this pattern when you want visual tests to prove that generic families are mapped correctly.

## Inspect The Display List

```cs
var renderer = new HtmlRenderer();
var displayList = renderer.BuildDisplayList(document);

foreach (var command in displayList.Commands)
{
		Console.WriteLine(command);
}
```

Inspecting the display list is often the fastest way to debug box-model or text-layout behavior.
