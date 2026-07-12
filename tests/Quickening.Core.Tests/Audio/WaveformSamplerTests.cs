using System.Text;
using Quickening.Core.Audio;
using Xunit;

namespace Quickening.Core.Tests.Audio;

public class WaveformSamplerTests
{
    private static byte[] BuildWav(short[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Length * 2;

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);              // fmt chunk size
        w.Write((short)1);        // PCM
        w.Write((short)1);        // mono
        w.Write(8000);            // sample rate
        w.Write(8000 * 2);        // byte rate
        w.Write((short)2);        // block align
        w.Write((short)16);       // bits per sample

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            w.Write(s);
        }

        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void ComputesPerColumnPeaks()
    {
        var wav = BuildWav(new short[] { 0, 16384, -32768, 8192 });

        var peaks = WaveformSampler.ComputePeaks(wav, 4);

        Assert.Equal(4, peaks.Length);
        Assert.Equal(0f, peaks[0], 3);
        Assert.Equal(0.5f, peaks[1], 2);
        Assert.Equal(1.0f, peaks[2], 2);
        Assert.Equal(0.25f, peaks[3], 2);
    }

    [Fact]
    public void TakesTheMaxWithinAColumn()
    {
        // Two samples per column; the louder one wins.
        var wav = BuildWav(new short[] { 100, 16384, -8000, 200 });

        var peaks = WaveformSampler.ComputePeaks(wav, 2);

        Assert.Equal(0.5f, peaks[0], 2);   // max(100, 16384)
        Assert.Equal(8000f / 32768f, peaks[1], 3); // max(8000, 200)
    }

    [Fact]
    public void NonWavInput_ReturnsZeroPeaks()
    {
        var peaks = WaveformSampler.ComputePeaks(Encoding.ASCII.GetBytes("not a wav file"), 8);

        Assert.Equal(8, peaks.Length);
        Assert.All(peaks, p => Assert.Equal(0f, p));
    }

    [Fact]
    public void ZeroColumns_ReturnsEmpty()
    {
        Assert.Empty(WaveformSampler.ComputePeaks(BuildWav(new short[] { 1, 2, 3 }), 0));
    }
}
