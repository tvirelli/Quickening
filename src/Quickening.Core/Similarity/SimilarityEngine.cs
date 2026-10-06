using Quickening.Core.Models;

namespace Quickening.Core.Similarity;

/// <summary>
/// Groups images by perceptual (not byte) similarity - a parallel to
/// DuplicateEngine, but for "looks alike" rather than "identical". Pure
/// grouping logic over already-hashed files (see ScanOrchestrator for where
/// PerceptualHashService actually computes/caches each file's hash) so this
/// stays unit-testable without any real image decoding.
/// </summary>
public sealed class SimilarityEngine
{
    // Max pHash Hamming distance (out of 64 bits) for "probably the same subject,
    // worth a second look". ~10 is the commonly-cited pHash threshold; the F2
    // tolerance slider rides on this (stricter = smaller, looser = larger),
    // passed in per scan. This is a heuristic, not a precise similarity metric
    // (see SimilarityGroup.MatchPercent's own doc comment).
    public const int DefaultMaxHammingDistance = 10;
    public const int MinMaxHammingDistance = 2;
    public const int MaxMaxHammingDistance = 18;

    private readonly int _maxHammingDistance;

    public SimilarityEngine(int maxHammingDistance = DefaultMaxHammingDistance)
    {
        _maxHammingDistance = Math.Clamp(maxHammingDistance, MinMaxHammingDistance, MaxMaxHammingDistance);
    }

    // Near-uniform images (a blank page, a solid colour, a transparent canvas
    // with a thin line) downsample to an almost-flat grid, so their dHash has
    // almost no set bits - or almost all - a degenerate value that collides
    // with every other flat image and produced "these look nothing alike but
    // matched 88%" false groups. Exclude them from matching; a real photo's
    // dHash sits well inside this band (typically ~25-40 of 64 bits set).
    // Low-detail images - a logo or product shot on a big uniform background -
    // have few brightness transitions, so their dHash sits near one extreme and
    // collides with every other low-detail image (the "7 dark logos matched at
    // 95%" clusters). A real photo has plenty of transitions and sits in the
    // middle of this band. Widened from the original 6/58 to actually exclude
    // those clusters.
    private const int MinSetBits = 12;
    private const int MaxSetBits = 52;

    private static bool IsDegenerateHash(ulong hash)
    {
        var bits = System.Numerics.BitOperations.PopCount(hash);
        return bits < MinSetBits || bits > MaxSetBits;
    }

    // Max allowed WORST-cell colour difference. A whole-image average is
    // dominated by a large uniform background (a red logo and a grey wheel are
    // both ~90% white), so instead require every region's colour to be close -
    // the differently-coloured subject then vetoes the match. Tunable; the
    // eventual tolerance slider (F2) will ride on this.
    private const int MaxCellColorThreshold = 120;

