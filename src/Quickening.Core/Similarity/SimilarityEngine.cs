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
    // A dHash Hamming distance of 0-10 (out of 64 bits) is the commonly-cited
    // threshold for "probably the same subject, worth a second look" without
    // being so loose that unrelated photos collide - this is a heuristic,
    // not a precise similarity metric (see SimilarityGroup.MatchPercent's
    // own doc comment).
    private const int MaxHammingDistance = 10;

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
            var closestDistance = int.MaxValue;

            neighbors.Clear();
            tree.CollectWithinDistance(anchor.PerceptualHash!.Value, MaxHammingDistance, neighbors);

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
                visited[index] = true;
                members.Add(record);
                closestDistance = Math.Min(closestDistance, distance);
            }

            if (members.Count < 2)
            {
                continue;
            }

            var matchPercent = (int)Math.Round(100.0 * (64 - closestDistance) / 64.0);
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
