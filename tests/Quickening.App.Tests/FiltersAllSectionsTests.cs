using Quickening.App.ViewModels;
using Quickening.Core.Audio;
using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Xunit;

namespace Quickening.App.Tests;

/// <summary>
/// 1.3.1: every filter applies to every section, and Select all / Remove only
/// ever act on what's visible. Before, filters only narrowed Duplicates - a
/// user filtering to ".aae" and pressing Select all also ticked every similar
/// photo, video and song, which Remove would then delete.
/// </summary>
public class FiltersAllSectionsTests
{
    private static FileRecord File(string path, MimeCategory category = MimeCategory.Image, long size = 100) => new()
    {
        Path = path,
        SizeBytes = size,
        LastWriteTimeUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        Category = category,
    };

    private static ResultsViewModel LoadEverySection()
    {
        var vm = new ResultsViewModel(new FakeRecycleBinService());
        vm.LoadGroups(new[]
        {
            new DuplicateGroup { FullHash = new byte[] { 1 }, Files = new List<FileRecord> { File(@"C:\a\IMG_1.aae", MimeCategory.Other), File(@"C:\b\IMG_1.aae", MimeCategory.Other) } },
        });
        vm.LoadSimilarityGroups(new[]
        {
            new SimilarityGroup { MatchPercent = 95, Files = new List<FileRecord> { File(@"C:\p\beach.jpg"), File(@"C:\p\beach small.jpg") } },
        });
        vm.LoadVideoGroups(new[]
        {
            new VideoSimilarityEngine.VideoGroup(new[] { File(@"C:\v\clip.mp4", MimeCategory.Video), File(@"C:\v\clip copy.mov", MimeCategory.Video) }, 97),
        });
        vm.LoadMusicGroups(new[]
        {
            new AudioDuplicateEngine.MusicGroup("Song", new[]
            {
                new AudioDuplicateEngine.SongCopy(File(@"C:\m\song.flac", MimeCategory.Audio), 900, 200),
                new AudioDuplicateEngine.SongCopy(File(@"C:\m\song.mp3", MimeCategory.Audio), 320, 200),
            }),
        });
        vm.LoadBlurryPhotos(new[] { File(@"C:\p\shaky.jpg") });
        return vm;
    }

    private static List<SelectableFile> EveryFile(ResultsViewModel vm) =>
        vm.Groups.SelectMany(g => g.Files)
            .Concat(vm.SimilarityGroups.SelectMany(g => g.Files))
            .Concat(vm.VideoGroups.SelectMany(g => g.Files))
            .Concat(vm.MusicGroups.SelectMany(g => g.Files))
            .Concat(vm.BlurryPhotos)
            .ToList();

