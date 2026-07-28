using SkiaSharp;

using Xunit.Sdk;

namespace AngleSharp.Renderer.Tests;

internal static class VisualSnapshotVerifier
{
    private const string StrictModeEnvironmentVariable = "ANGLESHARP_SNAPSHOT_STRICT";

    public static void VerifyOrCreate(
        string snapshotName,
        byte[] actualPng,
        byte perChannelTolerance = 0,
        int maxDifferentPixels = 0)
    {
        var projectRoot = GetProjectRoot();
        var verificationAssetsPath = Path.Combine(projectRoot, "verification-assets");
        var failureAssetsPath = Path.Combine(projectRoot, "failure-assets");

        Directory.CreateDirectory(verificationAssetsPath);
        Directory.CreateDirectory(failureAssetsPath);

        var baselinePath = Path.Combine(verificationAssetsPath, snapshotName);
        var failurePath = Path.Combine(failureAssetsPath, snapshotName);
        var diffPath = Path.Combine(failureAssetsPath, Path.GetFileNameWithoutExtension(snapshotName) + ".diff.png");

        if (!File.Exists(baselinePath))
        {
            if (IsStrictModeEnabled())
            {
                File.WriteAllBytes(failurePath, actualPng);

                throw new XunitException(
                    $"Missing baseline snapshot '{snapshotName}' while strict mode is enabled ({StrictModeEnvironmentVariable}=1). " +
                    $"Create baseline at: {baselinePath}. Actual output written to: {failurePath}.");
            }

            File.WriteAllBytes(baselinePath, actualPng);
            return;
        }

        var baselinePng = File.ReadAllBytes(baselinePath);
        var comparison = CompareImages(baselinePng, actualPng, perChannelTolerance);

        if (comparison.IsMatch(maxDifferentPixels))
        {
            if (File.Exists(failurePath))
            {
                File.Delete(failurePath);
            }

            if (File.Exists(diffPath))
            {
                File.Delete(diffPath);
            }

            return;
        }

        File.WriteAllBytes(failurePath, actualPng);
        File.WriteAllBytes(diffPath, comparison.DiffPng);

        throw new XunitException(
            $"Visual snapshot mismatch for '{snapshotName}'. " +
            $"Expected size {comparison.ExpectedWidth}x{comparison.ExpectedHeight}, " +
            $"actual size {comparison.ActualWidth}x{comparison.ActualHeight}, " +
            $"different pixels: {comparison.DifferentPixels} (allowed: {maxDifferentPixels}), " +
            $"channel tolerance: {perChannelTolerance}. " +
            $"Baseline: {baselinePath}. Failure output: {failurePath}. Diff output: {diffPath}.");
    }

    private static bool IsStrictModeEnabled()
    {
        var strictModeValue = Environment.GetEnvironmentVariable(StrictModeEnvironmentVariable);

        return string.Equals(strictModeValue, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(strictModeValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var csprojPath = Path.Combine(current.FullName, "AngleSharp.Renderer.Tests.csproj");

            if (File.Exists(csprojPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate AngleSharp.Renderer.Tests project root.");
    }

    private static ImageComparison CompareImages(byte[] expectedPng, byte[] actualPng, byte perChannelTolerance)
    {
        using var expected = SKBitmap.Decode(expectedPng);
        using var actual = SKBitmap.Decode(actualPng);

        if (expected is null)
        {
            throw new InvalidOperationException("Could not decode expected PNG snapshot.");
        }

        if (actual is null)
        {
            throw new InvalidOperationException("Could not decode actual PNG snapshot.");
        }

        var width = Math.Min(expected.Width, actual.Width);
        var height = Math.Min(expected.Height, actual.Height);
        var differentPixels = 0;
        using var diff = new SKBitmap(Math.Max(expected.Width, actual.Width), Math.Max(expected.Height, actual.Height), SKColorType.Rgba8888, SKAlphaType.Premul);

        // Initialize diff image as transparent for matching pixels.
        diff.Erase(SKColors.Transparent);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var ep = expected.GetPixel(x, y);
                var ap = actual.GetPixel(x, y);

                if (!WithinTolerance(ep.Red, ap.Red, perChannelTolerance) ||
                    !WithinTolerance(ep.Green, ap.Green, perChannelTolerance) ||
                    !WithinTolerance(ep.Blue, ap.Blue, perChannelTolerance) ||
                    !WithinTolerance(ep.Alpha, ap.Alpha, perChannelTolerance))
                {
                    differentPixels++;
                    diff.SetPixel(x, y, new SKColor(255, 0, 0, 200));
                }
            }
        }

        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            differentPixels += Math.Abs(expected.Width * expected.Height - actual.Width * actual.Height);

            var maxWidth = Math.Max(expected.Width, actual.Width);
            var maxHeight = Math.Max(expected.Height, actual.Height);

            for (var y = 0; y < maxHeight; y++)
            {
                for (var x = 0; x < maxWidth; x++)
                {
                    var isOutsideExpected = x >= expected.Width || y >= expected.Height;
                    var isOutsideActual = x >= actual.Width || y >= actual.Height;

                    if (isOutsideExpected || isOutsideActual)
                    {
                        diff.SetPixel(x, y, new SKColor(255, 165, 0, 220));
                    }
                }
            }
        }

        var diffPng = EncodeToPng(diff);

        return new ImageComparison(expected.Width, expected.Height, actual.Width, actual.Height, differentPixels, diffPng);
    }

    private static byte[] EncodeToPng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        if (data is null)
        {
            throw new InvalidOperationException("Could not encode diff image to PNG.");
        }

        return data.ToArray();
    }

    private static bool WithinTolerance(byte expected, byte actual, byte tolerance)
    {
        return Math.Abs(expected - actual) <= tolerance;
    }

    private readonly record struct ImageComparison(
        int ExpectedWidth,
        int ExpectedHeight,
        int ActualWidth,
        int ActualHeight,
        int DifferentPixels,
        byte[] DiffPng)
    {
        public bool IsMatch(int maxDifferentPixels) => DifferentPixels <= maxDifferentPixels;
    }
}