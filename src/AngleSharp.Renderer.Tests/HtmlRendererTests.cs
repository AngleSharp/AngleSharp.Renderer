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

    private const string OnePixelPng = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGMAAQABAA4A4cQTmwAAAABJRU5ErkJggg==";

    [Fact]
    public async Task BuildDisplayList_ObjectFitContainLetterboxesTheImageWithinItsBox()
    {
        var document = await ParseAsync($"""
            <html><body>
                <img src="{OnePixelPng}" style="width:40px; height:20px; object-fit:contain;" />
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

        // A 1x1 natural image scaled to `contain` within a 40x20 box uses the smaller axis
        // (height), so it ends up 20x20 - centered, leaving 10px of empty space on each side.
        Assert.Equal(20f, imageCommand.Rect.Width, precision: 2);
        Assert.Equal(20f, imageCommand.Rect.Height, precision: 2);
        Assert.Equal(10f, imageCommand.Rect.X, precision: 2);
        Assert.Equal(0f, imageCommand.Rect.Y, precision: 2);

        // Fits entirely within the box - no clipping needed.
        Assert.Empty(displayList.Commands.OfType<PushClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ObjectFitCoverFillsTheBoxAndClipsTheOverflow()
    {
        var document = await ParseAsync($"""
            <html><body>
                <img src="{OnePixelPng}" style="width:40px; height:20px; object-fit:cover;" />
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

        // `cover` scales up to the larger axis (width), so the 1x1 image becomes 40x40 - centered
        // vertically, meaning it overflows the 20px-tall box by 10px on the top and bottom.
        Assert.Equal(40f, imageCommand.Rect.Width, precision: 2);
        Assert.Equal(40f, imageCommand.Rect.Height, precision: 2);
        Assert.Equal(0f, imageCommand.Rect.X, precision: 2);
        Assert.Equal(-10f, imageCommand.Rect.Y, precision: 2);

        // Overflows the box - must clip to the box bounds so the image does not bleed into
        // whatever content sits above/below it.
        var clip = Assert.Single(displayList.Commands.OfType<PushClipCommand>());
        Assert.Equal(40f, clip.Rect.Width, precision: 2);
        Assert.Equal(20f, clip.Rect.Height, precision: 2);
        Assert.Single(displayList.Commands.OfType<PopClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ObjectFitNoneUsesNaturalSizeCenteredInTheBox()
    {
        var document = await ParseAsync($"""
            <html><body>
                <img src="{OnePixelPng}" style="width:40px; height:20px; object-fit:none;" />
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

        Assert.Equal(1f, imageCommand.Rect.Width, precision: 2);
        Assert.Equal(1f, imageCommand.Rect.Height, precision: 2);
        Assert.Equal(19.5f, imageCommand.Rect.X, precision: 2);
        Assert.Equal(9.5f, imageCommand.Rect.Y, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_ObjectFitScaleDownPicksTheSmallerOfNoneAndContain()
    {
        var document = await ParseAsync($"""
            <html><body>
                <img src="{OnePixelPng}" style="width:40px; height:20px; object-fit:scale-down;" />
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

        // `contain` would scale the 1x1 image up to 20x20; `none` keeps it at its natural 1x1 -
        // `scale-down` picks whichever is smaller, which is `none` here.
        Assert.Equal(1f, imageCommand.Rect.Width, precision: 2);
        Assert.Equal(1f, imageCommand.Rect.Height, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_ObjectPositionMovesTheFocalPointWithinTheBox()
    {
        var document = await ParseAsync($"""
            <html><body>
                <img src="{OnePixelPng}" style="width:40px; height:20px; object-fit:contain; object-position: left top;" />
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

        // `object-position: left top` anchors the (still 20x20, contain-scaled) image to the
        // box's own top-left corner instead of centering it.
        Assert.Equal(0f, imageCommand.Rect.X, precision: 2);
        Assert.Equal(0f, imageCommand.Rect.Y, precision: 2);
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
    public async Task BuildDisplayList_BackgroundImageIsRequestedAsAnImage()
    {
        // found on www.florian-rappl.de: its server refuses images asked for as documents (406)
        var configuration = Configuration.Default
            .WithCss()
            .With(new ImageOnlyRequester(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGMAAQABAA4A4cQTmwAAAABJRU5ErkJggg==")))
            .WithDefaultLoader(new LoaderOptions { IsResourceLoadingEnabled = true });
        var document = await ParseAsync("""
            <html><body>
                <div style="width:80px; height:60px; background-image: url(http://assets.test/tile.png);"></div>
            </body></html>
            """, configuration, "http://example.test/");

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 240, ViewPortHeight = 160 });

        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(fill => fill.Paint is RenderImagePaint));
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
    public async Task BuildDisplayList_PaintsSvgImageElementFromPercentEncodedDataUri()
    {
        var document = await ParseAsync("""
            <html><body>
                <img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='240' height='160'%3E%3Crect width='240' height='160' fill='%23d5e8f5'/%3E%3Ccircle cx='75' cy='80' r='45' fill='%23526f8c'/%3E%3C/svg%3E" style="width:180px; height:120px;" />
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
        Assert.Equal(180f, imageCommand.Rect.Width);
        Assert.Equal(120f, imageCommand.Rect.Height);
        Assert.NotEmpty(imageCommand.Image.Data);
        Assert.Equal("image/png", imageCommand.Image.MimeType);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsPercentEncodedSvgImageInsideFlexItem()
    {
        var document = await ParseAsync("""
            <html>
            <head><style>
                * { box-sizing:border-box; }
                .row { display:flex; gap:20px; }
                .frame { padding:10px; border:3px solid #40556a; background:#edf3f7; }
                img { display:block; width:180px; height:120px; }
            </style></head>
            <body><div class="row"><div class="frame">
                <img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='240' height='160'%3E%3Crect width='240' height='160' fill='%23d5e8f5'/%3E%3Ccircle cx='75' cy='80' r='45' fill='%23526f8c'/%3E%3C/svg%3E" />
            </div></div></body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 960,
            ViewPortHeight = 720,
            FontSize = 16f,
        });

        var imageCommand = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        Assert.Equal(180f, imageCommand.Rect.Width);
        Assert.Equal(120f, imageCommand.Rect.Height);

        var frameBackground = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(new RenderColor(237, 243, 247))));
        Assert.Equal(146f, frameBackground.Rect.Height);
    }

    private static RenderRect FillOf(DisplayList displayList, RenderColor color) =>
        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(color))).Rect;

    private static async Task<DisplayList> LayoutAsync(string html)
    {
        var document = await ParseAsync(html);
        return new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 500, ViewPortHeight = 300, FontSize = 16f });
    }

    [Fact]
    public async Task BuildDisplayList_PercentageHeightInAnAutoHeightParentIsAuto()
    {
        // CSS 2.1, 10.5: without a definite containing block height, a percentage height is auto
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div><div style="height:50%; background-color:#112233"><div style="height:20px"></div></div></div>
            </body></html>
            """);

        Assert.Equal(20f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Height);
    }

    [Fact]
    public async Task BuildDisplayList_PercentageHeightsOfTheRootAndBodyFillTheViewport()
    {
        var displayList = await LayoutAsync("""
            <html style="height:100%"><body style="margin:0; height:100%">
                <div style="height:50%; background-color:#112233"></div>
            </body></html>
            """);

        Assert.Equal(150f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Height);
    }

    [Fact]
    public async Task BuildDisplayList_FlexContainerWithPercentageHeightInAnAutoHeightParentIsSizedByItsContent()
    {
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <header><div style="display:flex; align-items:center; height:100%; background-color:#112233"><div style="height:30px; width:40px"></div></div></header>
            </body></html>
            """);

        Assert.Equal(30f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Height);
    }

    [Fact]
    public async Task BuildDisplayList_FlexItemPercentageHeightRefersToTheContainerHeight()
    {
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div style="display:flex; height:54px"><nav style="height:100%; width:40px; background-color:#112233"></nav></div>
            </body></html>
            """);

        Assert.Equal(54f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Height);
    }

    [Fact]
    public async Task BuildDisplayList_RowFlexContainerInAColumnTakesItsHeightFromTheMainAxis()
    {
        // the row container's height is its main size in the column, not its cross size (the width)
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div style="display:flex; flex-direction:column; width:400px">
                    <div style="display:flex; background-color:#112233"><div style="height:30px; width:40px"></div></div>
                </div>
            </body></html>
            """);

        var row = FillOf(displayList, new RenderColor(0x11, 0x22, 0x33));
        Assert.Equal(400f, row.Width);
        Assert.Equal(30f, row.Height);
    }

    [Fact]
    public async Task BuildDisplayList_ColumnFlexContainerIsAsTallAsItsItems()
    {
        // it was at least as tall as it is wide, taking its width (the cross size) for its height
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <header style="display:flex; flex-direction:column; background-color:#112233"><div style="height:36px"></div><nav style="min-height:48px"></nav></header>
            </body></html>
            """);

        Assert.Equal(84f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Height);
    }

    [Theory]
    [InlineData("height:auto")]
    [InlineData("height:fit-content")]
    public async Task BuildDisplayList_FlexItemWithAnAutoHeightIsSizedByItsContent(string height)
    {
        // an authored `height: auto` counted as a definite height
        var displayList = await LayoutAsync($"""
            <html><body style="margin:0">
                <div style="display:flex; align-items:center"><div style="display:flex; flex-direction:column; flex:1; {height}; background-color:#112233"><i style="display:block; height:20px"></i></div></div>
            </body></html>
            """);

        Assert.Equal(20f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Height);
    }

    [Fact]
    public async Task BuildDisplayList_StretchedColumnFlexContainerDistributesItsHeight()
    {
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div style="display:flex; height:100px">
                    <div style="display:flex; flex-direction:column; justify-content:flex-end; width:50px"><div style="height:20px; background-color:#112233"></div></div>
                </div>
            </body></html>
            """);

        Assert.Equal(80f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Y);
    }

    [Fact]
    public async Task BuildDisplayList_GridItemSpanningAFlexibleRowDoesNotGrowTheOtherRows()
    {
        // found on MDN: a tall sidebar spanning both rows made the min-content header row as tall
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:100px 100px; grid-template-rows:min-content 1fr; grid-template-areas:'side head' 'side body'">
                    <div style="grid-area:side; height:500px"></div>
                    <div style="grid-area:head; height:40px"></div>
                    <div style="grid-area:body; height:20px; background-color:#112233"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(40f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Y);
    }

    [Fact]
    public async Task BuildDisplayList_GridTrackListFromACustomPropertyIsUsed()
    {
        // the cascaded (not computed) value would still be the unresolved var()
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div style="--columns: 100px 200px; display:grid; grid-template-columns:var(--columns)">
                    <div></div><div style="height:20px; background-color:#112233"></div>
                </div>
            </body></html>
            """);

        var second = FillOf(displayList, new RenderColor(0x11, 0x22, 0x33));
        Assert.Equal(100f, second.X);
        Assert.Equal(200f, second.Width);
    }

    [Fact]
    public async Task BuildDisplayList_AspectRatioProvidesDefiniteHeightForPercentageChild()
    {
        var document = await ParseAsync("""
            <html><head><style>
                * { box-sizing:border-box; }
                .aspect { width:360px; aspect-ratio:3 / 2; padding:15px; border:4px solid #55704e; background:#dcebd8; }
                .inner { width:50%; height:50%; padding:10px; border:2px solid #333; background:#fff; }
            </style></head><body>
                <div class="aspect"><div class="inner">50% x 50%</div></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 360,
            FontSize = 16f,
        });

        var outer = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(new RenderColor(220, 235, 216))));
        var inner = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(RenderColor.White) && command.Rect.Width < 300f));

        Assert.Equal(360f, outer.Rect.Width);
        Assert.Equal(240f, outer.Rect.Height);
        Assert.Equal(161f, inner.Rect.Width);
        Assert.Equal(101f, inner.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_AspectRatioDerivesWidthFromExplicitHeightWhenWidthIsAuto()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="height:100px; aspect-ratio:2 / 1; background-color:#112233;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 360,
            FontSize = 16f,
        });

        // Width is unset (auto) and would otherwise fill the whole container - but with an
        // explicit height and a 2:1 aspect-ratio, the used width must be derived from the height
        // (200px), not stretched to the container's own width.
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(new RenderColor(0x11, 0x22, 0x33))));

        Assert.Equal(200f, fill.Rect.Width);
        Assert.Equal(100f, fill.Rect.Height);
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
    public async Task BuildDisplayList_CodeElementFlowsInlineWithSurroundingText()
    {
        var document = await ParseAsync("""
            <html><body><div style="width:500px;">before <code>aspect-ratio</code> after</div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 600,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var textCommands = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        var code = Assert.Single(textCommands.Where(command => command.Text == "aspect-ratio"));
        Assert.All(textCommands, command => Assert.Equal(code.Y, command.Y));
    }

    [Fact]
    public async Task BuildDisplayList_UnstyledHeadingsUseBoldFontWeight()
    {
        var document = await ParseAsync("""
            <html><body><h1>First heading</h1><h2>Second heading</h2></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var headings = displayList.Commands.OfType<DrawTextCommand>()
            .Where(command => command.Text.Contains("heading", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, headings.Length);
        Assert.All(headings, command => Assert.Equal(700f, command.FontWeight));
    }

    [Fact]
    public async Task BuildDisplayList_AuthoredHeadingFontWeightOverridesDefaultBold()
    {
        var document = await ParseAsync("""
            <html><head><style>h2 { font-weight:500; }</style></head><body><h2>Weighted heading</h2></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var heading = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal(500f, heading.FontWeight);
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
    public async Task BuildDisplayList_DirectionRtlKeepsLatinWordsInOrderButAlignsRight()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="direction:rtl; width:300px;">one two three</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 400,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());

        // Left-to-right words (and the spaces between them) form one left-to-right run inside the
        // right-to-left paragraph (UAX #9), so their order is kept; the paragraph starts right.
        Assert.Equal("one two three", text.Text);
        Assert.True(text.X > 150f);
    }

    [Fact]
    public async Task BuildDisplayList_DirectionLtrDoesNotReverseWordOrder()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="direction:ltr; width:300px;">one two three</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 400,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("one two three", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_DirectionIsInheritedByChildElements()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="direction:rtl;"><p>one two three</p></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 400,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        // The inherited right-to-left direction aligns the line to the right.
        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("one two three", text.Text);
        Assert.True(text.X > 150f);
    }

    [Fact]
    public async Task BuildDisplayList_UnsetTextAlignDefaultsToRightUnderRtlDirection()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="direction:rtl; width:200px;">hi</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 400,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());

        // `text-align`'s own CSS initial value is `start`, which resolves to `right` under
        // `direction: rtl` - the text should hug the box's right edge, not its left.
        Assert.True(text.X > 100f);
    }

    [Theory]
    [InlineData("ltr", "start", false)]
    [InlineData("ltr", "end", true)]
    [InlineData("rtl", "start", true)]
    [InlineData("rtl", "end", false)]
    public async Task BuildDisplayList_TextAlignStartAndEndResolveAgainstDirection(string direction, string textAlign, bool expectRightAligned)
    {
        var document = await ParseAsync($"""
            <html><body>
                <div style="direction:{direction}; text-align:{textAlign}; width:200px;">hi</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 400,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());

        if (expectRightAligned)
        {
            Assert.True(text.X > 100f);
        }
        else
        {
            Assert.True(text.X < 20f);
        }
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
                        <td style="height:10px; background-color:#ff0000;"></td>
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
    public async Task BuildDisplayList_NestedFlexChildrenDoNotInflateParentCrossSize()
    {
        var document = await ParseAsync("""
            <html><head><style>
                * { box-sizing:border-box; }
                .outer { display:flex; width:400px; min-height:130px; padding:12px; border:2px solid #555; background:#f6f6f6; }
                .nested { display:flex; flex:1 1 auto; align-items:center; padding:10px; border:2px dashed #666; background:#fff; }
                .nested > div { width:60px; height:36px; padding:8px; border:1px solid #333; }
            </style></head><body>
                <div class="outer"><div class="nested"><div>X</div><div>Y</div><div>Z</div></div></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 500,
            ViewPortHeight = 240,
            FontSize = 16f,
        });

        var outerBackground = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(new RenderColor(246, 246, 246))));
        Assert.Equal(130f, outerBackground.Rect.Height);
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
    public async Task BuildDisplayList_AutoHeightColumnFlexDoesNotShrinkPaddedItems()
    {
        var document = await ParseAsync("""
            <html><head><style>
                * { box-sizing:border-box; }
                .nav { display:flex; flex-direction:column; gap:8px; width:180px; }
                .item { width:100%; padding:9px; border:1px solid #777; background:#fff; font-size:13px; }
            </style></head><body>
                <div class="nav"><div class="item">One</div><div class="item">Two</div><div class="item">Three</div><div class="item">Four</div><div class="item">Five</div><div class="item">Six</div></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 240, FontSize = 14f });
        var items = displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(RenderColor.White) && command.Rect.Width == 180f)
            .OrderBy(command => command.Rect.Y)
            .ToArray();

        // Each item is as tall as it is as an ordinary block: one 13px line (line-height normal,
        // 1.35) plus 18px padding and 2px border.
        Assert.Equal(6, items.Length);
        Assert.All(items, item => Assert.Equal(37.55f, item.Rect.Height, tolerance: 0.01f));
        Assert.Equal(8f, items[1].Rect.Y - (items[0].Rect.Y + items[0].Rect.Height), precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_CentersFlexItemInsideStretchedGridCard()
    {
        var document = await ParseAsync("""
            <html><head><style>
                * { box-sizing:border-box; }
                .grid { display:grid; grid-template-columns:200px; }
                .card { display:flex; align-items:center; min-height:145px; padding:15px; background:#f1e5d5; }
                .icon { width:52px; height:52px; border:2px solid #4b6175; background:#fff; }
                .copy { width:100px; height:100px; }
            </style></head><body><div class="grid"><div class="card"><div class="icon"></div><div class="copy"></div></div></div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 240 });
        var card = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(241, 229, 213))));
        var icon = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(RenderColor.White) && command.Rect.Width == 52f));

        Assert.Equal(card.Rect.Y + (card.Rect.Height - icon.Rect.Height) / 2f, icon.Rect.Y, precision: 2);
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
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(80f, childBackgrounds[1].Rect.X);
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
    public async Task BuildDisplayList_RepeatAutoFillComputesFillCountFromAvailableSpace()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:repeat(auto-fill, minmax(100px, 1fr)); gap:10px; width:220px; height:20px;">
                    <div style="height:20px; background-color:#ff0000;"></div>
                    <div style="height:20px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var childBackgrounds = displayList.Commands
            .OfType<FillRectCommand>()
            .Where(command => command.Rect.Height == 20f && command.Rect.Width < 220f)
            .OrderBy(command => command.Rect.X)
            .ToArray();

        // 220px available, 10px gap, 100px minimum track: floor((220+10)/(100+10)) = 2 tracks fit,
        // each stretched to fill the 1fr maximum (105px, splitting the 210px left after one gap).
        Assert.Equal(2, childBackgrounds.Length);
        Assert.Equal(0f, childBackgrounds[0].Rect.X);
        Assert.Equal(105f, childBackgrounds[0].Rect.Width, precision: 2);
        Assert.Equal(115f, childBackgrounds[1].Rect.X, precision: 2);
        Assert.Equal(105f, childBackgrounds[1].Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_RepeatAutoFitCollapsesEmptyTrailingTracksButAutoFillKeepsThem()
    {
        async Task<float[]> RenderColumnWidthsAsync(string repeatKeyword)
        {
            var document = await ParseAsync($$"""
                <html><body>
                    <div style="display:grid; grid-template-columns:repeat({{repeatKeyword}}, minmax(100px, 1fr)); gap:10px; width:320px; height:20px;">
                        <div style="height:20px; background-color:#ff0000;"></div>
                        <div style="height:20px; background-color:#0000ff;"></div>
                    </div>
                </body></html>
                """);

            var renderer = new HtmlRenderer();
            var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
            {
                ViewPortWidth = 400,
                ViewPortHeight = 120,
                FontSize = 16f,
            });

            return displayList.Commands
                .OfType<FillRectCommand>()
                .Where(command => command.Rect.Height == 20f && command.Rect.Width < 320f)
                .OrderBy(command => command.Rect.X)
                .Select(command => command.Rect.Width)
                .ToArray();
        }

        // 320px available, 10px gap, 100px minimum: floor((320+10)/110) = 3 tracks would fit, but
        // only 2 items are actually placed. auto-fit collapses the empty trailing track away
        // entirely, so the 2 real items split the full width between just themselves; auto-fill
        // keeps all 3 tracks (and their gaps) reserved, leaving each item's track narrower.
        var autoFitWidths = await RenderColumnWidthsAsync("auto-fit");
        var autoFillWidths = await RenderColumnWidthsAsync("auto-fill");

        Assert.Equal(2, autoFitWidths.Length);
        Assert.All(autoFitWidths, width => Assert.Equal(155f, width, precision: 2));

        Assert.Equal(2, autoFillWidths.Length);
        Assert.All(autoFillWidths, width => Assert.True(width < 155f));
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
        // First grid: the item (20px) is below the minmax() floor, so the track starts at 60px,
        // then free space grows it to its fixed 100px maximum.
        Assert.Equal(100f, secondColumnCells[0].Rect.X);
        // Second grid: the item (150px) exceeds the minmax() ceiling, so the track stops at 100px.
        Assert.Equal(100f, secondColumnCells[1].Rect.X);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("flex; flex-direction:column")]
    public async Task BuildDisplayList_AuthoredHeightLetsContentOverflow(string display)
    {
        var document = await ParseAsync($$"""
            <html><body style="margin:0">
                <div style="display:{{display}}; height:20px; background-color:#ff0000;">
                    <div style="height:30px;"></div>
                    <div style="height:30px;"></div>
                </div>
                <div style="height:10px; background-color:#0000ff;"></div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var box = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));
        var next = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 0, 255))));

        Assert.Equal(20f, box.Rect.Height, precision: 2);
        Assert.Equal(box.Rect.Y + 20f, next.Rect.Y, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_SizesAutoGridColumnToItsMaxContent()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:auto 1fr; width:300px;">
                    <div style="background-color:#ff0000;"><span style="display:inline-block; width:40px; height:10px;"></span><span style="display:inline-block; width:40px; height:10px;"></span></div>
                    <div style="height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 });
        var first = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));
        var second = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 0, 255))));

        Assert.Equal(80f, first.Rect.Width, precision: 2);
        Assert.Equal(first.Rect.X + 80f, second.Rect.X, precision: 2);
        Assert.Equal(220f, second.Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_GrowsAutoGridColumnFromMinContentIntoFreeSpace()
    {
        // min-content 40px, max-content more than 80px: the track gets the 50px left beside 250px.
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:auto 250px; width:300px;">
                    <div style="background-color:#ff0000;"><span style="display:inline-block; width:40px; height:10px;"></span> <span style="display:inline-block; width:40px; height:10px;"></span></div>
                    <div style="height:10px; background-color:#0000ff;"></div>
                </div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 });
        var first = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));

        Assert.Equal(50f, first.Rect.Width, precision: 2);
    }

    [Theory]
    [InlineData("normal", 150f)]
    [InlineData("start", 50f)]
    public async Task BuildDisplayList_StretchesAutoGridColumnsUnlessJustified(string justifyContent, float expectedWidth)
    {
        var document = await ParseAsync($$"""
            <html><body>
                <div style="display:grid; grid-template-columns:auto auto; justify-content:{{justifyContent}}; width:300px;">
                    <div style="background-color:#ff0000;"><div style="width:50px; height:10px;"></div></div>
                    <div style="background-color:#0000ff;"><div style="width:50px; height:10px;"></div></div>
                </div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 });
        var first = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));
        var second = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 0, 255))));

        Assert.Equal(expectedWidth, first.Rect.Width, precision: 2);
        Assert.Equal(expectedWidth, second.Rect.Width, precision: 2);
        Assert.Equal(first.Rect.X + expectedWidth, second.Rect.X, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_SpreadsSpanningGridItemAcrossItsAutoColumns()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:auto auto; justify-content:start;">
                    <div style="grid-column:span 2;"><div style="width:120px; height:10px;"></div></div>
                    <div style="background-color:#ff0000;"><div style="width:20px; height:10px;"></div></div>
                    <div style="background-color:#0000ff;"><div style="width:20px; height:10px;"></div></div>
                </div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 });
        var first = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));
        var second = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 0, 255))));

        Assert.Equal(60f, first.Rect.Width, precision: 2);
        Assert.Equal(60f, second.Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_SizesMinAndMaxContentGridColumns()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="display:grid; grid-template-columns:min-content max-content; width:300px;">
                    <div style="background-color:#ff0000;"><span style="display:inline-block; width:40px; height:10px;"></span> <span style="display:inline-block; width:40px; height:10px;"></span></div>
                    <div style="background-color:#0000ff;"><span style="display:inline-block; width:30px; height:10px;"></span><span style="display:inline-block; width:30px; height:10px;"></span></div>
                </div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 400, ViewPortHeight = 200 });
        var first = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));
        var second = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 0, 255))));

        // Neither keyword stretches into the 200px left over.
        Assert.Equal(40f, first.Rect.Width, precision: 2);
        Assert.Equal(60f, second.Rect.Width, precision: 2);
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
    public async Task BuildDisplayList_GridAutoRowsAreNotStretchedWithAlignContentStart()
    {
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div style="display:grid; height:40px; align-content:start">
                    <div style="height:10px"></div>
                    <div style="height:10px; background-color:#112233"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(10f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).Y);
    }

    [Fact]
    public async Task BuildDisplayList_HiddenChildrenDoNotTakeAGridCell()
    {
        // found on MDN: hidden mobile-only items pushed the menu into the wrong column
        var displayList = await LayoutAsync("""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:50px 50px">
                    <div style="display:none"></div>
                    <div style="height:10px; background-color:#112233"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(0f, FillOf(displayList, new RenderColor(0x11, 0x22, 0x33)).X);
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

        // Implicit auto rows size to their content (10px each) and are then stretched to fill the
        // grid's definite 40px height (align-content: normal, CSS Grid 1, 11.8), i.e. 20px each.
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

        // The nowrap span has no break opportunities of its own, so the whole run stays on one
        // line despite the 80px-wide container. It does not fit after "before", so it moves to
        // the next line as a unit, and "after" follows it once a break is allowed again.
        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();
        var beforeWord = Assert.Single(texts.Where(t => t.Text == "before"));
        var nowrapRun = Assert.Single(texts.Where(t => t.Text == "a long run of nowrap text"));
        var afterWord = Assert.Single(texts.Where(t => t.Text == "after"));

        Assert.True(nowrapRun.Y > beforeWord.Y);
        Assert.True(afterWord.Y > nowrapRun.Y);
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

        // The child <span> has no white-space of its own, so it inherits `pre` from its parent and
        // keeps its interior whitespace verbatim inside the parent's line box.
        var words = displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text).ToArray();
        Assert.Equal(["a    b"], words);
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
    public async Task RenderToPng_SvgFeFloodFillsTheShapeWithTheFloodColor()
    {
        var document = await ParseAsync("""
            <html><body>
                <svg width="60" height="60" viewBox="0 0 60 60">
                    <defs>
                        <filter id="f" x="0" y="0" width="100%" height="100%">
                            <feFlood flood-color="rgb(0,255,0)"></feFlood>
                        </filter>
                    </defs>
                    <circle cx="30" cy="30" r="20" fill="rgb(255,0,0)" filter="url(#f)"></circle>
                </svg>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 60, ViewPortHeight = 60 });

        using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data);
        var pixel = bitmap.GetPixel(30, 30);

        // The circle's own red fill is replaced entirely by the flood's green color.
        Assert.Equal(0, pixel.Red);
        Assert.True(pixel.Green > 200);
    }

    [Fact]
    public async Task RenderToPng_SvgFeCompositeInOperatorIntersectsTwoShapes()
    {
        var document = await ParseAsync("""
            <html><body>
                <svg width="60" height="60" viewBox="0 0 60 60">
                    <defs>
                        <filter id="f" x="0" y="0" width="100%" height="100%">
                            <feFlood flood-color="rgb(0,0,255)" result="flood"></feFlood>
                            <feComposite in="flood" in2="SourceGraphic" operator="in"></feComposite>
                        </filter>
                    </defs>
                    <circle cx="30" cy="30" r="20" fill="rgb(255,0,0)" filter="url(#f)"></circle>
                </svg>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 60, ViewPortHeight = 60 });

        using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data);

        // Inside the circle: the flood's blue "in" the circle's own coverage - blue shows through.
        var inside = bitmap.GetPixel(30, 30);
        Assert.True(inside.Blue > 200);

        // Outside the circle (but still within the viewport): neither the flood nor the circle
        // has any coverage there, so it stays the page's own white background.
        var outside = bitmap.GetPixel(2, 2);
        Assert.Equal(255, outside.Red);
        Assert.Equal(255, outside.Green);
        Assert.Equal(255, outside.Blue);
    }

    [Fact]
    public async Task RenderToPng_SvgFeMorphologyDilateGrowsTheShape()
    {
        async Task<int> MeasureShapeWidthAsync(string? filterAttribute)
        {
            var document = await ParseAsync($"""
                <html><body>
                    <svg width="100" height="60" viewBox="0 0 100 60">
                        <defs>
                            <filter id="f" x="-50%" y="-50%" width="200%" height="200%">
                                <feMorphology operator="dilate" radius="8"></feMorphology>
                            </filter>
                        </defs>
                        <rect x="40" y="20" width="20" height="20" fill="rgb(0,0,0)"{filterAttribute}></rect>
                    </svg>
                </body></html>
                """);

            var renderer = new HtmlRenderer();
            var image = renderer.RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 100, ViewPortHeight = 60 });
            using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data);

            var width = 0;
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, 30).Red < 128)
                {
                    width++;
                }
            }

            return width;
        }

        var plainWidth = await MeasureShapeWidthAsync(null);
        var dilatedWidth = await MeasureShapeWidthAsync(" filter=\"url(#f)\"");

        // A 20px-wide rect dilated by an 8px radius grows to roughly 20+2*8=36px wide.
        Assert.True(dilatedWidth > plainWidth);
    }

    [Fact]
    public async Task RenderToPng_SvgFeComponentTransferLinearScalesAChannel()
    {
        var document = await ParseAsync("""
            <html><body>
                <svg width="40" height="40" viewBox="0 0 40 40">
                    <defs>
                        <filter id="f" x="0" y="0" width="100%" height="100%">
                            <feComponentTransfer>
                                <feFuncR type="linear" slope="0.5" intercept="0"></feFuncR>
                            </feComponentTransfer>
                        </filter>
                    </defs>
                    <rect x="0" y="0" width="40" height="40" fill="rgb(200,0,0)" filter="url(#f)"></rect>
                </svg>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var image = renderer.RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 40, ViewPortHeight = 40 });

        using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data);
        var pixel = bitmap.GetPixel(20, 20);

        // slope=0.5 halves the red channel in linearRGB, the default color-interpolation-filters:
        // 200 is 0.578 linear, halved 0.289, back in sRGB ~146. Green/blue are untouched (still 0).
        Assert.InRange(pixel.Red, 140, 152);
        Assert.Equal(0, pixel.Green);
        Assert.Equal(0, pixel.Blue);
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
    public async Task BuildDisplayList_PlainSemanticInlineElementSharesTheLineWithSiblingText()
    {
        // A plain, zero-CSS <b> (and any other standard semantic inline tag) computes an empty
        // `display` from AngleSharp.Css - `inline` is CSS's own initial value, so nothing ever
        // needs to set it explicitly - which a previous version of this renderer's `display`-unset
        // fallback misread as "block-level" for every tag except <code>. That, in turn, made the
        // parent's "merge onto a shared line" detection never trigger, so "World" rendered on its
        // own separate line below "Hello" instead of continuing on the same one beside it.
        var document = await ParseAsync("""
            <html><body>
                <p>Hello <b>World</b></p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var hello = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Hello"));
        var world = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "World"));

        Assert.Equal(hello.Y, world.Y);
        Assert.True(world.X > hello.X);
    }

    [Theory]
    [InlineData("span")]
    [InlineData("strong")]
    [InlineData("em")]
    [InlineData("i")]
    [InlineData("u")]
    public async Task BuildDisplayList_OtherPlainSemanticInlineTagsAlsoShareTheLineWithSiblingText(string tagName)
    {
        var document = await ParseAsync($"""
            <html><body>
                <p>Hello <{tagName}>World</{tagName}></p>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var hello = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Hello"));
        var world = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "World"));

        Assert.Equal(hello.Y, world.Y);
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
    public async Task BuildDisplayList_AutoSizedInlineBlockSiblingsShrinkToFitAndShareALine()
    {
        // An inline-block with an auto width shrinks to fit its content and flows next to its
        // siblings like any other inline content.
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

        Assert.Equal(redFill.Rect.Y, greenFill.Rect.Y);
        Assert.Equal(redFill.Rect.X + redFill.Rect.Width, greenFill.Rect.X, precision: 2);
        Assert.True(redFill.Rect.Width < 30f);
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
    public async Task BuildDisplayList_FloatsLeftOverTheFollowingBlocksBox()
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

        // An ordinary block's own box ignores the float (it sits underneath it); only its line
        // boxes move aside, see BuildDisplayList_WithoutClearABlockFlowsBesideTheFloat.
        Assert.Equal(0f, normalBox.Rect.X);
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
    public async Task BuildDisplayList_PositionsAbsoluteBoxFromRightAndBottomEdges()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:200px; height:100px; background:#eeeeee;">
                    <div style="position:absolute; right:10px; bottom:5px; width:40px; height:20px; background:#ff0000;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 180 });
        var child = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));

        Assert.Equal(150f, child.Rect.X);
        Assert.Equal(75f, child.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_GridAutoRowUsesDeepItemHeightAndStretchesSiblings()
    {
        var document = await ParseAsync("""
            <html><head><style>
                * { box-sizing:border-box; }
                .grid { display:grid; grid-template-columns:100px 200px 100px; gap:10px; }
                .side { background-color:#ff0000; }
                .hero { min-height:180px; margin-bottom:18px; }
                .cards { display:grid; grid-template-columns:repeat(2, 1fr); gap:14px; }
                .card { min-height:145px; }
                .right { background-color:#00ff00; }
                .footer { height:40px; background-color:#0000ff; }
            </style></head><body>
                <div class="grid">
                    <aside class="side"></aside>
                    <section><div class="hero"></div><div class="cards"><div class="card"></div><div class="card"></div><div class="card"></div><div class="card"></div></div></section>
                    <aside class="right"></aside>
                </div>
                <footer class="footer"></footer>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 500, ViewPortHeight = 700 });
        var side = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(255, 0, 0))));
        var right = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 255, 0))));
        var footer = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(command => command.Color.Equals(new RenderColor(0, 0, 255))));

        Assert.Equal(502f, side.Rect.Height);
        Assert.Equal(502f, right.Rect.Height);
        Assert.Equal(502f, footer.Rect.Y);
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
    public async Task BuildDisplayList_PaintsRelativeZIndexAboveAbsoluteSiblingWhenHigher()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:200px; height:80px; background-color:#eeeeee;">
                    <div style="position:absolute; left:10px; top:10px; width:50px; height:30px; background-color:#0000ff; z-index:1;"></div>
                    <div style="position:relative; left:20px; top:10px; width:80px; height:40px; background-color:#8b5b25; z-index:2;"></div>
                    <div style="position:absolute; left:0; top:0; width:20px; height:20px; background-color:#ff0000; z-index:-1;"></div>
                </div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 240,
            ViewPortHeight = 160,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var redIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(255, 0, 0)));
        var blueIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(0, 0, 255)));
        var brownIndex = Array.FindIndex(fills, f => f.Color.Equals(new RenderColor(139, 91, 37)));

        Assert.True(redIndex >= 0);
        Assert.True(blueIndex >= 0);
        Assert.True(brownIndex >= 0);
        Assert.True(redIndex < blueIndex);
        Assert.True(brownIndex > blueIndex);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsNegativeZIndexBehindOwnBackgroundWhenParentIsNotAStackingContext()
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

        // When the parent does not establish a stacking context (z-index: auto on the positioned
        // box), a negative-z-index descendant is painted behind that parent box's background and
        // border, matching Chrome and the CSS stacking-order rules. The negative child only paints
        // on top of the parent's background after the parent itself becomes a stacking context with
        // an explicit z-index (or other stacking-context trigger).
        Assert.True(redIndex < greenIndex);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsNegativeZIndexAfterOwnBackgroundWhenParentIsAStackingContext()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; z-index:0; width:120px; height:30px; background-color:#00ff00;">
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
    public async Task BuildDisplayList_PaintsHigherZIndexAboveRelativeSiblingInMixedStackingScene()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="position:relative; width:700px; height:330px;">
                    <div style="position:absolute; left:40px; top:40px; width:300px; height:180px; background-color:#d7e8f6; z-index:1;">base</div>
                    <div style="position:relative; left:70px; top:35px; width:240px; height:130px; background-color:#f0dfc7; z-index:2;">relative</div>
                    <div style="position:absolute; left:210px; top:100px; width:220px; height:120px; background-color:#eadcf0; z-index:4;">higher</div>
                    <div style="position:absolute; left:340px; top:150px; width:220px; height:120px; background-color:#dcebd8; z-index:3;">middle</div>
                    <div style="position:absolute; left:500px; top:40px; width:180px; height:100px; background-color:#f0d7d7; z-index:-1;">negative</div>
                </div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 960,
            ViewPortHeight = 420,
        });

        var fills = displayList.Commands.OfType<FillRectCommand>().ToArray();
        var relativeIndex = Array.FindIndex(fills, fill => fill.Color.Equals(new RenderColor(240, 223, 199)));
        var higherIndex = Array.FindIndex(fills, fill => fill.Color.Equals(new RenderColor(234, 220, 240)));

        Assert.True(relativeIndex >= 0);
        Assert.True(higherIndex >= 0);
        Assert.True(higherIndex > relativeIndex, "The z-index 4 box must paint above the z-index 2 relative box.");
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
    public async Task BuildDisplayList_ClipsMixedWidthBorderToAsymmetricCornerRadii()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:180px; height:100px; border-width:8px 3px 12px 5px; border-style:solid; border-color:#6b4b73 #3c607c #85503c #52704d; border-radius:8px 30px 50px 18px;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
        });

        var rings = displayList.Commands.OfType<FillRingCommand>().ToArray();
        Assert.Equal(4, rings.Length);
        Assert.All(rings, ring =>
        {
            Assert.Equal(8f, ring.OuterRadii.TopLeftX);
            Assert.Equal(30f, ring.OuterRadii.TopRightX);
            Assert.Equal(50f, ring.OuterRadii.BottomRightX);
            Assert.Equal(18f, ring.OuterRadii.BottomLeftX);
        });

        // Each edge keeps its own color: top, right, bottom, left.
        Assert.Equal(
            [new RenderColor(107, 75, 115), new RenderColor(60, 96, 124), new RenderColor(133, 80, 60), new RenderColor(82, 112, 77)],
            rings.Select(ring => ring.Color));
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
        // against its 80px height (10% = 8px) - per spec.
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
    public async Task BuildDisplayList_PaintsMixedWidthRoundedBorderAsClippedRings()
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

        // Mixed edge widths have no single stroke width, so each edge is the ring between the
        // outer and the inner rounded rectangle, clipped to that edge's own trapezoid.
        Assert.Empty(displayList.Commands.OfType<StrokeRoundedRectCommand>());
        Assert.Equal(4, displayList.Commands.OfType<PushClipShapeCommand>().Count());

        var rings = displayList.Commands.OfType<FillRingCommand>().ToArray();
        Assert.Equal(4, rings.Length);
        Assert.All(rings, ring => Assert.Equal(new RenderColor(0, 0, 255), ring.Color));

        // The inner corner follows the outer radius minus the adjacent border widths.
        Assert.Equal(12f, rings[0].OuterRadii.TopRightX);
        Assert.Equal(6f, rings[0].InnerRadii.TopRightX);
        Assert.Equal(10f, rings[0].InnerRadii.TopRightY);
        Assert.Equal(new RenderRect(2f, 2f, 100f, 60f), rings[0].Inner);
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

    [Fact]
    public async Task BuildDisplayList_AnimationPlayStatePausedFreezesProgressAndResumesFromTheSamePosition()
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
                #target { width: 40px; height: 40px; background-color: rgb(0, 0, 255); animation: fade 1s linear; }
            </style></head><body>
                <div id="target"></div>
            </body></html>
            """, configuration);
        var harness = document.Context.GetDomHarness();
        var target = document.GetElementById("target")!;
        var renderer = new HtmlRenderer();

        float CurrentAlpha()
        {
            var displayList = renderer.BuildDisplayList(document, renderDevice);
            var push = displayList.Commands.OfType<PushOpacityCommand>().SingleOrDefault();
            return push?.Alpha ?? 1f;
        }

        harness.AdvanceTime(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0.5f, CurrentAlpha(), precision: 2);

        // Pausing must freeze progress exactly where it was - further time passing while paused
        // must not advance (or, since the animation would otherwise have finished with no
        // fill-mode, revert) the interpolated value at all.
        target.SetAttribute("style", "animation-play-state: paused;");
        harness.AdvanceTime(TimeSpan.FromMilliseconds(2000));
        Assert.Equal(0.5f, CurrentAlpha(), precision: 2);

        // Resuming must continue from that same 50% position, not restart from 0% nor jump to
        // wherever the un-paused virtual clock would otherwise place it.
        target.SetAttribute("style", "animation-play-state: running;");
        harness.AdvanceTime(TimeSpan.FromMilliseconds(250));
        Assert.Equal(0.75f, CurrentAlpha(), precision: 2);

        harness.AdvanceTime(TimeSpan.FromMilliseconds(250));
        Assert.Equal(1f, CurrentAlpha(), precision: 2);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("url")]
    [InlineData("email")]
    [InlineData("search")]
    [InlineData("tel")]
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
    public async Task BuildDisplayList_UnfocusedTextAreaPaintsNoCaret()
    {
        var document = await ParseAsync("""<html><body><textarea>Hello</textarea></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        Assert.DoesNotContain(displayList.Commands, c => c is FillRectCommand f && f.Rect.Width == 1.5f);
    }

    [Fact]
    public async Task BuildDisplayList_FocusedTextAreaPaintsACaretOnTheLastWrappedLine()
    {
        var document = await ParseAsync("""<html><body><textarea id="target" style="width:100px;">one two three four five</textarea></body></html>""");
        var target = (IHtmlElement)document.GetElementById("target")!;
        target.DoFocus();

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var caret = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 1.5f));
        var textCommands = displayList.Commands.OfType<DrawTextCommand>().OrderBy(t => t.Y).ToList();

        Assert.True(textCommands.Count > 1);

        // The caret sits on the last wrapped line, below where the first line was painted - not
        // pinned to the first line's own baseline.
        Assert.True(caret.Rect.Y > textCommands[0].Y);
    }

    [Fact]
    public async Task BuildDisplayList_FocusedEmptyTextAreaPaintsACaretAtItsContentEdge()
    {
        var document = await ParseAsync("""<html><body><textarea id="target"></textarea></body></html>""");
        var target = (IHtmlElement)document.GetElementById("target")!;
        target.DoFocus();

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 1.5f));
    }

    [Fact]
    public async Task BuildDisplayList_FocusedTextAreaWithTrailingNewlineMovesCaretToANewEmptyLine()
    {
        var document = await ParseAsync("""
            <html><body><textarea id="target">Hi
            </textarea></body></html>
            """);
        var target = (IHtmlElement)document.GetElementById("target")!;
        target.DoFocus();

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var caret = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 1.5f));
        var hiText = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Hi"));

        // The trailing newline pushes the caret down onto its own, empty second line rather than
        // leaving it right after "Hi" on the first line.
        Assert.True(caret.Rect.Y > hiText.Y);
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
    public async Task BuildDisplayList_RangeInputPaintsATrackAndAThumbPositionedByValue()
    {
        var document = await ParseAsync("""<html><body><input type="range" min="0" max="100" value="25" style="width:100px;" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        // The track: a thin, rounded gray bar spanning the control's own width.
        var track = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Rect.Width == 100f));
        Assert.False(track.Radii.IsZero);

        // The thumb: a small rounded square, its center at 25% along the track for value=25 out
        // of a 0-100 range.
        var thumb = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Rect.Width < 100f && !command.Rect.Width.Equals(track.Rect.Width)));
        var thumbCenterX = thumb.Rect.X + (thumb.Rect.Width / 2f);
        Assert.Equal(25f, thumbCenterX, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_RangeInputDefaultsToTheMidpointWhenValueIsUnset()
    {
        var document = await ParseAsync("""<html><body><input type="range" style="width:100px;" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var track = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(c => c.Rect.Width == 100f));
        var thumb = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(c => c.Rect.Width < 100f && !c.Rect.Width.Equals(track.Rect.Width)));
        var thumbCenterX = thumb.Rect.X + (thumb.Rect.Width / 2f);

        // No `value` authored - defaults to the (min+max)/2 midpoint (0-100 default range), so
        // the thumb sits at 50% along the track.
        Assert.Equal(50f, thumbCenterX, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_FileInputGetsButtonLikeChromeAndAFixedLabel()
    {
        var document = await ParseAsync("""<html><body><input type="file" /></body></html>""");

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 100,
            FontSize = 16f,
        });

        var label = Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == "Choose File"));
        Assert.NotNull(label);

        // Button-like default chrome: a gray background box, matching FormControlKind.Button.
        var background = Assert.Single(displayList.Commands.OfType<FillRectCommand>()
            .Where(command => command.Color.Equals(new RenderColor(0xe8, 0xe8, 0xe8))));
        Assert.True(background.Rect.Width > 0f);
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

    [Theory]
    // found on GitHub: icon buttons showed a placeholder "Button" label, flex buttons their label twice
    [InlineData("<button><span>Save</span></button>", "Save")]
    [InlineData("<button><svg width=\"10\" height=\"10\"></svg>Save</button>", "Save")]
    [InlineData("<button style=\"display:flex\">Save</button>", "Save")]
    [InlineData("<button style=\"display:grid\">Save</button>", "Save")]
    public async Task BuildDisplayList_ButtonContentIsDrawnOnce(string markup, string label)
    {
        var document = await ParseAsync($"<html><body>{markup}</body></html>");

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100 });
        var texts = displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text.Trim()).Where(t => t.Length > 0).ToList();

        Assert.Equal([label], texts);
    }

    [Fact]
    public async Task BuildDisplayList_ButtonGeneratedContentIsDrawn()
    {
        // found on MDN: the chevrons of its menu buttons (:after) only took space
        var document = await ParseAsync("""
            <html><head><style>button::after { content: ""; display: inline-block; width: 10px; height: 10px; background-color: #ff0000 }</style></head>
            <body><button>Save</button></body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100 });
        var texts = displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text.Trim()).Where(t => t.Length > 0).ToList();

        Assert.Equal(["Save"], texts);
        Assert.Equal(10f, SingleFill(displayList, Red).Rect.Width, precision: 2);
    }

    [Theory]
    // found on MDN, whose icons are boxes with background-color: currentcolor and a mask
    [InlineData("color:#ff0000; background-color:currentcolor")]
    [InlineData("color:#ff0000; background-color:currentColor")]
    [InlineData("color:#ff0000; background-color:CURRENTCOLOR")]
    public async Task BuildDisplayList_CurrentColorIsTheElementsColor(string style)
    {
        var document = await ParseAsync($"""
            <html><body style="margin:0; color:#0000ff">
                <div style="width:20px; height:10px; {style}"></div>
            </body></html>
            """);

        Assert.Equal(20f, SingleFill(BuildDisplayList(document), Red).Rect.Width, precision: 2);
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

    [Theory]
    // custom properties are only substituted in the computed value
    [InlineData("<style>.box { --x: 15px; transform: translate(var(--x), 8px) }</style><div class=\"box\" style=\"width:40px; height:20px\"></div>", 15f, 8f)]
    [InlineData("<div style=\"--x: 15px; width:40px; height:20px; transform: translate(var(--x), 8px)\"></div>", 15f, 8f)]
    [InlineData("<style>.box { --x: 50%; transform: translateX(var(--x)) }</style><div class=\"box\" style=\"width:40px; height:20px\"></div>", 20f, 0f)]
    public async Task BuildDisplayList_TransformWithCustomPropertiesIsApplied(string body, float x, float y)
    {
        var document = await ParseAsync($"<html><body>{body}</body></html>");
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var push = Assert.Single(displayList.Commands.OfType<PushTransformCommand>());
        Assert.Equal(x, push.Transform.E, precision: 3);
        Assert.Equal(y, push.Transform.F, precision: 3);
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
    public async Task BuildDisplayList_StylesheetCenterTransformOriginUsesOwnBox()
    {
        var explicitCenter = await ParseAsync("""
            <html>
            <head><style>.target { position:absolute; left:300px; top:55px; width:200px; height:90px; transform:rotate(-7deg) scale(1.08); transform-origin:center center; }</style></head>
            <body><div class="target"></div></body>
            </html>
            """);
        var defaultCenter = await ParseAsync("""
            <html>
            <head><style>.target { position:absolute; left:300px; top:55px; width:200px; height:90px; transform:rotate(-7deg) scale(1.08); }</style></head>
            <body><div class="target"></div></body>
            </html>
            """);

        var renderer = new HtmlRenderer();
        var device = new DefaultRenderDevice { ViewPortWidth = 960, ViewPortHeight = 720 };
        var explicitTransform = Assert.Single(renderer.BuildDisplayList(explicitCenter, device).Commands.OfType<PushTransformCommand>()).Transform;
        var defaultTransform = Assert.Single(renderer.BuildDisplayList(defaultCenter, device).Commands.OfType<PushTransformCommand>()).Transform;

        Assert.Equal(defaultTransform.A, explicitTransform.A, precision: 3);
        Assert.Equal(defaultTransform.B, explicitTransform.B, precision: 3);
        Assert.Equal(defaultTransform.C, explicitTransform.C, precision: 3);
        Assert.Equal(defaultTransform.D, explicitTransform.D, precision: 3);
        Assert.Equal(defaultTransform.E, explicitTransform.E, precision: 2);
        Assert.Equal(defaultTransform.F, explicitTransform.F, precision: 2);
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
    public async Task BuildDisplayList_RotateYProducesAnOrthographicHorizontalSquash()
    {
        // Without a `perspective` in effect, this renderer flattens a 3D transform to 2D by
        // evaluating its already-general matrix at z=0 and discarding the z-output - which for
        // rotateY(theta) (rotation around the vertical axis) works out to a pure horizontal scale
        // by cos(theta), with no foreshortening (matching how a browser renders 3D transforms with
        // no perspective ancestor: an orthographic, not a perspective, projection).
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: rotateY(60deg);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var t = Assert.Single(displayList.Commands.OfType<PushTransformCommand>()).Transform;

        Assert.Equal(0.5f, t.A, precision: 2);
        Assert.Equal(0f, t.B, precision: 2);
        Assert.Equal(0f, t.C, precision: 2);
        Assert.Equal(1f, t.D, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_RotateXProducesAnOrthographicVerticalSquash()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: rotateX(60deg);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var t = Assert.Single(displayList.Commands.OfType<PushTransformCommand>()).Transform;

        Assert.Equal(1f, t.A, precision: 2);
        Assert.Equal(0f, t.B, precision: 2);
        Assert.Equal(0f, t.C, precision: 2);
        Assert.Equal(0.5f, t.D, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_TranslateZHasNoVisibleEffectWithoutPerspective()
    {
        // translateZ alone moves an element only along the axis perpendicular to the screen -
        // with no perspective in effect to project that depth back onto the 2D plane, it has
        // (correctly, matching a real browser with no `perspective` ancestor) no visible effect.
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: translateZ(200px);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });

        Assert.Empty(displayList.Commands.OfType<PushTransformCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_Translate3DAppliesItsXAndYComponents()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: translate3d(12px, 6px, 200px);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var t = Assert.Single(displayList.Commands.OfType<PushTransformCommand>()).Transform;

        Assert.Equal(new RenderTransform2D(1f, 0f, 0f, 1f, 12f, 6f), t);
    }

    [Fact]
    public async Task BuildDisplayList_Scale3DAppliesItsXAndYComponents()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:40px; height:20px; transform: scale3d(2, 3, 4);"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        var t = Assert.Single(displayList.Commands.OfType<PushTransformCommand>()).Transform;

        Assert.Equal(2f, t.A, precision: 2);
        Assert.Equal(0f, t.B, precision: 2);
        Assert.Equal(0f, t.C, precision: 2);
        Assert.Equal(3f, t.D, precision: 2);
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
    public async Task BuildDisplayList_ClipPathCircleClipsAnOrdinaryDivToACircleShape()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; clip-path: circle(20px at 50% 50%); background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });

        var push = Assert.Single(displayList.Commands.OfType<PushClipShapeCommand>());
        var circle = Assert.IsType<RenderClipCircle>(push.Shape);

        Assert.Equal(50f, circle.CenterX, precision: 1);
        Assert.Equal(30f, circle.CenterY, precision: 1);
        Assert.Equal(20f, circle.Radius, precision: 1);
        Assert.Single(displayList.Commands.OfType<PopClipCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ClipPathCircleDefaultsToCenteredClosestSideRadius()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; clip-path: circle(); background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });

        var push = Assert.Single(displayList.Commands.OfType<PushClipShapeCommand>());
        var circle = Assert.IsType<RenderClipCircle>(push.Shape);

        // No "at" clause -> centered (50,30); no explicit radius -> closest-side, which for a
        // 100x60 box centered at (50,30) is the vertical distance to the nearest edge (30px).
        Assert.Equal(50f, circle.CenterX, precision: 1);
        Assert.Equal(30f, circle.CenterY, precision: 1);
        Assert.Equal(30f, circle.Radius, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_ClipPathEllipseResolvesIndependentXAndYRadii()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; clip-path: ellipse(40px 20px at center); background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });

        var push = Assert.Single(displayList.Commands.OfType<PushClipShapeCommand>());
        var ellipse = Assert.IsType<RenderClipEllipse>(push.Shape);

        Assert.Equal(50f, ellipse.CenterX, precision: 1);
        Assert.Equal(30f, ellipse.CenterY, precision: 1);
        Assert.Equal(40f, ellipse.RadiusX, precision: 1);
        Assert.Equal(20f, ellipse.RadiusY, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_ClipPathInsetShrinksTheClipRectFromEachEdge()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; clip-path: inset(10px 20px 5px 15px); background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });

        var push = Assert.Single(displayList.Commands.OfType<PushClipShapeCommand>());
        var inset = Assert.IsType<RenderClipInset>(push.Shape);

        Assert.Equal(15f, inset.Rect.X, precision: 1);
        Assert.Equal(10f, inset.Rect.Y, precision: 1);
        Assert.Equal(65f, inset.Rect.Width, precision: 1);
        Assert.Equal(45f, inset.Rect.Height, precision: 1);
    }

    [Fact]
    public async Task RenderToPng_GradientToTransparentDoesNotDarken()
    {
        // found on MDN: the sidebar header faded white into `#0000` (transparent black) as grey
        var document = await ParseAsync("""
            <html><body style="margin:0; background-color:#ffffff">
                <div style="width:100px; height:100px; background:linear-gradient(to bottom, #ffffff, #0000)"></div>
            </body></html>
            """);

        var image = new HtmlRenderer().RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 100, ViewPortHeight = 100 });
        using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data);
        var middle = bitmap.GetPixel(50, 50);

        Assert.True(middle.Red > 250 && middle.Green > 250 && middle.Blue > 250, middle.ToString());
    }

    [Theory]
    [InlineData("#ff0000 0 70%, #0000ff 100%")]
    [InlineData("#ff0000 0 calc(100% - 2rem), #0000ff 100%")]
    public async Task RenderToPng_GradientStopsAreResolvedAlongTheGradientLine(string stops)
    {
        // the line of `to bottom` is as long as the box is tall, not as its diagonal; a stop with
        // two positions or a calc() position keeps its color between them
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <div style="width:300px; height:100px; background:linear-gradient(to bottom, {stops})"></div>
            </body></html>
            """);

        var image = new HtmlRenderer().RenderToPng(document, new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100 });
        using var bitmap = SkiaSharp.SKBitmap.Decode(image.Data);
        var top = bitmap.GetPixel(150, 2);
        var solid = bitmap.GetPixel(150, 60);
        var bottom = bitmap.GetPixel(150, 98);

        Assert.True(top.Red > 250 && top.Blue < 5, top.ToString());
        Assert.True(solid.Red > 250 && solid.Blue < 5, solid.ToString());
        Assert.True(bottom.Blue > 230 && bottom.Red < 25, bottom.ToString());
    }

    [Fact]
    public async Task BuildDisplayList_ClipPathIsPlacedAtItsBox()
    {
        // found on GitHub: a loading placeholder below the page's top clipped itself away
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="margin:30px 0 0 40px; width:100px; height:60px; clip-path: inset(10px 20px 5px 15px); background-color:#ff0000;"></div>
            </body></html>
            """);

        var push = Assert.Single(BuildDisplayList(document).Commands.OfType<PushClipShapeCommand>());
        var inset = Assert.IsType<RenderClipInset>(push.Shape);

        Assert.Equal(55f, inset.Rect.X, precision: 1);
        Assert.Equal(40f, inset.Rect.Y, precision: 1);
        Assert.Equal(65f, inset.Rect.Width, precision: 1);
        Assert.Equal(45f, inset.Rect.Height, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_NoBreakSpaceIsNoCollapsibleWhiteSpace()
    {
        // found on GitHub: a loading placeholder holding just a no-break space had no height
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="width:100px; background-color:#ff0000">&nbsp;</div>
            </body></html>
            """);

        Assert.True(SingleFill(BuildDisplayList(document), Red).Rect.Height > 10f);
    }

    [Fact]
    public async Task BuildDisplayList_ClipPathPolygonProducesVerticesResolvedAgainstTheBox()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; height:60px; clip-path: polygon(0 0, 100% 0, 50% 100%); background-color:#ff0000;"></div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });

        var push = Assert.Single(displayList.Commands.OfType<PushClipShapeCommand>());
        var polygon = Assert.IsType<RenderClipPolygon>(push.Shape);

        Assert.Equal(3, polygon.Points.Count);
        Assert.Equal((0f, 0f), polygon.Points[0]);
        Assert.Equal((100f, 0f), polygon.Points[1]);
        Assert.Equal((50f, 60f), polygon.Points[2]);
    }

    [Fact]
    public async Task BuildDisplayList_NoClipPathProducesNoClipShapeCommands()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:10px; height:10px;"></div>
            </body></html>
            """);
        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200 });
        Assert.Empty(displayList.Commands.OfType<PushClipShapeCommand>());
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
    public async Task BuildDisplayList_WebkitLineClampTruncatesToNLinesWithEllipsisOnTheLast()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:60px; overflow:hidden; -webkit-line-clamp:2;">one two three four five six seven eight</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        var textCommands = displayList.Commands.OfType<DrawTextCommand>().OrderBy(t => t.Y).ToList();

        // Clamped to exactly 2 lines - the remaining wrapped lines are dropped entirely, not just
        // clipped/hidden.
        Assert.Equal(2, textCommands.Count);
        Assert.EndsWith("…", textCommands[1].Text);
    }

    [Fact]
    public async Task BuildDisplayList_WebkitLineClampFromAStyleElementFollowsTheMatchingElements()
    {
        var document = await ParseAsync("""
            <html><head><style>.clamp { -webkit-line-clamp: 2 }</style></head><body>
                <div id="a" class="clamp" style="width:60px; overflow:hidden;">one two three four five six seven eight</div>
                <div id="b" style="width:60px; overflow:hidden;">one two three four five six seven eight</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var device = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 400, FontSize = 16f };
        var before = renderer.BuildDisplayList(document, device).Commands.OfType<DrawTextCommand>().Count();
        document.GetElementById("b")!.ClassList.Add("clamp");
        var after = renderer.BuildDisplayList(document, device).Commands.OfType<DrawTextCommand>().Count();

        // "a" is clamped to 2 lines in both renders; "b" only once the class reaches it
        Assert.True(before > 4);
        Assert.Equal(4, after);
    }

    [Fact]
    public async Task BuildDisplayList_WebkitLineClampHasNoEffectWhenTextFitsWithinTheLimit()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:200px; overflow:hidden; -webkit-line-clamp:3;">Short text</div>
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
        Assert.Equal("Short text", text.Text);
        Assert.DoesNotContain("…", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_WebkitLineClampHasNoEffectWithoutOverflowClipping()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:60px; -webkit-line-clamp:2;">one two three four five six seven eight</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 120,
            FontSize = 16f,
        });

        // No `overflow: hidden` - the clamp must not apply, matching the same "text-overflow has no
        // effect without clipping" precedent already established for the single-line case.
        var textCommands = displayList.Commands.OfType<DrawTextCommand>().ToList();
        Assert.True(textCommands.Count > 2);
        Assert.DoesNotContain(textCommands, t => t.Text.Contains('…'));
    }

    [Fact]
    public async Task BuildDisplayList_WebkitLineClampWorksAcrossDifferentWidthsAndLineHeights()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:100px; line-height:2; overflow:hidden; -webkit-line-clamp:1;">one two three four five six</div>
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
    public async Task BuildDisplayList_OverflowWrapAnywhereBreaksOverlongSlashTokenBeforeOverflow()
    {
        var document = await ParseAsync("""
            <html><body><div style="width:70px; overflow-wrap:anywhere;">ABCDEFGHIJKLMNOPQRSTUVWXYZ/path/to/resource</div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 240, FontSize = 16f });
        var lines = displayList.Commands.OfType<DrawTextCommand>().Select(command => command.Text).ToArray();

        Assert.True(lines.Length > 3, $"Expected more than three wrapped lines, got: {string.Join(" | ", lines)}");
        Assert.DoesNotContain(lines, line => line.StartsWith("ABCDEFGHIJKLMNOPQRSTUVWXYZ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("8/21/2026", "8/21/2026")]
    [InlineData("2024-2026", "2024-2026")]
    [InlineData("client/server", "client/|server")]
    public async Task BuildDisplayList_OverlongWordsBreakAfterSlashesAndHyphensButNotBeforeDigits(string word, string expected)
    {
        // found on www.florian-rappl.de: a date badge a hair too narrow showed "8/ 21/ 2026"
        var document = await ParseAsync($"""
            <html><body style="margin:0"><div style="width:20px">{word}</div><div style="width:20px">{word}</div></body></html>
            """);

        var lines = BuildDisplayList(document).Commands.OfType<DrawTextCommand>().Select(command => command.Text).ToArray();

        Assert.Equal(expected + "|" + expected, string.Join("|", lines));
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

    [Fact]
    public async Task BuildDisplayList_RendersBeforePseudoElementContentInlineBeforeRealContent()
    {
        var document = await ParseAsync("""
            <html><head><style>#target::before { content: "PREFIX-"; }</style></head>
            <body><div id="target">middle</div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.Equal(2, texts.Length);
        Assert.Equal("PREFIX-", texts[0].Text);
        Assert.Equal("middle", texts[1].Text);
        Assert.Equal(0f, texts[0].X);
        Assert.Equal(texts[0].Y, texts[1].Y);
        Assert.True(texts[1].X > texts[0].X);
    }

    [Fact]
    public async Task BuildDisplayList_RendersAfterPseudoElementContentInlineAfterRealContent()
    {
        var document = await ParseAsync("""
            <html><head><style>#target::after { content: "-SUFFIX"; }</style></head>
            <body><div id="target">middle</div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.Equal(2, texts.Length);
        Assert.Equal("middle", texts[0].Text);
        Assert.Equal("-SUFFIX", texts[1].Text);
        Assert.True(texts[1].X > texts[0].X);
    }

    [Fact]
    public async Task BuildDisplayList_PseudoElementWithNoContentRendersNothingExtra()
    {
        var document = await ParseAsync("""
            <html><body><div id="target">middle</div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var text = Assert.Single(displayList.Commands.OfType<DrawTextCommand>());
        Assert.Equal("middle", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_PseudoElementContentAttrResolvesHostAttribute()
    {
        var document = await ParseAsync("""
            <html><head><style>#target::before { content: attr(data-label); }</style></head>
            <body><div id="target" data-label="Hello">middle</div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.Equal(2, texts.Length);
        Assert.Equal("Hello", texts[0].Text);
    }

    [Fact]
    public async Task BuildDisplayList_CounterIncrementNumbersSiblingElementsSequentially()
    {
        var document = await ParseAsync("""
            <html><head><style>
                body { counter-reset: item; }
                .item { counter-increment: item; }
                .item::before { content: counter(item) ". "; }
            </style></head><body>
                <div class="item">Alpha</div>
                <div class="item">Bravo</div>
                <div class="item">Charlie</div>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().OrderBy(t => t.Y).Select(t => t.Text).ToArray();

        // The trailing space in `". "` is trimmed away by the default `white-space: normal`
        // rendering (the same trimming any other painted text gets), leaving just "N.".
        Assert.Equal(["1.", "Alpha", "2.", "Bravo", "3.", "Charlie"], texts);
    }

    [Fact]
    public async Task BuildDisplayList_NestedCounterResetRestartsAndRestoresTheOuterCounter()
    {
        var document = await ParseAsync("""
            <html><head><style>
                ol { counter-reset: item; list-style: none; }
                li { counter-increment: item; }
                li::before { content: counter(item) ". "; }
            </style></head><body>
                <ol>
                    <li>One
                        <ol>
                            <li>One-A</li>
                            <li>One-B</li>
                        </ol>
                    </li>
                    <li>Two</li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 300,
            FontSize = 16f,
        });

        var markerTexts = displayList.Commands.OfType<DrawTextCommand>()
            .Where(t => t.Text.EndsWith('.'))
            .OrderBy(t => t.Y)
            .Select(t => t.Text)
            .ToArray();

        // The nested <ol> restarts its own "item" counter at 1 independently of the outer list,
        // and the outer list's own counter correctly resumes at 2 for its second <li> once the
        // nested list's subtree has ended - not left at whatever value the nested list reached.
        Assert.Equal(["1.", "1.", "2.", "2."], markerTexts);
    }

    [Fact]
    public async Task BuildDisplayList_CountersFunctionJoinsNestedCounterValuesWithASeparator()
    {
        var document = await ParseAsync("""
            <html><head><style>
                ol { counter-reset: item; list-style: none; }
                li { counter-increment: item; }
                li::before { content: counters(item, "."); }
            </style></head><body>
                <ol>
                    <li>One
                        <ol>
                            <li>One-A</li>
                        </ol>
                    </li>
                </ol>
            </body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 300,
            FontSize = 16f,
        });

        var markerTexts = displayList.Commands.OfType<DrawTextCommand>()
            .Where(t => t.Text is not ("One" or "One-A"))
            .OrderBy(t => t.Y)
            .Select(t => t.Text)
            .ToArray();

        Assert.Equal(["1", "1.1"], markerTexts);
    }

    [Fact]
    public async Task BuildDisplayList_PseudoElementInheritsColorFromHostWhenNotItselfSet()
    {
        var document = await ParseAsync("""
            <html><head><style>
            #target { color: rgb(10, 20, 30); }
            #target::before { content: "x"; }
            </style></head>
            <body><div id="target">middle</div></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();

        Assert.Equal(2, texts.Length);
        Assert.Equal(new RenderColor(10, 20, 30), texts[0].Color);
        Assert.Equal(new RenderColor(10, 20, 30), texts[1].Color);
    }

    [Fact]
    public async Task BuildDisplayList_PseudoElementOnFormControlDoesNotDuplicateControlChrome()
    {
        var document = await ParseAsync("""
            <html><head><style>#target::before { content: "*"; }</style></head>
            <body><input id="target" type="text" value="hi" /></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        // Baseline (the identical markup without the ::before rule) paints exactly 6
        // FillRectCommands (the input's default chrome, plus page/body backgrounds) and 1
        // DrawTextCommand (its own "hi" value) - a pseudo-element misidentified as the same form
        // control would duplicate that chrome and/or the value text. Only the pseudo's own "*"
        // text should be added on top.
        Assert.Equal(6, displayList.Commands.OfType<FillRectCommand>().Count());
        var texts = displayList.Commands.OfType<DrawTextCommand>().ToArray();
        Assert.Equal(2, texts.Length);
        Assert.Contains(texts, t => t.Text == "*");
        Assert.Contains(texts, t => t.Text == "hi");
    }

    [Fact]
    public async Task BuildDisplayList_PseudoElementOnImageDoesNotRepaintTheImage()
    {
        var document = await ParseAsync("""
            <html><head><style>#target::before { content: "*"; }</style></head>
            <body><img id="target" src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACklEQVR4nGMAAQABAA4A4cQTmwAAAABJRU5ErkJggg==" style="width:40px; height:20px;" /></body></html>
            """);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = 300,
            ViewPortHeight = 200,
            FontSize = 16f,
        });

        var imageCommands = displayList.Commands.OfType<DrawImageCommand>().ToArray();
        Assert.Single(imageCommands);
    }

    [Fact]
    public async Task BuildDisplayList_StickyElementRendersAtItsNaturalPositionBeforeScrollingPastIt()
    {
        var renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 200,
            FontSize = 16,
        };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="height:50px;"></div>
                <div style="position:sticky; top:10px; width:50px; height:30px; background-color:#00ff00;"></div>
                <div style="height:210px;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(50f, fill.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_StickyElementPinsToTopOnceScrolledPastIt()
    {
        var renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 200,
            FontSize = 16,
        };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="height:210px;"></div>
                <div style="position:sticky; top:10px; width:50px; height:30px; background-color:#00ff00;"></div>
                <div style="height:210px;"></div>
              </body>
            </html>
            """, configuration);

        document.Context.GetDomHarness();
        document.DocumentElement.SetScrollTop(250);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // Natural position (210 - 250 = -40) has scrolled above the 10px sticky threshold, so the
        // element clamps to top:10px instead of following its natural (now off-screen) position.
        Assert.Equal(10f, PaintedRect(displayList, fill).Y);
    }

    [Fact]
    public async Task BuildDisplayList_StickyBottomPinsAnElementBelowTheViewportToItsBottomEdge()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="height:300px"></div>
                <div style="position:sticky; bottom:0; height:30px; background-color:#00ff00"></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document, 200, 200);

        Assert.Equal(170f, PaintedRect(displayList, SingleFill(displayList, Green)).Y);
    }

    [Fact]
    public async Task BuildDisplayList_StickyLeftKeepsItsDistanceFromTheViewportEdge()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="position:sticky; left:10px; width:50px; height:30px; background-color:#00ff00"></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document, 200, 200);

        Assert.Equal(10f, PaintedRect(displayList, SingleFill(displayList, Green)).X);
    }

    [Fact]
    public async Task BuildDisplayList_StickyElementStaysInsideItsContainingBlock()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200, FontSize = 16 };
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <section style="height:100px">
                    <div style="position:sticky; top:0; height:30px; background-color:#00ff00"></div>
                </section>
                <div style="height:400px"></div>
            </body></html>
            """, Configuration.Default.WithCss().WithRenderDevice(renderDevice));

        document.Context.GetDomHarness();
        document.DocumentElement.SetScrollTop(150);

        var displayList = new HtmlRenderer().BuildDisplayList(document, renderDevice);

        // The section ends at 100 - 150 = -50, so the header goes no further than its bottom.
        Assert.Equal(-80f, PaintedRect(displayList, SingleFill(displayList, Green)).Y);
    }

    [Fact]
    public async Task BuildDisplayList_NestedStickyElementPaintsOverTheSiblingsAfterIt()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 200, FontSize = 16 };
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <section>
                    <div style="position:sticky; top:0; height:30px; background-color:#00ff00"></div>
                    <div style="height:400px; background-color:#ff0000"></div>
                </section>
            </body></html>
            """, Configuration.Default.WithCss().WithRenderDevice(renderDevice));

        document.Context.GetDomHarness();
        document.DocumentElement.SetScrollTop(100);

        var displayList = new HtmlRenderer().BuildDisplayList(document, renderDevice);
        var commands = displayList.Commands.ToList();
        var green = SingleFill(displayList, Green);

        Assert.Equal(0f, PaintedRect(displayList, green).Y);
        Assert.True(commands.IndexOf(green) > commands.IndexOf(SingleFill(displayList, Red)));
    }

    /// <summary>The rectangle a fill actually covers, after the translations around it.</summary>
    private static RenderRect PaintedRect(DisplayList displayList, FillRectCommand target)
    {
        var offsets = new Stack<(float X, float Y)>();
        var current = (X: 0f, Y: 0f);

        foreach (var command in displayList.Commands)
        {
            switch (command)
            {
                case PushTransformCommand push:
                    offsets.Push(current);
                    current = (current.X + push.Transform.E, current.Y + push.Transform.F);
                    break;
                case PopTransformCommand:
                    current = offsets.Pop();
                    break;
                case FillRectCommand fill when ReferenceEquals(fill, target):
                    return new RenderRect(fill.Rect.X + current.X, fill.Rect.Y + current.Y, fill.Rect.Width, fill.Rect.Height);
            }
        }

        throw new InvalidOperationException("The fill is not part of the display list.");
    }

    [Fact]
    public async Task BuildDisplayList_StickyElementWithoutAnOffsetBehavesLikeStatic()
    {
        var renderDevice = new DefaultRenderDevice
        {
            ViewPortWidth = 200,
            ViewPortHeight = 200,
            FontSize = 16,
        };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="height:210px;"></div>
                <div style="position:sticky; width:50px; height:30px; background-color:#00ff00;"></div>
                <div style="height:210px;"></div>
              </body>
            </html>
            """, configuration);

        document.Context.GetDomHarness();
        document.DocumentElement.SetScrollTop(250);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // No offset property at all means nothing to stick to - stays at its natural (now
        // off-screen, negative-Y) scrolled position, exactly like `position: static` would.
        Assert.Equal(-40f, fill.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_ContentBoxSizingRemainsTheDefaultAndAddsPaddingAndBorderOnTop()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:100px; height:50px; padding:10px; border:5px solid black; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // Unset box-sizing (content-box, the CSS default) is unaffected by this feature: the
        // authored width/height already describe the content box, so padding and border are added
        // on top of it, exactly as this renderer always assumed before box-sizing existed anywhere
        // in its style map - 100 + 2*10 (padding) + 2*5 (border) = 130.
        Assert.Equal(130f, fill.Rect.Width);
        Assert.Equal(80f, fill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_BorderBoxSizingMakesWidthAndHeightIncludePaddingAndBorder()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="box-sizing:border-box; width:100px; height:50px; padding:10px; border:5px solid black; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // box-sizing: border-box means the authored width/height already *are* the border-box
        // size - padding and border eat into the content area instead of being added on top of it,
        // so the painted border box stays exactly 100x50 regardless of padding/border.
        Assert.Equal(100f, fill.Rect.Width);
        Assert.Equal(50f, fill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_BorderBoxSizingClampsContentToZeroWhenPaddingAndBorderExceedTheDeclaredSize()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="box-sizing:border-box; width:20px; height:20px; padding:20px; border:5px solid black; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // Padding (20px each side) plus border (5px each side) alone already exceed the declared
        // 20px border box - ResolveAuthoredDimension clamps the resulting negative content size to
        // 0 rather than letting it go negative, matching a real browser: the box still cannot be
        // physically smaller than its own border+padding, so the rendered border box ends up
        // border+padding (2*5 + 2*20 = 50) rather than the too-small declared 20px, exactly as
        // borderBoxWidth/Height's own pre-existing bottom-up "border + padding + content" formula
        // (unchanged by this feature) already produces once content is 0.
        Assert.Equal(50f, fill.Rect.Width);
        Assert.Equal(50f, fill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_FlexItemBorderBoxWidthIncludesPaddingAndBorder()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="display:flex; width:300px;">
                  <div style="box-sizing:border-box; width:100px; height:40px; padding:10px; border:5px solid black; background-color:#00ff00;"></div>
                </div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // A flex item's own box-sizing follows the same rule as an ordinary block box -
        // ResolveFlexBaseSize converts the item's authored border-box width into a content-box
        // main size, using this item's own (not the container's) border/padding, before the
        // flex-grow/shrink distribution ever runs.
        Assert.Equal(100f, fill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_FlexContainerBorderBoxWidthIncludesPaddingAndBorder()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="display:flex; box-sizing:border-box; width:200px; height:80px; padding:20px; border:10px solid black; background-color:#ff00ff;">
                  <div style="width:30px; height:30px; background-color:#00ff00;"></div>
                </div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 255))));

        // The flex container itself is an ordinary box for sizing purposes - its own declared
        // border-box width/height (200x80) must stay exactly that regardless of its own padding
        // (20px) and border (10px), with the reduced space left over distributed to items instead.
        Assert.Equal(200f, fill.Rect.Width);
        Assert.Equal(80f, fill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_GridContainerBorderBoxHeightIncludesPaddingAndBorder()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="display:grid; grid-template-columns: 1fr; box-sizing:border-box; width:150px; height:100px; padding:10px; border:5px solid black; background-color:#ff00ff;">
                  <div style="height:10px; background-color:#00ff00;"></div>
                </div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 255))));

        // The child is given an explicit, small height so the implicit row's own auto-growth
        // (ResolveGridItemEstimatedSize's pre-existing, unrelated fallback of the *container's*
        // content width whenever an item's own height is unset) never dominates over the
        // container's own specified height - isolating this assertion to the box-sizing
        // conversion this test actually targets.
        Assert.Equal(100f, fill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_InlineBlockBorderBoxWidthIsUsedForLineWrapPrediction()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:100px;">
                  <span style="display:inline-block; box-sizing:border-box; width:50px; height:20px; padding:5px; border:2px solid black; background-color:#00ff00;"></span><span style="display:inline-block; box-sizing:border-box; width:50px; height:20px; padding:5px; border:2px solid black; background-color:#0000ff;"></span>
                </div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var greenFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));
        var blueFill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 0, 255))));

        // TryMeasureInlineBlockBoxSize's own prediction must agree with box-sizing too: each
        // border-box inline-block is exactly 50px wide (not 50+2*5+2*2=64, which its own padding
        // and border would inflate it to under the old, box-sizing-unaware prediction), so both
        // fit together in the 100px container on the very same line without wrapping.
        Assert.Equal(50f, greenFill.Rect.Width);
        Assert.Equal(greenFill.Rect.Y, blueFill.Rect.Y);
        Assert.Equal(50f, blueFill.Rect.X);
    }

    // The following battery exercises ParseLength's typed-value fast path (StyleMap/
    // AddLengthProperty/CssLengthValue - see HtmlRenderer.cs) across the representative value
    // shapes it has to agree with the old, still-present string-parsing fallback on: a plain
    // pixel length, a percentage (resolved against a known container size), a negative value,
    // zero, an "auto" default, and a border-width keyword (thin/medium/thick, which
    // AngleSharp.Css itself resolves to a concrete CssLengthValue rather than a raw string).
    // These are ordinary rendering assertions, not a literal side-by-side dual-path comparison -
    // ParseLength has no public seam to force one path or the other from outside this file - but
    // they pin down the exact numeric behavior the fast path must reproduce, the same way every
    // other test in this suite already asserts concrete expected geometry.

    [Fact]
    public async Task BuildDisplayList_LengthFastPath_PercentageWidthResolvesAgainstContainer()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:50%; height:20px; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(150f, fill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_LengthFastPath_NegativeMarginShiftsElementLeft()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="margin-left:-10px; width:40px; height:20px; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(-10f, fill.Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_LengthFastPath_ZeroPaddingAddsNothingToBorderBox()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:40px; height:20px; padding:0; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(40f, fill.Rect.Width);
        Assert.Equal(20f, fill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_LengthFastPath_AutoWidthFillsTheContainer()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:220px;">
                  <div style="height:20px; background-color:#00ff00;"></div>
                </div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(220f, fill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_LengthFastPath_BorderWidthKeywordResolvesToPixels()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="width:40px; height:20px; border:thick solid black; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // "thick" is one of the CssLengthValue static keyword lengths (5px) the fast path reads
        // directly - content-box default means the 40px width is untouched by the border, but the
        // border box itself grows by 2*5px around it.
        Assert.Equal(50f, fill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_LengthFastPath_TopPercentageOffsetsARelativeElement()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 200, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <div style="position:relative; top:25%; width:40px; height:20px; background-color:#00ff00;"></div>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();
        var displayList = renderer.BuildDisplayList(document, renderDevice);
        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        // top's own percentage resolves against the *containing block's width* per this renderer's
        // existing (pre-existing, unchanged) convention for offsets - relativeTo is always the
        // containing width at every ParseLength("top"/"left", ...) call site - so 25% of the 300px
        // viewport width is 75px, added to the element's natural Y (0).
        Assert.Equal(75f, fill.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_LengthFastPath_UnsetLetterSpacingNormalDefaultDoesNotThrow()
    {
        var renderDevice = new DefaultRenderDevice { ViewPortWidth = 300, ViewPortHeight = 100, FontSize = 16 };
        var configuration = Configuration.Default.WithCss().WithRenderDevice(renderDevice);
        var document = await ParseAsync("""
            <html>
              <head><style>html, body { margin: 0; padding: 0; }</style></head>
              <body>
                <p>Hello world</p>
              </body>
            </html>
            """, configuration);

        var renderer = new HtmlRenderer();

        // letter-spacing's own CSS initial value is the literal keyword "normal", which
        // AngleSharp.Css resolves to a NaN-valued CssLengthValue - the same NaN encoding "auto"
        // uses for other properties. ParseLength's fast path (allowAuto: false at this call site)
        // must resolve this to its own default (0), matching the pre-existing string path exactly,
        // not throw or silently produce a wrong value.
        var displayList = Xunit.Record.Exception(() => renderer.BuildDisplayList(document, renderDevice));
        Assert.Null(displayList);
    }

    private static DisplayList BuildDisplayList(AngleSharp.Dom.IDocument document, int width = 400, int height = 300) =>
        new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice
        {
            ViewPortWidth = width,
            ViewPortHeight = height,
            FontSize = 16f,
        });

    private static DrawTextCommand SingleText(DisplayList displayList, string text) =>
        Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Where(t => t.Text == text));

    [Fact]
    public async Task BuildDisplayList_InlineSiblingsDirectlyInTheBodyShareALine()
    {
        var document = await ParseAsync("<html><body><span>One</span><span>Two</span> <span>Three</span></body></html>");

        var displayList = BuildDisplayList(document);

        var one = SingleText(displayList, "One");
        var two = SingleText(displayList, "Two");
        var three = SingleText(displayList, "Three");

        Assert.Equal(one.Y, two.Y);
        Assert.Equal(one.Y, three.Y);
        Assert.True(two.X > one.X);
        Assert.True(three.X > two.X);
    }

    [Fact]
    public async Task BuildDisplayList_AdjacentInlineElementsWithoutWhitespaceAreNotSeparated()
    {
        var document = await ParseAsync("<html><body><p><b>Hello</b>World</p></body></html>");

        var displayList = BuildDisplayList(document);

        var hello = SingleText(displayList, "Hello");
        var world = SingleText(displayList, "World");

        // No space between the two runs: "World" starts where the bold "Hello" ends (about 47px
        // at 16px), not a space width further on.
        Assert.Equal(hello.Y, world.Y);
        Assert.True(hello.IsBold);
        Assert.InRange(world.X - hello.X, 40f, 50f);
    }

    [Fact]
    public async Task BuildDisplayList_MixedInlineContentPreservesSpacesAndForcedBreaks()
    {
        var document = await ParseAsync("<html><body><div style=\"white-space:pre\">a   b\nc <b>d</b></div></body></html>");

        var displayList = BuildDisplayList(document);

        var firstLine = SingleText(displayList, "a   b");
        var c = SingleText(displayList, "c");
        var d = SingleText(displayList, "d");

        Assert.True(c.Y > firstLine.Y);
        Assert.Equal(c.Y, d.Y);
    }

    [Fact]
    public async Task BuildDisplayList_MixedInlineContentBreaksWordsWithWordBreakBreakAll()
    {
        var document = await ParseAsync("<html><body><div style=\"width:60px; word-break:break-all\">Supercalifragilistic <b>x</b></div></body></html>");

        var displayList = BuildDisplayList(document);

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();

        Assert.True(texts.Select(t => t.Y).Distinct().Count() >= 3);
        Assert.Equal("Supercalifragilistic", string.Concat(texts.Where(t => t.Text != "x").Select(t => t.Text.Trim())));
    }

    [Fact]
    public async Task BuildDisplayList_MixedInlineContentHonoursTextAlign()
    {
        var document = await ParseAsync("<html><body><p style=\"text-align:center\">Hello <b>World</b></p></body></html>");

        var displayList = BuildDisplayList(document);

        var hello = SingleText(displayList, "Hello");
        var world = SingleText(displayList, "World");

        Assert.True(hello.X > 100f);
        Assert.Equal(hello.Y, world.Y);
    }

    [Fact]
    public async Task BuildDisplayList_NestedInlineElementsKeepTheirOwnStyles()
    {
        var document = await ParseAsync("<html><body><p>Plain <b>bold <i>both</i></b> plain</p></body></html>");

        var displayList = BuildDisplayList(document);

        var plain = SingleText(displayList, "Plain");
        var bold = SingleText(displayList, "bold");
        var both = SingleText(displayList, "both");

        Assert.False(plain.IsBold);
        Assert.True(bold.IsBold);
        Assert.True(both.IsBold);
        Assert.True(both.IsItalic);
        Assert.False(bold.IsItalic);
    }

    [Fact]
    public async Task BuildDisplayList_BlockInsideInlineElementGetsItsOwnLine()
    {
        var document = await ParseAsync("<html><body><span>before<div>block</div>after</span></body></html>");

        var displayList = BuildDisplayList(document);

        var before = SingleText(displayList, "before");
        var block = SingleText(displayList, "block");
        var after = SingleText(displayList, "after");

        Assert.True(block.Y > before.Y);
        Assert.True(after.Y > block.Y);
    }

    [Fact]
    public async Task BuildDisplayList_InlineBackgroundIsPaintedPerLineFragment()
    {
        var document = await ParseAsync("<html><body><div style=\"width:120px\">x <span style=\"background-color:#ff0000\">one two three four five</span></div></body></html>");

        var displayList = BuildDisplayList(document);

        var fragments = displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))).ToList();

        Assert.True(fragments.Count >= 2);
        Assert.True(fragments[1].Rect.Y > fragments[0].Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_ClearLeftMovesABlockBelowTheFloat()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:left; width:50px; height:80px; background-color:#ff0000"></div>
                <div style="clear:left">After</div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        var after = SingleText(displayList, "After");

        Assert.Equal(0f, after.X);
        Assert.True(after.Y > 80f);
    }

    [Fact]
    public async Task BuildDisplayList_WithoutClearABlockFlowsBesideTheFloat()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:left; width:50px; height:80px; background-color:#ff0000"></div>
                <div>After</div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        var after = SingleText(displayList, "After");

        Assert.Equal(50f, after.X);
        Assert.True(after.Y < 80f);
    }

    [Fact]
    public async Task BuildDisplayList_FloatWithAutoWidthShrinksToFitItsContent()
    {
        var document = await ParseAsync("<html><body><div style=\"float:left; background-color:#00ff00\">Hi</div></body></html>");

        var displayList = BuildDisplayList(document);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.InRange(fill.Rect.Width, 10f, 30f);
    }

    [Fact]
    public async Task BuildDisplayList_AbsoluteBoxWithAutoWidthShrinksToFitItsContent()
    {
        var document = await ParseAsync("<html><body><div style=\"position:absolute; left:10px; top:10px; background-color:#00ff00\">Hi</div></body></html>");

        var displayList = BuildDisplayList(document);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.InRange(fill.Rect.Width, 10f, 30f);
    }

    [Fact]
    public async Task BuildDisplayList_AbsoluteBoxWithLeftAndRightStretchesBetweenThem()
    {
        var document = await ParseAsync("<html><body><div style=\"position:absolute; left:10px; right:30px; top:10px; background-color:#00ff00\">Hi</div></body></html>");

        var displayList = BuildDisplayList(document);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(10f, fill.Rect.X);
        Assert.Equal(360f, fill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_MaxWidthClampsABlockAndAutoMarginsCenterIt()
    {
        var document = await ParseAsync("<html><body><div style=\"max-width:200px; margin:0 auto; height:10px; background-color:#00ff00\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(200f, fill.Rect.Width);
        Assert.Equal(100f, fill.Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_MinWidthWidensABlock()
    {
        var document = await ParseAsync("<html><body><div style=\"width:50px; min-width:120px; height:10px; background-color:#00ff00\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(120f, fill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_ImageWithoutSizeIsAnInlineBoxOfItsNaturalSize()
    {
        var document = await ParseAsync($"""
            <html><body><p>Before <img src="{OnePixelPng}"> after</p></body></html>
            """);

        var displayList = BuildDisplayList(document);

        var image = Assert.Single(displayList.Commands.OfType<DrawImageCommand>());
        var before = SingleText(displayList, "Before");
        var after = SingleText(displayList, "after");

        Assert.Equal(1f, image.Rect.Width);
        Assert.Equal(1f, image.Rect.Height);
        Assert.True(image.Rect.X > before.X);
        Assert.True(after.X > image.Rect.X);
        Assert.Equal(before.Y, after.Y);

        // Its bottom edge sits on the baseline.
        Assert.Equal(before.Y, image.Rect.Y + image.Rect.Height, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_InlineBlockAlignsItsLastLineWithTheSurroundingBaseline()
    {
        var document = await ParseAsync("""
            <html><body><p>A <span style="display:inline-block; padding:6px; background-color:#0000ff">inner</span> B</p></body></html>
            """);

        var displayList = BuildDisplayList(document);

        var a = SingleText(displayList, "A");
        var inner = SingleText(displayList, "inner");
        var b = SingleText(displayList, "B");

        Assert.Equal(a.Y, inner.Y, precision: 2);
        Assert.Equal(a.Y, b.Y);
    }

    [Theory]
    [InlineData("<p style=\"text-transform:uppercase\">Hello world</p>", "HELLO WORLD")]
    [InlineData("<p style=\"text-transform:lowercase\">Hello World</p>", "hello world")]
    [InlineData("<p style=\"text-transform:capitalize\">hello wide world</p>", "Hello Wide World")]
    public async Task BuildDisplayList_AppliesTextTransform(string body, string expected)
    {
        var document = await ParseAsync($"<html><body>{body}</body></html>");

        var displayList = BuildDisplayList(document);

        SingleText(displayList, expected);
    }

    [Fact]
    public async Task BuildDisplayList_TextTransformIsInheritedIntoInlineContent()
    {
        var document = await ParseAsync("<html><body><p style=\"text-transform:uppercase\">one <b>two</b></p></body></html>");

        var displayList = BuildDisplayList(document);

        SingleText(displayList, "ONE");
        SingleText(displayList, "TWO");
    }

    [Fact]
    public async Task BuildDisplayList_WordSpacingWidensTheGapBetweenWords()
    {
        var normal = BuildDisplayList(await ParseAsync("<html><body><p>alpha beta</p></body></html>"));
        var spaced = BuildDisplayList(await ParseAsync("<html><body><p style=\"word-spacing:20px\">alpha beta</p></body></html>"));

        var spacedAlpha = SingleText(spaced, "alpha");
        var spacedBeta = SingleText(spaced, "beta");
        var normalText = SingleText(normal, "alpha beta");

        Assert.Equal(normalText.X, spacedAlpha.X);
        Assert.InRange(spacedBeta.X - spacedAlpha.X, 60f, 75f);
    }

    [Theory]
    [InlineData("one two three four five six seven eight nine ten eleven twelve")]
    [InlineData("one two <span>three</span> four five six seven eight nine ten eleven twelve")]
    public async Task BuildDisplayList_TextAlignJustifyStretchesAllButTheLastLine(string content)
    {
        // A tiny word spacing paints word by word too, so both lists have one command per word.
        var reference = BuildDisplayList(await ParseAsync($"<html><body><p style=\"width:150px; word-spacing:0.01px\">{content}</p></body></html>"));
        var justified = BuildDisplayList(await ParseAsync($"<html><body><p style=\"width:150px; text-align:justify\">{content}</p></body></html>"));

        var referenceWords = reference.Commands.OfType<DrawTextCommand>().ToList();
        var justifiedWords = justified.Commands.OfType<DrawTextCommand>().ToList();
        var lastY = justifiedWords.Max(w => w.Y);

        // The last line is not justified, so it stays one text run.
        var lastLine = Assert.Single(justifiedWords, w => w.Y == lastY);
        Assert.Equal(referenceWords.First(w => w.Y == lastY).X, lastLine.X, 0.01f);

        var stretchedWords = justifiedWords.Where(w => w.Y != lastY).ToList();
        Assert.Equal(referenceWords.Where(w => w.Y != lastY).Select(w => (w.Text, w.Y)), stretchedWords.Select(w => (w.Text, w.Y)));

        var lines = stretchedWords.Select((word, index) => (word, reference: referenceWords[index])).GroupBy(pair => pair.word.Y).ToList();
        Assert.True(lines.Count >= 2);

        foreach (var line in lines)
        {
            Assert.Equal(line.First().reference.X, line.First().word.X, 0.01f);
            Assert.True(line.Last().word.X >= line.Last().reference.X);
        }

        Assert.Contains(lines, line => line.Last().word.X > line.Last().reference.X + 10f);
    }

    [Fact]
    public async Task BuildDisplayList_TextAlignLastAlignsTheLastLine()
    {
        var start = BuildDisplayList(await ParseAsync("<html><body><p style=\"width:150px; text-align:justify\">one two three four five six seven eight nine ten eleven twelve</p></body></html>"));
        var centered = BuildDisplayList(await ParseAsync("<html><body><p style=\"width:150px; text-align:justify; text-align-last:center\">one two three four five six seven eight nine ten eleven twelve</p></body></html>"));

        var lastStart = start.Commands.OfType<DrawTextCommand>().Last();
        var lastCentered = centered.Commands.OfType<DrawTextCommand>().Last();

        Assert.Equal(lastStart.Text, lastCentered.Text);
        Assert.True(lastCentered.X > lastStart.X + 5f);
    }

    [Fact]
    public async Task BuildDisplayList_TextAlignJustifyDoesNotStretchLinesEndedByAForcedBreak()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body><p style=\"width:300px; text-align:justify\">one two<br>three four</p></body></html>"));

        Assert.Equal(SingleText(displayList, "three four").X, SingleText(displayList, "one two").X);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<span></span>")]
    public async Task BuildDisplayList_SoftHyphensBreakWordsWithAVisibleHyphen(string inlineContent)
    {
        var displayList = BuildDisplayList(await ParseAsync($"<html><body><p style=\"width:60px\">aaaa&shy;bbbb&shy;cccc{inlineContent}</p></body></html>"));

        Assert.Equal(["aaaa-", "bbbb-", "cccc"], displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<span></span>")]
    public async Task BuildDisplayList_UnusedSoftHyphensAreNotPainted(string inlineContent)
    {
        var displayList = BuildDisplayList(await ParseAsync($"<html><body><p>aaaa&shy;bbbb&shy;cccc{inlineContent}</p></body></html>"));

        Assert.Equal("aaaabbbbcccc", string.Concat(displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<span></span>")]
    public async Task BuildDisplayList_HyphensNoneIgnoresSoftHyphens(string inlineContent)
    {
        var displayList = BuildDisplayList(await ParseAsync($"<html><body><p style=\"width:60px; hyphens:none\">aaaa&shy;bbbb&shy;cccc{inlineContent}</p></body></html>"));

        Assert.Equal("aaaabbbbcccc", string.Concat(displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Text)));
        Assert.Single(displayList.Commands.OfType<DrawTextCommand>().Select(t => t.Y).Distinct());
    }

    [Fact]
    public async Task BuildDisplayList_TabSizeSetsTheTabStopsOfPreformattedText()
    {
        var paragraph = BuildDisplayList(await ParseAsync("<html><body><pre style=\"tab-size:4\">a\tb</pre></body></html>"));
        Assert.Equal("a   b", SingleText(paragraph, "a   b").Text);

        async Task<float> TabAdvance(string tabSize)
        {
            var displayList = BuildDisplayList(await ParseAsync($"<html><body><pre style=\"tab-size:{tabSize}\">a\tb<span></span></pre></body></html>"));
            return SingleText(displayList, "b").X - SingleText(displayList, "a").X;
        }

        var four = await TabAdvance("4");
        var eight = await TabAdvance("8");
        Assert.Equal(2f * four, eight, 0.01f);
        Assert.Equal(40f, await TabAdvance("40px"), 0.01f);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<span></span>")]
    public async Task BuildDisplayList_SmallCapsPaintsLowercaseAsReducedCapitals(string inlineContent)
    {
        var displayList = BuildDisplayList(await ParseAsync($"<html><body><p style=\"font-variant:small-caps\">Hello{inlineContent}</p></body></html>"));

        var capital = SingleText(displayList, "H");
        var reduced = SingleText(displayList, "ELLO");
        Assert.Equal(16f, capital.FontSize);
        Assert.Equal(16f * 0.7f, reduced.FontSize, 0.01f);
        Assert.Equal(capital.Y, reduced.Y);
        Assert.True(reduced.X > capital.X);
    }

    [Fact]
    public async Task BuildDisplayList_FontStretchIsCarriedOnTheTextCommand()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body><p style=\"font-stretch:condensed\">narrow</p></body></html>"));

        Assert.Equal(75f, SingleText(displayList, "narrow").FontStretch);
    }

    [Fact]
    public async Task BuildDisplayList_TextOverflowEllipsisTruncatesMixedInlineContent()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body style=\"margin:0\"><div style=\"width:100px; overflow:hidden; white-space:nowrap; text-overflow:ellipsis\">alpha <b>beta</b> gamma delta epsilon</div></body></html>"));

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();
        Assert.EndsWith("\u2026", texts[^1].Text);
        Assert.DoesNotContain(texts, t => t.Text.Contains("epsilon"));
        Assert.Equal("alpha", texts[0].Text);
    }

    [Fact]
    public async Task BuildDisplayList_LineClampCutsMixedInlineContentAfterTheLastLine()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body><div style=\"width:100px; overflow:hidden; -webkit-line-clamp:2\">one two <b>three</b> four five six seven eight nine ten</div></body></html>"));

        var texts = displayList.Commands.OfType<DrawTextCommand>().ToList();
        Assert.Equal(2, texts.Select(t => t.Y).Distinct().Count());
        Assert.EndsWith("\u2026", texts[^1].Text);
        Assert.DoesNotContain(texts, t => t.Text.Contains("ten"));
    }

    [Fact]
    public async Task BuildDisplayList_LineClampShowsNoEllipsisWhenEverythingFits()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body><div style=\"width:300px; overflow:hidden; -webkit-line-clamp:2\">one <b>two</b></div></body></html>"));

        Assert.DoesNotContain(displayList.Commands.OfType<DrawTextCommand>(), t => t.Text.Contains('\u2026'));
    }

    [Fact]
    public async Task BuildDisplayList_DoublePositionColorStopsGiveHardEdgedGradients()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body><div style=\"width:100px; height:20px; background-image:linear-gradient(to right, #ff0000 0 50%, #0000ff 50% 100%)\"></div></body></html>"));

        var stops = ((RenderGradientPaint)SingleGradientFill(displayList).Paint!).Gradient.Stops;
        Assert.Equal([0f, 0.5f, 0.5f, 1f], stops.Select(stop => stop.Position));
        Assert.Equal([Red, Red, Blue, Blue], stops.Select(stop => stop.Color));
    }

    [Fact]
    public async Task BuildDisplayList_NestedFlexContainerGrowsAndDistributesItsItems()
    {
        var document = await ParseAsync("""
            <html><head><style>
              body { margin: 0; }
              .demo { display: flex; width: 400px; padding: 12px; gap: 12px; }
              .nested { display: flex; flex: 1 1 auto; align-items: center; justify-content: space-evenly; padding: 10px; }
              .nested > div { width: 60px; height: 36px; background: #ff0000; }
            </style></head>
            <body><div class="demo"><div class="nested"><div></div><div></div><div></div></div></div></body></html>
            """);

        var displayList = BuildDisplayList(document, 500, 300);

        var boxes = displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(Red)).Select(f => f.Rect.X).ToList();
        Assert.Equal(3, boxes.Count);

        // The nested container fills the 400px content box: 380px inside its padding, so each of
        // the four gaps is (380 - 180) / 4 = 50px.
        Assert.Equal([12f + 10f + 50f, 12f + 10f + 160f, 12f + 10f + 270f], boxes);
    }

    [Fact]
    public async Task BuildDisplayList_TextIndentIsInheritedByNestedBlocks()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body style=\"margin:0\"><div style=\"text-indent:30px\"><p style=\"margin:0\">nested</p></div></body></html>"));

        Assert.Equal(30f, SingleText(displayList, "nested").X);
    }

    [Fact]
    public async Task BuildDisplayList_InsideMarkerSpaceIsNotInheritedByNestedBlocks()
    {
        var displayList = BuildDisplayList(await ParseAsync("<html><body style=\"margin:0\"><ul style=\"list-style-position:inside; margin:0; padding:0\"><li>first<p style=\"margin:0\">nested</p></li></ul></body></html>"));

        Assert.True(SingleText(displayList, "first").X > 0f);
        Assert.Equal(0f, SingleText(displayList, "nested").X);
    }

    [Fact]
    public async Task BuildDisplayList_OutlineOffsetMovesTheOutlineAwayFromTheBorderBox()
    {
        var document = await ParseAsync("<html><body><div style=\"margin:20px; width:40px; height:40px; outline:2px solid #ff0000; outline-offset:5px\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var outlineEdges = displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))).ToList();

        Assert.NotEmpty(outlineEdges);
        Assert.Equal(13f, outlineEdges.Min(f => f.Rect.X));
        Assert.Equal(67f, outlineEdges.Max(f => f.Rect.X + f.Rect.Width));
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundClipContentBoxInsetsTheBackground()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; padding:10px; border:5px solid #000000; background-color:#00ff00; background-clip:content-box\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(15f, fill.Rect.X);
        Assert.Equal(40f, fill.Rect.Width);
        Assert.Equal(40f, fill.Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundClipPaddingBoxExcludesTheBorder()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; padding:10px; border:5px solid #000000; background-color:#00ff00; background-clip:padding-box\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var fill = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(0, 255, 0))));

        Assert.Equal(5f, fill.Rect.X);
        Assert.Equal(60f, fill.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_MixBlendModeWrapsTheElementInABlendScope()
    {
        var document = await ParseAsync("<html><body><div style=\"mix-blend-mode:multiply; width:40px; height:40px; background-color:#00ff00\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var push = Assert.Single(displayList.Commands.OfType<PushBlendModeCommand>());
        Assert.Equal(RenderBlendMode.Multiply, push.Mode);

        var pushIndex = displayList.Commands.IndexOf(push);
        var fillIndex = displayList.Commands.ToList().FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(0, 255, 0)));
        var popIndex = displayList.Commands.ToList().FindIndex(c => c is PopBlendModeCommand);

        Assert.True(pushIndex < fillIndex);
        Assert.True(fillIndex < popIndex);
    }

    [Fact]
    public async Task BuildDisplayList_IsolationIsolateOpensANormalBlendGroup()
    {
        var document = await ParseAsync("<html><body><div style=\"isolation:isolate; width:40px; height:40px\"><div style=\"mix-blend-mode:multiply; height:10px\"></div></div></body></html>");

        var displayList = BuildDisplayList(document);

        var pushes = displayList.Commands.OfType<PushBlendModeCommand>().ToList();
        Assert.Equal([RenderBlendMode.Normal, RenderBlendMode.Multiply], pushes.Select(p => p.Mode));
        Assert.Equal(2, displayList.Commands.OfType<PopBlendModeCommand>().Count());
    }

    [Fact]
    public async Task BuildDisplayList_IsolationWithoutBlendingInsideOpensNoGroup()
    {
        // found on GitHub: a group around the page hid its header from the backdrop-filter of its buttons
        var document = await ParseAsync("""
            <html><body style="isolation:isolate">
                <div style="background-color:#000000; padding:10px">
                    <div style="backdrop-filter:blur(4px); width:40px; height:20px"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Empty(displayList.Commands.OfType<PushBlendModeCommand>());
        Assert.Single(displayList.Commands.OfType<DrawBackdropFilterCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundBlendModeBlendsTheImageWithTheBackgroundColor()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; background-color:#ff0000; background-image:linear-gradient(#000000, #ffffff); background-blend-mode:screen\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var commands = displayList.Commands.ToList();
        var pushes = commands.OfType<PushBlendModeCommand>().ToList();

        Assert.Equal([RenderBlendMode.Normal, RenderBlendMode.Screen], pushes.Select(p => p.Mode));

        var colorIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(255, 0, 0)));
        var gradientIndex = commands.FindIndex(c => c is FillRectCommand { Paint: RenderGradientPaint });

        Assert.True(commands.IndexOf(pushes[0]) < colorIndex);
        Assert.True(colorIndex < commands.IndexOf(pushes[1]));
        Assert.True(commands.IndexOf(pushes[1]) < gradientIndex);
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundColorPaintsUnderABackgroundImage()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; background-color:#ff0000; background-image:linear-gradient(transparent, #ffffff)\"></div></body></html>");

        var displayList = BuildDisplayList(document);

        var commands = displayList.Commands.ToList();
        var colorIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(new RenderColor(255, 0, 0)));
        var gradientIndex = commands.FindIndex(c => c is FillRectCommand { Paint: RenderGradientPaint });

        Assert.True(colorIndex >= 0);
        Assert.True(colorIndex < gradientIndex);
        Assert.Empty(commands.OfType<PushBlendModeCommand>());
    }

    [Fact]
    public async Task BuildDisplayList_ColumnCountSplitsContentIntoBalancedColumns()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:300px; column-count:2; column-gap:20px; column-rule:2px solid #ff0000">
                    <p style="margin:0">One</p><p style="margin:0">Two</p><p style="margin:0">Three</p><p style="margin:0">Four</p>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        var clips = displayList.Commands.OfType<PushClipCommand>().ToList();
        var translations = displayList.Commands.OfType<PushTransformCommand>().Select(t => t.Transform).ToList();

        Assert.Equal(2, clips.Count);
        Assert.Equal(clips[0].Rect.Height, clips[1].Rect.Height, precision: 2);
        Assert.Equal(0f, translations[0].E);
        Assert.Equal(160f, translations[1].E);
        Assert.Equal(-clips[0].Rect.Height, translations[1].F, precision: 2);

        var rule = Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(new RenderColor(255, 0, 0))));
        Assert.Equal(149f, rule.Rect.X);
        Assert.Equal(2f, rule.Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_ColumnWidthDerivesTheColumnCount()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="width:300px; column-width:90px; column-gap:10px"><p>a</p><p>b</p><p>c</p></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        // floor((300 + 10) / (90 + 10)) = 3 columns of (300 - 2 * 10) / 3 width.
        var clips = displayList.Commands.OfType<PushClipCommand>().ToList();
        Assert.Equal(3, clips.Count);
        Assert.Equal(93.33f + 10f, clips[0].Rect.Width, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_PercentageColumnGapResolvesAgainstTheGridsWidth()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:50px 50px; column-gap:10%">
                    <div style="height:10px; background-color:#ff0000"></div>
                    <div style="height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(90f, SingleFill(BuildDisplayList(document), Green).Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_PercentageRowGapIsZeroWithoutADefiniteHeight()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:50px; row-gap:10%">
                    <div style="height:10px; background-color:#ff0000"></div>
                    <div style="height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(10f, SingleFill(BuildDisplayList(document), Green).Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_NestedFlexContainerCountsItsGapShorthandInItsWidth()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; justify-content:flex-end; width:400px">
                    <div style="display:flex; gap:20px">
                        <div style="width:10px; height:10px"></div>
                        <div style="width:10px; height:10px"></div>
                        <div style="width:10px; height:10px; background-color:#00ff00"></div>
                    </div>
                </div>
            </body></html>
            """);

        Assert.Equal(390f, SingleFill(BuildDisplayList(document), Green).Rect.X, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FlexBasisIsNotWidenedToTheContent()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:300px">
                    <div style="flex-basis:50px; flex-shrink:0; background-color:#ff0000">a b c d e f g h i j k l m n o p</div>
                </div>
            </body></html>
            """);

        Assert.Equal(50f, SingleFill(BuildDisplayList(document), Red).Rect.Width);
    }

    [Fact]
    public async Task BuildDisplayList_FlexOneSharesTheSpaceRegardlessOfContent()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:200px">
                    <div style="flex:1; background-color:#ff0000">a much longer text</div>
                    <div style="flex:1; background-color:#00ff00">x</div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(100f, SingleFill(displayList, Red).Rect.Width, precision: 2);
        Assert.Equal(100f, SingleFill(displayList, Green).Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FlexZeroBasisSharesTheSpaceRegardlessOfContent()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:200px">
                    <div style="flex-grow:1; flex-basis:0; background-color:#ff0000">a much longer text</div>
                    <div style="flex-grow:1; flex-basis:0; background-color:#00ff00">x</div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(100f, SingleFill(displayList, Red).Rect.Width, precision: 2);
        Assert.Equal(100f, SingleFill(displayList, Green).Rect.Width, precision: 2);
    }

    [Theory]
    [InlineData("", 80f)]
    [InlineData("min-width:0;", 50f)]
    public async Task BuildDisplayList_FlexItemsDoNotShrinkBelowTheirContentUnlessAllowed(string minWidth, float expectedWidth)
    {
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <div style="display:flex; width:100px">
                    <div style="{minWidth} background-color:#ff0000"><span style="display:inline-block; width:80px; height:10px"></span></div>
                    <div style="{minWidth} background-color:#00ff00"><span style="display:inline-block; width:80px; height:10px"></span></div>
                </div>
            </body></html>
            """);

        Assert.Equal(expectedWidth, SingleFill(BuildDisplayList(document), Red).Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FlexShrinkIsWeightedByTheBaseSize()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:150px">
                    <div style="width:200px; height:10px; background-color:#ff0000"></div>
                    <div style="width:100px; height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(100f, SingleFill(displayList, Red).Rect.Width, precision: 2);
        Assert.Equal(50f, SingleFill(displayList, Green).Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FlexItemsFrozenAtTheirMaximumLeaveTheRestToOthers()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:300px">
                    <div style="flex-grow:1; flex-basis:0; max-width:50px; height:10px; background-color:#ff0000"></div>
                    <div style="flex-grow:1; flex-basis:0; height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(50f, SingleFill(displayList, Red).Rect.Width, precision: 2);
        Assert.Equal(250f, SingleFill(displayList, Green).Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FlexItemMarginsAreOutsideTheirMainSize()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:300px">
                    <div style="width:50px; height:10px; background-color:#ff0000"></div>
                    <div style="width:50px; height:10px; margin-left:20px; margin-right:10px; background-color:#00ff00"></div>
                    <div style="width:50px; height:10px; background-color:#0000ff"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);
        var green = SingleFill(displayList, Green).Rect;

        Assert.Equal(70f, green.X, precision: 2);
        Assert.Equal(50f, green.Width, precision: 2);
        Assert.Equal(130f, SingleFill(displayList, Blue).Rect.X, precision: 2);
    }

    [Theory]
    [InlineData("margin-right:auto", 0f, 250f)]
    [InlineData("margin-left:auto", 200f, 250f)]
    [InlineData("margin:0 auto", 100f, 250f)]
    public async Task BuildDisplayList_FlexItemAutoMarginsTakeTheFreeSpace(string redMargin, float expectedRedX, float expectedGreenX)
    {
        // e.g. MDN's breadcrumbs (margin-right: auto) push the other items to the end
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <div style="display:flex; justify-content:center; width:300px">
                    <div style="width:50px; height:10px; {redMargin}; background-color:#ff0000"></div>
                    <div style="width:50px; height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(expectedRedX, SingleFill(displayList, Red).Rect.X, precision: 2);
        Assert.Equal(expectedGreenX, SingleFill(displayList, Green).Rect.X, precision: 2);
    }

    [Theory]
    [InlineData("justify-content:end")]
    [InlineData("justify-content:flex-end")]
    [InlineData("justify-content:safe end")]
    [InlineData("justify-content:right")]
    public async Task BuildDisplayList_FlexJustifyContentUnderstandsBoxAlignmentKeywords(string justifyContent)
    {
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <div style="display:flex; {justifyContent}; width:300px">
                    <div style="width:50px; height:10px; background-color:#ff0000"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(250f, SingleFill(BuildDisplayList(document), Red).Rect.X, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FlexTextRunsAreItemsOfTheirOwnSize()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:300px">Text<div style="width:20px; height:10px; background-color:#ff0000"></div></div>
                <div style="display:flex; width:300px; background-color:#00ff00"><div style="display:flex">Text</div></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);
        var redX = SingleFill(displayList, Red).Rect.X;

        // after the text, which is about 30px wide at 16px
        Assert.InRange(redX, 20f, 60f);
        Assert.Equal(0f, displayList.Commands.OfType<DrawTextCommand>().First().X, precision: 2);
        Assert.True(SingleFill(displayList, Green).Rect.Height >= 16f);
    }

    [Fact]
    public async Task BuildDisplayList_CenteredColumnFlexItemsAreAsWideAsTheirContent()
    {
        // found on GitHub: its repository tabs are centered in column flex items and vanished
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; flex-direction:column; align-items:center; width:200px">
                    <div style="display:inline-flex; height:10px; background-color:#ff0000"><span style="display:inline-block; width:40px"></span></div>
                </div>
            </body></html>
            """);

        var red = SingleFill(BuildDisplayList(document), Red).Rect;

        Assert.Equal(40f, red.Width, precision: 2);
        Assert.Equal(80f, red.X, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_TextFittingItsShrinkToFitBoxGetsNoEllipsis()
    {
        // found on GitHub: the repository name in its breadcrumb was cut, as the width of the
        // text and the one of its box differed by a rounding error
        var boxes = string.Concat(Enumerable.Range(0, 15).Select(i =>
            $"<div style=\"display:flex\"><div style=\"width:{100 + i * 1.37f}px\"></div><a style=\"display:inline-flex; padding:0 6px\"><span style=\"display:inline-block; overflow:hidden; white-space:nowrap; text-overflow:ellipsis; font-weight:600; font-size:{12 + i * 0.25f}px\">AngleSharp</span></a></div>"));
        var document = await ParseAsync($"<html><body style=\"margin:0\">{boxes}</body></html>");

        var texts = BuildDisplayList(document).Commands.OfType<DrawTextCommand>().Select(command => command.Text).ToList();

        Assert.Equal(15, texts.Count(text => text == "AngleSharp"));
    }

    [Fact]
    public async Task BuildDisplayList_ZIndexedBoxKeepsItsPlaceButPaintsAbove()
    {
        // found on GitHub: its header (position: relative; z-index: 32) moved below the page
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="position:relative; z-index:1; height:20px; margin-bottom:-10px; background-color:#ff0000"></div>
                <div style="height:20px; background-color:#00ff00"></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);
        var fills = displayList.Commands.OfType<FillRectCommand>().ToList();
        var red = SingleFill(displayList, Red);
        var green = SingleFill(displayList, Green);

        Assert.Equal(0f, red.Rect.Y, precision: 2);
        Assert.Equal(10f, green.Rect.Y, precision: 2);
        Assert.True(fills.IndexOf(red) > fills.IndexOf(green));
    }

    [Fact]
    public async Task BuildDisplayList_ZIndexedFlexItemsKeepTheirOrder()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex">
                    <div style="position:relative; z-index:2; width:20px; height:10px; background-color:#ff0000"></div>
                    <div style="width:20px; height:10px; background-color:#00ff00"></div>
                    <div style="z-index:1; width:20px; height:10px; background-color:#0000ff"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(0f, SingleFill(displayList, Red).Rect.X, precision: 2);
        Assert.Equal(20f, SingleFill(displayList, Green).Rect.X, precision: 2);
        Assert.Equal(40f, SingleFill(displayList, Blue).Rect.X, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_AbsolutelyPositionedBoxAfterAZIndexedBoxKeepsItsStaticPosition()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="position:relative; z-index:1; height:20px"></div>
                <div style="position:absolute; width:10px; height:10px; background-color:#ff0000"></div>
                <div style="height:30px"></div>
            </body></html>
            """);

        Assert.Equal(20f, SingleFill(BuildDisplayList(document), Red).Rect.Y, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_ShrinkToFitGridIsAsWideAsItsColumns()
    {
        // found on GitHub: the label and icons of its buttons (a grid in an inline-flex) overlapped
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:inline-block; background-color:#0000ff">
                    <div style="display:grid; grid-template-columns:min-content minmax(0, auto) min-content; column-gap:5px">
                        <div style="width:10px; height:10px; background-color:#ff0000"></div>
                        <div style="width:20px; height:10px; background-color:#00ff00"></div>
                        <div style="width:10px; height:10px; background-color:#ffff00"></div>
                    </div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(50f, SingleFill(displayList, Blue).Rect.Width, precision: 2);
        Assert.Equal(15f, SingleFill(displayList, Green).Rect.X, precision: 2);
        Assert.Equal(40f, SingleFill(displayList, new RenderColor(255, 255, 0)).Rect.X, precision: 2);
    }

    [Theory]
    [InlineData("display:flex")]
    [InlineData("display:block")]
    [InlineData("display:grid")]
    public async Task BuildDisplayList_BlockLevelButtonIsAsWideAsItsContent(string display)
    {
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <button style="{display}; padding:0; border:0; background-color:#ff0000"><span style="display:inline-block; width:30px; height:10px"></span></button>
            </body></html>
            """);

        Assert.Equal(30f, SingleFill(BuildDisplayList(document), Red).Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_AbsolutelyPositionedFlexChildrenAreNoFlexItems()
    {
        // found on MDN: the chevron of its Baseline box (position: absolute; right: 1rem)
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; align-items:center; position:relative; width:300px; height:40px; padding-right:40px">
                    <div style="width:50px; height:10px; margin-left:auto; background-color:#00ff00"></div>
                    <div style="position:absolute; right:10px; width:20px; height:20px; background-color:#ff0000"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document, width: 500);
        var red = SingleFill(displayList, Red).Rect;

        Assert.Equal(250f, SingleFill(displayList, Green).Rect.X, precision: 2);
        Assert.Equal(310f, red.X, precision: 2);
        // top: auto keeps it where it would be as the only (centered) item
        Assert.Equal(10f, red.Y, precision: 2);
    }

    [Theory]
    [InlineData("left:0; top:0; right:0; bottom:0", 0f, 200f, 0f, 100f)]
    [InlineData("left:10px; top:10px; bottom:20px; width:50px", 10f, 50f, 10f, 70f)]
    [InlineData("inset:5px", 5f, 190f, 5f, 90f)]
    [InlineData("top:10%; bottom:10%; left:0; width:10px", 0f, 10f, 10f, 80f)]
    [InlineData("top:0; bottom:0; left:0; width:10px; padding:5px; margin:10px", 10f, 20f, 10f, 80f)]
    public async Task BuildDisplayList_AbsolutelyPositionedBoxStretchesBetweenTopAndBottom(string offsets, float x, float width, float y, float height)
    {
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <div style="position:relative; width:200px; height:100px"><div style="position:absolute; {offsets}; background-color:#ff0000"></div></div>
            </body></html>
            """);

        var red = SingleFill(BuildDisplayList(document), Red).Rect;

        Assert.Equal(x, red.X, precision: 1);
        Assert.Equal(width, red.Width, precision: 1);
        Assert.Equal(y, red.Y, precision: 1);
        Assert.Equal(height, red.Height, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_FixedBoxStretchesBetweenTopAndBottomOfTheViewport()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="position:fixed; top:10px; bottom:30px; left:0; width:20px; background-color:#ff0000"></div>
            </body></html>
            """);

        var displayList = new HtmlRenderer().BuildDisplayList(document, new DefaultRenderDevice { ViewPortWidth = 200, ViewPortHeight = 300 });

        Assert.Equal(260f, SingleFill(displayList, Red).Rect.Height, precision: 1);
    }

    [Fact]
    public async Task BuildDisplayList_AbsolutelyPositionedBoxWithAutoOffsetsStaysInPlace()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="position:relative; padding-left:20px">
                    <div style="height:30px"></div>
                    <div style="position:absolute; width:10px; height:10px; background-color:#ff0000"></div>
                    <div style="position:absolute; top:0; width:10px; height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);
        var red = SingleFill(displayList, Red).Rect;
        var green = SingleFill(displayList, Green).Rect;

        Assert.Equal(20f, red.X, precision: 2);
        Assert.Equal(30f, red.Y, precision: 2);
        Assert.Equal(20f, green.X, precision: 2);
        Assert.Equal(0f, green.Y, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_FlexContainerWithoutItemsKeepsItsSize()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:flex; width:100px; height:40px; background-color:#ff0000"></div>
            </body></html>
            """);

        var red = SingleFill(BuildDisplayList(document), Red).Rect;

        Assert.Equal(100f, red.Width, precision: 2);
        Assert.Equal(40f, red.Height, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_LetterSpacingCountsInTheMaxContentWidth()
    {
        // found on MDN: its "Widely available" pill wrapped, being a little too narrow
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="float:left; clear:left; background-color:#ff0000">aa bb</div>
                <div style="float:left; clear:left; letter-spacing:4px; background-color:#00ff00">aa bb</div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        // the spacing goes between all of the five characters
        Assert.Equal(SingleFill(displayList, Red).Rect.Width + 16f, SingleFill(displayList, Green).Rect.Width, precision: 1);
    }

    [Theory]
    [InlineData("justify-self:start", 0f, 30f)]
    [InlineData("justify-self:center", 35f, 30f)]
    [InlineData("justify-self:end", 70f, 30f)]
    [InlineData("justify-self:stretch", 0f, 100f)]
    public async Task BuildDisplayList_GridJustifySelfPlacesAShrinkToFitItem(string justifySelf, float expectedX, float expectedWidth)
    {
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:100px">
                    <div style="{justifySelf}; background-color:#ff0000"><span style="display:inline-block; width:30px; height:10px"></span></div>
                </div>
            </body></html>
            """);

        var item = SingleFill(BuildDisplayList(document), Red).Rect;

        Assert.Equal(expectedX, item.X, precision: 2);
        Assert.Equal(expectedWidth, item.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_GridJustifyItemsAndAlignSelfApplyToSizedItems()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:100px; grid-template-rows:50px; justify-items:end">
                    <div style="align-self:center; width:20px; height:10px; background-color:#ff0000"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(new RenderRect(80f, 20f, 20f, 10f), SingleFill(BuildDisplayList(document), Red).Rect);
    }

    [Theory]
    [InlineData("b / d", 50f, 100f)]
    [InlineData("a / span 2", 0f, 100f)]
    [InlineData("-2", 100f, 50f)]
    [InlineData("c", 100f, 50f)]
    public async Task BuildDisplayList_GridPlacesItemsByNamedAndNegativeLines(string gridColumn, float expectedX, float expectedWidth)
    {
        var document = await ParseAsync($"""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:[a] 50px [b] 50px [c] 50px [d]">
                    <div style="grid-column:{gridColumn}; height:10px; background-color:#ff0000"></div>
                </div>
            </body></html>
            """);

        var item = SingleFill(BuildDisplayList(document), Red).Rect;

        Assert.Equal(expectedX, item.X, precision: 2);
        Assert.Equal(expectedWidth, item.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_GridAreaNamesTheLinesAroundTheArea()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-columns:50px 50px 50px; grid-template-areas:'x y y'">
                    <div style="grid-column:y-start / y-end; height:10px; background-color:#ff0000"></div>
                    <div style="grid-column:y; grid-row:2; height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(new RenderRect(50f, 0f, 100f, 10f), SingleFill(displayList, Red).Rect);
        Assert.Equal(new RenderRect(50f, 10f, 100f, 10f), SingleFill(displayList, Green).Rect);
    }

    [Fact]
    public async Task BuildDisplayList_GridFractionRowsWithoutAHeightKeepTheirRatioAndFitTheirContent()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:grid; grid-template-rows:1fr 2fr; background-color:#0000ff">
                    <div style="height:30px; background-color:#ff0000"></div>
                    <div style="height:10px; background-color:#00ff00"></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(30f, SingleFill(displayList, Green).Rect.Y, precision: 2);
        Assert.Equal(90f, SingleFill(displayList, Blue).Rect.Height, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_GridHeightIsNotGrownByItsContent()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="display:grid; height:20px; background-color:#0000ff">
                    <div style="height:50px"></div>
                </div>
            </body></html>
            """);

        Assert.Equal(20f, SingleFill(BuildDisplayList(document), Blue).Rect.Height);
    }

    [Theory]
    [InlineData("display:flex")]
    [InlineData("display:grid")]
    [InlineData("display:table")]
    public async Task BuildDisplayList_ContainerEffectsWrapTheContainerAndItsChildren(string display)
    {
        var child = display == "display:table"
            ? """<div style="display:table-row"><div style="display:table-cell; width:10px; height:10px; background-color:#00ff00"></div></div>"""
            : """<div style="width:10px; height:10px; background-color:#00ff00"></div>""";
        var document = await ParseAsync($"""
            <html><body>
                <div style="{display}; transform:translate(5px, 0); opacity:0.5; filter:blur(1px); background-color:#ff0000">{child}</div>
            </body></html>
            """);

        var commands = BuildDisplayList(document).Commands.ToList();
        var background = commands.FindIndex(c => c is FillRectCommand fill && fill.Color.Equals(Red));
        var item = commands.FindIndex(c => c is FillRectCommand fill && fill.Color.Equals(Green));

        foreach (var (push, pop) in new (Type Push, Type Pop)[]
                 {
                     (typeof(PushTransformCommand), typeof(PopTransformCommand)),
                     (typeof(PushOpacityCommand), typeof(PopOpacityCommand)),
                     (typeof(PushFilterCommand), typeof(PopFilterCommand)),
                 })
        {
            var pushIndex = commands.FindIndex(c => c.GetType() == push);
            var popIndex = commands.FindLastIndex(c => c.GetType() == pop);

            Assert.True(pushIndex >= 0 && pushIndex < background && background < item && item < popIndex,
                $"{push.Name} at {pushIndex}, background at {background}, item at {item}, {pop.Name} at {popIndex}.");
        }
    }

    [Fact]
    public async Task BuildDisplayList_VisibilityHiddenKeepsTheBoxsSpaceButPaintsNothing()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="visibility:hidden; height:20px; background-color:#ff0000; border:2px solid #ff0000">Hidden</div>
                <div style="height:10px; background-color:#00ff00"></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
        Assert.DoesNotContain(displayList.Commands.OfType<DrawTextCommand>(), text => text.Text == "Hidden");
        Assert.Equal(24f, SingleFill(displayList, Green).Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_VisibleDescendantOfAHiddenBoxIsPainted()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <div style="visibility:hidden; background-color:#ff0000">A <span style="visibility:visible">B</span></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
        Assert.DoesNotContain(displayList.Commands.OfType<DrawTextCommand>(), text => text.Text.Contains('A'));
        Assert.True(SingleText(displayList, "B").X > 0f);
    }

    [Fact]
    public async Task BuildDisplayList_HiddenInlineContentKeepsItsWidthOnTheLine()
    {
        var hidden = BuildDisplayList(await ParseAsync("""<html><body><p>One <span style="visibility:hidden; background-color:#ff0000">Two</span> Three</p></body></html>"""));
        var shown = BuildDisplayList(await ParseAsync("""<html><body><p>One <span>Two</span> Three</p></body></html>"""));

        Assert.DoesNotContain(hidden.Commands.OfType<DrawTextCommand>(), text => text.Text.Contains("Two"));
        Assert.DoesNotContain(hidden.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
        Assert.Equal(SingleText(shown, "Three").X, SingleText(hidden, "Three").X, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_HiddenTableCellKeepsItsColumn()
    {
        var document = await ParseAsync("""
            <html><body style="margin:0">
                <table style="border-spacing:0">
                    <tr>
                        <td style="visibility:hidden; padding:0; width:20px; height:10px; background-color:#ff0000"></td>
                        <td style="padding:0; width:20px; height:10px; background-color:#00ff00"></td>
                    </tr>
                </table>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.DoesNotContain(displayList.Commands.OfType<FillRectCommand>(), fill => fill.Color.Equals(Red));
        Assert.Equal(20f, SingleFill(displayList, Green).Rect.X);
    }

    [Fact]
    public async Task BuildDisplayList_TableStructureComesFromDisplayNotTagNames()
    {
        // <div>s styled as a table lay out as one; a <tr> styled as a block does not count as a row.
        var document = await ParseAsync("""
            <html><body>
                <div style="display:table">
                    <div style="display:table-row"><div style="display:table-cell">A</div><div style="display:table-cell">B</div></div>
                    <div style="display:table-row"><div style="display:table-cell">C</div><div style="display:table-cell">D</div></div>
                </div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        var a = SingleText(displayList, "A");
        var b = SingleText(displayList, "B");
        var c = SingleText(displayList, "C");

        Assert.Equal(a.Y, b.Y);
        Assert.True(b.X > a.X);
        Assert.True(c.Y > a.Y);
        Assert.Equal(a.X, c.X);
    }

    private static FillRectCommand SingleFill(DisplayList displayList, RenderColor color) =>
        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(color)));

    private static readonly RenderColor Red = new(255, 0, 0);
    private static readonly RenderColor Green = new(0, 255, 0);
    private static readonly RenderColor Blue = new(0, 0, 255);

    [Fact]
    public async Task BuildDisplayList_FloatRightSitsAtTheRightEdge()
    {
        var document = await ParseAsync("<html><body><div style=\"float:right; width:50px; height:20px; background-color:#ff0000\"></div></body></html>");

        var fill = SingleFill(BuildDisplayList(document), Red);

        Assert.Equal(350f, fill.Rect.X);
        Assert.Equal(0f, fill.Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_FloatRightWithMarginsAndAutoWidth()
    {
        var document = await ParseAsync("<html><body><div style=\"float:right; margin-right:10px; background-color:#ff0000\">Hi</div></body></html>");

        var fill = SingleFill(BuildDisplayList(document), Red);

        Assert.InRange(fill.Rect.Width, 10f, 30f);
        Assert.Equal(390f, fill.Rect.X + fill.Rect.Width, precision: 2);
    }

    [Fact]
    public async Task BuildDisplayList_TextWrapsBesideARightFloat()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:right; width:300px; height:200px; background-color:#ff0000"></div>
                <p style="margin:0">alpha beta gamma delta epsilon zeta</p>
            </body></html>
            """);

        var texts = BuildDisplayList(document).Commands.OfType<DrawTextCommand>().ToList();

        // Only 100px are left beside the float, so the paragraph needs several lines, all of
        // which start at the left edge.
        Assert.True(texts.Select(t => t.Y).Distinct().Count() >= 3);
        Assert.All(texts, t => Assert.Equal(0f, t.X));
    }

    [Fact]
    public async Task BuildDisplayList_LeftAndRightFloatsShareTheTopOfTheLine()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:left; width:50px; height:20px; background-color:#ff0000"></div>
                <div style="float:right; width:60px; height:20px; background-color:#00ff00"></div>
                <p style="margin:0">middle</p>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);
        var left = SingleFill(displayList, Red);
        var right = SingleFill(displayList, Green);
        var middle = SingleText(displayList, "middle");

        Assert.Equal(0f, left.Rect.Y);
        Assert.Equal(0f, right.Rect.Y);
        Assert.Equal(340f, right.Rect.X);
        Assert.Equal(50f, middle.X);
    }

    [Fact]
    public async Task BuildDisplayList_ConsecutiveRightFloatsStackTowardsTheLeft()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:right; width:50px; height:20px; background-color:#ff0000"></div>
                <div style="float:right; width:50px; height:20px; background-color:#00ff00"></div>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(350f, SingleFill(displayList, Red).Rect.X);
        Assert.Equal(300f, SingleFill(displayList, Green).Rect.X);
        Assert.Equal(0f, SingleFill(displayList, Green).Rect.Y);
    }

    [Fact]
    public async Task BuildDisplayList_AFloatThatDoesNotFitMovesBelowTheOthers()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:left; width:250px; height:30px; background-color:#ff0000"></div>
                <div style="float:right; width:200px; height:20px; background-color:#00ff00"></div>
            </body></html>
            """);

        var green = SingleFill(BuildDisplayList(document), Green);

        Assert.Equal(30f, green.Rect.Y);
        Assert.Equal(200f, green.Rect.X);
    }

    [Theory]
    [InlineData("right", true)]
    [InlineData("both", true)]
    [InlineData("left", false)]
    public async Task BuildDisplayList_ClearRightOnlyClearsRightFloats(string clear, bool expectMovedBelow)
    {
        var document = await ParseAsync($"""
            <html><body>
                <div style="float:right; width:50px; height:80px; background-color:#ff0000"></div>
                <div style="clear:{clear}">After</div>
            </body></html>
            """);

        var after = SingleText(BuildDisplayList(document), "After");

        Assert.Equal(expectMovedBelow, after.Y > 80f);
    }

    [Fact]
    public async Task BuildDisplayList_FloatAffectsTheLinesOfEveryFollowingBlockBesideIt()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:left; width:50px; height:200px; background-color:#ff0000"></div>
                <p style="margin:0">one</p>
                <p style="margin:0">two</p>
            </body></html>
            """);

        var displayList = BuildDisplayList(document);

        Assert.Equal(50f, SingleText(displayList, "one").X);
        Assert.Equal(50f, SingleText(displayList, "two").X);
    }

    [Fact]
    public async Task BuildDisplayList_ABoxWithItsOwnFormattingContextAvoidsFloatsAsAWhole()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="float:left; width:50px; height:40px; background-color:#ff0000"></div>
                <div style="overflow:hidden; height:20px; background-color:#00ff00"></div>
            </body></html>
            """);

        var green = SingleFill(BuildDisplayList(document), Green);

        Assert.Equal(50f, green.Rect.X);
        Assert.Equal(350f, green.Rect.Width);
    }

    [Theory]
    [InlineData("overflow:hidden")]
    [InlineData("display:flow-root")]
    public async Task BuildDisplayList_ABoxWithItsOwnFormattingContextContainsItsFloats(string style)
    {
        var document = await ParseAsync($"""
            <html><body>
                <div style="{style}; background-color:#00ff00"><div style="float:right; width:10px; height:60px; background-color:#ff0000"></div></div>
            </body></html>
            """);

        Assert.Equal(60f, SingleFill(BuildDisplayList(document), Green).Rect.Height);
    }

    [Fact]
    public async Task BuildDisplayList_AnOrdinaryBlockDoesNotGrowToContainItsFloats()
    {
        var document = await ParseAsync("""
            <html><body>
                <div style="background-color:#00ff00">x<div style="float:right; width:10px; height:60px; background-color:#ff0000"></div></div>
            </body></html>
            """);

        Assert.True(SingleFill(BuildDisplayList(document), Green).Rect.Height < 60f);
    }

    private static FillRectCommand SingleGradientFill(DisplayList displayList) =>
        Assert.Single(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Paint is RenderGradientPaint));

    private const string OriginTestBox = "width:40px; height:20px; padding:5px; border:10px solid rgba(0,0,0,0.5); background-image:linear-gradient(to right, #ff0000, #0000ff)";

    [Fact]
    public async Task BuildDisplayList_BackgroundOriginDefaultsToThePaddingBox()
    {
        var document = await ParseAsync($"<html><body><div style=\"{OriginTestBox}\"></div></body></html>");

        var fill = SingleGradientFill(BuildDisplayList(document));

        // Painted out to the border edge, but sized and positioned inside the border.
        Assert.Equal(new RenderRect(0f, 0f, 70f, 50f), fill.Rect);
        Assert.Equal(new RenderRect(10f, 10f, 50f, 30f), fill.PaintArea);
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundOriginContentBoxPositionsInsideThePadding()
    {
        var document = await ParseAsync($"<html><body><div style=\"{OriginTestBox}; background-origin:content-box\"></div></body></html>");

        var fill = SingleGradientFill(BuildDisplayList(document));

        Assert.Equal(new RenderRect(15f, 15f, 40f, 20f), fill.PaintArea);
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundOriginBorderBoxNeedsNoSeparatePaintArea()
    {
        var document = await ParseAsync($"<html><body><div style=\"{OriginTestBox}; background-origin:border-box\"></div></body></html>");

        var fill = SingleGradientFill(BuildDisplayList(document));

        Assert.Null(fill.PaintArea);
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundOriginAndClipAreIndependent()
    {
        var document = await ParseAsync($"<html><body><div style=\"{OriginTestBox}; background-origin:border-box; background-clip:content-box\"></div></body></html>");

        var fill = SingleGradientFill(BuildDisplayList(document));

        Assert.Equal(new RenderRect(15f, 15f, 40f, 20f), fill.Rect);
        Assert.Equal(new RenderRect(0f, 0f, 70f, 50f), fill.PaintArea);
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundColorIgnoresBackgroundOrigin()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:20px; border:10px solid #000000; background-color:#00ff00; background-origin:content-box\"></div></body></html>");

        var fill = SingleFill(BuildDisplayList(document), Green);

        Assert.Equal(new RenderRect(0f, 0f, 60f, 40f), fill.Rect);
        Assert.Null(fill.PaintArea);
    }

    private const string TilePng = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAYAAACNMs+9AAAAFklEQVR4nGNgoBm4o6HxHxse3gqpBgB9dk4ND7V9KQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task BuildDisplayList_DottedBorderPaintsRoundDots()
    {
        var document = await ParseAsync("<html><body><div style=\"width:60px; height:10px; border-top:4px dotted #ff0000\"></div></body></html>");

        var dots = BuildDisplayList(document).Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(Red)).ToList();

        Assert.True(dots.Count > 3);
        Assert.All(dots, dot => Assert.Equal(2f, dot.Radii.TopLeftX));
        Assert.All(dots, dot => Assert.True(dot.Rect.Width <= 4f));
    }

    [Theory]
    [InlineData("inset", true)]
    [InlineData("outset", false)]
    public async Task BuildDisplayList_InsetAndOutsetShadeOppositeEdges(string style, bool topIsDarker)
    {
        var document = await ParseAsync($"<html><body><div style=\"width:40px; height:40px; border:6px {style} rgb(150,150,150)\"></div></body></html>");

        var fills = BuildDisplayList(document).Commands.OfType<FillRectCommand>().Skip(1).ToList();
        var top = fills.First(f => f.Rect.Y == 0f && f.Rect.Height == 6f);
        var bottom = fills.First(f => f.Rect.Height == 6f && f.Rect.Y > 0f);

        Assert.Equal(topIsDarker, top.Color.R < bottom.Color.R);
    }

    [Fact]
    public async Task BuildDisplayList_GrooveSplitsEachEdgeIntoTwoShades()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; border:6px groove rgb(150,150,150)\"></div></body></html>");

        var topBands = BuildDisplayList(document).Commands.OfType<FillRectCommand>().Where(f => f.Rect.Width == 52f && f.Rect.Height == 3f && f.Rect.Y < 6f).ToList();

        Assert.Equal(2, topBands.Count);
        Assert.NotEqual(topBands[0].Color, topBands[1].Color);
    }

    [Fact]
    public async Task BuildDisplayList_IndividualTransformPropertiesComposeBeforeTransform()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; translate:10px 20px; background-color:#ff0000\"></div></body></html>");

        var push = Assert.Single(BuildDisplayList(document).Commands.OfType<PushTransformCommand>());

        Assert.Equal(10f, push.Transform.E, precision: 3);
        Assert.Equal(20f, push.Transform.F, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_RotateAndScalePropertiesBuildTheirMatrix()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; rotate:90deg; scale:2; background-color:#ff0000\"></div></body></html>");

        var transform = Assert.Single(BuildDisplayList(document).Commands.OfType<PushTransformCommand>()).Transform;

        // rotate(90deg) then scale(2): [0 -2; 2 0].
        Assert.Equal(0f, transform.A, precision: 3);
        Assert.Equal(2f, transform.B, precision: 3);
        Assert.Equal(-2f, transform.C, precision: 3);
        Assert.Equal(0f, transform.D, precision: 3);
    }

    [Fact]
    public async Task BuildDisplayList_PaintsEveryBackgroundLayerBottomFirst()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; padding:5px; background-color:#00ff00; background-image:linear-gradient(#ff0000, #0000ff), radial-gradient(#ffffff, #000000); background-clip:content-box, border-box\"></div></body></html>");

        var fills = BuildDisplayList(document).Commands.OfType<FillRectCommand>().ToList();
        var gradients = fills.Where(f => f.Paint is RenderGradientPaint).ToList();
        var color = SingleFill(BuildDisplayList(document), Green);

        Assert.Equal(2, gradients.Count);

        // The radial (bottom) layer comes first and covers the border box; the linear (top)
        // layer is clipped to the content box. The color takes the bottom layer's clip.
        Assert.Equal(RenderGradientKind.Radial, ((RenderGradientPaint)gradients[0].Paint).Gradient.Kind);
        Assert.Equal(new RenderRect(0f, 0f, 50f, 50f), gradients[0].Rect);
        Assert.Equal(RenderGradientKind.Linear, ((RenderGradientPaint)gradients[1].Paint).Gradient.Kind);
        Assert.Equal(new RenderRect(5f, 5f, 40f, 40f), gradients[1].Rect);
        Assert.Equal(new RenderRect(0f, 0f, 50f, 50f), color.Rect);
    }

    [Fact]
    public async Task BuildDisplayList_SizedGradientLayerGetsATile()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:40px; background-image:linear-gradient(#ff0000, #0000ff); background-size:10px 20px; background-repeat:repeat-x\"></div></body></html>");

        var fill = SingleGradientFill(BuildDisplayList(document));
        var tile = Assert.IsType<RenderGradientPaint>(fill.Paint).Tile;

        Assert.NotNull(tile);
        Assert.Equal(10f, tile!.Size.Width.Value);
        Assert.Equal(20f, tile.Size.Height.Value);
        Assert.Equal(RenderBackgroundRepeat.Repeat, tile.RepeatX);
        Assert.Equal(RenderBackgroundRepeat.NoRepeat, tile.RepeatY);
    }

    [Fact]
    public async Task BuildDisplayList_BackgroundRepeatSpaceAndRoundReachTheImagePaint()
    {
        var document = await ParseAsync($"<html><body><div style=\"width:45px; height:45px; background-image:url({TilePng}); background-repeat:space round\"></div></body></html>");

        var fill = Assert.Single(BuildDisplayList(document).Commands.OfType<FillRectCommand>().Where(f => f.Paint is RenderImagePaint));
        var paint = (RenderImagePaint)fill.Paint;

        Assert.Equal(RenderBackgroundRepeat.Space, paint.EffectiveRepeatX);
        Assert.Equal(RenderBackgroundRepeat.Round, paint.EffectiveRepeatY);
    }

    [Fact]
    public async Task BuildDisplayList_BorderImageReplacesTheBorder()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:30px; border:8px solid #ff0000; border-image:linear-gradient(#0000ff, #00ff00) 25% / 8px / 4px round\"></div></body></html>");

        var displayList = BuildDisplayList(document);
        var command = Assert.Single(displayList.Commands.OfType<DrawBorderImageCommand>());

        Assert.Empty(displayList.Commands.OfType<FillRectCommand>().Where(f => f.Color.Equals(Red)));
        Assert.Equal(new RenderRect(-4f, -4f, 64f, 54f), command.Area);
        Assert.IsType<RenderGradientPaint>(command.Image.Source);
        Assert.Equal(new RenderBorderImageSlice(0.25f, true), command.Image.SliceTop);
        Assert.Equal(8f, command.Image.WidthLeft);
        Assert.Equal(RenderBorderImageRepeat.Round, command.Image.RepeatX);
        Assert.Equal(RenderBorderImageRepeat.Round, command.Image.RepeatY);
        Assert.False(command.Image.Fill);
    }

    [Fact]
    public async Task BuildDisplayList_BorderImageWidthNumbersAreMultiplesOfTheBorderWidth()
    {
        // Needs AngleSharp.Css to compute a unitless `border-image-width` (reported upstream; it
        // currently throws while building the render tree).
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:30px; border:8px solid #ff0000; border-image:linear-gradient(#0000ff, #00ff00) 25%; border-image-width:2 1\"></div></body></html>");

        var command = Assert.Single(BuildDisplayList(document).Commands.OfType<DrawBorderImageCommand>());

        Assert.Equal(16f, command.Image.WidthTop);
        Assert.Equal(8f, command.Image.WidthLeft);
    }

    [Fact]
    public async Task BuildDisplayList_MaskImageMasksTheWholeElement()
    {
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:30px; padding:5px; background-color:#ff0000; mask-image:linear-gradient(#000000, transparent); mask-mode:luminance\"></div></body></html>");

        var commands = BuildDisplayList(document).Commands.ToList();
        var push = Assert.Single(commands.OfType<PushMaskCommand>());
        var layer = Assert.Single(push.Layers);

        Assert.Equal(new RenderRect(0f, 0f, 50f, 40f), layer.Area);
        Assert.True(layer.UseLuminance);
        Assert.IsType<RenderGradientPaint>(layer.Paint);

        var pushIndex = commands.IndexOf(push);
        var fillIndex = commands.FindIndex(c => c is FillRectCommand f && f.Color.Equals(Red));
        var popIndex = commands.FindIndex(c => c is PopMaskCommand);
        Assert.True(pushIndex < fillIndex && fillIndex < popIndex);
    }

    [Fact]
    public async Task BuildDisplayList_MaskSizeSizesTheMaskImage()
    {
        // Needs AngleSharp.Css to compute `mask-size` (reported upstream).
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:30px; background-color:#ff0000; mask-image:linear-gradient(#000000, transparent); mask-size:10px 10px\"></div></body></html>");

        var layer = Assert.Single(Assert.Single(BuildDisplayList(document).Commands.OfType<PushMaskCommand>()).Layers);
        var tile = Assert.IsType<RenderGradientPaint>(layer.Paint).Tile;

        Assert.NotNull(tile);
        Assert.Equal(10f, tile!.Size.Width.Value);
    }

    [Fact]
    public async Task BuildDisplayList_BackdropFilterFiltersBehindTheBox()
    {
        // Needs AngleSharp.Css to compute `backdrop-filter` (reported upstream).
        var document = await ParseAsync("<html><body><div style=\"width:40px; height:30px; border-radius:6px; backdrop-filter:blur(4px)\"></div></body></html>");

        var command = Assert.Single(BuildDisplayList(document).Commands.OfType<DrawBackdropFilterCommand>());

        Assert.Equal(new RenderRect(0f, 0f, 40f, 30f), command.Rect);
        Assert.Equal(6f, command.Radii.TopLeftX);
        Assert.Equal(RenderFilterFunctionKind.Blur, Assert.Single(command.Functions).Kind);
    }

    [Fact]
    public async Task BuildDisplayList_HebrewTextIsPaintedInVisualOrder()
    {
        var document = await ParseAsync("<html><body><p dir=\"rtl\" style=\"direction:rtl\">\u05e9\u05dc\u05d5\u05dd \u05e2\u05d5\u05dc\u05dd</p></body></html>");

        var text = Assert.Single(BuildDisplayList(document).Commands.OfType<DrawTextCommand>());

        // The logical "shalom olam" is drawn right-to-left: last letter first, words swapped.
        Assert.Equal("\u05dd\u05dc\u05d5\u05e2 \u05dd\u05d5\u05dc\u05e9", text.Text);
    }

    [Fact]
    public async Task BuildDisplayList_HebrewInsideLeftToRightTextIsReorderedInPlace()
    {
        var document = await ParseAsync("<html><body><p>abc \u05d0\u05d1\u05d2 def</p></body></html>");

        var texts = BuildDisplayList(document).Commands.OfType<DrawTextCommand>().ToList();

        Assert.Equal(["abc", "\u05d2\u05d1\u05d0", "def"], texts.Select(t => t.Text.Trim()));
        Assert.True(texts[0].X < texts[1].X && texts[1].X < texts[2].X);
    }

    [Fact]
    public async Task BuildDisplayList_MixedInlineContentIsReorderedAcrossElements()
    {
        var document = await ParseAsync("<html><body><p style=\"direction:rtl\">\u05d0\u05d1 <b>\u05d2\u05d3</b> (1)</p></body></html>");

        var texts = BuildDisplayList(document).Commands.OfType<DrawTextCommand>().OrderBy(t => t.X).Select(t => t.Text.Trim()).ToList();

        // Right-to-left from the right edge: "אב", then the bold "גד", then "(1)" - so visually,
        // left to right, the mirrored "(1)" comes first and "אב" last.
        Assert.Equal(["(1)", "\u05d3\u05d2", "\u05d1\u05d0"], texts.Where(t => t.Length > 0));
    }

    [Fact]
    public async Task BuildDisplayList_UnicodeBidiOverrideForcesTheElementsDirection()
    {
        var document = await ParseAsync("<html><body><p>ab <span style=\"direction:rtl; unicode-bidi:bidi-override\">xyz</span> cd</p></body></html>");

        var texts = BuildDisplayList(document).Commands.OfType<DrawTextCommand>().Select(t => t.Text.Trim()).ToList();

        Assert.Contains("zyx", texts);
    }

    [Fact]
    public async Task BuildDisplayList_BracketsAreMirroredInRightToLeftText()
    {
        var document = await ParseAsync("<html><body><p style=\"direction:rtl\">\u05d0 (\u05d1)</p></body></html>");

        var text = Assert.Single(BuildDisplayList(document).Commands.OfType<DrawTextCommand>());

        Assert.Equal("(\u05d1) \u05d0", text.Text);
    }

    private static readonly RenderColor FormControlAccentColorForTests = new(26, 115, 232);

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html, IConfiguration? configuration = null, string? address = null)
    {
        var context = BrowsingContext.New(configuration ?? Configuration.Default.WithCss());

        var document = await context.OpenAsync(request =>
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                request.Address(address);
            }

            request.Content(html);
        });

        // The expectations here are in page coordinates without the UA sheet's `body { margin:
        // 8px }`. As the first sheet, any body margin a test sets itself still wins.
        var resetStyle = document.CreateElement("style");
        resetStyle.TextContent = "body { margin: 0 }";
        document.Head?.Prepend(resetStyle);

        return document;
    }

    private sealed class ImageOnlyRequester(byte[] imageData) : BaseRequester
    {
        public override bool SupportsProtocol(string protocol) =>
            string.Equals(protocol, "http", StringComparison.OrdinalIgnoreCase);

        protected override Task<IResponse?> PerformRequestAsync(Request request, CancellationToken cancel)
        {
            var acceptsImages = request.Headers.TryGetValue("Accept", out var accept) && accept.Contains("image/", StringComparison.Ordinal);
            var response = new DefaultResponse
            {
                Address = request.Address,
                StatusCode = acceptsImages ? HttpStatusCode.OK : HttpStatusCode.NotAcceptable,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = acceptsImages ? "image/png" : "text/html",
                },
                Content = new MemoryStream(acceptsImages ? imageData : "<h1>Not acceptable</h1>"u8.ToArray()),
            };

            return Task.FromResult<IResponse?>(response);
        }
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