    /// <summary>
    /// candidateFiles should already be filtered to image-category files
    /// that have a computed PerceptualHash - files with no hash (a decode
    /// failure, or a non-image) are silently ignored rather than throwing,
    /// same defensive posture as the rest of this codebase's per-file
    /// failure handling. exactDuplicatePaths excludes files DuplicateEngine
    /// already grouped as byte-identical (new-screens 4k is explicitly
    /// "not exact copies" - a true duplicate belongs only in the Duplicates
    /// results, never also in "Looks-alike photos").
    /// </summary>
    public IReadOnlyList<SimilarityGroup> FindSimilarGroups(
        IEnumerable<FileRecord> candidateFiles,
        IReadOnlySet<string>? exactDuplicatePaths = null,
        CancellationToken cancellationToken = default)
    {
        var images = candidateFiles
            .Where(f => f.Category == MimeCategory.Image && f.PerceptualHash is not null)
            .Where(f => !IsDegenerateHash(f.PerceptualHash!.Value))
            .Where(f => exactDuplicatePaths is null || !exactDuplicatePaths.Contains(f.Path))
            .ToList();

        // BK-tree over the distinct hash values: each neighbor query visits
        // only subtrees whose distance range can still contain a match,
        // replacing the old all-pairs loop (O(n²) - ~5x10^9 comparisons on a
        // 100k-photo library) with near-linear behavior at this threshold.
        var recordsByHash = new Dictionary<ulong, List<(FileRecord Record, int Index)>>();
        for (var i = 0; i < images.Count; i++)
        {
            var hash = images[i].PerceptualHash!.Value;
            if (!recordsByHash.TryGetValue(hash, out var list))
            {
                recordsByHash[hash] = list = new List<(FileRecord, int)>();
            }

            list.Add((images[i], i));
        }

        var tree = new BkTree();
        foreach (var hash in recordsByHash.Keys)
        {
            tree.Add(hash);
        }

        var visited = new bool[images.Count];
        var groups = new List<SimilarityGroup>();
        var neighbors = new List<(ulong Hash, int Distance)>();

        for (var anchorIndex = 0; anchorIndex < images.Count; anchorIndex++)
        {
            if (visited[anchorIndex])
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            visited[anchorIndex] = true;
            var anchor = images[anchorIndex];
            var members = new List<FileRecord> { anchor };
            var worstDistance = 0;

            neighbors.Clear();
            tree.CollectWithinDistance(anchor.PerceptualHash!.Value, _maxHammingDistance, neighbors);

            // Expand matched hash values to their records, preserving the
            // original enumeration order for stable group membership.
            var matched = new List<(FileRecord Record, int Index, int Distance)>();
            foreach (var (hash, distance) in neighbors)
            {
                foreach (var (record, index) in recordsByHash[hash])
                {
                    if (!visited[index])
                    {
                        matched.Add((record, index, distance));
                    }
                }
            }

            matched.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (var (record, index, distance) in matched)
            {
                // Brightness structure matched (small Hamming); also require the
                // COLOUR grids to be close. Skipping (not visiting) a colour
                // mismatch leaves it free to anchor its own group later.
                if (PerceptualHashService.MaxCellColorDistance(anchor.ColorSignature, record.ColorSignature) > MaxCellColorThreshold)
                {
                    continue;
                }

                visited[index] = true;
                members.Add(record);
                worstDistance = Math.Max(worstDistance, distance);
            }

            if (members.Count < 2)
            {
                continue;
            }

            // WORST member-to-anchor distance, not the closest: with 3+ members
            // the closest pair's score overclaimed the whole group ("97%" on a
            // group whose farthest member was barely inside the threshold).
            // Conservative honesty for a list a user deletes from.
            var matchPercent = (int)Math.Round(100.0 * (64 - worstDistance) / 64.0);
            groups.Add(new SimilarityGroup { Files = members, MatchPercent = matchPercent });
        }

        return groups;
    }

    /// <summary>
    /// Burkhard-Keller tree over 64-bit hashes with Hamming distance as the
    /// metric. Children are keyed by their distance to the parent; a query
    /// within tolerance t only needs to descend into children whose key is
    /// within [d - t, d + t] of the query's distance d to the node (triangle
    /// inequality), which prunes the vast majority of the tree.
    /// </summary>
    private sealed class BkTree
    {
        private Node? _root;

        public void Add(ulong hash)
        {
            if (_root is null)
            {
                _root = new Node(hash);
                return;
            }

            var current = _root;
            while (true)
            {
                var distance = PerceptualHashService.HammingDistance(current.Hash, hash);
                if (distance == 0)
                {
                    return; // Already present (callers de-duplicate values).
                }

                current.Children ??= new Dictionary<int, Node>();
                if (current.Children.TryGetValue(distance, out var child))
                {
                    current = child;
                    continue;
                }

                current.Children[distance] = new Node(hash);
                return;
            }
        }

        public void CollectWithinDistance(ulong query, int tolerance, List<(ulong Hash, int Distance)> results)
        {
            if (_root is null)
            {
                return;
            }

            var stack = new Stack<Node>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                var distance = PerceptualHashService.HammingDistance(node.Hash, query);
                if (distance <= tolerance)
                {
                    results.Add((node.Hash, distance));
                }

                if (node.Children is null)
                {
                    continue;
                }

                var low = distance - tolerance;
                var high = distance + tolerance;
                foreach (var (childDistance, child) in node.Children)
                {
                    if (childDistance >= low && childDistance <= high)
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        private sealed class Node
        {
            public readonly ulong Hash;
            public Dictionary<int, Node>? Children;

            public Node(ulong hash) => Hash = hash;
        }
    }
}
