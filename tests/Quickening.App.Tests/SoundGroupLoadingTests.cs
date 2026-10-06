using Quickening.App.ViewModels;
using Quickening.Core.Audio;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.App.Tests;

public class SoundGroupLoadingTests
{
    private static AcousticSignature Sig(string path, int bits, int rate = 48000, int kbps = 0, long size = 1000) => new(
        new FileRecord { Path = path, SizeBytes = size, Category = MimeCategory.Audio, LastWriteTimeUtc = DateTime.UtcNow },
        200,
        new uint[] { 1 },
        AudioFidelity.Create(path, bits, rate, kbps));

    [Fact]
    public void LoadSoundGroups_BadgesTheHighestFidelityCopy_AndLabelsFormats()
    {
        var vm = new ResultsViewModel(new FakeRecycleBinService());
        vm.LoadMusicGroups(Array.Empty<AudioDuplicateEngine.MusicGroup>());
        vm.LoadSoundGroups(new[] { new SoundGroup(new[] { Sig(@"C:\Original\Better Man.wav", 16), Sig(@"C:\Mastered\Better Man.wav", 24) }, 94) });

        var group = Assert.Single(vm.MusicGroups);
        Assert.Equal("Better Man", group.SongLabel);
        Assert.Equal("Sounds the same · 94%", group.MatchHint);
        Assert.Equal(@"C:\Mastered\Better Man.wav", group.Files[0].Path);
        Assert.Equal("BEST QUALITY", group.Files[0].HintLabel);
        Assert.Null(group.Files[1].HintLabel);
        Assert.Equal("WAV · 24-bit · 48 kHz", group.Files[0].FormatLabel);
        Assert.All(group.Files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void LoadSoundGroups_TitlesTheGroupWithTheShortestFileName()
    {
        // Mastering services prefix their output ("Mixea_MediumNeutral_hd_Reset");
        // the original's plain name is the better title even though the master
        // is the copy listed first.
        var vm = new ResultsViewModel(new FakeRecycleBinService());
        vm.LoadMusicGroups(Array.Empty<AudioDuplicateEngine.MusicGroup>());
        vm.LoadSoundGroups(new[] { new SoundGroup(new[] { Sig(@"C:\Reset\Original\Reset.wav", 16), Sig(@"C:\Reset\Mastered\Mixea_MediumNeutral_hd_Reset.wav", 24) }, 94) });

        var group = Assert.Single(vm.MusicGroups);
        Assert.Equal("Reset", group.SongLabel);
        Assert.EndsWith("Mixea_MediumNeutral_hd_Reset.wav", group.Files[0].Path);
    }

    [Fact]
    public void LoadSoundGroups_NoBadge_WhenFidelityTies()
    {
        var vm = new ResultsViewModel(new FakeRecycleBinService());
        vm.LoadMusicGroups(Array.Empty<AudioDuplicateEngine.MusicGroup>());
        vm.LoadSoundGroups(new[] { new SoundGroup(new[] { Sig(@"C:\a\x.mp3", 0, 44100, 320), Sig(@"C:\b\x.mp3", 0, 44100, 320) }, 97) });

        Assert.All(vm.MusicGroups[0].Files, f => Assert.Null(f.HintLabel));
    }

    [Fact]
    public void LoadSoundGroups_SkipsAGroupAlreadyCoveredByTags()
    {
        var a = Sig(@"C:\m\a.mp3", 0, 44100, 320);
        var b = Sig(@"C:\m\b.mp3", 0, 44100, 128);
        var vm = new ResultsViewModel(new FakeRecycleBinService());
        vm.LoadMusicGroups(new[]
        {
            new AudioDuplicateEngine.MusicGroup("Song — Artist", new[]
            {
                new AudioDuplicateEngine.SongCopy(a.File, 320, 200),
                new AudioDuplicateEngine.SongCopy(b.File, 128, 200),
            }),
        });

        vm.LoadSoundGroups(new[] { new SoundGroup(new[] { a, b }, 95) });

        var only = Assert.Single(vm.MusicGroups);
        Assert.Equal("Tags match", only.MatchHint);
    }
}
