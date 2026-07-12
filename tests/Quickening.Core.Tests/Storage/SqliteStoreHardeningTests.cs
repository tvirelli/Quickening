using Microsoft.Data.Sqlite;
using Quickening.Core.Models;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests.Storage;

/// <summary>
/// Regression tests for the store-hardening batch: hash-preserving upserts,
/// case-insensitive path keys (including the legacy-table rebuild), row
/// pruning, undo bookkeeping, and thread-safety under concurrent writers.
/// </summary>
public class SqliteStoreHardeningTests
{
    private static SqliteStore CreateStore()
    {
        var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();
        return store;
    }

    private static FileRecord MakeRecord(string path, long size = 100, DateTime? mtime = null, byte[]? fullHash = null, byte[]? partialHash = null) =>
        new()
        {
            Path = path,
            SizeBytes = size,
            LastWriteTimeUtc = mtime ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Category = MimeCategory.Other,
            FullHash = fullHash,
            PartialHash = partialHash,
        };

    [Fact]
    public void UpsertFile_PreservesCachedHashes_WhenSizeAndMtimeUnchanged()
    {
        using var store = CreateStore();
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hash = new byte[] { 1, 2, 3 };

        store.UpsertFile(MakeRecord(@"C:\t\a.bin", mtime: mtime, fullHash: hash, partialHash: hash));

        // Second scan: same file, unchanged, but this scan never needed to
        // hash it - the record arrives with null hashes.
        store.UpsertFile(MakeRecord(@"C:\t\a.bin", mtime: mtime));

        var cached = store.GetFileByPath(@"C:\t\a.bin");
        Assert.NotNull(cached);
        Assert.Equal(hash, cached!.FullHash);
        Assert.Equal(hash, cached.PartialHash);
    }

    [Fact]
    public void UpsertFile_DiscardsCachedHashes_WhenFileChanged()
    {
        using var store = CreateStore();
        var hash = new byte[] { 1, 2, 3 };

        store.UpsertFile(MakeRecord(@"C:\t\a.bin", size: 100, fullHash: hash, partialHash: hash));
        store.UpsertFile(MakeRecord(@"C:\t\a.bin", size: 200)); // grew: hashes are stale

        var cached = store.GetFileByPath(@"C:\t\a.bin");
        Assert.NotNull(cached);
        Assert.Null(cached!.FullHash);
        Assert.Null(cached.PartialHash);
    }

    [Fact]
    public void GetFileByPath_IsCaseInsensitive_LikeNtfs()
    {
        using var store = CreateStore();
        store.UpsertFile(MakeRecord(@"C:\Users\Tony\Docs\a.bin"));

        Assert.NotNull(store.GetFileByPath(@"c:\users\tony\docs\A.BIN"));
    }

    [Fact]
    public void UpsertFile_DifferentCasing_UpdatesSameRow_NotASecondOne()
    {
        using var store = CreateStore();
        store.UpsertFile(MakeRecord(@"C:\t\a.bin", size: 100));
        store.UpsertFile(MakeRecord(@"c:\T\A.BIN", size: 200));

        var cached = store.GetFileByPath(@"C:\t\a.bin");
        Assert.NotNull(cached);
        Assert.Equal(200, cached!.SizeBytes);
    }

    [Fact]
    public void FindFileByFullHash_ExcludesSelf_EvenWhenCasingDiffers()
    {
        using var store = CreateStore();
        var hash = new byte[] { 9, 9, 9 };
        store.UpsertFile(MakeRecord(@"C:\t\a.bin", fullHash: hash));

        // The watcher re-reports the same file with different casing - it
        // must NOT match itself as its own duplicate.
        Assert.Null(store.FindFileByFullHash(hash, excludingPath: @"c:\T\A.BIN"));
    }

