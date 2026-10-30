using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;

using SkiaSharp;

using System.Diagnostics;
using System.Net;
using System.Threading;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// Regressions found while hosting the renderer in an interactive browser (AngleSharp.Browser).
/// </summary>
public sealed class BrowserIntegrationTests
{
    private static DefaultRenderDevice CreateDevice() => new()
    {
        ViewPortWidth = 800,
        ViewPortHeight = 600,
        DeviceWidth = 800,
        DeviceHeight = 600,
        FontSize = 16,
    };

    private static async Task<IDocument> LoadAsync(string html, IConfiguration? configuration = null)
    {
        var context = BrowsingContext.New(configuration ?? Configuration.Default.WithCss().WithRendering().WithRenderDevice(CreateDevice()));
        return await context.OpenAsync(request => request.Content(html));
    }

    private static byte[] Render(IDocument document) => new HtmlRenderer().RenderToPng(document, CreateDevice()).Data;

    [Theory]
    [InlineData("<span style='float: left'>A</span>")]
    [InlineData("<ul><li style='display: inline; float: left'><a href='#'>A</a></li><li style='display: inline; float: left'><a href='#'>B</a></li></ul>")]
    [InlineData("<span style='display: inline-flex; float: right'>A</span>")]
    [InlineData("<span style='display: inline-grid; float: left'>A</span>")]
    [InlineData("<span style='display: inline-table; float: left'>A</span>")]
    [InlineData("<a style='float: left'><img style='float: right' width=10 height=10></a>")]
    [InlineData("<span style='position: absolute; top: 10px'>A</span>")]
    [InlineData("<span style='position: fixed; display: inline-flex'>A</span>")]
    public async Task FloatedAndPositionedInlineBoxesAreBlockified(string html)
    {
        var document = await LoadAsync(html);

        Assert.NotEmpty(Render(document));
    }

    [Fact]
    public async Task FloatedSpanGetsBlockBoxBeforeFollowingText()
    {
        var document = await LoadAsync("<style>body { margin: 0 }</style><div><span id=s style='float: left; width: 100px; height: 20px; background: red'>A</span>Text</div>");

        var rect = document.GetElementById("s")!.GetBoundingClientRect();

        Assert.Equal(100d, rect.Width, 1);
        Assert.Equal(20d, rect.Height, 1);
    }

    private sealed class PngRequester(byte[] png) : BaseRequester
    {
        public override bool SupportsProtocol(string protocol) => true;

        protected override Task<IResponse?> PerformRequestAsync(Request request, CancellationToken cancel) =>
            Task.FromResult<IResponse?>(new DefaultResponse
            {
                Address = request.Address,
                StatusCode = HttpStatusCode.OK,
                Headers = new Dictionary<string, string> { ["Content-Type"] = "image/png" },
                Content = new MemoryStream(png),
            });
    }

