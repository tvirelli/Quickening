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