    [Fact]
    public void Initialize_RebuildsLegacyCaseSensitiveFilesTable()
    {
        // Simulate a database created before COLLATE NOCASE existed: build
        // the legacy schema by hand (via a shared-cache in-memory DB, so a
        // second connection - the store's - sees the same tables), insert
        // case-duplicate rows, then let Initialize migrate it.
        var connectionString = "Data Source=legacy-migration-test;Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        using (var command = keepAlive.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Files (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE,
                    SizeBytes INTEGER NOT NULL,
                    LastWriteTimeUtc TEXT NOT NULL,
                    PartialHash BLOB,
                    FullHash BLOB,
                    MimeCategory TEXT NOT NULL,
                    FirstSeenUtc TEXT NOT NULL,
                    LastSeenUtc TEXT NOT NULL
                );
                INSERT INTO Files (Path, SizeBytes, LastWriteTimeUtc, MimeCategory, FirstSeenUtc, LastSeenUtc)
                    VALUES ('C:\t\a.bin', 1, '2026-01-01T00:00:00.0000000Z', 'Other', 'x', 'x'),
                           ('c:\T\A.BIN', 2, '2026-01-01T00:00:00.0000000Z', 'Other', 'x', 'x');
                """;
            command.ExecuteNonQuery();
        }

        using var store = new SqliteStore(connectionString);
        store.Initialize();

        // Case-duplicate rows collapsed to one; lookups are case-insensitive;
        // the PerceptualHash column was added along the way.
        var record = store.GetFileByPath(@"c:\t\A.bin");
        Assert.NotNull(record);
        store.UpsertFile(MakeRecord(@"C:\T\a.BIN", size: 42));
        Assert.Equal(42, store.GetFileByPath(@"C:\t\a.bin")!.SizeBytes);
    }

    [Fact]
    public void PruneFilesNotSeenSince_RemovesOnlyStaleRowsUnderRoot()
    {
        using var store = CreateStore();
        store.UpsertFile(MakeRecord(@"C:\scanroot\gone.bin"));
        store.UpsertFile(MakeRecord(@"C:\elsewhere\other.bin"));
        var cutoff = DateTime.UtcNow.AddMinutes(1); // both rows are "stale" vs this cutoff

        var pruned = store.PruneFilesNotSeenSince(@"C:\scanroot", cutoff);

        Assert.Equal(1, pruned);
        Assert.Null(store.GetFileByPath(@"C:\scanroot\gone.bin"));
        Assert.NotNull(store.GetFileByPath(@"C:\elsewhere\other.bin"));
    }

    [Fact]
    public void PruneFilesNotSeenSince_KeepsRowsSeenThisScan()
    {
        using var store = CreateStore();
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        store.UpsertFile(MakeRecord(@"C:\scanroot\fresh.bin")); // LastSeenUtc = now > cutoff

        var pruned = store.PruneFilesNotSeenSince(@"C:\scanroot", cutoff);

        Assert.Equal(0, pruned);
        Assert.NotNull(store.GetFileByPath(@"C:\scanroot\fresh.bin"));
    }

    [Fact]
    public void PruneFilesNotSeenSince_KeepsRowsForFilesStillOnDisk()
    {
        // "Not seen by this scan" is not "deleted": a narrower re-scan
        // (hidden files off, placeholders excluded) skips files that still
        // exist - their rows must survive pruning.
        using var store = CreateStore();
        var tempFile = Path.GetTempFileName();
        try
        {
            store.UpsertFile(MakeRecord(tempFile));
            var cutoff = DateTime.UtcNow.AddMinutes(1); // row is "stale" vs this cutoff

            var pruned = store.PruneFilesNotSeenSince(Path.GetTempPath(), cutoff);

            Assert.Equal(0, pruned);
            Assert.NotNull(store.GetFileByPath(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void RemoveFileRecord_DeletesTheRow()
    {
        using var store = CreateStore();
        store.UpsertFile(MakeRecord(@"C:\t\a.bin"));

        store.RemoveFileRecord(@"C:\t\a.bin");

        Assert.Null(store.GetFileByPath(@"C:\t\a.bin"));
    }

    [Fact]
    public void UndoTrashedFile_ReversesStatsAndSessionTotals()
    {
        using var store = CreateStore();
        var batchId = store.BeginRemovalBatch("Downloads", "Duplicates");
        store.RecordTrashedFile(@"C:\t\a.bin", null, 500, batchId);
        store.RecordTrashedFile(@"C:\t\b.bin", null, 300, batchId);
        store.CompleteRemovalBatch(batchId, 2, 800);

        store.UndoTrashedFile(@"C:\t\a.bin");

        var (bytes, files) = store.GetLifetimeStats();
        Assert.Equal(300, bytes);
        Assert.Equal(1, files);

        var session = Assert.Single(store.GetRemovalSessions());
        Assert.Equal(1, session.FilesRemoved);
        Assert.Equal(300, session.BytesRemoved);
        Assert.Single(store.GetTrashLogForSession(batchId));
    }

    [Fact]
    public void UndoTrashedFile_RemovesSession_WhenAllFilesUndone()
    {
        using var store = CreateStore();
        var batchId = store.BeginRemovalBatch("Downloads", "Duplicates");
        store.RecordTrashedFile(@"C:\t\a.bin", null, 500, batchId);
        store.RecordTrashedFile(@"C:\t\b.bin", null, 300, batchId);
        store.CompleteRemovalBatch(batchId, 2, 800);

        store.UndoTrashedFile(@"C:\t\a.bin");
        store.UndoTrashedFile(@"C:\t\b.bin");

        // Every file undone -> no leftover "0 files / 0 B" session in History,
        // and its trash-log rows are gone too.
        Assert.Empty(store.GetRemovalSessions());
        Assert.Empty(store.GetTrashLogForSession(batchId));
    }

    [Fact]
    public void UndoTrashedFile_KeepsSession_WhenFilesRemain()
    {
        using var store = CreateStore();
        var batchId = store.BeginRemovalBatch("Downloads", "Duplicates");
        store.RecordTrashedFile(@"C:\t\a.bin", null, 500, batchId);
        store.RecordTrashedFile(@"C:\t\b.bin", null, 300, batchId);
        store.CompleteRemovalBatch(batchId, 2, 800);

        store.UndoTrashedFile(@"C:\t\a.bin"); // only one undone

        var session = Assert.Single(store.GetRemovalSessions());
        Assert.Equal(1, session.FilesRemoved);
        Assert.Equal(300, session.BytesRemoved);
    }

    [Fact]
    public void UndoTrashedFile_UnknownPath_IsANoOp()
    {
        using var store = CreateStore();
        store.RecordTrashedFile(@"C:\t\a.bin", null, 500);

        store.UndoTrashedFile(@"C:\t\never-deleted.bin");

        var (bytes, files) = store.GetLifetimeStats();
        Assert.Equal(500, bytes);
        Assert.Equal(1, files);
    }

    [Fact]
    public async Task ConcurrentWritersAndReaders_DoNotCorruptOrThrow()
    {
        // The exact shape that used to break: scheduled-scan upserts on one
        // thread while the watcher looks up hashes and the UI thread records
        // deletions - all on one shared store instance.
        using var store = CreateStore();
        var tasks = new List<Task>();
        for (var t = 0; t < 4; t++)
        {
            var thread = t;
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    var path = $@"C:\t\{thread}-{i}.bin";
                    store.UpsertFile(MakeRecord(path, size: i + 1, fullHash: new byte[] { (byte)thread, (byte)i }));
                    store.GetFileByPath(path);
                    store.FindFileByFullHash(new byte[] { (byte)thread, (byte)i }, excludingPath: path);
                    store.RecordTrashedFile(path, null, i + 1);
                }
            }));
        }

        await Task.WhenAll(tasks);

        var (bytes, files) = store.GetLifetimeStats();
        Assert.Equal(200, files);
        Assert.Equal(4 * (50 * 51 / 2), bytes);
    }
}
