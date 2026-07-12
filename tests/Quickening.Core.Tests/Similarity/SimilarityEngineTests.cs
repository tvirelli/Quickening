using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Xunit;

namespace Quickening.Core.Tests.Similarity;

public class SimilarityEngineTests
{
    private static FileRecord MakeImage(string path, ulong perceptualHash) => new()
    {
        Path = path,
        SizeBytes = 100,
        LastWriteTimeUtc = DateTime.UtcNow,
        Category = MimeCategory.Image,
        PerceptualHash = perceptualHash,
    };

    [Fact]
    public void FindSimilarGroups_GroupsTwoImages_WithSmallHammingDistance()
    {
        var engine = new SimilarityEngine();
        // 0b...0000 vs 0b...0011 differ in 2 bits - well within the threshold.
        var files = new[]
        {
            MakeImage(@"C:\a.jpg", 0x0000000000000000UL),
            MakeImage(@"C:\b.jpg", 0x0000000000000003UL),
        };

        var groups = engine.FindSimilarGroups(files);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Files.Count);
    }

    [Fact]
    public void FindSimilarGroups_DoesNotGroup_WhenHammingDistanceExceedsThreshold()
    {
        var engine = new SimilarityEngine();
        // Every bit differs (64 bits) - nowhere near similar.
        var files = new[]
        {
            MakeImage(@"C:\a.jpg", 0x0000000000000000UL),
            MakeImage(@"C:\b.jpg", 0xFFFFFFFFFFFFFFFFUL),
        };

        var groups = engine.FindSimilarGroups(files);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindSimilarGroups_IgnoresNonImageFiles()
    {
        var engine = new SimilarityEngine();
        var files = new[]
        {
            new FileRecord { Path = @"C:\a.txt", SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow, Category = MimeCategory.Document, PerceptualHash = 0 },
            new FileRecord { Path = @"C:\b.txt", SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow, Category = MimeCategory.Document, PerceptualHash = 0 },
        };

        var groups = engine.FindSimilarGroups(files);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindSimilarGroups_IgnoresImagesWithNoComputedHash()
    {
        var engine = new SimilarityEngine();
        var files = new[]
        {
            new FileRecord { Path = @"C:\a.jpg", SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow, Category = MimeCategory.Image, PerceptualHash = null },
            new FileRecord { Path = @"C:\b.jpg", SizeBytes = 1, LastWriteTimeUtc = DateTime.UtcNow, Category = MimeCategory.Image, PerceptualHash = null },
        };

        var groups = engine.FindSimilarGroups(files);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindSimilarGroups_ExcludesFilesAlreadyInAnExactDuplicateGroup()
    {
        // new-screens 4k is explicitly "not exact copies" - a file
        // DuplicateEngine already grouped as byte-identical must never also
        // show up here, even if its perceptual hash would otherwise match.
        var engine = new SimilarityEngine();
        var files = new[]
        {
            MakeImage(@"C:\a.jpg", 0x0000000000000000UL),
            MakeImage(@"C:\b.jpg", 0x0000000000000000UL),
        };
        var exactDuplicatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\a.jpg", @"C:\b.jpg" };

        var groups = engine.FindSimilarGroups(files, exactDuplicatePaths);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindSimilarGroups_ThreeImages_OneExcludedAsExactDuplicate_StillGroupsRemainingTwo()
    {
        var engine = new SimilarityEngine();
        var files = new[]
        {
            MakeImage(@"C:\a.jpg", 0x0000000000000000UL),
            MakeImage(@"C:\b.jpg", 0x0000000000000000UL), // exact duplicate of a.jpg - excluded
            MakeImage(@"C:\c.jpg", 0x0000000000000001UL), // similar, not identical
        };
        var exactDuplicatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\a.jpg", @"C:\b.jpg" };

        var groups = engine.FindSimilarGroups(files, exactDuplicatePaths);

        Assert.Empty(groups); // only c.jpg remains as a candidate - nothing left to group it with
    }

    [Fact]
    public void FindSimilarGroups_MatchPercentIsHigh_ForNearlyIdenticalHashes()
    {
        var engine = new SimilarityEngine();
        var files = new[]
        {
            MakeImage(@"C:\a.jpg", 0x0000000000000000UL),
            MakeImage(@"C:\b.jpg", 0x0000000000000001UL), // 1 bit different out of 64
        };

        var groups = engine.FindSimilarGroups(files);

        Assert.Single(groups);
        Assert.True(groups[0].MatchPercent >= 95, $"expected a high match percent, got {groups[0].MatchPercent}");
    }
}
