using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;

namespace AngleSharp.Renderer.Tests;

public class CssAnimationEventTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    private const string Keyframes = "<style>@keyframes fade { from { opacity: 0 } to { opacity: 1 } } .run { animation: fade 1s linear }</style>";

    [Fact]
    public async Task AnimationOfTheFirstFrame_StartsAndEnds()
    {
        var (document, harness) = await OpenAsync(Keyframes + """<div id="box" class="run" style="height: 20px"></div>""");
        var events = Listen(document.GetElementById("box")!);

        harness.PrepareFrame();

        Assert.Equal(["animationstart fade 0"], events);

        harness.AdvanceTime(TimeSpan.FromSeconds(2));
        harness.PrepareFrame();

        Assert.Equal(["animationstart fade 0", "animationend fade 1"], events);
    }

    [Fact]
    public async Task AnimationAddedLater_StartsWhenItIsAdded()
    {
        var (document, harness) = await OpenAsync(Keyframes + """<div id="box" style="height: 20px"></div>""");
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromSeconds(5));
        harness.PrepareFrame();

        box.ClassList.Add("run");
        var frame = harness.PrepareFrame();

        Assert.True(frame.IsAnimating);
        Assert.Equal(["animationstart fade 0"], events);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        harness.PrepareFrame();

        Assert.Equal(0.5, double.Parse(harness.GetAnimatedValue(box, "opacity")!, System.Globalization.CultureInfo.InvariantCulture), 2);
        Assert.Equal(["animationstart fade 0"], events);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(600));
        harness.PrepareFrame();

        Assert.Equal(["animationstart fade 0", "animationend fade 1"], events);
    }

    [Fact]
    public async Task RepeatedAnimation_TellsEachNewIteration()
    {
        var (document, harness) = await OpenAsync("""
            <style>@keyframes fade { from { opacity: 0 } to { opacity: 1 } }</style>
            <div id="box" style="height: 20px; animation: fade 1s linear 3"></div>
            """);
        var events = Listen(document.GetElementById("box")!);
        harness.PrepareFrame();

        harness.AdvanceTime(TimeSpan.FromMilliseconds(1500));
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        harness.PrepareFrame();

        Assert.Equal(["animationstart fade 0", "animationiteration fade 1", "animationiteration fade 2", "animationend fade 3"], events);
    }

    [Fact]
    public async Task AnimationRemovedWhileRunning_IsCancelledAndStartsAgainWhenAddedAgain()
    {
        var (document, harness) = await OpenAsync(Keyframes + """<div id="box" class="run" style="height: 20px"></div>""");
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(300));

        box.ClassList.Remove("run");
        harness.PrepareFrame();

        Assert.Equal(["animationstart fade 0", "animationcancel fade 0"], events);

        box.ClassList.Add("run");
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        harness.PrepareFrame();

        Assert.Equal(["animationstart fade 0", "animationcancel fade 0", "animationstart fade 0", "animationend fade 1"], events);
    }

    [Fact]
    public async Task AnimationEvents_Bubble()
    {
        var (document, harness) = await OpenAsync(Keyframes + """<div id="outer"><div class="run" style="height: 20px"></div></div>""");
        var events = Listen(document.GetElementById("outer")!);

        harness.PrepareFrame();

        Assert.Equal(["animationstart fade 0"], events);
    }

    [Fact]
    public async Task AnimationOfMissingKeyframes_HasNoEvents()
    {
        var (document, harness) = await OpenAsync("""<div id="box" style="height: 20px; animation: nothing 1s"></div>""");
        var events = Listen(document.GetElementById("box")!);

        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromSeconds(2));
        harness.PrepareFrame();

        Assert.Empty(events);
    }

    private static List<string> Listen(IElement element)
    {
        var events = new List<string>();

        foreach (var type in new[] { "animationstart", "animationiteration", "animationend", "animationcancel" })
        {
            element.AddEventListener(type, (_, ev) =>
            {
                var animation = Assert.IsType<AnimationEvent>(ev);
                events.Add($"{ev.Type} {animation.AnimationName} {animation.ElapsedTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            });
        }

        return events;
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string body)
    {
        var configuration = Configuration.Default.WithCss().WithRenderDevice(Device);
        var context = BrowsingContext.New(configuration);
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head><style>html, body { margin: 0; padding: 0; }</style></head>
            <body>{{body}}</body></html>
            """));
        return (document, document.Context.GetDomHarness());
    }
}
