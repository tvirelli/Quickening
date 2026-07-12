using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Quickening.Tests.Shared;
using Xunit;

namespace Quickening.App.Tests;

/// <summary>
/// Shared test double for IRecycleBinService - originally a private nested
/// class inside ResultsViewModelTests, promoted to a top-level type (same
/// file, same namespace) so LargeFilesResultsViewModelTests can reuse it
/// unqualified rather than redefining an identical fake in a second file. A
/// private nested class is only visible inside its own enclosing class, so
/// it could not have been referenced from another test class as-is.
/// </summary>
internal sealed class FakeRecycleBinService : IRecycleBinService
{
    public bool AllowProtectedPaths { get; set; }

    private readonly HashSet<string> _pathsThatThrow;
    public List<string> DeletedPaths { get; } = new();

    public FakeRecycleBinService(params string[] pathsThatThrow)
    {
        _pathsThatThrow = new HashSet<string>(pathsThatThrow);
    }

    public void SendToRecycleBin(string path, long? expectedSizeBytes = null, DateTime? expectedLastWriteTimeUtc = null)
    {
        if (_pathsThatThrow.Contains(path))
        {
            throw new IOException($"Simulated failure for {path}");
        }

        DeletedPaths.Add(path);
        RestorablePaths.Add(path);
    }

    public (long ItemCount, long TotalSizeBytes) GetRecycleBinTotals() => (0, 0);

    public void EmptyRecycleBin()
    {
    }

    // Mirrors the real RecycleBinService: a path becomes restorable the
    // moment SendToRecycleBin succeeds for it, and TryRestore consumes the
    // entry on success (see the real implementation's identical
    // TryRemove-based one-shot behavior).
    public HashSet<string> RestorablePaths { get; } = new();

    public bool TryRestore(string originalPath) => RestorablePaths.Remove(originalPath);
}

public class ResultsViewModelTests
{
    private static DuplicateGroup MakeGroup(params string[] paths)
    {
        return new DuplicateGroup
        {
            FullHash = new byte[] { 1, 2, 3 },
            Files = paths.Select(p => new FileRecord
            {
                Path = p,
                SizeBytes = 100,
                LastWriteTimeUtc = DateTime.UtcNow,
                Category = MimeCategory.Other,
            }).ToList(),
        };
    }

    [Fact]
    public async Task DeleteSelected_PrunesGroup_WhenFewerThanTwoFilesRemain()
    {
        // A group is only meaningful as a "duplicate" with 2+ members - once
        // a delete drops it below that, it should disappear from Groups
        // entirely rather than lingering with a stale "2 copies" header over
        // a single remaining file.
        var fake = new FakeRecycleBinService();
        var viewModel = new ResultsViewModel(fake);
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt") });
        viewModel.Groups[0].Files[0].IsSelected = true;

        var failed = await viewModel.DeleteSelectedAsync();

        Assert.Empty(failed);
        Assert.Empty(viewModel.Groups);
        Assert.Contains(@"C:\a.txt", fake.DeletedPaths);
    }

