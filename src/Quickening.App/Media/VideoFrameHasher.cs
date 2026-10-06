using System.Drawing;
using System.IO;
using Quickening.Core.Models;
using Quickening.Core.Similarity;
using Windows.Media.Editing;
using Windows.Storage;

namespace Quickening.App.Media;

/// <summary>
/// Builds a video's frame-hash signature (F11) using Windows' own Media APIs -
/// no bundled decoder. Samples a fixed set of frames at the same relative
/// positions for every video (so VideoSimilarityEngine can compare position by
/// position), scales each to a small thumbnail, and pHashes it. WinRT-only, so
/// this lives in the App layer; Core just does the pure grouping afterwards.
/// </summary>
public static class VideoFrameHasher
{
    // Same relative positions for every video. Avoids the very start/end (title
    // cards, black frames) where unrelated clips often look alike.
    private static readonly double[] SamplePositions = { 0.1, 0.3, 0.5, 0.7, 0.9 };

    private const int FrameSize = 128;

    /// <summary>
    /// Returns the signature, or null for anything unreadable (an unsupported
    /// codec, a zero-length clip, a file that vanished) - such a video is simply
    /// left out of matching rather than aborting the whole pass.
    /// </summary>
    public static async Task<VideoSimilarityEngine.VideoSignature?> TryComputeSignatureAsync(FileRecord file)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(file.Path);
            var clip = await MediaClip.CreateFromFileAsync(storageFile);

            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var totalTicks = clip.OriginalDuration.Ticks;
            if (totalTicks <= 0)
            {
                return null;
            }

            var hashes = new ulong[SamplePositions.Length];
            for (var i = 0; i < SamplePositions.Length; i++)
            {
                var at = TimeSpan.FromTicks((long)(totalTicks * SamplePositions[i]));
                var thumbnail = await composition.GetThumbnailAsync(at, FrameSize, FrameSize, VideoFramePrecision.NearestFrame);

                using var stream = thumbnail.AsStreamForRead();
                using var bitmap = new Bitmap(stream);
                hashes[i] = PerceptualHashService.HashImage(bitmap);
            }

            return new VideoSimilarityEngine.VideoSignature(file, hashes);
        }
        catch (Exception)
        {
            // GetFileFromPathAsync / MediaClip / GetThumbnailAsync surface a wide
            // range of COM/format failures; none should abort the scan.
            return null;
        }
    }
}
