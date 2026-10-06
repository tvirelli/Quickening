using System.Globalization;

namespace Quickening.Core.Audio;

/// <summary>
/// How faithful an audio file's encoding is, for picking which copy of the
/// same recording to badge "BEST QUALITY" (deep audio matching). Lossless
/// beats lossy, then bit depth, sample rate and bitrate. Lossless is decided
/// by extension: Windows doesn't reliably expose whether an .m4a holds ALAC or
/// AAC, so .m4a ranks as lossy - it only affects which copy gets the badge.
/// Zero means "unknown" for any numeric field.
/// </summary>
public sealed record AudioFidelity(string Extension, bool Lossless, int BitsPerSample, int SampleRateHz, int BitrateKbps)
{
    private static readonly HashSet<string> LosslessExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".flac", ".aif", ".aiff" };

    public static AudioFidelity Create(string path, int bitsPerSample, int sampleRateHz, int bitrateKbps)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return new AudioFidelity(
            extension,
            LosslessExtensions.Contains(extension),
            Math.Max(0, bitsPerSample),
            Math.Max(0, sampleRateHz),
            Math.Max(0, bitrateKbps));
    }

    /// <summary>"WAV · 24-bit · 48 kHz" for lossless, "MP3 · 320 kbps" for lossy; unknown parts are left out.</summary>
    public string Label
    {
        get
        {
            var parts = new List<string> { Extension.TrimStart('.').ToUpperInvariant() };
            if (Lossless)
            {
                if (BitsPerSample > 0)
                {
                    parts.Add($"{BitsPerSample}-bit");
                }

                if (SampleRateHz > 0)
                {
                    parts.Add((SampleRateHz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz");
                }
            }
            else if (BitrateKbps > 0)
            {
                parts.Add($"{BitrateKbps} kbps");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Positive when <paramref name="a"/> is the more faithful copy, negative when <paramref name="b"/> is, 0 on a tie.</summary>
    public static int Compare(AudioFidelity a, AudioFidelity b)
    {
        var result = a.Lossless.CompareTo(b.Lossless);
        if (result != 0)
        {
            return result;
        }

        result = a.BitsPerSample.CompareTo(b.BitsPerSample);
        if (result != 0)
        {
            return result;
        }

        result = a.SampleRateHz.CompareTo(b.SampleRateHz);
        return result != 0 ? result : a.BitrateKbps.CompareTo(b.BitrateKbps);
    }
}
