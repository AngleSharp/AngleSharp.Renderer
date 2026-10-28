namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

using SkiaSharp;

/// <summary>
/// Text is shaped with HarfBuzz: kerning, ligatures, joining and complex scripts, and the
/// OpenType features CSS selects. Asserted through the measurer (advances and cluster
/// boundaries), which is what layout sees, and through rendered pixels where only the glyphs
/// themselves can tell.
/// </summary>
public sealed class TextShapingTests
{
    private static readonly RenderFont Sans = new("sans-serif", 32f, 400f, false, 0f);

    private readonly SkiaTextMeasurer _measurer = new();

    [Fact]
    public void Kerning_TightensPairsAndCanBeTurnedOff()
    {
        var kerned = _measurer.MeasureWidth("AV", Sans);
        var unkerned = _measurer.MeasureWidth("AV", Sans with { FontFeatures = "kern=0" });
        var separate = _measurer.MeasureWidth("A", Sans) + _measurer.MeasureWidth("V", Sans);

        Assert.True(kerned < unkerned, $"Expected kerning to tighten \"AV\" ({kerned:F2} vs {unkerned:F2}).");
        Assert.Equal(separate, unkerned, tolerance: 0.01f);
    }

    [Theory]
    [InlineData("fi")]
    [InlineData("fl")]
    public void StandardLigatures_FormOneCluster(string text)
    {
        Assert.Equal([0, 2], _measurer.GetClusterBoundaries(text, Sans));
    }

    [Theory]
    [InlineData("liga=0", 0f)]
    [InlineData(null, 2f)]
    public void Ligatures_AreOffWithTheFeatureOrUnderLetterSpacing(string? features, float letterSpacing)
    {
        var font = Sans with { FontFeatures = features, LetterSpacing = letterSpacing };

        Assert.Equal([0, 1, 2], _measurer.GetClusterBoundaries("fi", font));
    }

    [Fact]
    public void Arabic_Joins()
    {
        var font = Sans with { IsRightToLeft = true };

        // A zero-width non-joiner keeps the same letters apart, in their isolated forms.
        var joined = _measurer.MeasureWidth("ببب", font);
        var isolated = _measurer.MeasureWidth("ب‌ب‌ب", font);

        Assert.True(joined < isolated, $"Expected joined beh to be narrower than isolated beh ({joined:F2} vs {isolated:F2}).");
    }

    [Fact]
    public void LamAlef_IsOneLigature()
    {
        Assert.Equal([0, 2], _measurer.GetClusterBoundaries("لا", Sans with { IsRightToLeft = true }));
    }

    [Fact]
    public void Devanagari_FormsConjuncts()
    {
        // "क्ष" (ka, virama, ssa) is one conjunct glyph cluster, narrower than its parts.
        const string conjunct = "क्ष";

        Assert.Equal([0, 3], _measurer.GetClusterBoundaries(conjunct, Sans));
        Assert.True(_measurer.MeasureWidth(conjunct, Sans) < _measurer.MeasureWidth("क", Sans) + _measurer.MeasureWidth("ष", Sans));
    }

    [Fact]
    public void RightToLeftRuns_ArePaintedInVisualOrder()
    {
        // Alef does not join the letter after it, so "اب" is two isolated letters: alef on the
        // right, beh on the left. Painting them one by one, left to right, must give the same image.
        var rtl = Sans with { IsRightToLeft = true };
        var behWidth = _measurer.MeasureWidth("ب", Sans);

        var shaped = Render(list => DrawText(list, "اب", 10f, rtl));
        var manual = Render(list =>
        {
            DrawText(list, "ب", 10f, Sans);
            DrawText(list, "ا", 10f + behWidth, Sans);
        });

        Assert.Equal(manual, shaped);
    }

