namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

using SkiaSharp;

/// <summary>
/// Covers <c>@font-face</c> handling. The declared faces are built from the fonts bundled with the
/// renderer, so the expected result can be stated as "this has to measure like the bundled serif",
/// which holds on every platform.
/// </summary>
public sealed class FontFaceTests
{
    private const string Sample = "Handgloves quick brown fox";

    private static readonly string SerifDataUri = BuildDataUri("DejaVuSerif.ttf");
    private static readonly string MonoDataUri = BuildDataUri("DejaVuSansMono.ttf");

    [Fact]
    public async Task EmbeddedFace_IsUsedForTheDeclaredFamily()
    {
        var width = await MeasureAsync(
            $"@font-face {{ font-family: 'Embedded'; src: url({SerifDataUri}) format('truetype'); }}",
            "Embedded");

        Assert.Equal(await MeasureAsync(string.Empty, "serif"), width, tolerance: 0.01f);
        Assert.True(Math.Abs(await MeasureAsync(string.Empty, "sans-serif") - width) > 0.01f,
            "The embedded face measured like the default font, so it was not used.");
    }

    [Fact]
    public async Task EmbeddedFace_TakesPrecedenceOverTheFallbackEntry()
    {
        var width = await MeasureAsync(
            $"@font-face {{ font-family: 'Embedded'; src: url({SerifDataUri}) format('truetype'); }}",
            "'Embedded', monospace");

        Assert.Equal(await MeasureAsync(string.Empty, "serif"), width, tolerance: 0.01f);
    }

    [Theory]
    [InlineData("DejaVuSerif-Latin1.woff", "woff")]
    [InlineData("DejaVuSerif-Latin1.woff2", "woff2")]
    [InlineData("DejaVuSerif-Latin1-hmtx.woff2", "woff2")]
    public async Task WebFontFace_MeasuresLikeTheFontItWraps(string fileName, string format)
    {
        // The fixtures are an ASCII/Latin-1 subset of the bundled DejaVu Serif, saved as TTF,
        // WOFF, and WOFF2 by fontTools (with the transformed glyf/loca tables, and in the -hmtx
        // file also the transformed hmtx table).
        var width = await MeasureAsync(
            $"@font-face {{ font-family: 'Embedded'; src: url({FixtureDataUri(fileName, format)}) format('{format}'); }}",
            "Embedded");

        Assert.Equal(await MeasureAsync(string.Empty, "serif"), width, tolerance: 0.01f);
    }

    [Theory]
    [InlineData("DejaVuSerif-Latin1.woff", "woff")]
    [InlineData("DejaVuSerif-Latin1.woff2", "woff2")]
    [InlineData("DejaVuSerif-Latin1-hmtx.woff2", "woff2")]
    public async Task WebFontFace_RendersTheSameGlyphsAsTheFontItWraps(string fileName, string format)
    {
        // Identical pixels mean the decoded outlines, hinting instructions and metrics match the
        // original font, including the composite glyphs of the accented letters.
        var reference = await RenderSampleAsync(FixtureDataUri("DejaVuSerif-Latin1.ttf", "truetype"), "truetype");
        var decoded = await RenderSampleAsync(FixtureDataUri(fileName, format), format);

        Assert.Equal(reference, decoded);
    }

