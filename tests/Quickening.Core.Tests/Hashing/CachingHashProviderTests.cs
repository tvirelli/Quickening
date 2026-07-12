using Quickening.Core.Hashing;
using Quickening.Core.Models;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests.Hashing;

public class CachingHashProviderTests : IDisposable
{
    private readonly string _tempFile;
    private readonly SqliteStore _store;
    private readonly CountingHashProvider _counting;
    private readonly CachingHashProvider _caching;

    public CachingHashProviderTests()
    {
        _tempFile = Path.GetTempFileName();
        File.WriteAllBytes(_tempFile, "real file content"u8.ToArray());

        _store = new SqliteStore("Data Source=:memory:");
        _store.Initialize();

        _counting = new CountingHashProvider(new HashProvider());
        _caching = new CachingHashProvider(_counting, _store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_tempFile);
    }

    private sealed class CountingHashProvider : IHashProvider
    {
        private readonly IHashProvider _inner;
        public int FullHashCallCount { get; private set; }

        public CountingHashProvider(IHashProvider inner) => _inner = inner;

        public byte[] ComputePartialHash(string path, CancellationToken cancellationToken = default) =>
            _inner.ComputePartialHash(path, cancellationToken);

        public byte[] ComputeFullHash(string path, CancellationToken cancellationToken = default)
        {
            FullHashCallCount++;
            return _inner.ComputeFullHash(path, cancellationToken);
        }
    }

    [Fact]
    public void ComputeFullHash_ComputesFresh_WhenNoCacheEntryExists()
    {
        var result = _caching.ComputeFullHash(_tempFile);

        Assert.Equal(1, _counting.FullHashCallCount);
        Assert.Equal(new HashProvider().ComputeFullHash(_tempFile), result);
    }

    [Fact]
    public void ComputeFullHash_ReturnsCachedValue_WhenSizeAndMtimeMatch()
    {
        var info = new FileInfo(_tempFile);
        var bogusHash = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        _store.UpsertFile(new FileRecord
        {
            Path = _tempFile,
            SizeBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            Category = MimeCategory.Other,
            FullHash = bogusHash,
        });

        var result = _caching.ComputeFullHash(_tempFile);

        Assert.Equal(0, _counting.FullHashCallCount);
        Assert.Equal(bogusHash, result);
    }

    [Fact]
    public void ComputeFullHash_RecomputesFresh_WhenCachedMtimeIsStale()
    {
        _store.UpsertFile(new FileRecord
        {
            Path = _tempFile,
            SizeBytes = new FileInfo(_tempFile).Length,
            LastWriteTimeUtc = DateTime.UtcNow.AddDays(-1), // stale on purpose
            Category = MimeCategory.Other,
            FullHash = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
        });

        var result = _caching.ComputeFullHash(_tempFile);

        Assert.Equal(1, _counting.FullHashCallCount);
        Assert.Equal(new HashProvider().ComputeFullHash(_tempFile), result);
    }
}