    [Fact]
    public void ExtensionFilter_SelectAll_TicksOnlyMatchingFiles_InEverySection()
    {
        var vm = LoadEverySection();
        vm.ExtensionFilter = ".aae";
        vm.ApplyFilters();

        vm.SelectAll();

        Assert.Equal(new[] { @"C:\a\IMG_1.aae", @"C:\b\IMG_1.aae" }, vm.GetSelectedFilePaths().Order());
        Assert.All(EveryFile(vm).Where(f => !f.Path.EndsWith(".aae")), f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void Filters_HideNonMatchingGroups_InSimilarVideoMusicAndBlurry()
    {
        var vm = LoadEverySection();
        vm.PathContains = @"C:\a\";
        vm.ApplyFilters();

        Assert.Empty(vm.VisibleGroupFiles(vm.SimilarityGroups[0].Files));
        Assert.Empty(vm.VisibleGroupFiles(vm.VideoGroups[0].Files));
        Assert.Empty(vm.VisibleGroupFiles(vm.MusicGroups[0].Files));
        Assert.Empty(vm.VisibleBlurryPhotos());
    }

    [Fact]
    public void AGroupLeftWithOneMatchingFile_IsHidden()
    {
        var vm = LoadEverySection();
        vm.PathContains = "small";
        vm.ApplyFilters();

        Assert.Empty(vm.VisibleGroupFiles(vm.SimilarityGroups[0].Files));
    }

    [Fact]
    public void NoFilters_ShowEverything()
    {
        var vm = LoadEverySection();

        Assert.Equal(2, vm.VisibleGroupFiles(vm.SimilarityGroups[0].Files).Count);
        Assert.Equal(2, vm.VisibleGroupFiles(vm.MusicGroups[0].Files).Count);
        Assert.Single(vm.VisibleBlurryPhotos());
    }

    [Fact]
    public void CopiesFilter_HidesGroupsBelowIt_AndTheBlurryList()
    {
        var vm = LoadEverySection();
        vm.MinGroupSize = 3;
        vm.ApplyFilters();

        vm.SelectAll();

        Assert.Empty(vm.VisibleGroupFiles(vm.SimilarityGroups[0].Files));
        Assert.Empty(vm.VisibleBlurryPhotos());
        Assert.Empty(vm.GetSelectedFilePaths());
    }

    [Fact]
    public void SelectedFiles_HiddenByAFilter_AreNotRemoved()
    {
        var vm = LoadEverySection();
        vm.SelectAll();

        vm.ExtensionFilter = ".aae";
        vm.ApplyFilters();

        Assert.Equal(new[] { @"C:\a\IMG_1.aae", @"C:\b\IMG_1.aae" }, vm.GetSelectedFilePaths().Order());
    }

    [Fact]
    public async Task Remove_DeletesOnlyVisibleTickedFiles_NotOnesAFilterOrShowHides()
    {
        // Real files: the non-duplicate delete loops skip paths that no longer
        // exist (already recycled via another section), so fake paths would
        // pass this test for the wrong reason.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"qk-filters-{Guid.NewGuid():N}")).FullName;
        string Real(string name)
        {
            var path = Path.Combine(dir, name);
            System.IO.File.WriteAllText(path, name);
            return path;
        }

        try
        {
            var dupA = Real("IMG_1.aae");
            var dupB = Real("IMG_1 copy.aae");
            var bin = new FakeRecycleBinService();
            var vm = new ResultsViewModel(bin);
            vm.LoadGroups(new[]
            {
                new DuplicateGroup { FullHash = new byte[] { 1 }, Files = new List<FileRecord> { File(dupA, MimeCategory.Other), File(dupB, MimeCategory.Other) } },
            });
            vm.LoadSimilarityGroups(new[]
            {
                new SimilarityGroup { MatchPercent = 95, Files = new List<FileRecord> { File(Real("beach.jpg")), File(Real("beach small.jpg")) } },
            });
            vm.LoadBlurryPhotos(new[] { File(Real("shaky.aae"), MimeCategory.Other) });
            vm.SelectAll();

            vm.ExtensionFilter = ".aae";                  // hides the look-alikes
            vm.ApplyFilters();
            vm.HiddenSections.Add(ResultSections.Blurry); // hides the blurry list

            await vm.DeleteSelectedAsync();

            Assert.Equal(new[] { dupA, dupB }.Order(), bin.DeletedPaths.Order());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void KeepBestSimilar_LeavesGroupsAFilterHides_Untouched()
    {
        var vm = LoadEverySection();
        vm.ExtensionFilter = ".aae";
        vm.ApplyFilters();

        vm.SelectAllButBestInSimilarityGroups();

        Assert.All(vm.SimilarityGroups[0].Files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void HiddenSections_AreSkippedBySelectAll()
    {
        var vm = LoadEverySection();
        vm.HiddenSections.Add(ResultSections.Similar);
        vm.HiddenSections.Add(ResultSections.Blurry);

        vm.SelectAll();

        Assert.All(vm.SimilarityGroups[0].Files, f => Assert.False(f.IsSelected));
        Assert.False(vm.BlurryPhotos[0].IsSelected);
        Assert.True(vm.MusicGroups[0].Files[0].IsSelected);
    }

    [Fact]
    public void ExtensionList_IncludesExtensionsFromEverySection()
    {
        var vm = LoadEverySection();

        Assert.Contains(".jpg", vm.AvailableExtensions); // the look-alike pair (and the blurry photo)
        Assert.Contains(".aae", vm.AvailableExtensions);
        // One .flac + one .mp3: filtering to either leaves a single file and
        // hides the group, so neither is offered (same rule as duplicates).
        Assert.DoesNotContain(".flac", vm.AvailableExtensions);
    }
}

public class GroupSelectionTests
{
    private static List<SelectableFile> Files(int count) => Enumerable.Range(0, count)
        .Select(i => new SelectableFile { Path = $@"C:\x\{i}.jpg", SizeBytes = 1, Category = MimeCategory.Image, LastWriteTimeUtc = DateTime.UtcNow })
        .ToList();

    [Fact]
    public void Clicks_CycleAll_ThenAllButKeep_ThenNone()
    {
        var files = Files(3);
        files[1].IsKeepRecommended = true;

        Assert.False(GroupSelection.StateOf(files));

        GroupSelection.Advance(files);
        Assert.True(GroupSelection.StateOf(files));
        Assert.All(files, f => Assert.True(f.IsSelected));

        GroupSelection.Advance(files);
        Assert.Null(GroupSelection.StateOf(files));
        Assert.False(files[1].IsSelected);
        Assert.True(files[0].IsSelected && files[2].IsSelected);

        GroupSelection.Advance(files);
        Assert.False(GroupSelection.StateOf(files));
        Assert.All(files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void SecondClick_KeepsTheBestCopyBadge_WhenThereIsNoKeepRecommendation()
    {
        var files = Files(3);
        files[2].HintLabel = "BEST QUALITY";

        GroupSelection.Advance(files);
        GroupSelection.Advance(files);

        Assert.False(files[2].IsSelected);
        Assert.True(files[0].IsSelected && files[1].IsSelected);
    }

    [Fact]
    public void SecondClick_KeepsTheFirstCopy_WhenNothingIsMarked()
    {
        var files = Files(2);

        GroupSelection.Advance(files);
        GroupSelection.Advance(files);

        Assert.False(files[0].IsSelected);
        Assert.True(files[1].IsSelected);
    }

    [Fact]
    public void APartlyTickedGroup_ClearsOnTheNextClick()
    {
        var files = Files(3);
        files[0].IsSelected = true;

        Assert.Null(GroupSelection.StateOf(files));
        GroupSelection.Advance(files);

        Assert.All(files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void NetworkDriveFiles_AreNeverTicked()
    {
        var files = Files(3);
        files[0] = new SelectableFile { Path = @"\\server\share\0.jpg", SizeBytes = 1, Category = MimeCategory.Image, LastWriteTimeUtc = DateTime.UtcNow };

        GroupSelection.Advance(files);

        Assert.False(files[0].IsSelected);
        Assert.True(GroupSelection.StateOf(files));
    }
}
