namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

using SkiaSharp;

/// <summary>
/// CSS resolves a font-family list left to right and skips entries that are not available. These
/// assertions are expressed through measured advance widths, which is observable on every
/// platform, rather than through the typeface identity, which is not.
/// </summary>
public sealed class FontFallbackTests
{
    private const string Sample = "Handgloves quick brown fox";

    private static readonly SkiaTextMeasurer Measurer = new();

    [Theory]
    [InlineData("DefinitelyMissing, serif")]
    [InlineData("'DefinitelyMissing', serif")]
    [InlineData("\"DefinitelyMissing\", serif")]
    [InlineData("DefinitelyMissing, AlsoMissing, serif")]
    public void MissingFamilies_FallThroughToTheNextEntry(string fontFamily)
    {
        // The unavailable leading entries must be skipped, leaving the declared generic in charge.
        Assert.Equal(Width("serif"), Width(fontFamily), tolerance: 0.01f);
        Assert.True(Math.Abs(Width("sans-serif") - Width(fontFamily)) > 0.01f,
            "Expected the serif fallback to differ from sans-serif.");
    }

    [Fact]
    public void MissingFamily_OnItsOwn_FallsBackToTheDefault()
    {
        // Nothing in the list is available, so the renderer's own default takes over. It has to be
        // that default rather than whatever the host happens to prefer.
        Assert.Equal(Width("sans-serif"), Width("DefinitelyMissing"), tolerance: 0.01f);
    }

    [Fact]
    public void GenericFamilies_AreCaseInsensitive()
    {
        Assert.Equal(Width("serif"), Width("SERIF"), tolerance: 0.01f);
        Assert.Equal(Width("sans-serif"), Width("Sans-Serif"), tolerance: 0.01f);
        Assert.Equal(Width("monospace"), Width("MonoSpace"), tolerance: 0.01f);
    }

    [Fact]
    public void InstalledFamilies_ResolveRegardlessOfCase()
    {
        var family = FindDistinguishableInstalledFamily();

        if (family is null)
        {
            // No installed family renders differently from the bundled default, so there is
            // nothing here that could tell a successful match from a fallback.
            return;
        }

        Assert.Equal(Width(family), Width(family.ToUpperInvariant()), tolerance: 0.01f);
        Assert.Equal(Width(family), Width(family.ToLowerInvariant()), tolerance: 0.01f);
    }

    [Fact]
    public void FallbackOrder_PrefersTheFirstAvailableEntry()
    {
        Assert.Equal(Width("monospace"), Width("monospace, serif"), tolerance: 0.01f);
        Assert.Equal(Width("serif"), Width("serif, monospace"), tolerance: 0.01f);
    }

    [Fact]
    public async Task MissingFamily_RendersIdenticallyToItsFallback()
    {
        // Measuring and painting resolve fonts through the same path, so the pixels have to agree
        // as well - this is what a stale first entry would break.
        var withMissing = await RenderAsync("'DefinitelyMissing', serif");
        var serifOnly = await RenderAsync("serif");
        var sansOnly = await RenderAsync("sans-serif");

        Assert.Equal(serifOnly, withMissing);
        Assert.NotEqual(sansOnly, withMissing);
    }

    private static string? FindDistinguishableInstalledFamily()
    {
        var defaultWidth = Width("sans-serif");

        // Whatever an unknown name resolves to is useless for this test: if the candidate happens
        // to be the host's own substitute font, a failed match is indistinguishable from a hit.
        var substituteWidth = Width("DefinitelyMissing");

        foreach (var family in SKFontManager.Default.FontFamilies)
        {
            if (string.IsNullOrWhiteSpace(family) || family.Contains(',', StringComparison.Ordinal))
            {
                continue;
            }

            // Mixed case only proves something when the family name actually has letters to case.
            if (!family.Any(char.IsLetter))
            {
                continue;
            }

            var width = Width(family);

            if (Math.Abs(width - defaultWidth) < 1f || Math.Abs(width - substituteWidth) < 1f)
            {
                continue;
            }

            return family;
        }

        return null;
    }

    private static float Width(string fontFamily) =>
        Measurer.MeasureWidth(Sample, new RenderFont(fontFamily, 20f, 400f, false, 0f));

    private static async Task<byte[]> RenderAsync(string fontFamily)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body><p style="font-family:{{fontFamily}}; font-size:20px;">{{Sample}}</p></body>
            </html>
            """));

        return new HtmlRenderer()
            .RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 120, FontSize = 20f })
            .Data;
    }
}
