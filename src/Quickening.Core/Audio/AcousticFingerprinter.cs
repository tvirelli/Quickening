using System.Numerics;

namespace Quickening.Core.Audio;

/// <summary>
/// Acoustic fingerprint for deep audio matching: Haitsma-Kalker style 32-bit
/// sub-fingerprints, one per ~46 ms frame. Each bit says whether the energy
/// difference between two adjacent frequency bands rose or fell since the
/// previous frame - which survives mastering, EQ, loudness changes and lossy
/// encoding, but differs completely between different recordings (measured on
/// real files: same recording BER 0.00-0.13, different songs ~0.49).
/// Input is 11,025 Hz mono 16-bit PCM of the fingerprint window. Frames whose
/// level is below about -60 dBFS are emitted as <see cref="Quiet"/> and are
/// ignored when comparing - silence carries no identity.
/// </summary>
public static class AcousticFingerprinter
{
    public const int Version = 1;
    public const int SampleRate = 11025;
    public const int FrameSize = 4096;
    public const int HopSize = 512;
    public const double FramesPerSecond = SampleRate / (double)HopSize;
    public const uint Quiet = 0;

    private const int Bands = 33;
    private const double LowHz = 300;
    private const double HighHz = 2000;
    private const double QuietMeanSquare = 1e-6;

    private const double WindowStartSeconds = 20;
    private const double WindowLengthSeconds = 90;
    private const double ShortTrackSeconds = 60;

    private static readonly double[] HannWindow =
        Enumerable.Range(0, FrameSize).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1))).ToArray();

    private static readonly int[] BandEdges = Enumerable.Range(0, Bands + 1)
        .Select(b => (int)Math.Round(LowHz * Math.Pow(HighHz / LowHz, b / (double)Bands) * FrameSize / SampleRate))
        .ToArray();

    /// <summary>
    /// Which part of a track to fingerprint: 90 s starting 20 s in, skipping
    /// intros that are often near-silent or shared across a project; tracks
    /// under 60 s are fingerprinted from the start.
    /// </summary>
    public static (double StartSeconds, double LengthSeconds) WindowFor(double durationSeconds)
    {
        if (durationSeconds < ShortTrackSeconds)
        {
            return (0, Math.Max(0, Math.Min(durationSeconds, WindowLengthSeconds)));
        }

        return (WindowStartSeconds, Math.Min(WindowLengthSeconds, durationSeconds - WindowStartSeconds));
    }

    public static uint[] Compute(ReadOnlySpan<short> monoPcm)
    {
        var frameCount = (monoPcm.Length - FrameSize) / HopSize + 1;
        if (frameCount < 2)
        {
            return Array.Empty<uint>();
        }

        var output = new uint[frameCount - 1];
        var buffer = new Complex[FrameSize];
        var energies = new double[Bands];
        var previous = new double[Bands];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = frame * HopSize;
            double meanSquare = 0;
            for (var i = 0; i < FrameSize; i++)
            {
                var sample = monoPcm[offset + i] / 32768.0;
                meanSquare += sample * sample;
                buffer[i] = new Complex(sample * HannWindow[i], 0);
            }

            meanSquare /= FrameSize;
            Fft(buffer);
            for (var b = 0; b < Bands; b++)
            {
                double energy = 0;
                var end = Math.Max(BandEdges[b + 1], BandEdges[b] + 1);
                for (var k = BandEdges[b]; k < end; k++)
                {
                    var magnitude = buffer[k].Magnitude;
                    energy += magnitude * magnitude;
                }

                energies[b] = energy;
            }

            if (frame > 0)
            {
                if (meanSquare < QuietMeanSquare)
                {
                    output[frame - 1] = Quiet;
                }
                else
                {
                    uint bits = 0;
                    for (var m = 0; m < 32; m++)
                    {
                        if ((energies[m] - energies[m + 1]) - (previous[m] - previous[m + 1]) > 0)
                        {
                            bits |= 1u << m;
                        }
                    }

                    // A real frame that happens to be all zeros must not read as Quiet.
                    output[frame - 1] = bits == Quiet ? 1u : bits;
                }
            }

            (previous, energies) = (energies, previous);
        }

        return output;
    }

    private static void Fft(Complex[] a)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                for (var j = 0; j < length / 2; j++)
                {
                    var u = a[i + j];
                    var v = a[i + j + length / 2] * w;
                    a[i + j] = u + v;
                    a[i + j + length / 2] = u - v;
                    w *= step;
                }
            }
        }
    }
}
