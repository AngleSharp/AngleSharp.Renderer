namespace AngleSharp.Renderer.Tests;

using AngleSharp;
using AngleSharp.Html.Dom;
using SkiaSharp;

public sealed class Canvas2DRenderingContextTests
{
    [Fact]
    public async Task FillRectAndClearRect_ChangePixelContent()
    {
        var context = await CreateContextAsync();

        context.SetFillStyle("#ff0000");
        context.FillRect(0f, 0f, 100f, 100f);

        var filledPixels = CountNonTransparentPixels(context.ToImage("image/png"));
        Assert.True(filledPixels > 100, $"Expected fill to produce visible pixels, but found {filledPixels}.");

        context.ClearRect(0f, 0f, 100f, 100f);

        var clearedPixels = CountNonTransparentPixels(context.ToImage("image/png"));
        Assert.True(clearedPixels < filledPixels, $"Expected clearRect to reduce visible pixels from {filledPixels} to {clearedPixels}.");
    }

    [Fact]
    public async Task TextAndPathDrawing_LeaveVisiblePixels()
    {
        var context = await CreateContextAsync();

        context.SetFillStyle("#00ff00");
        context.SetStrokeStyle("#0000ff");
        context.SetLineWidth(2f);
        context.SetFont("20px sans-serif");

        context.BeginPath();
        context.MoveTo(10f, 10f);
        context.LineTo(90f, 10f);
        context.LineTo(90f, 90f);
        context.ClosePath();
        context.Fill();
        context.Stroke();
        context.FillText("Canvas", 12f, 70f);

        var pixelCount = CountNonTransparentPixels(context.ToImage("image/png"));
        Assert.True(pixelCount > 100, $"Expected text and path drawing to produce visible pixels, but found {pixelCount}.");
    }

    [Fact]
    public async Task SaveAndRestore_PreserveAndRestoreDrawingState()
    {
        var context = await CreateContextAsync();

        context.SetFillStyle("#ff0000");
        context.FillRect(0f, 0f, 50f, 50f);

        context.Save();
        context.SetFillStyle("#0000ff");
        context.FillRect(50f, 0f, 50f, 50f);
        context.Restore();
        context.FillRect(25f, 50f, 50f, 50f);

        var image = context.ToImage("image/png");
        var bitmap = SKBitmap.Decode(image);
        Assert.NotNull(bitmap);

        Assert.True(ContainsColor(bitmap, new SKColor(255, 0, 0, 255)));
        Assert.True(ContainsColor(bitmap, new SKColor(0, 0, 255, 255)));
    }

    [Fact]
    public async Task CanvasContext2D_InterfaceSupportsStateAndDimensions()
    {
        var context = await CreateContextAsync();
        var canvasContext = Assert.IsAssignableFrom<AngleSharp.Media.Dom.ICanvasRenderingContext2D>(context);

        Assert.Same(context.Host, canvasContext.Canvas);
        Assert.Equal(context.Host.Width, canvasContext.Width);
        Assert.Equal(context.Host.Height, canvasContext.Height);

        canvasContext.SaveState();
        canvasContext.Width = 120;
        canvasContext.Height = 80;
        Assert.Equal(120, canvasContext.Width);
        Assert.Equal(80, canvasContext.Height);
        canvasContext.RestoreState();

        // the size of the bitmap is not part of the drawing state
        Assert.Equal(120, canvasContext.Width);
        Assert.Equal(80, canvasContext.Height);
    }

    [Fact]
    public async Task Translate_AffectsSubsequentDrawingOperations()
    {
        var context = await CreateContextAsync();

        context.SetFillStyle("#00ff00");
        context.Translate(10f, 10f);
        context.FillRect(0f, 0f, 20f, 20f);

        var image = context.ToImage("image/png");
        var bitmap = SKBitmap.Decode(image);
        Assert.NotNull(bitmap);

        Assert.True(ContainsColor(bitmap, new SKColor(0, 255, 0, 255)));
    }

    [Fact]
    public async Task GetContext_GivesTheSameContextAgain()
    {
        var document = await ParseAsync("<html><body><canvas width='100' height='100'></canvas></body></html>");
        var canvas = (IHtmlCanvasElement)document.QuerySelector("canvas")!;
        var first = Assert.IsType<Canvas2DRenderingContext>(canvas.GetContext("2d"));
        first.FillRect(0d, 0d, 10d, 10d);

        Assert.Same(first, canvas.GetContext("2d"));
    }

    [Fact]
    public async Task Fill_FillsTheInsideOfThePath()
    {
        var context = await CreateContextAsync();

        context.FillStyle = "#ff0000";
        context.BeginPath();
        context.Arc(50d, 50d, 40d, 0d, Math.PI * 2d);
        context.Fill();

        using var bitmap = SKBitmap.Decode(context.ToImage("image/png"));
        Assert.Equal(new SKColor(255, 0, 0, 255), bitmap.GetPixel(50, 50));
        Assert.Equal(new SKColor(255, 0, 0, 255), bitmap.GetPixel(20, 50));
        Assert.Equal(0, bitmap.GetPixel(5, 5).Alpha);
    }