    [Fact]
    public async Task WebFontFace_KeepsGlyphsBeyondTheShortLocaRange()
    {
        // Source Sans Pro (SIL OFL) declares short loca offsets; its glyphs, rebuilt without the
        // point compression, outgrow them. The glyphs at the end of the table (here "i", a
        // composite, and "+") used to come out empty.
        var html =
            "<html><head><style>" +
            $"@font-face {{ font-family: 'Embedded'; src: url({FixtureDataUri("SourceSansPro-Regular.woff2", "woff2")}) format('woff2'); }}" +
            "body { margin: 0; font-family: 'Embedded'; font-size: 32px; }" +
            "</style></head><body>i+</body></html>";
        var document = await BrowsingContext.New(Configuration.Default.WithCss()).OpenAsync(request => request.Content(html));
        var image = new HtmlRenderer().RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 60, ViewPortHeight = 60 });
        using var bitmap = SKBitmap.Decode(image.Data);
        var inked = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                inked += bitmap.GetPixel(x, y).Red < 128 ? 1 : 0;
            }
        }

        Assert.True(inked > 50, $"Only {inked} pixels were inked, so the glyphs are missing.");
    }

    private static string FixtureDataUri(string fileName, string format) =>
        $"data:font/{format};base64,{Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fonts", fileName)))}";

    private static async Task<byte[]> RenderSampleAsync(string dataUri, string format)
    {
        var html =
            "<html><head><style>" +
            $"@font-face {{ font-family: 'Embedded'; src: url({dataUri}) format('{format}'); }}" +
            "body { margin: 0; font-family: 'Embedded'; font-size: 24px; }" +
            "</style></head><body>Handgloves {}[] àéîõü ÅÇÑØ ß 0123</body></html>";
        var document = await BrowsingContext.New(Configuration.Default.WithCss()).OpenAsync(request => request.Content(html));
        var image = new HtmlRenderer().RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 420, ViewPortHeight = 40 });
        return image.Data;
    }

    [Fact]
    public async Task UnsupportedFormat_FallsThroughToTheNextSource()
    {
        // A woff2 source that cannot be decoded has to be skipped rather than claim the family
        // and leave it unrenderable.
        var width = await MeasureAsync(
            "@font-face { font-family: 'Embedded'; " +
            $"src: url(data:font/woff2;base64,{Convert.ToBase64String("wOF2padding"u8.ToArray())}) format('woff2'), " +
            $"url({SerifDataUri}) format('truetype'); }}",
            "Embedded");

        Assert.Equal(await MeasureAsync(string.Empty, "serif"), width, tolerance: 0.01f);
    }

    [Fact]
    public async Task UnsupportedFormat_WithoutAlternative_FallsBackToTheNextFamily()
    {
        var width = await MeasureAsync(
            "@font-face { font-family: 'Embedded'; " +
            $"src: url(data:font/woff2;base64,{Convert.ToBase64String("wOF2padding"u8.ToArray())}) format('woff2'); }}",
            "'Embedded', monospace");

        Assert.Equal(await MeasureAsync(string.Empty, "monospace"), width, tolerance: 0.01f);
    }

    [Fact]
    public async Task MissingLocalSource_FallsThroughToTheUrlSource()
    {
        // local() is only usable when the family is installed, which is why the choice cannot be
        // made while loading the rule.
        var width = await MeasureAsync(
            $"@font-face {{ font-family: 'Embedded'; src: local('DefinitelyMissing'), url({SerifDataUri}) format('truetype'); }}",
            "Embedded");

        Assert.Equal(await MeasureAsync(string.Empty, "serif"), width, tolerance: 0.01f);
    }

    [Fact]
    public async Task NetworkUrl_IsIgnoredWithoutALoader()
    {
        // Nothing is fetched unless the browsing context was configured to load resources, so the
        // family stays unresolved and the declared fallback takes over.
        var width = await MeasureAsync(
            "@font-face { font-family: 'Web'; src: url(https://example.com/font.ttf) format('truetype'); }",
            "'Web', monospace");

        Assert.Equal(await MeasureAsync(string.Empty, "monospace"), width, tolerance: 0.01f);
    }

    [Fact]
    public async Task Weight_SelectsTheMatchingFace()
    {
        // Two faces under one family, deliberately different files so the choice is observable.
        var css =
            $"@font-face {{ font-family: 'Duo'; font-weight: 400; src: url({SerifDataUri}) format('truetype'); }}" +
            $"@font-face {{ font-family: 'Duo'; font-weight: 700; src: url({MonoDataUri}) format('truetype'); }}";

        Assert.Equal(await MeasureAsync(string.Empty, "serif"), await MeasureAsync(css, "Duo", weight: 400), tolerance: 0.01f);
        Assert.Equal(await MeasureAsync(string.Empty, "monospace"), await MeasureAsync(css, "Duo", weight: 700), tolerance: 0.01f);
    }

    [Fact]
    public async Task Weight_IsSynthesizedForAFaceWithoutABoldWeight()
    {
        // a single face covering all weights, like a variable font (e.g. Inter on MDN)
        var css = $"@font-face {{ font-family: 'Solo'; font-weight: 100 900; src: url({SerifDataUri}) format('truetype'); }}";

        var regular = CountInk(new HtmlRenderer().RenderToPng(await ParseAsync(css, "Solo", 400f), Device()).Data);
        var bold = CountInk(new HtmlRenderer().RenderToPng(await ParseAsync(css, "Solo", 700f), Device()).Data);

        Assert.True(bold > regular * 1.15, $"bold {bold} vs regular {regular}");
    }

    private static int CountInk(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        var count = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                count += pixel.Red + pixel.Green + pixel.Blue < 384 ? 1 : 0;
            }
        }

        return count;
    }

    [Fact]
    public async Task GenericFamilies_CannotBeOverridden()
    {
        // serif is a keyword, not a family name, so a face may not take it over.
        var width = await MeasureAsync(
            $"@font-face {{ font-family: 'serif'; src: url({MonoDataUri}) format('truetype'); }}",
            "serif");

        Assert.Equal(await MeasureAsync(string.Empty, "serif"), width, tolerance: 0.01f);
    }

    [Fact]
    public async Task DeclaredFace_IsExposedOnTheDisplayList()
    {
        var document = await ParseAsync(
            $"@font-face {{ font-family: 'Embedded'; src: local('DefinitelyMissing'), url({SerifDataUri}) format('truetype'); }}",
            "Embedded",
            400f);

        var displayList = new HtmlRenderer().BuildDisplayList(document, Device());

        var face = Assert.Single(displayList.Fonts.Faces);
        Assert.Equal("Embedded", face.Family);
        Assert.Equal(400f, face.Weight);
        Assert.False(face.IsItalic);
        Assert.Collection(face.Sources,
            source => Assert.Equal("DefinitelyMissing", source.LocalFamily),
            source => Assert.NotNull(source.Data));
    }

    [Fact]
    public async Task EmbeddedFace_RendersTheSamePixelsAsTheEquivalentGeneric()
    {
        var embedded = await RenderAsync(
            $"@font-face {{ font-family: 'Embedded'; src: url({SerifDataUri}) format('truetype'); }}",
            "Embedded");
        var serif = await RenderAsync(string.Empty, "serif");

        Assert.Equal(serif, embedded);
    }

    [Fact]
    public void FontFaceSet_Empty_MatchesNothing()
    {
        Assert.True(FontFaceSet.Empty.IsEmpty);
        Assert.False(FontFaceSet.Empty.TryMatch("anything", 400f, false, out _));
    }

    [Fact]
    public void FontFaceSet_PrefersAMatchingSlantOverACloserWeight()
    {
        var upright = new FontFace("F", 400f, false, [FontFaceSource.FromLocal("A")]);
        var italic = new FontFace("F", 900f, true, [FontFaceSource.FromLocal("B")]);
        var set = new FontFaceSet([upright, italic]);

        Assert.True(set.TryMatch("F", 400f, isItalic: true, out var matched));
        Assert.Same(italic, matched);

        Assert.True(set.TryMatch("f", 400f, isItalic: false, out var uprightMatch));
        Assert.Same(upright, uprightMatch);
    }

    private static DefaultRenderDevice Device() =>
        new() { ViewPortWidth = 480, ViewPortHeight = 140, FontSize = 20f };

    private static async Task<float> MeasureAsync(string css, string fontFamily, float weight = 400f)
    {
        var document = await ParseAsync(css, fontFamily, weight);
        var displayList = new HtmlRenderer().BuildDisplayList(document, Device());
        var command = displayList.Commands.OfType<DrawTextCommand>().First();

        return new SkiaTextMeasurer().MeasureWidth(
            command.Text,
            new RenderFont(command.FontFamily, command.FontSize, command.FontWeight, command.IsItalic, command.LetterSpacing, displayList.Fonts));
    }

    private static async Task<byte[]> RenderAsync(string css, string fontFamily) =>
        new HtmlRenderer().RenderToPng(await ParseAsync(css, fontFamily, 400f), Device()).Data;

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string css, string fontFamily, float weight)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());

        return await context.OpenAsync(request => request.Content($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; } {{css}}</style></head>
              <body><p style="font-family:{{fontFamily}}; font-weight:{{weight}}; font-size:20px;">{{Sample}}</p></body>
            </html>
            """));
    }

    private static string BuildDataUri(string fontFileName)
    {
        var assembly = typeof(HtmlRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream($"AngleSharp.Renderer.Resources.Fonts.{fontFileName}")
            ?? throw new InvalidOperationException($"Bundled font not found: {fontFileName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return $"data:font/ttf;base64,{Convert.ToBase64String(buffer.ToArray())}";
    }
}
