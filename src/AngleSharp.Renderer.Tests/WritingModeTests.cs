namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// <c>writing-mode</c> and <c>text-orientation</c>, asserted on the display list: content of a
/// vertical flow is laid out horizontally and placed by transforms, so every check composes the
/// transforms around a command to get its physical geometry and glyph orientation.
/// </summary>
public sealed class WritingModeTests
{
    private static readonly RenderColor Red = new(255, 0, 0);
    private static readonly RenderColor Green = new(0, 255, 0);
    private static readonly RenderColor Blue = new(0, 0, 255);

    [Fact]
    public async Task VerticalBlock_IsAsWideAsItsLinesAndAsTallAsItsHeight()
    {
        var displayList = await LayoutAsync("""
            <div style="writing-mode:vertical-rl; height:100px; background-color:#ff0000">Hello world</div>
            """);

        var box = PhysicalRect(displayList, SingleFill(displayList, Red));

        Assert.Equal(new RenderRect(0f, 0f, await HorizontalLineHeightAsync(), 100f), box);
    }

    [Fact]
    public async Task VerticalRl_RotatesTextClockwise()
    {
        var displayList = await LayoutAsync("""<div style="writing-mode:vertical-rl; height:100px">Hello</div>""");

        var glyphs = Composite(displayList, SingleText(displayList, "Hello"));

        Assert.Equal((0f, 1f, -1f, 0f), (glyphs.A, glyphs.B, glyphs.C, glyphs.D));
    }

    [Fact]
    public async Task VerticalLr_KeepsGlyphsRotatedClockwiseRatherThanMirrored()
    {
        var displayList = await LayoutAsync("""<div style="writing-mode:vertical-lr; height:100px">Hello</div>""");

        var glyphs = Composite(displayList, SingleText(displayList, "Hello"));

        Assert.Equal((0f, 1f, -1f, 0f), (glyphs.A, glyphs.B, glyphs.C, glyphs.D));
    }

    [Fact]
    public async Task SidewaysLr_RotatesTextCounterClockwise()
    {
        var displayList = await LayoutAsync("""<div style="writing-mode:sideways-lr; height:100px">Hello</div>""");

        var glyphs = Composite(displayList, SingleText(displayList, "Hello"));

        Assert.Equal((0f, -1f, 1f, 0f), (glyphs.A, glyphs.B, glyphs.C, glyphs.D));
    }

    [Theory]
    [InlineData("vertical-rl", 170f)]
    [InlineData("vertical-lr", 0f)]
    public async Task VerticalFlow_StacksBlocksFromItsBlockStartSide(string mode, float expectedX)
    {
        var displayList = await LayoutAsync($"""
            <div style="writing-mode:{mode}; width:200px; height:100px">
                <div style="width:30px; height:10px; background-color:#00ff00"></div>
                <div style="width:20px; height:10px; background-color:#0000ff"></div>
            </div>
            """);

        var first = PhysicalRect(displayList, SingleFill(displayList, Green));
        var second = PhysicalRect(displayList, SingleFill(displayList, Blue));

        // Physical width and height keep their meaning inside the vertical flow.
        Assert.Equal(new RenderRect(expectedX, 0f, 30f, 10f), first);
        Assert.Equal(mode == "vertical-rl" ? 150f : 30f, second.X);
    }

    [Fact]
    public async Task VerticalFlow_MapsPhysicalMarginsOntoTheirSides()
    {
        var displayList = await LayoutAsync("""
            <div style="writing-mode:vertical-rl; width:200px; height:100px">
                <div style="margin-top:7px; margin-right:11px; width:30px; height:10px; background-color:#00ff00"></div>
            </div>
            """);

        Assert.Equal(new RenderRect(159f, 7f, 30f, 10f), PhysicalRect(displayList, SingleFill(displayList, Green)));
    }

    [Fact]
    public async Task HorizontalContentInsideVerticalFlow_IsUpright()
    {
        var displayList = await LayoutAsync("""
            <div style="writing-mode:vertical-rl; height:100px"><p style="writing-mode:horizontal-tb; margin:0">Hi</p></div>
            """);

        var glyphs = Composite(displayList, SingleText(displayList, "Hi"));

        Assert.Equal((1f, 0f, 0f, 1f), (glyphs.A, glyphs.B, glyphs.C, glyphs.D));
    }

    [Theory]
    [InlineData("vertical-rl")]
    [InlineData("vertical-lr")]
    public async Task MixedOrientation_SetsCjkUprightOneGlyphPerEm(string mode)
    {
        var displayList = await LayoutAsync($"""
            <div style="writing-mode:{mode}; font-size:20px; background-color:#ff0000">漢字</div>
            """);

        var glyphs = displayList.Commands.OfType<DrawTextCommand>().ToList();

        Assert.Equal(["漢", "字"], glyphs.Select(glyph => glyph.Text));
        Assert.All(glyphs, glyph =>
        {
            var composite = Composite(displayList, glyph);
            Assert.Equal((1f, 0f, 0f, 1f), (composite.A, composite.B, composite.C, composite.D));
        });

        // Two upright glyphs advance one em each along the vertical inline axis.
        Assert.Equal(40f, PhysicalRect(displayList, SingleFill(displayList, Red)).Height, precision: 2);
    }

    [Fact]
    public async Task MixedOrientation_SetsLatinSideways()
    {
        var displayList = await LayoutAsync("""<div style="writing-mode:vertical-rl; height:100px">漢A</div>""");

        var latin = Composite(displayList, SingleText(displayList, "A"));

        Assert.Equal((0f, 1f, -1f, 0f), (latin.A, latin.B, latin.C, latin.D));
    }

