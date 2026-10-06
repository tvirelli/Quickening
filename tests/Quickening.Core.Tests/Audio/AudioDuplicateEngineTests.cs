using Quickening.Core.Audio;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.Core.Tests.Audio;

public class AudioDuplicateEngineTests
{
    private static FileRecord File(string path, long size = 1000) => new()
    {
        Path = path,
        SizeBytes = size,
        LastWriteTimeUtc = DateTime.UnixEpoch,
        Category = MimeCategory.Audio,
    };

    [Fact]
    public void GroupsSameSongAcrossFormatsAndBitrates()
    {
        var mp3 = (File(@"C:\music\song.mp3"), new AudioInfo("Song", "Artist", "Album", 200, 128));
        var flac = (File(@"C:\music\song.flac"), new AudioInfo("song", "  Artist ", "Album", 201, 1000));

        var groups = new AudioDuplicateEngine().FindDuplicateSongs(new[] { mp3, flac });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Copies.Count);
        // Highest bitrate first (the "best" copy).
        Assert.Equal(@"C:\music\song.flac", group.Copies[0].File.Path);
        Assert.Equal("Artist — Song", group.SongLabel);
    }

    [Fact]
    public void DoesNotGroupDifferentSongs()
    {
        var a = (File(@"C:\a.mp3"), new AudioInfo("First", "Artist", "", 180, 192));
        var b = (File(@"C:\b.mp3"), new AudioInfo("Second", "Artist", "", 180, 192));

        var groups = new AudioDuplicateEngine().FindDuplicateSongs(new[] { a, b });

        Assert.Empty(groups);
    }

    [Fact]
    public void DoesNotGroupDifferentLengthVersions()
    {
        // Same title/artist but a 3-min single vs a 6-min extended mix -> separate.
        var single = (File(@"C:\single.mp3"), new AudioInfo("Track", "Artist", "", 180, 256));
        var extended = (File(@"C:\extended.mp3"), new AudioInfo("Track", "Artist", "", 360, 256));

        var groups = new AudioDuplicateEngine().FindDuplicateSongs(new[] { single, extended });

        Assert.Empty(groups);
    }
}
