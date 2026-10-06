using Quickening.Core.Audio;
using Xunit;

namespace Quickening.Core.Tests.Audio;

public class AudioFidelityTests
{
    [Fact]
    public void Compare_RanksLosslessThenBitDepthThenRateThenBitrate()
    {
        var flac2496 = AudioFidelity.Create(@"C:\a.flac", 24, 96000, 0);
        var wav1644 = AudioFidelity.Create(@"C:\a.wav", 16, 44100, 1411);
        var mp3320 = AudioFidelity.Create(@"C:\a.mp3", 0, 44100, 320);
        var mp3128 = AudioFidelity.Create(@"C:\a.mp3", 0, 44100, 128);

        Assert.True(AudioFidelity.Compare(flac2496, wav1644) > 0);
        Assert.True(AudioFidelity.Compare(wav1644, mp3320) > 0);
        Assert.True(AudioFidelity.Compare(mp3320, mp3128) > 0);
        Assert.Equal(0, AudioFidelity.Compare(mp3128, AudioFidelity.Create(@"C:\b.mp3", 0, 44100, 128)));
    }

    [Fact]
    public void Compare_TwentyFourBitMasterBeatsSixteenBitOriginal()
    {
        var master = AudioFidelity.Create(@"C:\Mastered\Song.wav", 24, 48000, 2304);
        var original = AudioFidelity.Create(@"C:\Original\Song.wav", 16, 48000, 1536);

        Assert.True(AudioFidelity.Compare(master, original) > 0);
    }

    [Theory]
    [InlineData(@"C:\s.wav", 24, 48000, 2304, "WAV · 24-bit · 48 kHz")]
    [InlineData(@"C:\s.flac", 16, 44100, 0, "FLAC · 16-bit · 44.1 kHz")]
    [InlineData(@"C:\s.mp3", 0, 44100, 320, "MP3 · 320 kbps")]
    [InlineData(@"C:\s.m4a", 0, 0, 0, "M4A")]
    public void Label_DescribesTheFormat(string path, int bits, int rate, int kbps, string expected)
    {
        Assert.Equal(expected, AudioFidelity.Create(path, bits, rate, kbps).Label);
    }

    [Fact]
    public void M4a_IsTreatedAsLossy()
    {
        Assert.False(AudioFidelity.Create(@"C:\s.m4a", 16, 44100, 256).Lossless);
        Assert.True(AudioFidelity.Create(@"C:\s.AIFF", 16, 44100, 0).Lossless);
    }
}
