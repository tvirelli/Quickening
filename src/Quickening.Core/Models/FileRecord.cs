namespace Quickening.Core.Models;

public sealed class FileRecord
{
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }

    // Not required: only the live scan (FileEnumerator) sets a real value; the
    // SqliteStore hash-cache doesn't persist it and every other construction
    // (tests, watcher reads) leaves it default(DateTime) = "unknown", which the
    // created-together safety signal treats as no-signal. Used only by
    // Quickening.Core.Safety.CreatedTogetherDetector.
    public DateTime CreationTimeUtc { get; init; }

    public required MimeCategory Category { get; init; }
    public byte[]? PartialHash { get; set; }
    public byte[]? FullHash { get; set; }

    // dHash (difference hash) of an image file's pixel content - null for
    // non-image files, and for image files until SimilarityEngine computes
    // it. Two images are "similar" (not necessarily byte-identical) when
    // their hashes' Hamming distance is small - see SimilarityEngine's own
    // doc comment.
    public ulong? PerceptualHash { get; set; }

    // True for a cloud-sync "online-only" placeholder (OneDrive/Dropbox/
    // Google Drive) whose content isn't actually on disk yet - reading it
    // triggers a download. See FileEnumerator's own doc comment on how
    // this is detected. Not persisted to SqliteStore - a placeholder's
    // status can flip (synced/evicted) between scans, so it's recomputed
    // fresh from the live file every time rather than cached.
    public bool IsCloudPlaceholder { get; set; }
}
