using Quickening.Core.Audio;
using Xunit;

namespace Quickening.Core.Tests.Audio;

public class AcousticFingerprinterTests
{
    internal static short[] Music(int seed, double seconds, double gain = 1.0)
    {
        // A deterministic "song": a melody of harmonic tones that changes every
        // 0.25 s plus light noise - structured enough to fingerprint like music.
        var rng = new Random(seed);
        var n = (int)(seconds * AcousticFingerprinter.SampleRate);
        var samples = new short[n];
        var freqs = Enumerable.Range(0, (int)(seconds * 4) + 1).Select(_ => 300 + rng.NextDouble() * 1500).ToArray();
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)AcousticFingerprinter.SampleRate;
            var f = freqs[(int)(t * 4)];
            var v = 0.3 * Math.Sin(2 * Math.PI * f * t) + 0.15 * Math.Sin(2 * Math.PI * 2 * f * t) + 0.02 * (rng.NextDouble() - 0.5);
            samples[i] = (short)Math.Clamp(v * gain * 32767, short.MinValue, short.MaxValue);
        }

        return samples;
    }

    [Fact]
    public void Compute_IsDeterministic_AndIgnoresVolume()
    {
        var song = Music(1, 30);
        var quieter = Music(1, 30, gain: 0.5);

        var a = AcousticFingerprinter.Compute(song);
        var b = AcousticFingerprinter.Compute(quieter);

        Assert.Equal(a, AcousticFingerprinter.Compute(song));
        var differingBits = a.Zip(b).Sum(p => System.Numerics.BitOperations.PopCount(p.First ^ p.Second));
        Assert.True(differingBits / (32.0 * a.Length) < 0.02, $"gain change flipped {differingBits} bits");
    }

    [Fact]
    public void Compute_ProducesAboutTwentyOneFramesPerSecond()
    {
        var frames = AcousticFingerprinter.Compute(Music(2, 10));

        Assert.InRange(frames.Length, 205, 215);
    }

    [Fact]
    public void Compute_MarksSilentFramesQuiet()
    {
        var silence = new short[AcousticFingerprinter.SampleRate * 5];

        Assert.All(AcousticFingerprinter.Compute(silence), f => Assert.Equal(AcousticFingerprinter.Quiet, f));
    }

    [Theory]
    [InlineData(300, 20, 90)]
    [InlineData(100, 20, 80)]
    [InlineData(45, 0, 45)]
    public void WindowFor_SkipsTheIntroOfLongTracks(double duration, double start, double length)
    {
        Assert.Equal((start, length), AcousticFingerprinter.WindowFor(duration));
    }

    [Fact]
    public void WavPcm_ReadsTheDataChunk()
    {
        var pcm = new short[] { 1, -2, 300 };
        var bytes = new List<byte>();
        void Ascii(string s) => bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
        Ascii("RIFF"); bytes.AddRange(BitConverter.GetBytes(4 + 8 + 16 + 8 + 4 + 8 + 6));
        Ascii("WAVE");
        Ascii("fmt "); bytes.AddRange(BitConverter.GetBytes(16));
        bytes.AddRange(BitConverter.GetBytes((short)1)); bytes.AddRange(BitConverter.GetBytes((short)1));
        bytes.AddRange(BitConverter.GetBytes(11025)); bytes.AddRange(BitConverter.GetBytes(22050));
        bytes.AddRange(BitConverter.GetBytes((short)2)); bytes.AddRange(BitConverter.GetBytes((short)16));
        Ascii("LIST"); bytes.AddRange(BitConverter.GetBytes(4)); Ascii("abcd");
        Ascii("data"); bytes.AddRange(BitConverter.GetBytes(6));
        foreach (var s in pcm) bytes.AddRange(BitConverter.GetBytes(s));

        Assert.Equal(pcm, WavPcm.ReadMono16(bytes.ToArray()));
        Assert.Null(WavPcm.ReadMono16(new byte[] { 1, 2, 3 }));
    }
}
