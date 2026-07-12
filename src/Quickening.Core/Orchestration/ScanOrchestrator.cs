using System.Diagnostics;
using Quickening.Core.Duplicates;
using Quickening.Core.Hashing;
using Quickening.Core.Models;
using Quickening.Core.Safety;
using Quickening.Core.Scanning;
using Quickening.Core.Similarity;
using Quickening.Core.Storage;

namespace Quickening.Core.Orchestration;

public sealed class ScanResult
{
    public required IReadOnlyList<DuplicateGroup> DuplicateGroups { get; init; }
    public required int TotalFilesScanned { get; init; }

    /// <summary>
    /// Every file ScanForLargeFiles enumerated, regardless of whether it
    /// has a duplicate anywhere - null after a plain Scan, which only
    /// exposes files via DuplicateGroups (so a unique file, no matter how
    /// large, never appears there). Large Files mode needs this instead of
    /// DuplicateGroups precisely because most large files are unique.
    /// </summary>
    public IReadOnlyList<Models.FileRecord>? AllFiles { get; init; }

    /// <summary>
    /// "Looks-alike photos" (new-screens 4k) - images that are similar but
    /// NOT byte-identical (those stay in DuplicateGroups only). Always an
    /// empty list rather than null after a plain Scan (never populated by
    /// ScanForLargeFiles, which has no reason to compute image hashes).
    /// </summary>
    public IReadOnlyList<SimilarityGroup> SimilarityGroups { get; init; } = Array.Empty<SimilarityGroup>();
}

public enum ScanPhase { Enumerating, Comparing, Finalizing }

/// <summary>
/// Reported throughout ScanOrchestrator.Scan - throttled to roughly every
/// 50ms during the enumeration phase (Phase == Enumerating, CurrentPath is
/// the latest file's path, TotalFiles/DuplicateGroupsFoundSoFar/
/// ReclaimableBytesSoFar are all null since neither is known yet), then
/// again throughout the hashing/comparing phase (Phase == Comparing,
/// wrapping DuplicateEngine's own HashingProgress - CurrentPath is "" since
/// no single path is meaningful there). Comparing's FilesProcessed/
/// TotalFiles is a real, monotonically increasing count covering every file
/// (see DuplicateEngine.HashingProgress's own doc comment), so a progress
/// UI can safely treat this as "N of totalFiles done" across the whole
/// scan. Reports are relayed synchronously in order - the counter never
/// goes backwards at the receiving end.
/// </summary>
public sealed record ScanProgress(
    int FilesProcessed,
    string CurrentPath,
    ScanPhase Phase = ScanPhase.Enumerating,
    int? TotalFiles = null,
    int? DuplicateGroupsFoundSoFar = null,
    long? ReclaimableBytesSoFar = null);

/// <summary>
/// Wires FileEnumerator, DuplicateEngine (via CachingHashProvider over the
/// given SqliteStore), and hash persistence together into one call. This is
/// a synchronous, potentially slow call over a large tree - callers (the UI)
/// must run it off their own thread (e.g. via Task.Run), which they'd need
/// to do regardless of whether the hashing inside is ever parallelized.
/// </summary>
public sealed class ScanOrchestrator
{
    // Progress is throttled to this interval: a 500k-file scan reporting
    // per file means ~1M dispatcher posts at the UI - enough queue backlog
    // to make the window (and its Stop button) unresponsive exactly when
    // the user wants it.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(50);

    private readonly SqliteStore _store;

    public ScanOrchestrator(SqliteStore store)
    {
        _store = store;
    }

    public ScanResult Scan(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        bool includeHiddenFiles = false,
        bool allowProtectedPaths = false,
        bool paranoidMode = false,
        bool excludeCloudPlaceholders = false,
        CancellationToken cancellationToken = default,
        bool computeSimilarity = false)
    {
        // FileEnumerator only checks cancellation once it yields at least one
        // path, so an already-cancelled token against an empty directory
        // would otherwise silently proceed instead of throwing. Check eagerly
        // here so cancellation is honored regardless of directory contents.
        cancellationToken.ThrowIfCancellationRequested();

        var scanStartUtc = DateTime.UtcNow;
        var enumerator = new FileEnumerator();
        var hashProvider = new CachingHashProvider(new HashProvider(), _store);
        var engine = new DuplicateEngine(hashProvider);

        var files = EnumerateWithProgress(
            enumerator, rootPath, progress, includeHiddenFiles, allowProtectedPaths, excludeCloudPlaceholders, cancellationToken);

        // A synchronous relay (NOT Progress<T>: constructed on this worker
        // thread it would capture no SynchronizationContext and post every
        // report to the thread pool individually - unordered, so the UI can
        // watch the counter go backwards). The caller's own IProgress does
        // whatever marshaling it needs; relaying inline preserves order.
        // Throttled by time, except reports that carry a new duplicate-group
        // count (they drive the live "found so far" indicator) and the final
        // "all files done" report.
        IProgress<HashingProgress>? hashingProgress = null;
        if (progress is not null)
        {
            var throttle = Stopwatch.StartNew();
            var lastGroupCount = -1;
            hashingProgress = new SynchronousProgress<HashingProgress>(hp =>
            {
                var isFinal = hp.FilesHashed >= hp.TotalFiles;
                if (!isFinal && hp.DuplicateGroupsFoundSoFar == lastGroupCount && throttle.Elapsed < ProgressInterval)
                {
                    return;
                }

                lastGroupCount = hp.DuplicateGroupsFoundSoFar;
                throttle.Restart();
                progress.Report(new ScanProgress(
                    hp.FilesHashed,
                    CurrentPath: hp.CurrentPath ?? "",
                    Phase: ScanPhase.Comparing,
                    TotalFiles: hp.TotalFiles,
                    DuplicateGroupsFoundSoFar: hp.DuplicateGroupsFoundSoFar,
                    ReclaimableBytesSoFar: hp.ReclaimableBytesSoFar));
            });
        }

        var groups = engine.FindDuplicates(files, hashingProgress, paranoidMode, cancellationToken).ToList();

        // Everything past here (perceptual hashing, similarity grouping, and
        // the DB upsert/prune) runs with no per-file progress and can take a
        // while on a big scan - the compare phase already hit 100%, so
        // without this the UI sits frozen at 100%. Flip to an indeterminate
        // "finishing up" state so the progress animation keeps moving.
        progress?.Report(new ScanProgress(0, CurrentPath: "", Phase: ScanPhase.Finalizing));

        IReadOnlyList<SimilarityGroup> similarityGroups = Array.Empty<SimilarityGroup>();
        if (computeSimilarity)
        {
            // Image-only, and only after DuplicateEngine has already run - the
            // exact-duplicate paths below are what let similarity grouping
            // exclude byte-identical files (new-screens 4k: "not exact copies").
            // Opt-in (computeSimilarity): a plain scan does exact matching only.
            foreach (var file in files)
            {
                if (file.Category == MimeCategory.Image)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ComputePerceptualHashIfNeeded(file, hashProvider);
                }
            }

            var exactDuplicatePaths = groups
                .SelectMany(g => g.Files)
                .Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            similarityGroups = new SimilarityEngine().FindSimilarGroups(files, exactDuplicatePaths, cancellationToken);
        }

