namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Transitions and animations of <c>transform</c>, <c>filter</c> and the individual transform
/// properties, driven through the DOM harness's virtual clock.
/// </summary>
public sealed class TransformFilterAnimationTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    [Fact]
    public async Task TransformTransition_InterpolatesMatchingFunctions()
    {
        var (document, harness) = await OpenAsync("""
            #target { transform: translateX(0px); transition: transform 1s linear; }
            #target:hover { transform: translateX(100px); }
            """);

        harness.MousePosition = (10, 10);
        Assert.Null(TryTransform(document));

        harness.AdvanceTime(TimeSpan.FromMilliseconds(250));
        Assert.Equal(25f, Transform(document).E, precision: 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(750));
        Assert.Equal(100f, Transform(document).E, precision: 2);
    }

    [Fact]
    public async Task TransformTransition_FromNoneUsesTheIdentityFunctions()
    {
        var (document, harness) = await OpenAsync("""
            #target { transition: transform 1s linear; }
            #target:hover { transform: rotate(90deg) scale(3); }
            """);

        harness.MousePosition = (10, 10);
        Assert.Null(TryTransform(document));

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        var halfway = Transform(document);

        // rotate(45deg) scale(2): the first column is 2 * (cos 45, sin 45).
        Assert.Equal(2f * MathF.Cos(MathF.PI / 4f), halfway.A, precision: 3);
        Assert.Equal(2f * MathF.Sin(MathF.PI / 4f), halfway.B, precision: 3);
    }

    [Fact]
    public async Task TransformTransition_RotatesThroughFullTurns()
    {
        var (document, harness) = await OpenAsync("""
            #target { transform: rotate(0deg); transition: transform 1s linear; }
            #target:hover { transform: rotate(360deg); }
            """);

        harness.MousePosition = (10, 10);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        var halfway = Transform(document);

        Assert.Equal(-1f, halfway.A, precision: 3);
        Assert.Equal(0f, halfway.B, precision: 3);
    }

    [Fact]
    public async Task TransformTransition_MismatchedListsInterpolateTheMatrix()
    {
        var (document, harness) = await OpenAsync("""
            #target { transform: translate(0px, 0px); transition: transform 1s linear; }
            #target:hover { transform: scale(3) translateX(20px); }
            """);

        harness.MousePosition = (10, 10);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        var halfway = Transform(document);

        // scale(3) translateX(20px) is matrix(3, 0, 0, 3, 60, 0): halfway is scale 2, offset 30.
        Assert.Equal(2f, halfway.A, precision: 3);
        Assert.Equal(2f, halfway.D, precision: 3);
        Assert.Equal(0f, halfway.B, precision: 3);
    }

    [Fact]
    public async Task TranslateProperty_Transitions()
    {
        var (document, harness) = await OpenAsync("""
            #target { translate: 0px 0px; transition: translate 1s linear; }
            #target:hover { translate: 40px 10px; }
            """);

        harness.MousePosition = (10, 10);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        var halfway = Transform(document);

        Assert.Equal(20f, halfway.E, precision: 2);
        Assert.Equal(5f, halfway.F, precision: 2);
    }

    [Fact]
    public async Task TransformAnimation_InterpolatesBetweenKeyframes()
    {
        var (document, harness) = await OpenAsync("""
            @keyframes slide { from { transform: translateY(0px); } to { transform: translateY(80px); } }
            #target { animation: slide 2s linear; }
            """);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.Equal(20f, Transform(document).F, precision: 2);
    }

    [Fact]
    public async Task FilterTransition_InterpolatesFunctionArguments()
    {
        var (document, harness) = await OpenAsync("""
            #target { filter: blur(0px) grayscale(0); transition: filter 1s linear; }
            #target:hover { filter: blur(10px) grayscale(1); }
            """);

        harness.MousePosition = (10, 10);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        var functions = Filter(document);

        Assert.Equal(2, functions.Count);
        Assert.Equal(5f, functions[0].Amount, precision: 2);
        Assert.Equal(0.5f, functions[1].Amount, precision: 2);
    }

    [Fact]
    public async Task FilterTransition_FromNoneUsesTheIdentityFunctions()
    {
        var (document, harness) = await OpenAsync("""
            #target { transition: filter 1s linear; }
            #target:hover { filter: brightness(3); }
            """);

        harness.MousePosition = (10, 10);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));

        Assert.Equal(2f, Assert.Single(Filter(document)).Amount, precision: 2);
    }

    [Fact]
    public async Task FilterTransition_InterpolatesDropShadowColorAndOffsets()
    {
        var (document, harness) = await OpenAsync("""
            #target { filter: drop-shadow(0px 0px 0px rgb(0, 0, 0)); transition: filter 1s linear; }
            #target:hover { filter: drop-shadow(10px 20px 4px rgb(200, 100, 0)); }
            """);

        harness.MousePosition = (10, 10);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        var shadow = Assert.Single(Filter(document));

        Assert.Equal(5f, shadow.OffsetX, precision: 2);
        Assert.Equal(10f, shadow.OffsetY, precision: 2);
        Assert.Equal(new RenderColor(100, 50, 0), shadow.Color);
    }

    [Fact]
    public async Task FilterTransition_MismatchedListsSwitchHalfway()
    {
        var (document, harness) = await OpenAsync("""
            #target { filter: blur(2px); transition: filter 1s linear; }
            #target:hover { filter: sepia(1); }
            """);

        harness.MousePosition = (10, 10);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(400));
        Assert.Equal(RenderFilterFunctionKind.Blur, Assert.Single(Filter(document)).Kind);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(200));
        Assert.Equal(RenderFilterFunctionKind.Sepia, Assert.Single(Filter(document)).Kind);
    }

    private static RenderTransform2D Transform(IDocument document) =>
        TryTransform(document) ?? throw new Xunit.Sdk.XunitException("No transform was pushed.");

    private static RenderTransform2D? TryTransform(IDocument document)
    {
        var displayList = new HtmlRenderer().BuildDisplayList(document, Device);
        return displayList.Commands.OfType<PushTransformCommand>().Select(command => (RenderTransform2D?)command.Transform).FirstOrDefault();
    }

    private static IReadOnlyList<RenderFilterFunction> Filter(IDocument document)
    {
        var displayList = new HtmlRenderer().BuildDisplayList(document, Device);
        return Assert.Single(displayList.Commands.OfType<PushFilterCommand>()).Functions;
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string css)
    {
        var configuration = Configuration.Default.WithCss().WithRenderDevice(Device);
        var context = BrowsingContext.New(configuration);
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                {{css}}
            </style></head><body>
                <div id="target" style="width:60px; height:40px; background-color:#ff0000;"></div>
            </body></html>
            """));
        return (document, document.Context.GetDomHarness());
    }
}
