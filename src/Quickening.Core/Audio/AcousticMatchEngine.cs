using System.Numerics;
using Quickening.Core.Models;

namespace Quickening.Core.Audio;

/// <summary>An audio file's fingerprint plus what's needed to compare and rank it.</summary>
public sealed record AcousticSignature(FileRecord File, double DurationSeconds, uint[] Frames, AudioFidelity Fidelity);

/// <summary>Two or more files that are the same recording. MatchPercent is the weakest member's similarity to the anchor.</summary>
public sealed record SoundGroup(IReadOnlyList<AcousticSignature> Members, int MatchPercent);

/// <summary>
/// Deep audio matching: groups files that are the same recording - mastered vs
/// original, other format or bitrate, louder/quieter, small trims - by comparing
/// acoustic fingerprints (<see cref="AcousticFingerprinter"/>). Pure grouping
/// over precomputed signatures; the App layer decodes. Deliberately strict:
/// only durations within 5 s are compared (different edits are different
/// tracks), and a pair must agree on 75%+ of fingerprint bits.
/// </summary>
public sealed class AcousticMatchEngine
{
    public const double MaxBitErrorRate = 0.25;
    private const double MaxDurationDifferenceSeconds = 5;
    private const double MinDurationSeconds = 15;
    private const double MinAudibleSeconds = 10;
    private const int MaxOffsetFrames = 108;
    private const double MinOverlap = 0.7;
    private const int IndexThreshold = 200;
    private const int MinSharedFrames = 3;

    public static bool IsEligible(AcousticSignature signature) =>
        signature.DurationSeconds >= MinDurationSeconds
        && signature.Frames.Count(f => f != AcousticFingerprinter.Quiet) >= MinAudibleSeconds * AcousticFingerprinter.FramesPerSecond;

    /// <summary>
    /// Lowest bit error rate over time offsets of +/-5 s (0 = identical, ~0.5 =
    /// unrelated), counting only frames audible in both; null when no offset
    /// overlaps at least 70% of the shorter fingerprint's audible frames.
    /// </summary>
    public static double? BitErrorRate(uint[] a, uint[] b)
    {
        var audibleA = a.Count(f => f != AcousticFingerprinter.Quiet);
        var audibleB = b.Count(f => f != AcousticFingerprinter.Quiet);
        var required = MinOverlap * Math.Min(audibleA, audibleB);
        if (required <= 0)
        {
            return null;
        }

        double? best = null;
        for (var offset = -MaxOffsetFrames; offset <= MaxOffsetFrames; offset++)
        {
            long errors = 0;
            var compared = 0;
            for (var i = Math.Max(0, -offset); i < a.Length && i + offset < b.Length; i++)
            {
                var x = a[i];
                var y = b[i + offset];
                if (x == AcousticFingerprinter.Quiet || y == AcousticFingerprinter.Quiet)
                {
                    continue;
                }

                errors += BitOperations.PopCount(x ^ y);
                compared++;
            }

            if (compared < required)
            {
                continue;
            }

            var rate = errors / (32.0 * compared);
            if (best is null || rate < best)
            {
                best = rate;
            }
        }

        return best;
    }

    public IReadOnlyList<SoundGroup> FindSameRecordings(
        IReadOnlyList<AcousticSignature> signatures,
        CancellationToken cancellationToken = default)
    {
        var eligible = signatures.Where(IsEligible).OrderBy(s => s.DurationSeconds).ToList();
        var n = eligible.Count;
        var useIndex = n >= IndexThreshold;
        var frameSets = useIndex
            ? eligible.Select(s => s.Frames.Where(f => f != AcousticFingerprinter.Quiet).ToHashSet()).ToList()
            : null;
        var index = useIndex ? BuildIndex(frameSets!) : null;

        // BER for every verified matching pair, keyed (i, j) with i < j.
        var matches = new Dictionary<(int, int), double>();
        for (var i = 0; i < n; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shared = useIndex ? SharedFrameCounts(i, frameSets![i], index!) : null;
            for (var j = i + 1; j < n && eligible[j].DurationSeconds - eligible[i].DurationSeconds <= MaxDurationDifferenceSeconds; j++)
            {
                if (shared is not null && (!shared.TryGetValue(j, out var count) || count < MinSharedFrames))
                {
                    continue;
                }

                if (BitErrorRate(eligible[i].Frames, eligible[j].Frames) is { } rate && rate <= MaxBitErrorRate)
                {
                    matches[(i, j)] = rate;
                }
            }
        }

        // One-hop star grouping around an anchor (same rule as images/video):
        // every member is within the threshold of the anchor - never chained.
        var visited = new bool[n];
        var groups = new List<SoundGroup>();
        for (var anchor = 0; anchor < n; anchor++)
        {
            if (visited[anchor])
            {
                continue;
            }

            var members = new List<AcousticSignature> { eligible[anchor] };
            var worst = 0.0;
            for (var other = 0; other < n; other++)
            {
                if (other == anchor || visited[other])
                {
                    continue;
                }

                var key = anchor < other ? (anchor, other) : (other, anchor);
                if (matches.TryGetValue(key, out var rate))
                {
                    members.Add(eligible[other]);
                    visited[other] = true;
                    worst = Math.Max(worst, rate);
                }
            }

            if (members.Count > 1)
            {
                visited[anchor] = true;
                groups.Add(new SoundGroup(members, (int)Math.Clamp(Math.Round((1 - worst / 0.5) * 100), 0, 100)));
            }
        }

        return groups;
    }

    // value -> indices of the signatures containing it. Same-recording copies
    // share many exact 32-bit frame values; unrelated songs share almost none,
    // so this cheaply finds candidates in large libraries.
    private static Dictionary<uint, List<int>> BuildIndex(List<HashSet<uint>> frameSets)
    {
        var index = new Dictionary<uint, List<int>>();
        for (var s = 0; s < frameSets.Count; s++)
        {
            foreach (var value in frameSets[s])
            {
                if (!index.TryGetValue(value, out var list))
                {
                    index[value] = list = new List<int>();
                }

                list.Add(s);
            }
        }

        return index;
    }

    private static Dictionary<int, int> SharedFrameCounts(int self, HashSet<uint> frames, Dictionary<uint, List<int>> index)
    {
        var counts = new Dictionary<int, int>();
        foreach (var value in frames)
        {
            foreach (var other in index[value])
            {
                if (other > self)
                {
                    counts[other] = counts.TryGetValue(other, out var c) ? c + 1 : 1;
                }
            }
        }

        return counts;
    }
}
