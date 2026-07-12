using Quickening.Core.Duplicates;
using Quickening.Core.Hashing;
using Quickening.Core.Scanning;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests.Integration;

public class EndToEndTests : IDisposable
{
    private readonly string _tempDir;

    public EndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "QuickeningE2ETests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class CountingHashProvider : IHashProvider
    {
        private readonly IHashProvider _inner;
        public int PartialHashCallCount { get; private set; }
        public int FullHashCallCount { get; private set; }

        public CountingHashProvider(IHashProvider inner) => _inner = inner;

        public byte[] ComputePartialHash(string path, CancellationToken cancellationToken = default)
        {
            PartialHashCallCount++;
            return _inner.ComputePartialHash(path, cancellationToken);
        }

        public byte[] ComputeFullHash(string path, CancellationToken cancellationToken = default)
        {
            FullHashCallCount++;
            return _inner.ComputeFullHash(path, cancellationToken);
        }
    }

    [Fact]
    public void SecondScan_ReusesCachedHashes_AndStillFindsSameDuplicates()
    {
        var duplicateContent = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), duplicateContent);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), duplicateContent);
        File.WriteAllBytes(Path.Combine(_tempDir, "unique.txt"), "something else entirely"u8.ToArray());

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var countingInner = new CountingHashProvider(new HashProvider());
        var cachingProvider = new CachingHashProvider(countingInner, store);
        var engine = new DuplicateEngine(cachingProvider);
        var enumerator = new FileEnumerator();

        // First scan: cache is empty, hashes must be computed fresh.
        var filesFirstScan = enumerator.Enumerate(_tempDir).ToList();
        var groupsFirstScan = engine.FindDuplicates(filesFirstScan).ToList();

        Assert.Single(groupsFirstScan);
        Assert.Equal(2, groupsFirstScan[0].Files.Count);
        var partialHashCallsAfterFirstScan = countingInner.PartialHashCallCount;
        var fullHashCallsAfterFirstScan = countingInner.FullHashCallCount;
        Assert.True(partialHashCallsAfterFirstScan > 0, "first scan must compute partial hashes, not find them already cached");
        Assert.True(fullHashCallsAfterFirstScan > 0, "first scan must compute hashes, not find them already cached");

        // This is the scan orchestrator's responsibility per Task 8's design:
        // persist whatever hashes DuplicateEngine populated on the FileRecords.
        foreach (var file in filesFirstScan)
        {
            store.UpsertFile(file);
        }

        // Second scan: same files, completely unchanged. FileEnumerator produces
        // FRESH FileRecord instances (per its documented single-pass contract) -
        // this proves the cache hit comes from the SQLite-backed path+size+mtime
        // lookup, not from any in-memory object identity.
        var filesSecondScan = enumerator.Enumerate(_tempDir).ToList();
        var groupsSecondScan = engine.FindDuplicates(filesSecondScan).ToList();

        Assert.Single(groupsSecondScan);
        Assert.Equal(2, groupsSecondScan[0].Files.Count);
        Assert.Equal(partialHashCallsAfterFirstScan, countingInner.PartialHashCallCount);
        Assert.Equal(fullHashCallsAfterFirstScan, countingInner.FullHashCallCount);
    }
}