    [Fact]
    public void RightToLeftRuns_MirrorBrackets()
    {
        var mirrored = Render(list => DrawText(list, "(", 10f, Sans with { IsRightToLeft = true }));
        var closing = Render(list => DrawText(list, ")", 10f, Sans));
        var opening = Render(list => DrawText(list, "(", 10f, Sans));

        Assert.Equal(closing, mirrored);
        Assert.NotEqual(opening, mirrored);
    }

    [Fact]
    public void Ligatures_ArePainted()
    {
        var ligature = Render(list => DrawText(list, "fi", 10f, Sans));
        var separate = Render(list => DrawText(list, "fi", 10f, Sans with { FontFeatures = "liga=0" }));

        Assert.NotEqual(separate, ligature);
    }

    [Theory]
    [InlineData("font-feature-settings:\"liga\" 0", "liga=0")]
    [InlineData("font-feature-settings:\"tnum\", \"zero\" on", "tnum=1,zero=1")]
    [InlineData("font-kerning:none", "kern=0")]
    [InlineData("font-variant-ligatures:no-common-ligatures", "liga=0,clig=0")]
    [InlineData("font-variant-numeric:tabular-nums slashed-zero", "tnum,zero")]
    [InlineData("font-kerning:none; font-feature-settings:\"kern\" 1", "kern=0,kern=1")]
    public async Task FontProperties_SelectOpenTypeFeatures(string declarations, string expected)
    {
        var text = await LayoutSingleTextAsync($"<p style='{declarations}'>Text</p>");

        Assert.Equal(expected, text.FontFeatures);
    }

    // AngleSharp.Css parses `font-variant-east-asian` from 1.3.1 on (verified against the sibling
    // checkout); remove the skip once AngleSharpCssVersion is raised to it.
    [Fact(Skip = "Needs AngleSharp.Css 1.3.1, which parses font-variant-east-asian")]
    public async Task FontVariantEastAsian_SelectsOpenTypeFeatures()
    {
        var text = await LayoutSingleTextAsync("<p style='font-variant-east-asian:jis04 full-width ruby'>Text</p>");

        Assert.Equal("jp04,fwid,ruby", text.FontFeatures);
    }

    [Fact]
    public async Task FontFeatures_AreInherited()
    {
        var text = await LayoutSingleTextAsync("<div style='font-kerning:none'><p><span>Text</span></p></div>");

        Assert.Equal("kern=0", text.FontFeatures);
    }

    [Fact]
    public async Task FontFeatureSettings_Liga0TurnsLigaturesOffInLayout()
    {
        var withLigatures = await LayoutSingleTextAsync("<span style='font-size:32px'>ffi</span>");
        var withoutLigatures = await LayoutSingleTextAsync("<span style='font-size:32px; font-feature-settings:\"liga\" 0'>ffi</span>");

        Assert.Equal([0, 3], _measurer.GetClusterBoundaries("ffi", ToFont(withLigatures)));
        Assert.Equal([0, 1, 2, 3], _measurer.GetClusterBoundaries("ffi", ToFont(withoutLigatures)));
    }

    [Fact]
    public async Task ArabicParagraph_IsOneRightToLeftRunInLogicalOrder()
    {
        const string arabic = "مرحبا بالعالم";
        var text = await LayoutSingleTextAsync($"<p dir='rtl' style='direction:rtl; width:300px'>{arabic}</p>");

        Assert.Equal(arabic, text.Text);
        Assert.True(text.IsRightToLeft);

        // Right-aligned by its shaped width.
        Assert.Equal(300f - _measurer.MeasureWidth(arabic, ToFont(text)), text.X, tolerance: 0.5f);
    }

    [Fact]
    public async Task Ellipsis_CutsAtClusterBoundaries()
    {
        // A measurer that treats "ffi" as one cluster: the cut may fall before it or after it,
        // never inside.
        var measurer = new ClusteringMeasurer("ffi");
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content(
            "<html><body style='margin:0'><p style='width:45px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis'>affix and more</p></body></html>"));

