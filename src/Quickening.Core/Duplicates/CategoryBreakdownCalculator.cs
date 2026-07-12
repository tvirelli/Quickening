using Quickening.Core.Models;

namespace Quickening.Core.Duplicates;

public sealed record CategoryBreakdownItem(MimeCategory Category, long ReclaimableBytes);

/// <summary>
/// Aggregates reclaimable bytes (every file in a group except one "keeper")
/// per category, from a full scan's duplicate groups. Used by both the
/// Scan-Completed donut chart and the Results sidebar so their numbers
/// can never disagree - both call this same pure function against the
/// same ScanResult.DuplicateGroups.
/// </summary>
public static class CategoryBreakdownCalculator
{
    public static IReadOnlyList<CategoryBreakdownItem> Calculate(IReadOnlyList<DuplicateGroup> groups)
    {
        // Keep the first file per group; attribute every other
        // ("reclaimable") file's bytes to its OWN category, not the
        // group's - two files can share content (and thus a hash/group)
        // while carrying different categories if one has a misleading
        // extension (see the "renamed .bak" test case).
        return groups
            .SelectMany(g => g.Files.Skip(1))
            .GroupBy(f => f.Category)
            .Select(g => new CategoryBreakdownItem(g.Key, g.Sum(f => f.SizeBytes)))
            .OrderByDescending(item => item.ReclaimableBytes)
            .ToList();
    }
}
