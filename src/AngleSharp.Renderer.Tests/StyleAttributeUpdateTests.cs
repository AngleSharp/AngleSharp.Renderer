namespace AngleSharp.Renderer.Tests;

using System.Globalization;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;

/// <summary>
/// A script changing the style attributes of positioned boxes (like a game moving its sprites)
/// gets the frame patched: a box only placed elsewhere by an inset is moved, one only painting
/// differently is painted again, anything else is laid out on its own. Either way the frame has
/// to be the one a full layout gives.
/// </summary>
public sealed class StyleAttributeUpdateTests
{
    private const string Scene = """
        <style>
          html, body { margin: 0; padding: 0; font-family: sans-serif; }
          .game { position: absolute; left: 20px; top: 10px; width: 300px; height: 200px; overflow: hidden; background: rgb(240, 240, 200); }
          .world { position: absolute; left: 0; bottom: 0; width: 600px; height: 100%; z-index: 0; }
          .tile { position: absolute; width: 20px; height: 20px; z-index: 95; background: linear-gradient(90deg, rgb(200, 0, 0) 50%, rgb(0, 0, 200) 50%); background-size: 40px 20px; }
          .figure { position: absolute; width: 16px; height: 24px; z-index: 99; background: linear-gradient(90deg, rgb(0, 150, 0) 50%, rgb(150, 0, 150) 50%); background-size: 32px 24px; }
          .label { position: absolute; left: 4px; top: 4px; font-size: 12px; }
        </style>
        <div class="game" id="game">
          <div class="world" id="world" style="left: 0px">
            <div class="tile" id="first" style="left: 0px; bottom: 0px"></div>
            <div class="tile" style="left: 20px; bottom: 0px"></div>
            <div class="tile" style="left: 40px; bottom: 0px; background-position: -20px 0px"></div>
            <div class="tile" style="left: 200px; bottom: 40px"></div>
            <div class="figure" id="hero" style="left: 30px; bottom: 20px; background-position: 0px 0px"></div>
            <div class="figure" id="enemy" style="right: 300px; bottom: 20px"></div>
            <div class="label" id="label" style="left: 4px">Score</div>
          </div>
          <div class="gauge" id="gauge" style="position: absolute; right: 10px; top: 10px; z-index: 1000; width: 30px; height: 12px; background: rgb(0, 0, 0)"></div>
        </div>
        """;

    [Fact]
    public async Task MovingABoxByItsInsetPatchesTheFrameAsALayoutWould()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var world = document.GetElementById("world")!;
        var hero = document.GetElementById("hero")!;

        world.GetStyle().SetProperty("left", "-37.5px");
        hero.GetStyle().SetProperty("left", "41px");
        hero.GetStyle().SetProperty("bottom", "26px");
        var patched = harness.UpdateFrame();

        AssertSameAsFullLayout(document, harness, patched);

        // moved again from where it was moved to
        world.GetStyle().SetProperty("left", "-75px");
        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task MovingABoxByItsRightOrBottomPatchesTheFrame()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var enemy = document.GetElementById("enemy")!;

        enemy.GetStyle().SetProperty("right", "310px");
        enemy.GetStyle().SetProperty("bottom", "30px");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task WhatIsLaidOutAgainInAMovedBoxIsWhereTheBoxIsNow()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var world = document.GetElementById("world")!;

        world.GetStyle().SetProperty("left", "-50px");
        harness.UpdateFrame();
        document.GetElementById("label")!.TextContent = "Score 100";
        document.GetElementById("hero")!.GetStyle().SetProperty("width", "20px");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task ABackgroundChangeIsPaintedAgainOnly()
    {
        var (document, harness) = await OpenAsync(Scene);
        var before = harness.PrepareFrame();

        // a sprite showing its next frame
        document.GetElementById("hero")!.GetStyle().SetProperty("background-position", "-16px 0px");
        var patched = harness.UpdateFrame();

        Assert.NotNull(patched);
        Assert.Same(before.HitTestMap, patched!.HitTestMap);
        AssertSameAsFullLayout(document, harness, patched);
    }

    [Fact]
    public async Task ABoxRemovedAfterItsStyleChangedStillPatchesTheFrame()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var enemy = document.GetElementById("enemy")!;

