using Quickening.App.Settings;
using Quickening.App.ViewModels;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.App.Tests;

public class KeeperChooserTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SelectableFile File(string path, int minutesAfterBase = 0, long size = 100) => new()
    {
        Path = path,
        SizeBytes = size,
        Category = MimeCategory.Image,
        LastWriteTimeUtc = Base.AddMinutes(minutesAfterBase),
    };

    [Fact]
    public void NoOverrides_FallsBackToTheNormalRule()
    {
        var older = File(@"C:\a\photo.jpg", 0);
        var newer = File(@"C:\b\photo.jpg", 5);

        var (keeper, label) = KeeperChooser.Choose(new[] { older, newer }, KeepRule.Newest, Array.Empty<string>());

        Assert.Same(newer, keeper);
        Assert.Equal("KEEP — newest", label);
    }

    [Fact]
    public void CopyInsideAnOverrideFolder_WinsOverTheNormalRule()
    {
        var inKeepers = File(@"C:\Users\D\Pictures\Keepers\photo.jpg", 0);
        var newerElsewhere = File(@"C:\Users\D\Downloads\photo.jpg", 5);

        var (keeper, label) = KeeperChooser.Choose(
            new[] { newerElsewhere, inKeepers }, KeepRule.Newest, new[] { @"C:\Users\D\Pictures\Keepers" });

        Assert.Same(inKeepers, keeper);
        Assert.Equal("KEEP — in Keepers", label);
    }

    [Fact]
    public void SubfoldersOfAnOverrideFolderCount()
    {
        var nested = File(@"C:\Keepers\2024\Summer\photo.jpg", 0);
        var other = File(@"C:\Downloads\photo.jpg", 5);

        var (keeper, _) = KeeperChooser.Choose(new[] { other, nested }, KeepRule.Newest, new[] { @"C:\Keepers" });

        Assert.Same(nested, keeper);
    }

    [Fact]
    public void HigherRankedFolderWins_WhenCopiesSitInSeveralOverrideFolders()
    {
        var inSecond = File(@"D:\Family Photos\photo.jpg", 5);
        var inFirst = File(@"C:\Keepers\photo.jpg", 0);

        var (keeper, label) = KeeperChooser.Choose(
            new[] { inSecond, inFirst }, KeepRule.Newest, new[] { @"C:\Keepers", @"D:\Family Photos" });

        Assert.Same(inFirst, keeper);
        Assert.Equal("KEEP — in Keepers", label);
    }

    [Fact]
    public void SeveralCopiesInTheWinningFolder_NormalRulePicksAmongThem()
    {
        var olderInKeepers = File(@"C:\Keepers\a\photo.jpg", 0);
        var newerInKeepers = File(@"C:\Keepers\b\photo.jpg", 5);
        var newestOutside = File(@"C:\Downloads\photo.jpg", 10);

        var (keeper, label) = KeeperChooser.Choose(
            new[] { olderInKeepers, newestOutside, newerInKeepers }, KeepRule.Newest, new[] { @"C:\Keepers" });

        Assert.Same(newerInKeepers, keeper);
        Assert.Equal("KEEP — in Keepers", label);
    }

    [Fact]
    public void SimilarlyNamedSiblingFolder_DoesNotMatch()
    {
        // "C:\Keepers2" must not count as inside "C:\Keepers".
        var sibling = File(@"C:\Keepers2\photo.jpg", 0);
        var newer = File(@"C:\Downloads\photo.jpg", 5);

        var (keeper, _) = KeeperChooser.Choose(new[] { sibling, newer }, KeepRule.Newest, new[] { @"C:\Keepers" });

        Assert.Same(newer, keeper);
    }

    [Fact]
    public void OverrideFolderWithTrailingSlashOrDifferentCase_StillMatches()
    {
        var inKeepers = File(@"C:\Keepers\photo.jpg", 0);
        var newer = File(@"C:\Downloads\photo.jpg", 5);

        var (keeper, _) = KeeperChooser.Choose(new[] { newer, inKeepers }, KeepRule.Newest, new[] { @"c:\keepers\" });

        Assert.Same(inKeepers, keeper);
    }

    [Fact]
    public void DriveRootOverride_LabelsWithTheDrive()
    {
        var onD = File(@"D:\photo.jpg", 0);
        var newer = File(@"C:\Downloads\photo.jpg", 5);

        var (keeper, label) = KeeperChooser.Choose(new[] { newer, onD }, KeepRule.Newest, new[] { @"D:\" });

        Assert.Same(onD, keeper);
        Assert.Equal(@"KEEP — in D:\", label);
    }
}
