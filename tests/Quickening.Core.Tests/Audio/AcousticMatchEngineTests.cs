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

    private static AcousticSignature RawSig(string name, uint[] frames, double duration = 200) => new(
        new FileRecord { Path = $@"C:\music\{name}", SizeBytes = 1, Category = MimeCategory.Audio, LastWriteTimeUtc = DateTime.UtcNow },
        duration, frames, AudioFidelity.Create(name, 16, 44100, 0));

    // Final review: with 200+ files the old exact-value index needed >= 3 frames
    // identical in all 32 bits - at BER 0.2 only ~1.5 of 1,950 frames are, so a
    // genuine (weak-end) pair grouped in a folder of 199 songs and vanished at
    // 200. Matching must not depend on library size.
    [Fact]
    public void LargeSets_StillFindAWeakEndPair_AtBitErrorRateTwenty()
    {
        var rng = new Random(42);
        uint[] RandomFrames() => Enumerable.Range(0, 1950).Select(_ => (uint)rng.NextInt64(1, uint.MaxValue)).ToArray();
        var original = RandomFrames();
        var degraded = original.Select(f =>
        {
            var x = f;
            for (var bit = 0; bit < 32; bit++)
            {
                if (rng.NextDouble() < 0.2) x ^= 1u << bit;
            }

            return x == AcousticFingerprinter.Quiet ? 1u : x;
        }).ToArray();

        var sigs = Enumerable.Range(0, 210).Select(i => RawSig($"s{i}.wav", RandomFrames(), 200 + i % 4)).ToList();
        sigs.Add(RawSig("original.wav", original, 201));
        sigs.Add(RawSig("degraded.mp3", degraded, 201));

        var groups = new AcousticMatchEngine().FindSameRecordings(sigs);

        var group = Assert.Single(groups);
        Assert.Equal(new[] { "degraded.mp3", "original.wav" }, group.Members.Select(m => Path.GetFileName(m.File.Path)).Order());
    }

    [Fact]
    public void LargeSets_StillFindPairs_AmongManySongs()
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
