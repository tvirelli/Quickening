namespace Quickening.Core.Audio;

/// <summary>Reads the 16-bit PCM samples out of a WAV byte buffer (the deep audio decoder's output).</summary>
public static class WavPcm
{
    /// <summary>The "data" chunk as 16-bit samples, or null when the buffer isn't a RIFF/WAVE file or has no data chunk.</summary>
    public static short[]? ReadMono16(byte[] wav)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
        {
            return null;
        }

        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, position, 4);
            var length = BitConverter.ToInt32(wav, position + 4);
            if (id == "data")
            {
                length = Math.Min(Math.Max(0, length), wav.Length - position - 8);
                var samples = new short[length / 2];
                Buffer.BlockCopy(wav, position + 8, samples, 0, samples.Length * 2);
                return samples;
            }

            if (length < 0)
            {
                return null;
            }

            position += 8 + length + (length & 1);
        }

        return null;
    }
}
