using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// Changes of what never has a box (e.g. a script loader changing the type and src of scripts,
/// or adding scripts to the head and body) keep the frame, unless a selector may style
/// something else by them.
/// </summary>
public sealed class UnrenderedChangeTests
{
    [Fact]
    public async Task ChangingAScriptKeepsTheFrame()
    {
        var (document, harness) = await OpenAsync("p { color: rgb(255, 0, 0) } input:checked + p { color: blue } [data-x] { margin: 4px }", "<script type=\"text/later\" data-src=\"a.js\"></script><p>text</p>");
        var frame = harness.PrepareFrame();
        var script = document.QuerySelector("script")!;

        script.SetAttribute("type", "text/other");
        script.SetAttribute("data-status", "executed");
        script.RemoveAttribute("data-src");

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Fact]
    public async Task AddingScriptsKeepsTheFrame()
    {
        var (document, harness) = await OpenAsync("p { color: rgb(255, 0, 0) }", "<div id=\"host\"><p>text</p></div>");
        var frame = harness.PrepareFrame();

        document.Head!.AppendChild(document.CreateElement("script"));
        document.GetElementById("host")!.AppendChild(document.CreateElement("script"));
        document.Body!.AppendChild(document.CreateElement("script"));

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Theory]
    [InlineData("script[type=\"later\"] + p { margin-left: 50px }", "type", "now")]
    [InlineData("script { display: block; height: 20px; background: rgb(0, 0, 255) } script.big { height: 60px }", "class", "big")]
    public async Task AScriptStylingSomethingIsLaidOutAgain(string css, string name, string value)
    {
        var (document, harness) = await OpenAsync(css, "<script type=\"later\"></script><p>text</p>");
        harness.PrepareFrame();

        document.QuerySelector("script")!.SetAttribute(name, value);
        var updated = harness.UpdateFrame() ?? harness.PrepareFrame();
        var pixels = updated.Rasterize();
        var full = harness.PrepareFrame().Rasterize();

        Assert.Equal(full.Data, pixels.Data);
    }

    [Fact]
    public async Task AScriptChangingWhichChildIsFirstIsLaidOutAgain()
    {
        var (document, harness) = await OpenAsync("#host > :first-child { color: rgb(255, 0, 0) }", "<div id=\"host\"><p>text</p></div>");
        var before = harness.PrepareFrame().Rasterize();
        var host = document.GetElementById("host")!;

        host.InsertBefore(document.CreateElement("script"), host.FirstChild);
        var updated = (harness.UpdateFrame() ?? harness.PrepareFrame()).Rasterize();

        Assert.NotEqual(before.Data, updated.Data);
        Assert.Equal(harness.PrepareFrame().Rasterize().Data, updated.Data);
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string css, string body)
    {
        var context = BrowsingContext.New(Configuration.Default
            .WithCss()
            .WithRenderDevice(new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, DeviceWidth = 300, DeviceHeight = 200, FontSize = 16 }));
        var document = await context.OpenAsync(request => request.Content($"<html><head><style>body {{ margin: 0 }} {css}</style></head><body>{body}</body></html>"));
        return (document, document.Context.GetDomHarness());
    }
}
