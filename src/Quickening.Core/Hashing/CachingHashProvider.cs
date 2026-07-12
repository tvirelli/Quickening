using System.Collections.Concurrent;
using Quickening.Core.Storage;

namespace Quickening.Core.Hashing;

/// <summary>
/// Decorates an IHashProvider, checking SqliteStore for a cached hash before
/// recomputing. Only reads the cache - persisting a freshly computed hash
/// back to the store is the caller's responsibility. Thread-safe (the store
/// serializes internally; the memo below is concurrent).
/// </summary>
public sealed class CachingHashProvider : IHashProvider
{
    private readonly IHashProvider _inner;
    private readonly SqliteStore _store;

    // A duplicate scan asks for the partial hash and then (for surviving
    // candidates) the full hash of the same path - without this memo that is
    // two identical point queries per file. The memo only caches the *lookup*;
    // validity is still re-checked against fresh size/mtime on every call, so
    // a stale entry can only cause a recompute, never a wrong cache hit.
    private readonly ConcurrentDictionary<string, Models.FileRecord?> _lookupMemo =
        new(StringComparer.OrdinalIgnoreCase);

    public CachingHashProvider(IHashProvider inner, SqliteStore store)
    {
        _inner = inner;
        _store = store;
    }

    /// <summary>
    /// The memoized store row for a path (null when the store has none) -
    /// lets ScanOrchestrator's perceptual-hash cache check reuse the lookup
    /// this provider already paid for instead of issuing its own point query
    /// per image.
    /// </summary>
    public Models.FileRecord? GetCachedRecord(string path)
    {
        // Only positive results are memoized: a "no row yet" answer goes
        // stale the moment a scan upserts the file, and a long-lived
        // provider (the tray watcher's) would then never see the new row.
        if (_lookupMemo.TryGetValue(path, out var cached))
        {
            return cached;
        }

        cached = _store.GetFileByPath(path);
        if (cached is not null)
        {
            _lookupMemo[path] = cached;
        }

        return cached;
    }

    public byte[] ComputePartialHash(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetCachedHash(path, cached => cached.PartialHash, out var hash))
        {
            return hash;
        }

        return _inner.ComputePartialHash(path, cancellationToken);
    }

    public byte[] ComputeFullHash(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetCachedHash(path, cached => cached.FullHash, out var hash))
        {
            return hash;
        }

        return _inner.ComputeFullHash(path, cancellationToken);
    }

    private bool TryGetCachedHash(string path, Func<Models.FileRecord, byte[]?> select, out byte[] hash)
    {
        var info = new FileInfo(path);

        // Backstop for very long-lived instances (the tray watcher): the memo
        // is a pure lookup cache, so dropping it wholesale is always safe.
        if (_lookupMemo.Count > 500_000)
        {
            _lookupMemo.Clear();
        }

        var cached = GetCachedRecord(path);

        var candidate = cached is not null &&
                        cached.SizeBytes == info.Length &&
                        cached.LastWriteTimeUtc == info.LastWriteTimeUtc
            ? select(cached)
            : null;

        hash = candidate ?? Array.Empty<byte>();
        return candidate is not null;
    }
}