    [Fact]
    public async Task UprightOrientation_SetsLatinUprightOneGlyphPerEm()
    {
        var displayList = await LayoutAsync("""
            <div style="writing-mode:vertical-rl; text-orientation:upright; font-size:20px; background-color:#ff0000">AB</div>
            """);

        var glyphs = displayList.Commands.OfType<DrawTextCommand>().ToList();

        Assert.Equal(["A", "B"], glyphs.Select(glyph => glyph.Text));
        Assert.All(glyphs, glyph =>
        {
            var composite = Composite(displayList, glyph);
            Assert.Equal((1f, 0f, 0f, 1f), (composite.A, composite.B, composite.C, composite.D));
        });
        Assert.Equal(40f, PhysicalRect(displayList, SingleFill(displayList, Red)).Height, precision: 2);
    }

    [Fact]
    public async Task SidewaysOrientation_TurnsCjkSideways()
    {
        var displayList = await LayoutAsync("""<div style="writing-mode:vertical-rl; text-orientation:sideways; height:100px">漢字</div>""");

        var glyphs = Composite(displayList, SingleText(displayList, "漢字"));

        Assert.Equal((0f, 1f, -1f, 0f), (glyphs.A, glyphs.B, glyphs.C, glyphs.D));
    }

    [Fact]
    public async Task LegacyTbRl_IsVerticalRl()
    {
        var displayList = await LayoutAsync("""<div style="writing-mode:tb-rl; height:100px">Hello</div>""");

        var glyphs = Composite(displayList, SingleText(displayList, "Hello"));

        Assert.Equal((0f, 1f, -1f, 0f), (glyphs.A, glyphs.B, glyphs.C, glyphs.D));
    }

    [Fact]
    public async Task VerticalPage_StartsAtTheRightEdgeOfTheViewport()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content("""
            <html style="writing-mode:vertical-rl"><body style="margin:0">
                <div style="width:30px; height:10px; background-color:#00ff00"></div>
            </body></html>
            """));
        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 300, FontSize = 16f });

        Assert.Equal(new RenderRect(370f, 0f, 30f, 10f), PhysicalRect(displayList, SingleFill(displayList, Green)));
    }

    [Fact]
    public async Task VerticalFloat_IsAsWideAsItsLines()
    {
        var displayList = await LayoutAsync("""
            <div style="float:left; writing-mode:vertical-rl; height:100px; background-color:#ff0000">Hello</div>
            """);

        Assert.Equal(await HorizontalLineHeightAsync(), PhysicalRect(displayList, SingleFill(displayList, Red)).Width, precision: 2);
    }

    /// <summary>The height of one line of the same text laid out horizontally.</summary>
    private static async Task<float> HorizontalLineHeightAsync()
    {
        var displayList = await LayoutAsync("""<div style="background-color:#ff0000">Hello</div>""");
        return MathF.Round(SingleFill(displayList, Red).Rect.Height, 2);
    }

    private static async Task<DisplayList> LayoutAsync(string bodyHtml)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content($"<html><body style=\"margin:0\">{bodyHtml}</body></html>"));

        return new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 400,
            ViewPortHeight = 300,
            FontSize = 16f,
        });
    }

    /// <summary>The composition of every transform open around <paramref name="target"/>.</summary>
    private static RenderTransform2D Composite(DisplayList displayList, RenderCommand target)
    {
        var stack = new Stack<RenderTransform2D>();
        var current = RenderTransform2D.Translate(0f, 0f);

        foreach (var command in displayList.Commands)
        {
            if (ReferenceEquals(command, target))
            {
                return current;
            }

            switch (command)
            {
                case PushTransformCommand push:
                    stack.Push(current);
                    current = Multiply(current, push.Transform);
                    break;
                case PopTransformCommand:
                    current = stack.Pop();
                    break;
            }
        }

        throw new InvalidOperationException("The command is not part of the display list.");
    }

    private static RenderTransform2D Multiply(RenderTransform2D outer, RenderTransform2D inner) => new(
        (outer.A * inner.A) + (outer.C * inner.B),
        (outer.B * inner.A) + (outer.D * inner.B),
        (outer.A * inner.C) + (outer.C * inner.D),
        (outer.B * inner.C) + (outer.D * inner.D),
        (outer.A * inner.E) + (outer.C * inner.F) + outer.E,
        (outer.B * inner.E) + (outer.D * inner.F) + outer.F);

    private static RenderRect PhysicalRect(DisplayList displayList, FillRectCommand fill)
    {
        var transform = Composite(displayList, fill);
        var rect = fill.Rect;
        var corners = new[] { (rect.X, rect.Y), (rect.X + rect.Width, rect.Y), (rect.X, rect.Y + rect.Height), (rect.X + rect.Width, rect.Y + rect.Height) }
            .Select(corner => ((transform.A * corner.Item1) + (transform.C * corner.Item2) + transform.E, (transform.B * corner.Item1) + (transform.D * corner.Item2) + transform.F))
            .ToArray();
        var left = corners.Min(corner => corner.Item1);
        var top = corners.Min(corner => corner.Item2);

        return new RenderRect(
            MathF.Round(left, 2),
            MathF.Round(top, 2),
            MathF.Round(corners.Max(corner => corner.Item1) - left, 2),
            MathF.Round(corners.Max(corner => corner.Item2) - top, 2));
    }

    private static FillRectCommand SingleFill(DisplayList displayList, RenderColor color) =>
        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(fill => fill.Color.Equals(color)));

    private static DrawTextCommand SingleText(DisplayList displayList, string text) =>
        Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(command => command.Text == text));
}
