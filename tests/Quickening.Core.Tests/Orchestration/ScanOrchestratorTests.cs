using Quickening.Core.Duplicates;
using Quickening.Core.Orchestration;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests.Orchestration;

public class ScanOrchestratorTests : IDisposable
{
    private readonly string _tempDir;

    public ScanOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "QuickeningOrchestratorTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Scan_FindsDuplicates_AndPersistsHashes()
    {
        var content = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "unique.txt"), "unique"u8.ToArray());

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        var result = orchestrator.Scan(_tempDir);

        Assert.Single(result.DuplicateGroups);
        Assert.Equal(2, result.DuplicateGroups[0].Files.Count);
        Assert.Equal(3, result.TotalFilesScanned);

        // Hashes must actually be persisted - this is the whole point of
        // ScanOrchestrator existing rather than the caller manually wiring
        // FileEnumerator/DuplicateEngine/SqliteStore together itself.
        var persisted = store.GetFileByPath(Path.Combine(_tempDir, "a.txt"));
        Assert.NotNull(persisted);
        Assert.NotNull(persisted!.FullHash);
    }

    [Fact]
    public void Scan_PopulatesEmptyItems_HousekeepingByproduct()
    {
        // A real file, a zero-byte file, and an empty subfolder.
        File.WriteAllBytes(Path.Combine(_tempDir, "real.txt"), "content"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_tempDir, "blank.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(_tempDir, "emptysub"));

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        var result = orchestrator.Scan(_tempDir);

        Assert.NotNull(result.EmptyItems);
        Assert.Contains(Path.Combine(_tempDir, "emptysub"), result.EmptyItems!.EmptyFolders);
        Assert.Contains(Path.Combine(_tempDir, "blank.txt"), result.EmptyItems.ZeroByteFiles);
    }

    [Fact]
    public void ScanForLargeFiles_PopulatesEmptyItems_Too()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "blank.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(_tempDir, "emptysub"));

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        var result = orchestrator.ScanForLargeFiles(_tempDir);

        Assert.NotNull(result.EmptyItems);
        Assert.Contains(Path.Combine(_tempDir, "emptysub"), result.EmptyItems!.EmptyFolders);
        Assert.Contains(Path.Combine(_tempDir, "blank.txt"), result.EmptyItems.ZeroByteFiles);
    }

    [Fact]
    public void Scan_PopulatesDuplicateFolders_ForIdenticalFolderCopies()
    {
        var content1 = "alpha"u8.ToArray();
        var content2 = "beta"u8.ToArray();
        Directory.CreateDirectory(Path.Combine(_tempDir, "copy1"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "copy2"));
        File.WriteAllBytes(Path.Combine(_tempDir, "copy1", "a.txt"), content1);
        File.WriteAllBytes(Path.Combine(_tempDir, "copy1", "b.txt"), content2);
        File.WriteAllBytes(Path.Combine(_tempDir, "copy2", "a.txt"), content1);
        File.WriteAllBytes(Path.Combine(_tempDir, "copy2", "b.txt"), content2);
        // A unique file at the root keeps _tempDir itself from being the match.
        File.WriteAllBytes(Path.Combine(_tempDir, "unique.txt"), "only-here"u8.ToArray());

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        var result = orchestrator.Scan(_tempDir);

        var folderGroup = Assert.Single(result.DuplicateFolders);
        Assert.Equal(2, folderGroup.Folders.Count);
        Assert.Contains(folderGroup.Folders, f => f.Path == Path.Combine(_tempDir, "copy1"));
        Assert.Contains(folderGroup.Folders, f => f.Path == Path.Combine(_tempDir, "copy2"));
        Assert.Equal(2, folderGroup.FileCount);
    }

    [Fact]
    public void Scan_ExcludesCloudPlaceholders_WhenAsked()
    {
        var content = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        var placeholderPath = Path.Combine(_tempDir, "b.txt");
        File.WriteAllBytes(placeholderPath, content);
        File.SetAttributes(placeholderPath, File.GetAttributes(placeholderPath) | FileAttributes.Offline);

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        var result = orchestrator.Scan(_tempDir, excludeCloudPlaceholders: true);

        Assert.Empty(result.DuplicateGroups);
        Assert.Equal(1, result.TotalFilesScanned);
    }

    [Fact]
    public void EnumerateForScan_CountsPlaceholders_AndScanEnumeratedCanExcludeThem()
    {
        // Two byte-identical files, one flagged as a cloud "online-only" placeholder.
        var content = "dup"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        var placeholder = Path.Combine(_tempDir, "b.txt");
        File.WriteAllBytes(placeholder, content);
        File.SetAttributes(placeholder, File.GetAttributes(placeholder) | FileAttributes.Offline);

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        // The single enumeration walk sees both files and counts the placeholder.
        var enumeration = orchestrator.EnumerateForScan(_tempDir);
        Assert.Equal(2, enumeration.Files.Count);
        Assert.Equal(1, enumeration.CloudPlaceholderCount);

        // Processing that same enumeration (no second walk) with exclusion drops
        // the placeholder, so the once-duplicate pair is no longer a duplicate.
        var excluded = orchestrator.ScanEnumerated(enumeration, _tempDir, excludeCloudPlaceholders: true);
        Assert.Equal(1, excluded.TotalFilesScanned);
        Assert.Empty(excluded.DuplicateGroups);
    }

    [Fact]
    public void Scan_Throws_WhenCancellationAlreadyRequested()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "content");

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);
        var cancelledToken = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(() => orchestrator.Scan(_tempDir, cancellationToken: cancelledToken));
    }

    [Fact]
    public void Scan_Throws_WhenCancellationAlreadyRequested_ForEmptyDirectory()
    {
        // _tempDir is empty here (no files written). FileEnumerator only
        // checks cancellation once it yields at least one path, so without
        // an eager check in ScanOrchestrator itself, an empty directory
        // would silently succeed instead of honoring the cancelled token.
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);
        var cancelledToken = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(() => orchestrator.Scan(_tempDir, cancellationToken: cancelledToken));
    }

    [Fact]
    public void ScanForLargeFiles_ReturnsEveryFile_IncludingOnesWithNoDuplicate()
    {
        var content = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "unique.txt"), "unique, one of a kind"u8.ToArray());

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        var result = orchestrator.ScanForLargeFiles(_tempDir);

        Assert.Empty(result.DuplicateGroups);
        Assert.Equal(3, result.TotalFilesScanned);
        Assert.NotNull(result.AllFiles);
        Assert.Equal(3, result.AllFiles!.Count);
        Assert.Contains(result.AllFiles, f => f.Path == Path.Combine(_tempDir, "unique.txt"));
    }

    [Fact]
    public void ScanForLargeFiles_DoesNotOverwriteHashesAPriorScanCached()
    {
        var content = "duplicate content"u8.ToArray();
        var pathA = Path.Combine(_tempDir, "a.txt");
        var pathB = Path.Combine(_tempDir, "b.txt");
        File.WriteAllBytes(pathA, content);
        File.WriteAllBytes(pathB, content);

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);

        orchestrator.Scan(_tempDir);
        var hashedBefore = store.GetFileByPath(pathA);
        Assert.NotNull(hashedBefore!.FullHash);

        orchestrator.ScanForLargeFiles(_tempDir);
        var hashedAfter = store.GetFileByPath(pathA);

        Assert.NotNull(hashedAfter!.FullHash);
        Assert.Equal(hashedBefore.FullHash, hashedAfter.FullHash);
    }

    [Fact]
    public void ScanForLargeFiles_Throws_WhenCancellationAlreadyRequested_ForEmptyDirectory()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        var orchestrator = new ScanOrchestrator(store);
        var cancelledToken = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(
            () => orchestrator.ScanForLargeFiles(_tempDir, cancellationToken: cancelledToken));
    }
}
