namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using SkiaSharp;

/// <summary>
/// Rendering with <see cref="RenderOptions"/>: full page, clips, elements, device scale factor,
/// transparent background and image formats.
/// </summary>
public sealed class RenderOptionsTests
{
    private const string Reset = "<style>html, body { margin: 0; padding: 0; }</style>";

    [Fact]
    public async Task DefaultOptions_RenderAsRenderToPng()
    {
        var document = await OpenAsync($"""{Reset}<div style="height: 50px; background: red">Hello</div>""");

        Assert.Equal(document.RenderToPng().Data, document.RenderToImage().Data);
        Assert.Equal(document.RenderToPng().Data, new HtmlRenderer().RenderToImage(document, RenderOptions.Default).Data);
    }

    [Fact]
    public async Task FullPage_ReachesTheEndOfTheContent()
    {
        var document = await OpenAsync($"""{Reset}<div style="height: 500px"></div><div style="height: 100px; background: rgb(0, 0, 255)"></div>""");

        var image = document.RenderToImage(new RenderOptions { FullPage = true });

        Assert.Equal((300, 600), (image.Width, image.Height));
        using var bitmap = Decode(image);
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(150, 550));
        Assert.Equal(SKColors.White, bitmap.GetPixel(150, 300));
    }

    [Fact]
    public async Task FullPage_ShorterThanTheViewport_IsTheViewport()
    {
        var document = await OpenAsync($"""{Reset}<div style="height: 50px"></div>""");

        var image = document.RenderToImage(new RenderOptions { FullPage = true });

        Assert.Equal((300, 200), (image.Width, image.Height));
    }

    [Fact]
    public async Task FullPage_PaintsTheCanvasBackgroundAndKeepsFixedBoxesAtTheTop()
    {
        var document = await OpenAsync($"""
            {Reset}<style>body {"{"} background: rgb(0, 128, 0) {"}"}</style>
            <div style="position: fixed; top: 0; left: 0; width: 300px; height: 20px; background: rgb(255, 0, 0)"></div>
            <div style="height: 800px"></div>
            """);

        using var bitmap = Decode(document.RenderToImage(new RenderOptions { FullPage = true }));

        Assert.Equal(800, bitmap.Height);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(10, 10));
        Assert.Equal(new SKColor(0, 128, 0), bitmap.GetPixel(10, 790));
        Assert.Equal(new SKColor(0, 128, 0), bitmap.GetPixel(10, 410));
    }

    [Fact]
    public async Task FullPage_IgnoresTheScrollPosition()
    {
        var document = await OpenAsync($"""{Reset}<div style="height: 100px; background: rgb(255, 0, 0)"></div><div style="height: 700px"></div>""");
        document.Context.GetDomHarness();
        document.DocumentElement!.SetScrollTop(300);

        using var viewport = Decode(document.RenderToImage());
        using var page = Decode(document.RenderToImage(new RenderOptions { FullPage = true }));

        Assert.Equal(SKColors.White, viewport.GetPixel(10, 10));
        Assert.Equal(new SKColor(255, 0, 0), page.GetPixel(10, 10));
        Assert.Equal(800, page.Height);
    }

    [Fact]
    public async Task Clip_IsInPageCoordinates_AlsoBelowTheViewport()
    {
        var document = await OpenAsync($"""
            {Reset}<div style="height: 400px"></div>
            <div style="margin-left: 50px; width: 40px; height: 30px; background: rgb(0, 0, 255)"></div>
            <div style="height: 400px"></div>
            """);

        var image = document.RenderToImage(new RenderOptions { Clip = new RenderRect(40f, 390f, 60f, 50f) });

        Assert.Equal((60, 50), (image.Width, image.Height));
        using var bitmap = Decode(image);
        Assert.Equal(SKColors.White, bitmap.GetPixel(5, 5));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(10, 10));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(49, 39));
        Assert.Equal(SKColors.White, bitmap.GetPixel(51, 41));
    }

    [Fact]
    public async Task Clip_IsLimitedToThePage()
    {
        var document = await OpenAsync($"""{Reset}<div style="height: 100px"></div>""");

        var image = document.RenderToImage(new RenderOptions { Clip = new RenderRect(250f, 150f, 100f, 100f) });

        Assert.Equal((50, 50), (image.Width, image.Height));
        Assert.Throws<ArgumentException>(() => document.RenderToImage(new RenderOptions { Clip = new RenderRect(0f, 500f, 10f, 10f) }));
        Assert.Throws<ArgumentException>(() => document.RenderToImage(new RenderOptions { FullPage = true, Clip = new RenderRect(0f, 0f, 10f, 10f) }));
    }

    [Fact]
    public async Task Element_IsItsBorderBox_AlsoBelowTheViewport()
    {
        var document = await OpenAsync($"""
            {Reset}<div style="height: 350px"></div>
            <div id="card" style="margin-left: 20px; width: 100px; height: 60px; border: 5px solid rgb(255, 0, 0); background: rgb(0, 0, 255)"></div>
            """);
        var card = document.GetElementById("card")!;

        var image = card.RenderToImage();

        Assert.Equal((110, 70), (image.Width, image.Height));
        using var bitmap = Decode(image);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(1, 1));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(55, 35));
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(108, 68));
    }

    [Fact]
    public async Task Element_ClipIsRelativeToTheBorderBox()
    {
        var document = await OpenAsync($"""
            {Reset}<div id="card" style="margin: 30px; width: 100px; height: 60px; background: rgb(0, 0, 255)"><div style="width: 10px; height: 10px; background: rgb(255, 0, 0)"></div></div>
            """);

        var image = document.GetElementById("card")!.RenderToImage(new RenderOptions { Clip = new RenderRect(0f, 0f, 20f, 20f) });

        Assert.Equal((20, 20), (image.Width, image.Height));
        using var bitmap = Decode(image);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(5, 5));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(15, 15));
    }

    [Fact]
    public async Task Element_WithoutABox_Throws()
    {
        var document = await OpenAsync($"""{Reset}<div id="hidden" style="display: none"></div>""");

        Assert.Throws<InvalidOperationException>(() => document.GetElementById("hidden")!.RenderToImage());
    }

    [Fact]
    public async Task DeviceScaleFactor_ScalesTheImageNotTheLayout()
    {
        var document = await OpenAsync($"""{Reset}<div style="margin-left: 10px; width: 10px; height: 10px; background: rgb(0, 0, 255)"></div>""");

        var image = document.RenderToImage(new RenderOptions { DeviceScaleFactor = 2f });

        Assert.Equal((600, 400), (image.Width, image.Height));
        using var bitmap = Decode(image);
        Assert.Equal(SKColors.White, bitmap.GetPixel(19, 5));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(20, 0));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(39, 19));
        Assert.Equal(SKColors.White, bitmap.GetPixel(40, 20));
    }

    [Fact]
    public async Task DeviceScaleFactor_PaintsTextAtTheHigherResolution()
    {
        var document = await OpenAsync($"""{Reset}<p style="margin: 0; font: 20px sans-serif">Hello World</p>""");

        using var sharp = Decode(document.RenderToImage(new RenderOptions { DeviceScaleFactor = 2f, Clip = new RenderRect(0f, 0f, 120f, 30f) }));
        using var single = Decode(document.RenderToImage(new RenderOptions { Clip = new RenderRect(0f, 0f, 120f, 30f) }));
        using var upscaled = single.Resize(new SKImageInfo(240, 60), new SKSamplingOptions(SKFilterMode.Nearest));

        Assert.Equal((240, 60), (sharp.Width, sharp.Height));
        Assert.NotEqual(upscaled.Bytes, sharp.Bytes);
        Assert.Contains(Enumerable.Range(0, 240 * 60), i => sharp.GetPixel(i % 240, i / 240).Red < 64);
    }

    [Fact]
    public async Task DeviceScaleFactor_RoundsUpFractionalSizes()
    {
        var document = await OpenAsync($"""{Reset}<div></div>""");

        var image = document.RenderToImage(new RenderOptions { DeviceScaleFactor = 1.5f, Clip = new RenderRect(0f, 0f, 101f, 11f) });

        Assert.Equal((152, 17), (image.Width, image.Height));
    }

    [Fact]
    public async Task OmitBackground_LeavesThePageTransparent()
    {
        var document = await OpenAsync($"""{Reset}<div style="width: 50px; height: 50px; background: rgb(0, 0, 255)"></div>""");

        using var bitmap = Decode(document.RenderToImage(new RenderOptions { OmitBackground = true }));

        Assert.Equal(0, bitmap.GetPixel(150, 150).Alpha);
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(25, 25));
    }

    [Fact]
    public async Task OmitBackground_KeepsTheBackgroundOfThePage()
    {
        var document = await OpenAsync($"""{Reset}<style>body {"{"} background: rgb(0, 128, 0) {"}"}</style>""");

        using var bitmap = Decode(document.RenderToImage(new RenderOptions { OmitBackground = true, FullPage = true }));

        Assert.Equal(new SKColor(0, 128, 0), bitmap.GetPixel(150, 150));
    }

    [Fact]
    public async Task Jpeg_IsOpaqueOnWhite()
    {
        var document = await OpenAsync($"""{Reset}<div style="width: 50px; height: 50px; background: rgb(0, 0, 255)"></div>""");

        var image = document.RenderToImage(new RenderOptions { Format = RenderImageFormat.Jpeg, OmitBackground = true, Quality = 95 });

        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, image.Data[..2]);
        using var bitmap = Decode(image);
        Assert.Equal((300, 200), (bitmap.Width, bitmap.Height));
        var white = bitmap.GetPixel(150, 150);
        Assert.True(white.Red > 250 && white.Green > 250 && white.Blue > 250 && white.Alpha == 255);
        Assert.True(bitmap.GetPixel(25, 25).Blue > 240);
    }

    [Fact]
    public async Task Webp_KeepsTransparency()
    {
        var document = await OpenAsync($"""{Reset}<div style="width: 50px; height: 50px; background: rgb(0, 0, 255)"></div>""");

        var image = document.RenderToImage(new RenderOptions { Format = RenderImageFormat.Webp, OmitBackground = true });

        Assert.Equal("image/webp", image.MimeType);
        Assert.Equal("RIFF"u8.ToArray(), image.Data[..4]);
        Assert.Equal("WEBP"u8.ToArray(), image.Data[8..12]);
        using var bitmap = Decode(image);
        Assert.True(bitmap.GetPixel(150, 150).Alpha < 8);
    }

    [Fact]
    public async Task RenderToPixels_TakesTheOptions()
    {
        var document = await OpenAsync($"""{Reset}<div style="height: 500px"></div>""");

        var pixels = new HtmlRenderer().RenderToPixels(document, new RenderOptions { FullPage = true, DeviceScaleFactor = 2f });

        Assert.Equal((600, 1000), (pixels.Width, pixels.Height));
    }

    [Fact]
    public async Task Captures_CreateNoHarness()
    {
        var document = await OpenAsync($"""{Reset}<div id="a" style="height: 500px">a</div>""");

        document.RenderToImage(new RenderOptions { FullPage = true });
        document.GetElementById("a")!.RenderToImage();

        Assert.False(document.Context.TryGetDomHarness(out _));
    }

    [Fact]
    public async Task Captures_WithAHarness_PaintTheSelection()
    {
        var document = await OpenAsync($"""{Reset}<p id="p" style="margin: 0; font: 20px/30px monospace">Hello World</p><div style="height: 500px"></div>""");
        document.Context.GetDomHarness();
        var before = document.RenderToImage(new RenderOptions { FullPage = true }).Data;

        document.GetSelection().SelectAllChildren(document.GetElementById("p")!);

        Assert.NotEqual(before, document.RenderToImage(new RenderOptions { FullPage = true }).Data);
    }

    [Fact]
    public async Task InvalidOptions_Throw()
    {
        var document = await OpenAsync($"""{Reset}""");

        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderToImage(new RenderOptions { DeviceScaleFactor = 0f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderToImage(new RenderOptions { DeviceScaleFactor = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderToImage(new RenderOptions { Quality = 101 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderToImage(new RenderOptions { Clip = new RenderRect(0f, 0f, 0f, 10f) }));
    }

    [Fact]
    public async Task WithoutARenderDevice_RendersInTheFallbackViewport()
    {
        var document = await BrowsingContext.New(Configuration.Default.WithCss()).OpenAsync(req => req.Content("<div style='height: 1000px'>Hello</div>"));

        var viewport = document.RenderToImage();
        var png = document.RenderToPng();
        var page = document.RenderToImage(new RenderOptions { FullPage = true });

        Assert.Equal((HtmlRenderer.FallbackViewportWidth, HtmlRenderer.FallbackViewportHeight), (viewport.Width, viewport.Height));
        Assert.Equal(viewport.Data, png.Data);
        Assert.Equal((800, 1016), (page.Width, page.Height));
        Assert.Equal(800f, new HtmlRenderer().BuildDisplayList(document).Commands.OfType<FillRectCommand>().First().Rect.Width);
        Assert.False(document.Context.TryGetDomHarness(out _));
    }

    private static SKBitmap Decode(RenderedImage image) => SKBitmap.Decode(image.Data);

    private static Task<IDocument> OpenAsync(string body)
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200 };
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        return context.OpenAsync(req => req.Content($"<!doctype html><html><head></head><body>{body}</body></html>"));
    }
}
