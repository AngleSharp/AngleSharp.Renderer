using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
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
    public async Task BuildDisplayList_PaintsBackgroundImageFromDataUri()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:80px; height:60px; background-image: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGMAAQABAA4A4cQTmwAAAABJRU5ErkJggg==);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Paint is RenderImagePaint));
        var imagePaint = Assert.IsType<RenderImagePaint>(fill.Paint);

        Assert.NotEmpty(imagePaint.Image.Data);
        Assert.Equal(1, imagePaint.Image.Width);
        Assert.Equal(1, imagePaint.Image.Height);
        // CSS initial values: `repeat repeat`, `0% 0%`, `auto`.
        Assert.True(imagePaint.RepeatX);
        Assert.True(imagePaint.RepeatY);
        Assert.Equal(RenderBackgroundPositionComponent.Zero, imagePaint.PositionX);
        Assert.Equal(RenderBackgroundPositionComponent.Zero, imagePaint.PositionY);
        Assert.Equal(RenderBackgroundSizeKind.Auto, imagePaint.Size.Kind);
    }

    [Fact]
    public async Task BuildDisplayList_ParsesBackgroundRepeatPositionAndSize()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:80px; height:60px; background-image: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGMAAQABAA4A4cQTmwAAAABJRU5ErkJggg==); background-repeat: no-repeat; background-position: right bottom; background-size: cover;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Paint is RenderImagePaint));
        var imagePaint = Assert.IsType<RenderImagePaint>(fill.Paint);

        Assert.False(imagePaint.RepeatX);
        Assert.False(imagePaint.RepeatY);
        Assert.Equal(new RenderBackgroundPositionComponent(1f, 0f), imagePaint.PositionX);
        Assert.Equal(new RenderBackgroundPositionComponent(1f, 0f), imagePaint.PositionY);
        Assert.Equal(RenderBackgroundSizeKind.Cover, imagePaint.Size.Kind);
    }

    [Fact]
    public async Task BuildDisplayList_FetchesAndCachesHttpBackgroundImagePerDocument()
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
                <div style="width:80px; height:60px; background-image: url(http://assets.test/tile.png);"></div>
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

        Assert.IsType<RenderImagePaint>(Assert.Single(first.Commands.OfType<FillRectCommand>().Where(f => f.Paint is RenderImagePaint)).Paint);
        Assert.IsType<RenderImagePaint>(Assert.Single(second.Commands.OfType<FillRectCommand>().Where(f => f.Paint is RenderImagePaint)).Paint);
        // Requested once and reused on the second render - not re-fetched every time the display
        // list is rebuilt, the same guarantee already established for <img>.
        Assert.Equal(1, requester.ContentReadSessionCount);
    }

    [Fact]
    public async Task BuildDisplayList_WithoutDocumentLoader_FallsBackToBackgroundColorForNetworkUrl()
    {
        // No IDocumentLoader is registered here, so a network background-image URL must never be
        // fetched - matching how <img> and @font-face url() sources already behave. The box still
        // paints (its background-color), it just does not get the image.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:80px; height:60px; background-color:#0000ff; background-image: url(http://assets.test/tile.png);"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
            FontSize = 16f,
        });

        Assert.DoesNotContain(displayList.Commands, command => command is FillRectCommand fill && fill.Paint is RenderImagePaint);

        var colorFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 0, 255))));
        Assert.IsType<RenderColorPaint>(colorFill.Paint);
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

        // gap's two-value form is <row-gap> <column-gap> per spec, so "gap:10px 20px" is
        // row-gap=10px (second row starts at 20+10=30) and column-gap=20px (second column
        // starts at 50+20=70) - this used to assert the reverse, matching a real, now-fixed
        // upstream bug in AngleSharp.Css's GapDeclaration ordering (see AGENTS.md/GapShorthandComputedStyleTests).
        Assert.Equal(4, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(0f, childBackgrounds[0].Rect.Y);
        Assert.Equal(70f, childBackgrounds[1].Rect.X);
        Assert.Equal(0f, childBackgrounds[1].Rect.Y);
        Assert.Equal(0f, childBackgrounds[2].Rect.X);
        Assert.Equal(30f, childBackgrounds[2].Rect.Y);
        Assert.Equal(70f, childBackgrounds[3].Rect.X);
        Assert.Equal(30f, childBackgrounds[3].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_SizesFractionalTracksProportionally()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:1fr 2fr; width:150px; height:20px;">
                    <div style="height:20px; background-color:#ff0000;"></div>
                    <div style="height:20px; background-color:#0000ff;"></div>
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
            .Where(command => command.Rect.Height == 20f && command.Rect.Width < 150f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(50f, childBackgrounds[0].Rect.Width);
        Assert.Equal(50f, childBackgrounds[1].Rect.X);
        Assert.Equal(100f, childBackgrounds[1].Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_ExpandsRepeatFunctionIntoFixedTracks()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:repeat(3, 40px); gap:5px; width:150px; height:20px;">
                    <div style="height:20px; background-color:#ff0000;"></div>
                    <div style="height:20px; background-color:#0000ff;"></div>
                    <div style="height:20px; background-color:#00ff00;"></div>
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
            .Where(command => command.Rect.Height == 20f && command.Rect.Width < 150f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(3, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(40f, childBackgrounds[0].Rect.Width);
        Assert.Equal(45f, childBackgrounds[1].Rect.X);
        Assert.Equal(40f, childBackgrounds[1].Rect.Width);
        Assert.Equal(90f, childBackgrounds[2].Rect.X);
        Assert.Equal(40f, childBackgrounds[2].Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_ClampsMinMaxTrackToItsFractionalMaximum()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:minmax(30px, 1fr) 60px; width:150px; height:20px;">
                    <div style="height:20px; background-color:#ff0000;"></div>
                    <div style="height:20px; background-color:#0000ff;"></div>
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
            .Where(command => command.Rect.Height == 20f && command.Rect.Width < 150f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(90f, childBackgrounds[0].Rect.Width);
        Assert.Equal(90f, childBackgrounds[1].Rect.X);
        Assert.Equal(60f, childBackgrounds[1].Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_ClampsAutoTrackToItsMinMaxLengthBounds()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:minmax(60px, 100px) 30px; width:200px; height:20px;">
                    <div style="width:20px; height:20px; background-color:#ff0000;"></div>
                    <div style="height:20px; background-color:#0000ff;"></div>
                </div>
                <div style="display:grid; grid-template-columns:minmax(60px, 100px) 30px; width:250px; height:15px;">
                    <div style="width:150px; height:15px; background-color:#00ff00;"></div>
                    <div style="height:15px; background-color:#ffff00;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var secondColumnCells = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 30f)
            .OrderBy(command => command.Rect.Y)
            .ToArray();

        Assert.Equal(2, secondColumnCells.Length);
        // First grid: the item's estimated size (20px) is below the minmax() floor, so the
        // Auto track clamps up to its 60px minimum.
        Assert.Equal(60f, secondColumnCells[0].Rect.X);
        // Second grid: the item's estimated size (150px) exceeds the minmax() ceiling, so the
        // Auto track clamps down to its 100px maximum.
        Assert.Equal(100f, secondColumnCells[1].Rect.X);
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

        // Implicit auto rows now size to their own content (10px, each item's actual height),
        // the same way auto columns already did - this used to assert 20 (containerHeight/rowCount,
        // 40/2), a coarse guess from before implicit rows grew to fit content like explicit Auto
        // tracks do (see GrowGridTrackSize).
        Assert.Equal(3, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(0f, childBackgrounds[0].Rect.Y);
        Assert.Equal(50f, childBackgrounds[1].Rect.X);
        Assert.Equal(0f, childBackgrounds[1].Rect.Y);
        Assert.Equal(0f, childBackgrounds[2].Rect.X);
        Assert.Equal(10f, childBackgrounds[2].Rect.Y);
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
    public async Task BuildDisplayList_NormalWhiteSpaceCollapsesRunsOfSpacesAndNewlines()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="width:400px;">a    b
                c</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("a b c", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_PreWhiteSpacePreservesRunsOfSpacesAndForcesLineBreaks()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="white-space:pre; width:400px;">a    b
            c</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var lines = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal("a    b", lines[0].Text);
        Assert.Equal("c", lines[1].Text);
        // The forced break lands on the very next line, not merely wherever the wrap algorithm
        // would otherwise have broken - confirmed by the line-height gap between the two baselines.
        Assert.Equal(16f * 1.35f, lines[1].Y - lines[0].Y, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_PreWhiteSpaceDoesNotWrapEvenWhenWiderThanItsBox()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="white-space:pre; width:40px;">a long line that would normally wrap</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("a long line that would normally wrap", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_PreWrapWhiteSpaceWrapsButStillForcesExplicitLineBreaks()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="white-space:pre-wrap; width:100px; font-size:16px;">short
            a somewhat longer line that should wrap</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var lines = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        // The forced break after "short" always starts a new paragraph, and that paragraph's own
        // long line then wraps independently across more than one line of its own.
        Assert.True(lines.Length >= 3);
        Assert.Equal("short", lines[0].Text);
        Assert.NotEqual("short", lines[1].Text);
    }

    [Fact]
    public async Task BuildDisplayList_PreLineWhiteSpaceCollapsesSpacesButKeepsForcedBreaks()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="white-space:pre-line; width:400px;">a    b
            c</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var lines = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal("a b", lines[0].Text);
        Assert.Equal("c", lines[1].Text);
    }

    [Fact]
    public async Task BuildDisplayList_NowrapWhiteSpaceKeepsTextOnOneLineEvenWhenWiderThanItsBox()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="white-space:nowrap; width:40px;">a long line that would normally wrap</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("a long line that would normally wrap", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_NowrapWhiteSpaceStillCollapsesRunsOfSpaces()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="white-space:nowrap;">a    b</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("a b", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_NowrapSpanStaysUnwrappedEvenInANarrowContainer()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="width:80px;">before <span style="white-space:nowrap;">a long run of nowrap text</span> after</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        // The span's own text stays a single, unwrapped DrawText command despite the 80px-wide
        // container - without `nowrap` this would have been split across several wrapped lines.
        var run = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text.Contains("nowrap")));
        Assert.Equal("a long run of nowrap text", run.Text);
    }

    [Fact]
    public async Task BuildDisplayList_PreHtmlElementDefaultsToPreservingWhitespace()
    {
        // <pre> gets `white-space: pre` from AngleSharp.Css's own UA stylesheet, the same way <ol>/
        // <ul> get their list-style-type default - not something this renderer has to inject itself.
        var document = await ParseAsync("""
            <html><body><pre>a    b</pre></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("a    b", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_UnsetWhiteSpaceDefaultsToNormal()
    {
        var document = await ParseAsync("""
            <html><body><p>a    b</p></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("a b", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_WhiteSpaceIsInheritedByChildElements()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="white-space:pre;"><span id="child">a    b</span></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        // The child <span> has no white-space of its own, so it inherits `pre` from its parent - a
        // single all-inline child with no plain-text siblings routes through the same leaf-text
        // shortcut a lone inline element with only text content already uses elsewhere in
        // LayoutNode (the one LayoutWrappedText itself goes through), preserving the run verbatim.
        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("a    b", text.Text);
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
    public async Task BuildDisplayList_InlineBlockSiblingsWithExplicitSizeFlowOnTheSameLine()
    {
        // A real, confirmed bug (not form-control-specific): two inline-block elements previously
        // always stacked vertically, identical to display:block, regardless of whether they fit on
        // one line - verified independently of any form control with two plain inline-block spans.
        var document = await ParseAsync("""
            <html><body>
                <div><span style="display:inline-block; width:20px; height:20px; background-color:#ff0000;"></span><span style="display:inline-block; width:20px; height:20px; background-color:#00ff00;"></span></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
        });

        var redFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));
        var greenFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(redFill.Rect.Y, greenFill.Rect.Y);
        Assert.Equal(redFill.Rect.X + redFill.Rect.Width, greenFill.Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_InlineBlockSiblingsWrapToANewLineWhenTheyDoNotFit()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px;">
                    <span style="display:inline-block; width:20px; height:20px; background-color:#ff0000;"></span><span style="display:inline-block; width:20px; height:20px; background-color:#00ff00;"></span><span style="display:inline-block; width:20px; height:20px; background-color:#0000ff;"></span>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var redFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));
        var greenFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));
        var blueFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 0, 255))));

        // A 50px-wide container fits exactly two 20px items (40px) but not a third - the third
        // wraps to its own new line rather than overflowing the container width.
        Assert.Equal(redFill.Rect.Y, greenFill.Rect.Y);
        Assert.True(blueFill.Rect.Y > redFill.Rect.Y);
        Assert.Equal(redFill.Rect.X, blueFill.Rect.X);
    }


    [Fact]
    public async Task BuildDisplayList_AutoSizedInlineBlockSiblingsStillEachGetTheirOwnLine()
    {
        // The one deliberate, documented scope cut: an inline-block whose own size is not known up
        // front (auto width here) cannot be predicted without a full trial layout, so it keeps this
        // renderer's older, pre-existing behavior (its own line) rather than flowing inline - not a
        // regression, since this was already true for every inline-block element before shared-line
        // flow existed for any of them.
        var document = await ParseAsync("""
            <html><body>
                <div><span style="display:inline-block; height:20px; background-color:#ff0000;">A</span><span style="display:inline-block; height:20px; background-color:#00ff00;">B</span></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
        });

        var redFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));
        var greenFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.True(greenFill.Rect.Y > redFill.Rect.Y);
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

        // A border-radius percentage resolves per-axis against the element's own border box - the
        // horizontal component against its 200px width (10% = 20px), the vertical component
        // against its 80px height (10% = 8px) - per spec. An older AngleSharp.Css version this
        // test used to pin (1.1.1) resolved both components against the containing block's width
        // instead (a confirmed upstream bug, reported with a reproducing test in AngleSharp.Css's
        // own suite - see BorderRadiusPercentageResolutionTests.cs there); fixed upstream since,
        // confirmed by this test flipping from the old (wrong) 30px/30px to the correct 20px/8px
        // with no renderer-side code change of its own. Neither exceeds either edge, so no
        // overlap-clamping kicks in here (that path is covered separately by the pixel-radius
        // clamp test below).
        Assert.Equal(20f, boxBackground.Radii.TopLeftX);
        Assert.Equal(8f, boxBackground.Radii.TopLeftY);
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

    [Fact]
    public async Task BuildDisplayList_DoesNotClipWhenOverflowIsVisible()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px;"><p>Hi</p></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        Assert.Empty(displayList.Commands.OfType<PushClipCommand>());
        Assert.Empty(displayList.Commands.OfType<PopClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_PushesClipToPaddingBoxWhenOverflowHidden()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:50px; border:5px solid black; padding:3px; overflow:hidden;"><p>Hi</p></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var push = Assert.Single(displayList.Commands.OfType<PushClipCommand>());
        Assert.Single(displayList.Commands.OfType<PopClipCommand>());

        // The border box is 100 + 2*5 (border) + 2*3 (padding) = 116 wide; the clip region is the
        // padding box, which excludes the border but includes the padding: border box minus the
        // 5px border on each side.
        Assert.Equal(5f, push.Rect.X);
        Assert.Equal(5f, push.Rect.Y);
        Assert.Equal(106f, push.Rect.Width);
        Assert.Equal(56f, push.Rect.Height);
    }

    [Theory]
    [InlineData("scroll")]
    [InlineData("auto")]
    [InlineData("hidden")]
    public async Task BuildDisplayList_ClipsForScrollAutoAndHiddenOverflow(string overflowValue)
    {
        var document = await ParseAsync($$"""
            <html><body>
                <div style="width:50px; height:20px; overflow:{{overflowValue}};"><p>Hi</p></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        Assert.Single(displayList.Commands.OfType<PushClipCommand>());
        Assert.Single(displayList.Commands.OfType<PopClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ClipsWhenEitherOverflowAxisIsHidden()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px; overflow-x:hidden; overflow-y:visible;"><p>Hi</p></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        // This renderer clips with a single rectangle covering both axes together whenever either
        // axis requests clipping - it does not clip one axis while leaving the other open.
        Assert.Single(displayList.Commands.OfType<PushClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ClipWrapsChildContentBetweenPushAndPop()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px; overflow:hidden;"><p>Hi</p></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var commands = displayList.Commands.ToArray();
        var pushIndex = Array.FindIndex(commands, c => c is PushClipCommand);
        var popIndex = Array.FindIndex(commands, c => c is PopClipCommand);
        var textIndex = Array.FindIndex(commands, c => c is DrawTextCommand t && t.Text == "Hi");

        Assert.True(pushIndex >= 0);
        Assert.True(popIndex > pushIndex);
        Assert.True(textIndex > pushIndex && textIndex < popIndex);
    }

    [Fact]
    public async Task BuildDisplayList_ClipShapeFollowsBorderRadius()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:50px; border-radius:12px; overflow:hidden;"><p>Hi</p></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var push = Assert.Single(displayList.Commands.OfType<PushClipCommand>());
        Assert.False(push.Radii.IsZero);
        Assert.Equal(12f, push.Radii.TopLeftX);
    }

    [Fact]
    public async Task BuildDisplayList_ClipsFlexContainerToOwnBounds()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:flex; width:80px; height:30px; overflow:hidden;">
                    <div style="width:20px; height:20px;">Hi</div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        Assert.Single(displayList.Commands.OfType<PushClipCommand>());
        Assert.Single(displayList.Commands.OfType<PopClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ClipsGridContainerToOwnBounds()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns: 1fr; width:80px; height:30px; overflow:hidden;">
                    <div style="width:20px; height:20px;">Hi</div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        Assert.Single(displayList.Commands.OfType<PushClipCommand>());
        Assert.Single(displayList.Commands.OfType<PopClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_AppliesRootScrollOffsetToPaintedContent()
    {
        var renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 80,
            DeviceWidth = 200,
            DeviceHeight = 80,
            FontSize = 16,
        };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:50px; height:300px; background-color:#ff0000;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var unscrolled = renderer.BuildDisplayList(document, renderDevice);
        var unscrolledFill = Assert.Single(unscrolled.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));

        document.Context.GetDomHarness();
        document.DocumentElement.SetScrollTop(40);

        var scrolled = renderer.BuildDisplayList(document, renderDevice);
        var scrolledFill = Assert.Single(scrolled.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));

        Assert.Equal(unscrolledFill.Rect.Y - 40f, scrolledFill.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_ClampsRootScrollOffsetToScrollableExtent()
    {
        var renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 80,
            DeviceWidth = 200,
            DeviceHeight = 80,
            FontSize = 16,
        };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:50px; height:150px; background-color:#ff0000;"></div>
              </body>
            </html>
            """, configuration);

        document.Context.GetDomHarness();
        document.DocumentElement.SetScrollTop(100_000);

        var maxScrollTop = document.DocumentElement.GetScrollTop();
        Assert.True(maxScrollTop > 0);
        Assert.True(maxScrollTop < 100_000);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));

        // Clamped to the true scrollable extent, not the (much larger) value that was set.
        Assert.Equal(-(float)maxScrollTop, fill.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_WithoutHarness_IgnoresScrollAndRendersUnaffected()
    {
        // No IRenderDevice service is registered here, so no DOM harness can even exist for this
        // context - confirms rendering is completely unaffected (and does not throw) for the
        // overwhelmingly common case of a document that was never wired up for interactive use.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:50px; height:20px; background-color:#00ff00;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));
        Assert.Equal(0f, fill.Rect.Y);
    }

    [Fact]
    public async Task MousePosition_OverAnElementForcesItToMatchHover()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>html, body { margin: 0; padding: 0; }</style></head><body>
                <div id="target" style="width:60px; height:40px;"></div>
            </body></html>
            """, configuration);
        var target = document.GetElementById("target")!;
        var harness = document.Context.GetDomHarness();

        Assert.False(target.Matches(":hover"));

        harness.MousePosition = (30, 20);

        Assert.True(target.Matches(":hover"));

        harness.MousePosition = (150, 80);

        Assert.False(target.Matches(":hover"));
    }

    [Fact]
    public async Task MousePosition_OverANestedElementForcesHoverOnTheWholeAncestorChain()
    {
        // A real pointer device makes every ancestor of the physically-hovered element match
        // :hover too (`.card:hover .title` relies on this) - not just the innermost hit target.
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>html, body { margin: 0; padding: 0; }</style></head><body>
                <div id="parent" style="width:80px; height:60px;">
                    <span id="child" style="display:inline-block; width:30px; height:20px;"></span>
                </div>
                <div id="sibling" style="width:80px; height:20px;"></div>
            </body></html>
            """, configuration);
        var parent = document.GetElementById("parent")!;
        var child = document.GetElementById("child")!;
        var sibling = document.GetElementById("sibling")!;
        var harness = document.Context.GetDomHarness();

        harness.MousePosition = (10, 10);

        Assert.True(child.Matches(":hover"));
        Assert.True(parent.Matches(":hover"));
        Assert.False(sibling.Matches(":hover"));
    }

    [Fact]
    public async Task MousePosition_ChangingHoverTargetClearsThePreviousForcedChain()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>html, body { margin: 0; padding: 0; }</style></head><body>
                <div id="first" style="width:60px; height:40px;"></div>
                <div id="second" style="width:60px; height:40px;"></div>
            </body></html>
            """, configuration);
        var first = document.GetElementById("first")!;
        var second = document.GetElementById("second")!;
        var harness = document.Context.GetDomHarness();

        harness.MousePosition = (30, 20);
        Assert.True(first.Matches(":hover"));

        harness.MousePosition = (30, 60);

        Assert.False(first.Matches(":hover"));
        Assert.True(second.Matches(":hover"));
    }

    [Fact]
    public async Task BuildDisplayList_HoverStateChangesRenderedBackgroundColor()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                #target { background-color: rgb(0, 0, 255); }
                #target:hover { background-color: rgb(255, 0, 0); }
            </style></head><body>
                <div id="target" style="width:60px; height:40px;"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        var restingDisplayList = renderer.BuildDisplayList(document, renderDevice);
        Assert.Single(restingDisplayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 0, 255))));

        harness.MousePosition = (30, 20);

        var hoveredDisplayList = renderer.BuildDisplayList(document, renderDevice);
        Assert.Single(hoveredDisplayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));
    }

    [Fact]
    public async Task BuildDisplayList_HoverTransitionInterpolatesBackgroundColorOverTime()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                #target { background-color: rgb(0, 0, 255); transition: background-color 1s linear; }
                #target:hover { background-color: rgb(255, 0, 0); }
            </style></head><body>
                <div id="target" style="width:60px; height:40px;"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        RenderColor CurrentColor()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 60f && f.Rect.Height == 40f));
            return fill.Color;
        }

        harness.MousePosition = (30, 20);

        // The very first frame after hover starts still shows the resting color - the transition
        // has not advanced yet (no AdvanceTime call has happened), matching how a real transition
        // never jumps straight to its end value on the same frame it started.
        Assert.Equal(new RenderColor(0, 0, 255), CurrentColor());

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.Equal(new RenderColor(128, 0, 128), CurrentColor());

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.Equal(new RenderColor(255, 0, 0), CurrentColor());
    }

    [Fact]
    public async Task BuildDisplayList_TransitionReversesSmoothlyFromItsCurrentMidFlightValue()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                #target { background-color: rgb(0, 0, 255); transition: background-color 1s linear; }
                #target:hover { background-color: rgb(255, 0, 0); }
            </style></head><body>
                <div id="target" style="width:60px; height:40px;"></div>
                <div id="elsewhere" style="width:60px; height:40px;"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        RenderColor CurrentColor()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 60f && f.Rect.Height == 40f && f.Rect.Y < 40f));
            return fill.Color;
        }

        harness.MousePosition = (30, 20);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.Equal(new RenderColor(128, 0, 128), CurrentColor());

        // Move away mid-flight - the reverse transition (back toward blue) must start from the
        // purple currently on screen, not jump back to blue and re-fade, and not jump straight to
        // blue either.
        harness.MousePosition = (30, 60);
        var justReversed = CurrentColor();
        Assert.Equal(new RenderColor(128, 0, 128), justReversed);

        // The reversed transition restarts with its own full declared duration from the value
        // currently on screen (purple), rather than "picking up" some remaining fraction of the
        // original 1s - so it takes a full second from here, not just the 500ms that would have
        // finished the original hover-in transition.
        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        Assert.Equal(new RenderColor(0, 0, 255), CurrentColor());
    }

    [Fact]
    public async Task BuildDisplayList_TransitionDelayPostponesTheStartOfInterpolation()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                #target { background-color: rgb(0, 0, 255); transition: background-color 1s linear 0.5s; }
                #target:hover { background-color: rgb(255, 0, 0); }
            </style></head><body>
                <div id="target" style="width:60px; height:40px;"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        RenderColor CurrentColor()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 60f && f.Rect.Height == 40f));
            return fill.Color;
        }

        harness.MousePosition = (30, 20);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(400));

        // Still within the 500ms delay - no interpolation has started yet.
        Assert.Equal(new RenderColor(0, 0, 255), CurrentColor());

        harness.AdvanceTime(TimeSpan.FromMilliseconds(600));

        // 1000ms elapsed total, 500ms of which was delay - 500ms into the 1000ms duration = halfway.
        Assert.Equal(new RenderColor(128, 0, 128), CurrentColor());
    }

    [Fact]
    public async Task BuildDisplayList_NoTransitionDeclaredJumpsStraightToTheHoverStyle()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                #target { background-color: rgb(0, 0, 255); }
                #target:hover { background-color: rgb(255, 0, 0); }
            </style></head><body>
                <div id="target" style="width:60px; height:40px;"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        harness.MousePosition = (30, 20);

        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 60f && f.Rect.Height == 40f));
        Assert.Equal(new RenderColor(255, 0, 0), fill.Color);
    }

    [Fact]
    public async Task BuildDisplayList_HoverTransitionInterpolatesWidthAsANumericLength()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); transition: width 1s linear; }
                #target:hover { width: 80px; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        float CurrentWidth()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 0, 255))));
            return fill.Rect.Width;
        }

        Assert.Equal(40f, CurrentWidth());

        harness.MousePosition = (30, 20);
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));

        Assert.Equal(60f, CurrentWidth(), precision: 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));

        Assert.Equal(80f, CurrentWidth(), precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_AnimationInterpolatesBetweenKeyframesOverTime()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                @keyframes fade {
                    0% { opacity: 0; }
                    100% { opacity: 1; }
                }
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); animation: fade 2s linear; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        float CurrentAlpha()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var push = displayList.Commands.OfType<PushOpacityCommand>().SingleOrDefault();
            return push?.Alpha ?? 1f;
        }

        // Registering the harness (via GetDomHarness) starts the virtual clock at 0 - the animation
        // begins immediately, matching how a real animation starts playing on page load.
        Assert.Equal(0f, CurrentAlpha(), precision: 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        Assert.Equal(0.5f, CurrentAlpha(), precision: 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        Assert.Equal(1f, CurrentAlpha(), precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_InfiniteAnimationLoopsBackToTheStart()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                @keyframes fade {
                    0% { opacity: 0; }
                    100% { opacity: 1; }
                }
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); animation: fade 2s linear infinite; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        float CurrentAlpha()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var push = displayList.Commands.OfType<PushOpacityCommand>().SingleOrDefault();
            return push?.Alpha ?? 1f;
        }

        harness.AdvanceTime(TimeSpan.FromMilliseconds(2500));

        // 2500ms into a 2000ms duration is 500ms into the *second* iteration (25% through it),
        // not "past the end" - infinite iteration count means it just keeps looping.
        Assert.Equal(0.25f, CurrentAlpha(), precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_AlternateDirectionReversesOnOddIterations()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                @keyframes fade {
                    0% { opacity: 0; }
                    100% { opacity: 1; }
                }
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); animation: fade 2s linear infinite alternate; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        float CurrentAlpha()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var push = displayList.Commands.OfType<PushOpacityCommand>().SingleOrDefault();
            return push?.Alpha ?? 1f;
        }

        // 2500ms = 500ms into the second iteration (index 1, odd) - alternate makes odd iterations
        // run backward, so this should read as 75% (1 - 0.25), not 25% like the "normal" test above.
        harness.AdvanceTime(TimeSpan.FromMilliseconds(2500));
        Assert.Equal(0.75f, CurrentAlpha(), precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_AnimationDelayPostponesTheStartOfInterpolation()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                @keyframes fade {
                    0% { opacity: 0; }
                    100% { opacity: 1; }
                }
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); animation: fade 2s linear 1s; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        float CurrentAlpha()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var push = displayList.Commands.OfType<PushOpacityCommand>().SingleOrDefault();
            return push?.Alpha ?? 1f;
        }

        // Still within the 1s delay - fill-mode defaults to "none", so no override applies yet and
        // the base opacity (1, fully opaque - CSS's own initial value) shows instead of 0%'s value.
        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.Equal(1f, CurrentAlpha(), precision: 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(1000));
        Assert.Equal(0.25f, CurrentAlpha(), precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FillModeForwardsFreezesAtTheEndingKeyframeAfterCompletion()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                @keyframes fade {
                    0% { opacity: 0; }
                    100% { opacity: 0.3; }
                }
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); animation: fade 1s linear forwards; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        float CurrentAlpha()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var push = displayList.Commands.OfType<PushOpacityCommand>().SingleOrDefault();
            return push?.Alpha ?? 1f;
        }

        harness.AdvanceTime(TimeSpan.FromMilliseconds(2000));

        // The animation ended after 1s (iteration-count defaults to 1), but fill-mode: forwards
        // keeps it frozen at the 100% keyframe's value (0.3) instead of reverting to the base
        // opacity (1) the way the no-fill-mode case would.
        Assert.Equal(0.3f, CurrentAlpha(), precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_AnimationEndsAndRevertsToNaturalValueWithoutFillModeForwards()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html><head><style>
                html, body { margin: 0; padding: 0; }
                @keyframes fade {
                    0% { opacity: 0; }
                    100% { opacity: 0.3; }
                }
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); animation: fade 1s linear; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        harness.AdvanceTime(TimeSpan.FromMilliseconds(2000));

        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var push = displayList.Commands.OfType<PushOpacityCommand>().SingleOrDefault();
        Assert.Null(push);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("url")]
    [InlineData("email")]
    [InlineData("search")]
    [InlineData("tel")]
    [InlineData("date")]
    public async Task BuildDisplayList_TextLikeInputGetsDefaultChromeAndShowsItsValue(string type)
    {
        var document = await ParseAsync($"""<html><body><input type="{type}" value="Hello" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        // Default UA-like chrome: a white background box, sized to the built-in default content
        // width (150px) plus the default 4px*2 padding and 1px*2 border (border-box width 160),
        // since none was authored, and a plain 1px gray border (four straight edges, no
        // border-radius, so PaintBorder falls back to four separate FillRectCommands). Filtered to
        // width 160 because the page's own default background is also a full-viewport white fill.
        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width == 160f));

        var borderEdges = displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(118, 118, 118))).ToList();
        Assert.NotEmpty(borderEdges);

        var valueText = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Hello"));
        // Left-aligned at the content box's own left edge (border + padding in from the box), not
        // centered - matching how a browser shows typed input text.
        Assert.Equal(background.Rect.X + 1f + 4f, valueText.X, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_TextLikeInputCentersItsValueVerticallyWhenTallerThanOneLine()
    {
        // An explicit height taller than one line must still center the value vertically rather
        // than hugging the bottom. Centering is done on the text's own real visual ink (an
        // ascent/descent approximation of the bundled sans-serif font - see
        // FormControlTextAscentRatio/DescentRatio), not on the taller CSS line-height box: the bug
        // this guards against centered on the line-height box instead, which - because a
        // line-height box always has room "below the ink" for descenders/leading that a caseless
        // value like "Hi" never uses - left a visibly larger gap below the text than above it.
        var document = await ParseAsync("""
            <html><body>
                <input type="text" value="Hi" style="height:60px;" />
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 20f,
        });

        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width < 300f));
        var valueText = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Hi"));

        // border-top-width 1px + default padding-top 2px, and content height is the authored 60px.
        var contentTop = background.Rect.Y + 1f + 2f;
        // Mirrors FormControlTextAscentRatio/DescentRatio in HtmlRenderer.cs.
        const float AscentRatio = 0.928f;
        const float DescentRatio = 0.236f;
        var topGap = (valueText.Y - (20f * AscentRatio)) - contentTop;
        var bottomGap = (contentTop + 60f) - (valueText.Y + (20f * DescentRatio));
        Assert.Equal(topGap, bottomGap, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_PasswordInputMasksItsValue()
    {
        var document = await ParseAsync("""<html><body><input type="password" value="secret" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        Assert.DoesNotContain(displayList.Commands, c => c is DrawTextCommand t && t.Text.Contains("secret"));
        var masked = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal(new string('•', 6), masked.Text);
    }

    [Fact]
    public async Task BuildDisplayList_TextInputDefaultsCanBeOverriddenByAuthorCss()
    {
        // Every one of the synthesized defaults - border color/width, background, width - has an
        // authored equivalent here, and each authored value must win exactly like it does for any
        // other element, proving the defaults are only gap-fillers and not a fixed native look.
        var document = await ParseAsync("""
            <html><body>
                <input type="text" value="Hi" style="width:80px; background-color:#ff0000; border: 3px solid #0000ff;" />
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));
        // Border-box width, not the authored content-width in isolation: default content-box
        // sizing (unchanged here) adds the border (3px * 2) and the still-default padding (4px * 2,
        // since only border/background/width were overridden) on top of the authored 80px content
        // width - 80 + 6 + 8 = 94, the same box-model arithmetic a real browser would apply.
        Assert.Equal(94f, background.Rect.Width);

        Assert.DoesNotContain(displayList.Commands, c => c is FillRectCommand f && f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width < 300f);
        Assert.DoesNotContain(displayList.Commands, c => c is FillRectCommand f && f.Color.Equals(new RenderColor(118, 118, 118)));

        var borderEdges = displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 0, 255))).ToList();
        Assert.NotEmpty(borderEdges);
    }

    [Fact]
    public async Task BuildDisplayList_UnfocusedTextInputPaintsNoCaret()
    {
        var document = await ParseAsync("""<html><body><input type="text" value="Hi" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        // The caret is 1.5px wide (FormControlCaretWidth) - narrow enough that no other form
        // control fill (background, border edge, checkbox/radio accent) could plausibly collide
        // with it, so filtering on that width alone is enough to isolate it.
        Assert.DoesNotContain(displayList.Commands, c => c is FillRectCommand f && f.Rect.Width == 1.5f);
    }

    [Fact]
    public async Task BuildDisplayList_FocusedTextInputPaintsAnOpaqueCaretAfterItsValue()
    {
        var document = await ParseAsync("""<html><body><input id="target" type="text" value="Hi" /></body></html>""");
        var target = (IHtmlElement)document.GetElementById("target")!;
        target.DoFocus();

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var valueText = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Hi"));
        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width == 160f));

        // Without a registered IDomHarness there is no virtual clock to fade the caret against, so
        // it paints fully opaque instead of frozen mid-fade - the same fallback CSS `transition`/
        // `animation` use when no harness exists.
        var caret = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 1.5f));
        Assert.Equal(255, caret.Color.A);
        // Right after the typed value's own left edge, and still inside the input's own box - not
        // asserting an exact pixel offset (which would require duplicating this renderer's own text
        // measurement in the test), just that it tracks the end of the text rather than always
        // sitting at the content edge.
        Assert.True(caret.Rect.X > valueText.X);
        Assert.True(caret.Rect.X < background.Rect.X + background.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_FocusedEmptyTextInputPaintsCaretAtTheContentEdge()
    {
        var document = await ParseAsync("""<html><body><input id="target" type="text" /></body></html>""");
        var target = (IHtmlElement)document.GetElementById("target")!;
        target.DoFocus();

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width == 160f));
        var caret = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 1.5f));

        // border-left-width 1px + default padding-left 4px, the same content-edge math the value
        // text itself is positioned at (see BuildDisplayList_TextLikeInputGetsDefaultChromeAndShowsItsValue).
        Assert.Equal(background.Rect.X + 1f + 4f, caret.Rect.X, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_FocusedTextInputCaretFadesWithTheVirtualClock()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100, FontSize = 16f };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""<html><body><input id="target" type="text" value="Hi" /></body></html>""", configuration);
        var target = (IHtmlElement)document.GetElementById("target")!;
        target.DoFocus();
        var harness = document.Context.GetDomHarness();
        var renderer = new HtmlRenderer();

        int CurrentCaretAlpha()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            return Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 1.5f)).Color.A;
        }

        // A smooth cosine "breathe" over a 1000ms period (FormControlCaretBlinkPeriodMs): fully
        // visible at t=0, fully invisible at the half-period mark, back to fully visible a full
        // period later - not a hard on/off blink, so the quarter-period sample lands at a genuine
        // in-between alpha rather than either extreme.
        Assert.Equal(255, CurrentCaretAlpha());

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, CurrentCaretAlpha());

        harness.AdvanceTime(TimeSpan.FromMilliseconds(250));
        Assert.InRange(CurrentCaretAlpha(), 100, 155);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(250));
        Assert.Equal(255, CurrentCaretAlpha());
    }

    [Fact]
    public async Task BuildDisplayList_UncheckedCheckboxPaintsOnlyItsBoxNoFill()
    {
        var document = await ParseAsync("""<html><body><input type="checkbox" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        Assert.DoesNotContain(displayList.Commands, c => c is FillRectCommand f && f.Color.Equals(FormControlAccentColorForTests));
    }

    [Theory]
    [InlineData("checkbox")]
    [InlineData("radio")]
    public async Task BuildDisplayList_AdjacentCheckboxesOrRadiosGetADefaultGapBetweenThem(string type)
    {
        // Real UA stylesheets give a checkbox/radio a small default margin that this renderer's
        // own UA defaults previously omitted entirely - two controls written back-to-back with no
        // whitespace between their tags (as real forms commonly do, and as this exact markup does)
        // rendered flush against each other with no visible gap at all. Wrapped in a <div> so both
        // controls flow through the same parent's ordinary inline-merging child layout, rather than
        // being laid out as top-level document children (a separate, pre-existing renderer
        // limitation unrelated to this margin default: direct children of <body> do not currently
        // merge onto shared lines the way a wrapping block element's own children do).
        var document = await ParseAsync($"""<html><body><div><input type="{type}" /><input type="{type}" /></div></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        // Each control's own default white background fill covers its whole border box in one
        // rect (unlike the border, which for a checkbox is four separate straight-edge strips) -
        // the simplest, unambiguous way to identify each control's own horizontal extent.
        var backgrounds = displayList.Commands.OfType<FillRectCommand>()
            .Where(f => f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width < 300f)
            .OrderBy(f => f.Rect.X)
            .ToList();

        Assert.Equal(2, backgrounds.Count);
        var firstBoxRight = backgrounds[0].Rect.X + backgrounds[0].Rect.Width;
        var secondBoxLeft = backgrounds[1].Rect.X;
        Assert.True(secondBoxLeft - firstBoxRight > 0f, $"expected a gap between adjacent {type} controls, got firstBoxRight={firstBoxRight}, secondBoxLeft={secondBoxLeft}");
    }

    [Fact]
    public async Task BuildDisplayList_CheckedCheckboxPaintsAccentFillInsideItsBox()
    {
        var document = await ParseAsync("""<html><body><input type="checkbox" checked /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(FormControlAccentColorForTests)));
        // A perfectly square fill, inset within the (also square, default-sized) checkbox box.
        Assert.Equal(fill.Rect.Width, fill.Rect.Height, precision: 3);
        Assert.True(fill.Rect.Width > 0f);
    }

    [Fact]
    public async Task BuildDisplayList_CheckedCheckboxFillStaysCenteredWhenBoxIsOverriddenAsymmetrically()
    {
        // Width and height default to the same font-relative size, so the fill's inset used to be
        // computed off contentWidth alone and simply reused for both axes - correct only as long as
        // the box stays square. Overriding height independently of width must still center the fill
        // on both axes rather than leaving it flush with the top (or bottom) edge.
        var document = await ParseAsync("""
            <html><body><input type="checkbox" checked style="width:20px; height:40px;" /></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width < 100f));
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(FormControlAccentColorForTests)));

        var topGap = fill.Rect.Y - background.Rect.Y;
        var bottomGap = (background.Rect.Y + background.Rect.Height) - (fill.Rect.Y + fill.Rect.Height);
        Assert.Equal(topGap, bottomGap, precision: 3);

        var leftGap = fill.Rect.X - background.Rect.X;
        var rightGap = (background.Rect.X + background.Rect.Width) - (fill.Rect.X + fill.Rect.Width);
        Assert.Equal(leftGap, rightGap, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_CheckedRadioPaintsCircularAccentFillAndCircularBorder()
    {
        var document = await ParseAsync("""<html><body><input type="radio" checked /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        // The box's own outline is circular (default border-radius: 50%, uniform 1px width) -
        // painted as a single StrokeRoundedRectCommand rather than four straight edges.
        var strokedBorder = Assert.Single(displayList.Commands.OfType<StrokeRoundedRectCommand>());
        Assert.True(strokedBorder.Radii.TopLeftX > 0f);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(FormControlAccentColorForTests)));
        Assert.True(fill.Radii.TopLeftX > 0f);
    }

    [Fact]
    public async Task BuildDisplayList_ColorInputPaintsSwatchFromItsValue()
    {
        var document = await ParseAsync("""<html><body><input type="color" value="#ff8800" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 100,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 136, 0))));
    }

    [Fact]
    public async Task BuildDisplayList_ColorInputSwatchFillsEntireContentBoxWhenTaller()
    {
        // The swatch previously used a fixed line-height-tall rect regardless of the box's actual
        // content height, which for an explicitly taller box left it hugging the top with unfilled
        // space below it - it must fill the whole content box instead, so it is centered (trivially,
        // by filling everything) no matter how tall the box ends up.
        var document = await ParseAsync("""
            <html><body>
                <input type="color" value="#ff8800" style="height:40px;" />
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var swatch = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 136, 0))));
        Assert.Equal(40f, swatch.Rect.Height, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_SelectShowsOnlyItsSelectedOptionText()
    {
        var document = await ParseAsync("""
            <html><body>
                <select>
                    <option>First</option>
                    <option selected>Second</option>
                    <option>Third</option>
                </select>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        // Only the selected option's text is ever drawn - a <select> never lays out every <option>
        // stacked underneath it the way a plain container element would.
        var texts = displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text).ToList();
        Assert.Contains("Second", texts);
        Assert.DoesNotContain("First", texts);
        Assert.DoesNotContain("Third", texts);
    }

    [Fact]
    public async Task BuildDisplayList_SelectShrinksToFitItsLabelAndShowsItLeftAlignedNextToTheArrow()
    {
        // A native <select> is left-aligned and shrink-to-fit around its own selected option (plus
        // room for the dropdown arrow), not centered inside a fixed-width box the way an early,
        // incorrect implementation (reusing the <button>-centering branch) rendered it.
        var document = await ParseAsync("""
            <html><body>
                <select>
                    <option>First</option>
                    <option selected>Second</option>
                </select>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        // Button-like gray background, not the white a text-like input gets.
        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(232, 232, 232)) && f.Rect.Width < 300f));
        var label = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Second"));
        var arrow = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "⌄"));

        // Left-aligned at the content box's own left edge (border + padding in from the box), not
        // centered.
        Assert.Equal(background.Rect.X + 1f + 4f, label.X, precision: 3);

        // Narrower than a text-like input's fixed 150px default width - shrunk to fit "Second" plus
        // the arrow - and the arrow itself sits shortly after the label, not flush with a far-away
        // right edge of an unnecessarily wide box.
        Assert.True(background.Rect.Width < 150f);
        Assert.True(arrow.X > label.X);
        Assert.True(arrow.X < background.Rect.X + background.Rect.Width);

        // The arrow glyph's own ink sits much closer to the baseline than ordinary text does (it
        // is a short symbol, not a full-height letter run), so painting it on the exact same
        // baseline as the label visibly pushed it toward the bottom of the box - a real, reported
        // bug. Its baseline must sit strictly above the label's own to compensate.
        Assert.True(arrow.Y < label.Y);
    }

    [Fact]
    public async Task BuildDisplayList_SelectWithNoSelectedOptionShowsTheFirstOne()
    {
        var document = await ParseAsync("""
            <html><body>
                <select>
                    <option>Alpha</option>
                    <option>Beta</option>
                </select>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text).ToList();
        Assert.Contains("Alpha", texts);
        Assert.DoesNotContain("Beta", texts);
    }

    [Theory]
    [InlineData("<button>Click Me</button>", "Click Me")]
    [InlineData("""<input type="submit" value="Send" />""", "Send")]
    [InlineData("""<input type="submit" />""", "Submit")]
    [InlineData("""<input type="reset" />""", "Reset")]
    public async Task BuildDisplayList_ButtonLikeControlShowsItsLabelShrunkToFitWithDefaultChrome(string markup, string expectedLabel)
    {
        var document = await ParseAsync($"<html><body>{markup}</body></html>");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var label = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == expectedLabel));

        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(232, 232, 232))));
        // Shrink-to-fit: the button's own box is sized to its measured label, not a fixed default
        // width the way a text-like input is - so it must be narrower than that 150px default while
        // still being wide enough to actually contain the label text just drawn.
        Assert.True(background.Rect.Width < 150f);
        Assert.True(background.Rect.Width > 0f);
        Assert.True(label.X >= background.Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_TextAreaGetsDefaultChromeAndStillFlowsItsOwnTextContent()
    {
        var document = await ParseAsync("""<html><body><textarea>hello world</textarea></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width == 160f));
        // A textarea's own child text node still flows through the ordinary block child-layout
        // path (unlike <select>/<button>, it is never excluded from it) - this is what actually
        // paints its content, not any bespoke form-control content painting.
        Assert.Contains(displayList.Commands, c => c is DrawTextCommand t && t.Text == "hello world");
    }

    [Fact]
    public async Task BuildDisplayList_HiddenInputPaintsNothingAtAll()
    {
        var document = await ParseAsync("""
            <html><body>
                <input type="hidden" value="secret-token" />
                <div style="width:10px; height:10px; background-color:#00ff00;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        Assert.DoesNotContain(displayList.Commands, c => c is DrawTextCommand t && t.Text.Contains("secret-token"));
        // Filtered to width 150 (the form-control default) rather than a bare "no white fill at
        // all", since the page's own default background is also a full-viewport white fill.
        Assert.DoesNotContain(displayList.Commands, c => c is FillRectCommand f && f.Color.Equals(new RenderColor(255, 255, 255)) && f.Rect.Width == 160f);
        // The visible sibling still renders normally - the hidden input contributes no box at all
        // to disrupt the layout around it, not just an invisible one.
        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));
    }

    [Fact]
    public async Task BuildDisplayList_NoTransformProducesNoPushOrPopTransformCommands()
    {
        var document = await ParseAsync("""<html><body><div style="width:10px;height:10px;"></div></body></html>""");
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 100, ViewPortHeight = 100 });
        Assert.DoesNotContain(displayList.Commands, c => c is PushTransformCommand or PopTransformCommand);
    }

    [Fact]
    public async Task BuildDisplayList_TransformNoneProducesNoPushOrPopTransformCommands()
    {
        var document = await ParseAsync("""<html><body><div style="width:10px;height:10px; transform:none;"></div></body></html>""");
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 100, ViewPortHeight = 100 });
        Assert.DoesNotContain(displayList.Commands, c => c is PushTransformCommand or PopTransformCommand);
    }

    [Fact]
    public async Task BuildDisplayList_TranslateTransformProducesExactPixelOffsetRegardlessOfOrigin()
    {
        // Pure translation is origin-invariant - the origin shift cancels out exactly for a pure
        // translate - so this holds for the default center origin too, not just an explicit one.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: translate(15px, 8px);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushTransformCommand>());
        Assert.Equal(new RenderTransform2D(1f, 0f, 0f, 1f, 15f, 8f), push.Transform);
        Assert.Single(displayList.Commands.OfType<PopTransformCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_PercentageTranslateResolvesAgainstOwnBorderBox()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: translate(50%, 50%);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushTransformCommand>());
        Assert.Equal(20f, push.Transform.E, precision: 3);
        Assert.Equal(10f, push.Transform.F, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_RotateTransformProducesCorrectMatrixAroundDefaultCenter()
    {
        // AngleSharp.Css's own rotate() ComputeMatrix was previously confirmed broken (always NaN)
        // and this renderer used to work around it with a temporary no-op guard - now fixed
        // upstream, this test asserts the actual rotation matrix directly, matching this renderer's
        // policy of always trusting AngleSharp.Css's own computed matrix rather than special-casing
        // individual functions locally (see ParseCssTransform/ConvertToRenderTransform's remarks).
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: rotate(180deg);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushTransformCommand>());
        var t = push.Transform;

        // A 180-degree rotation around the box's own default center (20, 10) maps the box's own
        // top-left corner (0, 0) exactly to its own bottom-right corner (40, 20).
        Assert.Equal(-1f, t.A, precision: 3);
        Assert.Equal(0f, t.B, precision: 3);
        Assert.Equal(0f, t.C, precision: 3);
        Assert.Equal(-1f, t.D, precision: 3);
        Assert.Equal(40f, t.E, precision: 2);
        Assert.Equal(20f, t.F, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_ChainedTransformFunctionsComposeLeftToRightPerCssSpec()
    {
        // "translate then scale" applies scale to the point first (it is listed last/rightmost),
        // then translate; "scale then translate" is the reverse - order changes the visual result,
        // and this pins down the exact direction CSS defines, not just "some" composition. Uses
        // scale() (translate/rotate would work equally well now) simply because it was the function
        // originally used to build this test before rotate() got fixed upstream.
        var translateThenScale = await ParseAsync("""
            <html><body><div style="width:1px; height:1px; transform-origin: 0 0; transform: translate(100px, 0) scale(2);"></div></body></html>
            """);
        var scaleThenTranslate = await ParseAsync("""
            <html><body><div style="width:1px; height:1px; transform-origin: 0 0; transform: scale(2) translate(100px, 0);"></div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var device = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 };

        var pushA = Assert.Single(renderer.BuildDisplayList(translateThenScale, device).Commands.OfType<PushTransformCommand>());
        var pushB = Assert.Single(renderer.BuildDisplayList(scaleThenTranslate, device).Commands.OfType<PushTransformCommand>());

        Assert.Equal(100f, pushA.Transform.E, precision: 2);
        Assert.Equal(200f, pushB.Transform.E, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_ScaleTransformExpandsSymmetricallyAroundDefaultCenter()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: scale(2);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushTransformCommand>());
        var t = push.Transform;

        Assert.Equal(-20f, t.E, precision: 2);
        Assert.Equal(-10f, t.F, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_MatrixFunctionPreservesAllSixComponents()
    {
        // The plain 6-value 2D matrix() form was previously confirmed to compute *wrong* values in
        // AngleSharp.Css itself (a column-major-vs-row-major mismatch in how it padded the array
        // passed to TransformMatrix's constructor) - now fixed upstream, with a reproducing test in
        // AngleSharp.Css's own suite (TransformFunctionsTests.PlainSixValueMatrixFunctionPreservesAllSixComponents)
        // confirming it. This renderer never guarded against that bug locally to begin with - per
        // its own policy (see ParseCssTransform/ConvertToRenderTransform's remarks), it always
        // trusts whatever AngleSharp.Css computes - so once AngleSharp.Css's own bug was fixed,
        // matrix() started working correctly here automatically, with no change needed in this
        // renderer at all. This test asserts the actual, now-correct values directly, using the
        // same non-symmetric coefficients as AngleSharp.Css's own regression test specifically so a
        // symmetric input could never mask a b/c transposition or an e/f loss the way it did before.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; transform-origin: 0 0; transform: matrix(2, 3, 4, 5, 6, 7);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushTransformCommand>());
        var t = push.Transform;

        Assert.Equal(2f, t.A, precision: 2);
        Assert.Equal(3f, t.B, precision: 2);
        Assert.Equal(4f, t.C, precision: 2);
        Assert.Equal(5f, t.D, precision: 2);
        Assert.Equal(6f, t.E, precision: 2);
        Assert.Equal(7f, t.F, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_TranslateZDoesNotPerturbA2DTransformChain()
    {
        // translateZ() is a 3D function this renderer never filters out before asking
        // AngleSharp.Css to parse/compute it (see ParseCssTransform's own remarks) - its own effect
        // is Z-only, so its 2D projection (M11/M12/M21/M22/Tx/Ty) comes back as pure identity and
        // composing it into the chain leaves the following, genuinely 2D translate() untouched.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; transform: translateZ(50px) translate(12px, 0px);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushTransformCommand>());
        Assert.Equal(12f, push.Transform.E, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_TransformWrapsBackgroundBorderAndChildrenTogether()
    {
        // A transform affects the *whole* element - its own background/border and every descendant
        // - not just its content, unlike overflow clipping (which excludes border/outline).
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; background-color:#ff0000; border:2px solid #0000ff; transform: scale(1.5);">
                    <span style="display:inline-block; width:4px; height:4px; background-color:#00ff00;"></span>
                </div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var commands = displayList.Commands.ToList();

        var pushIndex = commands.FindIndex(c => c is PushTransformCommand);
        var popIndex = commands.FindIndex(c => c is PopTransformCommand);
        var backgroundIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(255, 0, 0)));
        var childIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(0, 255, 0)));

        Assert.True(pushIndex >= 0 && popIndex > pushIndex);
        Assert.True(backgroundIndex > pushIndex && backgroundIndex < popIndex);
        Assert.True(childIndex > pushIndex && childIndex < popIndex);
    }

    [Fact]
    public async Task BuildDisplayList_AuthoredTransformDoesNotCrashAngleSharpCssComputation()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; transform: translate(1px, 1px);"></div>
                <div style="width:10px; height:10px; transform: translateX(1px);"></div>
                <div style="width:10px; height:10px; transform: translateY(1px);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        Assert.Equal(3, displayList.Commands.OfType<PushTransformCommand>().Count());
    }

    [Fact]
    public async Task BuildDisplayList_NoFilterProducesNoPushOrPopFilterCommands()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px;"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        Assert.Empty(displayList.Commands.OfType<PushFilterCommand>());
        Assert.Empty(displayList.Commands.OfType<PopFilterCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_FilterNoneProducesNoPushOrPopFilterCommands()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; filter: none;"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        Assert.Empty(displayList.Commands.OfType<PushFilterCommand>());
        Assert.Empty(displayList.Commands.OfType<PopFilterCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_GrayscaleFilterParsesFractionAndPercentageIdentically()
    {
        var document = await ParseAsync("""
            <html><body>
                <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=" style="width:10px; height:10px; filter: grayscale(0.9);">
                <img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=" style="width:10px; height:10px; filter: grayscale(90%);">
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var pushes = displayList.Commands.OfType<PushFilterCommand>().ToList();

        Assert.Equal(2, pushes.Count);

        foreach (var push in pushes)
        {
            var function = Assert.Single(push.Functions);
            Assert.Equal(RenderFilterFunctionKind.Grayscale, function.Kind);
            Assert.Equal(0.9f, function.Amount, precision: 3);
        }
    }

    [Fact]
    public async Task BuildDisplayList_ChainedFilterFunctionsPreserveAuthoredOrder()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; filter: grayscale(0.9) blur(2px);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushFilterCommand>());

        Assert.Equal(2, push.Functions.Count);
        Assert.Equal(RenderFilterFunctionKind.Grayscale, push.Functions[0].Kind);
        Assert.Equal(0.9f, push.Functions[0].Amount, precision: 3);
        Assert.Equal(RenderFilterFunctionKind.Blur, push.Functions[1].Kind);
        Assert.Equal(2f, push.Functions[1].Amount, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_DropShadowFilterParsesOffsetsBlurAndColorRegardlessOfColorPosition()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; filter: drop-shadow(2px 4px 6px red);"></div>
                <div style="width:10px; height:10px; filter: drop-shadow(red 2px 4px 6px);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var pushes = displayList.Commands.OfType<PushFilterCommand>().ToList();

        Assert.Equal(2, pushes.Count);

        foreach (var push in pushes)
        {
            var function = Assert.Single(push.Functions);
            Assert.Equal(RenderFilterFunctionKind.DropShadow, function.Kind);
            Assert.Equal(2f, function.OffsetX, precision: 2);
            Assert.Equal(4f, function.OffsetY, precision: 2);
            Assert.Equal(6f, function.Amount, precision: 2);
            Assert.Equal(new RenderColor(255, 0, 0), function.Color);
        }
    }

    [Fact]
    public async Task BuildDisplayList_FilterWrapsBackgroundBorderAndChildrenTogether()
    {
        // A filter affects the *whole* element - its own background/border and every descendant -
        // not just its content, the same "wraps everything" scope PushTransformCommand already has.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; background-color:#ff0000; border:2px solid #0000ff; filter: grayscale(1);">
                    <span style="display:inline-block; width:4px; height:4px; background-color:#00ff00;"></span>
                </div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var commands = displayList.Commands.ToList();

        var pushIndex = commands.FindIndex(c => c is PushFilterCommand);
        var popIndex = commands.FindIndex(c => c is PopFilterCommand);
        var backgroundIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(255, 0, 0)));
        var childIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(0, 255, 0)));

        Assert.True(pushIndex >= 0 && popIndex > pushIndex);
        Assert.True(backgroundIndex > pushIndex && backgroundIndex < popIndex);
        Assert.True(childIndex > pushIndex && childIndex < popIndex);
    }

    [Fact]
    public async Task BuildDisplayList_TransformAndFilterNestFilterInsideTransform()
    {
        // Documents this renderer's own, deliberate choice of nesting order (see ParseCssFilter's
        // remarks in HtmlRenderer.LayoutElement): the filter's own raster operates inside the
        // element's already-transformed local space, so PushFilter has to appear *after*
        // PushTransform, and PopFilter *before* PopTransform.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; transform: scale(1.5); filter: grayscale(1);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var commands = displayList.Commands.ToList();

        var pushTransformIndex = commands.FindIndex(c => c is PushTransformCommand);
        var pushFilterIndex = commands.FindIndex(c => c is PushFilterCommand);
        var popFilterIndex = commands.FindIndex(c => c is PopFilterCommand);
        var popTransformIndex = commands.FindIndex(c => c is PopTransformCommand);

        Assert.True(pushTransformIndex < pushFilterIndex);
        Assert.True(pushFilterIndex < popFilterIndex);
        Assert.True(popFilterIndex < popTransformIndex);
    }

    [Fact]
    public async Task BuildDisplayList_FullyOpaqueProducesNoPushOrPopOpacityCommands()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; opacity: 1;"></div>
                <div style="width:10px; height:10px;"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        Assert.Empty(displayList.Commands.OfType<PushOpacityCommand>());
        Assert.Empty(displayList.Commands.OfType<PopOpacityCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_PartialOpacityProducesPushOpacityWithTheParsedAlpha()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; opacity: 0.4;"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushOpacityCommand>());
        Assert.Equal(0.4f, push.Alpha, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_OpacityOutOfRangeIsClampedToZeroToOne()
    {
        var document = await ParseAsync("""
            <html><body>
                <div id="over" style="width:10px; height:10px; opacity: 2;"></div>
                <div id="under" style="width:10px; height:10px; opacity: -1;"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });

        // opacity: 2 clamps to 1 (fully opaque), which produces no push/pop at all (same as
        // unspecified) - only opacity: -1 (clamped to 0) should push.
        var push = Assert.Single(displayList.Commands.OfType<PushOpacityCommand>());
        Assert.Equal(0f, push.Alpha, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_OpacityWrapsBackgroundBorderAndChildrenTogether()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; background-color:#ff0000; border:2px solid #0000ff; opacity: 0.5;">
                    <span style="display:inline-block; width:4px; height:4px; background-color:#00ff00;"></span>
                </div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var commands = displayList.Commands.ToList();

        var pushIndex = commands.FindIndex(c => c is PushOpacityCommand);
        var popIndex = commands.FindIndex(c => c is PopOpacityCommand);
        var backgroundIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(255, 0, 0)));
        var childIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(0, 255, 0)));

        Assert.True(pushIndex >= 0 && popIndex > pushIndex);
        Assert.True(backgroundIndex > pushIndex && backgroundIndex < popIndex);
        Assert.True(childIndex > pushIndex && childIndex < popIndex);
    }

    [Fact]
    public async Task BuildDisplayList_TransformOpacityAndFilterNestInOpacityBetweenTransformAndFilter()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px; transform: scale(1.5); opacity: 0.5; filter: grayscale(1);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var commands = displayList.Commands.ToList();

        var pushTransformIndex = commands.FindIndex(c => c is PushTransformCommand);
        var pushOpacityIndex = commands.FindIndex(c => c is PushOpacityCommand);
        var pushFilterIndex = commands.FindIndex(c => c is PushFilterCommand);
        var popFilterIndex = commands.FindIndex(c => c is PopFilterCommand);
        var popOpacityIndex = commands.FindIndex(c => c is PopOpacityCommand);
        var popTransformIndex = commands.FindIndex(c => c is PopTransformCommand);

        Assert.True(pushTransformIndex < pushOpacityIndex);
        Assert.True(pushOpacityIndex < pushFilterIndex);
        Assert.True(pushFilterIndex < popFilterIndex);
        Assert.True(popFilterIndex < popOpacityIndex);
        Assert.True(popOpacityIndex < popTransformIndex);
    }

    // Mirrors the private HtmlRenderer.FormControlAccentColor constant (26, 115, 232) - kept as an
    // independent literal here rather than reflecting into the private field, so a test failure
    // reads as "the painted color changed" rather than needing reflection to even compile.
    [Fact]
    public async Task BuildDisplayList_TextOverflowEllipsisTruncatesOverflowingSingleLine()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:80px; overflow:hidden; white-space:nowrap; text-overflow:ellipsis;">This is a long line of text</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.EndsWith("…", text.Text);
        Assert.True(text.Text.Length < "This is a long line of text".Length);
    }

    [Fact]
    public async Task BuildDisplayList_TextOverflowEllipsisHasNoEffectWithoutClipping()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:80px; white-space:nowrap; text-overflow:ellipsis;">This is a long line of text</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("This is a long line of text", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_WordBreakAllWrapsAnOverlongWordAcrossMultipleLines()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; word-break:break-all;">aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var lines = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.True(lines.Length > 1);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string.Concat(lines.Select(line => line.Text)));
    }

    [Fact]
    public async Task BuildDisplayList_OverflowWrapBreakWordBreaksAnOverlongWordAsLastResort()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; overflow-wrap:break-word;">aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var lines = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.True(lines.Length > 1);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string.Concat(lines.Select(line => line.Text)));
    }

    [Fact]
    public async Task BuildDisplayList_OverflowWrapNormalLeavesAnOverlongWordOnOneOverflowingLine()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px;">aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", text.Text);
    }

    private static readonly RenderColor FormControlAccentColorForTests = new(26, 115, 232);

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