using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Xunit;

namespace Quickening.Core.Tests;

public class SimilarityEngineDegenerateTests
{
    private static byte[] Color()
    {
        var a = new byte[48];
        Array.Fill(a, (byte)100);
        return a;
    }

    private static FileRecord Img(string path, ulong hash) => new()
    {
        Path = path,
        SizeBytes = 100,
        Category = MimeCategory.Image,
        LastWriteTimeUtc = DateTime.UtcNow,
        PerceptualHash = hash,
        ColorSignature = Color(),
    };

    [Fact]
    public void FindSimilarGroups_ExcludesDegenerateFlatImages()
    {
        // Two near-uniform images (all-zero dHash) are 0 Hamming apart but
        // meaningless - they must NOT form a similarity group. This is the
        // "pure white / transparent / thin-line falsely matched" fix.
        var files = new[]
        {
            Img(@"C:\white.png", 0UL),
            Img(@"C:\transparent.png", 0UL),
            Img(@"C:\alsoflat.png", 1UL), // 1 set bit - still degenerate
        };

        var groups = new SimilarityEngine().FindSimilarGroups(files);

        Assert.Empty(groups);
    }

    [Fact]
    public void FindSimilarGroups_StillGroupsGenuinelySimilarImages()
    {
        // A balanced (~32 set bits) hash is a real image; two identical ones
        // must still group - the degenerate filter mustn't suppress real matches.
        const ulong rich = 0xA5A5_5A5A_0F0F_F0F0;
        var files = new[]
        {
            Img(@"C:\a.png", rich),
            Img(@"C:\b.png", rich),
        };

        var groups = new SimilarityEngine().FindSimilarGroups(files);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Files.Count);
    }
}
