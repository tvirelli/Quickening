using System.Buffers;
using Quickening.Core.Hashing;
using Quickening.Core.Models;

namespace Quickening.Core.Duplicates;

/// <summary>
/// Reported by <see cref="DuplicateEngine.FindDuplicates"/> as the hashing
/// funnel proceeds. FilesHashed/TotalFiles cover every file passed in; a
/// file counts as "hashed" only once ALL hashing it needs is finished
/// (unique-size files immediately; partial-hash-only files when their size
/// group resolves; full-hash candidates after the full hash completes) - so
/// the counter is monotonic and never reaches TotalFiles while expensive
/// full-hash work is still pending. DuplicateGroupsFoundSoFar/
/// ReclaimableBytesSoFar are running totals as of the moment each group is
/// confirmed, driving a live "found so far" indicator while hashing is
/// still in progress.
/// </summary>
public sealed record HashingProgress(
    int FilesHashed,
    int TotalFiles,
    int DuplicateGroupsFoundSoFar,
    long ReclaimableBytesSoFar,
    // Path of the file most recently hashed, so the UI's live path chip can
    // show what the compare phase is chewing on. Null/empty when no single
    // file is meaningful (e.g. the initial 0-progress report).
    string? CurrentPath = null);

public sealed class DuplicateEngine
{
    private const int CompareBufferSize = 81920;

    private readonly IHashProvider _hashProvider;

    public DuplicateEngine(IHashProvider hashProvider)
    {
        _hashProvider = hashProvider;
    }

    /// <summary>
    /// Runs the four-stage duplicate-detection funnel (group by size, then partial
    /// hash, then full hash) over <paramref name="files"/>, yielding a
    /// <see cref="DuplicateGroup"/> per set of files proven to have identical content.
    /// This mutates the caller-owned <see cref="FileRecord"/> instances in
    /// <paramref name="files"/> in place - setting <see cref="FileRecord.PartialHash"/>
    /// and <see cref="FileRecord.FullHash"/> as a side effect of enumeration - so it is
    /// not safe to run concurrently over overlapping <see cref="FileRecord"/> sets.
    /// Argument validation happens eagerly at call time, but the funnel itself is a
    /// deferred-execution sequence: nothing runs until the result is enumerated.
    /// </summary>
    /// <param name="paranoidMode">
    /// Settings' "Paranoid mode" toggle - off by default. When true, every
    /// full-hash match also gets a byte-for-byte comparison before being
    /// confirmed, purely as extra reassurance (BLAKE3 full-hash collisions
    /// are already astronomically unlikely). A file that fails this check is
    /// excluded from its group rather than aborting the scan, same as a
    /// hashing IOException below.
    /// </param>
    public IEnumerable<DuplicateGroup> FindDuplicates(
        IEnumerable<FileRecord> files,
        IProgress<HashingProgress>? progress = null,
        bool paranoidMode = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        return FindDuplicatesCore(files, progress, paranoidMode, cancellationToken);
    }

