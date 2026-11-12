namespace AngleSharp.Renderer.Tests;

using System.Text;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

/// <summary>
/// The style maps of a large document are created on several threads before it is laid out,
/// which must give the same frame as creating them one after the other.
/// </summary>
public sealed class ParallelLayoutTests
{
    private const string Switch = "AngleSharp.Renderer.DisableParallelism";
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };

    [Fact]
    public async Task CreatingStyleMapsInParallelGivesTheSameFrame()
    {
        var html = new StringBuilder("""
            <style>
              :root { --gap: 6px } body { margin: 0; font: 14px sans-serif }
              .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: var(--gap) }
              .row { display: flex; gap: 4px; align-items: center } .row > * { flex: 1 }
              .card { border: 1px solid #ccc; padding: 4px; overflow-wrap: anywhere }
              .clamp { display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden }
              .v { writing-mode: vertical-rl; height: 60px }
              input::placeholder { color: rgb(200, 0, 0) }
              .fade { transition: opacity 1s; opacity: .5 }
            </style>
            """);

        for (var i = 0; i < 30; i++)
        {
            html.Append($"""<div class="grid"><div class="card fade"><div class="row"><span>a{i}</span><b>b</b><input placeholder="p{i}"></div></div>""");
            html.Append($"""<div class="card clamp">{string.Join(' ', Enumerable.Repeat("word", 30))}</div>""");
            html.Append($"""<div class="card"><p class="v">v{i}</p><ul><li>x</li><li style="color: green">y</li></ul></div></div>""");
        }

        var parallel = await LayOutAsync(html.ToString());

        AppContext.SetSwitch(Switch, true);

        try
        {
            var sequential = await LayOutAsync(html.ToString());

            Assert.True(parallel.Elements > 300);
            Assert.Equal(sequential.Boxes, parallel.Boxes);
            Assert.Equal(sequential.Pixels, parallel.Pixels);
        }
        finally
        {
            AppContext.SetSwitch(Switch, false);
        }
    }

    [Fact]
    public async Task OnDemandFlexItemStylesCanBeCreatedInParallel()
    {
        var items = string.Concat(Enumerable.Repeat(
            """<div style="height: 40px; border: 1px solid #ccc">A fixed-height flex item</div>""", 240));

        var parallel = await LayOutViewportAsync(items);
        AppContext.SetSwitch(Switch, true);

        try
        {
            var sequential = await LayOutViewportAsync(items);

            Assert.True(parallel.IsPartial);
            Assert.Equal(sequential.Boxes, parallel.Boxes);
            Assert.Equal(sequential.Pixels, parallel.Pixels);
        }
        finally
        {
            AppContext.SetSwitch(Switch, false);
        }
    }

    private static async Task<(int Elements, string Boxes, byte[] Pixels)> LayOutAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content($"<html><body>{body}</body></html>"));
        var harness = document.Context.GetDomHarness();
        var pixels = harness.PrepareFrame().Rasterize().Data;
        var boxes = string.Join('\n', document.All.Select(element => element.GetBoundingClientRect()).Select(rect => $"{rect.Left} {rect.Top} {rect.Width} {rect.Height}"));
        return (document.All.Length, boxes, pixels);
    }

    private static async Task<(bool IsPartial, int Boxes, byte[] Pixels)> LayOutViewportAsync(string items)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content(
            $"""<html><body><main style="display: flex; flex-direction: column">{items}</main></body></html>"""));
        var frame = document.Context.GetDomHarness().PrepareViewportFrame();
        return (frame.IsPartial, frame.Metrics.Boxes, frame.Rasterize().Data);
    }
}
