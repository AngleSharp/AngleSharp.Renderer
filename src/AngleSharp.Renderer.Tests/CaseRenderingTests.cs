namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using SkiaSharp;

public sealed class CaseRenderingTests
{
    private const int ViewportWidth = 960;
    private const int ViewportHeight = 1420;

    [Fact]
    public async Task RenderCasesAtDesktopViewport()
    {
        var projectRoot = FindProjectRoot();
        var casesPath = Path.Combine(projectRoot, "cases");
        var outputPath = Path.Combine(projectRoot, "rendered-cases");
        Directory.CreateDirectory(outputPath);

        var caseFiles = Directory.GetFiles(casesPath, "*.html")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(caseFiles);

        foreach (var caseFile in caseFiles)
        {
            var html = await File.ReadAllTextAsync(caseFile);
            var renderDevice = new DefaultRenderDevice
            {
                ViewPortWidth = ViewportWidth,
                ViewPortHeight = ViewportHeight,
                FontSize = 16f,
            };
            var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(renderDevice));
            var document = await context.OpenAsync(request => request.Content(html));
            var image = new HtmlRenderer().RenderToPng(document, renderDevice);
            var outputFile = Path.Combine(outputPath, Path.GetFileNameWithoutExtension(caseFile) + ".png");

            await File.WriteAllBytesAsync(outputFile, image.Data);

            using var bitmap = SKBitmap.Decode(image.Data);
            Assert.NotNull(bitmap);
            Assert.Equal(ViewportWidth, bitmap.Width);
            Assert.Equal(ViewportHeight, bitmap.Height);
        }
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