    [Fact]
    public async Task LoadedImagesCanBePainted()
    {
        using var bitmap = new SKBitmap(20, 20);
        bitmap.Erase(SKColors.Red);
        var png = bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var configuration = Configuration.Default
            .WithCss()
            .WithRendering()
            .WithRenderDevice(CreateDevice())
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = true })
            .With(new PngRequester(png));
        var document = await LoadAsync("<style>body { margin: 0 }</style><img src='http://localhost/red.png' width=20 height=20>", configuration);
        await document.WaitForReadyAsync();

        var data = Render(document);

        using var rendered = SKBitmap.Decode(data);
        Assert.Equal(SKColors.Red, rendered.GetPixel(10, 10));
    }

    [Fact]
    public async Task LoadedImagesWithoutTheRenderingServicesDoNotFailThePaint()
    {
        using var bitmap = new SKBitmap(20, 20);
        bitmap.Erase(SKColors.Red);
        var png = bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        var configuration = Configuration.Default
            .WithCss()
            .WithRenderDevice(CreateDevice())
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = true })
            .With(new PngRequester(png));
        var document = await LoadAsync("<img src='http://localhost/red.png' width=20 height=20><p>Text</p>", configuration);
        await document.WaitForReadyAsync();

        Assert.NotEmpty(Render(document));
    }

    [Fact]
    public async Task HarnessDoesNotMultiplyPaintCost()
    {
        var rules = string.Concat(Enumerable.Range(0, 2000).Select(i => $".c{i} {{ color: red; margin: {i % 7}px }}\n"));
        var body = string.Concat(Enumerable.Range(0, 150).Select(i => $"<div class='c{i}'><span>Item {i}</span></div>"));
        var html = $"<style>{rules}</style>{body}";

        var plain = await LoadAsync(html);
        var interactive = await LoadAsync(html);
        interactive.Context.GetDomHarness();

        var renderer = new HtmlRenderer();
        renderer.RenderToPng(plain, CreateDevice());
        renderer.RenderToPng(interactive, CreateDevice());
        var watch = Stopwatch.StartNew();
        renderer.RenderToPng(plain, CreateDevice());
        var plainTime = watch.Elapsed;
        watch.Restart();
        renderer.RenderToPng(interactive, CreateDevice());
        var interactiveTime = watch.Elapsed;

        Assert.True(interactiveTime.TotalMilliseconds < plainTime.TotalMilliseconds * 2 + 50,
            $"plain {plainTime.TotalMilliseconds:F0} ms, interactive {interactiveTime.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task SvgBackgroundImage_IsDrawnAtItsNaturalSize()
    {
        // an SVG is rasterized oversampled, which must not make it larger (dev.to's check marks)
        var document = await LoadAsync("""
            <style>body { margin: 0 }</style>
            <div style="width: 40px; height: 40px; background: rgb(255, 0, 0) url(&quot;data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='10'%3E%3Crect width='10' height='10' fill='%2300f'/%3E%3C/svg%3E&quot;) no-repeat"></div>
            """);

        using var rendered = SKBitmap.Decode(Render(document));

        Assert.Equal(new SKColor(0, 0, 255), rendered.GetPixel(5, 5));
        Assert.Equal(new SKColor(255, 0, 0), rendered.GetPixel(15, 15));
    }

    [Fact]
    public async Task HarnessStillAppliesAnimations()
    {
        var document = await LoadAsync("""
            <style>
                body { margin: 0 }
                @keyframes fade { from { background-color: rgb(255, 0, 0) } to { background-color: rgb(0, 0, 255) } }
                #a { width: 50px; height: 50px; background-color: rgb(0, 255, 0); animation: fade 1s linear forwards }
            </style>
            <div id=a></div>
            """);
        var harness = document.Context.GetDomHarness();
        harness.AdvanceTime(TimeSpan.FromSeconds(2));

        using var rendered = SKBitmap.Decode(Render(document));

        Assert.Equal(new SKColor(0, 0, 255), rendered.GetPixel(25, 25));
    }

    [Fact]
    public async Task InputValuePropertyIsPainted()
    {
        var empty = await LoadAsync("<input id=i>");
        var typed = await LoadAsync("<input id=i>");
        ((IHtmlInputElement)typed.GetElementById("i")!).Value = "Hello";

        Assert.NotEqual(Render(empty), Render(typed));
    }

    [Fact]
    public async Task InputValuePropertyBeatsAttribute()
    {
        var attribute = await LoadAsync("<input id=i value=Hello>");
        var property = await LoadAsync("<input id=i value=Hello>");
        ((IHtmlInputElement)property.GetElementById("i")!).Value = "";

        Assert.NotEqual(Render(attribute), Render(property));
    }

    [Fact]
    public async Task CheckedPropertyIsPainted()
    {
        var unchecked_ = await LoadAsync("<input id=c type=checkbox>");
        var checked_ = await LoadAsync("<input id=c type=checkbox>");
        ((IHtmlInputElement)checked_.GetElementById("c")!).IsChecked = true;

        Assert.NotEqual(Render(unchecked_), Render(checked_));
    }

    [Fact]
    public async Task SelectedPropertyIsPainted()
    {
        const string html = "<select id=s><option>First</option><option id=o>Second option</option></select>";
        var first = await LoadAsync(html);
        var second = await LoadAsync(html);
        ((IHtmlOptionElement)second.GetElementById("o")!).IsSelected = true;

        Assert.NotEqual(Render(first), Render(second));
    }

    [Fact]
    public async Task TextAreaValuePropertyIsPainted()
    {
        var empty = await LoadAsync("<textarea id=t></textarea>");
        var typed = await LoadAsync("<textarea id=t></textarea>");
        ((IHtmlTextAreaElement)typed.GetElementById("t")!).Value = "Hello";

        Assert.NotEqual(Render(empty), Render(typed));
    }

    [Fact]
    public async Task BodyPaddingIsApplied()
    {
        var document = await LoadAsync("<style>body { margin: 0; padding: 100px 0 0 50px }</style><p id=p>Text</p>");

        var rect = document.GetElementById("p")!.GetBoundingClientRect();

        Assert.Equal(100d + 16d, rect.Top, 1);
        Assert.Equal(50d, rect.Left, 1);
    }

    [Fact]
    public async Task BodyBoxIsLaidOut()
    {
        var document = await LoadAsync("<style>body { margin: 10px auto; max-width: 400px; padding: 20px; border: 2px solid black }</style><div id=d>Text</div>");

        var body = document.Body!.GetBoundingClientRect();
        var div = document.GetElementById("d")!.GetBoundingClientRect();

        Assert.Equal(444d, body.Width, 1);
        Assert.Equal((800d - 444d) / 2d, body.Left, 1);
        Assert.Equal(10d, body.Top, 1);
        Assert.Equal(400d, div.Width, 1);
        Assert.Equal(body.Left + 22d, div.Left, 1);
        Assert.Equal(32d, div.Top, 1);
    }

    [Fact]
    public async Task RootBackgroundPaintsCanvas()
    {
        var document = await LoadAsync("<style>html { background: rgb(0, 128, 0) }</style><p>Text</p>");

        using var bitmap = SKBitmap.Decode(Render(document));

        Assert.Equal(new SKColor(0, 128, 0), bitmap.GetPixel(2, 590));
    }

    [Fact]
    public async Task BodyBackgroundPropagatesToCanvasWhenRootHasNone()
    {
        var document = await LoadAsync("<style>body { background: rgb(0, 0, 128); height: 10px }</style><p>Text</p>");

        using var bitmap = SKBitmap.Decode(Render(document));

        Assert.Equal(new SKColor(0, 0, 128), bitmap.GetPixel(2, 590));
    }

    [Fact]
    public async Task BodyBackgroundPaintsItsBoxWhenRootHasOne()
    {
        var document = await LoadAsync("<style>html { background: rgb(0, 128, 0) } body { background: rgb(0, 0, 128); margin: 20px; height: 100px }</style>");

        using var bitmap = SKBitmap.Decode(Render(document));

        Assert.Equal(new SKColor(0, 128, 0), bitmap.GetPixel(5, 5));
        Assert.Equal(new SKColor(0, 0, 128), bitmap.GetPixel(50, 50));
        Assert.Equal(new SKColor(0, 128, 0), bitmap.GetPixel(50, 300));
    }

    [Theory]
    [InlineData("200px")]
    [InlineData("12.5em")]
    public async Task MaxWidthWithAutoMarginsCenters(string maxWidth)
    {
        var document = await LoadAsync($"<style>body {{ margin: 0 }}</style><div id=d style='max-width: {maxWidth}; margin: 0 auto'>Text</div>");

        var rect = document.GetElementById("d")!.GetBoundingClientRect();

        Assert.Equal(200d, rect.Width, 1);
        Assert.Equal((800d - 200d) / 2d, rect.Left, 1);
    }

    [Fact]
    public async Task FloatsLineUpHorizontally()
    {
        var document = await LoadAsync("<div id=a style='float: left'>A</div><div id=b style='float: left'>B</div>");

        var a = document.GetElementById("a")!.GetBoundingClientRect();
        var b = document.GetElementById("b")!.GetBoundingClientRect();

        Assert.True(b.Left >= a.Right - 0.5, $"a.Right {a.Right}, b.Left {b.Left}");
        Assert.Equal(a.Top, b.Top, 1);
    }

    [Fact]
    public async Task FloatedInlineListItemsLineUpHorizontally()
    {
        var document = await LoadAsync("<ul><li id=a style='display: inline; float: left'><a href='#'>A</a></li><li id=b style='display: inline; float: left'><a href='#'>B</a></li></ul>");

        var a = document.GetElementById("a")!.GetBoundingClientRect();
        var b = document.GetElementById("b")!.GetBoundingClientRect();

        Assert.True(b.Left >= a.Right - 0.5, $"a.Right {a.Right}, b.Left {b.Left}");
        Assert.Equal(a.Top, b.Top, 1);
    }

    [Fact]
    public async Task PlaceholderIsPainted()
    {
        var without = Render(await LoadAsync("<input>"));
        var with = Render(await LoadAsync("<input placeholder='Your name'>"));

        Assert.NotEqual(without, with);
    }

    [Fact]
    public async Task PlaceholderIsHiddenByValue()
    {
        var value = Render(await LoadAsync("<input value='Hi'>"));
        var both = Render(await LoadAsync("<input value='Hi' placeholder='Your name'>"));

        Assert.Equal(value, both);
    }
}
