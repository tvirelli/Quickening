using System.Diagnostics;
using Quickening.Core.Audio;
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

    /// <summary>
    /// Housekeeping byproduct (F5): the empty folders and zero-byte files under
    /// the scanned root. Null if it wasn't computed. Kept apart from
    /// DuplicateGroups/AllFiles because these aren't copies or space hogs -
    /// they're clutter to tidy. See <see cref="EmptyItemScanner"/>.
    /// </summary>
    public EmptyItemScanner.Result? EmptyItems { get; init; }

    /// <summary>
    /// Whole folders that are exact copies of each other (F8), derived from the
    /// file-level DuplicateGroups. Always empty after a Large Files scan (which
    /// has no duplicate groups). See <see cref="DuplicateFolderEngine"/>.
    /// </summary>
    public IReadOnlyList<DuplicateFolderEngine.DuplicateFolderGroup> DuplicateFolders { get; init; }
        = Array.Empty<DuplicateFolderEngine.DuplicateFolderGroup>();

    /// <summary>
    /// Likely-blurry photos (F9), blurriest first - images whose sharpness score
    /// fell at or below the threshold. Empty unless the blur pass ran. A review
    /// list only; these are never auto-selected (a smooth-but-sharp photo can
    /// score low too). See <see cref="PerceptualHashService"/>.Sharpness.
    /// </summary>
    public IReadOnlyList<Models.FileRecord> BlurryPhotos { get; init; } = Array.Empty<Models.FileRecord>();

    /// <summary>
    /// Same-song groups (F10): audio files that are the same track encoded
    /// differently (title/artist/duration match, any format/bitrate). Empty
    /// unless the audio pass ran. Distinct from byte-identical DuplicateGroups.
    /// </summary>
    public IReadOnlyList<AudioDuplicateEngine.MusicGroup> MusicGroups { get; init; }
        = Array.Empty<AudioDuplicateEngine.MusicGroup>();

    /// <summary>
    /// The video files this scan enumerated (F11) - populated only when video
    /// similarity was requested, so the App layer can extract + hash frames via
    /// Windows Media (which Core can't reach) and group them itself. Empty
    /// otherwise. Core does no video work; it just hands over the list.
    /// </summary>
    public IReadOnlyList<Models.FileRecord> VideoFiles { get; init; } = Array.Empty<Models.FileRecord>();

    /// <summary>
    /// Near-duplicate video groups (F11). Settable (not init) because Core can't
    /// decode video frames - the App layer extracts + hashes them via Windows
    /// Media, runs VideoSimilarityEngine, and assigns the result back here so it
    /// rides along with the rest of the ScanResult to the results page.
    /// </summary>
    public IReadOnlyList<VideoSimilarityEngine.VideoGroup> VideoGroups { get; set; }
        = Array.Empty<VideoSimilarityEngine.VideoGroup>();
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

    // Default blur cutoff (F9): an image whose Laplacian-variance sharpness is at
    // or below this is flagged as a likely-blurry review candidate. Content-
    // dependent and deliberately conservative; the UI exposes a strictness slider
    // that overrides it.
    public const double DefaultBlurryMaxSharpness = 90;

    private readonly SqliteStore _store;

    public ScanOrchestrator(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>
    /// A single enumeration pass' output: every file found (cloud placeholders
    /// included and flagged), plus the placeholder count/size so a caller can
    /// prompt once and run the rest of the scan WITHOUT walking the tree a second
    /// time. Also remembers the hidden/protected toggles so the process pass can
    /// reuse them (the F5 empty-item walk) without being told again.
    /// </summary>
    public sealed record ScanEnumeration(
        List<Models.FileRecord> Files,
        int CloudPlaceholderCount,
        long CloudPlaceholderBytes,
        bool IncludeHiddenFiles,
        bool AllowProtectedPaths);

    /// <summary>
    /// The single tree walk. Enumerates everything (placeholders included and
    /// flagged) and counts the cloud placeholders, so the caller can show its
    /// "online-only files" prompt from THIS pass rather than a separate walk.
    /// </summary>
    public ScanEnumeration EnumerateForScan(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        bool includeHiddenFiles = false,
        bool allowProtectedPaths = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var enumerator = new FileEnumerator();
        var files = EnumerateWithProgress(
            enumerator, rootPath, progress, includeHiddenFiles, allowProtectedPaths,
            excludeCloudPlaceholders: false, cancellationToken);

        var placeholderCount = 0;
        var placeholderBytes = 0L;
        foreach (var file in files)
        {
            if (file.IsCloudPlaceholder)
            {
                placeholderCount++;
                placeholderBytes += file.SizeBytes;
            }
        }

        return new ScanEnumeration(files, placeholderCount, placeholderBytes, includeHiddenFiles, allowProtectedPaths);
    }

    /// <summary>
    /// The duplicate scan proper, run over an already-enumerated file list (see
    /// EnumerateForScan) so the tree is walked only once even though the caller
    /// prompted about cloud placeholders in between. Placeholders are dropped
    /// here when excludeCloudPlaceholders is set, not via a second enumeration.
    /// </summary>
    public ScanResult ScanEnumerated(
        ScanEnumeration enumeration,
        string rootPath,
        bool excludeCloudPlaceholders = false,
        IProgress<ScanProgress>? progress = null,
        bool paranoidMode = false,
        CancellationToken cancellationToken = default,
        bool computeSimilarity = false,
        int similarityMaxDistance = SimilarityEngine.DefaultMaxHammingDistance,
        bool computeBlur = false,
        double blurryMaxSharpness = DefaultBlurryMaxSharpness,
        bool computeAudioDupes = false,
        bool collectVideoFiles = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var files = excludeCloudPlaceholders
            ? enumeration.Files.Where(f => !f.IsCloudPlaceholder).ToList()
            : enumeration.Files;

        return ProcessScan(
            files, rootPath, enumeration.IncludeHiddenFiles, enumeration.AllowProtectedPaths,
            paranoidMode, progress, cancellationToken, computeSimilarity, similarityMaxDistance,
            computeBlur, blurryMaxSharpness, computeAudioDupes, collectVideoFiles);
    }

    /// <summary>
    /// Convenience wrapper: enumerate then process in one call (still a single
    /// walk). The UI uses EnumerateForScan + ScanEnumerated directly so it can
    /// prompt about cloud placeholders without a second walk; tests and other
    /// callers that don't need that use this.
    /// </summary>
    public ScanResult Scan(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        bool includeHiddenFiles = false,
        bool allowProtectedPaths = false,
        bool paranoidMode = false,
        bool excludeCloudPlaceholders = false,
        CancellationToken cancellationToken = default,
        bool computeSimilarity = false,
        int similarityMaxDistance = SimilarityEngine.DefaultMaxHammingDistance,
        bool computeBlur = false,
        double blurryMaxSharpness = DefaultBlurryMaxSharpness,
        bool computeAudioDupes = false,
        bool collectVideoFiles = false)
    {
        // FileEnumerator only checks cancellation once it yields at least one
        // path, so an already-cancelled token against an empty directory
        // would otherwise silently proceed instead of throwing. Check eagerly
        // here so cancellation is honored regardless of directory contents.
        cancellationToken.ThrowIfCancellationRequested();

        var enumerator = new FileEnumerator();
        var files = EnumerateWithProgress(
            enumerator, rootPath, progress, includeHiddenFiles, allowProtectedPaths, excludeCloudPlaceholders, cancellationToken);

        return ProcessScan(
            files, rootPath, includeHiddenFiles, allowProtectedPaths,
            paranoidMode, progress, cancellationToken, computeSimilarity, similarityMaxDistance,
            computeBlur, blurryMaxSharpness, computeAudioDupes, collectVideoFiles);
    }

    // The post-enumeration work shared by Scan and ScanEnumerated: hashing +
    // duplicate grouping, opt-in similarity, the DB upsert/prune, and the F5/F8
    // byproducts. Takes the already-enumerated (and placeholder-filtered) list.
    private ScanResult ProcessScan(
        List<Models.FileRecord> files,
        string rootPath,
        bool includeHiddenFiles,
        bool allowProtectedPaths,
        bool paranoidMode,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool computeSimilarity,
        int similarityMaxDistance,
        bool computeBlur,
        double blurryMaxSharpness,
        bool computeAudioDupes,
        bool collectVideoFiles)
    {
        var scanStartUtc = DateTime.UtcNow;
        var hashProvider = new CachingHashProvider(new HashProvider(), _store);
        var engine = new DuplicateEngine(hashProvider);

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
        IReadOnlyList<Models.FileRecord> blurryPhotos = Array.Empty<Models.FileRecord>();
        if (computeSimilarity || computeBlur)
        {
            // Image-only. Perceptual hashing + the sharpness score both decode
            // every image (Image.FromFile) - the slowest part. Parallelize the
            // decode (the store cache is thread-safe: a ConcurrentDictionary memo
            // over a lock-serialized connection) and report throttled, monotonic
            // progress, so the "Almost done" phase visibly MOVES instead of
            // sitting silent for minutes on a large photo folder (which read as a
            // hang). requireSharpness forces a recompute of any row hashed before
            // F9 so it gains a sharpness score.
            var imageFiles = files.Where(f => f.Category == MimeCategory.Image).ToList();
            if (imageFiles.Count > 0)
            {
                var processed = 0;
                var lastReported = 0;
                var throttle = Stopwatch.StartNew();
                var reportGate = new object();
                Parallel.ForEach(
                    imageFiles,
                    new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount },
                    file =>
                    {
                        ComputePerceptualHashIfNeeded(file, hashProvider, requireSharpness: computeBlur);
                        var done = Interlocked.Increment(ref processed);
                        if (progress is null)
                        {
                            return;
                        }

                        lock (reportGate)
                        {
                            if (done > lastReported && (done == imageFiles.Count || throttle.Elapsed >= ProgressInterval))
                            {
                                lastReported = done;
                                throttle.Restart();
                                progress.Report(new ScanProgress(done, CurrentPath: "", Phase: ScanPhase.Finalizing, TotalFiles: imageFiles.Count));
                            }
                        }
                    });
            }

            if (computeSimilarity)
            {
                // The exact-duplicate paths let similarity grouping exclude
                // byte-identical files (new-screens 4k: "not exact copies").
                var exactDuplicatePaths = groups
                    .SelectMany(g => g.Files)
                    .Select(f => f.Path)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                similarityGroups = new SimilarityEngine(similarityMaxDistance).FindSimilarGroups(files, exactDuplicatePaths, cancellationToken);
            }

            if (computeBlur)
            {
                // Below the sharpness threshold = likely blurry (F9). Blurriest
                // first. Never auto-selected - purely a review candidate list.
                blurryPhotos = imageFiles
                    .Where(f => f.Sharpness is { } s && s <= blurryMaxSharpness)
                    .OrderBy(f => f.Sharpness)
                    .ToList();
            }
        }

        // Duplicate songs (F10): read each audio file's tags (cheap vs an image
        // decode, but still parallelized + progress-reported), then group same-
        // song encodings. Independent of the image passes above.
        IReadOnlyList<AudioDuplicateEngine.MusicGroup> musicGroups = Array.Empty<AudioDuplicateEngine.MusicGroup>();
        if (computeAudioDupes)
        {
            var audioFiles = files.Where(f => f.Category == MimeCategory.Audio).ToList();
            if (audioFiles.Count > 0)
            {
                var read = new System.Collections.Concurrent.ConcurrentBag<(Models.FileRecord File, AudioInfo Info)>();
                var processed = 0;
                var lastReported = 0;
                var throttle = Stopwatch.StartNew();
                var reportGate = new object();
                Parallel.ForEach(
                    audioFiles,
                    new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount },
                    file =>
                    {
                        if (AudioSignatureService.TryRead(file.Path) is { } info)
                        {
                            read.Add((file, info));
                        }

                        var done = Interlocked.Increment(ref processed);
                        if (progress is null)
                        {
                            return;
                        }

                        lock (reportGate)
                        {
                            if (done > lastReported && (done == audioFiles.Count || throttle.Elapsed >= ProgressInterval))
                            {
                                lastReported = done;
                                throttle.Restart();
                                progress.Report(new ScanProgress(done, CurrentPath: "", Phase: ScanPhase.Finalizing, TotalFiles: audioFiles.Count));
                            }
                        }
                    });

                musicGroups = new AudioDuplicateEngine().FindDuplicateSongs(read.ToList(), cancellationToken);
            }
        }

        // One transaction for the whole scan's rows (per-row autocommit is
        // one fsync per file), then drop rows for files this scan no longer
        // found - stale rows are what let the tray watcher claim "you
        // already have this file" about a copy deleted weeks ago.
        _store.UpsertFiles(files, cancellationToken);
        _store.PruneFilesNotSeenSince(HardBlockRules.NormalizePath(rootPath), scanStartUtc);

        // Housekeeping byproduct (F5) - a cheap metadata walk after the heavy
        // hashing is done, honoring the same hidden/protected toggles as the scan.
        var emptyItems = new EmptyItemScanner().Find(rootPath, includeHiddenFiles, allowProtectedPaths, cancellationToken);

        // Duplicate FOLDERS (F8) - derived purely from the file-level groups just
        // computed, no extra hashing.
        var duplicateFolders = new DuplicateFolderEngine().FindDuplicateFolders(files, groups, cancellationToken);

        return new ScanResult
        {
            DuplicateGroups = groups,
            TotalFilesScanned = files.Count,
            SimilarityGroups = similarityGroups,
            EmptyItems = emptyItems,
            DuplicateFolders = duplicateFolders,
            BlurryPhotos = blurryPhotos,
            MusicGroups = musicGroups,
            VideoFiles = collectVideoFiles
                ? files.Where(f => f.Category == MimeCategory.Video).ToList()
                : Array.Empty<Models.FileRecord>(),
        };
    }

    // Same cached-unless-size/mtime-changed pattern CachingHashProvider uses
    // for PartialHash/FullHash - and it reuses that provider's memoized
    // store lookup, so an image pays at most one point query per scan
    // instead of one here plus one per hash call.
    private static void ComputePerceptualHashIfNeeded(FileRecord file, CachingHashProvider hashProvider, bool requireSharpness)
    {
        var cached = hashProvider.GetCachedRecord(file.Path);
        // Require the colour signature AND the current hash version: a row cached
        // before the colour grid existed (no ColorSignature) or under the old
        // dHash algorithm (PerceptualHashVersion != current) must recompute rather
        // than have its stale value compared as if it were a current pHash. When
        // the blur pass (F9) is on, also require a cached Sharpness - rows hashed
        // before F9 don't have one, so they recompute to fill it.
        if (cached is { PerceptualHash: { } hash, ColorSignature: { } color, PerceptualHashVersion: PerceptualHashService.HashVersion }
            && (!requireSharpness || cached.Sharpness is not null)
            && cached.LastWriteTimeUtc == file.LastWriteTimeUtc && cached.SizeBytes == file.SizeBytes)
        {
            file.PerceptualHash = hash;
            file.ColorSignature = color;
            file.PixelWidth = cached.PixelWidth;
            file.PixelHeight = cached.PixelHeight;
            file.PerceptualHashVersion = cached.PerceptualHashVersion;
            file.Sharpness = cached.Sharpness;
            return;
        }

        var info = PerceptualHashService.TryCompute(file.Path);
        file.PerceptualHash = info?.Hash;
        file.ColorSignature = info?.ColorSignature;
        file.PixelWidth = info?.PixelWidth;
        file.PixelHeight = info?.PixelHeight;
        file.PerceptualHashVersion = info is null ? null : PerceptualHashService.HashVersion;
        file.Sharpness = info?.Sharpness;
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
        var enumeration = EnumerateForScan(rootPath, progress, includeHiddenFiles, allowProtectedPaths, cancellationToken);
        return ScanForLargeFilesEnumerated(enumeration, rootPath, excludeCloudPlaceholders, cancellationToken);
    }

    /// <summary>
    /// The Large Files result built over an already-enumerated file list (see
    /// EnumerateForScan), so the tree is walked once even when the caller prompted
    /// about cloud placeholders in between. No hashing - just a size-based listing.
    /// </summary>
    public ScanResult ScanForLargeFilesEnumerated(
        ScanEnumeration enumeration,
        string rootPath,
        bool excludeCloudPlaceholders = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var files = excludeCloudPlaceholders
            ? enumeration.Files.Where(f => !f.IsCloudPlaceholder).ToList()
            : enumeration.Files;

        // Housekeeping byproduct (F5) - same cheap metadata walk as Scan.
        var emptyItems = new EmptyItemScanner().Find(rootPath, enumeration.IncludeHiddenFiles, enumeration.AllowProtectedPaths, cancellationToken);

        return new ScanResult
        {
            DuplicateGroups = Array.Empty<DuplicateGroup>(),
            TotalFilesScanned = files.Count,
            AllFiles = files,
            EmptyItems = emptyItems,
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
