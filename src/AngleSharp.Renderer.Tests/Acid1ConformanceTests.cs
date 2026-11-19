namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// The Acid1 test (<c>cases/test15-acid1.html</c>, the CSS1 test "display/box/float/clear"):
/// every box above the paragraph has a size fixed in ems, so the reference rendering
/// (sec5526c.gif) pins each of them to the pixel.
/// </summary>
[Trait("Category", "Visual")]
public sealed class Acid1ConformanceTests
{
    private const int ViewportWidth = 960;
    private const int ViewportHeight = 1420;

    [Fact]
    public async Task Acid1_LaysOutTheBoxesOfTheReferenceRendering()
    {
        var document = await OpenAsync();

        // html at 10px: body has a 15px margin and a 5px border, the dl a 5px padding
        var body = Box(document, "body");
        Assert.Equal(15f, body.X, precision: 1);
        Assert.Equal(15f, body.Y, precision: 1);
        Assert.Equal(490f, body.Width, precision: 1);
        AssertBox(document, "dt", 25f, 25f, 80f, 310f);
        AssertBox(document, "dd", 115f, 25f, 380f, 310f);
        AssertBox(document, "li:nth-child(1)", 135f, 45f, 80f, 120f);
        AssertBox(document, "#bar", 225f, 45f, 160f, 110f);
        AssertBox(document, "li:nth-child(3)", 395f, 45f, 80f, 120f);
        // below "i grow old" (CSS 1 float rule 2), not beside it under #bar
        AssertBox(document, "#baz", 135f, 175f, 120f, 120f);
        AssertBox(document, "blockquote", 275f, 175f, 70f, 140f);
        AssertBox(document, "h1", 355f, 175f, 120f, 120f);

        // the paragraph clears both floats
        var paragraph = Box(document, "body > p");
        Assert.Equal(20f, paragraph.X, precision: 1);
        Assert.Equal(335f, paragraph.Y, precision: 1);
        Assert.Equal(480f, paragraph.Width, precision: 1);
    }

    [Fact]
    public async Task Acid1_MatchesTheSnapshot()
    {
        var document = await OpenAsync();
        var image = new HtmlRenderer().RenderToPng(document, CreateDevice());

        VisualSnapshotVerifier.VerifyOrCreate(
            snapshotName: "acid1.png",
            actualPng: image.Data,
            perChannelTolerance: 12,
            maxDifferentPixels: 20);
    }

    private static void AssertBox(IDocument document, string selector, float x, float y, float width, float height)
    {
        var box = Box(document, selector);
        Assert.Equal(x, box.X, precision: 1);
        Assert.Equal(y, box.Y, precision: 1);
        Assert.Equal(width, box.Width, precision: 1);
        Assert.Equal(height, box.Height, precision: 1);
    }

    private static RenderRect Box(IDocument document, string selector)
    {
        var rect = document.QuerySelector(selector)!.GetBoundingClientRect();
        return new RenderRect((float)rect.Left, (float)rect.Top, (float)rect.Width, (float)rect.Height);
    }

    private static DefaultRenderDevice CreateDevice() => new()
    {
        ViewPortWidth = ViewportWidth,
        ViewPortHeight = ViewportHeight,
        FontSize = 16f,
    };

    private static async Task<IDocument> OpenAsync()
    {
        var html = await File.ReadAllTextAsync(Path.Combine(FindProjectRoot(), "cases", "test15-acid1.html"));
        // Verdana is installed on some hosts only; snapshots use the bundled fonts
        html = html.Replace("Verdana, sans-serif", "sans-serif", StringComparison.Ordinal);

        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(CreateDevice()));
        var document = await context.OpenAsync(request => request.Content(html));
        context.GetDomHarness();
        return document;
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AngleSharp.Renderer.Tests.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate AngleSharp.Renderer.Tests project root.");
    }
}
