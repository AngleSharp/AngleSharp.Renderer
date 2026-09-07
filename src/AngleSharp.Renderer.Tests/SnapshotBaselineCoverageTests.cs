namespace AngleSharp.Renderer.Tests;

/// <summary>
/// Guards the snapshot baselines themselves. Visual tests can only compare against the
/// baseline of the platform they happen to run on, so a missing baseline for another
/// platform stays invisible until that platform's CI leg runs - or, when a whole platform
/// has no CI leg, forever. These tests make the gap fail on every platform instead.
/// </summary>
[Trait("Category", "SnapshotCoverage")]
public sealed class SnapshotBaselineCoverageTests
{
    [Fact]
    public void Baselines_ExistForEverySupportedPlatform()
    {
        var missing = new List<string>();

        foreach (var (snapshot, platforms) in EnumerateBaselines())
        {
            foreach (var platform in VisualSnapshotVerifier.SupportedPlatformSuffixes)
            {
                if (!platforms.Contains(platform))
                {
                    missing.Add($"{snapshot}.{platform}.png");
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"Missing baseline snapshots for {missing.Count} platform variant(s):{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing.Order(StringComparer.Ordinal).Select(name => $"  - {name}")) +
            $"{Environment.NewLine}Run the 'Update Snapshots' GitHub workflow to regenerate the baselines " +
            "for every platform, then commit the updated verification-assets.");
    }

    [Fact]
    public void Baselines_UseAKnownPlatformSuffix()
    {
        var unknown = Directory
            .EnumerateFiles(VisualSnapshotVerifier.VerificationAssetsPath, "*.png")
            .Select(path => Path.GetFileName(path))
            .Where(name => !IsKnownBaselineName(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            $"Baseline snapshots without a known platform suffix ({string.Join(", ", VisualSnapshotVerifier.SupportedPlatformSuffixes)}):" +
            $"{Environment.NewLine}" + string.Join(Environment.NewLine, unknown.Select(name => $"  - {name}")));
    }

    private static bool IsKnownBaselineName(string fileName) =>
        TrySplit(fileName, out _, out var platform) &&
        VisualSnapshotVerifier.SupportedPlatformSuffixes.Contains(platform);

    private static IEnumerable<(string Snapshot, IReadOnlyCollection<string> Platforms)> EnumerateBaselines()
    {
        var baselines = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(VisualSnapshotVerifier.VerificationAssetsPath, "*.png"))
        {
            var fileName = Path.GetFileName(path);

            if (!IsKnownBaselineName(fileName))
            {
                continue;
            }

            TrySplit(fileName, out var snapshot, out var platform);

            if (!baselines.TryGetValue(snapshot, out var platforms))
            {
                platforms = new HashSet<string>(StringComparer.Ordinal);
                baselines[snapshot] = platforms;
            }

            platforms.Add(platform);
        }

        return baselines.Select(entry => (entry.Key, (IReadOnlyCollection<string>)entry.Value));
    }

    private static bool TrySplit(string fileName, out string snapshot, out string platform)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var separator = withoutExtension.LastIndexOf('.');

        if (separator <= 0 || separator == withoutExtension.Length - 1)
        {
            snapshot = withoutExtension;
            platform = string.Empty;
            return false;
        }

        snapshot = withoutExtension[..separator];
        platform = withoutExtension[(separator + 1)..];
        return true;
    }
}
