namespace AngleSharp.Renderer.Tests;

using System.Net;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Reduced versions of what the Acid3 test (acid3.acidtests.org) showed to differ from browsers.
/// </summary>
public sealed class Acid3RegressionTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };

    // a 1x1 yellow PNG
    private static readonly byte[] YellowPixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mP4/58BAAT/Af9jgNErAAAAAElFTkSuQmCC");

    [Fact]
    public async Task ObjectWithADocument_HidesItsFallback()
    {
        // test 16: a 404 (even of an image) shows the fallback, a page is shown instead of it
        var document = await OpenAsync("""
            <object id="a" data="http://localhost/missing.png"><object id="b" data="http://localhost/page.png"><object id="c" data="http://localhost/c.png">FAIL</object></object></object>
            """);

        Assert.Equal(300f, Rect(document, "b").Width, precision: 1);
        Assert.Equal(150f, Rect(document, "b").Height, precision: 1);
        Assert.DoesNotContain(new HtmlRenderer().BuildDisplayList(document, Device).Commands, command => command.GetType().Name.Contains("Text"));
    }

    [Fact]
    public async Task ObjectWithAMissingImage_ShowsItsFallback()
    {
        var document = await OpenAsync("""
            <object id="a" data="http://localhost/missing.png"><span id="fallback">fallback</span></object>
            """);

        Assert.True(Rect(document, "fallback").Width > 0f);
    }

    private static RenderRect Rect(IDocument document, string id)
    {
        var rect = document.GetElementById(id)!.GetBoundingClientRect();
        return new RenderRect((float)rect.Left, (float)rect.Top, (float)rect.Width, (float)rect.Height);
    }

    private static async Task<IDocument> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(Device)
            .WithRendering()
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = true })
            .With(new SiteRequester()));
        var document = await context.OpenAsync(request => request.Address("http://localhost/").Content($$"""
            <html><head><style>html, body { margin: 0; padding: 0; font-family: sans-serif }</style></head>
            <body>{{body}}</body></html>
            """));

        // the objects show what they show once their data is there
        foreach (var element in document.QuerySelectorAll("object").OfType<ILoadableElement>())
        {
            if (element.CurrentDownload?.Task is { } task)
            {
                await task;
            }
        }

        await Task.Delay(50);
        context.GetDomHarness();
        return document;
    }

    // what Acid3's server answers
    private sealed class SiteRequester : BaseRequester
    {
        public override bool SupportsProtocol(string protocol) =>
            string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase);

        protected override Task<IResponse?> PerformRequestAsync(Request request, CancellationToken cancel)
        {
            var (status, type, content) = request.Address.Path switch
            {
                "missing.png" => (HttpStatusCode.NotFound, "image/png", YellowPixel),
                "page.png" => (HttpStatusCode.OK, "text/html; charset=utf-8", "<!DOCTYPE html><title>FAIL</title><p><!-- transparent -->"u8.ToArray()),
                _ => (HttpStatusCode.OK, "image/png", YellowPixel),
            };
            var response = new DefaultResponse
            {
                Address = request.Address,
                StatusCode = status,
                Content = new MemoryStream(content),
            };
            response.Headers[HeaderNames.ContentType] = type;
            return Task.FromResult<IResponse?>(response);
        }
    }
}
