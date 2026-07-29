namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Html.Dom;

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
}
