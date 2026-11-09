using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using System.Globalization;

namespace AngleSharp.Renderer.Tests;

public class CssTransitionEventTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    private const string Fade = "<style>.box { height: 20px; opacity: 0; transition: opacity 1s linear } .box.on { opacity: 1 }</style>";

    [Fact]
    public async Task ClassChangedByScript_TransitionsAndTellsItsEvents()
    {
        var (document, harness) = await OpenAsync(Fade + """<div id="box" class="box"></div>""");
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();

        box.ClassList.Add("on");
        var frame = harness.PrepareFrame();

        Assert.True(frame.IsAnimating);
        Assert.Equal(["transitionrun opacity 0", "transitionstart opacity 0"], events);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        harness.PrepareFrame();

        Assert.Equal(0.5, Opacity(harness, box), 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(600));
        frame = harness.PrepareFrame();

        Assert.False(frame.IsAnimating);
        Assert.Null(harness.GetTransitioningValue(box, "opacity"));
        Assert.Equal(["transitionrun opacity 0", "transitionstart opacity 0", "transitionend opacity 1"], events);
    }

    [Fact]
    public async Task TransitionWithDelay_StartsOnceTheDelayIsOver()
    {
        var (document, harness) = await OpenAsync("""
            <style>.box { height: 20px; opacity: 0; transition: opacity 1s linear 500ms } .box.on { opacity: 1 }</style>
            <div id="box" class="box"></div>
            """);
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();

        box.ClassList.Add("on");
        harness.PrepareFrame();

        Assert.Equal(["transitionrun opacity 0"], events);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(600));
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        harness.PrepareFrame();

        Assert.Equal(["transitionrun opacity 0", "transitionstart opacity 0", "transitionend opacity 1"], events);
    }

    [Fact]
    public async Task TransitionReversedWhileRunning_IsCancelledAndRunsBack()
    {
        var (document, harness) = await OpenAsync(Fade + """<div id="box" class="box"></div>""");
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();
        box.ClassList.Add("on");
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(400));
        harness.PrepareFrame();

        box.ClassList.Remove("on");
        harness.PrepareFrame();

        Assert.Equal(0.4, Opacity(harness, box), 2);
        Assert.Equal(["transitionrun opacity 0", "transitionstart opacity 0", "transitioncancel opacity 0.4", "transitionrun opacity 0", "transitionstart opacity 0"], events);
    }

    [Fact]
    public async Task ElementShownTheFirstTime_HasNoTransition()
    {
        var (document, harness) = await OpenAsync(Fade + """<div id="box" class="box on" style="display: none"></div>""");
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();

        box.RemoveAttribute("style");
        var frame = harness.PrepareFrame();

        Assert.False(frame.IsAnimating);
        Assert.Empty(events);
    }

    [Fact]
    public async Task ElementShownAgain_HasNoTransitionFromBeforeItWasHidden()
    {
        var (document, harness) = await OpenAsync(Fade + """<div id="box" class="box"></div>""");
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();
        box.SetAttribute("style", "display: none");
        harness.PrepareFrame();

        box.RemoveAttribute("style");
        box.ClassList.Add("on");
        harness.PrepareFrame();

        Assert.Empty(events);
    }

    [Fact]
    public async Task TransitionGivenByTheNewClass_Runs()
    {
        var (document, harness) = await OpenAsync("""
            <style>.box { height: 20px; opacity: 0 } .box.on { opacity: 1; transition: opacity 1s linear }</style>
            <div id="box" class="box"></div>
            """);
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();

        box.ClassList.Add("on");
        harness.PrepareFrame();

        Assert.Equal(["transitionrun opacity 0", "transitionstart opacity 0"], events);
    }

    [Fact]
    public async Task PropertyNotInterpolated_StillHasTheEvents()
    {
        var (document, harness) = await OpenAsync("""
            <style>.box { max-height: 0; overflow: hidden; transition: max-height 300ms } .box.open { max-height: 100px }</style>
            <div id="box" class="box"><div style="height: 50px"></div></div>
            """);
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();

        box.ClassList.Add("open");
        harness.PrepareFrame();
        harness.AdvanceTime(TimeSpan.FromMilliseconds(400));
        harness.PrepareFrame();

        Assert.Equal(["transitionrun max-height 0", "transitionstart max-height 0", "transitionend max-height 0.3"], events);
    }

    [Fact]
    public async Task ShorthandTransition_TransitionsItsLonghands()
    {
        var (document, harness) = await OpenAsync("""
            <style>.box { height: 20px; padding: 0; transition: padding 1s linear } .box.on { padding: 0 0 0 10px }</style>
            <div id="box" class="box"></div>
            """);
        var box = document.GetElementById("box")!;
        var events = Listen(box);
        harness.PrepareFrame();

        box.ClassList.Add("on");
        harness.PrepareFrame();

        Assert.Equal(["transitionrun padding-left 0", "transitionstart padding-left 0"], events);
    }

    private static double Opacity(IDomHarness harness, IElement element) =>
        double.Parse(harness.GetTransitioningValue(element, "opacity")!, CultureInfo.InvariantCulture);

    private static List<string> Listen(IElement element)
    {
        var events = new List<string>();

        foreach (var type in new[] { "transitionrun", "transitionstart", "transitionend", "transitioncancel" })
        {
            element.AddEventListener(type, (_, ev) =>
            {
                var transition = Assert.IsType<TransitionEvent>(ev);
                events.Add($"{ev.Type} {transition.PropertyName} {transition.ElapsedTime.ToString(CultureInfo.InvariantCulture)}");
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
