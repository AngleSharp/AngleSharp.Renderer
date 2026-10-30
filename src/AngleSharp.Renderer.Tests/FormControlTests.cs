namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Image buttons, the date/time input family and <c>accent-color</c>.
/// </summary>
public sealed class FormControlTests
{
    private static readonly RenderColor DefaultAccent = new(26, 115, 232);

    [Fact]
    public async Task ImageInput_PaintsItsImageAtItsNaturalSize()
    {
        var displayList = await LayoutAsync($"<input type=\"image\" alt=\"Go\" src=\"{GeneratedContentTests.PngDataUri(20, 14)}\">");

        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(20f, image.Rect.Width, precision: 2);
        Assert.Equal(14f, image.Rect.Height, precision: 2);
        Assert.Empty(displayList.Commands.OfType<DrawTextCommand>());
    }

    [Fact]
    public async Task ImageInput_TakesTheAuthoredSize()
    {
        var displayList = await LayoutAsync($"<input type=\"image\" style=\"width:40px; height:28px\" src=\"{GeneratedContentTests.PngDataUri(20, 14)}\">");

        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(40f, image.Rect.Width, precision: 2);
        Assert.Equal(28f, image.Rect.Height, precision: 2);
    }

    [Theory]
    [InlineData("alt=\"Go\"", "Go")]
    [InlineData("", "Submit")]
    public async Task ImageInputWithoutImage_ShowsItsAltText(string alt, string expected)
    {
        var displayList = await LayoutAsync($"<input type=\"image\" src=\"missing.png\" {alt}>");

        Assert.Empty(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(expected, Assert.Single(displayList.Commands.OfType<DrawTextCommand>()).Text);
    }

    [Theory]
    [InlineData("<button></button>")]
    [InlineData("<button style=\"width:32px; height:32px\"> </button>")]
    [InlineData("<input type=button>")]
    public async Task EmptyButtons_ShowNoLabel(string markup)
    {
        // found on www.florian-rappl.de: its search button is an icon (a background image)
        var displayList = await LayoutAsync(markup);

        Assert.Empty(displayList.Commands.OfType<DrawTextCommand>());
    }

    [Theory]
    [InlineData("date", "", "mm/dd/yyyy")]
    [InlineData("date", "2024-05-03", "05/03/2024")]
    [InlineData("time", "", "--:-- --")]
    [InlineData("time", "13:05", "01:05 PM")]
    [InlineData("time", "00:30", "12:30 AM")]
    [InlineData("datetime-local", "", "mm/dd/yyyy, --:-- --")]
    [InlineData("datetime-local", "2024-05-03T13:05", "05/03/2024, 01:05 PM")]
    [InlineData("month", "", "--------- ----")]
    [InlineData("month", "2024-05", "May 2024")]
    [InlineData("week", "", "Week --, ----")]
    [InlineData("week", "2024-W18", "Week 18, 2024")]
    [InlineData("date", "not-a-date", "mm/dd/yyyy")]
    public async Task DateAndTimeInputs_ShowTheirValueOrPlaceholder(string type, string value, string expected)
    {
        var displayList = await LayoutAsync($"<input type=\"{type}\" value=\"{value}\">");

        Assert.Equal(expected, Assert.Single(displayList.Commands.OfType<DrawTextCommand>()).Text);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("time")]
    public async Task DateAndTimeInputs_PaintAPickerIconAfterTheText(string type)
    {
        var displayList = await LayoutAsync($"<input type=\"{type}\" style=\"color:#00ff00\">");

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        var icon = displayList.Commands.OfType<StrokeRoundedRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 255, 0))).ToList();
        Assert.NotEmpty(icon);
        Assert.All(icon, part => Assert.True(part.Rect.X > text.X, $"icon at {part.Rect.X} is not after the text at {text.X}"));
    }

    [Fact]
    public async Task DateInput_IsWideEnoughForItsTextAndIcon()
    {
        var displayList = await LayoutAsync("<input type=\"date\" style=\"border:0; padding:0; background:#ff0000\">");

        var box = Assert.Single(displayList.Commands.OfType<FillRectCommand>(), command => command.Color.Equals(new RenderColor(255, 0, 0)));
        var icon = displayList.Commands.OfType<StrokeRoundedRectCommand>().ToList();
        Assert.NotEmpty(icon);
        Assert.True(icon.Max(part => part.Rect.X + part.Rect.Width) <= box.Rect.X + box.Rect.Width + 0.01f);
    }

    [Theory]
    [InlineData("checkbox")]
    [InlineData("radio")]
    public async Task CheckedControls_UseTheDefaultAccentColor(string type)
    {
        var displayList = await LayoutAsync($"<input type=\"{type}\" checked>");

        Assert.Contains(displayList.Commands.OfType<FillRectCommand>(), command => command.Color.Equals(DefaultAccent));
    }

    [Theory]
    [InlineData("checkbox", "checked")]
    [InlineData("radio", "checked")]
    [InlineData("range", "")]
    public async Task AccentColor_ColorsTheControl(string type, string attributes)
    {
        var displayList = await LayoutAsync($"<input type=\"{type}\" {attributes} style=\"accent-color: rgb(200, 0, 100)\">");

        Assert.Contains(displayList.Commands.OfType<FillRectCommand>(), command => command.Color.Equals(new RenderColor(200, 0, 100)));
        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), command => command.Color.Equals(DefaultAccent));
    }

    [Fact]
    public async Task AccentColor_IsInherited()
    {
        var displayList = await LayoutAsync("<form style=\"accent-color: rgb(200, 0, 100)\"><input type=\"checkbox\" checked></form>");

        Assert.Contains(displayList.Commands.OfType<FillRectCommand>(), command => command.Color.Equals(new RenderColor(200, 0, 100)));
    }

    [Fact]
    public async Task AccentColorAuto_UsesTheDefault()
    {
        var displayList = await LayoutAsync("<input type=\"checkbox\" checked style=\"accent-color: auto\">");

        Assert.Contains(displayList.Commands.OfType<FillRectCommand>(), command => command.Color.Equals(DefaultAccent));
    }

    [Theory]
    [InlineData("<input placeholder=\"Name\">")]
    [InlineData("<textarea placeholder=\"Name\"></textarea>")]
    public async Task Placeholder_TakesTheDefaultPlaceholderStyle(string control)
    {
        var displayList = await LayoutAsync($"<div style=\"color:#00ff00\">{control}</div>");

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>(), command => command.Text == "Name");
        Assert.Equal(new RenderColor(169, 169, 169), text.Color);
    }

    [Theory]
    [InlineData("<input placeholder=\"Name\">")]
    [InlineData("<textarea placeholder=\"Name\"></textarea>")]
    public async Task Placeholder_TakesAuthoredPlaceholderRules(string control)
    {
        var displayList = await LayoutAsync($"<style>::placeholder {{ color: rgb(1, 2, 3); font-size: 20px }}</style>{control}");

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>(), command => command.Text == "Name");
        Assert.Equal(new RenderColor(1, 2, 3), text.Color);
        Assert.Equal(20f, text.FontSize, precision: 2);
    }

    [Theory]
    [InlineData("checkbox")]
    [InlineData("radio")]
    public async Task AppearanceNone_PaintsNoNativeCheck(string type)
    {
        // dev.to draws the check mark of `appearance: none` checkboxes as a background image
        var displayList = await LayoutAsync($"<input type=\"{type}\" checked style=\"appearance: none; accent-color: rgb(200, 0, 100)\">");

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), command => command.Color.Equals(new RenderColor(200, 0, 100)));
    }

    private static async Task<DisplayList> LayoutAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var document = await context.OpenAsync(request => request.Content($"<html><body>{body}</body></html>"));
        return new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 300 });
    }
}
