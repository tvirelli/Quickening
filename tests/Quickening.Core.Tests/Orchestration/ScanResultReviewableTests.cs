using Quickening.Core.Audio;
using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Quickening.Core.Orchestration;
using Xunit;

namespace Quickening.Core.Tests.Orchestration;

// A scan whose only finds are in the newer sections (songs, videos, blurry,
// same-recording audio) used to route to "Squeaky clean - no two are alike",
// because the check looked at exact duplicates and similar photos only.
public class ScanResultReviewableTests
{
    private static FileRecord File(string name) => new() { Path = $@"C:\x\{name}", SizeBytes = 10, Category = MimeCategory.Audio, LastWriteTimeUtc = DateTime.UtcNow };

    private static ScanResult Empty() => new() { DuplicateGroups = Array.Empty<DuplicateGroup>(), TotalFilesScanned = 5 };

    [Fact]
    public void NothingFound_IsNotReviewable()
    {
        Assert.False(Empty().HasAnythingToReview);
    }

    [Fact]
    public void SoundGroupsAlone_AreReviewable()
    {
        var sig = new AcousticSignature(File("a.wav"), 200, new uint[] { 1 }, AudioFidelity.Create("a.wav", 16, 44100, 0));
        var result = Empty();
        result.SoundGroups = new[] { new SoundGroup(new[] { sig, sig with { File = File("b.wav") } }, 94) };

        Assert.True(result.HasAnythingToReview);
    }

    [Fact]
    public void TagSongsBlurryAndVideosAlone_AreEachReviewable()
    {
        var songs = new ScanResult
        {
            DuplicateGroups = Array.Empty<DuplicateGroup>(), TotalFilesScanned = 5,
            MusicGroups = new[] { new AudioDuplicateEngine.MusicGroup("S", new[] { new AudioDuplicateEngine.SongCopy(File("a.mp3"), 320, 200), new AudioDuplicateEngine.SongCopy(File("b.mp3"), 128, 200) }) },
        };
        var blurry = new ScanResult { DuplicateGroups = Array.Empty<DuplicateGroup>(), TotalFilesScanned = 5, BlurryPhotos = new[] { File("p.jpg") } };
        var videos = Empty();
        videos.VideoGroups = new[] { new Quickening.Core.Similarity.VideoSimilarityEngine.VideoGroup(new[] { File("a.mp4"), File("b.mp4") }, 90) };

        Assert.True(songs.HasAnythingToReview);
        Assert.True(blurry.HasAnythingToReview);
        Assert.True(videos.HasAnythingToReview);
    }
}