        var text = new HtmlRenderer(new SkiaRenderBackend(), measurer)
            .BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 100, FontSize = 16f })
            .Commands.OfType<DrawTextCommand>()
            .Single();

        Assert.True(text.Text is "a…" or "affi…" or "affix…" or "…", $"Truncated inside a cluster: \"{text.Text}\".");
    }

    [Theory]
    [InlineData("\u0932", "mr")]
    [InlineData("\u091D", "ne")]
    public void Language_SelectsLocalizedForms(string letter, string language)
    {
        // Noto Sans Devanagari has Marathi and Nepali forms of these letters (`locl`).
        Assert.NotEqual(_measurer.MeasureWidth(letter, Sans with { Language = "hi" }), _measurer.MeasureWidth(letter, Sans with { Language = language }));
    }

    [Fact]
    public async Task LangAttribute_IsInheritedByTheText()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content(
            "<html lang='mr'><body style='margin:0'><p>a</p><p lang='ne'><span>b</span></p></body></html>"));

        var texts = new HtmlRenderer()
            .BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200, FontSize = 16f })
            .Commands.OfType<DrawTextCommand>()
            .ToDictionary(command => command.Text, command => command.Language);

        Assert.Equal("mr", texts["a"]);
        Assert.Equal("ne", texts["b"]);
    }

    [Fact]
    public void ShapingContext_GivesTheContextualForm()
    {
        const string beh = "\u0628";
        var medial = Sans with { IsRightToLeft = true, ContextBefore = beh, ContextAfter = beh };

        Assert.True(_measurer.MeasureWidth(beh, medial) < _measurer.MeasureWidth(beh, Sans with { IsRightToLeft = true }));
    }

    [Fact]
    public async Task ArabicWord_JoinsAcrossElementBoundaries()
    {
        const string beh = "\u0628";
        var texts = await LayoutTextsAsync($"<p dir='rtl' style='direction:rtl; width:300px'>{beh}<span style='color:red'>{beh}</span>{beh}</p>");

        Assert.Equal(3, texts.Count);

        var middle = Assert.Single(texts, text => text.Color.R == 255);
        Assert.Equal(beh, middle.ContextBefore);
        Assert.Equal(beh, middle.ContextAfter);

        // The three pieces take exactly the width of the joined word, right-aligned.
        var joined = _measurer.MeasureWidth(beh + beh + beh, Sans with { FontSize = 16f, IsRightToLeft = true });
        Assert.Equal(300f - joined, texts.Min(text => text.X), tolerance: 0.5f);
    }

    [Fact]
    public async Task CanvasFillText_IsShapedAndReordered()
    {
        // "אב 12" in a left-to-right paragraph: the digits stay left to right, left of the Hebrew.
        var reordered = await RenderCanvasAsync(context => context.FillText("\u05d0\u05d1 12", 10f, 40f));
        var manual = await RenderCanvasAsync(context =>
        {
            var font = new RenderFont("sans-serif", 24f, 400f, false, 0f);
            context.FillText("12 ", 10f, 40f);
            context.FillText("\u05d0\u05d1", 10f + _measurer.MeasureWidth("12 ", font), 40f);
        });

        Assert.Equal(manual, reordered);

        var ligature = await RenderCanvasAsync(context => context.FillText("fi", 10f, 40f));
        var shapedAsHtml = Render(list => list.DrawText("fi", 10f, 40f, RenderColor.Black, 24f, "sans-serif"), 100, 60, transparent: true);

        Assert.Equal(shapedAsHtml, ligature);
    }

    [Fact]
    public async Task SvgText_IsReorderedAndAnchoredByItsDirection()
    {
        // Right to left, "אב 12" ends at x (its start edge) and reads, from the left: "12", a
        // space, then the Hebrew. Left to right, "12 אב" has that very visual order, so anchored
        // at its end it has to cover the same pixels.
        var rightToLeft = await RasterizeSvgAsync("<text x='150' y='40' direction='rtl'>\u05d0\u05d1 12</text>");
        var leftToRight = await RasterizeSvgAsync("<text x='150' y='40' text-anchor='end'>12 \u05d0\u05d1</text>");
        var startingAtX = await RasterizeSvgAsync("<text x='150' y='40'>12 \u05d0\u05d1</text>");

        Assert.Equal(leftToRight, rightToLeft);
        Assert.NotEqual(startingAtX, rightToLeft);
        Assert.Contains(rightToLeft, value => value != 0);
    }

    private static RenderFont ToFont(DrawTextCommand command) =>
        new(command.FontFamily, command.FontSize, command.FontWeight, command.IsItalic, command.LetterSpacing, null, command.FontStretch, command.IsRightToLeft, command.FontFeatures);

    private static void DrawText(DisplayList list, string text, float x, RenderFont font) =>
        list.DrawText(text, x, 40f, RenderColor.Black, font.FontSize, font.FontFamily, font.FontWeight, font.IsItalic,
            letterSpacing: font.LetterSpacing, isRightToLeft: font.IsRightToLeft, fontFeatures: font.FontFeatures);

    private static byte[] Render(Action<DisplayList> draw, int width = 120, int height = 60, bool transparent = false)
    {
        var list = new DisplayList();

        if (!transparent)
        {
            list.FillRect(new RenderRect(0f, 0f, width, height), RenderColor.White);
        }

        draw(list);

        var image = new SkiaRenderBackend().RenderToPng(list, new RenderViewport(width, height));
        using var bitmap = SKBitmap.Decode(image.Data);
        return bitmap.Bytes;
    }

    private static async Task<byte[]> RenderCanvasAsync(Action<Canvas2DRenderingContext> draw)
    {
        var browsingContext = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await browsingContext.OpenAsync(request => request.Content("<canvas width='100' height='60'></canvas>"));
        var context = new Canvas2DRenderingContext((AngleSharp.Html.Dom.IHtmlCanvasElement)document.QuerySelector("canvas")!);
        context.SetFont("24px sans-serif");
        draw(context);

        using var bitmap = SKBitmap.Decode(context.ToImage("image/png"));
        return bitmap.Bytes;
    }

    private static async Task<byte[]> RasterizeSvgAsync(string content)
    {
        var browsingContext = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await browsingContext.OpenAsync(request => request.Content(
            $"<html><body style='margin:0'><svg width='200' height='60' font-size='24'>{content}</svg></body></html>"));
        var image = new HtmlRenderer()
            .BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100 })
            .Commands.OfType<DrawImageCommand>()
            .Single();

        using var bitmap = SKBitmap.Decode(image.Image.Data);
        return bitmap.Bytes;
    }

    private static async Task<List<DrawTextCommand>> LayoutTextsAsync(string bodyHtml)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content($"<html><body style='margin:0'>{bodyHtml}</body></html>"));

        return new HtmlRenderer()
            .BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200, FontSize = 16f })
            .Commands.OfType<DrawTextCommand>()
            .ToList();
    }

    private static async Task<DrawTextCommand> LayoutSingleTextAsync(string bodyHtml)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content($"<html><body style='margin:0'>{bodyHtml}</body></html>"));

        return new HtmlRenderer()
            .BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200, FontSize = 16f })
            .Commands.OfType<DrawTextCommand>()
            .Single();
    }

    private sealed class ClusteringMeasurer(string cluster) : ITextMeasurer
    {
        public float MeasureWidth(string text, RenderFont font) => text.Length * 8f;

        public IReadOnlyList<int> GetClusterBoundaries(string text, RenderFont font)
        {
            var boundaries = new List<int> { 0 };
            var index = 0;

            while (index < text.Length)
            {
                index += text.AsSpan(index).StartsWith(cluster) ? cluster.Length : 1;
                boundaries.Add(index);
            }

            return boundaries;
        }
    }
}
