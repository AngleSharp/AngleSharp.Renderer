namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Media;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;
using System.Linq;

public sealed class DocumentFontLoadingTests
{
    private static readonly string SerifDataUri = BuildDataUri();

    [Fact]
    public async Task DocumentFonts_ReadySettlesAndReflectsCssFaces()
    {
        var empty = await BrowsingContext.New(Configuration.Default.WithCss())
            .OpenAsync(request => request.Content("<html><body></body></html>"));
        var emptySet = empty.Fonts();

        Assert.Same(emptySet, await emptySet.Ready);
        Assert.Equal("loaded", emptySet.Status);
        Assert.Equal(0, emptySet.Size);
        var style = empty.CreateElement("style");
        style.TextContent = $"@font-face {{ font-family: AddedLater; src: url({SerifDataUri}); }}";
        empty.Head!.Append(style);
        Assert.Equal(1, emptySet.Size);
        Assert.Equal("AddedLater", Assert.Single(emptySet).Family);

        var document = await BrowsingContext.New(Configuration.Default.WithCss())
            .OpenAsync(request => request.Content(
                $"<style>@font-face {{ font-family: Test; font-style: italic; font-weight: 700; font-stretch: condensed; unicode-range: U+0-00FF; font-display: swap; src: url({SerifDataUri}); }}</style>"));
        var set = document.Fonts();

        Assert.Equal(1, set.Size);
        var face = Assert.Single(set);
        Assert.Equal("Test", face.Family);
        Assert.Equal("italic", face.Style);
        Assert.Equal("700", face.Weight);
        Assert.Equal("condensed", face.Stretch);
        Assert.Equal("U+0-00FF", face.UnicodeRange);
        Assert.Equal("swap", face.Display);
        Assert.Equal("loaded", face.Status);
        Assert.True(set.Has(face));
        Assert.True(set.Check("16px Test"));
    }

    [Fact]
    public async Task ScriptFontFace_LoadsFromUrlAndCanBeAddedForLayout()
    {
        var document = await BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(new DefaultRenderDevice
        {
            ViewPortWidth = 480,
            ViewPortHeight = 140,
            FontSize = 20,
        }))
            .OpenAsync(request => request.Content("<html><body><p>Handgloves quick brown fox</p></body></html>"));
        var invalidations = 0;
        document.Context.GetDomHarness().PaintInvalidated += (_, _) => invalidations++;
        document.Body!.SetAttribute("style", "font-family: ScriptFace; font-size: 20px;");
        var original = BuildTextWidth(document);
        var set = document.Fonts();
        var events = new List<string>();
        set.AddEventListener("loading", (_, _) => events.Add("loading"));
        set.AddEventListener("loadingdone", (_, _) => events.Add("loadingdone"));
        var face = new DocumentFontFace(document.DefaultView!, "ScriptFace", $"url({SerifDataUri})", new FontFaceDescriptors
        {
            Style = "oblique",
            Weight = "650",
            Stretch = "condensed",
            UnicodeRange = "U+0-00FF",
            Display = "swap",
        });

        Assert.Equal("unloaded", face.Status);
        Assert.Equal("oblique", face.Style);
        Assert.Equal("650", face.Weight);
        Assert.Equal("condensed", face.Stretch);
        Assert.Equal("U+0-00FF", face.UnicodeRange);
        Assert.Equal("swap", face.Display);
        Assert.Same(face, await face.Load());
        Assert.Equal("loaded", face.Status);

        set.Add(face);
        Assert.True(set.Has(face));
        Assert.True(set.Check("20px ScriptFace"));
        Assert.True(set.Check("20px ScriptFace", "漢"));
        Assert.Empty(await set.Load("20px ScriptFace", "漢"));
        Assert.True(invalidations > 0);
        Assert.Same(set, await set.Ready);
        Assert.Equal(new[] { "loading", "loadingdone" }, events);

        var withFace = BuildTextWidth(document);
        var serif = MeasureWithFamily("serif");
        Assert.Equal(serif, withFace, tolerance: 0.01f);
        Assert.True(Math.Abs(original - withFace) > 0.01f);
    }

    [Fact]
    public async Task ScriptFontFace_LoadsFromBinaryData()
    {
        var bytes = Convert.FromBase64String(SerifDataUri[(SerifDataUri.IndexOf("base64,", StringComparison.Ordinal) + 7)..]);
        var document = await BrowsingContext.New(Configuration.Default.WithCss())
            .OpenAsync(request => request.Content("<html><body></body></html>"));
        var face = new DocumentFontFace(document.DefaultView!, "BinaryFace", bytes);

        await face.Load();

        Assert.Equal("loaded", face.Status);
        document.Fonts().Add(face);
        Assert.True(document.Fonts().Has(face));
    }

    [Fact]
    public async Task FailedFaceLoad_RejectsButResolvesTheSetReadyPromise()
    {
        var document = await BrowsingContext.New(Configuration.Default.WithCss())
            .OpenAsync(request => request.Content("<html><body></body></html>"));
        var set = document.Fonts();
        var events = new List<string>();
        set.AddEventListener("loading", (_, _) => events.Add("loading"));
        set.AddEventListener("loadingerror", (_, _) => events.Add("loadingerror"));
        var face = new DocumentFontFace(document.DefaultView!, "Missing", "url(https://example.invalid/missing.woff2)");

        await Assert.ThrowsAsync<DomException>(async () => await face.Load());
        Assert.Same(set, await set.Ready);
        Assert.Equal("loaded", set.Status);
        Assert.Equal("error", face.Status);
        Assert.Equal(new[] { "loading", "loadingerror" }, events);
    }

    private static float BuildTextWidth(AngleSharp.Dom.IDocument document)
    {
        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 480,
            ViewPortHeight = 140,
            FontSize = 20,
        });
        var command = displayList.Commands.OfType<DrawTextCommand>().First();
        return new SkiaTextMeasurer().MeasureWidth(command.Text,
            new RenderFont(command.FontFamily, command.FontSize, command.FontWeight, command.IsItalic, command.LetterSpacing, displayList.Fonts));
    }

    private static float MeasureWithFamily(string family)
    {
        var measurer = new SkiaTextMeasurer();
        return measurer.MeasureWidth("Handgloves quick brown fox", new RenderFont(family, 20f, 400f, false, 0f));
    }

    private static string BuildDataUri()
    {
        using var stream = typeof(HtmlRenderer).Assembly.GetManifestResourceStream("AngleSharp.Renderer.Resources.Fonts.DejaVuSerif.ttf")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return $"data:font/ttf;base64,{Convert.ToBase64String(buffer.ToArray())}";
    }
}
