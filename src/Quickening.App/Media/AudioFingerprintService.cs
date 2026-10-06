using System.Runtime.InteropServices.WindowsRuntime;
using Quickening.Core.Audio;
using Quickening.Core.Models;
using Quickening.Core.Storage;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Quickening.App.Media;

/// <summary>
/// Deep audio matching, App side: decodes a song's fingerprint window with
/// Windows' own MediaTranscoder (WAV/MP3/AAC/FLAC/WMA - no bundled decoder),
/// fingerprints it in Core, and caches the result in the store. WinRT-only,
/// so it lives here, like VideoFrameHasher.
/// </summary>
public static class AudioFingerprintService
{
    private static readonly string[] AudioProperties =
        { "System.Media.Duration", "System.Audio.SampleRate", "System.Audio.SampleSize", "System.Audio.EncodingBitrate" };

    /// <summary>
    /// The file's signature from the cache when still valid, else decoded and
    /// fingerprinted (and cached). Null for anything unreadable - DRM, an
    /// unsupported codec, a corrupt or vanished file - which is simply left out.
    /// </summary>
    public static async Task<AcousticSignature?> TryGetSignatureAsync(FileRecord file, SqliteStore? store, CancellationToken cancellationToken)
    {
        try
        {
            if (store?.GetAudioFingerprint(file.Path) is { } cached
                && cached.Version == AcousticFingerprinter.Version
                && cached.SizeBytes == file.SizeBytes
                && cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return new AcousticSignature(file, cached.DurationSeconds, cached.Frames,
                    AudioFidelity.Create(file.Path, cached.BitsPerSample, cached.SampleRateHz, cached.BitrateKbps));
            }

            var storageFile = await StorageFile.GetFileFromPathAsync(file.Path);
            var props = await storageFile.Properties.RetrievePropertiesAsync(AudioProperties);
            var durationSeconds = props.TryGetValue("System.Media.Duration", out var d) && d is ulong ticks ? ticks / 1e7 : 0;
            var sampleRate = props.TryGetValue("System.Audio.SampleRate", out var r) && r is uint rate ? (int)rate : 0;
            var bits = props.TryGetValue("System.Audio.SampleSize", out var s) && s is uint size ? (int)size : 0;
            var kbps = props.TryGetValue("System.Audio.EncodingBitrate", out var b) && b is uint bps ? (int)(bps / 1000) : 0;
            if (durationSeconds <= 0)
            {
                return null;
            }

            var (start, length) = AcousticFingerprinter.WindowFor(durationSeconds);
            var pcm = await DecodeAsync(storageFile, start, start + length, cancellationToken);
            if (pcm is null)
            {
                // Some sources reject trimming: decode the whole file and slice.
                var whole = await DecodeAsync(storageFile, 0, 0, cancellationToken);
                pcm = whole is null ? null : Slice(whole, start, length);
            }

            if (pcm is null || pcm.Length < AcousticFingerprinter.FrameSize)
            {
                return null;
            }

            var frames = AcousticFingerprinter.Compute(pcm);
            store?.UpsertAudioFingerprint(file.Path, new CachedAudioFingerprint(
                file.SizeBytes, file.LastWriteTimeUtc, AcousticFingerprinter.Version, durationSeconds, bits, sampleRate, kbps, frames));
            return new AcousticSignature(file, durationSeconds, frames, AudioFidelity.Create(file.Path, bits, sampleRate, kbps));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger?.LogInfo($"Deep audio: skipped '{file.Path}': {ex.Message}");
            return null;
        }
    }

    // Transcodes to an in-memory WAV, 11,025 Hz mono 16-bit, from startSeconds to
    // stopSeconds (0 = to the end). NOTE: MediaTranscoder.TrimStopTime is the
    // stop POSITION, not "time trimmed off the end" as its docs suggest -
    // passing the latter decoded 225 s of a 355 s song instead of the 90 s
    // window. Null when the source can't be transcoded or yields no samples
    // (some sources reject trimming - the caller retries untrimmed).
    private static async Task<short[]?> DecodeAsync(StorageFile file, double startSeconds, double stopSeconds, CancellationToken cancellationToken)
    {
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
        profile.Audio = AudioEncodingProperties.CreatePcm(AcousticFingerprinter.SampleRate, 1, 16);
        profile.Video = null;
        var transcoder = new MediaTranscoder
        {
            TrimStartTime = TimeSpan.FromSeconds(startSeconds),
            TrimStopTime = TimeSpan.FromSeconds(stopSeconds),
        };

        using var input = await file.OpenReadAsync();
        using var output = new InMemoryRandomAccessStream();
        var prepared = await transcoder.PrepareStreamTranscodeAsync(input, output, profile);
        if (!prepared.CanTranscode)
        {
            return null;
        }

        await prepared.TranscodeAsync().AsTask(cancellationToken);
        output.Seek(0);
        var bytes = new byte[output.Size];
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        var samples = WavPcm.ReadMono16(bytes);
        return samples is { Length: > 0 } ? samples : null;
    }

    private static short[] Slice(short[] whole, double startSeconds, double lengthSeconds)
    {
        var from = Math.Min(whole.Length, (int)(startSeconds * AcousticFingerprinter.SampleRate));
        var count = Math.Min(whole.Length - from, (int)(lengthSeconds * AcousticFingerprinter.SampleRate));
        return whole.AsSpan(from, count).ToArray();
    }
}
