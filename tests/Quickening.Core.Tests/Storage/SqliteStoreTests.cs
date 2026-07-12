using Quickening.Core.Models;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests.Storage;

public class SqliteStoreTests
{
    [Fact]
    public void Initialize_CreatesFilesTable()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var record = new FileRecord
        {
            Path = @"C:\test\file.txt",
            SizeBytes = 100,
            LastWriteTimeUtc = DateTime.UtcNow,
            Category = MimeCategory.Document,
        };

        store.UpsertFile(record);
        var retrieved = store.GetFileByPath(@"C:\test\file.txt");

        Assert.NotNull(retrieved);
        Assert.Equal(100, retrieved!.SizeBytes);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)] // top bit set - a plain bit pattern, not a magnitude, so this must round-trip too (see UpsertFile's own comment on the signed/unsigned reinterpretation).
    [InlineData(1234567890123UL)]
    public void UpsertFile_RoundTripsPerceptualHash(ulong perceptualHash)
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        store.UpsertFile(new FileRecord
        {
            Path = @"C:\test\photo.jpg",
            SizeBytes = 100,
            LastWriteTimeUtc = DateTime.UtcNow,
            Category = MimeCategory.Image,
            PerceptualHash = perceptualHash,
        });

        var retrieved = store.GetFileByPath(@"C:\test\photo.jpg");

        Assert.Equal(perceptualHash, retrieved!.PerceptualHash);
    }

    [Fact]
    public void UpsertFile_LeavesPerceptualHashNull_WhenNeverSet()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        store.UpsertFile(new FileRecord
        {
            Path = @"C:\test\doc.txt",
            SizeBytes = 100,
            LastWriteTimeUtc = DateTime.UtcNow,
            Category = MimeCategory.Document,
        });

        var retrieved = store.GetFileByPath(@"C:\test\doc.txt");

        Assert.Null(retrieved!.PerceptualHash);
    }

    [Fact]
    public void RecordTrashedFile_IncrementsLifetimeStatsAtomically()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        store.RecordTrashedFile(
            originalPath: @"C:\test\deleted.txt",
            recycleBinPath: @"C:\$Recycle.Bin\deleted.txt",
            sizeBytes: 500);

        var (bytesReclaimed, filesRemoved) = store.GetLifetimeStats();

        Assert.Equal(500, bytesReclaimed);
        Assert.Equal(1, filesRemoved);
    }

    [Fact]
    public void RecordTrashedFile_AccumulatesAcrossMultipleCalls()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        store.RecordTrashedFile(@"C:\a.txt", @"C:\$Recycle.Bin\a.txt", 100);
        store.RecordTrashedFile(@"C:\b.txt", @"C:\$Recycle.Bin\b.txt", 250);

        var (bytesReclaimed, filesRemoved) = store.GetLifetimeStats();

        Assert.Equal(350, bytesReclaimed);
        Assert.Equal(2, filesRemoved);
    }

    [Fact]
    public void GetFileByPath_ReturnsNull_WhenNotFound()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var result = store.GetFileByPath(@"C:\does\not\exist.txt");

        Assert.Null(result);
    }

    [Fact]
    public void FindFileByFullHash_ReturnsTheOtherFile_WhenHashesMatch()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var hash = new byte[] { 1, 2, 3, 4 };
        store.UpsertFile(new FileRecord
        {
            Path = @"C:\test\original.txt",
            SizeBytes = 100,
            LastWriteTimeUtc = DateTime.UtcNow,
            Category = MimeCategory.Document,
            FullHash = hash,
        });

        var match = store.FindFileByFullHash(hash, excludingPath: @"C:\test\newlyArrived.txt");

        Assert.NotNull(match);
        Assert.Equal(@"C:\test\original.txt", match!.Path);
    }

    [Fact]
    public void FindFileByFullHash_ExcludesTheGivenPath_EvenWithAMatchingHash()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var hash = new byte[] { 1, 2, 3, 4 };
        store.UpsertFile(new FileRecord
        {
            Path = @"C:\test\onlyCopy.txt",
            SizeBytes = 100,
            LastWriteTimeUtc = DateTime.UtcNow,
            Category = MimeCategory.Document,
            FullHash = hash,
        });

        var match = store.FindFileByFullHash(hash, excludingPath: @"C:\test\onlyCopy.txt");

        Assert.Null(match);
    }

    [Fact]
    public void FindFileByFullHash_ReturnsNull_WhenNoFileHasThatHash()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var match = store.FindFileByFullHash(new byte[] { 9, 9, 9 }, excludingPath: @"C:\test\newlyArrived.txt");

        Assert.Null(match);
    }

    [Fact]
    public void UpsertFile_UpdatesExistingRow_WhenPathAlreadyExists()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var original = new FileRecord
        {
            Path = @"C:\test\changing.txt",
            SizeBytes = 100,
            LastWriteTimeUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Category = MimeCategory.Document,
        };
        store.UpsertFile(original);

        var updated = new FileRecord
        {
            Path = @"C:\test\changing.txt",
            SizeBytes = 999,
            LastWriteTimeUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Category = MimeCategory.Document,
        };
        store.UpsertFile(updated);

        var retrieved = store.GetFileByPath(@"C:\test\changing.txt");

        Assert.NotNull(retrieved);
        Assert.Equal(999, retrieved!.SizeBytes);
        Assert.Equal(new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc), retrieved.LastWriteTimeUtc);
    }

    [Fact]
    public void UpsertFile_ThenGetFileByPath_RoundTripsAllFields()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var expectedLastWriteTimeUtc = new DateTime(2024, 3, 15, 10, 30, 0, DateTimeKind.Utc);
        var record = new FileRecord
        {
            Path = @"C:\test\full.txt",
            SizeBytes = 12345,
            LastWriteTimeUtc = expectedLastWriteTimeUtc,
            Category = MimeCategory.Archive,
            PartialHash = new byte[] { 1, 2, 3 },
            FullHash = new byte[] { 4, 5, 6, 7 },
        };

        store.UpsertFile(record);
        var retrieved = store.GetFileByPath(@"C:\test\full.txt");

        Assert.NotNull(retrieved);
        Assert.Equal(12345, retrieved!.SizeBytes);
        Assert.Equal(expectedLastWriteTimeUtc, retrieved.LastWriteTimeUtc);
        Assert.Equal(MimeCategory.Archive, retrieved.Category);
        Assert.Equal(new byte[] { 1, 2, 3 }, retrieved.PartialHash);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, retrieved.FullHash);
    }

    [Fact]
    public void BeginRemovalBatch_ThenRecordTrashedFile_GroupsUnderThatSession()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var batchId = store.BeginRemovalBatch("Downloads", "Duplicates");
        store.RecordTrashedFile(@"C:\a.txt", null, 100, batchId);
        store.RecordTrashedFile(@"C:\b.txt", null, 250, batchId);
        store.CompleteRemovalBatch(batchId, filesRemoved: 2, bytesRemoved: 350);

        var sessions = store.GetRemovalSessions();
        Assert.Single(sessions);
        Assert.Equal("Downloads", sessions[0].TargetLabel);
        Assert.Equal("Duplicates", sessions[0].ModeLabel);
        Assert.Equal(2, sessions[0].FilesRemoved);
        Assert.Equal(350, sessions[0].BytesRemoved);

        var entries = store.GetTrashLogForSession(batchId);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.OriginalPath == @"C:\a.txt" && e.SizeBytes == 100);
        Assert.Contains(entries, e => e.OriginalPath == @"C:\b.txt" && e.SizeBytes == 250);
    }

    [Fact]
    public void GetRemovalSessions_ExcludesBatchesNeverCompleted()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        // Started but never completed - e.g. the app crashed mid-removal.
        store.BeginRemovalBatch("Documents", "Large files");

        var sessions = store.GetRemovalSessions();

        Assert.Empty(sessions);
    }

    [Fact]
    public void GetRemovalSessions_OrdersNewestFirst()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var firstBatch = store.BeginRemovalBatch("Older", "Duplicates");
        store.CompleteRemovalBatch(firstBatch, 1, 10);
        var secondBatch = store.BeginRemovalBatch("Newer", "Duplicates");
        store.CompleteRemovalBatch(secondBatch, 1, 10);

        var sessions = store.GetRemovalSessions();

        Assert.Equal(2, sessions.Count);
        Assert.Equal(secondBatch, sessions[0].Id);
        Assert.Equal(firstBatch, sessions[1].Id);
    }

    [Fact]
    public void GetRemovalSessions_ReturnsEmpty_WhenNoneRecorded()
    {
        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        Assert.Empty(store.GetRemovalSessions());
    }
}