    private IEnumerable<DuplicateGroup> FindDuplicatesCore(
        IEnumerable<FileRecord> files,
        IProgress<HashingProgress>? progress,
        bool paranoidMode,
        CancellationToken cancellationToken)
    {
        // Materialized up front (rather than left as a lazy GroupBy) so the
        // total file count is known before the first progress report -
        // GroupBy has to see every file before it can yield any group at
        // all, so this doesn't change when work actually starts.
        var allFiles = files as IReadOnlyCollection<FileRecord> ?? files.ToList();
        var totalFiles = allFiles.Count;
        var filesHashed = 0;
        var groupsFoundSoFar = 0;
        long reclaimableBytesSoFar = 0;
        var currentPath = "";

        void ReportProgress() =>
            progress?.Report(new HashingProgress(filesHashed, totalFiles, groupsFoundSoFar, reclaimableBytesSoFar, currentPath));

        ReportProgress();

        // Stage 1: group by size - different size can never be a duplicate.
        var bySize = allFiles.GroupBy(f => f.SizeBytes).ToList();

        foreach (var sizeGroup in bySize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sizeGroupFiles = sizeGroup.ToList();
            if (sizeGroup.Key == 0 || sizeGroupFiles.Count <= 1)
            {
                // A unique size can never be a duplicate - no hashing needed.
                // Empty files are trivially "identical" to every other empty
                // file regardless of actual type (an empty .zip and an empty
                // .htm have the same zero bytes) - grouping them as
                // duplicates has no reclaimable space behind it and is
                // actively misleading (a batch of unrelated failed/
                // interrupted downloads reading as "N identical copies"), so
                // they're excluded from comparison entirely rather than
                // hashed. Either way, this file needs no hashing, so it's
                // already "done" for progress purposes.
                filesHashed += sizeGroupFiles.Count;
                ReportProgress();
                continue;
            }

            // Stage 2: partial hash - cheap pre-filter within this size group.
            foreach (var record in sizeGroupFiles)
            {
                currentPath = record.Path;
                try
                {
                    record.PartialHash = _hashProvider.ComputePartialHash(record.Path, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // File vanished/locked between enumeration and hashing (TOCTOU) -
                    // exclude it rather than aborting the whole scan.
                    record.PartialHash = null;
                }
            }

            var byPartialHash = sizeGroupFiles
                .Where(f => f.PartialHash is not null)
                .GroupBy(f => f.PartialHash!, HashBytesComparer.Instance)
                .Where(g => g.Count() > 1)
                .Select(g => g.ToList())
                .ToList();

            // Files whose funnel ends here (unreadable, or a partial hash no
            // other file shares) are done; full-hash candidates count only
            // when their expensive stage finishes, so "N of M" never lies at
            // 100% while multi-gigabyte full reads are still running.
            var advancingCount = byPartialHash.Sum(g => g.Count);
            filesHashed += sizeGroupFiles.Count - advancingCount;
            ReportProgress();

            foreach (var partialGroup in byPartialHash)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Stage 3: full hash - the actual proof of identical content.
                foreach (var record in partialGroup)
                {
                    currentPath = record.Path;
                    try
                    {
                        record.FullHash = _hashProvider.ComputeFullHash(record.Path, cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        record.FullHash = null;
                    }

                    filesHashed++;
                    ReportProgress();
                }

                var byFullHash = partialGroup
                    .Where(f => f.FullHash is not null)
                    .GroupBy(f => f.FullHash!, HashBytesComparer.Instance)
                    .Where(g => g.Count() > 1);

                foreach (var fullGroup in byFullHash)
                {
                    var groupFiles = fullGroup.ToList();

                    if (paranoidMode)
                    {
                        var verified = VerifyByteIdentical(groupFiles, cancellationToken);
                        if (verified.Count < 2)
                        {
                            // Extremely unlikely (a confirmed BLAKE3 match failing
                            // byte comparison), but if it happens there's no
                            // honest duplicate left to report for this group.
                            continue;
                        }

                        groupFiles = verified;
                    }

                    groupsFoundSoFar++;
                    reclaimableBytesSoFar += (long)(groupFiles.Count - 1) * groupFiles[0].SizeBytes;
                    ReportProgress();

                    yield return new DuplicateGroup
                    {
                        FullHash = groupFiles[0].FullHash!,
                        Files = groupFiles,
                    };
                }
            }
        }
    }

    /// <summary>
    /// Byte-compares every group member against the first in a single pass:
    /// all streams open at once, the reference chunk read once and compared
    /// against each survivor's chunk. The old pairwise loop re-read the
    /// reference file in full once per other member - for a group of eleven
    /// 4 GB copies that was 40 GB of redundant reference reads.
    /// </summary>
    private static List<FileRecord> VerifyByteIdentical(List<FileRecord> groupFiles, CancellationToken cancellationToken)
    {
        var reference = groupFiles[0];
        var verified = new List<FileRecord> { reference };

        FileStream? referenceStream = null;
        var candidates = new List<(FileRecord Record, FileStream Stream)>();
        var referenceBuffer = ArrayPool<byte>.Shared.Rent(CompareBufferSize);
        var candidateBuffer = ArrayPool<byte>.Shared.Rent(CompareBufferSize);
        try
        {
            try
            {
                referenceStream = File.OpenRead(reference.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Reference vanished/locked since its full hash ran (TOCTOU):
                // no byte-verification is possible. Paranoid mode promises
                // byte-verified groups, so an unverifiable group is dropped
                // (matching the previous behavior) rather than passed through
                // on hash evidence alone.
                return verified;
            }

            foreach (var record in groupFiles.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = File.OpenRead(record.Path);

                    // Length equality first (the old pairwise implementation's
                    // guard): a candidate that grew since its full hash ran
                    // (log file, active download) shares its prefix with the
                    // reference, and a prefix-only compare would confirm it
                    // as "byte-identical" right up to reference EOF.
                    if (stream.Length != referenceStream.Length)
                    {
                        stream.Dispose();
                        continue;
                    }

                    candidates.Add((record, stream));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Vanished/locked - exclude from the group, same as a
                    // hashing failure.
                }
            }

            while (candidates.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead;
                try
                {
                    bytesRead = referenceStream.Read(referenceBuffer, 0, CompareBufferSize);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Reference became unreadable mid-compare (antivirus lock,
                    // disk error): same contract as the open failure above -
                    // the group can't be byte-verified, so it's dropped
                    // instead of the exception aborting the caller's whole
                    // scan.
                    return new List<FileRecord> { reference };
                }

                if (bytesRead == 0)
                {
                    break;
                }

                for (var i = candidates.Count - 1; i >= 0; i--)
                {
                    var (record, stream) = candidates[i];
                    var matches = false;
                    try
                    {
                        var totalRead = 0;
                        while (totalRead < bytesRead)
                        {
                            var read = stream.Read(candidateBuffer, totalRead, bytesRead - totalRead);
                            if (read == 0)
                            {
                                break;
                            }

                            totalRead += read;
                        }

                        matches = totalRead == bytesRead &&
                                  referenceBuffer.AsSpan(0, bytesRead).SequenceEqual(candidateBuffer.AsSpan(0, bytesRead));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }

                    if (!matches)
                    {
                        stream.Dispose();
                        candidates.RemoveAt(i);
                    }
                }
            }

            verified.AddRange(candidates.Select(c => c.Record));
            return verified;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(referenceBuffer);
            ArrayPool<byte>.Shared.Return(candidateBuffer);
            referenceStream?.Dispose();
            foreach (var (_, stream) in candidates)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Content equality for hash byte arrays, so GroupBy doesn't need a hex
    /// string allocated per file purely as a grouping key.
    /// </summary>
    private sealed class HashBytesComparer : IEqualityComparer<byte[]>
    {
        public static readonly HashBytesComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(byte[] obj) =>
            // Hashes are uniformly random; the first 4 bytes are as good a
            // bucket key as any.
            obj.Length >= 4 ? BitConverter.ToInt32(obj, 0) : obj.Length;
    }
}