        // One transaction for the whole scan's rows (per-row autocommit is
        // one fsync per file), then drop rows for files this scan no longer
        // found - stale rows are what let the tray watcher claim "you
        // already have this file" about a copy deleted weeks ago.
        _store.UpsertFiles(files, cancellationToken);
        _store.PruneFilesNotSeenSince(HardBlockRules.NormalizePath(rootPath), scanStartUtc);

        return new ScanResult
        {
            DuplicateGroups = groups,
            TotalFilesScanned = files.Count,
            SimilarityGroups = similarityGroups,
        };
    }

    // Same cached-unless-size/mtime-changed pattern CachingHashProvider uses
    // for PartialHash/FullHash - and it reuses that provider's memoized
    // store lookup, so an image pays at most one point query per scan
    // instead of one here plus one per hash call.
    private static void ComputePerceptualHashIfNeeded(FileRecord file, CachingHashProvider hashProvider)
    {
        var cached = hashProvider.GetCachedRecord(file.Path);
        if (cached is { PerceptualHash: { } hash } && cached.LastWriteTimeUtc == file.LastWriteTimeUtc && cached.SizeBytes == file.SizeBytes)
        {
            file.PerceptualHash = hash;
            return;
        }

        file.PerceptualHash = PerceptualHashService.TryComputeHash(file.Path);
    }

    /// <summary>
    /// Finds every file under rootPath by size alone - no hashing, so it's
    /// far cheaper than Scan over a large tree and (unlike Scan) surfaces
    /// unique files too, via ScanResult.AllFiles. Deliberately does not
    /// write to the SqliteStore hash cache: these rows carry no hashes, so
    /// upserting them buys nothing (UpsertFile preserves cached hashes for
    /// unchanged files nowadays, but there's still no reason to pay the
    /// writes).
    /// </summary>
    public ScanResult ScanForLargeFiles(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        bool includeHiddenFiles = false,
        bool allowProtectedPaths = false,
        bool excludeCloudPlaceholders = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var enumerator = new FileEnumerator();
        var files = EnumerateWithProgress(
            enumerator, rootPath, progress, includeHiddenFiles, allowProtectedPaths, excludeCloudPlaceholders, cancellationToken);

        return new ScanResult
        {
            DuplicateGroups = Array.Empty<DuplicateGroup>(),
            TotalFilesScanned = files.Count,
            AllFiles = files,
        };
    }

    private static List<Models.FileRecord> EnumerateWithProgress(
        FileEnumerator enumerator,
        string rootPath,
        IProgress<ScanProgress>? progress,
        bool includeHiddenFiles,
        bool allowProtectedPaths,
        bool excludeCloudPlaceholders,
        CancellationToken cancellationToken)
    {
        // Materialized here (rather than via .ToList() after the fact) so
        // progress can be reported live as files are discovered - the
        // dominant cost of a scan is usually walking the tree and hashing,
        // not what comes after, so this is where live feedback matters most.
        var files = new List<Models.FileRecord>();
        var throttle = Stopwatch.StartNew();
        Models.FileRecord? lastFile = null;
        foreach (var file in enumerator.Enumerate(rootPath, includeHiddenFiles, allowProtectedPaths, excludeCloudPlaceholders, cancellationToken))
        {
            files.Add(file);
            lastFile = file;
            // The first file reports unconditionally so the UI flips from
            // "starting..." to live progress immediately.
            if (files.Count == 1 || throttle.Elapsed >= ProgressInterval)
            {
                throttle.Restart();
                progress?.Report(new ScanProgress(files.Count, file.Path));
            }
        }

        if (lastFile is not null)
        {
            progress?.Report(new ScanProgress(files.Count, lastFile.Path));
        }

        return files;
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
