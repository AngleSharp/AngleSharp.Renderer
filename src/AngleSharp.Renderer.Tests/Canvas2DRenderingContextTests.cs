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

        Assert.Equal(100, canvasContext.Width);
        Assert.Equal(100, canvasContext.Height);
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
