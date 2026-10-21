namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;

using SkiaSharp;

/// <summary>
/// <c>::before</c>/<c>::after</c> content that AngleSharp.Css leaves empty because it needs
/// document state: quote nesting (<c>open-quote</c>, <c>close-quote</c>, <c>quotes</c>) and images
/// (<c>content: url()</c>).
/// </summary>
public sealed class GeneratedContentTests
{
    private const string QuoteRules = "q::before { content: open-quote } q::after { content: close-quote }";

    [Fact]
    public async Task OpenAndCloseQuote_NestWithTheDefaultQuotes()
    {
        var texts = await DrawnTextsAsync($"<style>{QuoteRules}</style><p><q>one <q>two</q></q></p>");

        // Runs drop their edge spaces, so only the characters are compared.
        Assert.Equal("“one‘two’”", string.Concat(texts).Replace(" ", string.Empty));
    }

    [Fact]
    public async Task QElements_GetQuotesFromTheDefaultStylesheet()
    {
        var texts = await DrawnTextsAsync("<p><q>a<q>b</q></q></p>");

        Assert.Equal("\u201Ca\u2018b\u2019\u201D", string.Concat(texts));
    }

    [Fact]
    public async Task Quotes_UseTheQuotesProperty()
    {
        var texts = await DrawnTextsAsync($"<style>{QuoteRules} p {{ quotes: '<' '>' '[' ']' }}</style><p><q>a<q>b</q></q></p>");

        Assert.Equal("<a[b]>", string.Concat(texts));
    }

    [Fact]
    public async Task Quotes_RepeatTheLastPairWhenNestedDeeper()
    {
        var texts = await DrawnTextsAsync($"<style>{QuoteRules} p {{ quotes: '<' '>' }}</style><p><q>a<q>b</q></q></p>");

        Assert.Equal("<a<b>>", string.Concat(texts));
    }

    [Fact]
    public async Task QuotesNone_ProducesNoQuotes()
    {
        var texts = await DrawnTextsAsync($"<style>{QuoteRules} p {{ quotes: none }}</style><p><q>a</q></p>");

        Assert.Equal("a", string.Concat(texts));
    }

    [Fact]
    public async Task NoOpenQuote_AdvancesTheDepthWithoutText()
    {
        var texts = await DrawnTextsAsync("""
            <style>
                .skip::before { content: no-open-quote }
                .skip::after { content: no-close-quote }
                q::before { content: open-quote }
                q::after { content: close-quote }
            </style>
            <p><span class="skip">x<q>y</q></span></p>
            """);

        Assert.Equal("x‘y’", string.Concat(texts));
    }

    [Fact]
    public async Task UnbalancedCloseQuote_ProducesNothing()
    {
        var texts = await DrawnTextsAsync("<style>p::after { content: close-quote }</style><p>a</p>");

        Assert.Equal("a", string.Concat(texts));
    }

    [Fact]
    public async Task Quotes_FollowTheLanguage()
    {
        var texts = await DrawnTextsAsync($"<style>{QuoteRules}</style><p lang=\"de\"><q>a</q></p>");

        Assert.Equal("„a“", string.Concat(texts));
    }

    [Fact]
    public async Task QuotesMixWithStringsAndCounters()
    {
        var texts = await DrawnTextsAsync("""
            <style>
                ol { counter-reset: n }
                li { counter-increment: n; display: block }
                li::before { content: open-quote counter(n) ". " }
                li::after { content: close-quote }
            </style>
            <ol><li>a</li><li>b</li></ol>
            """);

        Assert.Equal("“1.a”“2.b”", string.Concat(texts).Replace(" ", string.Empty));
    }

    [Fact]
    public async Task ContentUrl_PaintsTheImageAtItsNaturalSize()
    {
        var displayList = await LayoutAsync($"<style>p::before {{ content: url({PngDataUri(12, 7)}) }}</style><p>text</p>");

        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(12f, image.Rect.Width, precision: 2);
        Assert.Equal(7f, image.Rect.Height, precision: 2);
    }

    [Fact]
    public async Task ContentUrl_SitsInlineBeforeTheText()
    {
        var displayList = await LayoutAsync($"<style>p {{ margin: 0 }} p::before {{ content: url({PngDataUri(30, 10)}) }}</style><p>text</p>");

        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>(), command => command.Text.Contains("text"));
        Assert.True(text.X >= image.Rect.X + 30f - 0.01f, $"text at {text.X} overlaps the image");
    }

    [Fact]
    public async Task ContentUrl_TakesTheAuthoredSize()
    {
        var displayList = await LayoutAsync($"<style>p::before {{ content: url({PngDataUri(12, 6)}); width: 24px; height: 12px; display: inline-block }}</style><p>text</p>");

        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(24f, image.Rect.Width, precision: 2);
        Assert.Equal(12f, image.Rect.Height, precision: 2);
    }

    [Fact]
    public async Task ContentUrlMixedWithText_PaintsTheImageAmongTheText()
    {
        var displayList = await LayoutAsync($"<style>p {{ margin: 0 }} p::before {{ content: \"a\" url({PngDataUri(30, 10)}) \"b\" }}</style><p>text</p>");

        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();
        var a = Assert.Single(texts, command => command.Text.StartsWith('a'));
        var b = Assert.Single(texts, command => command.Text.Contains('b'));
        Assert.Equal(30f, image.Rect.Width, precision: 2);
        Assert.Equal(10f, image.Rect.Height, precision: 2);
        Assert.True(a.X < image.Rect.X, "the text before the image");
        Assert.True(b.X >= image.Rect.X + 30f - 0.01f, "the text after the image");
    }

    [Fact]
    public async Task ContentUrlMixedWithQuotes_KeepsTheQuotes()
    {
        var displayList = await LayoutAsync($"<style>q::before {{ content: open-quote url({PngDataUri(8, 8)}) }} q::after {{ content: close-quote }}</style><p><q>x</q></p>");

        Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal("“x”", string.Concat(displayList.Commands.OfType<DrawTextCommand>().Select(command => command.Text)));
    }

    [Fact]
    public async Task ContentUrlThatFailsToLoad_PaintsNothing()
    {
        var displayList = await LayoutAsync("<style>p::before { content: url(missing.png) }</style><p>text</p>");

        Assert.Empty(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Contains(displayList.Commands.OfType<DrawTextCommand>(), command => command.Text.Contains("text"));
    }

    internal static string PngDataUri(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(200, 30, 30));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    private static async Task<IReadOnlyList<string>> DrawnTextsAsync(string body)
    {
        var displayList = await LayoutAsync(body);
        return displayList.Commands.OfType<DrawTextCommand>().Select(command => command.Text).ToList();
    }

    private static async Task<DisplayList> LayoutAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content($"<html><body>{body}</body></html>"));
        return new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 300 });
    }
}
