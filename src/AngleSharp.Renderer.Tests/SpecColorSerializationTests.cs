namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Values;
using AngleSharp.Renderer.Rendering;

[CollectionDefinition(nameof(SpecColorSerializationTests), DisableParallelization = true)]
public sealed class SpecColorSerializationCollection
{
}

/// <summary>
/// Pages rendered while AngleSharp.Css serializes opaque colors as the spec says (e.g.,
/// <c>rgb(255, 255, 255)</c> rather than <c>rgba(255, 255, 255, 1)</c>), which a browser sets
/// for the whole process.
/// </summary>
[Collection(nameof(SpecColorSerializationTests))]
public sealed class SpecColorSerializationTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 400, ViewPortHeight = 300, FontSize = 16 };

    [Fact]
    public async Task BoxShadow_KeepsAnOpaqueColor()
    {
        // the tractor store's buttons: a white shadow was painted black
        var shadows = await GetBoxShadowsAsync("0 2px 3px 2px rgb(255, 255, 255), inset 0 -10px 5px #e5e5e5, 0 0 25px rgba(0, 0, 0, 0.05)");

        Assert.Equal(3, shadows.Count);
        Assert.Equal(new RenderColor(255, 255, 255), shadows[0].Color);
        Assert.Equal(2f, shadows[0].SpreadRadius);
        Assert.Equal(new RenderColor(229, 229, 229), shadows[1].Color);
        Assert.True(shadows[1].Inset);
        Assert.Equal(-10f, shadows[1].OffsetY);
        Assert.Equal(25f, shadows[2].BlurRadius);
    }

    private static async Task<IReadOnlyList<RenderBoxShadow>> GetBoxShadowsAsync(string boxShadow)
    {
        var previous = CssColorValue.UseSpecSerialization;
        CssColorValue.UseSpecSerialization = true;

        try
        {
            var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
            var document = await context.OpenAsync(request => request.Content(
                $"""<div style="width: 100px; height: 50px; margin: 40px; box-shadow: {boxShadow}"></div>"""));
            var commands = new HtmlRenderer().BuildDisplayList(document, Device).Commands;
            // painted from the last one listed to the first
            return commands.OfType<DrawBoxShadowCommand>().Select(command => command.Shadow).Reverse().ToList();
        }
        finally
        {
            CssColorValue.UseSpecSerialization = previous;
        }
    }
}