        // e.g. a coin moving up once more before the game removes it
        enemy.GetStyle().SetProperty("bottom", "30px");
        enemy.GetStyle().SetProperty("background-position", "-16px 0px");
        enemy.Remove();

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".was-validated :valid ~ .valid-feedback { display: block }")]
    [InlineData("li:first-child, .tile + .other, .menu:empty { color: red }")]
    public async Task RemovingAPositionedBoxTakesItsCommandsOutWithoutALayout(string rule)
    {
        // a coin of Mario 5 disappearing, one of hundreds of sprites of the world (with the
        // structural rules of Bootstrap matching none of them)
        var (document, harness) = await OpenAsync(Scene.Replace("</style>", rule + "</style>"));
        harness.PrepareFrame();

        document.GetElementById("enemy")!.Remove();
        var patched = harness.UpdateFrame();

        Assert.NotNull(patched);
        Assert.Equal(TimeSpan.Zero, patched!.Metrics.Style);
        AssertSameAsFullLayout(document, harness, patched);
    }

    [Fact]
    public async Task ChangesAfterARemovedBoxStillPatchTheFrame()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();

        document.GetElementById("first")!.Remove();
        harness.UpdateFrame();
        document.GetElementById("world")!.GetStyle().SetProperty("left", "-20px");
        document.GetElementById("hero")!.GetStyle().SetProperty("background-position", "-16px 0px");
        document.GetElementById("label")!.TextContent = "Score 100";

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Theory]
    [InlineData(".figure:last-child { background: rgb(255, 0, 0) }")]
    [InlineData(".tile + .figure { background: rgb(255, 0, 0) }")]
    [InlineData(".world:empty { display: none }")]
    [InlineData(":nth-child(2) { outline: 2px solid red }")]
    public async Task RemovingABoxOthersAreMatchedByIsLaidOut(string rule)
    {
        var (document, harness) = await OpenAsync(Scene.Replace("</style>", rule + "</style>"));
        harness.PrepareFrame();

        document.GetElementById("enemy")!.Remove();
        document.GetElementById("label")!.Remove();

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task ABoxShownInAMovedBoxMovesWithIt()
    {
        // the figure is painted inside the translation the world got for its move
        var (document, harness) = await OpenAsync(Scene.Replace("id=\"hero\" style=\"", "id=\"hero\" style=\"display: none; "));
        harness.PrepareFrame();

        document.GetElementById("hero")!.GetStyle().SetProperty("display", "block");
        document.GetElementById("world")!.GetStyle().SetProperty("left", "-55.5px");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task ABoxRemovedWhileItsParentIsLaidOutAgainPatchesTheFrame()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var world = document.GetElementById("world")!;
        var coin = document.CreateElement("div");
        coin.ClassName = "tile";
        coin.SetAttribute("style", "left: 59px; bottom: 44px");
        world.AppendChild(coin);
        harness.UpdateFrame();

        var other = document.CreateElement("div");
        other.ClassName = "tile";
        other.SetAttribute("style", "left: 142px; bottom: 17px");
        world.AppendChild(other);
        coin.Remove();

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task AnInsetOverriddenByARuleDoesNotMoveTheBox()
    {
        var (document, harness) = await OpenAsync(Scene.Replace("</style>", "#hero { left: 60px !important; }</style>"));
        harness.PrepareFrame();

        document.GetElementById("hero")!.GetStyle().SetProperty("left", "10px");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task APercentageInsetIsLaidOutAgain()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();

        document.GetElementById("hero")!.GetStyle().SetProperty("left", "25%");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task ManyChangesOfTheSameStylesBetweenFramesStillPatchTheFrame()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var hero = document.GetElementById("hero")!;
        var world = document.GetElementById("world")!;

        // a game ticking many times between two frames
        for (var tick = 0; tick < 200; tick++)
        {
            hero.GetStyle().SetProperty("left", (30 + tick % 50).ToString(CultureInfo.InvariantCulture) + "px");
            hero.GetStyle().SetProperty("background-position", tick % 2 == 0 ? "0px 0px" : "-16px 0px");
            world.GetStyle().SetProperty("left", (-tick / 4.0).ToString(CultureInfo.InvariantCulture) + "px");
        }

        var patched = harness.UpdateFrame();

        Assert.NotNull(patched);
        AssertSameAsFullLayout(document, harness, patched);
    }

    [Fact]
    public async Task MovesAndRepaintsInARowAreWhatALayoutShows()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var hero = document.GetElementById("hero")!;
        var style = hero.GetStyle();

        // frames of a game: running, showing other sprites, jumping
        for (var tick = 1; tick <= 12; tick++)
        {
            style.SetProperty("left", (30 + tick * 3).ToString(CultureInfo.InvariantCulture) + "px");

            if (tick % 3 == 0)
            {
                style.SetProperty("background-position", tick % 2 == 0 ? "0px 0px" : "-16px 0px");
            }

            if (tick % 4 == 0)
            {
                style.SetProperty("bottom", (20 + tick).ToString(CultureInfo.InvariantCulture) + "px");
            }

            Assert.NotNull(harness.UpdateFrame());
        }

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task AClassOverridingTheInsetAfterMovesIsWhatALayoutShows()
    {
        var (document, harness) = await OpenAsync(Scene.Replace("</style>", ".pinned { left: 100px !important; }</style>"));
        harness.PrepareFrame();
        var hero = document.GetElementById("hero")!;

        hero.GetStyle().SetProperty("left", "40px");
        harness.UpdateFrame();
        hero.GetStyle().SetProperty("left", "50px");
        harness.UpdateFrame();
        hero.ClassList.Add("pinned");
        harness.UpdateFrame();
        hero.GetStyle().SetProperty("left", "60px");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task ABoxWithATransitionIsNotMovedRightAway()
    {
        var (document, harness) = await OpenAsync(Scene.Replace("</style>", "#hero { transition: left 1s linear; }</style>"));
        harness.PrepareFrame();
        var hero = document.GetElementById("hero")!;

        hero.GetStyle().SetProperty("left", "130px");
        var frame = harness.UpdateFrame() ?? harness.PrepareFrame();

        Assert.True(frame.IsAnimating);
        Assert.NotNull(harness.GetTransitioningValue(hero, "left"));
    }

    [Fact]
    public async Task ARepaintPaintingMoreInAMovedBoxLeavesWhatIsAfterItInPlace()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var world = document.GetElementById("world")!;
        var hero = document.GetElementById("hero")!;

        world.GetStyle().SetProperty("left", "-20px");
        harness.UpdateFrame();
        // painted with a shadow now: more commands in the box of the world
        hero.GetStyle().SetProperty("box-shadow", "2px 2px 0px rgb(0, 0, 0)");
        hero.GetStyle().SetProperty("left", "50px");
        harness.UpdateFrame();
        world.GetStyle().SetProperty("left", "-40px");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Fact]
    public async Task MovingTheFirstAndLastBoxInAMovedBoxKeepsTheirTransformsInIt()
    {
        var (document, harness) = await OpenAsync(Scene);
        harness.PrepareFrame();
        var world = document.GetElementById("world")!;

        // the commands of the world start with the first box in it, and end with the box painted
        // last in it (raised by its z-index)
        document.GetElementById("first")!.GetStyle().SetProperty("left", "5px");
        document.GetElementById("label")!.GetStyle().SetProperty("left", "9px");
        document.GetElementById("enemy")!.GetStyle().SetProperty("right", "290px");
        harness.UpdateFrame();
        world.GetStyle().SetProperty("left", "-30px");
        harness.UpdateFrame();
        document.GetElementById("first")!.GetStyle().SetProperty("left", "8px");
        world.GetStyle().SetProperty("left", "-45px");

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RandomChangesOfAGameAreWhatALayoutShows(int seed)
    {
        // a game: tiles and figures in a world scrolled by its left, gauges painted after it
        var world = new System.Text.StringBuilder();

        for (var i = 0; i < 12; i++)
        {
            world.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\" id=\"t{i}\" style=\"left: {i * 20}px; bottom: {(i % 3) * 20}px\"></div>");
        }

        for (var i = 0; i < 5; i++)
        {
            world.Append(CultureInfo.InvariantCulture, $"<div class=\"figure\" id=\"f{i}\" style=\"left: {30 + i * 40}px; bottom: 20px; background-position: 0px 0px{(i == 4 ? "; display: none" : "")}\"></div>");
        }

        var page = Scene[..Scene.IndexOf("<div class=\"game\"", StringComparison.Ordinal)] + $"""
            <div class="game" id="game">
              <div class="world" id="world" style="left: 0px">{world}</div>
              <div class="gauge" id="coins" style="position: absolute; left: 10px; top: 10px; z-index: 1000; width: 30px; height: 12px; background: rgb(0, 0, 0)">0</div>
              <div class="gauge" id="lives" style="position: absolute; right: 10px; top: 10px; z-index: 1000; width: 30px; height: 12px; background: rgb(200, 0, 0)"></div>
            </div>
            """;
        var (document, harness) = await OpenAsync(page);
        harness.PrepareFrame();
        var random = new Random(seed);
        string Px(double value) => value.ToString(CultureInfo.InvariantCulture) + "px";

        for (var step = 0; step < 40; step++)
        {
            var figure = document.GetElementById($"f{random.Next(5)}")!.GetStyle();
            var tile = document.GetElementById($"t{random.Next(12)}")!.GetStyle();

            switch (random.Next(9))
            {
                case 0:
                    figure.SetProperty("left", Px(random.Next(0, 300)));
                    break;
                case 1:
                    figure.SetProperty("left", Px(random.Next(0, 300)));
                    figure.SetProperty("background-position", Px(-16 * random.Next(2)) + " 0px");
                    break;
                case 2:
                    figure.SetProperty("bottom", Px(random.Next(0, 80)));
                    figure.SetProperty("width", Px(random.Next(12, 20)));
                    break;
                case 3:
                    figure.SetProperty("display", random.Next(2) == 0 ? "none" : "block");
                    break;
                case 4:
                    tile.SetProperty("background-position", Px(-20 * random.Next(2)) + " 0px");
                    break;
                case 5:
                    tile.SetProperty("left", Px(random.Next(0, 300)));
                    break;
                case 6:
                    document.GetElementById("world")!.GetStyle().SetProperty("left", Px(-random.Next(0, 300) / 2.0));
                    break;
                case 7:
                    document.GetElementById("coins")!.TextContent = random.Next(100).ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    figure.SetProperty("box-shadow", random.Next(2) == 0 ? "none" : "1px 1px 0px rgb(0, 0, 0)");
                    break;
            }

            // a frame now and then, as the changes come in between frames
            if (random.Next(3) == 0 && harness.UpdateFrame() is null)
            {
                harness.PrepareFrame();
            }
        }

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame() ?? harness.PrepareFrame());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RandomCoinsOfAGameAreWhatALayoutShows(int seed)
    {
        // a game whose coins come and go, while the rest changes
        var world = new System.Text.StringBuilder();

        for (var i = 0; i < 10; i++)
        {
            world.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\" id=\"c{i}\" style=\"left: {15 + i * 25}px; bottom: {60 + (i % 2) * 20}px\"></div> ");
        }

        for (var i = 0; i < 12; i++)
        {
            world.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\" id=\"t{i}\" style=\"left: {i * 20}px; bottom: {(i % 3) * 20}px\"></div>");
        }

        for (var i = 0; i < 5; i++)
        {
            world.Append(CultureInfo.InvariantCulture, $"<div class=\"figure\" id=\"f{i}\" style=\"left: {30 + i * 40}px; bottom: 20px; background-position: 0px 0px{(i == 4 ? "; display: none" : "")}\"></div>");
        }

        var page = Scene[..Scene.IndexOf("<div class=\"game\"", StringComparison.Ordinal)] + $"""
            <div class="game" id="game">
              <div class="world" id="world" style="left: 0px">{world}</div>
              <div class="gauge" id="coins" style="position: absolute; left: 10px; top: 10px; z-index: 1000; width: 30px; height: 12px; background: rgb(0, 0, 0)">0</div>
              <div class="gauge" id="lives" style="position: absolute; right: 10px; top: 10px; z-index: 1000; width: 30px; height: 12px; background: rgb(200, 0, 0)"></div>
            </div>
            """;
        var (document, harness) = await OpenAsync(page);
        harness.PrepareFrame();
        var random = new Random(seed);
        string Px(double value) => value.ToString(CultureInfo.InvariantCulture) + "px";
        var coins = Enumerable.Range(0, 10).Select(i => document.GetElementById($"c{i}")!).ToList();

        for (var step = 0; step < 40; step++)
        {
            var figure = document.GetElementById($"f{random.Next(5)}")!.GetStyle();
            var tile = document.GetElementById($"t{random.Next(12)}")!.GetStyle();

            switch (random.Next(12))
            {
                case 9:
                    // a coin popping out of a box
                    var added = document.CreateElement("div");
                    added.ClassName = "tile";
                    added.SetAttribute("style", $"left: {Px(random.Next(0, 300))}; bottom: {Px(random.Next(0, 100))}");
                    document.GetElementById("world")!.AppendChild(added);
                    coins.Add(added);
                    break;
                case >= 10 when coins.Count > 0:
                    var coin = coins[random.Next(coins.Count)];

                    // sometimes moved once more before it is removed
                    if (random.Next(2) == 0)
                    {
                        coin.GetStyle().SetProperty("bottom", Px(random.Next(0, 100)));
                    }

                    coin.Remove();
                    coins.Remove(coin);
                    break;
                case 0:
                    figure.SetProperty("left", Px(random.Next(0, 300)));
                    break;
                case 1:
                    figure.SetProperty("left", Px(random.Next(0, 300)));
                    figure.SetProperty("background-position", Px(-16 * random.Next(2)) + " 0px");
                    break;
                case 2:
                    figure.SetProperty("bottom", Px(random.Next(0, 80)));
                    figure.SetProperty("width", Px(random.Next(12, 20)));
                    break;
                case 3:
                    figure.SetProperty("display", random.Next(2) == 0 ? "none" : "block");
                    break;
                case 4:
                    tile.SetProperty("background-position", Px(-20 * random.Next(2)) + " 0px");
                    break;
                case 5:
                    tile.SetProperty("left", Px(random.Next(0, 300)));
                    break;
                case 6:
                    document.GetElementById("world")!.GetStyle().SetProperty("left", Px(-random.Next(0, 300) / 2.0));
                    break;
                case 7:
                    document.GetElementById("coins")!.TextContent = random.Next(100).ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    figure.SetProperty("box-shadow", random.Next(2) == 0 ? "none" : "1px 1px 0px rgb(0, 0, 0)");
                    break;
            }

            // a frame now and then, as the changes come in between frames
            if (random.Next(3) == 0 && harness.UpdateFrame() is null)
            {
                harness.PrepareFrame();
            }
        }

        AssertSameAsFullLayout(document, harness, harness.UpdateFrame() ?? harness.PrepareFrame());
    }

    private static void AssertSameAsFullLayout(IDocument document, IDomHarness harness, AngleSharp.Renderer.Rendering.RenderedFrame? patched)
    {
        Assert.NotNull(patched);

        var elements = document.QuerySelectorAll(".game, .game *").ToList();
        var rects = elements.Select(Describe).ToList();
        var pixels = patched!.Rasterize().Data;
        var hits = HitTestGrid(patched);

        var full = harness.PrepareFrame(patched.OverscanTop);

        Assert.Equal(elements.Select(Describe).ToList(), rects);
        Assert.Equal(full.Rasterize().Data, pixels);
        Assert.Equal(HitTestGrid(full), hits);
    }

    // what is hit where, every few pixels
    private static List<string> HitTestGrid(AngleSharp.Renderer.Rendering.RenderedFrame frame)
    {
        var hits = new List<string>();

        for (var y = 0; y < 300; y += 3)
        {
            for (var x = 0; x < 400; x += 3)
            {
                var hit = frame.HitTestMap.HitTest(x, y);
                hits.Add($"{x},{y}:{hit?.Element.Id}{hit?.Element.ClassName}{hit?.Element.TextContent.Length}");
            }
        }

        return hits;
    }

    // a box as getBoundingClientRect has it (moves add up in floats, a layout sums the offsets)
    private static string Describe(IElement element)
    {
        var rect = element.GetBoundingClientRect();
        return string.Create(CultureInfo.InvariantCulture, $"{element.Id}{element.ClassName} {rect.Left:0.0},{rect.Top:0.0},{rect.Width:0.0},{rect.Height:0.0}");
    }

    private static async Task<(IDocument Document, IDomHarness Harness)> OpenAsync(string body)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRenderDevice(new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 300, FontSize = 16 }));
        var document = await context.OpenAsync(request => request.Content($"<!doctype html><html><head></head><body>{body}</body></html>"));
        return (document, document.Context.GetDomHarness());
    }
}
