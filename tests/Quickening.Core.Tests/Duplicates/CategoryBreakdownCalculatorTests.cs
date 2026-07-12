using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.Core.Tests.Duplicates;

public class CategoryBreakdownCalculatorTests
{
    private static DuplicateGroup MakeGroup(params (string Path, long Size, MimeCategory Category)[] files)
    {
        return new DuplicateGroup
        {
            FullHash = new byte[] { 1, 2, 3 },
            Files = files.Select(f => new FileRecord
            {
                Path = f.Path,
                SizeBytes = f.Size,
                LastWriteTimeUtc = DateTime.UtcNow,
                Category = f.Category,
            }).ToList(),
        };
    }

    [Fact]
    public void Calculate_ReturnsEmpty_ForNoGroups()
    {
        var result = CategoryBreakdownCalculator.Calculate(Array.Empty<DuplicateGroup>());

        Assert.Empty(result);
    }

    [Fact]
    public void Calculate_CountsAllButOneFilePerGroup()
    {
        // 3 identical-content files in one group: one "keeper", 2 reclaimable.
        var groups = new[]
        {
            MakeGroup(
                (@"C:\a.jpg", 100L, MimeCategory.Image),
                (@"C:\b.jpg", 100L, MimeCategory.Image),
                (@"C:\c.jpg", 100L, MimeCategory.Image)),
        };

        var result = CategoryBreakdownCalculator.Calculate(groups);

        var image = Assert.Single(result);
        Assert.Equal(MimeCategory.Image, image.Category);
        Assert.Equal(200L, image.ReclaimableBytes);
    }

    [Fact]
    public void Calculate_SumsAcrossMultipleGroupsInTheSameCategory()
    {
        var groups = new[]
        {
            MakeGroup((@"C:\a.jpg", 100L, MimeCategory.Image), (@"C:\b.jpg", 100L, MimeCategory.Image)),
            MakeGroup((@"C:\c.jpg", 50L, MimeCategory.Image), (@"C:\d.jpg", 50L, MimeCategory.Image)),
        };

        var result = CategoryBreakdownCalculator.Calculate(groups);

        var image = Assert.Single(result);
        Assert.Equal(150L, image.ReclaimableBytes); // 100 + 50, one keeper skipped per group
    }

    [Fact]
    public void Calculate_SeparatesDistinctCategories()
    {
        var groups = new[]
        {
            MakeGroup((@"C:\a.jpg", 100L, MimeCategory.Image), (@"C:\b.jpg", 100L, MimeCategory.Image)),
            MakeGroup((@"C:\a.mp3", 40L, MimeCategory.Audio), (@"C:\b.mp3", 40L, MimeCategory.Audio)),
        };

        var result = CategoryBreakdownCalculator.Calculate(groups);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, item => item.Category == MimeCategory.Image && item.ReclaimableBytes == 100L);
        Assert.Contains(result, item => item.Category == MimeCategory.Audio && item.ReclaimableBytes == 40L);
    }

    [Fact]
    public void Calculate_AttributesEachReclaimableFileToItsOwnCategory()
    {
        // Same content (same hash/group) can carry a different Category if
        // a file's extension doesn't match its actual content (e.g. a photo
        // renamed to .bak) - reclaimable bytes must follow each individual
        // file's own Category, not assume the whole group shares one.
        var groups = new[]
        {
            MakeGroup(
                (@"C:\original.jpg", 100L, MimeCategory.Image),
                (@"C:\renamed.bak", 100L, MimeCategory.Other)),
        };

        var result = CategoryBreakdownCalculator.Calculate(groups);

        // Only one file per group is reclaimable (the other is the
        // "keeper"), so exactly one category entry (Other) can appear here
        // - not two, since Image is never reclaimable in this group.
        Assert.Single(result);
        Assert.Contains(result, item => item.Category == MimeCategory.Other && item.ReclaimableBytes == 100L);
        Assert.DoesNotContain(result, item => item.Category == MimeCategory.Image);
    }

    [Fact]
    public void Calculate_OrdersDescendingByReclaimableBytes()
    {
        var groups = new[]
        {
            MakeGroup((@"C:\a.mp3", 10L, MimeCategory.Audio), (@"C:\b.mp3", 10L, MimeCategory.Audio)),
            MakeGroup((@"C:\a.jpg", 500L, MimeCategory.Image), (@"C:\b.jpg", 500L, MimeCategory.Image)),
        };

        var result = CategoryBreakdownCalculator.Calculate(groups);

        Assert.Equal(MimeCategory.Image, result[0].Category);
        Assert.Equal(MimeCategory.Audio, result[1].Category);
    }
}
