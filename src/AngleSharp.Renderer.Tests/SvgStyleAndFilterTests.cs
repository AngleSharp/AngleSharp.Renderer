namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;
using AngleSharp.Renderer.Skia;

using System.Reflection;
using System.Runtime.CompilerServices;

using SkiaSharp;

/// <summary>
/// Inline SVG styled by the page's CSS, and the SVG filter primitives and filter region. Inline SVG
/// is rasterized into an image, so every check samples that image at an SVG user-space point.
/// </summary>
public sealed class SvgStyleAndFilterTests
{
    private static readonly SKColor Green = new(0, 128, 0);
    private static readonly SKColor Blue = new(0, 0, 255);
    private static readonly SKColor Gray = new(128, 128, 128);

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
    public async Task PageCss_WithCustomPropertiesStylesSvgElements()
    {
        // found on MDN: the logo's paths are filled with var(--color-text-primary)
        var pixel = await SampleAsync("""
            <style>:root { --brand: rgb(0, 128, 0) } .icon rect { fill: var(--brand) }</style>
            <svg class="icon" width="20" height="20"><rect width="20" height="20" fill="red"/></svg>
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
    public async Task PageCss_StylesSvgsLaidOutRepeatedly()
    {
        // flex items are measured before they are laid out, which resolves an SVG more than once per pass
        var document = await OpenAsync("""
            <style>.icon rect { fill: rgb(0, 128, 0) } .on .icon rect { fill: rgb(0, 0, 255) }</style>
            <div id="bar" style="display: flex"><span><svg class="icon" width="20" height="20"><rect width="20" height="20"/></svg></span></div>
            """);

        AssertColor(Green, Sample(document, 10, 10));
        document.GetElementById("bar")!.ClassList.Add("on");
        AssertColor(Blue, Sample(document, 10, 10));
    }

    [Fact]
    public async Task PageCss_StylesEachOfSeveralIdenticalSvgs()
    {
        var document = await OpenAsync("""
            <style>.a rect { fill: rgb(0, 128, 0) } .b rect { fill: rgb(0, 0, 255) }</style>
            <div style="display: flex">
            <span class="a"><svg width="20" height="20"><rect width="20" height="20"/></svg></span>
            <span class="b"><svg width="20" height="20"><rect width="20" height="20"/></svg></span>
            </div>
            """);
        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200 });
        var images = displayList.Commands.OfType<DrawImageCommand>().OrderBy(command => command.Rect.X).ToArray();

        Assert.Equal(2, images.Length);
        AssertColor(Green, Center(images[0]));
        AssertColor(Blue, Center(images[1]));
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

    // A black left half: scaled onto a whole number of pixels its edge falls between two pixels, so
    // painted as a vector it is perfectly sharp, while a scaled raster smears it over several.
    private const string LeftHalf = """<rect width="10" height="20" fill="black"/>""";

    // Scaled to 16px its edge is in the middle of a pixel, which only painting the shape covers
    // right: a nearest sample of a raster is sharp, but has no half covered pixel.
    private const string HalfPixelEdge = """<rect width="10.625" height="20" fill="black"/>""";

    [Fact]
    public async Task ImageSvg_ScaledFarDown_IsPaintedAtTheTargetResolution()
    {
        // found on speicher-kraftwerk.de: an 800px icon in a 16px box blurred
        var bitmap = await RenderSvgImageAsync($"""<svg xmlns="http://www.w3.org/2000/svg" width="800" height="800" viewBox="0 0 20 20">{LeftHalf}</svg>""", "width: 16px; height: 16px");

        AssertColor(SKColors.Black, bitmap.GetPixel(7, 8));
        AssertColor(SKColors.White, bitmap.GetPixel(8, 8));
    }

    [Fact]
    public async Task ImageSvg_ScaledUp_StaysSharp()
    {
        var bitmap = await RenderSvgImageAsync($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">{LeftHalf}</svg>""", "width: 400px; height: 400px");

        AssertColor(SKColors.Black, bitmap.GetPixel(199, 200));
        AssertColor(SKColors.White, bitmap.GetPixel(200, 200));
    }

