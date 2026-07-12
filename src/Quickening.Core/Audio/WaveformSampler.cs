namespace Quickening.Core.Audio;

/// <summary>
/// Reduces decoded PCM audio (a 16-bit WAV, as produced by transcoding any source
/// file) to a small array of per-column peak amplitudes in 0..1, ready to draw as
/// a waveform. Pure and allocation-light so it can be unit-tested and run off the
/// audio file's real samples rather than a decorative animation.
/// </summary>
public static class WaveformSampler
{
    /// <summary>
    /// Computes <paramref name="columns"/> peak amplitudes (max absolute sample,
    /// normalized to 0..1) across the WAV's data. Returns all-zero peaks for input
    /// that isn't 16-bit PCM WAV or has no data, so callers can fall back cleanly.
    /// </summary>
    public static float[] ComputePeaks(byte[] wav, int columns)
    {
        if (columns <= 0)
        {
            return Array.Empty<float>();
        }

        var peaks = new float[columns];
        if (wav is null || wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            return peaks;
        }

        var bitsPerSample = 0;
        var dataOffset = -1;
        var dataLength = 0;

        // Walk the RIFF chunks; capture fmt's bit depth and the data chunk's span.
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0)
            {
                break;
            }

            var body = pos + 8;
            if (id == "fmt " && body + 16 <= wav.Length)
            {
                bitsPerSample = BitConverter.ToUInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = Math.Min(size, wav.Length - body);
            }

            pos = body + size + (size & 1); // chunks are word-aligned
        }

        // Only 16-bit PCM is emitted by the transcoder path that feeds this.
        if (dataOffset < 0 || bitsPerSample != 16 || dataLength < 2)
        {
            return peaks;
        }

        var totalSamples = dataLength / 2; // interleaved across channels; peak ignores channel layout
        var perColumn = Math.Max(1, totalSamples / columns);

        for (var c = 0; c < columns; c++)
        {
            var start = c * perColumn;
            if (start >= totalSamples)
            {
                break;
            }

            var end = Math.Min(start + perColumn, totalSamples);
            var maxAbs = 0;
            for (var s = start; s < end; s++)
            {
                var idx = dataOffset + s * 2;
                var sample = (short)(wav[idx] | (wav[idx + 1] << 8));
                var magnitude = Math.Abs((int)sample);
                if (magnitude > maxAbs)
                {
                    maxAbs = magnitude;
                }
            }

            peaks[c] = maxAbs / 32768f;
        }

        return peaks;
    }
}
