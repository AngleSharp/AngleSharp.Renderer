---
title: "Examples"
section: "AngleSharp.Renderer"
---
# Example Code

## Render A Page To PNG

```cs
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer;

var device = new DefaultRenderDevice { ViewPortWidth = 320, ViewPortHeight = 200, FontSize = 16 };
var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
var document = await context.OpenAsync(request => request.Content("""
<html>
	<body>
		<p style="font-family:serif; font-size:24px;">Hello renderer</p>
	</body>
</html>
"""));

var image = document.RenderToPng();

File.WriteAllBytes("render.png", image.Data);
```

## Capture The Full Page, An Element, Or A HiDPI Image

```cs
File.WriteAllBytes("page.png", document.RenderToImage(new RenderOptions { FullPage = true }).Data);

var intro = document.QuerySelector("p")!;
File.WriteAllBytes("paragraph@2x.png", intro.RenderToImage(new RenderOptions { DeviceScaleFactor = 2f }).Data);

var preview = document.RenderToImage(new RenderOptions { Format = RenderImageFormat.Webp, Quality = 75 });
File.WriteAllBytes("preview.webp", preview.Data);
```

## Highlight A Search Hit

Selecting text needs no pointer: select a DOM range and render. The selection is painted once the document has a harness (see [Interactivity](../general/02-Interactivity.md#selecting-text-headlessly)):

```cs
var harness = document.Context.GetDomHarness();
var paragraph = document.QuerySelector("p")!;
var text = paragraph.FirstChild!;
var start = text.TextContent.IndexOf("renderer", StringComparison.Ordinal);

document.GetSelection().SetBaseAndExtent(text, start, text, start + "renderer".Length);

File.WriteAllBytes("highlighted.png", document.RenderToPng().Data);
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

`ToImage` encodes the canvas bitmap (`image/png`, `image/jpeg` or `image/webp`).

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
