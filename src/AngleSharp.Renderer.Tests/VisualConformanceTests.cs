namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
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

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        return await context.OpenAsync(request => request.Content(html));
    }
}