    [Fact]
    public async Task Transformations_MoveRotateAndScaleWhatIsDrawn()
    {
        var context = await CreateContextAsync();

        context.FillStyle = "rgb(0, 0, 255)";
        context.Translate(50d, 50d);
        context.Rotate(Math.PI / 2d);
        context.Scale(2d, 1d);
        // 0..20 x 0..5 is, rotated a quarter turn clockwise, 40 wide downwards and 5 to the left
        context.FillRect(0d, 0d, 20d, 5d);

        using var bitmap = SKBitmap.Decode(context.ToImage("image/png"));
        Assert.Equal(new SKColor(0, 0, 255, 255), bitmap.GetPixel(47, 85));
        Assert.Equal(0, bitmap.GetPixel(55, 60).Alpha);
    }

    [Fact]
    public async Task GlobalAlphaAndGradients_AreApplied()
    {
        var context = await CreateContextAsync();
        var gradient = context.CreateLinearGradient(0d, 0d, 100d, 0d);
        gradient.AddColorStop(0d, "#000000");
        gradient.AddColorStop(1d, "#ffffff");

        context.FillStyle = gradient;
        Assert.Same(gradient, context.FillStyle);
        context.FillRect(0d, 0d, 100d, 50d);
        context.GlobalAlpha = 0.5d;
        context.FillStyle = "#ff0000";
        context.FillRect(0d, 50d, 100d, 50d);

        using var bitmap = SKBitmap.Decode(context.ToImage("image/png"));
        Assert.True(bitmap.GetPixel(10, 25).Red < 60);
        Assert.True(bitmap.GetPixel(90, 25).Red > 200);
        Assert.InRange(bitmap.GetPixel(50, 75).Alpha, 120, 135);
        Assert.Equal("#ff0000", context.FillStyle);
    }

    [Fact]
    public async Task MeasureText_GivesTheWidthOfTheText()
    {
        var context = await CreateContextAsync();
        context.Font = "20px sans-serif";

        var narrow = context.MeasureText("i");
        var wide = context.MeasureText("iiii");

        Assert.True(narrow.Width > 0d);
        Assert.Equal(narrow.Width * 4d, wide.Width, 1);
        Assert.True(wide.FontBoundingBoxAscent > 10d);
    }

    [Fact]
    public async Task DrawImage_DrawsAnotherCanvas()
    {
        var document = await ParseAsync("<html><body><canvas id='a' width='20' height='20'></canvas><canvas id='b' width='100' height='100'></canvas></body></html>");
        var source = Assert.IsType<Canvas2DRenderingContext>(((IHtmlCanvasElement)document.GetElementById("a")!).GetContext("2d"));
        var target = Assert.IsType<Canvas2DRenderingContext>(((IHtmlCanvasElement)document.GetElementById("b")!).GetContext("2d"));
        source.FillStyle = "#00ff00";
        source.FillRect(0d, 0d, 20d, 20d);

        target.DrawImage(source.Canvas, 0d, 0d, 20d, 20d, 50d, 50d, 40d, 40d);

        using var bitmap = SKBitmap.Decode(target.ToImage("image/png"));
        Assert.Equal(new SKColor(0, 255, 0, 255), bitmap.GetPixel(70, 70));
        Assert.Equal(0, bitmap.GetPixel(40, 40).Alpha);
    }

    [Fact]
    public async Task SettingTheWidth_ClearsTheBitmap()
    {
        var context = await CreateContextAsync();
        context.FillRect(0d, 0d, 100d, 100d);

        context.Canvas.Width = 50;

        using var bitmap = SKBitmap.Decode(context.ToImage("image/png"));
        Assert.Equal(50, bitmap.Width);
        Assert.Equal(0, bitmap.GetPixel(10, 10).Alpha);
    }

    private static async Task<Canvas2DRenderingContext> CreateContextAsync()
    {
        var document = await ParseAsync("<html><body><canvas width='100' height='100'></canvas></body></html>");
        var canvas = document.QuerySelector("canvas") as IHtmlCanvasElement;
        Assert.NotNull(canvas);
        return new Canvas2DRenderingContext(canvas!);
    }

    private static async Task<AngleSharp.Dom.IDocument> ParseAsync(string html)
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss().WithRendering());
        return await context.OpenAsync(request => request.Content(html));
    }

    private static int CountNonTransparentPixels(byte[] imageData)
    {
        using var bitmap = SKBitmap.Decode(imageData);
        var count = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool ContainsColor(SKBitmap bitmap, SKColor targetColor)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);

                if (pixel.Red == targetColor.Red &&
                    pixel.Green == targetColor.Green &&
                    pixel.Blue == targetColor.Blue &&
                    pixel.Alpha == targetColor.Alpha)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
