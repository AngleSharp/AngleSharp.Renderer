namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;

/// <summary>
/// The IntersectionObserver: what it tells about boxes in the viewport or in another box.
/// </summary>
public sealed class IntersectionObserverTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    private sealed class Probe
    {
        public List<IntersectionObserverEntry> Entries { get; } = [];

        public IntersectionObserverEntry Last => Entries[^1];

        public void Callback(IntersectionObserverEntry[] entries, IntersectionObserver observer) => Entries.AddRange(entries);
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
    public async Task FirstCallbackTellsATargetInView()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="width: 100px; height: 50px"></div>""");
        var probe = new Probe();
        var target = document.GetElementById("t")!;

        new IntersectionObserver(window, probe.Callback).Observe(target);

        var entry = Assert.Single(probe.Entries);
        Assert.True(entry.IsIntersecting);
        Assert.Equal(1d, entry.IntersectionRatio);
        Assert.Same(target, entry.Target);
        Assert.Equal(100d, entry.BoundingClientRect.Width);
        Assert.Equal(50d, entry.IntersectionRect.Height);
        Assert.Equal(200d, entry.RootBounds!.Height);
    }

    [Fact]
    public async Task FirstCallbackTellsATargetOutOfView()
    {
        var (document, _, window) = await OpenAsync("""<div style="height: 1000px"></div><div id="t" style="height: 50px"></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        var entry = Assert.Single(probe.Entries);
        Assert.False(entry.IsIntersecting);
        Assert.Equal(0d, entry.IntersectionRatio);
        Assert.Equal(1000d, entry.BoundingClientRect.Top);
        Assert.Equal(0d, entry.IntersectionRect.Width);
    }

    [Fact]
    public async Task ScrollingATargetIntoViewIsTold()
    {
        var (document, harness, window) = await OpenAsync("""<div style="height: 1000px"></div><div id="t" style="height: 50px"></div>""");
        var probe = new Probe();
        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        harness.SetScrollTop(document.DocumentElement, 900, 850);

        Assert.Equal(2, probe.Entries.Count);
        Assert.True(probe.Last.IsIntersecting);
        Assert.Equal(1d, probe.Last.IntersectionRatio);
    }

    [Fact]
    public async Task PartOfATargetIsTold()
    {
        var (document, _, window) = await OpenAsync("""<div style="height: 180px"></div><div id="t" style="height: 40px"></div><div style="height: 1000px"></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        Assert.Equal(0.5d, probe.Last.IntersectionRatio, 3);
        Assert.Equal(20d, probe.Last.IntersectionRect.Height, 3);
    }

    [Fact]
    public async Task NothingIsToldWithoutAChange()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 50px"></div><div style="height: 1000px"></div>""");
        var probe = new Probe();
        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        harness.SetScrollTop(document.DocumentElement, 10, 1000);
        harness.PrepareFrame();

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task OnlyCrossedThresholdsAreTold()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 100px"></div><div style="height: 1000px"></div>""");
        var probe = new Probe();
        var observer = new IntersectionObserver(window, probe.Callback, new IntersectionObserverOptions { Threshold = new[] { 0.75d, 0.25d } });
        observer.Observe(document.GetElementById("t")!);
        Assert.Equal([0.25d, 0.75d], observer.Thresholds);
        Assert.Single(probe.Entries);

        harness.SetScrollTop(document.DocumentElement, 10, 1000);
        Assert.Single(probe.Entries);

        harness.SetScrollTop(document.DocumentElement, 50, 1000);
        Assert.Equal(2, probe.Entries.Count);
        Assert.Equal(0.5d, probe.Last.IntersectionRatio, 3);
    }

    [Theory]
    [InlineData("100px", 200d)]
    [InlineData("0px 0px 100px", 200d)]
    [InlineData("50%", 200d)]
    public async Task RootMarginBringsATargetIntoView(string margin, double top)
    {
        var (document, _, window) = await OpenAsync($$"""<div style="height: {{top}}px"></div><div id="t" style="height: 50px"></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback, new IntersectionObserverOptions { RootMargin = margin }).Observe(document.GetElementById("t")!);

        Assert.True(probe.Last.IsIntersecting);
        Assert.Equal(1d, probe.Last.IntersectionRatio);
    }

    [Fact]
    public async Task NegativeRootMarginTakesATargetOutOfView()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="height: 10px"></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback, new IntersectionObserverOptions { RootMargin = "-50px 0px 0px" }).Observe(document.GetElementById("t")!);

        Assert.False(probe.Last.IsIntersecting);
        Assert.Equal(150d, probe.Last.RootBounds!.Height);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("10em")]
    [InlineData("1px 2px 3px 4px 5px")]
    public async Task InvalidRootMarginIsRefused(string margin)
    {
        var (_, _, window) = await OpenAsync(string.Empty);

        Assert.Throws<DomException>(() => new IntersectionObserver(window, new Probe().Callback, new IntersectionObserverOptions { RootMargin = margin }));
    }

    [Fact]
    public async Task RootMarginIsNormalized()
    {
        var (_, _, window) = await OpenAsync(string.Empty);

        var observer = new IntersectionObserver(window, new Probe().Callback, new IntersectionObserverOptions { RootMargin = "1px 10%" });

        Assert.Equal("1px 10% 1px 10%", observer.RootMargin);
    }

    [Fact]
    public async Task ThresholdOutOfRangeIsRefused()
    {
        var (_, _, window) = await OpenAsync(string.Empty);

        Assert.Throws<ArgumentOutOfRangeException>(() => new IntersectionObserver(window, new Probe().Callback, new IntersectionObserverOptions { Threshold = 1.5d }));
    }

    [Fact]
    public async Task TargetClippedByABoxIsNotInView()
    {
        var (document, _, window) = await OpenAsync("""<div style="height: 50px; overflow: auto"><div style="height: 100px"></div><div id="t" style="height: 10px"></div></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        Assert.False(probe.Last.IsIntersecting);
    }

    [Fact]
    public async Task PositionedTargetEscapingAClipIsInView()
    {
        var (document, _, window) = await OpenAsync("""<div style="height: 50px; overflow: auto"><div id="t" style="position: absolute; top: 100px; height: 10px; width: 10px"></div></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        Assert.True(probe.Last.IsIntersecting);
    }

    [Fact]
    public async Task ScrollingAContainerBringsATargetIntoView()
    {
        var (document, harness, window) = await OpenAsync("""<div id="box" style="height: 50px; overflow: auto"><div style="height: 100px"></div><div id="t" style="height: 10px"></div></div>""");
        var probe = new Probe();
        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);
        Assert.False(probe.Last.IsIntersecting);

        harness.SetScrollTop(document.GetElementById("box")!, 60, 60);

        Assert.True(probe.Last.IsIntersecting);
    }

    [Fact]
    public async Task TargetIsComparedWithAnElementAsRoot()
    {
        var (document, _, window) = await OpenAsync("""<div id="box" style="height: 50px; overflow: auto"><div style="height: 100px"></div><div id="t" style="height: 10px"></div></div>""");
        var probe = new Probe();
        var root = document.GetElementById("box")!;
        var observer = new IntersectionObserver(window, probe.Callback, new IntersectionObserverOptions { Root = root, RootMargin = "60px 0px" });

        observer.Observe(document.GetElementById("t")!);

        Assert.Same(root, observer.Root);
        Assert.True(probe.Last.IsIntersecting);
        Assert.Equal(170d, probe.Last.RootBounds!.Height);
    }

    [Fact]
    public async Task TargetOutsideOfTheRootElementDoesNotIntersect()
    {
        var (document, _, window) = await OpenAsync("""<div id="box" style="height: 50px"></div><div id="t" style="height: 10px"></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback, new IntersectionObserverOptions { Root = document.GetElementById("box") }).Observe(document.GetElementById("t")!);

        Assert.False(probe.Last.IsIntersecting);
    }

    [Fact]
    public async Task TargetNotRenderedDoesNotIntersect()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="display: none"></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        Assert.False(probe.Last.IsIntersecting);
        Assert.Equal(0d, probe.Last.BoundingClientRect.Width);
    }

    [Fact]
    public async Task TargetWithoutAreaInViewIntersects()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="height: 0"></div>""");
        var probe = new Probe();

        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);

        Assert.True(probe.Last.IsIntersecting);
        Assert.Equal(1d, probe.Last.IntersectionRatio);
    }

    [Fact]
    public async Task ChangesOfTheDocumentAreToldWithTheNextFrame()
    {
        var (document, harness, window) = await OpenAsync("""<div id="spacer" style="height: 0"></div><div id="t" style="height: 50px"></div>""");
        var probe = new Probe();
        new IntersectionObserver(window, probe.Callback).Observe(document.GetElementById("t")!);
        Assert.True(probe.Last.IsIntersecting);

        document.GetElementById("spacer")!.SetAttribute("style", "height: 1000px");
        harness.PrepareFrame();

        Assert.False(probe.Last.IsIntersecting);
    }

    [Fact]
    public async Task UnobservedTargetIsNotToldAnymore()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 50px"></div><div style="height: 1000px"></div>""");
        var probe = new Probe();
        var observer = new IntersectionObserver(window, probe.Callback);
        var target = document.GetElementById("t")!;
        observer.Observe(target);

        observer.Unobserve(target);
        harness.SetScrollTop(document.DocumentElement, 500, 1000);

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task DisconnectedObserverIsNotToldAnymore()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 50px"></div><div style="height: 1000px"></div>""");
        var probe = new Probe();
        var observer = new IntersectionObserver(window, probe.Callback);
        observer.Observe(document.GetElementById("t")!);

        observer.Disconnect();
        harness.SetScrollTop(document.DocumentElement, 500, 1000);

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task ObservingTwiceTellsOnce()
    {
        var (document, _, window) = await OpenAsync("""<div id="t" style="height: 50px"></div>""");
        var probe = new Probe();
        var observer = new IntersectionObserver(window, probe.Callback);
        var target = document.GetElementById("t")!;

        observer.Observe(target);
        observer.Observe(target);

        Assert.Single(probe.Entries);
    }

    [Fact]
    public async Task ObservingDoesNotInvalidateAnything()
    {
        var (document, harness, window) = await OpenAsync("""<div id="t" style="height: 50px"></div>""");
        var invalidated = 0;
        harness.PrepareFrame();
        harness.PaintInvalidated += (_, _) => invalidated++;

        new IntersectionObserver(window, new Probe().Callback).Observe(document.GetElementById("t")!);

        Assert.Equal(0, invalidated);
    }
}
