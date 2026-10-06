using Quickening.App.ViewModels;
using Quickening.Core.Models;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.App.Tests;

public class LargeFilesResultsViewModelTests
{
    [Fact]
    public void LoadFiles_SortsBiggestFirst()
    {
        var viewModel = new LargeFilesResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadFiles(new[]
        {
            new SelectableFile { Path = @"C:\small.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
            new SelectableFile { Path = @"C:\big.bin", SizeBytes = 9_000_000, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
        });

        Assert.Equal(@"C:\big.bin", viewModel.Files[0].Path);
        Assert.Equal(@"C:\small.bin", viewModel.Files[1].Path);
    }

    [Fact]
    public async Task DeleteSelectedAsync_RemovesFileFromListWithNoGroupConcept()
    {
        var viewModel = new LargeFilesResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadFiles(new[]
        {
            new SelectableFile { Path = @"C:\a.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow } as SelectableFile,
        });
        viewModel.Files[0].IsSelected = true;

        var failed = await viewModel.DeleteSelectedAsync();

        Assert.Empty(failed);
        Assert.Empty(viewModel.Files);
    }

    // Regression test for the bug where Large Files deletions never updated
    // the lifetime "reclaimed" total: LargeFilesResultsViewModel used to have
    // no SqliteStore dependency at all, so its DeleteSelectedAsync had no way
    // to call RecordTrashedFile the way ResultsViewModel.DeleteSelectedAsync
    // already did for the Duplicates flow.
    [Fact]
    public async Task DeleteSelectedAsync_RecordsTrashedFile_UpdatingLifetimeStats()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var viewModel = new LargeFilesResultsViewModel(new FakeRecycleBinService(), store);
        viewModel.LoadFiles(new[]
        {
            new SelectableFile { Path = @"C:\big.bin", SizeBytes = 9_000_000, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
        });
        viewModel.Files[0].IsSelected = true;

        await viewModel.DeleteSelectedAsync();

        var (bytesReclaimed, filesRemoved) = store.GetLifetimeStats();
        Assert.Equal(9_000_000, bytesReclaimed);
        Assert.Equal(1, filesRemoved);
    }

    [Fact]
    public void ApplyFilters_ByPathContains_HidesNonMatchingFiles()
    {
        var viewModel = new LargeFilesResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadFiles(new[]
        {
            new SelectableFile { Path = @"C:\camera-export\a.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
            new SelectableFile { Path = @"C:\docs\b.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
        });

        viewModel.PathContains = "camera";
        viewModel.ApplyFilters();

        Assert.Single(viewModel.Files);
        Assert.Contains("camera-export", viewModel.Files[0].Path);
    }

    [Fact]
    public void SelectByFolder_SelectsOnlyFilesUnderThatFolder()
    {
        var viewModel = new LargeFilesResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadFiles(new[]
        {
            new SelectableFile { Path = @"C:\Downloads\a.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
            new SelectableFile { Path = @"C:\Documents\b.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
        });

        viewModel.SelectByFolder(@"C:\Downloads");

        Assert.True(viewModel.Files.Single(f => f.Path == @"C:\Downloads\a.bin").IsSelected);
        Assert.False(viewModel.Files.Single(f => f.Path == @"C:\Documents\b.bin").IsSelected);
    }

    [Fact]
    public void SelectAll_NeverSelectsNetworkDriveFiles()
    {
        // See ResultsViewModelTests's identical test for why a UNC path is
        // safe to use here without any real mapped network drive.
        var viewModel = new LargeFilesResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadFiles(new[]
        {
            new SelectableFile { Path = @"C:\local.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
            new SelectableFile { Path = @"\\nas\photos\remote.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow },
        });

        viewModel.SelectAll();

        Assert.True(viewModel.Files.Single(f => f.Path == @"C:\local.bin").IsSelected);
        Assert.False(viewModel.Files.Single(f => f.Path == @"\\nas\photos\remote.bin").IsSelected);
    }

    [Fact]
    public void ApplyFilters_ByModifiedBefore_HidesFilesNewerThanCutoff()
    {
        var viewModel = new LargeFilesResultsViewModel(new FakeRecycleBinService());
        viewModel.LoadFiles(new[]
        {
            new SelectableFile { Path = @"C:\old.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow.AddYears(-2) },
            new SelectableFile { Path = @"C:\recent.bin", SizeBytes = 100, Category = MimeCategory.Other, LastWriteTimeUtc = DateTime.UtcNow.AddDays(-1) },
        });

        // "Older than 1 year": keep only files last modified on or before the cutoff.
        viewModel.ModifiedBefore = DateTime.UtcNow.AddYears(-1);
        viewModel.ApplyFilters();

        Assert.Single(viewModel.Files);
        Assert.Equal(@"C:\old.bin", viewModel.Files[0].Path);
    }
}
