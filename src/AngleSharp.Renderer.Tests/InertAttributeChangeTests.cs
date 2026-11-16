using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer.Tests;

/// <summary>
/// Changes of attributes that change nothing rendered keep the frame: one set to the value it had
/// (e.g. a script setting the same style on every pointer move), or one no element renders by
/// (e.g. link previews taking the title of the hovered link away), unless a rule selects the
/// element by it or shows it.
/// </summary>
public sealed class InertAttributeChangeTests
{
    private const string Body = """<p id="p">Some <a id="link" href="#" title="Page">link</a> text</p><ul><li id="item" style="display: none">hidden</li></ul>""";

    [Fact]
    public async Task ChangingTheTitleOfALinkKeepsTheFrame()
    {
        // a rule of the title of another element (as MediaWiki has) does not style the link
        var (document, harness) = await OpenAsync("abbr[title], .explain[title] { border-bottom: 1px dotted }", Body);
        var frame = harness.PrepareFrame();
        var link = document.GetElementById("link")!;

        link.SetAttribute("title", string.Empty);
        link.SetAttribute("data-preview", "shown");
        link.SetAttribute("aria-expanded", "true");

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Fact]
    public async Task SettingAnAttributeToItsValueAgainKeepsTheFrame()
    {
        var (document, harness) = await OpenAsync(string.Empty, Body);
        var frame = harness.PrepareFrame();
        var item = document.GetElementById("item")!;
        var paragraph = document.GetElementById("p")!;

        item.SetAttribute("style", "display: none");
        paragraph.SetAttribute("class", "changed");
        paragraph.RemoveAttribute("class");

        Assert.Same(frame, harness.UpdateFrame());
    }

    [Theory]
    [InlineData("a[title] { color: rgb(255, 0, 0) }", "title", null)]
    [InlineData("a[title=\"Page\"] { color: rgb(255, 0, 0) }", "title", "Other")]
    [InlineData("a:not([title=\"\"]) { color: rgb(255, 0, 0) }", "title", "")]
    [InlineData("a::after { content: attr(title) }", "title", "Other")]
    [InlineData("[data-state=\"on\"] { background: rgb(0, 0, 255) }", "data-state", "on")]
    public async Task AnAttributeARuleSelectsOrShowsIsLaidOutAgain(string css, string name, string? value)
    {
        var (document, harness) = await OpenAsync(css, Body);
        var before = harness.PrepareFrame().Rasterize();
        var link = document.GetElementById("link")!;

        if (value is null)
        {
            link.RemoveAttribute(name);
        }
        else
        {
            link.SetAttribute(name, value);
        }

        var updated = (harness.UpdateFrame() ?? harness.PrepareFrame()).Rasterize();
        var full = harness.PrepareFrame().Rasterize();

        Assert.NotEqual(before.Data, full.Data);
        Assert.Equal(full.Data, updated.Data);
    }

    [Fact]
    public async Task AStyleChangedAndChangedBackKeepsTheFrame()
    {
        var (document, harness) = await OpenAsync(string.Empty, Body);
        var frame = harness.PrepareFrame();
        var paragraph = document.GetElementById("p")!;

        paragraph.SetAttribute("style", "color: rgb(255, 0, 0)");
        paragraph.RemoveAttribute("style");

        Assert.Same(frame, harness.UpdateFrame());
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
