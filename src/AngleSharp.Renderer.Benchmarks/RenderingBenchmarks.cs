using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using BenchmarkDotNet.Attributes;

namespace AngleSharp.Renderer.Benchmarks;

/// <summary>
/// Baseline performance benchmarks for the renderer against a single, realistic ~1500-element
/// page (see <see cref="BenchmarkFixture"/>) - the starting point for investigating where to spend
/// future optimization effort, not a micro-benchmark of any one feature.
///
/// The HTML document is parsed once in <see cref="Setup"/>, not inside the timed benchmarks
/// themselves - <see cref="BuildDisplayList"/>/<see cref="RenderToPng"/> only ever read the
/// resulting DOM (layout never mutates it), so re-parsing on every iteration would only add noise
/// from AngleSharp's own HTML parser, which is not this project's code to optimize.
/// <see cref="ParseDocument"/> exists separately to give that parse cost its own, honest number
/// rather than silently folding it into the layout benchmarks.
/// </summary>
[MemoryDiagnoser]
public class RenderingBenchmarks
{
    private String _html = String.Empty;
    private IConfiguration _configuration = null!;
    private IDocument _document = null!;
    private IRenderDevice _renderDevice = null!;
    private HtmlRenderer _renderer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var (html, elementCount) = BenchmarkFixture.BuildLargePageHtml();
        _html = html;
        Console.WriteLine($"[BenchmarkFixture] Generated page with ~{elementCount} elements ({_html.Length:N0} chars of HTML).");

        _renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = 1280,
            ViewPortHeight = 6000,
            FontSize = 16,
        };
        // The render device must be attached to the *parsing* configuration, not just passed to
        // BuildDisplayList/RenderToPng - AngleSharp.Css's own em/rem computation reads it off the
        // document's browsing context, independent of whichever IRenderDevice a caller later hands
        // to this renderer's own entry points (mirrors every call site in HtmlRendererTests.cs).
        _configuration = Configuration.Default.WithCss().WithRenderDevice(_renderDevice);
        _renderer = new HtmlRenderer();
        _document = ParseDocumentCore(_html, _configuration);
    }

    [Benchmark(Description = "Parse HTML+CSS into a DOM (AngleSharp's own cost, not this renderer's)")]
    public IDocument ParseDocument()
    {
        return ParseDocumentCore(_html, _configuration);
    }

    [Benchmark(Baseline = true, Description = "Layout only: build the backend-agnostic display list")]
    public DisplayList BuildDisplayList()
    {
        return _renderer.BuildDisplayList(_document, _renderDevice);
    }

    [Benchmark(Description = "Full pipeline: layout + Skia rasterization + PNG encoding")]
    public RenderedImage RenderToPng()
    {
        return _renderer.RenderToPng(_document, _renderDevice);
    }

    private static IDocument ParseDocumentCore(String html, IConfiguration configuration)
    {
        var context = BrowsingContext.New(configuration);
        return context.OpenAsync(request => request.Content(html)).GetAwaiter().GetResult();
    }
}
