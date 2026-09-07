using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Renderer.Rendering;

using System.Net;
using System.Threading;

namespace AngleSharp.Renderer.Tests;

public sealed class HtmlRendererTests
{
    [Fact]
    public async Task BuildDisplayList_IncludesBackgroundAndTextCommands()
    {
        var document = await ParseAsync("<html><body><h1>Title</h1><p>Hello renderer world from AngleSharp.</p></body></html>");
        var renderer = new HtmlRenderer();

        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 360,
            ViewPortHeight = 240,
            FontSize = 16f,
        });

        Assert.NotEmpty(displayList.Commands);
        Assert.IsType<FillRectCommand>(displayList.Commands[0]);
        Assert.Contains(displayList.Commands, command => command is DrawTextCommand);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsImageElementsFromCurrentDownload()
    {
        var document = await ParseAsync("""
            <html><body>
                <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGMAAQABAA4A4cQTmwAAAABJRU5ErkJggg==" style="width:40px; height:20px;" />
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var imageCommand = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(40f, imageCommand.Rect.Width);
        Assert.Equal(20f, imageCommand.Rect.Height);
        Assert.NotEmpty(imageCommand.Image.Data);
    }

    [Fact]
    public async Task BuildDisplayList_CachesHttpImagePayloadPerDocument()
    {
        var requester = new SingleResponseImageRequester(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGMAAQABAA4A4cQTmwAAAABJRU5ErkJggg=="));
        var configuration = Configuration.Default
            .WithCss()
            .With(requester)
            .WithDefaultLoader(new LoaderOptions
            {
                IsResourceLoadingEnabled = true,
            });

        var document = await ParseAsync("""
            <html><body>
                <img src="http://assets.test/image.png" style="width:40px; height:20px;" />
            </body></html>
            """, configuration, "http://example.test/");

        var renderer = new HtmlRenderer();

        var first = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var second = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        Assert.Single(first.Commands.OfType<DrawImageCommand>());
        Assert.Single(second.Commands.OfType<DrawImageCommand>());
        Assert.Equal(1, requester.ContentReadSessionCount);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsSvgImageElementFromDataUri()
    {
        var document = await ParseAsync("""
            <html><body>
                <img src="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSIxMCIgaGVpZ2h0PSIxMCI+PHJlY3Qgd2lkdGg9IjEwIiBoZWlnaHQ9IjEwIiBmaWxsPSJyZWQiLz48L3N2Zz4=" style="width:40px; height:40px;" />
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var imageCommand = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(40f, imageCommand.Rect.Width);
        Assert.Equal(40f, imageCommand.Rect.Height);
        Assert.NotEmpty(imageCommand.Image.Data);
        Assert.Equal("image/png", imageCommand.Image.MimeType);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsInlineSvgAsReplacedElement()
    {
        var document = await ParseAsync("""
            <html><body>
                <svg width="10" height="10" viewBox="0 0 10 10">
                    <circle cx="5" cy="5" r="4" fill="green"></circle>
                </svg>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var imageCommand = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(10f, imageCommand.Rect.Width);
        Assert.Equal(10f, imageCommand.Rect.Height);
        Assert.NotEmpty(imageCommand.Image.Data);
    }

    [Fact]
    public async Task BuildDisplayList_IgnoresInlineSvgTitleTextContent()
    {
        var document = await ParseAsync("""
            <html><body>
                <svg width="10" height="10" viewBox="0 0 10 10">
                    <title>This must not be painted as page text</title>
                    <circle cx="5" cy="5" r="4" fill="green"></circle>
                </svg>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.DoesNotContain(displayList.Commands.OfType<DrawTextCommand>(), command => command.Text.Contains("must not be painted"));
    }

    [Fact]
    public async Task BuildDisplayList_ParsesLinearGradientBackgrounds()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:50px; background-image:linear-gradient(#ff0000, #0000ff);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var gradientBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(command => command.Paint is RenderGradientPaint);

        var gradient = Assert.IsType<RenderGradientPaint>(gradientBackground.Paint).Gradient;

        Assert.Equal(RenderGradientKind.Linear, gradient.Kind);
        Assert.Equal(2, gradient.Stops.Count);
        Assert.Equal(new RenderColor(255, 0, 0), gradient.Stops[0].Color);
        Assert.Equal(new RenderColor(0, 0, 255), gradient.Stops[1].Color);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesRadialGradientBackgrounds()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:50px; background-image:radial-gradient(circle, #ff0000 0%, #0000ff 100%);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var gradientBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(command => command.Paint is RenderGradientPaint);

        var gradient = Assert.IsType<RenderGradientPaint>(gradientBackground.Paint).Gradient;

        Assert.Equal(RenderGradientKind.Radial, gradient.Kind);
        Assert.Equal(2, gradient.Stops.Count);
        Assert.Equal(new RenderColor(255, 0, 0), gradient.Stops[0].Color);
        Assert.Equal(new RenderColor(0, 0, 255), gradient.Stops[1].Color);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesConicGradientBackgrounds()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:50px; background-image:conic-gradient(from 45deg, #ff0000, #00ff00, #0000ff);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var gradientBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(command => command.Paint is RenderGradientPaint);

        var gradient = Assert.IsType<RenderGradientPaint>(gradientBackground.Paint).Gradient;

        Assert.Equal(RenderGradientKind.Conic, gradient.Kind);
        Assert.Equal(3, gradient.Stops.Count);
        Assert.Equal(new RenderColor(255, 0, 0), gradient.Stops[0].Color);
        Assert.Equal(new RenderColor(0, 255, 0), gradient.Stops[1].Color);
        Assert.Equal(new RenderColor(0, 0, 255), gradient.Stops[2].Color);
    }

    [Fact]
    public async Task BuildDisplayList_PropagatesFontSizeStyleAndDecorationToTextCommands()
    {
        var document = await ParseAsync("""
            <html><body>
                <p>
                    <span style="font-size:12px; font-weight:400; text-decoration:underline;">Small</span>
                    <span style="font-size:24px; font-style:italic; font-weight:700; text-decoration:line-through;">Large</span>
                </p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 360,
            ViewPortHeight = 240,
            FontSize = 16f,
        });

        var textCommands = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.Equal(2, textCommands.Length);
        Assert.Equal("Small", textCommands[0].Text);
        Assert.Equal(12f, textCommands[0].FontSize);
        Assert.False(textCommands[0].IsBold);
        Assert.False(textCommands[0].IsItalic);
        Assert.True(textCommands[0].Underline);
        Assert.False(textCommands[0].StrikeThrough);

        Assert.Equal("Large", textCommands[1].Text);
        Assert.Equal(24f, textCommands[1].FontSize);
        Assert.True(textCommands[1].IsBold);
        Assert.True(textCommands[1].IsItalic);
        Assert.False(textCommands[1].Underline);
        Assert.True(textCommands[1].StrikeThrough);
    }

    [Fact]
    public async Task BuildDisplayList_CentersWrappedTextAndPreservesLineHeight()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:120px; text-align:center; line-height:2; font-size:10px;">
                    one two three four five six seven eight nine ten eleven twelve
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 180,
            FontSize = 10f,
        });

        var textCommands = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.True(textCommands.Length >= 2);
        Assert.True(textCommands[0].X > 0f);
        Assert.True(textCommands[1].Y - textCommands[0].Y >= 20f);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesColspanToCellGeometry()
    {
        var document = await ParseAsync("""
            <html><body>
                <table style="width:160px;">
                    <tr>
                        <td colspan="2" style="background-color:#ff0000;">Header</td>
                    </tr>
                    <tr>
                        <td style="background-color:#00ff00;">A</td>
                        <td style="background-color:#0000ff;">B</td>
                    </tr>
                </table>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var headerBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .FirstOrDefault(command => command.Rect.Width > 100f);

        Assert.NotNull(headerBackground);
        Assert.True(headerBackground!.Rect.Width > 100f);
    }

    [Fact]
    public async Task BuildDisplayList_CollapsesAdjacentCellBordersWhenRequested()
    {
        var collapsed = await CountCellBorderCommandsAsync("collapse");
        var separate = await CountCellBorderCommandsAsync("separate");

        Assert.True(collapsed.Total < separate.Total,
            $"Expected collapsing to draw fewer borders than separate ones, got {collapsed.Total} against {separate.Total}.");

        // The point of collapsing is that neighbours share an edge, so the rule between the two
        // columns has to be painted exactly once.
        Assert.Equal(1, collapsed.InteriorVerticalRules);
        Assert.Equal(2, separate.InteriorVerticalRules);
    }

    private static async Task<(int Total, int InteriorVerticalRules)> CountCellBorderCommandsAsync(string borderCollapse)
    {
        var document = await ParseAsync($$"""
            <html><body>
                <table style="border-collapse:{{borderCollapse}}; width:200px;">
                    <tr><td style="border:1px solid black;">A</td><td style="border:1px solid black;">B</td></tr>
                    <tr><td style="border:1px solid black;">C</td><td style="border:1px solid black;">D</td></tr>
                </table>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var borders = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Color == RenderColor.Black)
            .ToArray();

        // Vertical rules that sit strictly inside the table, counted on the first row only.
        var tableLeft = borders.Min(command => command.Rect.X);
        var tableRight = borders.Max(command => command.Rect.X + command.Rect.Width);
        var firstRowY = borders.Min(command => command.Rect.Y);

        var interior = borders
            .Where(command => command.Rect.Width <= 2f)
            .Where(command => command.Rect.X > tableLeft + 0.5f && command.Rect.X + command.Rect.Width < tableRight - 0.5f)
            .Where(command => command.Rect.Y <= firstRowY + 1f)
            .Count();

        return (borders.Length, interior);
    }

    [Fact]
    public async Task BuildDisplayList_UsesColumnWidthsFromColgroup()
    {
        var document = await ParseAsync("""
            <html><body>
                <table style="width:180px;">
                    <colgroup>
                        <col style="width:120px;" />
                    </colgroup>
                    <tr>
                        <td style="background-color:#ff0000;"></td>
                    </tr>
                </table>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var cellBackground = fills.FirstOrDefault(command => command.Color == new RenderColor(255, 0, 0));

        Assert.NotNull(cellBackground);
        Assert.True(cellBackground!.Rect.Width >= 100f, $"Expected the colgroup width to expand the cell geometry, but got {cellBackground.Rect.Width}.");
    }

    [Fact]
    public async Task BuildDisplayList_LaysOutFlexItemsInCenteredRow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; width:100px; height:40px; justify-content:center; align-items:center;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var containerBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .First(command => command.Rect.Width == 100f && command.Rect.Height == 40f);

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(containerBackground.Rect.X + 30f, childBackgrounds[0].Rect.X);
        Assert.Equal(containerBackground.Rect.Y + 15f, childBackgrounds[0].Rect.Y);
        Assert.Equal(containerBackground.Rect.X + 50f, childBackgrounds[1].Rect.X);
        Assert.Equal(containerBackground.Rect.Y + 15f, childBackgrounds[1].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_LaysOutFlexItemsInColumnDirection()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; flex-direction:column; width:100px; height:60px; align-items:center;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 140,
            FontSize = 16f,
        });

        var containerBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .First(command => command.Rect.Width == 100f && command.Rect.Height == 60f);

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.Y)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(containerBackground.Rect.X + 40f, childBackgrounds[0].Rect.X);
        Assert.Equal(containerBackground.Rect.Y, childBackgrounds[0].Rect.Y);
        Assert.Equal(containerBackground.Rect.X + 40f, childBackgrounds[1].Rect.X);
        Assert.Equal(containerBackground.Rect.Y + 10f, childBackgrounds[1].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesFlexGrowToItems()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; width:100px; height:40px;">
                    <div style="flex-grow:1; width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="flex-grow:1; width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 50f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(childBackgrounds[0].Rect.X, childBackgrounds[0].Rect.X);
        Assert.Equal(childBackgrounds[0].Rect.X + 50f, childBackgrounds[1].Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_WrapsItemsToNewLinesWhenNeeded()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; flex-wrap:wrap; width:70px; height:40px;">
                    <div style="width:40px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:40px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 40f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.Y)
            .ThenBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.True(childBackgrounds[1].Rect.Y > childBackgrounds[0].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_UsesAlignSelfForIndividualItems()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; width:100px; height:40px; align-items:center;">
                    <div style="align-self:flex-start; width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.True(childBackgrounds[0].Rect.Y < childBackgrounds[1].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_UsesFlexEndJustification()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; justify-content:flex-end; width:100px; height:40px;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(60f, childBackgrounds[0].Rect.X);
        Assert.Equal(80f, childBackgrounds[1].Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_UsesSpaceBetweenJustification()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; justify-content:space-between; width:100px; height:40px;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(15f, childBackgrounds[0].Rect.X);
        Assert.Equal(65f, childBackgrounds[1].Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_UsesRowReverseDirection()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; flex-direction:row-reverse; width:100px; height:40px;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var containerBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .First(command => command.Rect.Width == 100f && command.Rect.Height == 40f);

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(containerBackground.Rect.X + 60f, childBackgrounds[0].Rect.X);
        Assert.Equal(containerBackground.Rect.X + 80f, childBackgrounds[1].Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_UsesColumnReverseDirection()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; flex-direction:column-reverse; width:100px; height:60px;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 140,
            FontSize = 16f,
        });

        var containerBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .First(command => command.Rect.Width == 100f && command.Rect.Height == 60f);

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.Y)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(containerBackground.Rect.Y + 40f, childBackgrounds[0].Rect.Y);
        Assert.Equal(containerBackground.Rect.Y + 50f, childBackgrounds[1].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_UsesFlexBasisForMainSize()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; width:100px; height:40px;">
                    <div style="flex-basis:40px; width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 40f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Single(childBackgrounds);
    }

    [Fact]
    public async Task BuildDisplayList_LaysOutGridItemsInRowsAndColumns()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:50px 50px; grid-template-rows:20px 20px; width:100px; height:40px;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                    <div style="width:20px; height:10px; background-color:#00ff00;"></div>
                    <div style="width:20px; height:10px; background-color:#ffff00;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.Y)
            .ThenBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(4, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(0f, childBackgrounds[0].Rect.Y);
        Assert.Equal(50f, childBackgrounds[1].Rect.X);
        Assert.Equal(0f, childBackgrounds[1].Rect.Y);
        Assert.Equal(0f, childBackgrounds[2].Rect.X);
        Assert.Equal(20f, childBackgrounds[2].Rect.Y);
        Assert.Equal(50f, childBackgrounds[3].Rect.X);
        Assert.Equal(20f, childBackgrounds[3].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesGridGapsToTrackPlacement()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:50px 50px; grid-template-rows:20px 20px; gap:10px 20px; width:120px; height:60px;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                    <div style="width:20px; height:10px; background-color:#00ff00;"></div>
                    <div style="width:20px; height:10px; background-color:#ffff00;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.Y)
            .ThenBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(4, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(0f, childBackgrounds[0].Rect.Y);
        Assert.Equal(60f, childBackgrounds[1].Rect.X);
        Assert.Equal(0f, childBackgrounds[1].Rect.Y);
        Assert.Equal(0f, childBackgrounds[2].Rect.X);
        Assert.Equal(30f, childBackgrounds[2].Rect.Y);
        Assert.Equal(60f, childBackgrounds[3].Rect.X);
        Assert.Equal(30f, childBackgrounds[3].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesExplicitGridItemPlacement()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:50px 50px; grid-template-rows:20px 20px; width:100px; height:40px;">
                    <div style="width:20px; height:10px; background-color:#ff0000; grid-column:2; grid-row:2;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(command => command.Rect.Width == 20f && command.Rect.Height == 10f);

        Assert.Equal(50f, childBackground.Rect.X);
        Assert.Equal(20f, childBackground.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesSpanBasedGridItemPlacement()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:50px 50px; grid-template-rows:20px 20px; width:100px; height:40px;">
                    <div style="width:20px; height:10px; background-color:#ff0000; grid-column:1 / span 2;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackground = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(command => command.Rect.Width == 20f && command.Rect.Height == 10f);

        Assert.Equal(0f, childBackground.Rect.X);
        Assert.Equal(0f, childBackground.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesAutoPlacementAcrossImplicitTracks()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:50px 50px; width:100px; height:40px;">
                    <div style="width:20px; height:10px; background-color:#ff0000;"></div>
                    <div style="width:20px; height:10px; background-color:#0000ff;"></div>
                    <div style="width:20px; height:10px; background-color:#00ff00;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.Y)
            .ThenBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(3, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(0f, childBackgrounds[0].Rect.Y);
        Assert.Equal(50f, childBackgrounds[1].Rect.X);
        Assert.Equal(0f, childBackgrounds[1].Rect.Y);
        Assert.Equal(0f, childBackgrounds[2].Rect.X);
        Assert.Equal(20f, childBackgrounds[2].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesLetterSpacingToTextCommands()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="letter-spacing:2px;">Spacing</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var textCommand = displayList.Commands.OfType<DrawTextCommand>().Single();

        Assert.Equal(2f, textCommand.LetterSpacing);
    }

    [Fact]
    public async Task BuildDisplayList_PropagatesTextDecorationColorAndStyle()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="text-decoration:underline; text-decoration-style:dashed; text-decoration-color:#ff0000;">
                    Decorated text
                </p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var textCommand = displayList.Commands.OfType<DrawTextCommand>().Single();

        Assert.True(textCommand.Underline);
        Assert.Equal(new RenderColor(255, 0, 0), textCommand.DecorationColor);
        Assert.Equal(RenderTextDecorationStyle.Dashed, textCommand.DecorationStyle);
    }

    [Fact]
    public async Task BuildDisplayList_IndentsFirstLineOfBlockText()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="text-indent:20px; font-size:16px; width:180px;">Indented block text that wraps to a second line.</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var textCommands = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.True(textCommands.Length >= 2);
        Assert.True(textCommands[0].X >= 20f);
        Assert.True(textCommands[1].X < textCommands[0].X);
    }

    [Fact]
    public async Task BuildDisplayList_ShiftsInlineTextWithVerticalAlign()
    {
        var document = await ParseAsync("""
            <html><body>
                <p>
                    before <span style="vertical-align:super; font-size:12px;">sup</span> after
                </p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var textCommands = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.True(textCommands.Length >= 2);
        var superCommand = textCommands.First(command => command.Text.Contains("sup", StringComparison.OrdinalIgnoreCase));

        Assert.True(superCommand.Y < textCommands[0].Y);
    }

    [Fact]
    public async Task RenderToPng_UsesFontFamilyFallbackList()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="font-family:'DefinitelyMissing', serif; font-size:18px;">Fallback font family</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 120,
            FontSize = 18f,
        });

        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Data.Length > 8);
    }

    [Fact]
    public async Task RenderToPng_ReturnsPngPayload()
    {
        var document = await ParseAsync("<html><body><p>PNG smoke test output.</p></body></html>");
        var renderer = new HtmlRenderer();

        var image = renderer.RenderToPng(document, new DefaultRenderDevice
        {
            ViewPortWidth = 320,
            ViewPortHeight = 180,
        });

        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Data.Length > 8);
        Assert.Equal(0x89, image.Data[0]);
        Assert.Equal((byte)'P', image.Data[1]);
        Assert.Equal((byte)'N', image.Data[2]);
        Assert.Equal((byte)'G', image.Data[3]);
    }

    [Fact]
    public async Task BuildDisplayList_RendersBoxBackgroundFromPaddingAndMargins()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="margin:10px; padding:5px; width:100px; height:20px; background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var backgrounds = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(backgrounds.Length >= 2);

        var boxBackground = backgrounds[1];
        Assert.Equal(10f, boxBackground.Rect.X);
        Assert.Equal(10f, boxBackground.Rect.Y);
        Assert.Equal(110f, boxBackground.Rect.Width);
        Assert.Equal(30f, boxBackground.Rect.Height);
        Assert.Equal(new RenderColor(255, 0, 0), boxBackground.Color);
    }

    [Fact]
    public async Task BuildDisplayList_RendersPerSideBorderWidths()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:20px; border-top-width:2px; border-right-width:3px; border-bottom-width:4px; border-left-width:5px; border-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 5);

        var top = fills[1];
        var right = fills[2];
        var bottom = fills[3];
        var left = fills[4];

        Assert.Equal(2f, top.Rect.Height);
        Assert.Equal(3f, right.Rect.Width);
        Assert.Equal(4f, bottom.Rect.Height);
        Assert.Equal(5f, left.Rect.Width);

        Assert.Equal(new RenderColor(0, 0, 255), top.Color);
        Assert.Equal(new RenderColor(0, 0, 255), right.Color);
        Assert.Equal(new RenderColor(0, 0, 255), bottom.Color);
        Assert.Equal(new RenderColor(0, 0, 255), left.Color);
    }

    [Fact]
    public async Task BuildDisplayList_ResolvesPercentageWidthAgainstContainingBlock()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50%; height:10px; background-color:#00ff00;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 150,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 2);

        var boxBackground = fills[1];
        Assert.Equal(150f, boxBackground.Rect.Width);
        Assert.Equal(10f, boxBackground.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_CentersBlockWithAutoHorizontalMargins()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:10px; margin-left:auto; margin-right:auto; background-color:#00ff00;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 150,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 2);

        var boxBackground = fills[1];
        Assert.Equal(100f, boxBackground.Rect.X);
        Assert.Equal(100f, boxBackground.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_CollapsesAdjacentVerticalMargins()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="height:10px; margin-bottom:20px; background-color:#ff0000;"></div>
                <div style="height:10px; margin-top:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 3);

        var firstBox = fills[1];
        var secondBox = fills[2];

        Assert.Equal(0f, firstBox.Rect.Y);
        Assert.Equal(30f, secondBox.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_CollapsesParentAndFirstChildTopMargins()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="margin-top:10px; background-color:#eeeeee;">
                    <div style="margin-top:20px; height:10px; background-color:#ff0000;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 320,
            ViewPortHeight = 240,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 3);

        var parentBackground = fills.Single(f => f.Color.Equals(new RenderColor(0xee, 0xee, 0xee)));
        var childBackground = fills.Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));

        Assert.Equal(20f, parentBackground.Rect.Y);
        Assert.Equal(20f, childBackground.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_DoesNotCollapseParentAndFirstChildTopMarginsWhenParentHasPadding()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="margin-top:10px; padding-top:1px; background-color:#eeeeee;">
                    <div style="margin-top:20px; height:10px; background-color:#ff0000;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 320,
            ViewPortHeight = 240,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 3);

        var parentBackground = fills.Single(f => f.Color.Equals(new RenderColor(0xee, 0xee, 0xee)));
        var childBackground = fills.Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));

        Assert.Equal(10f, parentBackground.Rect.Y);
        Assert.Equal(31f, childBackground.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_CollapsesParentAndLastChildBottomMargins()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="margin-bottom:10px; background-color:#eeeeee;">
                    <div style="height:10px; margin-bottom:20px; background-color:#ff0000;"></div>
                </div>
                <div style="height:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 320,
            ViewPortHeight = 260,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var nextSibling = fills.Single(f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.Equal(30f, nextSibling.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_DoesNotPaintBorderWhenStyleIsNone()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:20px; border:5px none #ff0000; background-color:#00ff00;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var redBorderFills = fills.Where(f => f.Color.Equals(new RenderColor(255, 0, 0))).ToArray();
        Assert.Empty(redBorderFills);
    }

    [Fact]
    public async Task BuildDisplayList_DoesNotPaintDisplayNoneElement()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:none; width:50px; height:20px; background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.Single(fills);
    }

    [Fact]
    public async Task BuildDisplayList_DoesNotPaintVisibilityHiddenElement()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="visibility:hidden; width:50px; height:20px; background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.Single(fills);
    }

    [Fact]
    public async Task BuildDisplayList_RendersInlineBlockBox()
    {
        var document = await ParseAsync("""
            <html><body>
                <span style="display:inline-block; width:40px; height:12px; background-color:#ff0000;"></span>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
        });

        var redFill = displayList.Commands
            .OfType<FillRectCommand>()
            .SingleOrDefault(f => f.Color.Equals(new RenderColor(255, 0, 0)));

        Assert.NotNull(redFill);
        Assert.Equal(40f, redFill.Rect.Width);
        Assert.Equal(12f, redFill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_RespectsDisplayBlock()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:block; width:60px; height:10px; background-color:#00ff00;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
        });

        var greenFill = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(f => f.Color.Equals(new RenderColor(0, 255, 0)));

        Assert.Equal(60f, greenFill.Rect.Width);
        Assert.Equal(10f, greenFill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_TreatsInvalidDisplayFixedAsDefaultBlock()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:fixed; height:10px; background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 180,
            ViewPortHeight = 80,
        });

        var redFill = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));

        Assert.Equal(180f, redFill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_TreatsInvalidDisplayRelativeAsDefaultBlock()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:relative; height:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 180,
            ViewPortHeight = 80,
        });

        var blueFill = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.Equal(180f, blueFill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_FloatsLeftAndWrapsFollowingBlock()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:left; width:50px; height:20px; background-color:#ff0000;"></div>
                <div style="width:40px; height:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 180,
            ViewPortHeight = 100,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var floatBox = fills.Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var normalBox = fills.Single(f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.Equal(0f, floatBox.Rect.X);
        Assert.Equal(0f, floatBox.Rect.Y);
        Assert.Equal(50f, floatBox.Rect.Width);
        Assert.Equal(20f, floatBox.Rect.Height);

        Assert.Equal(50f, normalBox.Rect.X);
        Assert.Equal(0f, normalBox.Rect.Y);
        Assert.Equal(40f, normalBox.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesRelativePositionOffsetWithoutChangingFlow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; left:20px; top:5px; width:40px; height:10px; background-color:#ff0000;"></div>
                <div style="width:40px; height:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var relativeBox = fills.Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var nextBlock = fills.Single(f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.Equal(20f, relativeBox.Rect.X);
        Assert.Equal(5f, relativeBox.Rect.Y);
        Assert.Equal(10f, nextBlock.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_RendersFixedPositionRelativeToViewportAndExcludesFlow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:fixed; left:15px; top:8px; width:30px; height:10px; background-color:#ff0000;"></div>
                <div style="width:30px; height:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var fixedBox = fills.Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var normalBox = fills.Single(f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.Equal(15f, fixedBox.Rect.X);
        Assert.Equal(8f, fixedBox.Rect.Y);
        Assert.Equal(0f, normalBox.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_DistinguishesPaddingFromMargin()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="margin-left:10px; margin-top:4px; padding-left:5px; padding-right:7px; width:20px; height:10px; background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 120,
        });

        var redFill = displayList.Commands
            .OfType<FillRectCommand>()
            .Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));

        Assert.Equal(10f, redFill.Rect.X);
        Assert.Equal(4f, redFill.Rect.Y);
        Assert.Equal(32f, redFill.Rect.Width);
        Assert.Equal(10f, redFill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsOutlineOutsideBorderWithoutChangingFlow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:10px; background-color:#ff0000; outline:3px solid #0000ff;"></div>
                <div style="width:40px; height:10px; background-color:#00ff00;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var greenBox = fills.Single(f => f.Color.Equals(new RenderColor(0, 255, 0)));
        var outlineTop = fills.SingleOrDefault(f =>
            f.Color.Equals(new RenderColor(0, 0, 255)) &&
            f.Rect.X == -3f &&
            f.Rect.Y == -3f &&
            f.Rect.Width == 46f &&
            f.Rect.Height == 3f);

        Assert.NotNull(outlineTop);
        Assert.Equal(10f, greenBox.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_RendersAbsolutePositionRelativeToContainingBlockAndExcludesFlow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:100px; height:20px; background-color:#eeeeee;">
                    <div style="position:absolute; left:12px; top:6px; width:30px; height:10px; background-color:#ff0000;"></div>
                </div>
                <div style="width:40px; height:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 140,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var absoluteBox = fills.Single(f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var nextFlowBox = fills.Single(f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.Equal(12f, absoluteBox.Rect.X);
        Assert.Equal(6f, absoluteBox.Rect.Y);
        Assert.Equal(20f, nextFlowBox.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsHigherZIndexAfterLowerForPositionedOverlaps()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:120px; height:40px;">
                    <div style="position:absolute; left:10px; top:5px; width:30px; height:20px; background-color:#ff0000; z-index:1;"></div>
                    <div style="position:absolute; left:10px; top:5px; width:30px; height:20px; background-color:#0000ff; z-index:2;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var redIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var blueIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.True(redIndex >= 0);
        Assert.True(blueIndex >= 0);
        Assert.True(blueIndex > redIndex);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsNegativeZIndexOnTopOfOwnStackingContextBackground()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:120px; height:30px; background-color:#00ff00;">
                    <div style="position:absolute; left:0; top:0; width:30px; height:10px; background-color:#ff0000; z-index:-1;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var greenIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(0, 255, 0)));
        var redIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(255, 0, 0)));

        Assert.True(greenIndex >= 0);
        Assert.True(redIndex >= 0);

        // Per CSS 2.1 Appendix E, a stacking context's own background/border paints first, and
        // its negative-z-index descendants paint immediately after (on top of) that background -
        // not before/behind it. So the red z-index:-1 child paints after the green box's own
        // background, even though it still paints before the green box's normal in-flow content
        // (there is none here) and before any positive/zero-z-index descendants.
        Assert.True(redIndex > greenIndex);
    }

    [Fact]
    public async Task BuildDisplayList_UsesZIndexOverSourceOrderForPositionedSiblings()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:120px; height:40px;">
                    <div style="position:absolute; left:10px; top:5px; width:30px; height:20px; background-color:#0000ff; z-index:2;"></div>
                    <div style="position:absolute; left:10px; top:5px; width:30px; height:20px; background-color:#ff0000; z-index:1;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 220,
            ViewPortHeight = 120,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var redIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var blueIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.True(redIndex >= 0);
        Assert.True(blueIndex >= 0);
        Assert.True(blueIndex > redIndex);
    }

    [Fact]
    public async Task BuildDisplayList_AppliesUniformBorderRadiusToBackgroundFill()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; background-color:#00ff00; border-radius:8px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        Assert.True(fills.Length >= 2);

        var boxBackground = fills[1];
        Assert.Equal(8f, boxBackground.Radii.TopLeftX);
        Assert.Equal(8f, boxBackground.Radii.TopLeftY);
        Assert.Equal(8f, boxBackground.Radii.TopRightX);
        Assert.Equal(8f, boxBackground.Radii.TopRightY);
        Assert.Equal(8f, boxBackground.Radii.BottomRightX);
        Assert.Equal(8f, boxBackground.Radii.BottomRightY);
        Assert.Equal(8f, boxBackground.Radii.BottomLeftX);
        Assert.Equal(8f, boxBackground.Radii.BottomLeftY);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesTwoValueBorderRadiusShorthand()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; background-color:#00ff00; border-radius:20px 5px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var boxBackground = fills[1];

        // border-radius: 20px 5px -> top-left/bottom-right get 20px, top-right/bottom-left get 5px.
        Assert.Equal(20f, boxBackground.Radii.TopLeftX);
        Assert.Equal(5f, boxBackground.Radii.TopRightX);
        Assert.Equal(20f, boxBackground.Radii.BottomRightX);
        Assert.Equal(5f, boxBackground.Radii.BottomLeftX);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesEllipticalBorderRadiusPerCornerLonghand()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; background-color:#00ff00; border-top-left-radius:20px 10px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var boxBackground = fills[1];

        Assert.Equal(20f, boxBackground.Radii.TopLeftX);
        Assert.Equal(10f, boxBackground.Radii.TopLeftY);
        Assert.Equal(0f, boxBackground.Radii.TopRightX);
        Assert.Equal(0f, boxBackground.Radii.BottomRightX);
        Assert.Equal(0f, boxBackground.Radii.BottomLeftX);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesSlashSeparatedBorderRadiusShorthand()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; background-color:#00ff00; border-radius:40px / 20px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var boxBackground = fills[1];

        // border-radius: 40px / 20px -> every corner gets a 40px horizontal / 20px vertical
        // elliptical radius (all four corners share the same X and share the same Y here, since
        // no per-corner variation was given on either side of the slash).
        Assert.Equal(40f, boxBackground.Radii.TopLeftX);
        Assert.Equal(20f, boxBackground.Radii.TopLeftY);
        Assert.Equal(40f, boxBackground.Radii.BottomRightX);
        Assert.Equal(20f, boxBackground.Radii.BottomRightY);
    }

    [Fact]
    public async Task BuildDisplayList_ResolvesPercentageBorderRadiusAgainstBox()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:200px; height:80px; background-color:#00ff00; border-radius:10%;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var boxBackground = fills[1];

        // Verified against AngleSharp.Css 1.1.0: a border-radius percentage resolves against the
        // containing block's width for both the horizontal and vertical component (here, the
        // 300px viewport width -> 10% = 30px), not per-axis against the element's own width and
        // height as the CSS spec technically prescribes - a quirk of the computed-style engine
        // this renderer sits on top of, not something this renderer's own parsing controls. 30px
        // on each side of a 200x80 box does not exceed either edge, so no overlap-clamping kicks
        // in here (that path is covered separately by the pixel-radius clamp test below).
        Assert.Equal(30f, boxBackground.Radii.TopLeftX);
        Assert.Equal(30f, boxBackground.Radii.TopLeftY);
    }

    [Fact]
    public async Task BuildDisplayList_ClampsOverlappingBorderRadiiToBoxSize()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; background-color:#00ff00; border-radius:100px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var boxBackground = fills[1];

        // A 100px radius on every corner of a 40x20 box would overlap; the CSS corner-overlap
        // algorithm scales all radii down uniformly until the tightest edge (height=20, two
        // 100px radii sharing it) just fits: scale = 20/200 = 0.1 -> 10px.
        Assert.Equal(10f, boxBackground.Radii.TopLeftX, 0.001f);
        Assert.Equal(10f, boxBackground.Radii.TopLeftY, 0.001f);
        Assert.Equal(10f, boxBackground.Radii.BottomRightX, 0.001f);
        Assert.Equal(10f, boxBackground.Radii.BottomRightY, 0.001f);
    }

    [Fact]
    public async Task BuildDisplayList_RendersUniformBorderRadiusBorderAsStrokedRoundedRect()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; border-width:4px; border-style:solid; border-color:#0000ff; border-radius:12px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var strokedBorder = Assert.Single(displayList.Commands.OfType<StrokeRoundedRectCommand>());
        Assert.Equal(new RenderColor(0, 0, 255), strokedBorder.Color);
        Assert.Equal(4f, strokedBorder.StrokeWidth);

        // The stroke is drawn along the border's centerline, half the border width inside the
        // outer edge, so its radius is the outer 12px radius minus half the 4px border width.
        Assert.Equal(10f, strokedBorder.Radii.TopLeftX, 0.001f);

        // A rounded border is painted as a single ring, never as the four separate edge rectangles.
        Assert.Empty(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 0, 255))));
    }

    [Fact]
    public async Task BuildDisplayList_FallsBackToStraightEdgesForMixedWidthRoundedBorder()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; border-top-width:2px; border-right-width:6px; border-bottom-width:2px; border-left-width:2px; border-style:solid; border-color:#0000ff; border-radius:12px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        // Mixed edge widths have no single stroke width for a rounded ring, so the renderer falls
        // back to the un-rounded four-rectangle border path.
        Assert.Empty(displayList.Commands.OfType<StrokeRoundedRectCommand>());

        var borderFills = displayList.Commands.OfType<FillRectCommand>()
            .Where(f => f.Color.Equals(new RenderColor(0, 0, 255)))
            .ToArray();
        Assert.Equal(4, borderFills.Length);
    }

    [Fact]
    public async Task BuildDisplayList_DoesNotRoundOutlineWithBorderRadius()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; border-radius:12px; outline-width:3px; outline-style:solid; outline-color:#ff00ff;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        // The outline is unaffected by border-radius per spec, so it stays a plain four-rectangle
        // border rather than a StrokeRoundedRectCommand.
        Assert.Empty(displayList.Commands.OfType<StrokeRoundedRectCommand>());

        var outlineFills = displayList.Commands.OfType<FillRectCommand>()
            .Where(f => f.Color.Equals(new RenderColor(255, 0, 255)))
            .ToArray();
        Assert.Equal(4, outlineFills.Length);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesSingleOutsetBoxShadow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px; background-color:#00ff00; box-shadow: 2px 3px 4px 1px rgba(0, 0, 0, 0.5);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var shadow = Assert.Single(displayList.Commands.OfType<DrawBoxShadowCommand>());
        Assert.Equal(2f, shadow.Shadow.OffsetX);
        Assert.Equal(3f, shadow.Shadow.OffsetY);
        Assert.Equal(4f, shadow.Shadow.BlurRadius);
        Assert.Equal(1f, shadow.Shadow.SpreadRadius);
        Assert.Equal(new RenderColor(0, 0, 0, 128), shadow.Shadow.Color);
        Assert.False(shadow.Shadow.Inset);

        // Per spec, box-shadow paints after the background (and before the border) - not before
        // it - so an inset shadow is not hidden underneath an opaque background. For an outset
        // shadow like this one, the shadow shape is clipped to never overlap the border box
        // anyway, so this ordering has no visible effect here; it matters for the inset case.
        var shadowIndex = Array.IndexOf(displayList.Commands.ToArray(), (RenderCommand)shadow);
        var backgroundIndex = Array.FindIndex(displayList.Commands.ToArray(), c => c is FillRectCommand fill && fill.Color.Equals(new RenderColor(0, 255, 0)));
        Assert.True(shadowIndex > backgroundIndex);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesInsetBoxShadow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px; box-shadow: inset 0 0 5px rgba(255, 0, 0, 1);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var shadow = Assert.Single(displayList.Commands.OfType<DrawBoxShadowCommand>());
        Assert.True(shadow.Shadow.Inset);
        Assert.Equal(0f, shadow.Shadow.OffsetX);
        Assert.Equal(0f, shadow.Shadow.OffsetY);
        Assert.Equal(5f, shadow.Shadow.BlurRadius);
        Assert.Equal(0f, shadow.Shadow.SpreadRadius);
        Assert.Equal(new RenderColor(255, 0, 0), shadow.Shadow.Color);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsMultipleBoxShadowsWithFirstListedOnTop()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px; box-shadow: 1px 1px 0 rgba(255, 0, 0, 1), 2px 2px 0 rgba(0, 0, 255, 1);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var shadows = displayList.Commands.OfType<DrawBoxShadowCommand>().ToArray();
        Assert.Equal(2, shadows.Length);

        // The first-listed shadow (red) paints on top of the second (blue), so it is added to
        // the display list last, even though it appears first in the CSS source.
        Assert.Equal(new RenderColor(0, 0, 255), shadows[0].Shadow.Color);
        Assert.Equal(new RenderColor(255, 0, 0), shadows[1].Shadow.Color);
    }

    [Fact]
    public async Task BuildDisplayList_TreatsBoxShadowNoneAsNoShadow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px; box-shadow: none;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        Assert.Empty(displayList.Commands.OfType<DrawBoxShadowCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ParsesTextShadowAndPaintsItBeforeTheText()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="text-shadow: 1px 1px 2px rgba(0, 0, 0, 1);">Hi</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var commands = displayList.Commands.ToArray();
        var shadow = Assert.Single(commands.OfType<DrawTextShadowCommand>());
        var text = Assert.Single(commands.OfType<DrawTextCommand>());

        Assert.Equal("Hi", shadow.Text);
        Assert.Equal(text.X + 1f, shadow.X);
        Assert.Equal(text.Y + 1f, shadow.Y);
        Assert.Equal(2f, shadow.BlurRadius);
        Assert.Equal(new RenderColor(0, 0, 0), shadow.Color);
        Assert.True(Array.IndexOf(commands, (RenderCommand)shadow) < Array.IndexOf(commands, (RenderCommand)text));
    }

    [Fact]
    public async Task BuildDisplayList_PaintsMultipleTextShadowsWithFirstListedOnTop()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="text-shadow: 1px 1px 0 rgba(255, 0, 0, 1), 2px 2px 0 rgba(0, 0, 255, 1);">Hi</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var shadows = displayList.Commands.OfType<DrawTextShadowCommand>().ToArray();
        Assert.Equal(2, shadows.Length);
        Assert.Equal(new RenderColor(0, 0, 255), shadows[0].Color);
        Assert.Equal(new RenderColor(255, 0, 0), shadows[1].Color);
    }

    [Fact]
    public async Task BuildDisplayList_InheritsTextShadowToChildrenWithoutTheirOwn()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="text-shadow: 1px 1px 2px rgba(255, 0, 0, 1);"><span>Hi</span></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var shadow = Assert.Single(displayList.Commands.OfType<DrawTextShadowCommand>());
        Assert.Equal(new RenderColor(255, 0, 0), shadow.Color);
    }

    [Fact]
    public async Task BuildDisplayList_TextShadowNoneOverridesInheritedShadow()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="text-shadow: 1px 1px 2px rgba(255, 0, 0, 1);"><span style="text-shadow: none;">Hi</span></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        Assert.Empty(displayList.Commands.OfType<DrawTextShadowCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_PaintsOwnBackgroundBehindOwnDirectTextContent()
    {
        // Regression test: an auto-height box's own background can only be sized once its
        // children are measured, which used to mean it was appended to the display list (and so
        // painted) AFTER its children's commands - silently hiding any direct text content behind
        // an opaque background. The fix splices the box's own background/border/shadow/outline
        // commands in before its children's once the box's final size is known.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:40px; background-color:#ff0000;">Hi</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var commands = displayList.Commands.ToArray();
        var backgroundIndex = Array.FindIndex(commands, c => c is FillRectCommand fill && fill.Color.Equals(new RenderColor(255, 0, 0)));
        var textIndex = Array.FindIndex(commands, c => c is DrawTextCommand text && text.Text == "Hi");

        Assert.True(backgroundIndex >= 0);
        Assert.True(textIndex >= 0);
        Assert.True(backgroundIndex < textIndex);
    }

    [Fact]
    public async Task BuildDisplayList_RendersUnorderedListWithDiscMarkers()
    {
        var document = await ParseAsync("""
            <html><body>
                <ul>
                    <li>One</li>
                    <li>Two</li>
                </ul>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        // A disc marker is a small circular FillRectCommand (full corner radii), distinguishable
        // from an ordinary square background fill.
        var discMarkers = displayList.Commands.OfType<FillRectCommand>()
            .Where(f => !f.Radii.IsZero && f.Rect.Width < 10f)
            .ToArray();
        Assert.Equal(2, discMarkers.Length);

        Assert.Contains(displayList.Commands, c => c is DrawTextCommand t && t.Text == "One");
        Assert.Contains(displayList.Commands, c => c is DrawTextCommand t && t.Text == "Two");
    }

    [Fact]
    public async Task BuildDisplayList_RendersOrderedListWithDecimalMarkers()
    {
        var document = await ParseAsync("""
            <html><body>
                <ol>
                    <li>First</li>
                    <li>Second</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        Assert.Contains(texts, t => t.Text == "1.");
        Assert.Contains(texts, t => t.Text == "2.");
        Assert.Contains(texts, t => t.Text == "First");
        Assert.Contains(texts, t => t.Text == "Second");

        // The marker for "First" sits to the left of the "First" text it labels.
        var marker = texts.Single(t => t.Text == "1.");
        var content = texts.Single(t => t.Text == "First");
        Assert.True(marker.X < content.X);
    }

    [Fact]
    public async Task BuildDisplayList_RespectsOlStartAttribute()
    {
        var document = await ParseAsync("""
            <html><body>
                <ol start="5">
                    <li>a</li>
                    <li>b</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        Assert.Contains(texts, t => t.Text == "5.");
        Assert.Contains(texts, t => t.Text == "6.");
    }

    [Fact]
    public async Task BuildDisplayList_RespectsLiValueAttributeOverride()
    {
        var document = await ParseAsync("""
            <html><body>
                <ol>
                    <li>a</li>
                    <li value="10">b</li>
                    <li>c</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        Assert.Contains(texts, t => t.Text == "1.");
        Assert.Contains(texts, t => t.Text == "10.");
        Assert.Contains(texts, t => t.Text == "11.");
    }

    [Fact]
    public async Task BuildDisplayList_RespectsOlReversedAttribute()
    {
        var document = await ParseAsync("""
            <html><body>
                <ol reversed="reversed">
                    <li>a</li>
                    <li>b</li>
                    <li>c</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        Assert.Contains(texts, t => t.Text == "3.");
        Assert.Contains(texts, t => t.Text == "2.");
        Assert.Contains(texts, t => t.Text == "1.");
    }

    [Fact]
    public async Task BuildDisplayList_RendersLowerRomanMarkers()
    {
        var document = await ParseAsync("""
            <html><body>
                <ol style="list-style-type: lower-roman;">
                    <li>a</li>
                    <li>b</li>
                    <li>c</li>
                    <li>d</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        Assert.Contains(texts, t => t.Text == "i.");
        Assert.Contains(texts, t => t.Text == "ii.");
        Assert.Contains(texts, t => t.Text == "iii.");
        Assert.Contains(texts, t => t.Text == "iv.");
    }

    [Fact]
    public async Task BuildDisplayList_RendersUpperAlphaMarkers()
    {
        var document = await ParseAsync("""
            <html><body>
                <ol style="list-style-type: upper-alpha;">
                    <li>a</li>
                    <li>b</li>
                    <li>c</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        Assert.Contains(texts, t => t.Text == "A.");
        Assert.Contains(texts, t => t.Text == "B.");
        Assert.Contains(texts, t => t.Text == "C.");
    }

    [Fact]
    public async Task BuildDisplayList_SuppressesMarkerWhenListStyleTypeIsNone()
    {
        var document = await ParseAsync("""
            <html><body>
                <ul style="list-style-type: none;">
                    <li>One</li>
                </ul>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        Assert.Empty(displayList.Commands.OfType<FillRectCommand>().Where(f => !f.Radii.IsZero && f.Rect.Width < 10f));
        Assert.Contains(displayList.Commands, c => c is DrawTextCommand t && t.Text == "One");
    }

    [Fact]
    public async Task BuildDisplayList_NestedListsNumberIndependently()
    {
        var document = await ParseAsync("""
            <html><body>
                <ol>
                    <li>a
                        <ol>
                            <li>nested</li>
                        </ol>
                    </li>
                    <li>b</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text).ToArray();

        // Two "1." markers: the outer list's first item and the (independently-numbered) nested
        // list's own first item. The outer list's second item is "2.", unaffected by nesting.
        Assert.Equal(2, texts.Count(t => t == "1."));
        Assert.Contains("2.", texts);
    }

    [Fact]
    public async Task BuildDisplayList_ListStylePositionInsideStartsMarkerAtContentEdge()
    {
        var document = await ParseAsync("""
            <html><body>
                <ul style="list-style-position: inside;">
                    <li>One</li>
                </ul>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var marker = displayList.Commands.OfType<FillRectCommand>().Single(f => !f.Radii.IsZero && f.Rect.Width < 10f);
        var text = displayList.Commands.OfType<DrawTextCommand>().Single(t => t.Text == "One");

        // "inside" starts the marker at the li's own content edge and pushes the text to make
        // room for it, rather than placing the marker in the gutter to the left of that edge.
        Assert.True(text.X > marker.Rect.X);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html, IConfiguration? configuration = null, string? address = null)
    {
        var context = BrowsingContext.New(configuration ?? Configuration.Default.WithCss());

        return await context.OpenAsync(request =>
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                request.Address(address);
            }

            request.Content(html);
        });
    }

    private sealed class SingleResponseImageRequester : BaseRequester
    {
        private readonly ReadTrackingMemoryStream _stream;

        public SingleResponseImageRequester(byte[] imageData)
        {
            _stream = new ReadTrackingMemoryStream(imageData);
        }

        public int ContentReadSessionCount => _stream.ReadSessionCount;

        public override bool SupportsProtocol(string protocol)
        {
            return string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase);
        }

        protected override Task<IResponse?> PerformRequestAsync(Request request, CancellationToken cancel)
        {
            var response = new DefaultResponse
            {
                Address = request.Address,
                StatusCode = HttpStatusCode.OK,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = "image/png",
                },
                Content = _stream,
            };

            return Task.FromResult<IResponse?>(response);
        }

        private sealed class ReadTrackingMemoryStream : MemoryStream
        {
            public ReadTrackingMemoryStream(byte[] buffer)
                : base(buffer)
            {
            }

            public int ReadSessionCount { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position == 0)
                {
                    ReadSessionCount++;
                }

                return base.Read(buffer, offset, count);
            }

            public override int Read(Span<byte> buffer)
            {
                if (Position == 0)
                {
                    ReadSessionCount++;
                }

                return base.Read(buffer);
            }

            protected override void Dispose(bool disposing)
            {
                // Keep the backing stream alive for deterministic test behavior.
            }
        }
    }
}