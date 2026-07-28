using AngleSharp;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer.Tests;

public sealed class HtmlRendererTests
{
    [Fact]
    public async Task BuildDisplayList_IncludesBackgroundAndTextCommands()
    {
        var document = await ParseAsync("<html><body><h1>Title</h1><p>Hello renderer world from AngleSharp.</p></body></html>");
        var renderer = new HtmlRenderer();

        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 360,
            Height = 240,
            FontSize = 16f,
        });

        Assert.NotEmpty(displayList.Commands);
        Assert.IsType<FillRectCommand>(displayList.Commands[0]);
        Assert.Contains(displayList.Commands, command => command is DrawTextCommand);
    }

    [Fact]
    public async Task RenderToPng_ReturnsPngPayload()
    {
        var document = await ParseAsync("<html><body><p>PNG smoke test output.</p></body></html>");
        var renderer = new HtmlRenderer();

        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 320,
            Height = 180,
        });

        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Data.Length > 8);
        Assert.Equal(0x89, image.Data[0]);
        Assert.Equal((byte)'P', image.Data[1]);
        Assert.Equal((byte)'N', image.Data[2]);
        Assert.Equal((byte)'G', image.Data[3]);
    }

    [Fact]
    public async Task BuildDisplayList_RendersBoxBackgroundFromPaddingAndMargins()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="margin:10px; padding:5px; width:100px; height:20px; background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 200,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        var backgrounds = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(backgrounds.Length >= 2);

        var boxBackground = backgrounds[1];
        Assert.Equal(10f, boxBackground.Rect.X);
        Assert.Equal(10f, boxBackground.Rect.Y);
        Assert.Equal(110f, boxBackground.Rect.Width);
        Assert.Equal(30f, boxBackground.Rect.Height);
        Assert.Equal(new RenderColor(255, 0, 0), boxBackground.Color);
    }

    [Fact]
    public async Task BuildDisplayList_RendersPerSideBorderWidths()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:20px; border-top-width:2px; border-right-width:3px; border-bottom-width:4px; border-left-width:5px; border-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 200,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 5);

        var top = fills[1];
        var right = fills[2];
        var bottom = fills[3];
        var left = fills[4];

        Assert.Equal(2f, top.Rect.Height);
        Assert.Equal(3f, right.Rect.Width);
        Assert.Equal(4f, bottom.Rect.Height);
        Assert.Equal(5f, left.Rect.Width);

        Assert.Equal(new RenderColor(0, 0, 255), top.Color);
        Assert.Equal(new RenderColor(0, 0, 255), right.Color);
        Assert.Equal(new RenderColor(0, 0, 255), bottom.Color);
        Assert.Equal(new RenderColor(0, 0, 255), left.Color);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(request => request.Content(html));
    }
}