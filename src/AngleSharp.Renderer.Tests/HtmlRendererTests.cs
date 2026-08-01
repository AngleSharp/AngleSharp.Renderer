using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;

namespace AngleSharp.Renderer.Tests;

public sealed class HtmlRendererTests
{
    [Fact]
    public async Task BuildDisplayList_IncludesBackgroundAndTextCommands()
    {
        var document = await ParseAsync("<html><body><h1>Title</h1><p>Hello renderer world from AngleSharp.</p></body></html>");
        var renderer = new HtmlRenderer();

        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 360,
            Height = 240,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
            FontSize = 16f,
        });

        var imageCommand = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(40f, imageCommand.Rect.Width);
        Assert.Equal(20f, imageCommand.Rect.Height);
        Assert.NotEmpty(imageCommand.Image.Data);
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 360,
            Height = 240,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 220,
            Height = 180,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
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
        var document = await ParseAsync("""
            <html><body>
                <table style="border-collapse:collapse;">
                    <tr><td style="border:1px solid black;">A</td><td style="border:1px solid black;">B</td></tr>
                    <tr><td style="border:1px solid black;">C</td><td style="border:1px solid black;">D</td></tr>
                </table>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
            FontSize = 16f,
        });

        var borderCommands = displayList.Commands
            .OfType<FillRectCommand>()
            .Count(command => command.Color == RenderColor.Black);

        Assert.True(borderCommands < 10, $"Expected collapsed borders to reduce border commands, but found {borderCommands}.");
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 140,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(76f, childBackgrounds[0].Rect.X);
        Assert.Equal(96f, childBackgrounds[1].Rect.X);
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 20f && command.Rect.Height == 10f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(31f, childBackgrounds[0].Rect.X);
        Assert.Equal(81f, childBackgrounds[1].Rect.X);
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 140,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
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
    public async Task BuildDisplayList_AppliesLetterSpacingToTextCommands()
    {
        var document = await ParseAsync("""
            <html><body>
                <p style="letter-spacing:2px;">Spacing</p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 120,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 160,
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
        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 120,
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

        var image = renderer.RenderToPng(document, new HtmlRenderOptions
        {
            Width = 320,
            Height = 180,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 200,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 200,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 150,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 150,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 200,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 320,
            Height = 240,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 320,
            Height = 240,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 320,
            Height = 260,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 300,
            Height = 200,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 180,
            Height = 80,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 180,
            Height = 80,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 180,
            Height = 100,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 220,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 200,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 240,
            Height = 140,
            Padding = 0f,
            ParagraphSpacing = 0f,
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 220,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var redIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var blueIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.True(redIndex >= 0);
        Assert.True(blueIndex >= 0);
        Assert.True(blueIndex > redIndex);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsNegativeZIndexBeforeInFlowBackground()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:120px; height:30px; background-color:#00ff00;">
                    <div style="position:absolute; left:0; top:0; width:30px; height:10px; background-color:#ff0000; z-index:-1;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 220,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var greenIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(0, 255, 0)));
        var redIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(255, 0, 0)));

        Assert.True(greenIndex >= 0);
        Assert.True(redIndex >= 0);
        Assert.True(redIndex < greenIndex);
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
        var displayList = renderer.BuildDisplayList(document, new HtmlRenderOptions
        {
            Width = 220,
            Height = 120,
            Padding = 0f,
            ParagraphSpacing = 0f,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var redIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var blueIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(0, 0, 255)));

        Assert.True(redIndex >= 0);
        Assert.True(blueIndex >= 0);
        Assert.True(blueIndex > redIndex);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        return await context.OpenAsync(request => request.Content(html));
    }
}