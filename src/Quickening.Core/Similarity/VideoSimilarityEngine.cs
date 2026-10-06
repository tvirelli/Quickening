using System.Numerics;
using Quickening.Core.Models;

namespace Quickening.Core.Similarity;

/// <summary>
/// Groups near-duplicate videos (F11) by comparing per-video frame-hash
/// signatures - each signature is the pHash of a fixed set of frames sampled at
/// the same relative positions (e.g. 10/30/50/70/90% of the runtime). Two videos
/// are similar when their frames match position-by-position within a small
/// average Hamming distance, so re-encodes / resizes / different bitrates of the
/// same clip group together. Pure grouping over already-computed signatures (the
/// App layer does the actual frame extraction via Windows Media), so this stays
/// unit-testable without decoding any video.
/// </summary>
public sealed class VideoSimilarityEngine
{
    /// <summary>A video plus the pHashes of its sampled frames (same order/count
    /// for every video, so position i compares to position i).</summary>
    public sealed record VideoSignature(FileRecord File, ulong[] FrameHashes);

    public sealed record VideoGroup(IReadOnlyList<FileRecord> Files, int MatchPercent);

    // Max average per-frame Hamming distance (of 64) for "same clip". Frame pHash
    // is the same DCT hash used for photos, where ~10 is the similar threshold;
    // averaging over several frames makes this robust to a single odd frame.
    private const int MaxAverageHamming = 10;

    // A frame hash with a popcount outside this band carries no structure - a
    // flat/black frame's DCT is all zeros, so its pHash is 0, and ANY two flat
    // frames "match" perfectly. The image engine excludes such degenerate hashes
    // outright (SimilarityEngine's popcount band); frames get the same guard so
    // two DIFFERENT videos that are mostly black/fades (concert footage, screen
    // recordings) can't group on their empty frames.
    private const int MinStructuredBits = 10;
    private const int MaxStructuredBits = 54;

    // At least this many structured frame pairs must survive the guard for a
    // comparison to mean anything - one usable frame out of five is a
    // coincidence, not a clip match.
    private const int MinComparableFrames = 3;

    public IReadOnlyList<VideoGroup> FindSimilarVideos(
        IReadOnlyList<VideoSignature> signatures,
        CancellationToken cancellationToken = default)
    {
        // One-hop "star" grouping around an anchor, exactly like the image
        // engine - NOT transitive union-find, which chained A~B, B~C into one
        // group even when A and C were far apart (each member here is within
        // the threshold of the group's anchor, bounding any member pair to 2x).
        var n = signatures.Count;
        var visited = new bool[n];
        var groups = new List<VideoGroup>();

        for (var i = 0; i < n; i++)
        {
            if (visited[i])
            {
                continue;
            }

            List<int>? members = null;
            var bestDistance = int.MaxValue;

            for (var j = i + 1; j < n; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (visited[j])
                {
                    continue;
                }

                var distance = AverageDistance(signatures[i].FrameHashes, signatures[j].FrameHashes);
                if (distance <= MaxAverageHamming)
                {
                    (members ??= new List<int> { i }).Add(j);
                    visited[j] = true;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                    }
                }
            }

            if (members is null)
            {
                continue;
            }

            visited[i] = true;
            var files = members.Select(m => signatures[m].File).ToList();
            var matchPercent = (int)Math.Round(100.0 * (64 - bestDistance) / 64.0);
            groups.Add(new VideoGroup(files, matchPercent));
        }

        return groups.OrderByDescending(g => g.Files.Count).ToList();
    }

    // Average Hamming distance over the STRUCTURED frames both videos share
    // (position-by-position, skipping degenerate flat frames on either side).
    // int.MaxValue when fewer than MinComparableFrames survive - "cannot be
    // compared" rather than "matches".
    private static int AverageDistance(ulong[] a, ulong[] b)
    {
        var count = Math.Min(a.Length, b.Length);
        long sum = 0;
        var comparable = 0;
        for (var k = 0; k < count; k++)
        {
            if (!IsStructured(a[k]) || !IsStructured(b[k]))
            {
                continue;
            }

            sum += PerceptualHashService.HammingDistance(a[k], b[k]);
            comparable++;
        }

        if (comparable < MinComparableFrames)
        {
            return int.MaxValue;
        }

        return (int)(sum / comparable);
    }

    private static bool IsStructured(ulong hash)
    {
        var bits = BitOperations.PopCount(hash);
        return bits is >= MinStructuredBits and <= MaxStructuredBits;
    }
}
