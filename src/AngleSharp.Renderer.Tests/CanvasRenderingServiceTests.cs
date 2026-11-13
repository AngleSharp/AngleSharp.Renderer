namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Scripting;

public sealed class CanvasRenderingServiceTests
{
    [Fact]
    public async Task GetContext_ProvidesA2DRenderingContextForCanvasElements()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering());
        var document = await context.OpenAsync(request => request.Content("<html><body><canvas width='100' height='80'></canvas></body></html>"));

        var canvas = document.QuerySelector("canvas") as IHtmlCanvasElement;

        Assert.NotNull(canvas);

        var renderingContext = canvas!.GetContext("2d");

        Assert.NotNull(renderingContext);
        Assert.Equal("2d", renderingContext.ContextId);
        Assert.False(renderingContext.IsFixed);
        Assert.NotEmpty(renderingContext.ToImage("image/png"));
    }

    [Fact]
    public async Task Page_ShowsTheBitmapOfACanvasInsteadOfItsFallback()
    {
        var (document, harness) = await OpenScriptedAsync("<canvas id='c' width='40' height='30'><p id='fallback'>No canvas</p></canvas>");
        var canvas = (IHtmlCanvasElement)document.GetElementById("c")!;
        var context = Assert.IsType<Canvas2DRenderingContext>(canvas.GetContext("2d"));
        context.FillStyle = "#ff0000";
        context.FillRect(0d, 0d, 40d, 30d);

        var frame = harness.PrepareFrame();

        Assert.Equal(new RenderColor(255, 0, 0), GetPixel(frame.Rasterize(), 20, 15));
        Assert.Equal(new RenderColor(255, 255, 255), GetPixel(frame.Rasterize(), 60, 15));
        Assert.Equal(40d, canvas.GetBoundingClientRect().Width);
        Assert.Equal(0d, document.GetElementById("fallback")!.GetBoundingClientRect().Height);
    }

    [Fact]
    public async Task Page_WithoutScripting_ShowsTheFallbackOfACanvas()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content("<html><body style='margin:0'><canvas width='40' height='30'><p id='fallback'>No canvas</p></canvas></body></html>"));
        context.GetDomHarness();

        Assert.True(document.GetElementById("fallback")!.GetBoundingClientRect().Height > 0d);
    }

    [Fact]
    public async Task DrawingIntoACanvas_IsShownByRasterizingTheSameFrameAgain()
    {
        var (document, harness) = await OpenScriptedAsync("<canvas id='c' width='40' height='30'></canvas>");
        var canvas = (IHtmlCanvasElement)document.GetElementById("c")!;
        var context = Assert.IsType<Canvas2DRenderingContext>(canvas.GetContext("2d"));
        var frame = harness.PrepareFrame();
        var invalidated = 0;
        harness.PaintInvalidated += (_, _) => invalidated++;

        context.FillStyle = "#0000ff";
        context.FillRect(0d, 0d, 40d, 30d);
        context.FillRect(0d, 0d, 10d, 10d);

        Assert.Equal(1, invalidated);
        Assert.Same(frame, harness.UpdateFrame());
        Assert.Equal(new RenderColor(0, 0, 255), GetPixel(frame.Rasterize(), 20, 15));

        context.FillStyle = "#00ff00";
        context.FillRect(0d, 0d, 40d, 30d);

        Assert.Equal(2, invalidated);
        Assert.Equal(new RenderColor(0, 255, 0), GetPixel(frame.Rasterize(), 20, 15));
    }

    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenScriptedAsync(string body)
    {
        var configuration = Configuration.Default.WithCss().WithRendering().WithRenderDevice(Device).With(new InertScripting());
        var context = BrowsingContext.New(configuration);
        var document = await context.OpenAsync(request => request.Content($"<html><body style='margin:0'>{body}</body></html>"));
        return (document, context.GetDomHarness());
    }

    private static RenderColor GetPixel(RenderedPixels pixels, int x, int y)
    {
        var offset = ((y * pixels.Width) + x) * 4;
        return new RenderColor(pixels.Data[offset], pixels.Data[offset + 1], pixels.Data[offset + 2], pixels.Data[offset + 3]);
    }

    // A canvas shows its bitmap where there is scripting.
    private sealed class InertScripting : IScriptingService
    {
        public bool SupportsType(string mimeType) => false;

        public Task EvaluateScriptAsync(IResponse response, ScriptOptions options, CancellationToken cancel) => Task.CompletedTask;
    }
}