    [Fact]
    public async Task DeleteSelected_ReportsFailure_WithoutAbortingOtherDeletions()
    {
        var fake = new FakeRecycleBinService(@"C:\locked.txt");
        var viewModel = new ResultsViewModel(fake);
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\locked.txt", @"C:\ok.txt") });
        viewModel.Groups[0].Files[0].IsSelected = true;
        viewModel.Groups[0].Files[1].IsSelected = true;

        var failed = await viewModel.DeleteSelectedAsync();

        Assert.Single(failed);
        Assert.Equal(@"C:\locked.txt", failed[0]);
        // ok.txt was successfully deleted, leaving only the failed locked.txt -
        // fewer than 2 files, so the group is pruned even though one delete
        // failed. The failure itself is reported via the returned list, not
        // by keeping the file visible in a now-meaningless single-file group.
        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public async Task DeleteSelected_RelabelsSurvivingGroup_WhenTwoOrMoreFilesRemain()
    {
        var fake = new FakeRecycleBinService();
        var viewModel = new ResultsViewModel(fake);
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt", @"C:\c.txt") });
        viewModel.Groups[0].Files[0].IsSelected = true;

        var failed = await viewModel.DeleteSelectedAsync();

        Assert.Empty(failed);
        Assert.Single(viewModel.Groups);
        Assert.Equal(2, viewModel.Groups[0].Files.Count);
        Assert.StartsWith("2 copies", viewModel.Groups[0].GroupLabel);
        Assert.Contains(@"C:\a.txt", fake.DeletedPaths);
    }

    [Fact]
    public async Task DeleteSelectedAsync_ReportsProgress_OncePerSelectedFile()
    {
        var fake = new FakeRecycleBinService();
        var viewModel = new ResultsViewModel(fake);
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt", @"C:\c.txt") });
        viewModel.Groups[0].Files[0].IsSelected = true;
        viewModel.Groups[0].Files[1].IsSelected = true;

        var reports = new List<DeleteProgress>();
        var progress = new SynchronousProgress<DeleteProgress>(r => reports.Add(r));

        await viewModel.DeleteSelectedAsync(progress);

        Assert.Equal(2, reports.Count);
        Assert.Equal(new DeleteProgress(1, 2, @"C:\a.txt"), reports[0]);
        Assert.Equal(new DeleteProgress(2, 2, @"C:\b.txt"), reports[1]);
    }

    private static DuplicateGroup MakeGroupWithDetails(
        (string Path, long Size, MimeCategory Category, DateTime Modified)[] files)
    {
        return new DuplicateGroup
        {
            FullHash = new byte[] { 1, 2, 3 },
            Files = files.Select(f => new FileRecord
            {
                Path = f.Path,
                SizeBytes = f.Size,
                LastWriteTimeUtc = f.Modified,
                Category = f.Category,
            }).ToList(),
        };
    }

    [Fact]
    public void ApplyFilters_HidesGroup_WhenFewerThanTwoFilesMatchSizeFilter()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\small1.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
                (@"C:\small2.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
            }),
        });

        viewModel.MinSizeBytes = 1000; // neither file qualifies
        viewModel.ApplyFilters();

        Assert.Empty(viewModel.Groups);
    }

    [Fact]
    public void ApplyFilters_ShowsOnlyMatchingCategory()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\a.jpg", 100L, MimeCategory.Image, DateTime.UtcNow),
                (@"C:\b.jpg", 100L, MimeCategory.Image, DateTime.UtcNow),
            }),
            MakeGroupWithDetails(new[]
            {
                (@"C:\a.mp3", 100L, MimeCategory.Audio, DateTime.UtcNow),
                (@"C:\b.mp3", 100L, MimeCategory.Audio, DateTime.UtcNow),
            }),
        });

        viewModel.CategoryFilter.Add(MimeCategory.Image);
        viewModel.ApplyFilters();

        Assert.Single(viewModel.Groups);
        Assert.All(viewModel.Groups[0].Files, f => Assert.Equal(MimeCategory.Image, f.Category));
    }

    [Fact]
    public void ApplyFilters_ClearingFilters_RestoresFullView()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\a.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
                (@"C:\b.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
            }),
        });

        viewModel.MinSizeBytes = 1000;
        viewModel.ApplyFilters();
        Assert.Empty(viewModel.Groups);

        viewModel.MinSizeBytes = null;
        viewModel.ApplyFilters();
        Assert.Single(viewModel.Groups);
    }

    [Fact]
    public void ApplyFilters_ByModifiedDate_HidesOnlyTheOlderFile_PruningTheGroup()
    {
        // Unlike size/category (identical across true duplicates by
        // definition), LastWriteTimeUtc can genuinely differ between
        // otherwise-identical files (e.g. one copy touched/re-saved later)
        // - this is the one filter that can make a group drop below 2
        // visible files even though the file CONTENT is identical, so it
        // specifically exercises the per-file (not per-group) side of
        // MatchesFilters/ApplyFilters's pruning rule.
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        var recent = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\recent.txt", 100L, MimeCategory.Document, recent),
                (@"C:\old.txt", 100L, MimeCategory.Document, old),
            }),
        });

        viewModel.ModifiedAfter = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        viewModel.ApplyFilters();

        // Only recent.txt matches the date filter; with just 1 visible file
        // left, the whole group is pruned (not a duplicate with only 1
        // member) rather than showing a "1 copy" group.
        Assert.Empty(viewModel.Groups);

        viewModel.ModifiedAfter = null;
        viewModel.ApplyFilters();
        Assert.Single(viewModel.Groups);
        Assert.Equal(2, viewModel.Groups[0].Files.Count);
    }

    [Fact]
    public void SelectAll_SelectsEveryVisibleFile()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt") });

        viewModel.SelectAll();

        Assert.All(viewModel.Groups[0].Files, f => Assert.True(f.IsSelected));
    }

    [Fact]
    public void ClearSelection_DeselectsEveryFile()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt") });
        viewModel.SelectAll();

        viewModel.ClearSelection();

        Assert.All(viewModel.Groups[0].Files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void InvertSelection_FlipsEveryFilesSelectedState()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt") });
        viewModel.Groups[0].Files[0].IsSelected = true;

        viewModel.InvertSelection();

        Assert.False(viewModel.Groups[0].Files[0].IsSelected);
        Assert.True(viewModel.Groups[0].Files[1].IsSelected);
    }

    [Fact]
    public void SelectBySizeThreshold_SelectsOnlyFilesAtOrAboveThreshold()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\big1.bin", 5_000_000L, MimeCategory.Other, DateTime.UtcNow),
                (@"C:\big2.bin", 5_000_000L, MimeCategory.Other, DateTime.UtcNow),
            }),
            MakeGroupWithDetails(new[]
            {
                (@"C:\small1.txt", 100L, MimeCategory.Other, DateTime.UtcNow),
                (@"C:\small2.txt", 100L, MimeCategory.Other, DateTime.UtcNow),
            }),
        });

        viewModel.SelectBySizeThreshold(1_000_000);

        Assert.All(viewModel.Groups[0].Files, f => Assert.True(f.IsSelected));
        Assert.All(viewModel.Groups[1].Files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void SelectBySizeThreshold_IsInclusiveOfExactBoundary()
    {
        // The implementation uses >=, not > - a file exactly at the
        // threshold must be selected. Covered separately from the
        // far-above/far-below test above so a regression to > is caught.
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\exact1.bin", 1_000_000L, MimeCategory.Other, DateTime.UtcNow),
                (@"C:\exact2.bin", 1_000_000L, MimeCategory.Other, DateTime.UtcNow),
            }),
        });

        viewModel.SelectBySizeThreshold(1_000_000);

        Assert.All(viewModel.Groups[0].Files, f => Assert.True(f.IsSelected));
    }

    [Fact]
    public void SelectAll_DoesNotSelectFilesHiddenByAnActiveFilter()
    {
        // The design intent (see ResultsViewModel.SelectAll's comment) is
        // that mass-select only affects what's currently visible under the
        // active filters, not the full unfiltered result set - a filtered-
        // out file must not come back pre-selected once the filter clears.
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\shown.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
                (@"C:\also-shown.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
            }),
            MakeGroupWithDetails(new[]
            {
                (@"C:\hidden.jpg", 100L, MimeCategory.Image, DateTime.UtcNow),
                (@"C:\also-hidden.jpg", 100L, MimeCategory.Image, DateTime.UtcNow),
            }),
        });

        viewModel.CategoryFilter.Add(MimeCategory.Document);
        viewModel.ApplyFilters();

        viewModel.SelectAll();

        viewModel.CategoryFilter.Clear();
        viewModel.ApplyFilters();

        var hiddenGroup = viewModel.Groups.Single(g => g.Files[0].Path == @"C:\hidden.jpg");
        Assert.All(hiddenGroup.Files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void ApplyFilters_HidesGroup_WhenFewerVisibleFilesThanMinGroupSize()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroup(@"C:\a.txt", @"C:\b.txt"), // 2 copies
            MakeGroupWithDetails(new[]
            {
                (@"C:\x.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
                (@"C:\y.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
                (@"C:\z.txt", 100L, MimeCategory.Document, DateTime.UtcNow),
            }), // 3 copies
        });

        viewModel.MinGroupSize = 3;
        viewModel.ApplyFilters();

        Assert.Single(viewModel.Groups);
        Assert.Equal(3, viewModel.Groups[0].Files.Count);
    }

    [Fact]
    public void ApplyFilters_ByPathContains_HidesNonMatchingGroup()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroup(@"C:\camera-export\a.txt", @"C:\camera-export\b.txt"),
            MakeGroup(@"C:\docs\c.txt", @"C:\docs\d.txt"),
        });

        viewModel.PathContains = "camera";
        viewModel.ApplyFilters();

        Assert.Single(viewModel.Groups);
        Assert.All(viewModel.Groups[0].Files, f => Assert.Contains("camera-export", f.Path));
    }

    [Fact]
    public void SelectByFolder_SelectsOnlyFilesUnderThatFolder()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroup(@"C:\Downloads\a.txt", @"C:\Downloads\sub\b.txt"),
            MakeGroup(@"C:\Documents\c.txt", @"C:\Documents\d.txt"),
        });

        viewModel.SelectByFolder(@"C:\Downloads");

        Assert.All(viewModel.Groups[0].Files, f => Assert.True(f.IsSelected));
        Assert.All(viewModel.Groups[1].Files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void SelectAll_NeverSelectsNetworkDriveFiles()
    {
        // A UNC path is recognized as network-located without needing a real
        // mapped drive - see NetworkPathDetector.IsNetworkPath's leading
        // "\\" fast path - so this exercises the exclusion without any
        // dependency on the test machine's actual drive configuration.
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\local.txt", @"\\nas\photos\remote.txt") });

        viewModel.SelectAll();

        Assert.True(viewModel.Groups[0].Files.Single(f => f.Path == @"C:\local.txt").IsSelected);
        Assert.False(viewModel.Groups[0].Files.Single(f => f.Path == @"\\nas\photos\remote.txt").IsSelected);
    }

    [Fact]
    public void SelectByCategory_SelectsOnlyMatchingCategoryFiles()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\a.mp3", 100L, MimeCategory.Audio, DateTime.UtcNow),
                (@"C:\b.mp3", 100L, MimeCategory.Audio, DateTime.UtcNow),
            }),
            MakeGroupWithDetails(new[]
            {
                (@"C:\a.jpg", 100L, MimeCategory.Image, DateTime.UtcNow),
                (@"C:\b.jpg", 100L, MimeCategory.Image, DateTime.UtcNow),
            }),
        });

        viewModel.SelectByCategory(MimeCategory.Audio);

        Assert.All(viewModel.Groups[0].Files, f => Assert.True(f.IsSelected));
        Assert.All(viewModel.Groups[1].Files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void GetSelectedSizeBytes_SumsOnlySelectedFiles()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\a.bin", 100L, MimeCategory.Other, DateTime.UtcNow),
                (@"C:\b.bin", 250L, MimeCategory.Other, DateTime.UtcNow),
            }),
        });

        viewModel.Groups[0].Files[1].IsSelected = true; // only the 250-byte file

        Assert.Equal(250L, viewModel.GetSelectedSizeBytes());
    }

    [Fact]
    public void GetSelectedSizeBytes_IsZero_WhenNothingSelected()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt") });

        Assert.Equal(0L, viewModel.GetSelectedSizeBytes());
    }

    [Fact]
    public void SelectRecommended_SelectsEveryFileExceptTheNewestPerGroup()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\old.txt", 100L, MimeCategory.Document, older),
                (@"C:\new.txt", 100L, MimeCategory.Document, newer),
            }),
        });

        viewModel.SelectRecommended();

        Assert.True(viewModel.Groups[0].Files.Single(f => f.Path == @"C:\old.txt").IsSelected);
        Assert.False(viewModel.Groups[0].Files.Single(f => f.Path == @"C:\new.txt").IsSelected);
    }

    [Fact]
    public void SelectRecommended_OnATie_KeepsExactlyOneFileUnselected()
    {
        var sameTimestamp = DateTime.UtcNow;
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\a.txt", 100L, MimeCategory.Document, sameTimestamp),
                (@"C:\b.txt", 100L, MimeCategory.Document, sameTimestamp),
            }),
        });

        viewModel.SelectRecommended();

        var selectedCount = viewModel.Groups[0].Files.Count(f => f.IsSelected);
        Assert.Equal(1, selectedCount);
    }

    [Fact]
    public void LoadGroups_MarksTheNewestFileInEachGroupAsKeepRecommended()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        viewModel.LoadGroups(new[]
        {
            MakeGroupWithDetails(new[]
            {
                (@"C:\old.txt", 100L, MimeCategory.Document, older),
                (@"C:\new.txt", 100L, MimeCategory.Document, newer),
            }),
        });

        Assert.True(viewModel.Groups[0].Files.Single(f => f.Path == @"C:\new.txt").IsKeepRecommended);
        Assert.False(viewModel.Groups[0].Files.Single(f => f.Path == @"C:\old.txt").IsKeepRecommended);
    }

    [Fact]
    public void GetSelectedRiskyFilePaths_ReturnsOnlySelectedFilesMatchingRiskyExtensions()
    {
        // Per the design doc's "Risky-extension warning" step, only files
        // that are both currently selected AND match RiskyExtensions.IsRisky
        // should trigger the extra delete-confirmation friction - a risky
        // file that isn't selected, or a selected file that isn't risky,
        // must not show up here.
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[]
        {
            MakeGroup(@"C:\vm\guest.vdi", @"C:\vm\backup.bak", @"C:\docs\notes.txt"),
        });
        viewModel.Groups[0].Files[0].IsSelected = true; // guest.vdi - risky, selected
        viewModel.Groups[0].Files[1].IsSelected = false; // backup.bak - risky, NOT selected
        viewModel.Groups[0].Files[2].IsSelected = true; // notes.txt - safe, selected

        var riskyPaths = viewModel.GetSelectedRiskyFilePaths();

        Assert.Single(riskyPaths);
        Assert.Equal(@"C:\vm\guest.vdi", riskyPaths[0]);
    }

    [Fact]
    public void GetSelectedRiskyFilePaths_ReturnsEmpty_WhenNoSelectedFilesAreRisky()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\a.txt", @"C:\b.txt") });
        viewModel.SelectAll();

        var riskyPaths = viewModel.GetSelectedRiskyFilePaths();

        Assert.Empty(riskyPaths);
    }

    // A dedicated test forcing a real SQLITE_BUSY write conflict (to prove
    // DeleteSelectedAsync survives RecordTrashedFile failing) was attempted
    // and deliberately dropped: Microsoft.Data.Sqlite's connection pooling
    // and default busy-timeout behavior made it take 30+ seconds per run and
    // left the temp DB file locked during cleanup - disproportionately
    // slow/fragile for what is, in the production code, a plain try/catch
    // around a single call. The fix itself (see ResultsViewModel.
    // DeleteSelectedAsync) is straightforward exception isolation matching
    // the same pattern already used and tested elsewhere in this codebase
    // (DuplicateEngine's per-file IOException handling, RecycleBinService's
    // per-file isolation in this same method) - reasoned-through rather
    // than independently regression-tested for this specific call site.

    private static SimilarityGroup MakeSimilarityGroup(int matchPercent, params string[] paths) => new()
    {
        MatchPercent = matchPercent,
        Files = paths.Select(p => new FileRecord
        {
            Path = p,
            SizeBytes = 100,
            LastWriteTimeUtc = DateTime.UtcNow,
            Category = MimeCategory.Image,
        }).ToList(),
    };

    [Fact]
    public void LoadSimilarityGroups_PopulatesSimilarityGroupsCollection()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());

        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(94, @"C:\a.jpg", @"C:\b.jpg") });

        Assert.Single(viewModel.SimilarityGroups);
        Assert.Equal(94, viewModel.SimilarityGroups[0].MatchPercent);
        Assert.Equal(2, viewModel.SimilarityGroups[0].Files.Count);
        Assert.Equal("2 similar photos", viewModel.SimilarityGroups[0].GroupLabel);
    }

    [Fact]
    public void LoadSimilarityGroups_TagsTheBiggestFile_WithAHintLabel()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        var group = new SimilarityGroup
        {
            MatchPercent = 90,
            Files = new List<FileRecord>
            {
                new() { Path = @"C:\small.jpg", SizeBytes = 100, LastWriteTimeUtc = DateTime.UtcNow, Category = MimeCategory.Image },
                new() { Path = @"C:\big.jpg", SizeBytes = 900, LastWriteTimeUtc = DateTime.UtcNow, Category = MimeCategory.Image },
            },
        };

        viewModel.LoadSimilarityGroups(new[] { group });

        var files = viewModel.SimilarityGroups[0].Files;
        Assert.Equal("BIGGER FILE", files.Single(f => f.Path == @"C:\big.jpg").HintLabel);
        Assert.Null(files.Single(f => f.Path == @"C:\small.jpg").HintLabel);
    }

    [Fact]
    public void LoadSimilarityGroups_LeavesHintLabelNull_WhenFileSizesTie()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());

        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg") });

        Assert.All(viewModel.SimilarityGroups[0].Files, f => Assert.Null(f.HintLabel));
    }

    [Fact]
    public void GetSelectedSizeBytes_IncludesSelectedSimilarityGroupFiles()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg") });

        viewModel.SimilarityGroups[0].Files[0].IsSelected = true;

        Assert.Equal(100, viewModel.GetSelectedSizeBytes());
    }

    [Fact]
    public void SelectAll_AlsoSelectsSimilarityGroupFiles()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg") });

        viewModel.SelectAll();

        Assert.All(viewModel.SimilarityGroups[0].Files, f => Assert.True(f.IsSelected));
    }

    [Fact]
    public void GetSelectedFilePaths_ReturnsPathsFromBothGroupsAndSimilarityGroups()
    {
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\dup1.txt", @"C:\dup2.txt") });
        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg") });
        viewModel.Groups[0].Files[0].IsSelected = true;
        viewModel.SimilarityGroups[0].Files[1].IsSelected = true;

        var paths = viewModel.GetSelectedFilePaths();

        Assert.Equal(2, paths.Count);
        Assert.Contains(@"C:\dup1.txt", paths);
        Assert.Contains(@"C:\b.jpg", paths);
    }

    [Fact]
    public async Task DeleteSelectedAsync_RemovesSelectedSimilarityGroupFiles()
    {
        var fake = new FakeRecycleBinService();
        var viewModel = new ResultsViewModel(fake);
        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg") });
        viewModel.SimilarityGroups[0].Files[0].IsSelected = true;

        var failed = await viewModel.DeleteSelectedAsync();

        Assert.Empty(failed);
        Assert.Contains(@"C:\a.jpg", fake.DeletedPaths);
    }

    [Fact]
    public async Task DeleteSelectedAsync_PrunesSimilarityGroup_WhenFewerThanTwoFilesRemain()
    {
        var fake = new FakeRecycleBinService();
        var viewModel = new ResultsViewModel(fake);
        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg") });
        viewModel.SimilarityGroups[0].Files[0].IsSelected = true;

        await viewModel.DeleteSelectedAsync();

        Assert.Empty(viewModel.SimilarityGroups);
    }

    [Fact]
    public async Task DeleteSelectedAsync_RelabelsSurvivingSimilarityGroup_WhenThreeOrMoreFilesRemain()
    {
        var fake = new FakeRecycleBinService();
        var viewModel = new ResultsViewModel(fake);
        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg", @"C:\c.jpg") });
        viewModel.SimilarityGroups[0].Files[0].IsSelected = true;

        await viewModel.DeleteSelectedAsync();

        Assert.Single(viewModel.SimilarityGroups);
        Assert.Equal(2, viewModel.SimilarityGroups[0].Files.Count);
        Assert.Equal("2 similar photos", viewModel.SimilarityGroups[0].GroupLabel);
    }

    [Fact]
    public void SelectRecommended_NeverSelectsSimilarityGroupFiles()
    {
        // new-screens 4k: "never auto-selected" - SelectRecommended is only
        // ever invoked for the Duplicates Groups collection.
        var viewModel = new ResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadGroups(new[] { MakeGroup(@"C:\dup1.txt", @"C:\dup2.txt") });
        viewModel.LoadSimilarityGroups(new[] { MakeSimilarityGroup(90, @"C:\a.jpg", @"C:\b.jpg") });

        viewModel.SelectRecommended();

        Assert.All(viewModel.SimilarityGroups[0].Files, f => Assert.False(f.IsSelected));
    }
}
