using AngleSharp;
using AngleSharp.Css;

namespace AngleSharp.Renderer.Tests;

public sealed class VisualConformanceTests
{
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
        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 120,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "paints-box-background-and-border.png",
          actualPng: image.Data,
          perChannelTolerance: 2,
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
        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 120,
            Height = 80,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "centers-auto-margin-block.png",
          actualPng: image.Data,
          perChannelTolerance: 2,
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
        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 120,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "shows-collapsed-vertical-margin-gap.png",
          actualPng: image.Data,
          perChannelTolerance: 2,
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
        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 160,
            Height = 100,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "absolute-positioned-out-of-flow.png",
          actualPng: image.Data,
          perChannelTolerance: 2,
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
        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 160,
            Height = 100,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "higher-z-index-over-lower-z-index.png",
          actualPng: image.Data,
          perChannelTolerance: 2,
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
        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 160,
            Height = 100,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        VisualSnapshotVerifier.VerifyOrCreate(
          snapshotName: "negative-z-index-behind-in-flow.png",
          actualPng: image.Data,
          perChannelTolerance: 2,
          maxDifferentPixels: 0);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        return await context.OpenAsync(request => request.Content(html));
    }
}