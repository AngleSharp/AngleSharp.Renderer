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
