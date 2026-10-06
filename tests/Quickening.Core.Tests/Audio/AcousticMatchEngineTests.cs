using Quickening.Core.Audio;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.Core.Tests.Audio;

public class AcousticMatchEngineTests
{
    private static AcousticSignature Sig(string name, short[] pcm, double? duration = null) => new(
        new FileRecord { Path = $@"C:\music\{name}", SizeBytes = pcm.Length * 2L, Category = MimeCategory.Audio, LastWriteTimeUtc = DateTime.UtcNow },
        duration ?? 200,
        AcousticFingerprinter.Compute(pcm),
        AudioFidelity.Create(name, 16, 44100, 1411));

    private static short[] Music(int seed, double seconds = 40, double gain = 1) => AcousticFingerprinterTests.Music(seed, seconds, gain);

    [Fact]
    public void SameRecording_LouderAndShifted_GroupsTogether()
    {
        var song = Music(7);
        var louderShifted = Music(7, gain: 0.6).Skip(AcousticFingerprinter.SampleRate).ToArray(); // 1 s trimmed

        var groups = new AcousticMatchEngine().FindSameRecordings(new[] { Sig("a.wav", song), Sig("b.wav", louderShifted) });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Members.Count);
        Assert.InRange(group.MatchPercent, 60, 100); // half-hop misalignment of an abrupt synthetic tune: BER ~0.13
    }

    [Fact]
    public void DifferentSongs_OfEqualLength_DoNotGroup()
    {
        var groups = new AcousticMatchEngine().FindSameRecordings(new[] { Sig("a.wav", Music(1)), Sig("b.wav", Music(2)) });

        Assert.Empty(groups);
    }

    [Fact]
    public void DurationsMoreThanFiveSecondsApart_AreNeverCompared()
    {
        var song = Music(3);

        var groups = new AcousticMatchEngine().FindSameRecordings(new[] { Sig("a.wav", song, 200), Sig("b.wav", song, 206) });

        Assert.Empty(groups);
    }

    [Fact]
    public void ShortClipsAndMostlySilentFiles_AreIneligible()
    {
        var silentish = new short[AcousticFingerprinter.SampleRate * 40];
        Array.Copy(Music(4, 5), silentish, AcousticFingerprinter.SampleRate * 5); // only 5 s audible

        Assert.False(AcousticMatchEngine.IsEligible(Sig("clip.wav", Music(4), duration: 10)));
        Assert.False(AcousticMatchEngine.IsEligible(Sig("quiet.wav", silentish)));
        Assert.True(AcousticMatchEngine.IsEligible(Sig("song.wav", Music(4))));
    }

    [Fact]
    public void SilentPadding_DoesNotDiluteAMatch()
    {
        var song = Music(5);
        var padded = new short[song.Length + AcousticFingerprinter.SampleRate * 8];
        Array.Copy(song, 0, padded, AcousticFingerprinter.SampleRate * 4, song.Length); // 4 s silence each side

        var ber = AcousticMatchEngine.BitErrorRate(AcousticFingerprinter.Compute(song), AcousticFingerprinter.Compute(padded));

        Assert.NotNull(ber);
        Assert.True(ber < 0.1, $"BER {ber}");
    }

    [Fact]
    public void Grouping_IsOneHop_NotTransitive()
    {
        // A~B and B~C but A and C differ: with star grouping C must not join A's group.
        var a = Music(10);
        var c = Music(11);
        var b = a.Select((s, i) => i < a.Length / 2 ? s : c[i]).ToArray();

        var groups = new AcousticMatchEngine().FindSameRecordings(new[] { Sig("a.wav", a), Sig("b.wav", b), Sig("c.wav", c) });

        Assert.DoesNotContain(groups, g => g.Members.Any(m => m.File.Path.EndsWith("a.wav")) && g.Members.Any(m => m.File.Path.EndsWith("c.wav")));
    }

    [Fact]
    public void LargeSets_UseTheIndex_AndStillFindPairs()
    {
        var sigs = new List<AcousticSignature>();
        for (var i = 0; i < 210; i++)
        {
            sigs.Add(Sig($"s{i}.wav", Music(100 + i, seconds: 25), duration: 100 + i % 3));
        }

        sigs.Add(Sig("copy.wav", Music(150, seconds: 25, gain: 0.7), duration: 100 + 150 % 3));

        var groups = new AcousticMatchEngine().FindSameRecordings(sigs);

        var group = Assert.Single(groups);
        Assert.Contains(group.Members, m => m.File.Path.EndsWith("copy.wav"));
        Assert.Contains(group.Members, m => m.File.Path.EndsWith("s50.wav"));
    }
}
