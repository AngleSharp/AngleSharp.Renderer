namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;

/// <summary>
/// The ResizeObserver: what it tells about the sizes of elements.
/// </summary>
public sealed class ResizeObserverTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    private sealed class Probe
    {
        public List<ResizeObserverEntry> Entries { get; } = [];

        public int Calls { get; private set; }

        public ResizeObserverEntry Last => Entries[^1];

        public void Callback(ResizeObserverEntry[] entries, ResizeObserver observer)
        {
            Calls++;
            Entries.AddRange(entries);
        }
    }

    private static async Task<(IDocument Document, IDomHarness Harness, IWindow Window)> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content($$"""
            <!doctype html><html><head><style>html, body { margin: 0; padding: 0; }</style></head><body>{{body}}</body></html>
            """));
        return (document, context.GetDomHarness(), document.DefaultView!);
    }

    [Fact]
    public async Task FirstCallbackTellsTheSizes()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="width: 100px; height: 50px; padding: 5px 10px; border: 2px solid"></div>""");
        var probe = new Probe();
        var target = document.GetElementById("t")!;

        new ResizeObserver(window, probe.Callback).Observe(target);

        var entry = Assert.Single(probe.Entries);
        Assert.Same(target, entry.Target);
        Assert.Equal((10d, 5d, 100d, 50d), (entry.ContentRect.X, entry.ContentRect.Y, entry.ContentRect.Width, entry.ContentRect.Height));
        Assert.Equal((100d, 50d), (entry.ContentBoxSize[0].InlineSize, entry.ContentBoxSize[0].BlockSize));
        Assert.Equal((124d, 64d), (entry.BorderBoxSize[0].InlineSize, entry.BorderBoxSize[0].BlockSize));
        Assert.Equal((100d, 50d), (entry.DevicePixelContentBoxSize[0].InlineSize, entry.DevicePixelContentBoxSize[0].BlockSize));
    }

    [Theory]
    [InlineData("""<div id="t" style="display: none"></div>""")]
    [InlineData("""<div id="t" style="height: 0; width: 0"></div>""")]
    [InlineData("""<span id="t">text</span>""")]
    public async Task ElementWithoutASizeIsNotTold(string body)
    {
        var (document, _, window) = await OpenAsync(body);
        var probe = new Probe();

        new ResizeObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        Assert.Empty(probe.Entries);
    }

    [Fact]
    public async Task InlineReplacedElementHasItsSize()
    {
        var (document, _, window) = await OpenAsync("""<button id="t" style="width: 40px; height: 20px; padding: 0; border: 0"></button>""");
        var probe = new Probe();

        new ResizeObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        Assert.Equal(40d, Assert.Single(probe.Entries).ContentRect.Width);
    }

    [Fact]
    public async Task ChangeOfTheSizeIsToldWithTheNextFrame()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="width: 100px; height: 50px"></div>""");
        var probe = new Probe();
        var target = document.GetElementById("t")!;
        new ResizeObserver(window, probe.Callback).Observe(target);

        target.SetAttribute("style", "width: 100px; height: 70px");
        harness.PrepareFrame();

        Assert.Equal(2, probe.Entries.Count);
        Assert.Equal(70d, probe.Last.ContentRect.Height);
    }

    [Fact]
    public async Task ChangeOfTheSizeIsToldWithoutAFrame()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="width: 100px; height: 50px"></div>""");
        var probe = new Probe();
        var target = document.GetElementById("t")!;
        new ResizeObserver(window, probe.Callback).Observe(target);

        target.SetAttribute("style", "width: 100px; height: 70px");
        document.Body!.AppendChild(document.CreateElement("div"));

        Assert.Equal(70d, probe.Last.ContentRect.Height);
    }

    [Fact]
    public async Task HidingAndShowingIsTold()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="width: 100px; height: 50px"></div>""");
        var probe = new Probe();
        var target = document.GetElementById("t")!;
        new ResizeObserver(window, probe.Callback).Observe(target);

        target.SetAttribute("style", "display: none");
        harness.PrepareFrame();
        Assert.Equal(0d, probe.Last.ContentRect.Width);
        Assert.Equal(0d, probe.Last.BorderBoxSize[0].InlineSize);

        target.SetAttribute("style", "width: 100px; height: 50px");
        harness.PrepareFrame();
        Assert.Equal(100d, probe.Last.ContentRect.Width);
        Assert.Equal(3, probe.Entries.Count);
    }

    [Fact]
    public async Task MovingWithoutChangingTheSizeIsNotTold()
    {
        var (document, harness, window) = await OpenAsync("""<div id="spacer" style="height: 0"></div><div id="t" style="width: 100px; height: 50px"></div><div style="height: 1000px"></div>""");
        var probe = new Probe();
        new ResizeObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        document.GetElementById("spacer")!.SetAttribute("style", "height: 300px");
        harness.SetScrollTop(document.DocumentElement, 100, 1000);
        harness.PrepareFrame();

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task OnlyTheObservedBoxCounts()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="width: 100px; height: 50px; padding: 0"></div>""");
        var content = new Probe();
        var border = new Probe();
        var target = document.GetElementById("t")!;
        new ResizeObserver(window, content.Callback).Observe(target);
        new ResizeObserver(window, border.Callback).Observe(target, new ResizeObserverOptions { Box = "border-box" });

        // the same border box from a smaller content box
        target.SetAttribute("style", "box-sizing: border-box; width: 100px; height: 50px; padding: 10px");
        harness.PrepareFrame();

        Assert.Equal(2, content.Entries.Count);
        Assert.Single(border.Entries);
    }

    [Fact]
    public async Task UnknownBoxIsRefused()
    {
        var (document, _, window) = await OpenAsync("""<div id="t"></div>""");

        Assert.Throws<ArgumentException>(() => new ResizeObserver(window, new Probe().Callback).Observe(document.GetElementById("t")!, new ResizeObserverOptions { Box = "padding-box" }));
    }

    [Fact]
    public async Task VerticalWritingModeSwapsTheDirections()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="width: 100px; height: 50px; writing-mode: vertical-rl"></div>""");
        var probe = new Probe();

        new ResizeObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        var entry = Assert.Single(probe.Entries);
        Assert.Equal((100d, 50d), (entry.ContentRect.Width, entry.ContentRect.Height));
        Assert.Equal((50d, 100d), (entry.ContentBoxSize[0].InlineSize, entry.ContentBoxSize[0].BlockSize));
    }

    [Fact]
    public async Task ElementsOfACallAreTogether()
    {
        var (document, _, window) = await OpenAsync("""<div id="a" style="height: 10px"></div><div id="b" style="height: 20px"></div>""");
        var probe = new Probe();
        var observer = new ResizeObserver(window, probe.Callback);

        // observing one after the other tells one after the other without an event loop
        observer.Observe(document.GetElementById("a")!);
        observer.Observe(document.GetElementById("b")!);

        Assert.Equal(2, probe.Entries.Count);
    }

    [Fact]
    public async Task ChangeOfADeeperElementIsToldInTheSameGo()
    {
        var (document, harness, window) = await OpenAsync("""<div id="outer" style="height: 10px"><div id="inner" style="height: 5px"></div></div>""");
        var outer = document.GetElementById("outer")!;
        var inner = document.GetElementById("inner")!;
        var told = new List<string>();
        var errors = 0;
        window.AddEventListener("error", (_, _) => errors++);
        var observer = new ResizeObserver(window, (entries, _) =>
        {
            foreach (var entry in entries)
            {
                told.Add(entry.Target.Id!);

                if (entry.Target.Id == "outer" && entry.ContentRect.Height == 30d)
                {
                    inner.SetAttribute("style", "height: 15px");
                }
            }
        });
        observer.Observe(outer);
        observer.Observe(inner);
        told.Clear();

        outer.SetAttribute("style", "height: 30px");
        harness.PrepareFrame();

        Assert.Equal(["outer", "inner"], told);
        Assert.Equal(0, errors);
    }

    [Fact]
    public async Task CallbackKeepsChangingItsOwnElementEndsAndIsReported()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 10px"></div>""");
        var target = document.GetElementById("t")!;
        var calls = 0;
        var messages = new List<string>();
        window.AddEventListener("error", (_, ev) => messages.Add(((ErrorEvent)ev).Message));

        // never settles, and has to give back control all the same
        new ResizeObserver(window, (_, _) => target.SetAttribute("style", $"height: {10 + (++calls * 10)}px")).Observe(target);

        Assert.Equal(1, calls);
        Assert.Contains(messages, m => m.Contains("ResizeObserver loop"));

        harness.PrepareFrame();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ElementInAFrameHasTheSizeTheFrameGivesIt()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device).WithDefaultLoader(new AngleSharp.Io.LoaderOptions { IsResourceLoadingEnabled = true }));
        // the frame is loaded with the page, which is complete only once its frames are
        var document = await context.OpenAsync(request => request.Address("https://example.com/").Content("""<!doctype html><html><body style="margin: 0"><iframe srcdoc='<!doctype html><html><body style="margin: 0"><div id="t" style="height: 50px"></div></body></html>' style="border: 0; display: block; width: 100px; height: 100px"></iframe></body></html>"""));
        var frame = (AngleSharp.Html.Dom.IHtmlInlineFrameElement)document.QuerySelector("iframe")!;
        Assert.Equal(DocumentReadyState.Complete, document.ReadyState);
        var probe = new Probe();

        new ResizeObserver(frame.ContentWindow!, probe.Callback).Observe(frame.ContentDocument!.GetElementById("t")!);
        Assert.NotEmpty(probe.Entries);

        Assert.Equal(100d, probe.Entries[0].ContentRect.Width);
    }

    [Fact]
    public async Task UnobservedElementIsNotToldAnymore()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 10px"></div>""");
        var probe = new Probe();
        var observer = new ResizeObserver(window, probe.Callback);
        var target = document.GetElementById("t")!;
        observer.Observe(target);

        observer.Unobserve(target);
        target.SetAttribute("style", "height: 20px");
        harness.PrepareFrame();

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task DisconnectedObserverIsNotToldAnymore()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 10px"></div>""");
        var probe = new Probe();
        var observer = new ResizeObserver(window, probe.Callback);
        var target = document.GetElementById("t")!;
        observer.Observe(target);

        observer.Disconnect();
        target.SetAttribute("style", "height: 20px");
        harness.PrepareFrame();

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task ObservingTwiceTellsOnce()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="height: 10px"></div>""");
        var probe = new Probe();
        var observer = new ResizeObserver(window, probe.Callback);
        var target = document.GetElementById("t")!;

        observer.Observe(target);
        observer.Observe(target);

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task ObservingAgainWithAnotherBoxStartsOver()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="height: 10px"></div>""");
        var probe = new Probe();
        var observer = new ResizeObserver(window, probe.Callback);
        var target = document.GetElementById("t")!;

        observer.Observe(target);
        observer.Observe(target, new ResizeObserverOptions { Box = "border-box" });

        Assert.Equal(2, probe.Entries.Count);
    }

    [Fact]
    public async Task ResizeIsToldBeforeIntersection()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="height: 10px"></div>""");
        var told = new List<string>();
        var target = document.GetElementById("t")!;

        new IntersectionObserver(window, (_, _) => told.Add("intersection")).Observe(target);
        told.Clear();
        new ResizeObserver(window, (_, _) => told.Add("resize")).Observe(target);

        Assert.Equal(["resize"], told);
        target.SetAttribute("style", "height: 300px");
        Assert.Equal(2, told.Count);
        Assert.Equal("resize", told[1]);
    }

    [Fact]
    public async Task ObservingDoesNotInvalidateAnything()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 50px"></div>""");
        var invalidated = 0;
        harness.PrepareFrame();
        harness.PaintInvalidated += (_, _) => invalidated++;

        new ResizeObserver(window, new Probe().Callback).Observe(document.GetElementById("t")!);

        Assert.Equal(0, invalidated);
    }
}
