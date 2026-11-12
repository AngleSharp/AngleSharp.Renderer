using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// Adjacent text nodes, as scripts and frameworks (e.g. Blazor) insert them, are laid out like
/// the single text node the parser would have made of the same text.
/// </summary>
public sealed class TextRunLayoutTests
{
    private const string Page = """
        <html>
          <head><style>html, body { margin: 0; padding: 0; } p { margin: 0; } td { padding: 0; }</style></head>
          <body>
            <p id="p"></p>
            <table style="border-collapse: collapse"><tr><td id="td"></td></tr></table>
            <div style="display:flex; gap:20px"><span id="flex"></span></div>
            <div style="display:flex; flex-direction:column"><p id="column"></p></div>
            <div id="inline-flex" style="display:inline-flex; gap:20px"></div>
          </body>
        </html>
        """;

    [Theory]
    [InlineData("p")]
    [InlineData("td")]
    [InlineData("flex")]
    [InlineData("column")]
    [InlineData("inline-flex")]
    public async Task AdjacentTextNodes_AreLaidOutAsOneText(string id)
    {
        var single = await LayoutAsync(id, "Picture 4 of 4");
        var split = await LayoutAsync(id, "Picture ", "4", " of 4");

        Assert.Equal(single.Width, split.Width, 3);
        Assert.Equal(single.Height, split.Height, 3);
    }

    [Fact]
    public async Task AdjacentTextNodes_AroundACommentAreOneText()
    {
        var single = await LayoutAsync("p", "Loaded at 10:42");
        var document = await ParseAsync();
        var p = document.GetElementById("p")!;
        p.AppendChild(document.CreateTextNode("Loaded at "));
        p.AppendChild(document.CreateComment("marker"));
        p.AppendChild(document.CreateTextNode("10:42"));
        document.Context.GetDomHarness().PaintToPng();

        Assert.Equal(single.Height, p.GetBoundingClientRect().Height, 3);
    }

    [Fact]
    public async Task AdjacentTextNodes_AfterWhiteSpaceOnlyNodeKeepTheirText()
    {
        var single = await LayoutAsync("p", "Picture 4 of 4");
        var split = await LayoutAsync("p", "  ", "Picture ", "4", " of 4");

        Assert.Equal(single.Height, split.Height, 3);
    }

    private static async Task<(double Width, double Height)> LayoutAsync(string id, params string[] texts)
    {
        var document = await ParseAsync();
        var element = document.GetElementById(id)!;

        foreach (var text in texts)
        {
            element.AppendChild(document.CreateTextNode(text));
        }

        document.Context.GetDomHarness().PaintToPng();
        var rect = element.GetBoundingClientRect();
        return (rect.Width, rect.Height);
    }

    private static async Task<IDocument> ParseAsync()
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice
            {
                ViewPortWidth = 400,
                ViewPortHeight = 300,
                DeviceWidth = 400,
                DeviceHeight = 300,
                FontSize = 16,
            }));
        return await context.OpenAsync(request => request.Content(Page));
    }
}