    [Fact]
    public async Task ImageSvg_UnderATransform_StaysSharp()
    {
        var bitmap = await RenderSvgImageAsync($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">{LeftHalf}</svg>""", "width: 20px; height: 20px; transform: scale(10); transform-origin: 0 0");

        AssertColor(SKColors.Black, bitmap.GetPixel(99, 100));
        AssertColor(SKColors.White, bitmap.GetPixel(100, 100));
    }

    [Fact]
    public async Task ImageSvg_NeverPaintsOutsideItsBox()
    {
        var bitmap = await RenderSvgImageAsync("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><rect x="-50" y="-50" width="200" height="200" fill="black"/></svg>""", "width: 40px; height: 40px; margin: 10px");

        AssertColor(SKColors.White, bitmap.GetPixel(9, 30));
        AssertColor(SKColors.Black, bitmap.GetPixel(10, 30));
        AssertColor(SKColors.Black, bitmap.GetPixel(49, 30));
        AssertColor(SKColors.White, bitmap.GetPixel(50, 30));
    }

    [Fact]
    public async Task InlineSvg_ScaledByCss_IsPaintedAtTheTargetResolution()
    {
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <svg style="display: block; width: 16px; height: 16px" width="800" height="800" viewBox="0 0 20 20">{{LeftHalf}}</svg>
            """);
        using var bitmap = Render(document);

        AssertColor(SKColors.Black, bitmap.GetPixel(7, 8));
        AssertColor(SKColors.White, bitmap.GetPixel(8, 8));
    }

    [Fact]
    public async Task ImageSvg_AtSeveralSizes_IsSharpAtEach()
    {
        var source = EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="800" height="800" viewBox="0 0 20 20">{LeftHalf}</svg>""");
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white } img { display: block }</style>
            <img style="width: 16px; height: 16px" src="{{source}}">
            <img style="width: 64px; height: 64px" src="{{source}}">
            <img style="width: 16px; height: 16px" src="{{source}}">
            """);
        using var bitmap = Render(document);

        AssertColor(SKColors.Black, bitmap.GetPixel(7, 8));
        AssertColor(SKColors.White, bitmap.GetPixel(8, 8));
        AssertColor(SKColors.Black, bitmap.GetPixel(31, 40));
        AssertColor(SKColors.White, bitmap.GetPixel(32, 40));
        AssertColor(SKColors.Black, bitmap.GetPixel(7, 88));
        AssertColor(SKColors.White, bitmap.GetPixel(8, 88));
    }

    [Fact]
    public async Task ImageSvg_AtAFractionalPosition_CoversPixelsPartly()
    {
        var bitmap = await RenderSvgImageAsync($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">{LeftHalf}</svg>""", "position: absolute; left: 0.5px; top: 0; width: 20px; height: 20px");

        AssertColor(SKColors.Black, bitmap.GetPixel(9, 10));
        AssertColor(Gray, bitmap.GetPixel(10, 10));
        AssertColor(SKColors.White, bitmap.GetPixel(11, 10));
    }

    [Fact]
    public async Task ImageSvg_Rotated_IsPaintedInPlace()
    {
        // a quarter turn around the center: the black half is on top
        var bitmap = await RenderSvgImageAsync($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">{LeftHalf}</svg>""", "width: 40px; height: 40px; transform: rotate(90deg)");

        AssertColor(SKColors.Black, bitmap.GetPixel(20, 19));
        AssertColor(SKColors.White, bitmap.GetPixel(20, 20));
    }

    [Fact]
    public async Task ImageSvg_PaintedAgain_IsTheSame()
    {
        var source = EncodeSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><circle cx="10" cy="10" r="7"/></svg>""");
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <img style="display: block; width: 33px; height: 33px" src="{{source}}">
            """);
        using var first = Render(document);
        using var second = Render(document);

        Assert.Equal(first.Bytes, second.Bytes);
    }

    [Fact]
    public async Task ImageSvg_PayloadIsAPngOfTheImage()
    {
        var document = await OpenAsync($$"""<img src="{{EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">{LeftHalf}</svg>""")}}">""");
        var device = new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };
        var image = new HtmlRenderer().BuildDisplayList(document, device).Commands.OfType<DrawImageCommand>().Single();
        using var payload = SKBitmap.Decode(image.Image.Data);

        Assert.Equal("image/png", image.Image.MimeType);
        Assert.Equal(60, payload.Width);
        AssertColor(SKColors.Black, payload.GetPixel(29, 30));
        Assert.Equal(0, payload.GetPixel(30, 30).Alpha);
    }

    [Theory]
    [InlineData("no-repeat", 16, 16)] // the image shader, Decal
    [InlineData("repeat", 32, 16)]    // the image shader, Repeat
    [InlineData("round", 32, 16)]     // the tiled paint
    public async Task BackgroundSvg_ScaledFarDown_IsPaintedAtTheTargetResolution(string repeat, int width, int height)
    {
        var source = EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="800" height="800" viewBox="0 0 20 20">{HalfPixelEdge}</svg>""");
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <div style="width: {{width}}px; height: {{height}}px; background: url({{source}}) {{repeat}}; background-size: 16px 16px"></div>
            """);
        using var bitmap = Render(document);

        AssertColor(SKColors.Black, bitmap.GetPixel(7, 8));
        AssertColor(Gray, bitmap.GetPixel(8, 8));
        AssertColor(SKColors.White, bitmap.GetPixel(9, 8));

        if (width > 16)
        {
            AssertColor(SKColors.Black, bitmap.GetPixel(23, 8));
            AssertColor(Gray, bitmap.GetPixel(24, 8));
            AssertColor(SKColors.White, bitmap.GetPixel(25, 8));
        }
    }

    [Fact]
    public async Task BackgroundSvg_ScaledUp_StaysSharp()
    {
        var source = EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><rect width="10.025" height="20" fill="black"/></svg>""");
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <div style="width: 400px; height: 400px; background: url({{source}}) no-repeat; background-size: cover"></div>
            """);
        using var bitmap = Render(document);

        AssertColor(SKColors.Black, bitmap.GetPixel(199, 200));
        AssertColor(Gray, bitmap.GetPixel(200, 200));
        AssertColor(SKColors.White, bitmap.GetPixel(201, 200));
    }

    [Fact]
    public async Task BackgroundSvg_AtAFractionalSize_CoversPixelsPartly()
    {
        var source = EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="800" height="800" viewBox="0 0 20 20">{LeftHalf}</svg>""");
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <div style="width: 40px; height: 40px; background: url({{source}}) no-repeat; background-size: 17px 17px"></div>
            """);
        using var bitmap = Render(document);

        AssertColor(SKColors.Black, bitmap.GetPixel(7, 8));
        AssertColor(Gray, bitmap.GetPixel(8, 8));
        AssertColor(SKColors.White, bitmap.GetPixel(9, 8));
    }

    [Fact]
    public async Task MaskSvg_ScaledFarDown_IsPaintedAtTheTargetResolution()
    {
        var source = EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="800" height="800" viewBox="0 0 20 20">{HalfPixelEdge}</svg>""");
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <div style="width: 16px; height: 16px; background: rgb(0, 128, 0); mask-image: url({{source}}); mask-size: 16px 16px; mask-repeat: no-repeat"></div>
            """);
        using var bitmap = Render(document);

        AssertColor(Green, bitmap.GetPixel(7, 8));
        AssertColor(new SKColor(127, 191, 127), bitmap.GetPixel(8, 8));
        AssertColor(SKColors.White, bitmap.GetPixel(9, 8));
    }

    [Fact]
    public async Task BorderImageSvg_SlicesCountInItsNaturalSize()
    {
        // a red corner of 10 by 10 in an otherwise blue image of 30 by 30
        var source = EncodeSvg("""<svg xmlns="http://www.w3.org/2000/svg" width="30" height="30"><rect width="30" height="30" fill="rgb(0, 0, 255)"/><rect width="10" height="10" fill="red"/></svg>""");
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <div style="box-sizing: border-box; width: 50px; height: 50px; border: 10px solid; border-image: url({{source}}) 10 / 10px"></div>
            """);
        using var bitmap = Render(document);

        AssertColor(SKColors.Red, bitmap.GetPixel(5, 5));
        AssertColor(Blue, bitmap.GetPixel(12, 5));
        AssertColor(Blue, bitmap.GetPixel(45, 5));
    }

    [Fact]
    public async Task ImageSvg_RastersOfAllImagesStayWithinTheBudget()
    {
        // 16 MB each, kept alive by their documents: more than the budget holds
        var documents = new List<IDocument>();

        for (var index = 0; index < 12; index++)
        {
            var document = await OpenAsync($$"""
                <style>html, body { margin: 0; background: white }</style>
                <img style="display: block; width: 1024px; height: 4096px" src="{{EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" data-index="{index}">{LeftHalf}</svg>""")}}">
                """);
            documents.Add(document);
            using var bitmap = Render(document);

            Assert.InRange(ReadRasterBudget().Used, 1L, ReadRasterBudget().Limit);
            AssertColor(SKColors.Black, bitmap.GetPixel(10, 100));
            AssertColor(SKColors.White, bitmap.GetPixel(600, 100));
        }

        // the raster of the first one is long gone, and it is simply rendered again
        using var again = Render(documents[0]);

        AssertColor(SKColors.Black, again.GetPixel(10, 100));
        AssertColor(SKColors.White, again.GetPixel(600, 100));
        Assert.InRange(ReadRasterBudget().Used, 1L, ReadRasterBudget().Limit);
    }

    [Fact]
    public async Task ImageSvg_IsNotKeptAliveByTheRasterBudget()
    {
        var image = await PaintAndForgetAsync();

        for (var attempt = 0; attempt < 5 && image.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(image.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> PaintAndForgetAsync()
    {
        var document = await OpenAsync($$"""<img src="{{EncodeSvg($"""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">{LeftHalf}</svg>""")}}">""");
        var device = new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };
        var list = new HtmlRenderer().BuildDisplayList(document, device);
        new SkiaRenderBackend().RenderToPixels(list, new RenderViewport(800, 600));
        var image = list.Commands.OfType<DrawImageCommand>().Single().Image;
        var vectorImage = typeof(RenderedImage).GetProperty("VectorImage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(image);

        Assert.NotNull(vectorImage);
        return new WeakReference(vectorImage);
    }

    // The books of the budget cannot be seen from the outside, as they are not in the pixels.
    private static (long Used, long Limit) ReadRasterBudget()
    {
        var budget = typeof(RenderedImage).Assembly.GetType("AngleSharp.Renderer.Skia.SvgRasterBudget")!;
        var used = (long)budget.GetProperty("UsedBytes", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        var limit = (long)budget.GetField("LimitBytes", BindingFlags.Static | BindingFlags.Public)!.GetValue(null)!;
        return (used, limit);
    }

    private static string EncodeSvg(string svg) =>
        $"data:image/svg+xml;base64,{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg))}";

    private static async Task<SKBitmap> RenderSvgImageAsync(string svg, string style)
    {
        var source = EncodeSvg(svg);
        var document = await OpenAsync($$"""
            <style>html, body { margin: 0; background: white }</style>
            <img style="display: block; {{style}}" src="{{source}}">
            """);

        return Render(document);
    }

    private static SKBitmap Render(IDocument document)
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };
        return SKBitmap.Decode(new HtmlRenderer().RenderToPng(document, device).Data.ToArray());
    }

    private static SKColor Center(DrawImageCommand image)
    {
        using var bitmap = SKBitmap.Decode(image.Image.Data);
        return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    }

    private static async Task<IDocument> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        return await context.OpenAsync(request => request.Content($"<html><body>{body}</body></html>"));
    }
}
