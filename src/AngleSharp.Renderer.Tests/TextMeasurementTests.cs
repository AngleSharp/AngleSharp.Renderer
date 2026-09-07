namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

/// <summary>
/// Layout has to break lines against the advance widths the backend will actually paint with.
/// These assertions are structural on purpose - they hold on every platform, unlike the
/// snapshots, which can only ever check the platform they were recorded on.
/// </summary>
public sealed class TextMeasurementTests
{
    private const string Sentence = "The quick brown fox jumps over the lazy dog and keeps running";

    [Theory]
    [InlineData("serif")]
    [InlineData("sans-serif")]
    [InlineData("monospace")]
    public async Task WrappedLines_StayWithinTheContainer(string fontFamily)
    {
        var lines = await LayoutLinesAsync(fontFamily, containerWidth: 180, fontSize: 14f);
        var measurer = new SkiaTextMeasurer();

        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            var width = measurer.MeasureWidth(line.Text, ToFont(line));

            Assert.True(width <= 180f,
                $"Line \"{line.Text}\" measures {width:F2}px in {fontFamily}, which overflows the 180px container.");
        }
    }

    [Fact]
    public async Task WrappedLines_DependOnTheFontFamily()
    {
        // Monospace is markedly wider than the proportional families at the same size, so it has
        // to break earlier. Identical break points would mean layout is ignoring the font.
        var sans = await LayoutLinesAsync("sans-serif", containerWidth: 180, fontSize: 14f);
        var mono = await LayoutLinesAsync("monospace", containerWidth: 180, fontSize: 14f);

        var sansTexts = sans.Select(line => line.Text).ToArray();
        var monoTexts = mono.Select(line => line.Text).ToArray();

        Assert.NotEqual(sansTexts, monoTexts);
        Assert.True(mono.Count >= sans.Count,
            $"Expected monospace to need at least as many lines as sans-serif, got {mono.Count} vs {sans.Count}.");

        // The break points differ because the advance widths do.
        var measurer = new SkiaTextMeasurer();
        var sansWidth = measurer.MeasureWidth(Sentence, new RenderFont("sans-serif", 14f, 400f, false, 0f));
        var monoWidth = measurer.MeasureWidth(Sentence, new RenderFont("monospace", 14f, 400f, false, 0f));

        Assert.True(monoWidth > sansWidth,
            $"Expected monospace to measure wider than sans-serif, got {monoWidth:F2} vs {sansWidth:F2}.");
    }

    [Fact]
    public async Task CenteredLines_AreOffsetByTheMeasuredWidth()
    {
        const float containerWidth = 180f;
        var lines = await LayoutLinesAsync("serif", containerWidth, fontSize: 14f, textAlign: "center");
        var measurer = new SkiaTextMeasurer();

        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            var width = measurer.MeasureWidth(line.Text, ToFont(line));

            Assert.Equal((containerWidth - width) / 2f, line.X, tolerance: 0.5f);
        }
    }

    [Fact]
    public async Task Layout_UsesTheInjectedMeasurer()
    {
        // A deliberately wrong measurer must still drive the line breaking - that is the proof
        // layout goes through the measurer rather than a built-in heuristic.
        var measurer = new FixedWidthTextMeasurer(widthPerCharacter: 20f);
        var document = await ParseAsync("sans-serif", containerWidth: 100, fontSize: 14f, textAlign: "left");

        var renderer = new HtmlRenderer(new SkiaRenderBackend(), measurer);
        var lines = renderer
            .BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 600, FontSize = 14f })
            .Commands.OfType<DrawTextCommand>()
            .ToArray();

        Assert.True(measurer.CallCount > 0, "Layout never consulted the injected measurer.");
        Assert.NotEmpty(lines);

        // At 20px per character not even the two shortest words fit together into 100px, so every
        // line has to come out as a single word. A word wider than the container still overflows,
        // because nothing here opts into breaking inside a word.
        foreach (var line in lines)
        {
            Assert.DoesNotContain(' ', line.Text);
        }

        // The same document measured with the real font puts several words on a line, so the
        // difference can only come from the injected measurer.
        var realLines = await LayoutLinesAsync("sans-serif", containerWidth: 100, fontSize: 14f);

        Assert.Contains(realLines, line => line.Text.Contains(' '));
    }

    [Fact]
    public void Measurer_AccountsForLetterSpacing()
    {
        var measurer = new SkiaTextMeasurer();
        var plain = new RenderFont("sans-serif", 16f, 400f, false, 0f);
        var spaced = plain with { LetterSpacing = 4f };

        // "Handgloves" has ten characters, so nine gaps of four pixels each.
        Assert.Equal(measurer.MeasureWidth("Handgloves", plain) + 36f, measurer.MeasureWidth("Handgloves", spaced), tolerance: 0.01f);
    }

    [Fact]
    public void Measurer_ReturnsZeroForEmptyText()
    {
        Assert.Equal(0f, new SkiaTextMeasurer().MeasureWidth(string.Empty, new RenderFont("serif", 16f, 400f, false, 0f)));
    }

    private static RenderFont ToFont(DrawTextCommand command) =>
        new(command.FontFamily, command.FontSize, command.FontWeight, command.IsItalic, command.LetterSpacing);

    private static async Task<IReadOnlyList<DrawTextCommand>> LayoutLinesAsync(
        string fontFamily,
        float containerWidth,
        float fontSize,
        string textAlign = "left")
    {
        var document = await ParseAsync(fontFamily, containerWidth, fontSize, textAlign);

        return new HtmlRenderer()
            .BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 600, FontSize = fontSize })
            .Commands.OfType<DrawTextCommand>()
            .ToArray();
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(
        string fontFamily,
        float containerWidth,
        float fontSize,
        string textAlign)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());

        return await context.OpenAsync(request => request.Content($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:{{containerWidth}}px; font-family:{{fontFamily}}; font-size:{{fontSize}}px; text-align:{{textAlign}};">
                  {{Sentence}}
                </div>
              </body>
            </html>
            """));
    }

    private sealed class FixedWidthTextMeasurer(float widthPerCharacter) : ITextMeasurer
    {
        public int CallCount { get; private set; }

        public float MeasureWidth(string text, RenderFont font)
        {
            CallCount++;
            return text.Length * widthPerCharacter;
        }
    }
}
