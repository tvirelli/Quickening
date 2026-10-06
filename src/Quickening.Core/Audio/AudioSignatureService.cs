namespace Quickening.Core.Audio;

/// <summary>
/// The tag-based identity of an audio file (F10): title/artist/album plus
/// duration and bitrate, read from the file's metadata (ID3/Vorbis/MP4/etc.)
/// via ATL.NET. Duration is in whole seconds, bitrate in kbps.
/// </summary>
public readonly record struct AudioInfo(string Title, string Artist, string Album, int DurationSeconds, int BitrateKbps);

/// <summary>
/// Reads an audio file's tags for duplicate-song matching. Returns null for
/// anything unreadable OR untagged (no title) - a file with no song identity
/// can't be matched by tags, and shouldn't be guessed at from its filename.
/// </summary>
public static class AudioSignatureService
{
    static AudioSignatureService()
    {
        // ATL defaults to reporting the FILENAME (sans extension) as Title when
        // a file carries no title tag - which silently defeated the "untagged
        // files never group" guard below and made a real false positive: two
        // untagged rips both named "Track01.mp3" in different album folders
        // (with durations within tolerance) would group as "the same song".
        // Caught by DetectionAccuracyTests.AudioSignature_UntaggedFile_
        // YieldsNoSignature. With the fallback off, an untagged file's Title
        // is genuinely empty and TryRead returns null as documented.
        ATL.Settings.UseFileNameWhenNoTitle = false;
    }

    public static AudioInfo? TryRead(string path)
    {
        try
        {
            var track = new ATL.Track(path);
            if (string.IsNullOrWhiteSpace(track.Title))
            {
                return null;
            }

            // No usable duration = no duration-based matching; the engine's
            // tolerance clustering would lump every unknown-length file with
            // the same title into one "same song" run.
            if (track.Duration <= 0)
            {
                return null;
            }

            return new AudioInfo(
                track.Title ?? string.Empty,
                track.Artist ?? string.Empty,
                track.Album ?? string.Empty,
                track.Duration,
                (int)track.Bitrate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
