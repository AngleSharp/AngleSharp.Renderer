namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

public sealed class WebAnimationsTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 160, ViewPortHeight = 100, FontSize = 16 };

    [Fact]
    public async Task Animate_KeyframeArrayRunsOnTheVirtualClockAndFillsForwards()
    {
        var (document, harness) = await OpenAsync();
        var element = document.GetElementById("target")!;
        var animation = element.Animate(
            new object[]
            {
                new Dictionary<string, object?> { ["opacity"] = 0d },
                new Dictionary<string, object?> { ["opacity"] = 1d },
            },
            new Dictionary<string, object?>
            {
                ["duration"] = 100d,
                ["fill"] = "forwards",
                ["easing"] = "linear",
            });

        Assert.Equal("running", animation.PlayState);
        Assert.Equal(0d, animation.CurrentTime);
        Assert.True(harness.PrepareFrame().IsAnimating);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(50));
        Assert.Equal(0.5f, Opacity(document), precision: 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(50));
        Assert.Equal("finished", animation.PlayState);
        Assert.True(animation.Finished.IsCompletedSuccessfully);
        Assert.False(harness.PrepareFrame().IsAnimating);
        Assert.Empty(new HtmlRenderer().BuildDisplayList(document, Device).Commands.OfType<PushOpacityCommand>());
    }

    [Fact]
    public async Task Animate_ObjectOfArraysAndCancelRejectsFinished()
    {
        var (document, _) = await OpenAsync();
        var element = document.GetElementById("target")!;
        var animation = element.Animate(
            new Dictionary<string, object?>
            {
                ["opacity"] = new[] { 0d, 1d },
                ["offset"] = new[] { 0d, 1d },
            },
            100d);
        var cancelled = false;
        animation.AddEventListener("cancel", (_, _) => cancelled = true);

        Assert.Equal(2, animation.Effect.GetKeyframes().Length);
        Assert.Equal(100d, animation.Effect.GetTiming().Duration);
        Assert.Single(element.GetAnimations());
        Assert.Single(document.GetAnimations());

        var finished = animation.Finished;
        animation.Cancel();

        Assert.Equal("idle", animation.PlayState);
        Assert.True(cancelled);
        Assert.True(finished.IsFaulted);
        Assert.IsType<DomException>(finished.Exception!.InnerException);
        Assert.Empty(element.GetAnimations());
        Assert.Equal(0d, document.GetTimeline().CurrentTime);
    }

    [Fact]
    public async Task Animate_ControlsPlaybackAndDispatchesFinish()
    {
        var (document, harness) = await OpenAsync();
        var element = document.GetElementById("target")!;
        var animation = element.Animate(
            new Dictionary<string, object?> { ["opacity"] = new[] { 0d, 1d } },
            new Dictionary<string, object?> { ["duration"] = 100d, ["fill"] = "both" });
        var finished = false;
        animation.OnFinish = (_, _) => finished = true;

        animation.Pause();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(40));
        Assert.Equal(0d, animation.CurrentTime);
        animation.CurrentTime = 80d;
        animation.Play();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(30));

        Assert.True(finished);
        Assert.Equal("finished", animation.PlayState);
        Assert.True(animation.Finished.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Animate_AlternatesAcrossIterations()
    {
        var (document, harness) = await OpenAsync();
        document.GetElementById("target")!.Animate(
            new Dictionary<string, object?> { ["opacity"] = new[] { 0d, 1d } },
            new Dictionary<string, object?>
            {
                ["duration"] = 100d,
                ["iterations"] = 2d,
                ["direction"] = "alternate",
                ["fill"] = "both",
            });

        harness.AdvanceTime(TimeSpan.FromMilliseconds(125));

        Assert.Equal(0.75f, Opacity(document), precision: 2);
    }

    [Fact]
    public async Task Animate_TakesPriorityOverCssAnimation()
    {
        var (document, harness) = await OpenAsync("""
            @keyframes css { from { opacity: .8; } to { opacity: .8; } }
            #target { animation: css 100ms linear infinite; }
            """);
        var element = document.GetElementById("target")!;
        element.Animate(
            new Dictionary<string, object?> { ["opacity"] = new[] { 0d, 1d } },
            new Dictionary<string, object?> { ["duration"] = 100d, ["fill"] = "both" });

        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(50));

        Assert.Equal(0.5f, Opacity(document), precision: 2);
    }

    private static float Opacity(IDocument document)
    {
        var list = new HtmlRenderer().BuildDisplayList(document, Device);
        return Assert.Single(list.Commands.OfType<PushOpacityCommand>()).Alpha;
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string additionalCss = "")
    {
        var configuration = Configuration.Default.WithCss().WithRenderDevice(Device);
        var context = BrowsingContext.New(configuration);
        var document = await context.OpenAsync(request => request.Content($$"""
            <!doctype html>
            <html><head><style>html, body { margin: 0; } #target { opacity: 1; width: 20px; height: 20px; } {{additionalCss}}</style></head>
            <body><div id="target"></div></body></html>
            """));
        return (document, document.Context.GetDomHarness());
    }
}
