using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Xunit;

namespace Quickening.Core.Tests.Similarity;

public class VideoSimilarityEngineTests
{
    private static FileRecord File(string path) => new()
    {
        Path = path,
        SizeBytes = 1000,
        LastWriteTimeUtc = DateTime.UnixEpoch,
        Category = MimeCategory.Video,
    };

    private static VideoSimilarityEngine.VideoSignature Sig(string path, params ulong[] frames) =>
        new(File(path), frames);

    [Fact]
    public void GroupsVideosWithNearlyIdenticalFrameHashes()
    {
        // Realistic frame hashes: a real pHash has roughly half its 64 bits
        // set (the engine treats near-empty/near-full hashes as degenerate
        // flat frames and skips them - see MinStructuredBits).
        var a = Sig(@"C:\a.mp4", 0xA5A5_5A5A_F00D_BEEF, 0x1234_5678_9ABC_DEF0, 0x0F0F_F0F0_AAAA_5555, 0xDEAD_BEEF_CAFE_F00D, 0x1111_2222_3333_4444);
        // Each frame differs from a by only a couple of bits -> same clip.
        var b = Sig(@"C:\b.mp4", 0xA5A5_5A5A_F00D_BEEC, 0x1234_5678_9ABC_DEF3, 0x0F0F_F0F0_AAAA_5555, 0xDEAD_BEEF_CAFE_F00E, 0x1111_2222_3333_4447);
        // Every frame far off (half the bits flipped) -> a different video.
        var c = Sig(@"C:\c.mp4",
            0xA5A5_5A5A_F00D_BEEF ^ 0xFFFF_FFFF, 0x1234_5678_9ABC_DEF0 ^ 0xFFFF_FFFF,
            0x0F0F_F0F0_AAAA_5555 ^ 0xFFFF_FFFF, 0xDEAD_BEEF_CAFE_F00D ^ 0xFFFF_FFFF,
            0x1111_2222_3333_4444 ^ 0xFFFF_FFFF);

        var groups = new VideoSimilarityEngine().FindSimilarVideos(new[] { a, b, c });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, f => f.Path == @"C:\a.mp4");
        Assert.Contains(group.Files, f => f.Path == @"C:\b.mp4");
        Assert.DoesNotContain(group.Files, f => f.Path == @"C:\c.mp4");
    }

    [Fact]
    public void ReturnsNoGroups_WhenAllVideosDiffer()
    {
        var a = Sig(@"C:\a.mp4", 0x0000, 0x0000, 0x0000, 0x0000, 0x0000);
        var b = Sig(@"C:\b.mp4", 0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFF);

        var groups = new VideoSimilarityEngine().FindSimilarVideos(new[] { a, b });

        Assert.Empty(groups);
    }
}
