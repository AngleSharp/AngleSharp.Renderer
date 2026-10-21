using SkiaSharp;

using Xunit.Sdk;

namespace AngleSharp.Renderer.Tests;

internal static class VisualSnapshotVerifier
{
    private const string StrictModeEnvironmentVariable = "ANGLESHARP_SNAPSHOT_STRICT";
    private const string UpdateModeEnvironmentVariable = "ANGLESHARP_SNAPSHOT_UPDATE";

    /// <summary>
    /// The platforms a baseline has to exist for. Skia rasterizes glyphs through a different
    /// scaler per platform (FreeType on Linux, DirectWrite on Windows, CoreText on macOS), so
    /// the very same font file produces different anti-aliasing and the baselines cannot be shared.
    /// </summary>
    public static readonly string[] SupportedPlatformSuffixes = ["linux", "macos", "windows"];

    /// <summary>
    /// CoreText's glyph anti-aliasing changes between macOS releases, so a baseline rendered on the
    /// pinned CI runner differs from a newer Mac along glyph and shape edges (observed: up to ~40 levels
    /// per channel). A real regression - a moved glyph, edge or wrong color - differs by close to the
    /// full range, so on macOS smaller differences on edge pixels are treated as anti-aliasing noise.
    /// Flat areas still have to match within the test's own tolerance.
    /// </summary>
    private const byte MacOSEdgeTolerance = 64;

    /// <summary>
    /// Gets the directory holding the committed baseline images.
    /// </summary>
    public static string VerificationAssetsPath => Path.Combine(GetProjectRoot(), "verification-assets");

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

        var platformSnapshotName = GetPlatformSnapshotName(snapshotName);
        var baselinePath = Path.Combine(verificationAssetsPath, platformSnapshotName);
        var failurePath = Path.Combine(failureAssetsPath, platformSnapshotName);
        var diffPath = Path.Combine(failureAssetsPath, Path.GetFileNameWithoutExtension(platformSnapshotName) + ".diff.png");

        if (IsEnabled(UpdateModeEnvironmentVariable))
        {
            File.WriteAllBytes(baselinePath, actualPng);
            DeleteIfExists(failurePath);
            DeleteIfExists(diffPath);
            return;
        }

        if (!File.Exists(baselinePath))
        {
            if (IsEnabled(StrictModeEnvironmentVariable))
            {
                File.WriteAllBytes(failurePath, actualPng);

                throw new XunitException(
                    $"Missing baseline snapshot '{platformSnapshotName}' while strict mode is enabled ({StrictModeEnvironmentVariable}=1). " +
                    $"Create baseline at: {baselinePath}. Actual output written to: {failurePath}. " +
                    "Baselines for all platforms are produced by the 'Update Snapshots' GitHub workflow.");
            }

            File.WriteAllBytes(baselinePath, actualPng);
            return;
        }

        var baselinePng = File.ReadAllBytes(baselinePath);
        var edgeTolerance = OperatingSystem.IsMacOS() ? Math.Max(perChannelTolerance, MacOSEdgeTolerance) : perChannelTolerance;
        var comparison = CompareImages(baselinePng, actualPng, perChannelTolerance, (byte)edgeTolerance);

        if (comparison.IsMatch(maxDifferentPixels))
        {
            DeleteIfExists(failurePath);
            DeleteIfExists(diffPath);
            return;
        }

        File.WriteAllBytes(failurePath, actualPng);
        File.WriteAllBytes(diffPath, comparison.DiffPng);

        throw new XunitException(
            $"Visual snapshot mismatch for '{platformSnapshotName}'. " +
            $"Expected size {comparison.ExpectedWidth}x{comparison.ExpectedHeight}, " +
            $"actual size {comparison.ActualWidth}x{comparison.ActualHeight}, " +
            $"different pixels: {comparison.DifferentPixels} ({comparison.DifferentPixelRatio:P2}, allowed: {maxDifferentPixels}), " +
            $"channel tolerance: {perChannelTolerance} (edges: {edgeTolerance}). " +
            $"Baseline: {baselinePath}. Failure output: {failurePath}. Diff output: {diffPath}.");
    }

    private static string GetPlatformSnapshotName(string snapshotName)
    {
        var platformSuffix = GetPlatformSuffix();
        var directory = Path.GetDirectoryName(snapshotName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(snapshotName);
        var extension = Path.GetExtension(snapshotName);
        var platformFileName = string.IsNullOrEmpty(extension)
            ? $"{fileNameWithoutExtension}.{platformSuffix}"
            : $"{fileNameWithoutExtension}.{platformSuffix}{extension}";

        return string.IsNullOrEmpty(directory)
            ? platformFileName
            : Path.Combine(directory, platformFileName);
    }

    private static string GetPlatformSuffix()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return Environment.OSVersion.Platform.ToString().ToLowerInvariant();
    }

    private static bool IsEnabled(string environmentVariable)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
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

    private static ImageComparison CompareImages(byte[] expectedPng, byte[] actualPng, byte perChannelTolerance, byte edgeTolerance)
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

                if (!WithinTolerance(ep, ap, perChannelTolerance) &&
                    (edgeTolerance <= perChannelTolerance || !WithinTolerance(ep, ap, edgeTolerance) || !IsEdgePixel(expected, x, y)))
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

    private static bool WithinTolerance(SKColor expected, SKColor actual, byte tolerance)
    {
        return WithinTolerance(expected.Red, actual.Red, tolerance) &&
               WithinTolerance(expected.Green, actual.Green, tolerance) &&
               WithinTolerance(expected.Blue, actual.Blue, tolerance) &&
               WithinTolerance(expected.Alpha, actual.Alpha, tolerance);
    }

    private static bool WithinTolerance(byte expected, byte actual, byte tolerance)
    {
        return Math.Abs(expected - actual) <= tolerance;
    }

    /// <summary>
    /// A pixel lies on an edge (where anti-aliasing happens) when any of its 8 neighbors differs from it.
    /// </summary>
    private static bool IsEdgePixel(SKBitmap bitmap, int x, int y)
    {
        var center = bitmap.GetPixel(x, y);

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;

                if ((dx != 0 || dy != 0) && nx >= 0 && ny >= 0 && nx < bitmap.Width && ny < bitmap.Height && bitmap.GetPixel(nx, ny) != center)
                {
                    return true;
                }
            }
        }

        return false;
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

        public double DifferentPixelRatio
        {
            get
            {
                var total = Math.Max(ExpectedWidth * ExpectedHeight, ActualWidth * ActualHeight);
                return total > 0 ? (double)DifferentPixels / total : 0d;
            }
        }
    }
}