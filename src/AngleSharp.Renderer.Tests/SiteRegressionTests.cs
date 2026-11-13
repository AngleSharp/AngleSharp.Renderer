namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Reduced cases of layout bugs found on real sites, checked through the layout metrics.
/// </summary>
public sealed class SiteRegressionTests
{
    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };

    [Fact]
    public async Task AbsoluteBox_WithExplicitAutoTop_IsPlacedByBottom()
    {
        // github.com tab underline: `inset: auto 0 0` sets `top: auto` explicitly
        var document = await OpenAsync("""
            <div style="position: relative; width: 80px; height: 32px">
              <div id="bar" style="position: absolute; inset: auto 0 0; height: 2px; margin-bottom: -8px"></div>
            </div>
            """);

        var bar = document.GetElementById("bar")!.GetBoundingClientRect();
        var parent = document.GetElementById("bar")!.ParentElement!.GetBoundingClientRect();

        Assert.Equal(parent.Bottom + 8, bar.Bottom, precision: 0);
        Assert.Equal(80, bar.Width, precision: 0);
    }

    [Fact]
    public async Task FormattingContext_AutoHeightIncludesLastChildMargin()
    {
        // github.com menu: `margin-block: -20px; padding-block: 20px` keeps a flex item at the
        // height of its button
        var document = await OpenAsync("""
            <div style="display: flex; align-items: flex-start">
              <div id="negative"><div style="margin: -20px 0; padding: 20px 0; width: 50px"><div style="height: 28px"></div></div></div>
              <div id="positive"><div style="margin-bottom: 10px; width: 50px; height: 20px"></div></div>
            </div>
            <div id="padded" style="padding-bottom: 5px"><div style="margin-bottom: 10px; height: 20px"></div></div>
            """);

        Assert.Equal(28, document.GetElementById("negative")!.GetBoundingClientRect().Height, precision: 0);
        Assert.Equal(30, document.GetElementById("positive")!.GetBoundingClientRect().Height, precision: 0);
        Assert.Equal(35, document.GetElementById("padded")!.GetBoundingClientRect().Height, precision: 0);
    }

    [Fact]
    public async Task OutOfFlowSibling_DoesNotAffectMarginCollapsing()
    {
        // github.com sidebar: `sr-only` headings between the blocks of the sidebar
        const string Hidden = """<h3 style="position: absolute; width: 1px; height: 1px; margin: 0"></h3>""";
        var document = await OpenAsync($$"""
            <div id="first" style="height: 20px; margin-bottom: 12px"></div>
            {{Hidden}}
            <div id="second" style="height: 20px"></div>
            <div id="parent">{{Hidden}}<div id="child" style="height: 20px; margin-top: 8px"></div></div>
            """);

        var first = document.GetElementById("first")!.GetBoundingClientRect();
        var second = document.GetElementById("second")!.GetBoundingClientRect();
        var child = document.GetElementById("child")!.GetBoundingClientRect();

        Assert.Equal(first.Bottom + 12, second.Top, precision: 0);
        // collapses through the parent exactly once
        Assert.Equal(second.Bottom + 8, child.Top, precision: 0);
    }

    [Fact]
    public async Task ColumnFlexItem_AutoMarginsTakeOnlyTheFreeSpace()
    {
        // github.com page layout: a `width: 100%` item with padding and `margin: 0 auto` moved left
        var document = await OpenAsync("""
            <div style="display: flex; flex-direction: column; width: 400px">
              <div id="full" style="box-sizing: border-box; width: 100%; padding: 16px; margin: 0 auto"></div>
              <div id="centered" style="width: 100px; margin: 0 auto"></div>
              <div id="end" style="width: 100px; margin-left: auto"></div>
            </div>
            """);

        var full = document.GetElementById("full")!.GetBoundingClientRect();
        var parent = document.GetElementById("full")!.ParentElement!.GetBoundingClientRect();

        Assert.Equal(parent.Left, full.Left, precision: 0);
        Assert.Equal(400, full.Width, precision: 0);
        Assert.Equal(parent.Left + 150, document.GetElementById("centered")!.GetBoundingClientRect().Left, precision: 0);
        Assert.Equal(parent.Left + 300, document.GetElementById("end")!.GetBoundingClientRect().Left, precision: 0);
    }

    [Theory]
    [InlineData("text-bottom", 17)]
    [InlineData("text-top", 1)]
    public async Task AtomicInline_AlignsWithTheTextEdges(string alignment, double expectedBottom)
    {
        // github.com sidebar icons: 16px icons in lines of 21px keep them from growing the line
        var document = await OpenAsync($"""
            <div id="line" style="font-size: 14px; line-height: 21px"><span id="icon" style="display: inline-block; width: 16px; height: 16px; vertical-align: {alignment}"></span>Readme</div>
            """);

        var line = document.GetElementById("line")!.GetBoundingClientRect();
        var icon = document.GetElementById("icon")!.GetBoundingClientRect();

        Assert.Equal(21, line.Height, precision: 0);

        if (alignment == "text-bottom")
        {
            Assert.InRange(icon.Bottom - line.Top, expectedBottom, expectedBottom + 2);
        }
        else
        {
            Assert.InRange(icon.Top - line.Top, expectedBottom, expectedBottom + 2);
        }
    }

    [Fact]
    public async Task FlexItems_FollowAlignSelfAndStretchTextFields()
    {
        // github.com "Go to file": a stretched input between icons centered with `align-self`
        var document = await OpenAsync("""
            <div id="box" style="display: flex; align-items: stretch; min-height: 32px; width: 300px; line-height: 20px">
              <span id="icon" style="align-self: center">x</span>
              <input id="input" style="border: 0; padding: 0; width: 100%; box-sizing: border-box">
            </div>
            """);

        var box = document.GetElementById("box")!.GetBoundingClientRect();
        var icon = document.GetElementById("icon")!.GetBoundingClientRect();
        var input = document.GetElementById("input")!.GetBoundingClientRect();

        Assert.Equal(32, input.Height, precision: 0);
        Assert.Equal(box.Top, input.Top, precision: 0);
        Assert.Equal(20, icon.Height, precision: 0);
        Assert.Equal(box.Top + 6, icon.Top, precision: 0);
    }

    [Fact]
    public async Task ShrinkToFitBox_PrefersTheDefaultWidthOfATextField()
    {
        // github.com "Go to file": a `width: 100%` input in a box sized by its content
        var document = await OpenAsync("""
            <div style="display: flex"><div id="box"><input style="box-sizing: border-box; border: 0; padding: 0; width: 100%"></div></div>
            """);

        var box = document.GetElementById("box")!.GetBoundingClientRect().Width;
        Assert.InRange(box, 100, 300);
    }

    [Theory]
    [InlineData(13.3333, 15)] // DejaVu Sans: rounded ascent + rounded descent + rounded line gap
    [InlineData(14, 16)]
    [InlineData(16, 19)]
    [InlineData(32, 38)]
    public async Task NormalLineHeight_IsTheLineSpacingOfTheFont(double fontSize, double expected)
    {
        var document = await OpenAsync($"""
            <div id="line" style="font: {fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px 'sans-serif'">Text</div>
            """);

        Assert.Equal(expected, document.GetElementById("line")!.GetBoundingClientRect().Height, precision: 0);
    }

    [Fact]
    public async Task NormalLineHeight_DependsOnTheFontOfEachElement()
    {
        var document = await OpenAsync("""
            <div style="font: 16px sans-serif">
              <div id="same">Text</div>
              <div id="bigger" style="font-size: 32px">Text</div>
              <div id="mono" style="font-family: monospace">Text</div>
            </div>
            <div id="explicit" style="font-size: 16px; line-height: 2"><div id="normal" style="line-height: normal">Text</div><div id="inherited">Text</div></div>
            """);

        double Height(string id) => document.GetElementById(id)!.GetBoundingClientRect().Height;

        Assert.Equal(19, Height("same"), precision: 0);
        Assert.Equal(38, Height("bigger"), precision: 0);
        Assert.Equal(19, Height("mono"), precision: 0);
        // `normal` isn't the line height of the parent
        Assert.Equal(19, Height("normal"), precision: 0);
        Assert.Equal(32, Height("inherited"), precision: 0);
    }

    [Fact]
    public async Task TextArea_IsAsTallAsItsRowsOfNormalLines()
    {
        var document = await OpenAsync("""
            <style>textarea { display: block; font: 16px sans-serif; padding: 0; border: 0 }</style>
            <textarea id="two"></textarea>
            <textarea id="four" rows="4"></textarea>
            """);

        Assert.Equal(38, document.GetElementById("two")!.GetBoundingClientRect().Height, precision: 0);
        Assert.Equal(76, document.GetElementById("four")!.GetBoundingClientRect().Height, precision: 0);
    }

    [Theory]
    [InlineData("<button class=c>x</button>", 16)] // no minimum width
    [InlineData("<input class=c type=button value=x>", 16)]
    [InlineData("<button class=c></button>", 0)]
    public async Task Button_IsAsWideAsItsLabel(string markup, double expectedHeight)
    {
        var document = await OpenAsync($$"""
            <style>.c { display: block; font: 14px sans-serif; padding: 0; border: 0 }</style>
            {{markup}}
            """);

        var rect = document.QuerySelector(".c")!.GetBoundingClientRect();

        Assert.True(rect.Width < 12, $"The width is {rect.Width}.");
        Assert.Equal(expectedHeight, rect.Height, precision: 0);
    }

    [Fact]
    public async Task Select_IsAsWideAsItsWidestOption()
    {
        var document = await OpenAsync("""
            <style>select { display: block; font: 14px sans-serif; padding: 0; border: 0; box-sizing: border-box }</style>
            <select id="first"><option>A long long option</option><option>B</option></select>
            <select id="second"><option selected>B</option><option>A long long option</option></select>
            <select id="group"><optgroup label="G"><option>A long long option</option></optgroup></select>
            <select id="plain" style="appearance: none; line-height: 30px"><option>A long long option</option></select>
            <select id="line" style="line-height: 30px"><option>A long long option</option></select>
            """);

        (double Width, double Height) Rect(string id)
        {
            var rect = document.GetElementById(id)!.GetBoundingClientRect();
            return (rect.Width, rect.Height);
        }

        Assert.Equal(Rect("first").Width, Rect("second").Width, precision: 0);
        Assert.Equal(Rect("first").Width, Rect("group").Width, precision: 0);
        // no arrow, and the line height is honored
        Assert.Equal(Rect("first").Width - 20, Rect("plain").Width, precision: 0);
        Assert.Equal(30, Rect("plain").Height, precision: 0);
        Assert.Equal(Rect("first").Height, Rect("line").Height, precision: 0);
    }

    [Fact]
    public async Task ListBox_ShowsItsRows()
    {
        var document = await OpenAsync("""
            <style>select { display: block; font: 14px/16px sans-serif; padding: 0; border: 0; box-sizing: border-box }</style>
            <select id="multiple" multiple><option>a</option><option>b</option></select>
            <select id="size" size="2"><option>a</option><option>b</option></select>
            <select id="one" size="1"><option>a</option></select>
            """);

        // a row is at least 1.2em and has a pixel of padding
        Assert.Equal(4 * 17.8, document.GetElementById("multiple")!.GetBoundingClientRect().Height, precision: 1);
        Assert.Equal(2 * 17.8, document.GetElementById("size")!.GetBoundingClientRect().Height, precision: 1);
        Assert.True(document.GetElementById("one")!.GetBoundingClientRect().Height < 20);
    }

    [Fact]
    public async Task ListBox_FindsTheRowAtAPoint()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } select { display: block; font: 14px/16px sans-serif; border: 2px solid; padding: 3px; width: 100px }</style>
            <select id="list" size="4"><optgroup label="G"><option id="a">a</option></optgroup><option id="b">b</option></select>
            <select id="drop"><option>a</option></select>
            """);
        var list = document.GetElementById("list")!;
        var rect = list.GetBoundingClientRect();
        double RowY(int row) => rect.Top + 2 + 3 + ((row + 0.5) * 17.8);

        Assert.True(list.IsListBox());
        Assert.False(document.GetElementById("drop")!.IsListBox());
        Assert.Equal("optgroup", list.GetListBoxRowAt(rect.Left + 10, RowY(0))?.LocalName);
        Assert.Equal("a", list.GetListBoxRowAt(rect.Left + 10, RowY(1))?.Id);
        Assert.Equal("b", list.GetListBoxRowAt(rect.Left + 10, RowY(2))?.Id);
        // below the last row, in the border and outside of the box
        Assert.Null(list.GetListBoxRowAt(rect.Left + 10, RowY(3)));
        Assert.Null(list.GetListBoxRowAt(rect.Left + 1, RowY(1)));
        Assert.Null(list.GetListBoxRowAt(rect.Left - 20, RowY(1)));
        Assert.Null(document.GetElementById("drop")!.GetListBoxRowAt(5, RowY(0)));
    }

    [Fact]
    public async Task ListBox_ScrollsToItsSelectedOptionAndCanBeScrolled()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } select { display: block; font: 14px/16px sans-serif; border: 0; padding: 0 }</style>
            <select id="list" size="2"><option id="a">a</option><option>b</option><option id="c" selected>c</option><option id="d">d</option><option id="e">e</option></select>
            """);
        var list = document.GetElementById("list")!;
        var rect = list.GetBoundingClientRect();

        Assert.Equal(17.8, list.GetScrollTop(), tolerance: 0.5);
        Assert.Equal(90, list.GetScrollHeight(), tolerance: 1);
        Assert.Equal("c", list.GetListBoxRowAt(rect.Left + 5, rect.Top + 18 + 5)?.Id);

        list.ScrollListBoxRowIntoView(document.GetElementById("e")!);
        Assert.Equal(3 * 17.8, list.GetScrollTop(), tolerance: 0.5);
        Assert.Equal("e", list.GetListBoxRowAt(rect.Left + 5, rect.Top + 18 + 5)?.Id);

        list.ScrollListBoxRowIntoView(document.GetElementById("a")!);
        Assert.Equal(0, list.GetScrollTop(), tolerance: 0.01);

        // once shown, scrolling back up to the top stays there
        list.SetScrollTop(0);
        _ = list.GetBoundingClientRect();
        Assert.Equal(0, list.GetScrollTop(), tolerance: 0.01);

        list.SetScrollTop(10000);
        Assert.Equal((5 * 17.8) - (2 * 17.8), list.GetScrollTop(), tolerance: 1);
    }

    [Theory]
    [InlineData("<input class=c type=checkbox>", 13, 13)]
    [InlineData("<input class=c type=radio>", 13, 13)]
    [InlineData("<input class=c type=color>", 50, 27)]
    [InlineData("<input class=c type=range>", 129, 16)]
    public async Task Controls_HaveTheirFixedSizeForAnyFont(string markup, double width, double height)
    {
        var document = await OpenAsync($$"""
            <style>body { font-size: 40px } .c { display: block }</style>
            {{markup}}
            """);

        var rect = document.QuerySelector(".c")!.GetBoundingClientRect();

        Assert.Equal(width, rect.Width, precision: 0);
        Assert.Equal(height, rect.Height, precision: 0);
    }

    [Theory]
    [InlineData("progress", 140)]
    [InlineData("meter", 70)]
    public async Task ProgressAndMeter_AreSizedByTheirFontAndHideTheirFallback(string tag, double width)
    {
        var document = await OpenAsync($"""
            <div style="font: 14px sans-serif"><{tag} id="bar" value="0.5">fallback</{tag}></div>
            """);

        var rect = document.GetElementById("bar")!.GetBoundingClientRect();

        Assert.Equal(width, rect.Width, precision: 0);
        Assert.Equal(14, rect.Height, precision: 0);
    }

    [Fact]
    public async Task FileInput_IsSizedFor34Digits()
    {
        var document = await OpenAsync("""
            <input id="file" type="file" style="display: block; font: 14px monospace; padding: 0; border: 0">
            """);

        // a digit of the bundled monospace font is 8.43px wide
        Assert.Equal(287, document.GetElementById("file")!.GetBoundingClientRect().Width, tolerance: 2d);
    }

    [Fact]
    public async Task SelectPopup_GoesBelowTheSelectAndKnowsItsRows()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } select { position: absolute; left: 30px; top: 40px; box-sizing: border-box; width: 100px; height: 20px; font: 14px/16px sans-serif }</style>
            <select id="s"><option id="a">a</option><optgroup id="g" label="A rather long label for a group"><option id="b">b</option></optgroup></select>
            """);
        var select = document.GetElementById("s")!;

        var popup = SelectPopup.Create(select, document.GetElementById("b"), 0)!;

        Assert.Equal(30, popup.Left, precision: 0);
        Assert.Equal(60, popup.Top, precision: 0);
        // as wide as its widest row, at least as wide as the select
        Assert.True(popup.Width > 100, $"The width is {popup.Width}.");
        Assert.Equal((3 * popup.RowHeight) + 2, popup.Height, precision: 1);
        Assert.Equal("a", popup.GetRowAt(40, popup.Top + 1 + (0.5 * popup.RowHeight))?.Id);
        Assert.Equal("g", popup.GetRowAt(40, popup.Top + 1 + (1.5 * popup.RowHeight))?.Id);
        Assert.Equal("b", popup.GetRowAt(40, popup.Top + 1 + (2.5 * popup.RowHeight))?.Id);
        Assert.Null(popup.GetRowAt(40, popup.Top - 5));
        Assert.False(popup.Contains(10, popup.Top + 5));
    }

    [Fact]
    public async Task SelectPopup_OpensAboveWithoutRoomAndScrollsWhenItDoesNotFit()
    {
        var options = string.Concat(Enumerable.Range(0, 100).Select(i => $"<option id='o{i}'>o{i}</option>"));
        var document = await OpenAsync($$"""
            <style>body { margin: 0 } select { position: absolute; left: 0; box-sizing: border-box; width: 100px; height: 20px } #low { top: 570px } #high { top: 0 }</style>
            <select id="low"><option>a</option><option>b</option></select>
            <select id="high">{{options}}</select>
            """);

        var low = SelectPopup.Create(document.GetElementById("low")!, null, 0)!;
        Assert.Equal(570, low.Top + low.Height, precision: 0);

        // at most 20 rows at once
        var high = SelectPopup.Create(document.GetElementById("high")!, null, 0)!;
        Assert.Equal((20 * high.RowHeight) + 2, high.Height, precision: 1);
        Assert.Equal(80 * high.RowHeight, high.MaximumScrollTop, tolerance: 1d);

        var shown = high.GetScrollTopShowing(document.GetElementById("o50")!);
        Assert.Equal(31 * high.RowHeight, shown, tolerance: 1d);
        var scrolled = SelectPopup.Create(document.GetElementById("high")!, null, shown)!;
        Assert.Equal("o31", scrolled.GetRowAt(5, scrolled.Top + 1 + (0.5 * scrolled.RowHeight))?.Id);
        Assert.Equal(scrolled.MaximumScrollTop, SelectPopup.Create(document.GetElementById("high")!, null, 1e6)!.ScrollTop, tolerance: 0.01d);
    }

    private static async Task<(RenderColor Background, RenderColor Text, RenderColor Border, RenderColor HighlightBackground, RenderColor HighlightText)> GetPopupColorsAsync(string head, string selectStyle, string? preference)
    {
        var device = new DefaultRenderDevice { ViewPortWidth = 800, ViewPortHeight = 600, FontSize = 16 };

        if (preference is not null)
        {
            device.Preferences = new Dictionary<string, string> { ["prefers-color-scheme"] = preference };
        }

        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(device));
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head>{{head}}</head><body>
            <select id="s" style="{{selectStyle}}"><option id="a">a</option><option id="b">b</option></select>
            </body></html>
            """));
        context.GetDomHarness();
        var popup = SelectPopup.Create(document.GetElementById("s")!, document.GetElementById("b"), 0)!;
        var fills = popup.Overlay.Commands.OfType<FillRectCommand>().ToList();
        var texts = popup.Overlay.Commands.OfType<DrawTextCommand>().ToList();
        // the frame, the area of the rows (and the color of the author on top), the highlighted row
        return (fills[^2].Color, texts[0].Color, fills[0].Color, fills[^1].Color, texts[1].Color);
    }

    private static readonly RenderColor DarkPopup = new(59, 59, 59);

    [Theory]
    [InlineData("", "", null, false)]
    [InlineData("", "", "light", false)]
    [InlineData("", "", "dark", false)] // a preference alone doesn't change it, the page has to support it
    [InlineData("<style>:root { color-scheme: dark }</style>", "", "light", true)]
    [InlineData("<style>:root { color-scheme: dark }</style>", "", "dark", true)]
    [InlineData("<style>:root { color-scheme: light dark }</style>", "", "dark", true)]
    [InlineData("<style>:root { color-scheme: light dark }</style>", "", "light", false)]
    [InlineData("<style>:root { color-scheme: light dark }</style>", "", null, false)]
    [InlineData("<style>:root { color-scheme: only light }</style>", "", "dark", false)]
    [InlineData("<style>:root { color-scheme: dark }</style>", "color-scheme: light", "dark", false)]
    [InlineData("", "color-scheme: dark", "light", true)]
    [InlineData("<meta name='color-scheme' content='dark'>", "", "light", true)]
    [InlineData("<meta name='color-scheme' content='light dark'>", "", "dark", true)]
    [InlineData("<meta name='color-scheme' content='light dark'>", "", "light", false)]
    [InlineData("<meta name='color-scheme' content='light'>", "", "dark", false)]
    public async Task SelectPopup_FollowsTheColorSchemeOfTheSelect(string head, string selectStyle, string? preference, bool isDark)
    {
        var (background, text, border, highlight, highlightText) = await GetPopupColorsAsync(head, selectStyle, preference);

        Assert.Equal(isDark ? DarkPopup : RenderColor.White, background);
        Assert.Equal(isDark ? RenderColor.White : RenderColor.Black, text);
        Assert.Equal(isDark ? new RenderColor(133, 133, 133) : new RenderColor(118, 118, 118), border);
        Assert.Equal(isDark ? new RenderColor(153, 200, 255) : new RenderColor(0, 117, 255), highlight);
        Assert.Equal(isDark ? RenderColor.Black : RenderColor.White, highlightText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<style>:root { color-scheme: dark }</style>")]
    public async Task SelectPopup_ShowsTheColorsOfTheSelectInAnyScheme(string head)
    {
        var (background, text, _, _, _) = await GetPopupColorsAsync(head, "background-color: rgb(10, 20, 30); color: rgb(200, 210, 220)", null);

        Assert.Equal(new RenderColor(10, 20, 30), background);
        Assert.Equal(new RenderColor(200, 210, 220), text);
    }

    [Fact]
    public async Task SelectPopup_ShowsTheColorsOfItsOptions()
    {
        var document = await OpenAsync("""
            <style>:root { color-scheme: dark }</style>
            <select id="s" style="color: rgb(0, 0, 0)">
              <option id="plain">a</option>
              <option id="own" style="color: rgb(1, 2, 3); background-color: rgb(4, 5, 6)">b</option>
              <option id="off" disabled style="color: rgb(1, 2, 3)">c</option>
              <option id="on" style="color: rgb(1, 2, 3); background-color: rgb(4, 5, 6)">d</option>
            </select>
            """);

        var popup = SelectPopup.Create(document.GetElementById("s")!, document.GetElementById("on"), 0)!;
        var texts = popup.Overlay.Commands.OfType<DrawTextCommand>().ToList();
        var fills = popup.Overlay.Commands.OfType<FillRectCommand>().Select(fill => fill.Color).ToList();

        Assert.Equal(RenderColor.White, texts[0].Color);
        Assert.Equal(new RenderColor(1, 2, 3), texts[1].Color);
        Assert.Contains(new RenderColor(4, 5, 6), fills);
        // what is disabled or highlighted shows that, whatever its colors are
        Assert.Equal(new RenderColor(170, 170, 170), texts[2].Color);
        Assert.Equal(RenderColor.Black, texts[3].Color);
        Assert.Equal(new RenderColor(153, 200, 255), fills[^1]);
    }

    [Fact]
    public async Task RenderedFrame_ShowsAnOverlayOnTopWithoutChangingTheFrame()
    {
        var document = await OpenAsync("""<body style="margin: 0; background: rgb(0, 0, 255)"></body>""");
        var frame = document.Context.GetDomHarness().PrepareFrame(50);
        var overlay = new AngleSharp.Renderer.Rendering.DisplayList();
        overlay.FillRect(new AngleSharp.Renderer.Rendering.RenderRect(10f, 20f, 5f, 5f), new AngleSharp.Renderer.Rendering.RenderColor(255, 0, 0));

        var pixels = frame.WithOverlay(overlay).Rasterize();
        var plain = frame.Rasterize();
        int Red(AngleSharp.Renderer.Rendering.RenderedPixels image, int x, int y) => image.Data[(((y + frame.OverscanTop) * image.Width) + x) * 4];

        Assert.Equal(255, Red(pixels, 12, 22));
        Assert.Equal(0, Red(pixels, 12, 30));
        Assert.Equal(0, Red(plain, 12, 22));
    }

    [Theory]
    [InlineData("<input class=c type=checkbox>", 13d)]
    [InlineData("<input class=c type=color>", 21d)]
    [InlineData("<input class=c type=range>", 18d)] // a range is 16px tall inside of its border
    [InlineData("<select class=c multiple><option>a</option></select>", null)]
    public async Task ControlsWithoutText_DefineTheirBaseline(string markup, double? expected)
    {
        var document = await OpenAsync($$"""
            <style>.c { margin: 0; box-sizing: border-box; border: 1px solid; padding: 0 }</style>
            <div style="font: 14px sans-serif"><span id="mark" style="display: inline-block; width: 1px; height: 0"></span>{{markup}}</div>
            """);

        var mark = document.GetElementById("mark")!.GetBoundingClientRect();
        var control = document.QuerySelector(".c")!.GetBoundingClientRect();

        Assert.Equal(expected ?? control.Height - 3, mark.Bottom - control.Top, precision: 0);
    }

    [Theory]
    [InlineData("-2px", 2)]
    [InlineData("5px", -5)]
    [InlineData("-0.5em", 8)]
    public async Task AtomicInline_FollowsAVerticalAlignLength(string value, double lowered)
    {
        var document = await OpenAsync($"""
            <div style="font: 16px sans-serif; line-height: 40px">
              <span id="base" style="display: inline-block; width: 10px; height: 10px"></span><span id="moved" style="display: inline-block; width: 10px; height: 10px; vertical-align: {value}"></span>
            </div>
            """);

        var difference = document.GetElementById("moved")!.GetBoundingClientRect().Top - document.GetElementById("base")!.GetBoundingClientRect().Top;
        Assert.Equal(lowered, difference, precision: 0);
    }

    [Fact]
    public async Task CaretPosition_FollowsLinesAndFontMetrics()
    {
        var document = await OpenAsync("""
            <div id="tall" style="font: 16px monospace; line-height: 40px">Hello</div>
            <div id="wrapped" style="font: 16px monospace; width: 60px">Hello world</div>
            <div id="spaced" style="font: 16px monospace">   spaced</div>
            """);

        var tall = document.GetElementById("tall")!.GetBoundingClientRect();
        var caret = document.CaretPositionFromPoint(tall.Left + 22, tall.Top + 20);
        var rect = caret!.GetClientRect();
        // as tall as the font and centered in the line, not 16 times as tall as the line
        Assert.Equal(19, rect!.Height, precision: 0);
        Assert.Equal(tall.Top + 10, rect.Top, tolerance: 1d);
        Assert.Equal(2, caret.Offset);

        var wrapped = document.GetElementById("wrapped")!.GetBoundingClientRect();
        caret = document.CaretPositionFromPoint(wrapped.Left + 22, wrapped.Top + 30);
        Assert.Equal(8, caret!.Offset); // "wo" on the second line
        Assert.Equal(wrapped.Top + 19, caret.GetClientRect()!.Top, tolerance: 1d);

        var spaced = document.GetElementById("spaced")!.GetBoundingClientRect();
        caret = document.CaretPositionFromPoint(spaced.Left + 22, spaced.Top + 5);
        Assert.Equal(5, caret!.Offset); // the collapsed spaces still count in the offset of the node
    }

    [Fact]
    public async Task TextArea_ReservesRoomForItsScrollbar()
    {
        var document = await OpenAsync("""
            <style>textarea { display: block; font: 16px monospace; padding: 0; border: 0 }</style>
            <textarea id="plain"></textarea>
            <textarea id="hidden" style="overflow-y: hidden"></textarea>
            <textarea id="none" style="scrollbar-width: none"></textarea>
            <textarea id="thin" style="scrollbar-width: thin"></textarea>
            <textarea id="horizontal" style="overflow-x: hidden"></textarea>
            <textarea id="scroll" style="overflow: scroll"></textarea>
            """);

        double Width(string id) => document.GetElementById(id)!.GetBoundingClientRect().Width;

        var content = Width("hidden");
        Assert.Equal(content, Width("none"), precision: 0);
        Assert.Equal(content + 15, Width("plain"), precision: 0);
        Assert.Equal(content + 10, Width("thin"), precision: 0);
        Assert.Equal(content + 15, Width("horizontal"), precision: 0);
        Assert.Equal(content + 15, Width("scroll"), precision: 0);
    }

    [Fact]
    public async Task TextField_IsSizedByItsFontAndSizeAttribute()
    {
        // 20 average characters unless `size` says otherwise, in the font of the control
        var document = await OpenAsync("""
            <style>input { font: 16px monospace; padding: 0; border: 0 }</style>
            <input id="default"><input id="short" size="10"><input id="long" size="40"><input id="big" style="font-size: 32px">
            <textarea id="area" cols="40" rows="3" style="font: 16px/20px monospace; padding: 0; border: 0"></textarea>
            """);

        double Width(string id) => document.GetElementById(id)!.GetBoundingClientRect().Width;

        // one average character is the slope, whatever is added to that
        Assert.Equal((Width("long") - Width("short")) / 30d, (Width("long") - Width("default")) / 20d, precision: 0);
        Assert.InRange(Width("default"), 190, 215);
        Assert.Equal(2 * Width("default"), Width("big"), tolerance: 2d);
        Assert.Equal(60, document.GetElementById("area")!.GetBoundingClientRect().Height, precision: 0);
    }

    [Fact]
    public async Task Grid_AutoRowsFillTheMinHeight()
    {
        // github.com search button: its label is centered in a grid that only has a min-height
        var document = await OpenAsync("""
            <div style="display: grid; align-items: center; min-height: 36px; width: 100px">
              <span id="item" style="display: block; height: 20px"></span>
            </div>
            """);

        var item = document.GetElementById("item")!;
        Assert.Equal(item.ParentElement!.GetBoundingClientRect().Top + 8, item.GetBoundingClientRect().Top, precision: 0);
    }

    [Theory]
    [InlineData("12.25em minmax(0, 1fr)", 196, 604)]
    [InlineData("2rem 1fr", 32, 768)]
    [InlineData("10vw 1fr", 80, 720)]
    [InlineData("calc(1em + 20px) 1fr", 36, 764)]
    [InlineData("calc(10% + 20px) 1fr", 100, 700)]
    [InlineData("minmax(5em, 10em) 1fr", 160, 640)]
    [InlineData("minmax(10em, 1fr) 1fr", 400, 400)]
    [InlineData("minmax(600px, 1fr) 1fr", 600, 200)]
    public async Task GridTrack_ResolvesItsUnitsAndFloors(string columns, double first, double second)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0; font-size: 16px }</style>
            <div style="display: grid; width: 800px; grid-template-columns: {{columns}}"><div id="a"></div><div id="b"></div></div>
            """);

        Assert.Equal(first, RectOf(document, "a").Width, precision: 1);
        Assert.Equal(second, RectOf(document, "b").Width, precision: 1);
    }

    [Theory]
    [InlineData("column-gap: 24px", 124, 10)]
    [InlineData("row-gap: 24px", 100, 34)]
    [InlineData("gap: 8px 24px", 124, 18)]
    [InlineData("column-gap: calc(5% + 2px)", 142, 10)]
    public async Task GridGap_OfOneAxisLeavesTheOtherAlone(string gap, double secondColumn, double secondRow)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0 } div div { height: 10px }</style>
            <div style="display: grid; width: 800px; grid-template-columns: 100px 100px; {{gap}}"><div id="a"></div><div id="b"></div><div id="c"></div></div>
            """);

        Assert.Equal(secondColumn, RectOf(document, "b").Left, precision: 1);
        Assert.Equal(secondRow, RectOf(document, "c").Top, precision: 1);
    }

    [Theory]
    [InlineData("<table id=\"f\" style=\"float: right; width: 200px\"><tr><td>x</td></tr></table>")]
    [InlineData("<div id=\"f\" style=\"display: table; float: right; width: 200px; height: 30px\"></div>")]
    [InlineData("<div id=\"f\" style=\"display: flex; float: right; width: 200px; height: 30px\"></div>")]
    [InlineData("<div id=\"f\" style=\"display: grid; float: right; width: 200px; height: 30px\"></div>")]
    public async Task FloatedContainer_TakesNoRoomInTheFlow(string floated)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0; font: 14px/20px sans-serif }</style>
            <div style="display: flow-root; width: 600px">{{floated}}<div id="after" style="margin-top: 8px"><span id="text">text</span></div></div>
            """);

        Assert.Equal(400, RectOf(document, "f").Left, precision: 0);
        Assert.Equal(0, RectOf(document, "f").Top, precision: 0);
        Assert.Equal(8, RectOf(document, "after").Top, precision: 0);
    }

    [Fact]
    public async Task FloatedTable_HasLinesWrapAroundIt()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0; font: 14px/20px sans-serif }</style>
            <div style="width: 300px"><table style="float: right; width: 200px"><tr><td style="height: 50px">x</td></tr></table>
            <span id="text">words that fill up more than the narrow band that the table leaves free next to it</span></div>
            """);

        Assert.True(RectOf(document, "text").Height > 40);
        Assert.True(RectOf(document, "text").Width <= 300);
    }

    [Theory]
    [InlineData("<div id=\"f\" style=\"float: right; width: 9px; height: 9px\"></div>", 16)]
    [InlineData("<p style=\"margin: 0 0 30px\">x</p><div id=\"f\" style=\"float: right; width: 9px; height: 9px\"></div>", 50)]
    public async Task Float_LeavesTheMarginsAroundItToCollapse(string before, double top)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0; font: 14px/20px sans-serif }</style>
            <div style="display: flow-root">{{before}}<p id="after" style="margin: 16px 0">x</p></div>
            """);

        Assert.Equal(top, RectOf(document, "after").Top, precision: 0);
    }

    [Theory]
    // the border edge is at the bottom of the float
    [InlineData("clear: both; margin-top: 20px; height: 10px", 50)]
    // its margin takes it there anyway
    [InlineData("clear: both; margin-top: 70px; height: 10px", 70)]
    public async Task Clear_PutsTheBorderEdgeBelowTheFloat(string style, double top)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0 }</style>
            <div style="display: flow-root"><div style="float: left; width: 50px; height: 50px"></div><div id="c" style="{{style}}"></div></div>
            """);

        Assert.Equal(top, RectOf(document, "c").Top, precision: 0);
    }

    [Fact]
    public async Task Clear_OfAFloatLeavesTheFlowWhereItIs()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0; font: 14px/20px sans-serif }</style>
            <div style="display: flow-root"><div style="float: right; width: 100px; height: 20px"></div>
            <div id="f" style="float: right; clear: right; width: 50px; height: 20px"></div><span id="text">after</span></div>
            """);

        Assert.Equal(20, RectOf(document, "f").Top, precision: 0);
        Assert.True(RectOf(document, "text").Top < 10);
    }

    [Theory]
    [InlineData("vertical-align: 5px", -5)]
    [InlineData("vertical-align: -5px", 5)]
    [InlineData("vertical-align: 50%", -15)]
    [InlineData("vertical-align: super", -6.33)]
    [InlineData("vertical-align: sub", 4.2)]
    [InlineData("position: relative; top: 7px", 7)]
    public async Task InlineBox_IsShiftedFromTheBaselineOfItsParent(string style, double shift)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0; font: 16px/30px sans-serif }</style>
            <div style="height: 200px"><span id="base">abc</span><span id="s" style="{{style}}">def</span></div>
            """);

        Assert.InRange(RectOf(document, "s").Top - RectOf(document, "base").Top, shift - 0.5, shift + 0.5);
    }

    [Fact]
    public async Task InlineBox_ShiftsWhatItHoldsAlongWithIt()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0; font: 16px/30px sans-serif }</style>
            <div><span id="base">abc</span><sup style="vertical-align: 5px; font-size: inherit"><a id="s">def</a></sup></div>
            """);

        Assert.Equal(RectOf(document, "base").Top - 5, RectOf(document, "s").Top, precision: 1);
    }

    [Fact]
    public async Task Line_GrowsForWhatIsShiftedOutOfIt()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0; font: 16px/30px sans-serif }</style>
            <div id="line">abc<span style="vertical-align: 20px">def</span></div>
            """);

        Assert.Equal(50, RectOf(document, "line").Height, precision: 0);
    }

    [Fact]
    public async Task InlineBox_WithASmallLineHeightOverflowsItsLine()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0; font: 30px/10px sans-serif }</style>
            <div id="line" style="margin-top: 50px"><span id="s">abc</span></div>
            """);

        Assert.True(RectOf(document, "s").Top < RectOf(document, "line").Top);
    }

    [Theory]
    // the bottom edge of an item without any text is the baseline, and the line holds what is below it
    [InlineData("display: inline-flex; height: 32px; width: 32px; align-items: center", 32)]
    [InlineData("display: inline-flex; height: 32px; width: 32px; align-items: flex-end", 37)]
    public async Task InlineFlex_HasTheBaselineOfItsFirstItem(string style, double lineHeight)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0; font: 16px/20px sans-serif }</style>
            <div id="line"><span style="{{style}}"><span style="display: block; width: 10px; height: 10px"></span></span>x</div>
            """);

        Assert.Equal(lineHeight, RectOf(document, "line").Height, precision: 0);
    }

    [Fact]
    public async Task TableRowsAndGroups_ReportTheirBoxes()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } td { height: 20px; padding: 0 } table { border-spacing: 0 }</style>
            <table><thead id="head"><tr id="first"><td style="width: 50px"></td></tr></thead><tbody id="body"><tr id="second"><td></td></tr></tbody></table>
            """);

        Assert.Equal((0d, 0d, 50d, 20d), RectOf(document, "first"));
        Assert.Equal((0d, 20d, 50d, 20d), RectOf(document, "second"));
        Assert.Equal((0d, 0d, 50d, 20d), RectOf(document, "head"));
        Assert.Equal((0d, 20d, 50d, 20d), RectOf(document, "body"));
    }

    [Fact]
    public async Task DataUrlWithQuotes_LoadsAsABackground()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } div { width: 20px; height: 20px; background-image: url('data:image/svg+xml;utf8,<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><rect width="20" height="20" fill="%23ff0000"/></svg>') }</style>
            <div></div>
            """);

        var image = new HtmlRenderer().RenderToPng(document, Device);
        using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data.ToArray());
        var pixel = bitmap.GetPixel(10, 10);

        Assert.Equal((byte)255, pixel.Red);
        Assert.Equal((byte)0, pixel.Green);
    }

    [Fact]
    public async Task QuirksMode_OnlyTextGivesALineItsHeight()
    {
        var document = await ParseDocumentAsync("""
            <html><body style="margin: 0; font: 16px/20px sans-serif"><div id="image" style="line-height: 50px"><img width="20" height="20" style="display: inline-block"></div>
            <div id="small" style="line-height: 50px"><span style="line-height: 10px">x</span></div>
            <div id="break"><span></span><br></div></body></html>
            """);

        Assert.Equal(20, RectOf(document, "image").Height, precision: 0);
        Assert.Equal(10, RectOf(document, "small").Height, precision: 0);
        Assert.Equal(20, RectOf(document, "break").Height, precision: 0);
    }

    [Fact]
    public async Task QuirksMode_PercentageHeightsLookPastAutoHeights()
    {
        var document = await ParseDocumentAsync("""
            <html><body style="margin: 0"><div><div id="box" style="height: 50%"></div></div>
            <div style="height: 100px"><div><div id="inner" style="height: 50%"></div></div></div>
            <table id="table" style="height: 100%"><tr><td></td></tr></table></body></html>
            """);

        Assert.Equal(Device.ViewPortHeight / 2, RectOf(document, "box").Height, precision: 0);
        Assert.Equal(50, RectOf(document, "inner").Height, precision: 0);
        Assert.Equal(Device.ViewPortHeight, RectOf(document, "table").Height, precision: 0);
    }

    [Fact]
    public async Task QuirksMode_TheHeightOfACellIncludesItsPadding()
    {
        var quirky = await ParseDocumentAsync("""<html><body><table id="t" style="border-spacing: 0"><tr><td style="height: 60px; padding: 5px"></td></tr></table></body></html>""");
        var standard = await ParseDocumentAsync("""<!doctype html><html><body><table id="t" style="border-spacing: 0"><tr><td style="height: 60px; padding: 5px"></td></tr></table></body></html>""");

        Assert.Equal(60, RectOf(quirky, "t").Height, precision: 0);
        Assert.Equal(70, RectOf(standard, "t").Height, precision: 0);
    }

    [Theory]
    [InlineData("top", 0)]
    [InlineData("middle", 20)]
    [InlineData("bottom", 40)]
    public async Task TableCell_IsAlignedByWhatItHoldsWhateverItsHeight(string align, double top)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0 } table { border-spacing: 0; font: 16px/20px sans-serif } td { padding: 0 }</style>
            <table><tr><td style="height: 60px; vertical-align: {{align}}"><div id="content">x</div></td></tr></table>
            """);

        Assert.Equal(top, RectOf(document, "content").Top, precision: 0);
    }

    [Fact]
    public async Task TableWidth_WinsOverWhatACellAsksFor()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <table id="t" style="width: 100px"><tr><td style="width: 300px">x</td></tr></table>
            """);

        Assert.Equal(100, RectOf(document, "t").Width, precision: 0);
    }

    [Fact]
    public async Task TableWidth_LeftOverGoesToTheColumnsNotAskingForAWidth()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } table { border-spacing: 0 } td { padding: 0 }</style>
            <table style="width: 500px"><tr><td id="a" style="width: 50px">x</td><td id="b">y</td></tr></table>
            """);

        Assert.Equal(50, RectOf(document, "a").Width, precision: 0);
        Assert.Equal(450, RectOf(document, "b").Width, precision: 0);
    }

    [Fact]
    public async Task BlockInInline_KeepsItsBottomMargin()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div id="outer" style="display: flow-root"><a><div style="height: 10px; margin: 3px 0 6px"></div></a></div>
            """);

        Assert.Equal(19, RectOf(document, "outer").Height, precision: 0);
    }

    [Theory]
    [InlineData("transform: translate(10px, 20px)", 10, 20, 50, 50)]
    [InlineData("translate: 10px 20px", 10, 20, 50, 50)]
    [InlineData("transform: scale(2); transform-origin: 0 0", 0, 0, 100, 100)]
    public async Task BoundingRect_IsWhereTheTransformPaintsIt(string style, double left, double top, double width, double height)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0 }</style>
            <div id="box" style="width: 50px; height: 50px; {{style}}"></div>
            """);

        Assert.Equal((left, top, width, height), RectOf(document, "box"));
    }

    [Fact]
    public async Task BoundingRect_HoldsTheTransformsAround()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div style="transform: translateX(30px)"><div style="transform: translateY(10px)"><div id="box" style="width: 50px; height: 50px"></div></div></div>
            """);

        Assert.Equal((30d, 10d, 50d, 50d), RectOf(document, "box"));
    }

    [Theory]
    [InlineData("right: 10px; top: 5px", 690, 5)]
    [InlineData("left: 10px; bottom: 5px", 10, 400 - 5 - 26)]
    public async Task PositionedTable_IsPlacedLikeAnyPositionedBox(string style, double left, double top)
    {
        var document = await ParseDocumentAsync($$"""
            <!doctype html><html><body style="margin: 0"><div style="position: relative; width: 800px; height: 400px">
            <table id="t" style="position: absolute; width: 100px; height: 26px; {{style}}"><tr><td>x</td></tr></table></div></body></html>
            """);

        Assert.Equal(left, RectOf(document, "t").Left, precision: 0);
        Assert.Equal(top, RectOf(document, "t").Top, precision: 0);
    }

    [Fact]
    public async Task PositionedTable_TakesNoRoomInTheFlow()
    {
        var document = await ParseDocumentAsync("""
            <!doctype html><html><body style="margin: 0"><div style="position: relative"><table style="position: absolute; height: 50px"><tr><td>x</td></tr></table><div id="after" style="height: 5px"></div></div></body></html>
            """);

        Assert.Equal(0, RectOf(document, "after").Top, precision: 0);
    }

    [Theory]
    [InlineData("", 300, 100)]
    [InlineData("min-width: 0", 200, 200)]
    [InlineData("overflow: hidden", 200, 200)]
    public async Task FractionTrack_IsNotNarrowerThanItsContent(string style, double first, double second)
    {
        var document = await ParseDocumentAsync($$"""
            <!doctype html><html><body style="margin: 0"><div style="display: grid; grid-template-columns: 1fr 1fr; width: 400px">
            <div id="a" style="{{style}}"><div style="width: 300px; height: 5px"></div></div><div id="b">y</div></div></body></html>
            """);

        Assert.Equal(first, RectOf(document, "a").Width, precision: 0);
        Assert.Equal(second, RectOf(document, "b").Width, precision: 0);
    }

    [Fact]
    public async Task Grid_KeepsItsWidthWhenItsTracksOverflow()
    {
        var document = await ParseDocumentAsync("""
            <!doctype html><html><body style="margin: 0"><div id="grid" style="display: grid; grid-template-columns: 500px; width: 300px"><div></div></div></body></html>
            """);

        Assert.Equal(300, RectOf(document, "grid").Width, precision: 0);
    }

    [Theory]
    [InlineData("width: min-content", 10)]
    [InlineData("width: max-content", 20)]
    [InlineData("width: fit-content", 20)]
    public async Task Width_TakesIntrinsicKeywords(string style, double width)
    {
        var document = await ParseDocumentAsync($$"""
            <!doctype html><html><body style="margin: 0; font-size: 0"><style>span { display: inline-block; width: 10px; height: 5px }</style>
            <div id="block" style="{{style}}"><span></span> <span></span></div>
            <div id="inline" style="display: inline-block; {{style}}"><span></span> <span></span></div></body></html>
            """);

        Assert.Equal(width, RectOf(document, "block").Width, 0);
        Assert.Equal(width, RectOf(document, "inline").Width, 0);
    }

    [Fact]
    public async Task ShrinkToFit_CountsAFloatNextToItsText()
    {
        var document = await ParseDocumentAsync("""
            <!doctype html><html><body style="margin: 0"><div id="box" style="float: left"><div style="float: left; width: 100px; height: 20px"></div><span id="text">abc</span></div></body></html>
            """);

        Assert.Equal(100 + RectOf(document, "text").Width, RectOf(document, "box").Width, precision: 0);
    }

    [Theory]
    [InlineData("top")]
    [InlineData("bottom")]
    public async Task TableBox_HoldsItsCaption(string side)
    {
        var document = await ParseDocumentAsync($$"""
            <!doctype html><html><body style="margin: 0"><table id="t" style="width: 100px; border-spacing: 0"><caption id="c" style="caption-side: {{side}}; height: 20px"></caption><tr><td style="height: 30px; padding: 0"></td></tr></table></body></html>
            """);

        Assert.Equal(50, RectOf(document, "t").Height, precision: 0);
    }

    [Fact]
    public async Task BrokenImage_ShowsItsAlternativeText()
    {
        var document = await ParseDocumentAsync("""
            <!doctype html><html><body style="margin: 0; font: 16px/20px sans-serif"><img id="broken" alt="alt text" width="5" height="5"><span id="after">z</span><img id="empty" alt="" width="0"></body></html>
            """);

        Assert.True(RectOf(document, "broken").Width > 16);
        Assert.Equal(RectOf(document, "broken").Width, RectOf(document, "after").Left, precision: 0);
    }

    [Fact]
    public async Task Iframe_IsAnInlineBoxOfADefaultSize()
    {
        var document = await ParseDocumentAsync("""
            <!doctype html><html><body style="margin: 0; font: 16px/20px sans-serif"><span id="before">a</span><iframe id="f" style="border: 0"></iframe><span id="after">b</span></body></html>
            """);

        Assert.Equal(300, RectOf(document, "f").Width, precision: 0);
        Assert.Equal(150, RectOf(document, "f").Height, precision: 0);
        Assert.Equal(RectOf(document, "before").Width + 300, RectOf(document, "after").Left, precision: 0);
    }

    [Fact]
    public async Task Iframe_TakesItsSizeFromStyles()
    {
        var document = await ParseDocumentAsync("""
            <!doctype html><html><body style="margin: 0"><div style="width: 400px"><iframe id="f" style="display: block; border: 0; width: 50%; height: 40px"></iframe></div></body></html>
            """);

        Assert.Equal((0d, 0d, 200d, 40d), RectOf(document, "f"));
    }

    private static (double Left, double Top, double Width, double Height) RectOf(IDocument document, string id)
    {
        var rect = document.GetElementById(id)!.GetBoundingClientRect();
        return (Math.Round(rect.Left), Math.Round(rect.Top), Math.Round(rect.Width), Math.Round(rect.Height));
    }

    [Theory]
    [InlineData("left: 20px; top: 10px; width: 30px; height: 10px", 20, 10, 30, 10)]
    [InlineData("right: 20px; bottom: 10px; width: 30px; height: 10px", 750, 580, 30, 10)]
    [InlineData("left: 20px; right: 30px; top: 0; height: 10px", 20, 0, 750, 10)]
    [InlineData("top: 20px; bottom: 30px; left: 0; width: 10px", 0, 20, 10, 550)]
    [InlineData("left: 10%; top: 10%; width: 50%; height: 25%", 80, 60, 400, 150)]
    [InlineData("left: 0; right: 0; top: 0; width: 100px; margin: 0 auto; height: 5px", 350, 0, 100, 5)]
    [InlineData("top: 0; bottom: 0; left: 0; height: 100px; margin: auto 0; width: 5px", 0, 250, 5, 100)]
    public async Task AbsoluteBox_WithoutPositionedAncestor_UsesTheViewport(string style, double left, double top, double width, double height)
    {
        // its parents are no containing blocks, however deep it is
        var document = await OpenAsync($$"""
            <style>body { margin: 10px } div { box-sizing: border-box }</style>
            <div style="margin: 50px; height: 1px"><div style="margin: 7px; height: 1px">
              <div id="box" style="position: absolute; {{style}}"></div>
            </div></div>
            """);

        Assert.Equal((left, top, width, height), RectOf(document, "box"));
    }

    [Theory]
    [InlineData("position: relative")]
    [InlineData("position: absolute; left: 20px; top: 20px")]
    [InlineData("position: fixed; left: 20px; top: 20px")]
    [InlineData("position: relative; display: flex")]
    [InlineData("position: relative; display: grid")]
    [InlineData("transform: translate(0, 0)")]
    public async Task AbsoluteBox_IsPlacedAgainstTheNearestPositionedAncestor(string style)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0 }</style>
            <div id="block" style="{{style}}; width: 200px; height: 100px; border: 3px solid; padding: 4px">
              <div style="margin: 11px"><div style="padding: 13px">
                <div id="box" style="position: absolute; right: 5px; bottom: 6px; width: 50%; height: 50%"></div>
              </div></div>
            </div>
            """);
        var block = RectOf(document, "block");

        // of the padding box
        Assert.Equal((block.Left + 3 + 208 - 5 - 104, block.Top + 3 + 108 - 6 - 54, 104d, 54d), RectOf(document, "box"));
    }

    [Theory]
    [InlineData("position: relative", "")]
    [InlineData("position: relative; display: flex", "")]
    [InlineData("position: relative; display: flex; flex-direction: column", "")]
    [InlineData("position: relative; display: grid", "")]
    public async Task AbsoluteBox_WaitsForTheAutoHeightOfItsContainingBlock(string style, string itemStyle)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0 }</style>
            <div id="block" style="{{style}}; width: 200px">
              <div style="{{itemStyle}}"><div>
                <div id="box" style="position: absolute; left: 0; bottom: 0; width: 10px; height: 50%; background-color: rgb(1, 2, 3)"></div>
              </div></div>
              <div style="height: 80px"></div>
            </div>
            """);
        var block = RectOf(document, "block");

        Assert.Equal(80d, block.Height);
        Assert.Equal((block.Left, block.Top + 40, 10d, 40d), RectOf(document, "box"));

        // held back once, however many times what is around it is laid out
        var list = new HtmlRenderer().BuildDisplayList(document, Device);
        Assert.Single(list.Commands.OfType<FillRectCommand>().Where(fill => fill.Color.Equals(new RenderColor(1, 2, 3))));
    }

    [Theory]
    [InlineData("<div style='margin: 50px 0'></div>", 50)] // collapses with itself
    [InlineData("<div style='margin: 50px 0 20px'></div>", 50)]
    [InlineData("<div style='margin: 30px 0'></div><div style='margin: 40px 0'></div>", 40)]
    [InlineData("<div style='margin: 50px 0'><div style='margin: 60px 0'></div></div>", 60)]
    [InlineData("<div style='margin: -20px 0'></div>", -20)]
    [InlineData("<div style='margin: 30px 0 -10px'></div>", 20)]
    [InlineData("<div style='margin: 50px 0'>   </div>", 50)]
    [InlineData("<div style='margin: 50px 0; height: 0'></div>", 50)]
    [InlineData("<div style='margin: 50px 0'><span></span></div>", 50)]
    [InlineData("<div style='margin: 50px 0'><div style='position: absolute; width: 5px; height: 5px'></div></div>", 50)]
    [InlineData("<div style='margin: 50px 0'><div style='float: left; width: 5px; height: 5px'></div></div>", 50)]
    [InlineData("<div style='margin: 50px 0; padding-bottom: 1px'></div>", 101)] // these separate them
    [InlineData("<div style='margin: 50px 0; border-top: 1px solid'></div>", 101)]
    [InlineData("<div style='margin: 50px 0; min-height: 5px'></div>", 105)]
    [InlineData("<div style='margin: 50px 0; display: flow-root'></div>", 100)]
    public async Task EmptyBlock_LetsItsMarginsCollapseThroughIt(string markup, double gap)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0; font: 14px sans-serif }</style>
            <div style="display: flow-root"><div id="before" style="height: 10px"></div>{{markup}}<div id="after" style="height: 10px"></div></div>
            """);

        var before = document.GetElementById("before")!.GetBoundingClientRect();
        var after = document.GetElementById("after")!.GetBoundingClientRect();

        Assert.Equal(gap, after.Top - before.Bottom, precision: 0);
    }

    [Fact]
    public async Task EmptyFirstChild_CollapsesWithItsParentAndWhatFollows()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div id="root" style="display: flow-root; height: 200px">
              <div id="parent" style="margin: 5px 0"><div style="margin: 30px 0"></div><div id="child" style="height: 10px; margin-top: 40px"></div></div>
            </div>
            """);

        Assert.Equal(40d, RectOf(document, "parent").Top);
        Assert.Equal(40d, RectOf(document, "child").Top);
    }

    [Fact]
    public async Task EmptyLastChild_CollapsesWithItsParentsBottomMargin()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div style="display: flow-root">
              <div id="parent"><div style="height: 10px; margin-bottom: 40px"></div><div style="margin: 30px 0"></div></div>
              <div id="next" style="height: 10px"></div>
            </div>
            """);

        Assert.Equal(10d, RectOf(document, "parent").Height);
        Assert.Equal(50d, RectOf(document, "next").Top);
    }

    [Theory]
    // container, item, its left, top, width and height; the next item is 20 by 20
    [InlineData("flex-direction: column", "margin: 10px", 10, 10, 280, 16)]
    [InlineData("flex-direction: column; align-items: center", "margin: 10px; width: 50px", 125, 10, 50, 16)]
    [InlineData("flex-direction: column; align-items: flex-end", "margin: 10px; width: 50px", 240, 10, 50, 16)]
    [InlineData("flex-direction: column; align-items: center", "margin: 10px; width: 100%", 0, 10, 300, 16)]
    [InlineData("flex-direction: column", "margin: 5%", 15, 15, 270, 16)]
    [InlineData("flex-direction: column", "margin-left: auto; width: 50px", 250, 0, 50, 16)]
    [InlineData("flex-direction: column", "margin: auto; width: 50px", 125, 82, 50, 16)]
    [InlineData("", "margin: 10px; width: 50px", 10, 10, 50, 180)]
    [InlineData("align-items: center", "margin: 10px; width: 50px; height: 50px", 10, 75, 50, 50)]
    [InlineData("align-items: flex-end", "margin: 10px; width: 50px; height: 50px", 10, 140, 50, 50)]
    [InlineData("", "margin: 5%; width: 50px", 15, 15, 50, 170)]
    [InlineData("", "margin-top: auto; width: 50px", 0, 184, 50, 16)]
    [InlineData("", "margin-bottom: auto; width: 50px", 0, 0, 50, 16)]
    [InlineData("", "margin: auto; width: 50px", 115, 92, 50, 16)]
    [InlineData("align-items: center", "margin-top: auto; width: 50px", 0, 184, 50, 16)]
    public async Task FlexItem_ItsCrossMarginsTakePartInItsAlignment(string container, string item, double left, double top, double width, double height)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0; font: 14px/16px sans-serif }</style>
            <div style="display: flex; width: 300px; height: 200px; {{container}}"><div id="item" style="{{item}}">x</div><div style="width: 20px; height: 20px"></div></div>
            """);

        Assert.Equal((left, top, width, height), RectOf(document, "item"));
    }

    [Fact]
    public async Task FlexContainer_OfAutoHeightContainsTheMarginsOfItsItems()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div id="row" style="display: flex; width: 300px"><div style="margin: 10px; height: 30px; width: 30px"></div></div>
            """);

        Assert.Equal(50d, RectOf(document, "row").Height);
    }

    [Theory]
    [InlineData("<div class='t'></div>", 0, 16)] // block-level: on its own below the line
    [InlineData("<span class='t'></span>", 28, 0)] // inline-level: where it is on the line
    [InlineData("<span class='t' style='display: inline-block'></span>", 28, 0)]
    public async Task AbsoluteBox_WithoutOffsets_IsWhereItWouldBeAfterText(string markup, double left, double top)
    {
        var document = await OpenAsync($$"""
            <style>body { margin: 0 } .t { position: absolute; width: 10px; height: 10px }</style>
            <div id="line" style="font: 16px/16px monospace">aaa{{markup}}</div>
            """);
        var line = RectOf(document, "line");
        var box = document.QuerySelector(".t")!.GetBoundingClientRect();

        Assert.Equal(left, box.Left, tolerance: 2d);
        Assert.Equal(line.Top + top, box.Top, tolerance: 1d);
    }

    [Fact]
    public async Task AbsoluteBox_IsNotClippedByABoxThatIsNotItsContainingBlock()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } .a { position: absolute; left: 150px; top: 0; width: 30px; height: 30px }</style>
            <div style="overflow: hidden; width: 100px; height: 40px"><div class="a" style="background-color: rgb(255, 0, 0)"></div></div>
            <div style="overflow: hidden; width: 100px; height: 40px; position: relative"><div class="a" style="background-color: rgb(0, 255, 0)"></div></div>
            <div style="overflow: hidden; width: 100px; height: 40px"><div style="position: fixed; left: 150px; top: 100px; width: 30px; height: 30px; background-color: rgb(0, 0, 255)"></div></div>
            """);
        var list = new HtmlRenderer().BuildDisplayList(document, Device);
        var commands = list.Commands.ToList();

        // what escapes paints with no clip around it, what doesn't is in the clip of its containing block
        int Clips(RenderColor color)
        {
            var index = commands.FindIndex(command => command is FillRectCommand fill && fill.Color.Equals(color));
            return commands.Take(index).Count(command => command is PushClipCommand) - commands.Take(index).Count(command => command is PopClipCommand);
        }

        Assert.Equal(0, Clips(new RenderColor(255, 0, 0)));
        Assert.Equal(0, Clips(new RenderColor(0, 0, 255)));
        Assert.Equal(1, Clips(new RenderColor(0, 255, 0)));
    }

    [Fact]
    public async Task RootElement_ReportsItsOwnBoxButTheViewportAsItsClientArea()
    {
        var document = await OpenAsync("""
            <html><body style="margin: 10px"><div style="height: 50px"></div></body></html>
            """);
        var root = document.DocumentElement!;

        Assert.Equal(70d, root.GetBoundingClientRect().Height);
        Assert.Equal(70, root.GetOffsetHeight());
        Assert.Equal(600, root.GetClientHeight());
        Assert.Equal(600, root.GetScrollHeight());
        Assert.Equal(800d, root.GetBoundingClientRect().Width);
    }

    [Fact]
    public async Task Columns_PlaceTheBoxesInThemWhereTheyArePainted()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div style="columns: 2; column-gap: 20px; width: 400px">
              <div id="first" style="height: 40px; break-inside: avoid"></div>
              <div id="second" style="height: 40px; break-inside: avoid"></div>
            </div>
            """);

        Assert.Equal((0d, 0d, 190d, 40d), RectOf(document, "first"));
        Assert.Equal((210d, 0d, 190d, 40d), RectOf(document, "second"));
    }

    [Fact]
    public async Task Columns_GiveABoxAcrossThemAFragmentInEach()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div style="columns: 2; column-gap: 20px; width: 400px; height: 100px; column-fill: auto"><div id="box" style="height: 150px"></div></div>
            """);
        var box = document.GetElementById("box")!;
        var rects = box.GetClientRects();

        Assert.Equal((0d, 0d, 400d, 100d), RectOf(document, "box"));
        Assert.Equal(2, rects.Length);
        Assert.Equal(100d, rects.Item(0)!.Height);
        Assert.Equal(210d, rects.Item(1)!.Left);
        Assert.Equal(50d, rects.Item(1)!.Height);
    }

    [Fact]
    public async Task ZeroWidthBoxes_StillLayOutWhatIsInThem()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0; font: 14px/16px sans-serif }</style>
            <div style="width: 0"><div id="words">some words here</div></div>
            <div style="width: 0; height: 0"><div><div id="placed" style="position: absolute; left: 300px; top: 300px; width: 5px; height: 5px"></div></div></div>
            <div style="display: flex"><div id="item"><div><span id="inner" style="position: absolute; left: 320px; top: 300px; width: 5px; height: 5px"></span></div></div></div>
            """);

        Assert.Equal((0d, 0d, 0d, 48d), RectOf(document, "words"));
        Assert.Equal((300d, 300d, 5d, 5d), RectOf(document, "placed"));
        Assert.Equal((320d, 300d, 5d, 5d), RectOf(document, "inner"));
    }

    [Fact]
    public async Task AbsoluteBox_IsPlacedAgainstAPositionedTable()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 } table { border-spacing: 0 }</style>
            <table id="block" style="position: relative; width: 200px"><tr><td><div style="height: 60px">
              <div id="box" style="position: absolute; right: 0; bottom: 0; width: 10px; height: 10px"></div>
            </div></td></tr></table>
            """);
        var block = RectOf(document, "block");

        Assert.Equal((block.Left + block.Width - 10, block.Top + block.Height - 10, 10d, 10d), RectOf(document, "box"));
    }

    [Fact]
    public async Task AbsoluteBox_IsPlacedAgainstAPositionedInlineBox()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0; font: 16px/20px sans-serif }</style>
            <p style="margin: 30px 0 0 40px">Before <span id="inline" style="position: relative">text<span id="box" style="position: absolute; left: 3px; top: 4px; width: 5px; height: 5px"></span></span></p>
            """);
        var inline = document.GetElementById("inline")!.GetBoundingClientRect();
        var box = RectOf(document, "box");

        Assert.Equal(Math.Round(inline.Left + 3), box.Left);
        Assert.Equal(Math.Round(inline.Top + 4), box.Top);
    }

    [Fact]
    public async Task AbsoluteBox_KeepsItsStaticPositionWithoutOffsets()
    {
        var document = await OpenAsync("""
            <style>body { margin: 0 }</style>
            <div style="margin: 20px; padding: 5px">
              <div style="height: 30px"></div>
              <div id="box" style="position: absolute; width: 10px; height: 10px"></div>
              <div id="side" style="position: absolute; top: 1px; width: 10px; height: 10px"></div>
            </div>
            """);

        Assert.Equal((25d, 55d, 10d, 10d), RectOf(document, "box"));
        Assert.Equal((25d, 1d, 10d, 10d), RectOf(document, "side"));
    }

    [Fact]
    public async Task FixedBox_TakesPercentagesFromTheViewport()
    {
        var document = await OpenAsync("""
            <style>body { margin: 20px }</style>
            <div style="width: 100px"><div id="box" style="position: fixed; left: 10%; top: 10%; width: 50%; height: 50%"></div></div>
            """);

        Assert.Equal((80d, 60d, 400d, 300d), RectOf(document, "box"));
    }

    [Fact]
    public async Task RootElement_DoesNotCollapseItsMarginWithTheBody()
    {
        var document = await OpenAsync("""
            <html style="margin: 7px; position: relative">
            <body style="margin: 10px"><div style="height: 20px"></div>
              <div id="box" style="position: absolute; left: 0; top: 0; width: 5px; height: 5px"></div></body>
            </html>
            """);

        Assert.Equal(17d, document.Body!.GetBoundingClientRect().Top);
        Assert.Equal((7d, 7d, 5d, 5d), RectOf(document, "box"));
    }

    [Fact]
    public async Task GridRow_FitsAnInlineItemLaidOutAsBlock()
    {
        // w3schools.com "All languages": <a> cards in a grid were measured as inline boxes, so
        // the rows were too short (or too tall) for the wrapped text of the blockified cards
        var document = await OpenAsync("""
            <div style="display: grid; grid-template-columns: 200px">
              <a id="item" style="padding: 10px"><h3 style="margin: 0; font-size: 20px; line-height: 24px">Title</h3>
                <p id="text" style="margin: 0; font-size: 16px; line-height: 20px">Some longer text that wraps across two or more lines here</p></a>
            </div>
            <div id="next" style="height: 10px"></div>
            """);

        var item = document.GetElementById("item")!.GetBoundingClientRect();
        var text = document.GetElementById("text")!.GetBoundingClientRect();
        var next = document.GetElementById("next")!.GetBoundingClientRect();

        Assert.True(text.Height >= 40, $"The text should wrap, but is {text.Height}px tall.");
        Assert.Equal(text.Bottom + 10, item.Bottom, precision: 0);
        Assert.Equal(item.Bottom, next.Top, precision: 0);
    }

    [Fact]
    public async Task InlineSvg_KeepsWithinMaxSizeAndRatio()
    {
        // w3schools.com language icons: a viewBox-only SVG with auto size and max-width/height 44px
        var document = await OpenAsync("""
            <span style="display: inline-flex; align-items: center; width: 44px; height: 44px">
              <svg id="icon" viewBox="0 0 64 32" style="display: block; width: auto; height: auto; max-width: 44px; max-height: 44px"><rect width="64" height="32"/></svg>
            </span>
            """);

        var icon = document.GetElementById("icon")!.GetBoundingClientRect();

        Assert.Equal(44, icon.Width, precision: 0);
        Assert.Equal(22, icon.Height, precision: 0);
    }

    [Fact]
    public async Task Image_MinWidthGrowsKeepingRatio()
    {
        var document = await OpenAsync("""
            <svg id="icon" viewBox="0 0 20 10" style="min-width: 40px"><rect width="20" height="10"/></svg>
            """);

        var icon = document.GetElementById("icon")!.GetBoundingClientRect();

        Assert.Equal(40, icon.Width, precision: 0);
        Assert.Equal(20, icon.Height, precision: 0);
    }

    private static async Task<IDocument> ParseDocumentAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content(html));
        context.GetDomHarness();
        return document;
    }

    [Fact]
    public async Task RelativeBox_PaintsAboveEarlierAbsoluteSibling()
    {
        // the tractor store's cart button: the icon in a relative box was hidden by the
        // button's absolutely positioned ::before (positioned boxes paint in tree order)
        var document = await OpenAsync("""
            <style>
              .button { position: relative; width: 60px; height: 60px }
              .button::before { content: ""; position: absolute; inset: 0; background: #ffffff }
              .inner { position: relative; height: 60px; background: #ff0000 }
            </style>
            <div class="button"><div class="inner"></div></div>
            """);

        var commands = new HtmlRenderer().BuildDisplayList(document, Device).Commands.OfType<FillRectCommand>().ToList();
        var before = commands.FindIndex(command => command.Color == new RenderColor(0xff, 0xff, 0xff) && command.Rect.Width == 60);
        var inner = commands.FindIndex(command => command.Color == new RenderColor(0xff, 0x00, 0x00));

        Assert.True(before >= 0 && inner > before, $"::before at {before}, inner at {inner}");
    }

    [Fact]
    public async Task Image_WithPercentageWidth_DoesNotWidenGridTrack()
    {
        // the tractor store's home page: an image with width: 100% in a 1fr track can shrink
        // to nothing (compressible replaced element), so both tracks stay equal
        var document = await OpenAsync($"""
            <div style="display: grid; grid-template-columns: 1fr 1fr; gap: 16px; width: 600px">
              <a id="first" style="display: block"><img src="{GeneratedContentTests.PngDataUri(1000, 560)}" style="width: 100%"></a>
              <a id="second" style="display: block"><img src="{GeneratedContentTests.PngDataUri(1000, 560)}" style="width: 100%"></a>
            </div>
            """);

        var first = document.GetElementById("first")!.GetBoundingClientRect();
        var second = document.GetElementById("second")!.GetBoundingClientRect();

        Assert.Equal(292, first.Width, precision: 0);
        Assert.Equal(292, second.Width, precision: 0);
        Assert.Equal(308, second.Left, precision: 0);
    }

    [Fact]
    public async Task Image_WithPercentageMaxWidth_DoesNotWidenGridTrack()
    {
        var document = await OpenAsync($"""
            <div style="display: grid; grid-template-columns: 1fr 1fr; width: 400px">
              <div id="first"><img src="{GeneratedContentTests.PngDataUri(1000, 100)}" style="max-width: 100%; display: block"></div>
              <div></div>
            </div>
            """);

        Assert.Equal(200, document.GetElementById("first")!.GetBoundingClientRect().Width, precision: 0);
    }

    [Theory]
    [InlineData("justify-content: center", 16.5)]
    [InlineData("justify-content: end", 33)]
    [InlineData("justify-content: normal", 0)]
    public async Task ImplicitGridColumn_IsPlacedByJustifyContent(string alignment, double left)
    {
        // the tractor store's cart button centers its icon with place-content: center (which
        // sets justify-content from AngleSharp.Css 1.4.2) in a grid without template columns
        var document = await OpenAsync($"""
            <div style="display: grid; {alignment}; width: 66px; height: 66px">
              <div id="icon" style="width: 33px; height: 33px"></div>
            </div>
            """);

        Assert.Equal(left, document.GetElementById("icon")!.GetBoundingClientRect().Left, precision: 1);
    }

    [Fact]
    public async Task Float_AfterTextOnALine_IsPlacedOnThatLine()
    {
        // a close button floated right at the end of a dialog's title
        var document = await OpenAsync("""
            <h2 style="margin: 0; padding: 16px 32px; font-size: 16px; line-height: 20px">Title<a id="close" style="float: right">X</a></h2>
            <div style="width: 300px; line-height: 20px"><span id="text">Some text</span><b id="left" style="float: left">F</b> more</div>
            """);

        var close = document.GetElementById("close")!.GetBoundingClientRect();
        var left = document.GetElementById("left")!.GetBoundingClientRect();
        var text = document.GetElementById("text")!.GetBoundingClientRect();

        Assert.Equal(16d, close.Top, 1);
        Assert.Equal(800d - 32d, close.Right, 1);
        Assert.Equal(text.Top, left.Top, 1);
        Assert.Equal(0d, left.Left, 1);
        // what was before a left float on its line moves aside
        Assert.Equal(left.Right, text.Left, 1);
    }

    [Fact]
    public async Task PositionedBox_WithMaxHeightAndAutoMargins_IsCentered()
    {
        var document = await OpenAsync("""
            <div id="dialog" style="position: fixed; top: 0; left: 0; right: 0; bottom: 0; margin: auto; max-width: 50%; max-height: 50%">Dialog</div>
            """);

        var dialog = document.GetElementById("dialog")!.GetBoundingClientRect();

        Assert.Equal(200d, dialog.Left, 1);
        Assert.Equal(150d, dialog.Top, 1);
        Assert.Equal(400d, dialog.Width, 1);
        Assert.Equal(300d, dialog.Height, 1);
    }

    private static async Task<IDocument> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(Device));
        var document = await context.OpenAsync(request => request.Content($$"""
            <html><head><style>html, body { margin: 0; padding: 0; font-family: sans-serif }</style></head>
            <body>{{body}}</body></html>
            """));
        context.GetDomHarness();
        return document;
    }
}
