namespace AngleSharp.Renderer.Tests;

using System.Text;
using System.Threading;
using AngleSharp;
using AngleSharp.Css;

/// <summary>
/// Deeply nested documents: layout recurses per nesting level, so a deep enough tree must leave
/// its innermost content out rather than overflow the stack (which ends the whole process).
/// </summary>
public sealed class DeepNestingTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    // The HTML parser keeps at most 511 levels below the body, so this is the deepest markup can get.
    private const int ParserDepthLimit = 511;

    // A thread-pool thread or a desktop app's thread commonly has 1 MB of stack.
    private const int StackSize = 1024 * 1024;

    // Nested tables and inline-blocks are not here yet: each level lays out the levels below it
    // twice (a trial and the real layout), so up to the depth the stack allows that takes hours.
    [Theory]
    [InlineData("<div>", "</div>")]
    [InlineData("<span>x", "</span>")]
    [InlineData("<div style='display: flex'>", "</div>")]
    [InlineData("<div style='display: grid'>", "</div>")]
    [InlineData("<div style='float: left'>", "</div>")]
    public async Task DeepestParsableNesting_RendersWithoutOverflowingTheStack(string open, string close)
    {
        var markup = new StringBuilder("<!doctype html><html><body>");

        for (var i = 0; i < ParserDepthLimit; i++)
        {
            markup.Append(open);
        }

        markup.Append("leaf");

        for (var i = 0; i < ParserDepthLimit; i++)
        {
            markup.Append(close);
        }

        markup.Append("</body></html>");
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content(markup.ToString()));
        byte[]? png = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                png = new HtmlRenderer().RenderToPng(document, Device).Data;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        }, StackSize);

        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
    }
}
