namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using AngleSharp.Renderer.Rendering;

/// <summary>
/// Selecting text: with the pointer through the harness (<see cref="IDomHarness.BeginTextSelection"/>),
/// and through the DOM (<see cref="ISelection"/>).
/// </summary>
public sealed class TextSelectionTests
{
    private const string Style = "font: 20px/30px monospace;";
    // the bundled monospace font at 20px
    private const double CharWidth = 12.04;

    [Fact]
    public async Task Drag_SelectsTheCharactersBetweenPressAndPointer()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");

        Assert.True(harness.BeginTextSelection(CharWidth * 2, 15));
        harness.ExtendTextSelection(CharWidth * 8, 15);
        harness.EndTextSelection();

        var selection = document.GetSelection();
        Assert.Equal("llo Wo", harness.SelectedText);
        Assert.Equal("llo Wo", selection.ToString());
        Assert.Equal("Range", selection.Type);
        Assert.Equal("forward", selection.Direction);
        Assert.Equal(2, selection.AnchorOffset);
        Assert.Equal(8, selection.FocusOffset);
        Assert.False(harness.IsSelectingText);
    }

    [Fact]
    public async Task Drag_OverTextNodesOfOneFlexItem_SelectsInEachNode()
    {
        // a template's text nodes ("Sign in with ", "Microsoft") are one anonymous flex item
        var (document, harness) = await OpenAsync($"""<div id="box" style="{Style} display: flex">Hello </div>""");
        var box = document.GetElementById("box")!;
        box.AppendChild(document.CreateTextNode("World"));
        harness.PrepareFrame();

        Assert.True(harness.BeginTextSelection(CharWidth * 2, 15));
        harness.ExtendTextSelection(CharWidth * 8, 15);
        harness.EndTextSelection();

        var selection = document.GetSelection();
        Assert.Equal("llo Wo", selection.ToString());
        Assert.Same(box.FirstChild, selection.AnchorNode);
        Assert.Equal(2, selection.AnchorOffset);
        Assert.Same(box.LastChild, selection.FocusNode);
        Assert.Equal(2, selection.FocusOffset);
    }

    [Fact]
    public async Task Drag_Backwards_KeepsTheAnchorAtThePress()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");

        harness.BeginTextSelection(CharWidth * 8, 15);
        harness.ExtendTextSelection(CharWidth * 2, 15);

        var selection = document.GetSelection();
        Assert.Equal("llo Wo", harness.SelectedText);
        Assert.Equal("backward", selection.Direction);
        Assert.Equal(8, selection.AnchorOffset);
        Assert.Equal(2, selection.FocusOffset);
    }

    [Fact]
    public async Task Drag_AcrossInlineElements_FollowsTheNodes()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style}">Hello <b id="b" style="font-weight: normal">big</b> World</div>""");

        harness.BeginTextSelection(CharWidth * 3, 15);
        harness.ExtendTextSelection(CharWidth * 12, 15);

        Assert.Equal("lo big Wo", harness.SelectedText);
        Assert.Equal(3, document.GetSelection().AnchorOffset);
    }

    [Fact]
    public async Task Offsets_AreThoseOfTheSource_WithCollapsedWhiteSpace()
    {
        var (document, harness) = await OpenAsync($"""<div id="t" style="{Style}">  Hello    <i></i>World</div>""");

        harness.BeginTextSelection(CharWidth * 7, 15);
        harness.ExtendTextSelection(CharWidth * 11, 15);

        var selection = document.GetSelection();
        Assert.Equal("orld", harness.SelectedText);
        Assert.Equal("World", selection.AnchorNode!.TextContent);
        Assert.Equal(1, selection.AnchorOffset);
        Assert.Equal(5, selection.FocusOffset);
    }

    [Fact]
    public async Task Drag_AcrossBlocks_SeparatesThemWithLineBreaks()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}"><div>first</div><div>second</div><p style="margin: 0">third</p></div>""");

        harness.BeginTextSelection(CharWidth * 2, 15);
        harness.ExtendTextSelection(CharWidth * 3, 75);

        Assert.Equal("rst\nsecond\n\nthi", harness.SelectedText);
    }

    [Fact]
    public async Task Text_KeepsASpaceWhereALineWrapped_AndABreakWhereForced()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} width: 80px">aaaa bbbb<br>cccc</div>""");

        harness.SelectAllText();

        Assert.Equal("aaaa bbbb\ncccc", harness.SelectedText);
    }

    [Fact]
    public async Task Text_IsAsShown_Transformed_WithoutWhatIsNotRendered()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} text-transform: uppercase">some <span style="display: none">hidden</span>text</div>""");

        harness.SelectAllText();

        Assert.Equal("SOME TEXT", harness.SelectedText);
    }

    [Fact]
    public async Task Text_SeparatesTableCellsWithTabs()
    {
        var (_, harness) = await OpenAsync($"""<table style="{Style}"><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>""");

        harness.SelectAllText();

        Assert.Equal("a\tb\nc\td", harness.SelectedText);
    }

    [Fact]
    public async Task DoubleClick_SelectsAWord_AndDraggingGoesOnByWords()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} white-space: nowrap">one two three four</div>""");

        harness.BeginTextSelection(CharWidth * 5, 15, clickCount: 2);
        Assert.Equal("two", harness.SelectedText);

        harness.ExtendTextSelection(CharWidth * 9, 15);
        Assert.Equal("two three", harness.SelectedText);

        harness.ExtendTextSelection(CharWidth * 1, 15);
        Assert.Equal("one two", harness.SelectedText);
    }

    [Fact]
    public async Task TripleClick_SelectsTheParagraph()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} width: 80px"><div>aaaa <b style="font-weight: normal">bbbb</b> cccc</div><div>dddd</div></div>""");

        harness.BeginTextSelection(CharWidth * 1, 45, clickCount: 3);

        Assert.Equal("aaaa bbbb cccc", harness.SelectedText);
    }

    [Fact]
    public async Task ShiftPress_ExtendsTheSelection()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");

        harness.BeginTextSelection(CharWidth * 2, 15);
        harness.ExtendTextSelection(CharWidth * 4, 15);
        harness.EndTextSelection();
        harness.BeginTextSelection(CharWidth * 7, 15, extend: true);

        Assert.Equal("llo W", harness.SelectedText);
        Assert.Equal(2, document.GetSelection().AnchorOffset);
    }

    [Fact]
    public async Task Press_ClearsWhatWasSelected()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");

        harness.SelectAllText();
        harness.BeginTextSelection(CharWidth * 2, 15);
        harness.EndTextSelection();

        Assert.Equal(string.Empty, harness.SelectedText);
        Assert.Equal("Caret", document.GetSelection().Type);
    }

    [Fact]
    public async Task Pointer_NextToTheText_SelectsUpToTheNearestLine()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">Hello</div><div style="{Style} margin-top: 100px">World</div>""");

        harness.BeginTextSelection(CharWidth * 2, 15);
        harness.ExtendTextSelection(280, 15);
        Assert.Equal("llo", harness.SelectedText);

        harness.ExtendTextSelection(CharWidth * 1, 195);
        Assert.Equal("llo\nW", harness.SelectedText);
    }

    [Fact]
    public async Task UserSelectNone_CannotBeSelected()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">one <span style="user-select: none">two</span> three</div><div style="{Style} user-select: none">four</div>""");

        Assert.False(harness.BeginTextSelection(CharWidth, 45));
        Assert.False(harness.IsSelectingText);

        harness.SelectAllText();

        Assert.Equal("one  three", harness.SelectedText);
    }

    [Fact]
    public async Task UserSelectText_InsideOfNone_CanBeSelected()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} user-select: none"><span style="user-select: text">one</span> two</div>""");

        harness.BeginTextSelection(0, 15);
        harness.ExtendTextSelection(CharWidth * 2, 15);

        Assert.Equal("on", harness.SelectedText);
    }

    [Fact]
    public async Task UserSelectAll_IsSelectedAsAWhole()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">one <span style="user-select: all">two <b>three</b></span> four</div>""");

        harness.BeginTextSelection(CharWidth * 5, 15);

        Assert.Equal("two three", harness.SelectedText);
    }

    [Fact]
    public async Task FormControls_DoNotStartASelection()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">text</div><input style="position: absolute; left: 0; top: 50px; width: 200px; height: 30px" value="value">""");

        Assert.False(harness.BeginTextSelection(10, 60));
    }

    [Fact]
    public async Task SelectStart_WhenCanceled_DoesNotSelect()
    {
        var (document, harness) = await OpenAsync($"""<div id="t" style="{Style}">Hello World</div>""");
        document.GetElementById("t")!.AddEventListener("selectstart", (_, e) => e.Cancel());

        Assert.False(harness.BeginTextSelection(CharWidth, 15));
        Assert.Equal("None", document.GetSelection().Type);
    }

    [Fact]
    public async Task SelectionChange_IsFired_AndThePaintInvalidated()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");
        var changes = 0;
        var invalidations = 0;
        document.AddEventListener("selectionchange", (_, _) => changes++);
        harness.PaintInvalidated += (_, _) => invalidations++;

        harness.SelectAllText();

        Assert.Equal(1, changes);
        Assert.True(invalidations > 0);
    }

    [Fact]
    public async Task Frame_PaintsTheHighlightBelowTheGlyphsOfTheSelectedCharacters()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");
        var plain = harness.PrepareFrame().Rasterize();

        harness.BeginTextSelection(CharWidth * 2, 15);
        harness.ExtendTextSelection(CharWidth * 8, 15);
        var frame = harness.UpdateFrame() ?? harness.PrepareFrame();
        var pixels = frame.Rasterize();

        // in the highlight, above the glyphs (a line is 30px, its glyphs are less)
        Assert.Equal(new byte[] { 0xB4, 0xD5, 0xFE, 0xFF }, Pixel(pixels, (int)(CharWidth * 5), 1));
        // not before and not after it
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(pixels, (int)CharWidth, 1));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(pixels, (int)(CharWidth * 9), 1));
        // and without a selection, not at all
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(plain, (int)(CharWidth * 5), 1));
        // the glyphs stay above it
        Assert.Contains(Enumerable.Range((int)(CharWidth * 2), (int)(CharWidth * 6)), x => Pixel(pixels, x, 15)[0] < 0x80);
    }

    [Fact]
    public async Task Frame_ShowingASelection_NeedsNoLayout()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");
        var plain = harness.PrepareFrame();

        Assert.Same(plain, harness.UpdateFrame());

        harness.SelectAllText();
        var selected = harness.UpdateFrame();

        Assert.NotNull(selected);
        Assert.NotSame(plain, selected);
        Assert.Same(selected, harness.UpdateFrame());

        harness.ClearTextSelection();
        Assert.Same(plain, harness.UpdateFrame());
    }

    [Fact]
    public async Task PaintToPng_ShowsTheSelection()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");
        var plain = harness.PaintToPng();

        harness.SelectAllText();

        Assert.NotEqual(plain.Data, harness.PaintToPng().Data);
    }

    [Fact]
    public async Task SelectionPseudoElement_GivesTheColors()
    {
        var (_, harness) = await OpenAsync("""<style>.c::selection { background-color: #ff0000; color: #00ff00 }</style>""" + $"""<div class="c" style="{Style}">HHHHHH</div>""", withSelectionPseudoElement: true);

        harness.SelectAllText();
        var pixels = (harness.UpdateFrame() ?? harness.PrepareFrame()).Rasterize();

        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(pixels, (int)(CharWidth * 2), 1));
        Assert.Contains(Enumerable.Range(0, (int)(CharWidth * 6)), x => Pixel(pixels, x, 15) is [< 0x40, > 0xC0, < 0x40, _]);
        Assert.DoesNotContain(Enumerable.Range(0, (int)(CharWidth * 6)), x => Pixel(pixels, x, 15) is [< 0x40, < 0x40, < 0x40, _]);
    }

    [Fact]
    public async Task SelectionPseudoElement_OnPartOfARun_RecolorsOnlyThatPart()
    {
        var (_, harness) = await OpenAsync("""<style>.c::selection { background-color: #ff0000; color: #00ff00 }</style>""" + $"""<div class="c" style="{Style}">HHHHHH</div>""", withSelectionPseudoElement: true);

        harness.BeginTextSelection(CharWidth * 2, 15);
        harness.ExtendTextSelection(CharWidth * 4, 15);
        var pixels = (harness.UpdateFrame() ?? harness.PrepareFrame()).Rasterize();

        Assert.Contains(Enumerable.Range(0, (int)(CharWidth * 2)), x => Pixel(pixels, x, 15) is [< 0x40, < 0x40, < 0x40, _]);
        Assert.Contains(Enumerable.Range((int)(CharWidth * 2) + 1, (int)(CharWidth * 2) - 2), x => Pixel(pixels, x, 15) is [< 0x40, > 0xC0, < 0x40, _]);
        Assert.Contains(Enumerable.Range((int)(CharWidth * 4) + 1, (int)(CharWidth * 2)), x => Pixel(pixels, x, 15) is [< 0x40, < 0x40, < 0x40, _]);
    }

    [Fact]
    public async Task UniversalRules_DoNotApplyToTheSelection()
    {
        var (_, harness) = await OpenAsync("""<style>* { background-color: #00ff00; color: #0000ff }</style>""" + $"""<div style="{Style}">HHHHHH</div>""", withSelectionPseudoElement: true);

        harness.SelectAllText();
        var pixels = (harness.UpdateFrame() ?? harness.PrepareFrame()).Rasterize();

        Assert.Equal(new byte[] { 0xB4, 0xD5, 0xFE, 0xFF }, Pixel(pixels, (int)(CharWidth * 2), 1));
    }

    [Fact]
    public async Task Frame_WithOverscan_FindsAndPaintsTheTextWhereItIs()
    {
        var (document, harness) = await OpenAsync($"""<div style="height: 500px"></div><div style="{Style} height: 500px">Hello World</div>""");
        document.DocumentElement.SetScrollTop(480);
        harness.PrepareFrame(100);
        var frame = harness.PrepareFrame(100);
        Assert.Equal(100, frame.OverscanTop);

        harness.BeginTextSelection(CharWidth * 2, 35);
        harness.ExtendTextSelection(CharWidth * 4, 35);
        var pixels = harness.UpdateFrame()!.Rasterize();

        Assert.Equal("ll", harness.SelectedText);
        Assert.Equal(new byte[] { 0xB4, 0xD5, 0xFE, 0xFF }, Pixel(pixels, (int)(CharWidth * 3), 100 + 21));
    }

    [Fact]
    public async Task Frame_Repositioned_KeepsTheSelection()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style} height: 1000px">Hello World</div>""");
        harness.SelectAllText();
        var frame = harness.PrepareFrame();

        var moved = frame.Reposition(10);

        Assert.Equal(new byte[] { 0xB4, 0xD5, 0xFE, 0xFF }, Pixel(moved.Rasterize(), 5, 0));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(moved.Rasterize(), 5, 25));
    }

    [Fact]
    public async Task Frame_PatchedByAMutation_KeepsTheSelectionAndItsPlaces()
    {
        var (document, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div><div id="p" style="{Style} position: absolute; top: 100px; left: 0">menu</div>""");
        harness.PrepareFrame();
        harness.BeginTextSelection(0, 15);
        harness.ExtendTextSelection(CharWidth * 5, 15);

        document.GetElementById("p")!.TextContent = "other";
        var pixels = harness.UpdateFrame()!.Rasterize();

        Assert.Equal("Hello", harness.SelectedText);
        Assert.Equal(new byte[] { 0xB4, 0xD5, 0xFE, 0xFF }, Pixel(pixels, (int)(CharWidth * 2), 1));

        harness.BeginTextSelection(CharWidth, 115);
        harness.ExtendTextSelection(CharWidth * 3, 115);

        Assert.Equal("th", harness.SelectedText);
    }

    [Fact]
    public async Task Ellipsis_IsNotPartOfTheText()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} width: 60px; overflow: hidden; white-space: nowrap; text-overflow: ellipsis">Hello World</div>""");

        harness.BeginTextSelection(0, 15);
        harness.ExtendTextSelection(60, 15);

        Assert.DoesNotContain("\u2026", harness.SelectedText);
        Assert.StartsWith("Hel", harness.SelectedText);
    }

    [Fact]
    public async Task Position_FollowsThePageScroll()
    {
        var (document, harness) = await OpenAsync($"""<div style="height: 500px"></div><div style="{Style} height: 500px">Hello World</div>""");
        harness.PrepareFrame();
        document.DocumentElement.SetScrollTop(480);

        harness.BeginTextSelection(CharWidth * 2, 35);
        harness.ExtendTextSelection(CharWidth * 4, 35);

        Assert.Equal("ll", harness.SelectedText);
    }

    [Fact]
    public async Task Position_FollowsTheScrollOfAContainer()
    {
        var (document, harness) = await OpenAsync($"""<div id="s" style="{Style} height: 60px; overflow: auto"><div>first</div><div>second</div><div>third</div><div>fourth</div></div>""");
        document.GetElementById("s")!.SetScrollTop(60);

        harness.BeginTextSelection(CharWidth, 15);
        harness.ExtendTextSelection(CharWidth * 3, 15);

        Assert.Equal("hi", harness.SelectedText);
    }

    [Fact]
    public async Task Position_FollowsTransforms()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} width: 100px; transform-origin: 0 0; transform: translate(100px, 50px) scale(2)">Hello</div>""");

        harness.BeginTextSelection(100 + (CharWidth * 2), 80);
        harness.ExtendTextSelection(100 + (CharWidth * 6), 80);

        Assert.Equal("el", harness.SelectedText);
    }

    [Fact]
    public async Task Position_OfFixedBoxes_DoesNotFollowThePage()
    {
        var (document, harness) = await OpenAsync($"""<div style="height: 1000px"></div><div style="{Style} position: fixed; top: 0; left: 0">Fixed</div>""");
        harness.PrepareFrame();
        document.DocumentElement.SetScrollTop(300);

        harness.BeginTextSelection(0, 15);
        harness.ExtendTextSelection(CharWidth * 2, 15);

        Assert.Equal("Fi", harness.SelectedText);
    }

    [Fact]
    public async Task Position_InAColumn_IsFound()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} width: 200px; columns: 2; column-gap: 0"><div>aaa</div><div>bbb</div></div>""");

        harness.BeginTextSelection(100 + CharWidth, 15);
        harness.ExtendTextSelection(100 + (CharWidth * 2), 15);

        Assert.Equal("b", harness.SelectedText);
    }

    [Fact]
    public async Task Position_InVerticalText_IsFound()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} writing-mode: vertical-rl; width: 30px; height: 200px">Hello</div>""");

        harness.BeginTextSelection(15, CharWidth);
        harness.ExtendTextSelection(15, CharWidth * 3);

        Assert.Equal("el", harness.SelectedText);
    }

    [Fact]
    public async Task Position_InRightToLeftText_RunsFromTheRight()
    {
        var (_, harness) = await OpenAsync($"""<div style="font: 20px/30px sans-serif; direction: rtl; width: 200px">{"אבגד"}</div>""");

        harness.BeginTextSelection(199, 15);
        harness.ExtendTextSelection(100, 15);

        Assert.Equal("אבגד", harness.SelectedText);
    }

    [Fact]
    public async Task Justified_SpacesCanBeSelected()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style} width: 200px; text-align: justify">aaa bbb ccc ddd eee fff ggg hhh</div>""");

        harness.SelectAllText();

        Assert.Equal("aaa bbb ccc ddd eee fff ggg hhh", harness.SelectedText);
    }

    [Fact]
    public async Task Selection_SurvivesALayout()
    {
        var (document, harness) = await OpenAsync($"""<div id="t" style="{Style}">Hello World</div>""");
        harness.SelectAllText();
        document.GetElementById("t")!.SetAttribute("style", $"{Style} margin-top: 50px");

        Assert.Equal("Hello World", harness.SelectedText);
        Assert.Equal(new byte[] { 0xB4, 0xD5, 0xFE, 0xFF }, Pixel(harness.PrepareFrame().Rasterize(), 5, 51));
    }

    [Fact]
    public async Task Selection_OfRemovedNodes_CollapsesWhereTheyWere()
    {
        var (document, harness) = await OpenAsync($"""<div id="t" style="{Style}">Hello World</div>""");
        harness.BeginTextSelection(0, 15);
        harness.ExtendTextSelection(CharWidth * 3, 15);

        document.GetElementById("t")!.Remove();

        Assert.Equal("Caret", document.GetSelection().Type);
        Assert.Same(document.Body, document.GetSelection().FocusNode);
        Assert.Equal(string.Empty, harness.SelectedText);
    }

    [Fact]
    public async Task Setting_Disabled_SelectsAndRecordsNothing()
    {
        var (_, harness) = await OpenAsync($"""<div style="{Style}">Hello World</div>""");
        harness.SelectAllText();
        Assert.Equal("Hello World", harness.SelectedText);

        harness.IsTextSelectionEnabled = false;

        Assert.Equal(string.Empty, harness.SelectedText);
        Assert.False(harness.BeginTextSelection(0, 15));
        harness.SelectAllText();
        Assert.Equal(string.Empty, harness.SelectedText);

        harness.IsTextSelectionEnabled = true;
        Assert.True(harness.BeginTextSelection(0, 15));
    }

    [Fact]
    public async Task Disabled_DoesNotChangeTheDisplayList_ComparedToWithoutAHarness()
    {
        var html = $"""<div style="{Style}">Hello <b>World</b></div>""";
        var (documentWithout, _) = await OpenAsync(html, createHarness: false);
        var (documentWith, harness) = await OpenAsync(html);
        harness.IsTextSelectionEnabled = false;

        var renderer = new HtmlRenderer();
        var without = renderer.BuildDisplayList(documentWithout, Device).Commands.Select(static c => c.GetType()).ToList();
        var with = renderer.BuildDisplayList(documentWith, Device).Commands.Select(static c => c.GetType()).ToList();

        Assert.Equal(without, with);
    }

    [Fact]
    public async Task Api_SetBaseAndExtent_IsPaintedAndStringified()
    {
        var (document, harness) = await OpenAsync($"""<div id="t" style="{Style}">Hello World</div>""");
        var text = document.GetElementById("t")!.FirstChild!;
        var selection = document.GetSelection();

        selection.SetBaseAndExtent(text, 6, text, 11);

        Assert.Equal("World", selection.ToString());
        Assert.Equal(1, selection.RangeCount);
        Assert.Equal(6, selection.GetRangeAt(0).Start);
        Assert.Equal(new byte[] { 0xB4, 0xD5, 0xFE, 0xFF }, Pixel(harness.PrepareFrame().Rasterize(), (int)(CharWidth * 8), 1));
    }

    [Fact]
    public async Task Api_OfElementBoundaries_SelectsTheTextInThem()
    {
        var (document, harness) = await OpenAsync($"""<div id="a" style="{Style}">one</div><div id="b" style="{Style}">two</div><div style="{Style}">three</div>""");
        var selection = document.GetSelection();
        var range = document.CreateRange();
        range.StartBefore(document.GetElementById("a")!);
        range.EndAfter(document.GetElementById("b")!);

        selection.AddRange(range);

        Assert.Equal("one\ntwo", harness.SelectedText);
        Assert.True(selection.ContainsNode(document.GetElementById("a")!));
        Assert.False(selection.ContainsNode(document.Body!.LastElementChild!));
    }

    [Fact]
    public async Task Api_MovesTheCaretAround()
    {
        var (document, _) = await OpenAsync($"""<div id="t" style="{Style}">Hello World</div>""");
        var text = document.GetElementById("t")!.FirstChild!;
        var selection = document.GetSelection();

        selection.Collapse(text, 2);
        Assert.Equal("Caret", selection.Type);
        Assert.True(selection.IsCollapsed);

        selection.Extend(text, 5);
        Assert.Equal("llo", selection.ToString());

        selection.Extend(text, 0);
        Assert.Equal("He", selection.ToString());
        Assert.Equal("backward", selection.Direction);

        selection.CollapseToStart();
        Assert.Equal(0, selection.FocusOffset);

        selection.SelectAllChildren(text.Parent!);
        Assert.Equal("Hello World", selection.ToString());

        selection.RemoveAllRanges();
        Assert.Equal("None", selection.Type);
        Assert.Throws<DomException>(() => selection.GetRangeAt(0));
        Assert.Throws<DomException>(() => selection.Collapse(text, 100));
    }

    [Fact]
    public async Task Api_WindowHasTheSelectionOfItsDocument()
    {
        var (document, _) = await OpenAsync($"""<div>text</div>""");

        Assert.Same(document.GetSelection(), document.DefaultView!.GetSelection());
    }

    [Fact]
    public async Task Api_WithoutAHarness_GivesTheTextOfTheNodes()
    {
        var (document, _) = await OpenAsync($"""<div id="t">a<b>b</b>c</div>""", createHarness: false);
        var selection = document.GetSelection();
        selection.SelectAllChildren(document.GetElementById("t")!);

        Assert.Equal("abc", selection.ToString());
    }

    private static readonly DefaultRenderDevice Device = new() { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };

    private static byte[] Pixel(RenderedPixels pixels, int x, int y) =>
        pixels.Data[(((y * pixels.Width) + x) * 4)..((((y * pixels.Width) + x) * 4) + 4)];

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string body, bool createHarness = true, bool withSelectionPseudoElement = false)
    {
        var configuration = Configuration.Default.WithCss().WithRenderDevice(Device);

        // the released AngleSharp and AngleSharp.Css do not know ::selection yet; it stands in for ::before here
        if (withSelectionPseudoElement)
        {
            var selectors = new DefaultPseudoElementSelectorFactory();
            var before = selectors.Create("before")!;
            selectors.Unregister("selection");
            selectors.Register("selection", before);
            var elements = new DefaultPseudoElementFactory();
            elements.Unregister("selection");
            elements.Register("selection", host => elements.Create(host, "before"));
            configuration = configuration.WithOnly<IPseudoElementSelectorFactory>(selectors).WithOnly<IPseudoElementFactory>(elements);
        }

        var context = BrowsingContext.New(configuration);
        var document = await context.OpenAsync(request => request.Content($$"""
            <!doctype html><html><head><style>html, body { margin: 0; padding: 0; }</style></head>
            <body>{{body}}</body></html>
            """));
        return (document, createHarness ? document.Context.GetDomHarness() : null!);
    }
}
