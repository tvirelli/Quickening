using Quickening.App.Settings;
using Xunit;

namespace Quickening.App.Tests;

/// <summary>
/// Settings must survive a restart (RC QA 10.2) - including the Ignore list,
/// which is what keeps ignored files hidden after a relaunch + rescan (5.9).
/// </summary>
public class SettingsServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"qk-settings-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void SaveThenLoad_RoundTripsScanTolerancesAndIgnoreList()
    {
        var saved = new AppSettings
        {
            IncludeSimilarPhotos = true,
            SimilarityMaxDistance = 6,
            FlagBlurryPhotos = true,
            BlurryMaxSharpness = 42.5,
            ScanHiddenFiles = true,
            IgnoredFilePaths = new() { @"C:\Scrapbook\book1\page1\wedding.png" },
            IgnoredFolderPaths = new() { @"C:\Scrapbook" },
        };

        new SettingsService(_path).Save(saved);
        var loaded = new SettingsService(_path).Load();

        Assert.True(loaded.IncludeSimilarPhotos);
        Assert.Equal(6, loaded.SimilarityMaxDistance);
        Assert.True(loaded.FlagBlurryPhotos);
        Assert.Equal(42.5, loaded.BlurryMaxSharpness);
        Assert.True(loaded.ScanHiddenFiles);
        Assert.Equal(new[] { @"C:\Scrapbook\book1\page1\wedding.png" }, loaded.IgnoredFilePaths);
        Assert.Equal(new[] { @"C:\Scrapbook" }, loaded.IgnoredFolderPaths);
    }

    [Fact]
    public void Load_FallsBackToDefaults_WhenFileIsCorrupt()
    {
        File.WriteAllText(_path, "{ not json");

        var loaded = new SettingsService(_path).Load();

        Assert.Equal(new AppSettings().SimilarityMaxDistance, loaded.SimilarityMaxDistance);
        Assert.Empty(loaded.IgnoredFilePaths);
    }
}
