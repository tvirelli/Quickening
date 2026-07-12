using Quickening.Core.Models;

namespace Quickening.Core.Similarity;

/// <summary>
/// A cluster of images that look alike (small dHash Hamming distance) but
/// are NOT byte-identical - unlike DuplicateGroup, there's no single
/// canonical "this is the extra copy" answer here (new-screens 4k:
/// "never auto-selected"), just a rough match confidence for the group.
/// </summary>
public sealed class SimilarityGroup
{
    public required List<FileRecord> Files { get; init; }

    /// <summary>
    /// 0-100, how close the closest anchor-to-member hash pair in this group
    /// is (100 - Hamming distance / 64 * 100). Only pairs involving the
    /// group's anchor are measured - two non-anchor members may be closer to
    /// each other. A rough "how alike" confidence, not a precise metric.
    /// </summary>
    public required int MatchPercent { get; init; }
}
