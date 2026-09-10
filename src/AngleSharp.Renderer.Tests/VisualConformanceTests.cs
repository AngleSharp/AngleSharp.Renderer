namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

[Trait("Category", "Visual")]
public sealed class VisualConformanceTests
{
    // Text glyph rasterization is delegated to the OS's own font engine (CoreText on macOS,
    // DirectWrite on Windows, FreeType on Linux - see AGENTS.md), and that engine's hinting and
    // anti-aliasing can differ across OS versions even on the *same* platform: CI is pinned to a
    // specific runner image (macos-14 et al.), but a developer's local machine runs whatever OS
    // version they have, which can be materially newer. That produces a handful of glyph/border
    // edge pixels differing by a few intensity levels - not a rendering regression, since the same
    // input consistently produces the same *content*, just very slightly different anti-aliasing.
    // These tolerances (measured: real CI-vs-local drift topped out at a per-channel delta of 6
    // across 9 pixels; doubled here for headroom) apply only to tests whose content is dominated
    // by text. Every shape/gradient/SVG test keeps an exact 0/0 tolerance - that geometry is
    // rendered by Skia's own rasterizer with no OS dependency, and has proven bit-for-bit
    // reproducible across OS versions, so loosening it here would hide real regressions there.
    private const byte TextRenderingToleranceChannel = 12;
    private const int TextRenderingToleranceMaxPixels = 20;

