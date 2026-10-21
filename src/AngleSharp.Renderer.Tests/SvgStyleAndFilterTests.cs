namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

using SkiaSharp;

/// <summary>
/// Inline SVG styled by the page's CSS, and the SVG filter primitives and filter region. Inline SVG
/// is rasterized into an image, so every check samples that image at an SVG user-space point.
/// </summary>
public sealed class SvgStyleAndFilterTests
{
    private static readonly SKColor Green = new(0, 128, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    [Fact]
    public async Task PageCss_StylesSvgElements()
    {
        var pixel = await SampleAsync("""
            <style>.icon rect { fill: rgb(0, 128, 0) }</style>
            <svg class="icon" width="20" height="20"><rect width="20" height="20"/></svg>
            """, 10, 10);

        AssertColor(Green, pixel);
    }

    [Fact]
    public async Task PageCss_BeatsPresentationAttributes()
    {
        var pixel = await SampleAsync("""
            <style>svg rect { fill: rgb(0, 128, 0) }</style>
            <svg width="20" height="20"><rect width="20" height="20" fill="red"/></svg>
            """, 10, 10);

        AssertColor(Green, pixel);
    }

    [Fact]
    public async Task InlineStyle_BeatsPageCss()
    {
        var pixel = await SampleAsync("""
            <style>svg rect { fill: rgb(0, 128, 0) }</style>
            <svg width="20" height="20"><rect width="20" height="20" style="fill: rgb(0, 0, 255)"/></svg>
            """, 10, 10);

        AssertColor(Blue, pixel);
    }

    [Fact]
    public async Task CurrentColor_InheritsTheHtmlColor()
    {
        var pixel = await SampleAsync("""
            <span style="color: rgb(0, 0, 255)"><svg width="20" height="20"><rect width="20" height="20" fill="currentColor"/></svg></span>
            """, 10, 10);

        AssertColor(Blue, pixel);
    }

    [Fact]
    public async Task Fill_InheritsFromHtmlAncestors()
    {
        var pixel = await SampleAsync("""
            <div style="fill: rgb(0, 128, 0)"><svg width="20" height="20"><rect width="20" height="20"/></svg></div>
            """, 10, 10);

        AssertColor(Green, pixel);
    }

    [Fact]
    public async Task InheritedPageCss_DoesNotBeatAChildsOwnAttribute()
    {
        var pixel = await SampleAsync("""
            <style>svg { fill: rgb(0, 128, 0) }</style>
            <span style="color: rgb(0, 0, 255)"><svg width="20" height="20"><rect width="20" height="20" fill="currentColor"/></svg></span>
            """, 10, 10);

        AssertColor(Blue, pixel);
    }

    [Fact]
    public async Task PageCssChanges_ReachAnAlreadyRenderedSvg()
    {
        var document = await OpenAsync("""
            <style>.on rect { fill: rgb(0, 0, 255) }</style>
            <svg id="icon" width="20" height="20"><rect width="20" height="20" fill="rgb(0, 128, 0)"/></svg>
            """);

        AssertColor(Green, Sample(document, 10, 10));
        document.GetElementById("icon")!.ClassList.Add("on");
        AssertColor(Blue, Sample(document, 10, 10));
    }

    [Fact]
    public async Task FeFlood_FillsTheWholeFilterRegion()
    {
        // The circle leaves the corners of its bounding box empty; a flood still fills them.
        var document = await OpenAsync("""
            <svg width="40" height="40">
                <filter id="f" x="0" y="0" width="1" height="1"><feFlood flood-color="rgb(0, 128, 0)"/></filter>
                <circle cx="20" cy="20" r="10" fill="red" filter="url(#f)"/>
            </svg>
            """);

        AssertColor(Green, Sample(document, 11, 11));
        Assert.Equal(0, Sample(document, 5, 5).Alpha);
    }

    [Fact]
    public async Task FilterRegion_ClipsTheResult()
    {
        // A wide blur would spread far beyond the rect; the default region only adds 10%.
        var document = await OpenAsync("""
            <svg width="80" height="40">
                <filter id="f"><feGaussianBlur stdDeviation="8"/></filter>
                <rect x="30" y="10" width="20" height="20" fill="rgb(0, 0, 255)" filter="url(#f)"/>
            </svg>
            """);

        Assert.True(Sample(document, 29, 20).Alpha > 0);
        Assert.Equal(0, Sample(document, 20, 20).Alpha);
    }

    [Fact]
    public async Task FilterRegion_UserSpaceOnUse()
    {
        var document = await OpenAsync("""
            <svg width="40" height="40">
                <filter id="f" filterUnits="userSpaceOnUse" x="0" y="0" width="20" height="40"><feFlood flood-color="rgb(0, 128, 0)"/></filter>
                <rect x="10" y="10" width="20" height="20" fill="red" filter="url(#f)"/>
            </svg>
            """);

        AssertColor(Green, Sample(document, 5, 5));
        Assert.Equal(0, Sample(document, 30, 20).Alpha);
    }

    [Fact]
    public async Task SourceAlpha_IsBlackWithTheSourceAlpha()
    {
        var pixel = await SampleAsync("""
            <svg width="20" height="20">
                <filter id="f"><feOffset in="SourceAlpha" dx="0" dy="0"/></filter>
                <rect width="20" height="20" fill="rgb(255, 0, 0)" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(new SKColor(0, 0, 0), pixel);
    }

    [Fact]
    public async Task FeBlend_Multiplies()
    {
        var pixel = await SampleAsync("""
            <svg width="20" height="20">
                <filter id="f" x="0" y="0" width="1" height="1">
                    <feFlood flood-color="rgb(0, 0, 255)" result="flood"/>
                    <feBlend in="SourceGraphic" in2="flood" mode="multiply"/>
                </filter>
                <rect width="20" height="20" fill="rgb(255, 255, 255)" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(Blue, pixel);
    }

    [Theory]
    [InlineData("type=\"table\" tableValues=\"1 1\"", 255)]
    [InlineData("type=\"discrete\" tableValues=\"0 1\"", 0)]
    [InlineData("type=\"gamma\" amplitude=\"1\" exponent=\"1\" offset=\"0.5\"", 188)]
    [InlineData("type=\"linear\" slope=\"0\" intercept=\"1\"", 255)]
    public async Task FeComponentTransfer_AppliesEveryFunctionType(string function, int expectedRed)
    {
        var pixel = await SampleAsync($"""
            <svg width="20" height="20">
                <filter id="f"><feComponentTransfer><feFuncR {function}/></feComponentTransfer></filter>
                <rect width="20" height="20" fill="rgb(0, 0, 0)" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        Assert.InRange(pixel.Red, expectedRed - 2, expectedRed + 2);
    }

    [Theory]
    [InlineData("", 188)]
    [InlineData("color-interpolation-filters=\"linearRGB\"", 188)]
    [InlineData("color-interpolation-filters=\"sRGB\"", 128)]
    [InlineData("style=\"color-interpolation-filters: sRGB\"", 128)]
    public async Task ColorInterpolationFilters_SelectsTheColorSpace(string attribute, int expectedRed)
    {
        // An offset of 0.5 is mid-grey in whichever space the primitive computes in.
        var pixel = await SampleAsync($"""
            <svg width="20" height="20">
                <filter id="f" {attribute}><feComponentTransfer><feFuncR type="linear" slope="0" intercept="0.5"/></feComponentTransfer></filter>
                <rect width="20" height="20" fill="rgb(0, 0, 0)" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        Assert.InRange(pixel.Red, expectedRed - 2, expectedRed + 2);
    }

    [Theory]
    [InlineData("", 0, 128, 0)]
    [InlineData("color-interpolation-filters=\"sRGB\"", 0, 128, 0)]
    public async Task FeFlood_KeepsItsColorInEitherSpace(string attribute, int red, int green, int blue)
    {
        var pixel = await SampleAsync($"""
            <svg width="20" height="20">
                <filter id="f" x="0" y="0" width="1" height="1" {attribute}><feFlood flood-color="rgb(0, 128, 0)"/></filter>
                <rect width="20" height="20" fill="red" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(new SKColor((byte)red, (byte)green, (byte)blue), pixel);
    }

    [Fact]
    public async Task FeTurbulence_PaintsNoiseOverTheRegion()
    {
        var document = await OpenAsync("""
            <svg width="40" height="40">
                <filter id="f" x="0" y="0" width="1" height="1"><feTurbulence baseFrequency="0.2" numOctaves="2"/></filter>
                <rect width="40" height="40" fill="red" filter="url(#f)"/>
            </svg>
            """);

        var samples = Enumerable.Range(0, 8).Select(i => Sample(document, 4 + (i * 4), 20)).ToList();
        Assert.Contains(samples, sample => sample.Alpha > 0);
        Assert.True(samples.Distinct().Count() > 1, "turbulence painted a uniform color");
        Assert.DoesNotContain(samples, sample => sample == new SKColor(255, 0, 0));
    }

    [Fact]
    public async Task FeDiffuseLighting_LightsWithTheLightingColor()
    {
        var pixel = await SampleAsync("""
            <svg width="20" height="20">
                <filter id="f" x="0" y="0" width="1" height="1">
                    <feDiffuseLighting lighting-color="rgb(0, 0, 255)" surfaceScale="1" diffuseConstant="1">
                        <feDistantLight azimuth="0" elevation="90"/>
                    </feDiffuseLighting>
                </filter>
                <rect width="20" height="20" fill="red" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(Blue, pixel);
    }

    [Fact]
    public async Task FeSpecularLighting_ProducesHighlights()
    {
        var pixel = await SampleAsync("""
            <svg width="20" height="20">
                <filter id="f" x="0" y="0" width="1" height="1">
                    <feSpecularLighting lighting-color="rgb(255, 255, 255)" specularExponent="1" specularConstant="1">
                        <fePointLight x="10" y="10" z="20"/>
                    </feSpecularLighting>
                </filter>
                <rect width="20" height="20" fill="red" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        Assert.True(pixel.Alpha > 0);
    }

    [Fact]
    public async Task FeConvolveMatrix_IdentityKernelKeepsTheImage()
    {
        var pixel = await SampleAsync("""
            <svg width="20" height="20">
                <filter id="f"><feConvolveMatrix order="3" kernelMatrix="0 0 0 0 1 0 0 0 0"/></filter>
                <rect width="20" height="20" fill="rgb(0, 128, 0)" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(Green, pixel);
    }

    [Fact]
    public async Task FeDisplacementMap_Renders()
    {
        var pixel = await SampleAsync("""
            <svg width="20" height="20">
                <filter id="f">
                    <feTurbulence baseFrequency="0.1" result="noise"/>
                    <feDisplacementMap in="SourceGraphic" in2="noise" scale="2" xChannelSelector="R" yChannelSelector="G"/>
                </filter>
                <rect width="20" height="20" fill="rgb(0, 128, 0)" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(Green, pixel);
    }

    [Fact]
    public async Task FeTile_RepeatsItsInputOverTheRegion()
    {
        var document = await OpenAsync("""
            <svg width="40" height="40">
                <filter id="f" x="0" y="0" width="1" height="1">
                    <feFlood x="0" y="0" width="10" height="10" flood-color="rgb(0, 0, 255)" result="tile"/>
                    <feTile in="tile"/>
                </filter>
                <rect width="40" height="40" fill="red" filter="url(#f)"/>
            </svg>
            """);

        AssertColor(Blue, Sample(document, 5, 5));
        AssertColor(Blue, Sample(document, 35, 35));
    }

    [Fact]
    public async Task FeImage_DrawsAReferencedImage()
    {
        var pixel = await SampleAsync($"""
            <svg width="20" height="20">
                <filter id="f" x="0" y="0" width="1" height="1"><feImage href="{GeneratedContentTests.PngDataUri(4, 4)}" preserveAspectRatio="none"/></filter>
                <rect width="20" height="20" fill="rgb(0, 0, 255)" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(new SKColor(200, 30, 30), pixel);
    }

    [Fact]
    public async Task FeImage_DrawsAReferencedElement()
    {
        var pixel = await SampleAsync("""
            <svg width="20" height="20">
                <defs><rect id="source" width="20" height="20" fill="rgb(0, 128, 0)"/></defs>
                <filter id="f" x="0" y="0" width="1" height="1"><feImage href="#source"/></filter>
                <rect width="20" height="20" fill="red" filter="url(#f)"/>
            </svg>
            """, 10, 10);

        AssertColor(Green, pixel);
    }

    private static void AssertColor(SKColor expected, SKColor actual)
    {
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= 3 && Math.Abs(expected.Green - actual.Green) <= 3 &&
            Math.Abs(expected.Blue - actual.Blue) <= 3 && actual.Alpha >= 250,
            $"expected {expected}, got {actual}");
    }

    private static async Task<SKColor> SampleAsync(string body, float x, float y) =>
        Sample(await OpenAsync(body), x, y);

    private static SKColor Sample(IDocument document, float x, float y)
    {
        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200 });
        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        using var bitmap = SKBitmap.Decode(image.Image.Data);
        var scaleX = bitmap.Width / image.Rect.Width;
        var scaleY = bitmap.Height / image.Rect.Height;
        return bitmap.GetPixel((int)((x + 0.5f) * scaleX), (int)((y + 0.5f) * scaleY));
    }

    private static async Task<IDocument> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        return await context.OpenAsync(request => request.Content($"<html><body>{body}</body></html>"));
    }
}
