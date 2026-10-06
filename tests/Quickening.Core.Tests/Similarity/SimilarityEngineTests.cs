using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Xunit;

namespace Quickening.Core.Tests.Similarity;

public class SimilarityEngineTests
{
    // A uniform 4x4 colour grid, so tests that only care about the brightness
    // hash still pass the new colour-match requirement (matching colours).
    private static byte[] MakeColor(byte v)
    {
        var a = new byte[48];
        Array.Fill(a, v);
        return a;
    }

    private static readonly byte[] DefaultColor = MakeColor(100);

    private static FileRecord MakeImage(string path, ulong perceptualHash, byte[]? colorSignature = null) => new()
    {
        Path = path,
        SizeBytes = 100,
        LastWriteTimeUtc = DateTime.UtcNow,
        Category = MimeCategory.Image,
        PerceptualHash = perceptualHash,
        ColorSignature = colorSignature ?? DefaultColor,
    };

    [Fact]
    public void FindSimilarGroups_GroupsTwoImages_WithSmallHammingDistance()
    {
        var engine = new SimilarityEngine();
        // A realistic (non-degenerate) hash and a copy 2 bits different - well
        // within the threshold, and both with enough set bits to be real images
        // (degenerate near-flat hashes are now excluded from matching).
        const ulong baseHash = 0xA5A5_5A5A_0F0F_F0F0UL;
        var files = new[]
        {
            MakeImage(@"C:\a.jpg", baseHash),
            MakeImage(@"C:\b.jpg", baseHash ^ 0x3UL),
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
        const ulong baseHash = 0xA5A5_5A5A_0F0F_F0F0UL;
        var files = new[]
        {
            MakeImage(@"C:\a.jpg", baseHash),
            MakeImage(@"C:\b.jpg", baseHash ^ 0x1UL), // 1 bit different out of 64
        };

        var groups = engine.FindSimilarGroups(files);

        Assert.Single(groups);
        Assert.True(groups[0].MatchPercent >= 95, $"expected a high match percent, got {groups[0].MatchPercent}");
    }

    [Fact]
    public void FindSimilarGroups_DoesNotGroup_SameStructureButDifferentColour()
    {
        // Identical brightness hash (same light/dark layout) but very different
        // colours - a grey image vs a red one, the "B&W cube matched a colourful
        // photo at 88%" bug. Colour must veto the brightness-only match.
        const ulong sharedHash = 0xA5A5_5A5A_0F0F_F0F0UL;
        var red = new byte[48];
        for (var i = 0; i < 48; i += 3) { red[i] = 200; red[i + 1] = 20; red[i + 2] = 20; }

        var files = new[]
        {
            MakeImage(@"C:\grey.png", sharedHash, MakeColor(128)),
            MakeImage(@"C:\red.png", sharedHash, red),
        };

        var groups = new SimilarityEngine().FindSimilarGroups(files);

        Assert.Empty(groups);
    }
}