    [Fact]
    public async Task RenderToPng_PaintsBoxBackgroundAndBorderAtExpectedPixels()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin-left:10px; margin-top:10px; width:40px; height:20px; padding:5px; border:2px solid rgb(0,0,255); background-color:rgb(255,0,0);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-box-background-and-border.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_CentersAutoMarginBlock()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:50px; height:20px; margin-left:auto; margin-right:auto; background-color:rgb(0,255,0);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 80,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "centers-auto-margin-block.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_ShowsCollapsedVerticalMarginGap()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="height:10px; margin-bottom:20px; background-color:rgb(255,0,0);"></div>
                <div style="height:10px; margin-top:10px; background-color:rgb(0,0,255);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "shows-collapsed-vertical-margin-gap.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersDefaultEllipticalRadialGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:200px; height:100px; background-image:radial-gradient(red, blue);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-default-elliptical-radial-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersPositionedCircleRadialGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:120px; height:120px; background-image:radial-gradient(circle at 20% 80%, yellow, green);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-positioned-circle-radial-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersClosestSideRadialGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:120px; height:120px; background-image:radial-gradient(circle closest-side at center, white, black);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-closest-side-radial-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersConicGradientWithDegreeStops()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:120px; height:120px; background-image:conic-gradient(red 0deg, red 90deg, blue 90deg, blue 180deg, lime 180deg, lime 270deg, yellow 270deg, yellow 360deg);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-conic-gradient-with-degree-stops.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersRepeatingLinearGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:120px; height:120px; background-image:repeating-linear-gradient(to right, red 0px, red 10px, blue 10px, blue 20px);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-repeating-linear-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersRepeatingRadialGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:120px; height:120px; background-image:repeating-radial-gradient(circle at center, red 0px, red 10px, blue 10px, blue 20px);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-repeating-radial-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersRepeatingConicGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:120px; height:120px; background-image:repeating-conic-gradient(red 0deg, red 15deg, blue 15deg, blue 30deg);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-repeating-conic-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersSimpleTableLayout()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; } table { border-collapse: collapse; } td { padding: 4px; border: 1px solid black; background-color: #f0f0f0; }</style>
              </head>
              <body>
                <table style="width:120px;">
                  <tr><td>A</td><td>B</td></tr>
                  <tr><td>C</td><td>D</td></tr>
                </table>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 180,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-simple-table-layout.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersTableCellBordersAndWidths()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; } table { border-collapse: collapse; width: 160px; } td { padding: 6px; border: 2px solid #333; background-color: #dceeff; }</style>
              </head>
              <body>
                <table>
                  <tr>
                    <td style="width:70px;">Left</td>
                    <td style="width:70px;">Right</td>
                  </tr>
                </table>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-table-cell-borders-and-widths.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersTableWithColspanAndRowspan()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; } table { border-collapse: collapse; width: 180px; } td { padding: 6px; border: 1px solid #222; background-color: #eef7ff; }</style>
              </head>
              <body>
                <table>
                  <tr>
                    <td colspan="2">Header</td>
                  </tr>
                  <tr>
                    <td rowspan="2">Left</td>
                    <td>Right</td>
                  </tr>
                  <tr>
                    <td>Bottom</td>
                  </tr>
                </table>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 140,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-table-with-colspan-and-rowspan.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersCanvasRectanglesAndClearRect()
    {
        var image = await RenderCanvasSnapshotAsync("""
            <html>
              <body>
                <canvas width="120" height="100"></canvas>
              </body>
            </html>
            """, context =>
        {
            context.SetFillStyle("#ff0000");
            context.FillRect(10f, 10f, 70f, 40f);
            context.SetFillStyle("#00ff00");
            context.FillRect(40f, 30f, 45f, 30f);
            context.ClearRect(25f, 20f, 45f, 25f);
        });

        VisualSnapshotVerifier.VerifyOrCreate(
            snapshotName: "renders-canvas-rectangles-and-clear-rect.png",
            actualPng: image,
            perChannelTolerance: 0,
            maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersCanvasPathAndStroke()
    {
        var image = await RenderCanvasSnapshotAsync("""
            <html>
              <body>
                <canvas width="140" height="120"></canvas>
              </body>
            </html>
            """, context =>
        {
            context.SetFillStyle("#00ff00");
            context.SetStrokeStyle("#0000ff");
            context.SetLineWidth(2f);
            context.SetFont("20px sans-serif");
            context.BeginPath();
            context.MoveTo(10f, 10f);
            context.LineTo(110f, 10f);
            context.LineTo(110f, 90f);
            context.ClosePath();
            context.Fill();
            context.Stroke();
        });

        VisualSnapshotVerifier.VerifyOrCreate(
            snapshotName: "renders-canvas-path-and-text.png",
            actualPng: image,
            perChannelTolerance: 0,
            maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersCanvasTranslationAndState()
    {
        var image = await RenderCanvasSnapshotAsync("""
            <html>
              <body>
                <canvas width="140" height="120"></canvas>
              </body>
            </html>
            """, context =>
        {
            context.SetFillStyle("#ff0000");
            context.FillRect(10f, 10f, 30f, 30f);
            context.Save();
            context.Translate(20f, 0f);
            context.SetFillStyle("#0000ff");
            context.FillRect(10f, 10f, 30f, 30f);
            context.Restore();
            context.SetFillStyle("#00ff00");
            context.FillRect(50f, 50f, 30f, 30f);
        });

        VisualSnapshotVerifier.VerifyOrCreate(
            snapshotName: "renders-canvas-translation-and-state.png",
            actualPng: image,
            perChannelTolerance: 0,
            maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsAbsolutePositionedElementOutOfFlow()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="position:relative; width:100px; height:20px; background-color:#eeeeee;">
                    <div style="position:absolute; left:12px; top:6px; width:30px; height:10px; background-color:#ff0000;"></div>
                </div>
                <div style="width:40px; height:10px; background-color:#0000ff;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "absolute-positioned-out-of-flow.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsHigherZIndexAboveLowerZIndex()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="position:relative; width:120px; height:40px;">
                    <div style="position:absolute; left:10px; top:5px; width:30px; height:20px; background-color:#ff0000; z-index:1;"></div>
                    <div style="position:absolute; left:10px; top:5px; width:30px; height:20px; background-color:#0000ff; z-index:2;"></div>
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "higher-z-index-over-lower-z-index.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsNegativeZIndexBehindInFlowContent()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="position:relative; width:120px; height:30px; background-color:#00ff00;">
                    <div style="position:absolute; left:0; top:0; width:30px; height:10px; background-color:#ff0000; z-index:-1;"></div>
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "negative-z-index-behind-in-flow.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersMixedTextSizesStylesAndDecorations()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>
                  html, body { margin: 0; padding: 0; }
                  body { font-family: sans-serif; }
                </style>
              </head>
              <body>
                <p>
                  <span style="font-size:12px; font-weight:400; text-decoration:underline;">Small text</span>
                  <span style="font-size:24px; font-style:italic; font-weight:700; text-decoration:line-through;"> Large text</span>
                </p>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 260,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "mixed-text-sizes-styles-decorations.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersAlignedWrappedTextWithLineHeight()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>
                  html, body { margin: 0; padding: 0; }
                  body { font-family: sans-serif; }
                </style>
              </head>
              <body>
                <div style="width:140px; text-align:center; line-height:2; font-size:12px; color:#0000ff;">
                  one two three four five six seven eight nine ten eleven twelve thirteen fourteen
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 180,
            FontSize = 12f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "aligned-wrapped-text-with-line-height.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersDecorationColorAndStyle()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>
                  html, body { margin: 0; padding: 0; }
                  body { font-family: sans-serif; }
                </style>
              </head>
              <body>
                <p style="font-size:18px; text-decoration:underline; text-decoration-style:dashed; text-decoration-color:#ff0000; color:#000000;">
                  Decoration test
                </p>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 100,
            FontSize = 18f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "decoration-color-and-style.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersTextIndentAndVerticalAlign()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>
                  html, body { margin: 0; padding: 0; }
                  body { font-family: sans-serif; }
                </style>
              </head>
              <body>
                <p style="text-indent:24px; width:180px; font-size:16px;">
                  Indented text that wraps to a second line.
                </p>
                <p style="font-size:16px;">
                  normal <span style="vertical-align:super; font-size:12px; color:#ff0000;">sup</span> text
                </p>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 260,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "text-indent-and-vertical-align.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersSvgImageSource()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIxMCIgaGVpZ2h0PSIxMCI+PHJlY3Qgd2lkdGg9IjEwIiBoZWlnaHQ9IjEwIiBmaWxsPSJyZWQiLz48L3N2Zz4=" style="width:60px; height:60px;" />
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-svg-image-source.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgShapes()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="60" height="60" viewBox="0 0 60 60">
                  <rect x="0" y="0" width="60" height="60" fill="rgb(0,0,255)"></rect>
                  <circle cx="30" cy="30" r="20" fill="rgb(0,255,0)"></circle>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-shapes.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgPathAndTransform()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <path d="M10 10 L70 10 A20 20 0 0 1 70 50 L10 50 Z" fill="rgb(255,140,0)"></path>
                  <g transform="translate(75,75) rotate(45)">
                    <rect x="-10" y="-10" width="20" height="20" fill="rgb(128,0,128)"></rect>
                  </g>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-path-and-transform.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgLinearGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <defs>
                    <linearGradient id="g" x1="0" y1="0" x2="1" y2="0">
                      <stop offset="0" stop-color="rgb(255,0,0)"></stop>
                      <stop offset="1" stop-color="rgb(0,0,255)"></stop>
                    </linearGradient>
                  </defs>
                  <rect x="0" y="0" width="100" height="100" fill="url(#g)"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-linear-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgRadialGradient()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <defs>
                    <radialGradient id="g" cx="0.5" cy="0.5" r="0.5">
                      <stop offset="0" stop-color="rgb(255,255,0)"></stop>
                      <stop offset="1" stop-color="rgb(0,128,0)"></stop>
                    </radialGradient>
                  </defs>
                  <circle cx="50" cy="50" r="45" fill="url(#g)"></circle>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-radial-gradient.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgUseElement()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="50" viewBox="0 0 100 50">
                  <defs>
                    <circle id="dot" cx="0" cy="0" r="15" fill="rgb(220,20,60)"></circle>
                  </defs>
                  <use href="#dot" x="25" y="25"></use>
                  <use href="#dot" x="75" y="25"></use>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 50,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-use-element.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgTextWithTspanAndAnchor()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="160" height="60" viewBox="0 0 160 60">
                  <text x="80" y="30" font-size="20" font-family="sans-serif" fill="rgb(0,0,0)" text-anchor="middle">Hi<tspan fill="rgb(200,0,0)">!</tspan></text>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 60,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-text.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgClipPath()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <defs>
                    <clipPath id="c">
                      <circle cx="50" cy="50" r="35"></circle>
                    </clipPath>
                  </defs>
                  <rect x="0" y="0" width="100" height="100" fill="rgb(255,140,0)" clip-path="url(#c)"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-clip-path.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgMask()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <defs>
                    <mask id="m">
                      <rect x="0" y="0" width="100" height="100" fill="white"></rect>
                      <circle cx="50" cy="50" r="25" fill="black"></circle>
                    </mask>
                  </defs>
                  <rect x="0" y="0" width="100" height="100" fill="rgb(0,128,0)" mask="url(#m)"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-mask.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgPreserveAspectRatioSlice()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 200 100" preserveAspectRatio="xMidYMid slice">
                  <rect x="0" y="0" width="100" height="100" fill="rgb(255,0,0)"></rect>
                  <rect x="100" y="0" width="100" height="100" fill="rgb(0,0,255)"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-preserve-aspect-ratio-slice.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgInternalStyleSheet()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <style>
                    .box { fill: rgb(0,0,255); }
                    #special { fill: rgb(0,128,0); }
                    rect { stroke: rgb(0,0,0); stroke-width: 2; }
                  </style>
                  <rect class="box" x="5" y="5" width="40" height="40"></rect>
                  <rect id="special" class="box" x="55" y="55" width="40" height="40"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-internal-stylesheet.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgPercentageLengths()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <rect x="0%" y="0%" width="100%" height="100%" fill="rgb(0,0,255)"></rect>
                  <circle cx="50%" cy="50%" r="30%" fill="rgb(255,255,0)"></circle>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-percentage-lengths.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersNestedSvgViewport()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <rect x="0" y="0" width="100" height="100" fill="rgb(220,220,220)"></rect>
                  <svg x="10" y="10" width="50" height="50" viewBox="0 0 10 10">
                    <circle cx="5" cy="5" r="5" fill="rgb(0,128,0)"></circle>
                  </svg>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-nested-svg-viewport.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersSymbolViaUse()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="50" viewBox="0 0 100 50">
                  <defs>
                    <symbol id="icon" viewBox="0 0 10 10">
                      <rect x="0" y="0" width="10" height="10" fill="rgb(220,20,60)"></rect>
                    </symbol>
                  </defs>
                  <use href="#icon" x="5" y="5" width="40" height="40"></use>
                  <use href="#icon" x="55" y="5" width="40" height="40"></use>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 50,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-symbol-via-use.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgCurrentColor()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100" color="rgb(0,128,0)">
                  <rect x="10" y="10" width="80" height="80" fill="currentColor"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-current-color.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgExplicitMaskRegion()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <defs>
                    <mask id="m" maskUnits="userSpaceOnUse" x="0" y="0" width="50" height="100">
                      <rect x="0" y="0" width="100" height="100" fill="white"></rect>
                    </mask>
                  </defs>
                  <rect x="0" y="0" width="100" height="100" fill="rgb(0,128,0)" mask="url(#m)"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-explicit-mask-region.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgPatternFill()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <defs>
                    <pattern id="p" patternUnits="userSpaceOnUse" x="0" y="0" width="20" height="20">
                      <rect x="0" y="0" width="20" height="20" fill="rgb(255,255,255)"></rect>
                      <rect x="0" y="0" width="10" height="10" fill="rgb(0,0,255)"></rect>
                      <rect x="10" y="10" width="10" height="10" fill="rgb(0,0,255)"></rect>
                    </pattern>
                  </defs>
                  <rect x="0" y="0" width="100" height="100" fill="url(#p)"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-pattern-fill.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersInlineSvgGaussianBlurFilter()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <defs>
                    <filter id="f">
                      <feGaussianBlur stdDeviation="4"></feGaussianBlur>
                    </filter>
                  </defs>
                  <circle cx="50" cy="50" r="30" fill="rgb(0,0,255)" filter="url(#f)"></circle>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-inline-svg-gaussian-blur-filter.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_IgnoresMediaRuleInsideInlineSvgStyle()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <svg width="100" height="100" viewBox="0 0 100 100">
                  <style>
                    /* comment */
                    @media (min-width: 1px) {
                      rect { fill: rgb(255,0,0); }
                    }
                    rect { fill: rgb(0,128,0); }
                  </style>
                  <rect x="10" y="10" width="80" height="80"></rect>
                </svg>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "ignores-media-rule-inside-inline-svg-style.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

      [Fact]
      public async Task RenderToPng_RendersWebSafeFontFamiliesDifferently()
      {
          var document = await ParseAsync("""
              <html>
                <body>
                  <p style="font-family:serif; font-size:26px;">Serif sample</p>
                  <p style="font-family:sans-serif; font-size:26px;">Sans sample</p>
                  <p style="font-family:monospace; font-size:26px;">Mono sample</p>
                </body>
              </html>
              """);

          var renderer = new HtmlRenderer();
          var image = renderer.RenderToPng(document, new DefaultRenderDevice
          {
              ViewPortWidth = 320,
              ViewPortHeight = 200,
              FontSize = 16f,
          });

          VisualSnapshotVerifier.VerifyOrCreate(
            snapshotName: "web-safe-font-families.png",
            actualPng: image.Data,
            perChannelTolerance: TextRenderingToleranceChannel,
            maxDifferentPixels: TextRenderingToleranceMaxPixels);
      }

    [Fact]
    public async Task RenderToPng_PaintsUniformRoundedBoxBackgroundAndBorder()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:80px; height:50px; border:4px solid rgb(0,0,255); background-color:rgb(255,0,0); border-radius:12px;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 90,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-uniform-rounded-box-background-and-border.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsPillShapeFromClampedBorderRadius()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:100px; height:30px; background-color:rgb(0,128,255); border-radius:100px;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 50,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-pill-shape-from-clamped-border-radius.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsEllipticalBorderRadiusCorners()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:100px; height:60px; background-color:rgb(0,180,0); border-radius:40px / 20px;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 80,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-elliptical-border-radius-corners.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsPerCornerDifferentBorderRadii()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:100px; height:60px; background-color:rgb(255,128,0); border-top-left-radius:0; border-top-right-radius:24px; border-bottom-right-radius:0; border-bottom-left-radius:24px;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 80,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-per-corner-different-border-radii.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_FallsBackToStraightEdgesForMixedWidthRoundedBorder()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:80px; height:50px; border-top-width:2px; border-right-width:10px; border-bottom-width:2px; border-left-width:2px; border-style:solid; border-color:rgb(0,0,255); background-color:rgb(255,0,0); border-radius:12px;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 90,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "falls-back-to-straight-edges-for-mixed-width-rounded-border.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsOutsetBoxShadow()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:20px; width:60px; height:40px; background-color:rgb(0,128,255); box-shadow: 6px 6px 8px rgba(0,0,0,0.6);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 130,
            ViewPortHeight = 110,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-outset-box-shadow.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsInsetBoxShadow()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:20px; width:80px; height:50px; background-color:rgb(255,255,0); box-shadow: inset 0 0 0 8px rgba(0,0,0,0.7);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 130,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-inset-box-shadow.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsBoxShadowFollowingBorderRadius()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:20px; width:70px; height:40px; border-radius:16px; background-color:rgb(0,200,0); box-shadow: 4px 4px 0 rgba(0,0,0,0.8);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 130,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-box-shadow-following-border-radius.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsMultipleBoxShadowsLayered()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:20px; width:50px; height:30px; background-color:rgb(220,220,220); box-shadow: 3px 3px 0 rgba(255,0,0,1), 6px 6px 0 rgba(0,0,255,1);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 110,
            ViewPortHeight = 90,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-multiple-box-shadows-layered.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsTextShadow()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; background-color: rgb(60,60,60); }</style>
              </head>
              <body>
                <p style="margin:10px; font-family:sans-serif; font-size:28px; color:rgb(255,255,255); text-shadow: 2px 2px 3px rgba(0,0,0,0.9);">Hi</p>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 70,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-text-shadow.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_PaintsMultipleTextShadowsLayered()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <p style="margin:10px; font-family:sans-serif; font-size:28px; color:rgb(0,0,0); text-shadow: 2px 2px 0 rgba(255,0,0,1), 4px 4px 0 rgba(0,0,255,1);">Hi</p>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 70,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-multiple-text-shadows-layered.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_PaintsOwnBackgroundBehindOwnDirectTextContent()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="width:100px; height:40px; background-color:rgb(255,0,0); color:rgb(0,0,0); font-size:24px;">Hi</div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 60,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-own-background-behind-own-text.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersUnorderedListWithDiscBullets()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <ul style="font-family:sans-serif; font-size:18px; color:rgb(0,0,0); margin-top:10px; margin-right:10px; margin-bottom:10px;">
                    <li>One</li>
                    <li>Two</li>
                </ul>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 90,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-unordered-list-with-disc-bullets.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersOrderedListWithDecimalMarkers()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <ol style="font-family:sans-serif; font-size:18px; color:rgb(0,0,0); margin-top:10px; margin-right:10px; margin-bottom:10px;">
                    <li>First</li>
                    <li>Second</li>
                    <li>Third</li>
                </ol>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 110,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-ordered-list-with-decimal-markers.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersSquareAndCircleListStyleTypes()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <ul style="font-family:sans-serif; font-size:18px; color:rgb(0,0,0); margin-top:10px; margin-right:10px; margin-bottom:10px; list-style-type:square;">
                    <li>Square</li>
                </ul>
                <ul style="font-family:sans-serif; font-size:18px; color:rgb(0,0,0); margin-top:0; margin-right:10px; margin-bottom:10px; list-style-type:circle;">
                    <li>Circle</li>
                </ul>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 90,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-square-and-circle-list-style-types.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersNestedListWithIndependentNumbering()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <ol style="font-family:sans-serif; font-size:16px; color:rgb(0,0,0); margin-top:10px; margin-right:10px; margin-bottom:10px;">
                    <li>Parent
                        <ol style="margin-top:0; margin-bottom:0;">
                            <li>Child</li>
                        </ol>
                    </li>
                    <li>Sibling</li>
                </ol>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 180,
            ViewPortHeight = 130,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-nested-list-with-independent-numbering.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_RendersStyledListItemsWithBackgroundColor()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <ul style="font-family:sans-serif; font-size:16px; color:rgb(255,255,255); margin-top:10px; margin-right:10px; margin-bottom:10px; list-style-type:none;">
                    <li style="background-color:rgb(0,120,215); padding:4px; margin-bottom:4px;">Styled item one</li>
                    <li style="background-color:rgb(0,150,80); padding:4px;">Styled item two</li>
                </ul>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-styled-list-items-with-background-color.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_ComparesListStylePositionInsideAndOutside()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <ul style="font-family:sans-serif; font-size:16px; color:rgb(0,0,0); width:90px; margin-top:10px; margin-right:10px; margin-bottom:10px; list-style-position:outside;">
                    <li>Outside position keeps the marker in the gutter</li>
                </ul>
                <ul style="font-family:sans-serif; font-size:16px; color:rgb(0,0,0); width:90px; margin-top:0; margin-right:10px; margin-bottom:10px; list-style-position:inside;">
                    <li>Inside position puts the marker on the first line</li>
                </ul>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 280,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "compares-list-style-position-inside-and-outside.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_ClipsOverflowHiddenContentToBox()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:50px; height:30px; overflow:hidden; background-color:rgb(230,230,230); position:relative;">
                    <div style="position:absolute; left:20px; top:10px; width:60px; height:60px; background-color:rgb(255,0,0);"></div>
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 80,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "clips-overflow-hidden-content-to-box.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_DoesNotClipOverflowVisibleContent()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:50px; height:30px; background-color:rgb(230,230,230); position:relative;">
                    <div style="position:absolute; left:20px; top:10px; width:60px; height:60px; background-color:rgb(255,0,0);"></div>
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 80,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "does-not-clip-overflow-visible-content.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_ClipsToRoundedShapeWithBorderRadius()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:60px; height:60px; border-radius:16px; overflow:hidden; position:relative;">
                    <div style="position:absolute; left:-10px; top:-10px; width:100px; height:100px; background-color:rgb(0,120,215);"></div>
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "clips-to-rounded-shape-with-border-radius.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_ClipsOversizedNormalFlowChildToBox()
    {
        // A child's own explicit width is not constrained by its parent's - a plain, normal-flow
        // (non-absolute) child can be wider than its container, which is a common real-world
        // overflow trigger (an oversized image, a wide table, ...), distinct from the
        // position:absolute escape the other tests here use. The child is deliberately no taller
        // than the parent's own height, since this renderer's auto content height always grows to
        // fit a normal-flow child vertically (height behaves like a floor, not a cap) - so a
        // taller child would just grow the parent instead of overflowing it.
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:40px; height:30px; overflow:hidden; background-color:rgb(230,230,230);">
                    <div style="width:80px; height:20px; background-color:rgb(255,0,0);"></div>
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "clips-oversized-normal-flow-child-to-box.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    private static async Task<byte[]> RenderCanvasSnapshotAsync(string html, Action<Canvas2DRenderingContext> draw)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering());
        var document = await context.OpenAsync(request => request.Content(html));
        var canvas = document.QuerySelector("canvas") as IHtmlCanvasElement;

        Assert.NotNull(canvas);

        var renderingContext = canvas!.GetContext("2d");
        var canvasContext = Assert.IsType<Canvas2DRenderingContext>(renderingContext);
        draw(canvasContext);

        return canvasContext.ToImage("image/png");
    }

    [Fact]
    public async Task RenderToPng_PaintsTopOfTallPageWhenUnscrolled()
    {
        var image = await RenderScrolledTallPageAsync(scrollTop: null, viewportHeight: 80);

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-top-of-tall-page-when-unscrolled.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsOnlyBottomOfTallPageWhenScrollOffsetIsLargeEnough()
    {
        // The page is 5 stacked 40px bands (200px total) in an 80px-tall viewport - scrolling by
        // 120px moves past the first three bands entirely, so only the last two (green, blue)
        // should paint, shifted up to fill the viewport exactly as the unscrolled page's first two
        // bands (red, orange) do in the sibling test above.
        var image = await RenderScrolledTallPageAsync(scrollTop: 120, viewportHeight: 80);

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-only-bottom-of-tall-page-when-scroll-offset-is-large-enough.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PaintsMiddleOfTallPageAtAnArbitrarySettableScrollOffset()
    {
        // A scroll offset that isn't aligned to a band boundary (100px lands 20px into the third
        // band, yellow) proves the offset is a continuously "settable" pixel value, not just a
        // switch between a fixed top/bottom view - the viewport should show the bottom 20px of
        // yellow, all 40px of green, then the top 20px of blue. 100px is also comfortably within
        // the page's actual 120px max scroll (200px content - 80px viewport), unlike a larger
        // value that would just clamp to the same view as the "scrolled past the top" test above.
        var image = await RenderScrolledTallPageAsync(scrollTop: 100, viewportHeight: 80);

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-middle-of-tall-page-at-an-arbitrary-settable-scroll-offset.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    private static async Task<AngleSharp.Renderer.Rendering.RenderedImage> RenderScrolledTallPageAsync(double? scrollTop, int viewportHeight)
    {
        var renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = 80,
            ViewPortHeight = viewportHeight,
            DeviceWidth = 80,
            DeviceHeight = viewportHeight,
            FontSize = 16,
        };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:80px; height:40px; background-color:rgb(255,0,0);"></div>
                <div style="width:80px; height:40px; background-color:rgb(255,165,0);"></div>
                <div style="width:80px; height:40px; background-color:rgb(255,255,0);"></div>
                <div style="width:80px; height:40px; background-color:rgb(0,180,0);"></div>
                <div style="width:80px; height:40px; background-color:rgb(0,0,255);"></div>
              </body>
            </html>
            """, configuration);

        if (scrollTop.HasValue)
        {
            document.Context.GetDomHarness();
            document.DocumentElement.SetScrollTop(scrollTop.Value);
        }

        var renderer = new HtmlRenderer();
        return renderer.RenderToPng(document, renderDevice);
    }

    // A hand-built 2x2 RGB PNG (no palette, filter 0), one red pixel diagonal from one blue pixel -
    // a tiny, unambiguous checkerboard tile that makes tiling/scaling/positioning visually obvious
    // even at very small sizes, unlike a solid-color 1x1 pixel (which would look identical whether
    // or not tiling/positioning code actually ran).
    private const string CheckerboardTileDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAE0lEQVR42mO4IycnZ3OHAYiBLAAgUgSdATw7NgAAAABJRU5ErkJggg==";

    // A larger (20x20, four 10x10 quadrants) checkerboard PNG built the same way as
    // CheckerboardTileDataUri, used specifically for the default-repeat tiling test below - tiling
    // a 2x2 tile at 1:1 scale produces a genuinely correct but single-pixel-period checkerboard,
    // which is indistinguishable from a rendering bug under any kind of downsampled visual
    // inspection (verified: a raw per-pixel dump of that render confirmed it actually alternates
    // correctly, but it is not a useful baseline for a human - or a diff image - to eyeball).
    private const string CoarseCheckerboardTileDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAIAAAAC64paAAAAJUlEQVR42mO4IyeHB8nZ3MGDGEY1j2omqBm/NH6jRzWPaiaoGQCY+s0A5fuwxQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task RenderToPng_TilesBackgroundImageAcrossBoxByDefault()
    {
        // No background-repeat/position/size given at all - the CSS initial values (`repeat repeat`,
        // `0% 0%`, `auto`) tile the 20x20 tile at its own natural size across the whole box, so a
        // 60x60 box shows a clean 3x3 grid of the tile's four quadrants.
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:60px; height:60px; background-image: url({{CoarseCheckerboardTileDataUri}});"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 60,
            ViewPortHeight = 60,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "tiles-background-image-across-box-by-default.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PositionsAndSizesNoRepeatBackgroundImage()
    {
        // An explicit background-size, background-repeat: no-repeat and background-position: right
        // bottom together place a single, scaled-up copy of the tile in the box's bottom-right
        // corner - the rest of the box shows the plain background-color underneath, since a
        // non-repeating axis paints transparent (Decal), not a smeared/clamped edge color.
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:60px; height:60px; background-color:#ffffff; background-image: url({{CheckerboardTileDataUri}}); background-repeat: no-repeat; background-size: 20px 20px; background-position: right bottom;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 60,
            ViewPortHeight = 60,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "positions-and-sizes-no-repeat-background-image.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_ScalesBackgroundImageToCoverBox()
    {
        // background-size: cover scales the (square) tile up until it fills a non-square box
        // entirely, cropping whichever axis overflows - here the box is wider than it is tall, so
        // the tile is scaled to the box's width and its vertical overflow is cropped symmetrically
        // (background-position: center, the CSS default for a single "center" keyword).
        var document = await ParseAsync($$"""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:80px; height:40px; background-image: url({{CheckerboardTileDataUri}}); background-repeat: no-repeat; background-size: cover; background-position: center;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 80,
            ViewPortHeight = 40,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "scales-background-image-to-cover-box.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RendersFormControlGalleryWithDefaultStyling()
    {
        var document = await ParseAsync("""
            <html>
              <head><style>
                html, body { margin: 0; padding: 0; }
                div.row { margin-bottom: 6px; }
              </style></head>
              <body>
                <div class="row"><input type="text" value="Hello" /></div>
                <div class="row"><input type="number" value="42" /></div>
                <div class="row"><input type="url" value="https://x.test" /></div>
                <div class="row"><input type="color" value="#3388ff" /></div>
                <div class="row"><input type="checkbox" /><input type="checkbox" checked /></div>
                <div class="row"><input type="radio" /><input type="radio" checked /></div>
                <div class="row">
                  <select>
                    <option>First</option>
                    <option selected>Second</option>
                  </select>
                </div>
                <div class="row"><textarea>Some notes</textarea></div>
                <div class="row"><button>Click Me</button><input type="submit" value="Send" /></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 340,
            FontSize = 16,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "renders-form-control-gallery-with-default-styling.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_FormControlDefaultsAreOverriddenByAuthorCss()
    {
        // Two otherwise-identical text inputs side by side - the left keeps every synthesized
        // default, the right overrides border color/width, border-radius, background-color and
        // width via ordinary CSS, exactly the way a real browser lets you restyle a form control's
        // "somewhat overridable" native chrome without needing a `appearance: none` escape hatch.
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <input type="text" value="Default" style="display:block; margin-bottom:8px;" />
                <input type="text" value="Custom" style="display:block; width:100px; background-color:#fff6d5; border:3px solid #cc6600; border-radius:8px;" />
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 180,
            ViewPortHeight = 80,
            FontSize = 16,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "form-control-defaults-are-overridden-by-author-css.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_TranslatesAndScalesAChainedTransformedBox()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:30px; height:15px; background-color:rgb(0,128,255); transform: translate(20px, 10px) scale(1.5);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 150,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "translates-and-scales-a-chained-transformed-box.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_RotatesABoxAroundItsDefaultCenter()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:30px; width:60px; height:30px; background-color:rgb(220,50,50); transform: rotate(45deg);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 150,
            ViewPortHeight = 120,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "rotates-a-box-around-its-default-center.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_ScalesABoxAroundACustomTransformOrigin()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:40px; height:40px; background-color:rgb(200,60,0); transform: scale(1.5); transform-origin: top left;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "scales-a-box-around-a-custom-transform-origin.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_GrayscaleFilterDesaturatesABox()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:20px; width:60px; height:40px; background-color:rgb(220,50,50); filter: grayscale(1);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "grayscale-filter-desaturates-a-box.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_BlurFilterSoftensABoxEdge()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:30px; width:40px; height:40px; background-color:rgb(0,128,255); filter: blur(6px);"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "blur-filter-softens-a-box-edge.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_OpacityFadesABoxOverAWhiteBackground()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; background-color: white; }</style>
              </head>
              <body>
                <div style="margin:20px; width:60px; height:40px; background-color:rgb(220,50,50); opacity: 0.4;"></div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 120,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "opacity-fades-a-box-over-a-white-background.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_HoverTransitionShowsAnInterpolatedMidFlightFrame()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 120, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head>
                <style>
                  html, body { margin: 0; padding: 0; background-color: white; }
                  #target { background-color: rgb(0, 0, 255); transition: background-color 1s linear; }
                  #target:hover { background-color: rgb(255, 0, 0); }
                </style>
              </head>
              <body>
                <div id="target" style="margin:20px; width:60px; height:40px;"></div>
              </body>
            </html>
            """, configuration);

        var harness = document.Context.GetDomHarness();
        harness.MousePosition = (50, 40);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));

        var image = harness.PaintToPng();

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "hover-transition-shows-an-interpolated-mid-flight-frame.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_AnimationShowsAnInterpolatedMidFlightFrame()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 120, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head>
                <style>
                  html, body { margin: 0; padding: 0; background-color: white; }
                  @keyframes slide {
                    0% { left: 0px; }
                    100% { left: 60px; }
                  }
                  #target { position: relative; background-color: rgb(0, 128, 255); animation: slide 2s linear; }
                </style>
              </head>
              <body>
                <div id="target" style="margin:20px; width:30px; height:40px;"></div>
              </body>
            </html>
            """, configuration);

        var harness = document.Context.GetDomHarness();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));

        var image = harness.PaintToPng();

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "animation-shows-an-interpolated-mid-flight-frame.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_FocusedTextInputShowsAnOpaqueCaret()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 120, ViewPortHeight = 60, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; background-color: white; }</style>
              </head>
              <body>
                <input id="target" type="text" value="Hi" style="margin:10px;" />
              </body>
            </html>
            """, configuration);

        var target = (IHtmlElement)document.GetElementById("target")!;
        target.DoFocus();

        // Registering the harness starts the virtual clock at 0, where the caret's cosine "breathe"
        // is at its peak (see FormControlCaretBlinkPeriodMs) - a deterministic, fully-opaque frame.
        var harness = document.Context.GetDomHarness();
        var image = harness.PaintToPng();

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "focused-text-input-shows-an-opaque-caret.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_PreWhiteSpacePreservesIndentationAndLineBreaks()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 260, ViewPortHeight = 120, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var html = "<html><head><style>html, body { margin: 0; padding: 0; background-color: white; }</style></head>"
            + "<body><pre>function f() {\n    return 1;\n}</pre></body></html>";
        var document = await ParseAsync(html, configuration);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, renderDevice);

        // Multi-line, monospace-heavy text (the whole point of this snapshot) is exactly the case
        // TextRenderingToleranceChannel/MaxPixels already exist for elsewhere in this file - font
        // hinting/anti-aliasing can shift a glyph edge by a pixel or two between runs even on the
        // same platform, with no actual layout regression.
        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "pre-white-space-preserves-indentation-and-line-breaks.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    [Fact]
    public async Task RenderToPng_SizesGridTracksWithFrRepeatAndMinMax()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; }</style>
              </head>
              <body>
                <div style="margin:10px; width:160px; height:60px; display:grid; grid-template-columns:minmax(30px, 1fr) repeat(2, 40px); gap:5px; background-color:#eeeeee;">
                  <div style="height:60px; background-color:rgb(220,50,50);"></div>
                  <div style="height:60px; background-color:rgb(50,150,220);"></div>
                  <div style="height:60px; background-color:rgb(80,200,120);"></div>
                </div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 100,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "sizes-grid-tracks-with-fr-repeat-and-minmax.png",
          actualPng: image.Data,
          perChannelTolerance: 0,
          maxDifferentPixels: 0);
    }

    [Fact]
    public async Task RenderToPng_TruncatesAndBreaksOverflowingText()
    {
        var document = await ParseAsync("""
            <html>
              <head>
                <style>html, body { margin: 0; padding: 0; background-color: white; }</style>
              </head>
              <body>
                <div style="width:120px; margin:8px; padding:4px; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; border:1px solid #333333;">This text is much too long to fit</div>
                <div style="width:70px; margin:8px; padding:4px; border:1px solid #333333; word-break:break-all;">Supercalifragilisticexpialidocious</div>
              </body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 160,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "truncates-and-breaks-overflowing-text.png",
          actualPng: image.Data,
          perChannelTolerance: TextRenderingToleranceChannel,
          maxDifferentPixels: TextRenderingToleranceMaxPixels);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html, IConfiguration? configuration = null)
    {
        var context = BrowsingContext.New(configuration ?? Configuration.Default.WithCss());
        return await context.OpenAsync(request => request.Content(html));
    }
}