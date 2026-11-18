namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// The snippets of the README and <c>docs/</c>, as written there, so the documentation keeps
/// compiling and doing what it says.
/// </summary>
public sealed class DocumentationExampleTests
{
    [Fact]
    public async Task QuickStart_RendersAPngOfTheViewport()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 450 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device).WithRendering());
        var document = await context.OpenAsync(req => req.Content("<html><body><h1>Hello Renderer</h1></body></html>"));

        var image = document.RenderToPng();

        Assert.Equal(800, image.Width);
        Assert.Equal(450, image.Height);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(0x89, image.Data[0]);
    }

    [Fact]
    public async Task QuickStart_ExplicitDevice_OverridesTheContext()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(req => req.Content("<p>Hello</p>"));

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 320, ViewPortHeight = 200 });

        Assert.Equal(320, image.Width);
        Assert.Equal(200, image.Height);
    }

    [Fact]
    public async Task HeadlessSelection_IsPaintedReadAndMeasured()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        var document = await context.OpenAsync(req => req.Content("""
            <p id="intro" style="font: 20px/30px monospace">Hello World</p>
            """));

        var harness = document.Context.GetDomHarness();
        var unselected = document.RenderToPng().Data;

        var text = document.GetElementById("intro")!.FirstChild!;
        var selection = document.GetSelection();
        selection.SetBaseAndExtent(text, 6, text, 11);

        Assert.Equal("World", selection.ToString());
        Assert.Equal("World", harness.SelectedText);

        var rect = selection.GetRangeAt(0).GetBoundingClientRect();
        Assert.True(rect.Width > 50 && rect.Height > 0);
        Assert.Single(selection.GetRangeAt(0).GetClientRects());

        Assert.NotEqual(unselected, document.RenderToPng().Data);

        selection.SelectAllChildren(document.Body!);
        Assert.Equal("Hello World", selection.ToString());

        selection.RemoveAllRanges();
        Assert.Equal(unselected, document.RenderToPng().Data);
    }

    [Fact]
    public async Task HeadlessSelection_PointerGestures_SelectAsInABrowser()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        var document = await context.OpenAsync(req => req.Content("""
            <body style="margin: 0"><p style="margin: 0; font: 20px/30px monospace">Hello World</p></body>
            """));
        var harness = document.Context.GetDomHarness();

        Assert.True(harness.BeginTextSelection(30, 15, clickCount: 2));
        harness.EndTextSelection();

        Assert.Equal("Hello", harness.SelectedText);
    }

    [Fact]
    public async Task Api_RawPixelsAndGeometry()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 1024, ViewPortHeight = 768, FontSize = 16 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device).WithRendering());
        var document = await context.OpenAsync(req => req.Content("""
            <body style="margin: 0"><div id="box" style="width: 200px; height: 80px; background: red">Hello</div></body>
            """));
        var element = document.GetElementById("box")!;

        var renderer = new HtmlRenderer();
        var backend = new AngleSharp.Renderer.Skia.SkiaRenderBackend();
        var pixels = backend.RenderToPixels(renderer.BuildDisplayList(document), new RenderViewport(1024, 768));
        Assert.Equal(1024 * 4, pixels.RowBytes);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.Data[(pixels.RowBytes * 70 + 4 * 150)..(pixels.RowBytes * 70 + 4 * 150 + 4)]);

        var rect = element.GetBoundingClientRect();
        Assert.Equal((200d, 80d), (rect.Width, rect.Height));
        Assert.Equal(200, element.GetOffsetWidth());
        Assert.Same(element, document.ElementFromPoint(100, 40));
        Assert.NotNull(document.CaretPositionFromPoint(10, 10));
    }

    [Fact]
    public async Task Api_FontAddedFromCode_IsUsedForRendering()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 60 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        var document = await context.OpenAsync(req => req.Content("""
            <p style="font: 30px Brand, monospace">Hello</p>
            """));
        var before = document.RenderToPng().Data;
        var fontBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fonts", "DejaVuSerif-Latin1.ttf"));

        var face = new DocumentFontFace(document.DefaultView!, "Brand", fontBytes);
        document.Fonts().Add(face);
        await document.Fonts().Ready;

        Assert.NotEqual(before, document.RenderToPng().Data);
    }

    [Fact]
    public async Task HeadlessSelection_TextIsAsShown()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        var document = await context.OpenAsync(req => req.Content("""
            <div id="t"><p style="text-transform: uppercase">one   two</p><p>three</p></div>
            """));
        var harness = document.Context.GetDomHarness();

        document.GetSelection().SelectAllChildren(document.GetElementById("t")!);

        Assert.Equal("ONE TWO\n\nthree", harness.SelectedText);
        Assert.Equal(harness.SelectedText, document.GetSelection().ToString());
    }

    [Fact]
    public async Task Examples_HighlightASearchHit()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 320, ViewPortHeight = 200, FontSize = 16 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        var document = await context.OpenAsync(request => request.Content("""
            <html><body><p style="font-family:serif; font-size:24px;">Hello renderer</p></body></html>
            """));
        var before = document.RenderToPng().Data;

        var harness = document.Context.GetDomHarness();
        var paragraph = document.QuerySelector("p")!;
        var text = paragraph.FirstChild!;
        var start = text.TextContent.IndexOf("renderer", StringComparison.Ordinal);

        document.GetSelection().SetBaseAndExtent(text, start, text, start + "renderer".Length);

        Assert.Equal("renderer", harness.SelectedText);
        Assert.NotEqual(before, document.RenderToPng().Data);
    }

    [Fact]
    public async Task Api_RenderOptions()
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device).WithRendering());
        var document = await context.OpenAsync(req => req.Content("""
            <div class="card" style="width: 200px; height: 100px; background: teal">Card</div><div style="height: 1000px"></div>
            """));

        var page = document.RenderToImage(new RenderOptions { FullPage = true });
        var retina = document.RenderToImage(new RenderOptions { DeviceScaleFactor = 2f });
        var thumbnail = document.RenderToImage(new RenderOptions { Format = RenderImageFormat.Jpeg, Quality = 80 });
        var banner = document.RenderToImage(new RenderOptions { Clip = new RenderRect(0, 0, 800, 120) });

        var card = document.QuerySelector(".card")!;
        var image = card.RenderToImage(new RenderOptions { DeviceScaleFactor = 2f, OmitBackground = true });

        Assert.True(page.Height > 1000);
        Assert.Equal((1600, 1200), (retina.Width, retina.Height));
        Assert.Equal("image/jpeg", thumbnail.MimeType);
        Assert.Equal((800, 120), (banner.Width, banner.Height));
        Assert.Equal((400, 200), (image.Width, image.Height));

        var preview = document.RenderToImage(new RenderOptions { Format = RenderImageFormat.Webp, Quality = 75 });
        Assert.Equal("image/webp", preview.MimeType);
    }

    [Fact]
    public async Task QuickStart_WithoutARenderDevice_RendersAt800x600()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering());
        var document = await context.OpenAsync(req => req.Content("<p>Hello</p>"));

        var image = new HtmlRenderer().RenderToPng(document);

        Assert.Equal((800, 600), (image.Width, image.Height));
    }
}
