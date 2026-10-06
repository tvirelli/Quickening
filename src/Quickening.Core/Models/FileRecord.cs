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

    // DCT perceptual hash (pHash) of an image file's pixel content - null for
    // non-image files, and for image files until the similarity pass computes
    // it. Two images are "similar" (not necessarily byte-identical) when their
    // hashes' Hamming distance is small - see SimilarityEngine's own doc comment.
    public ulong? PerceptualHash { get; set; }

    // Which hash ALGORITHM produced PerceptualHash (PerceptualHashService.HashVersion).
    // Null for non-images / uncomputed rows and for rows cached before versioning
    // existed - a mismatch with the current version forces a recompute rather than
    // comparing an old dHash value as if it were a current pHash.
    public int? PerceptualHashVersion { get; set; }

    // Pixel dimensions of an image, captured alongside PerceptualHash during
    // the similarity pass (System.Drawing decodes the image to hash it, so
    // width/height are free). Null for non-images and until that pass runs.
    // Persisted so a cached re-scan keeps them - drives the "best" (highest-
    // resolution) copy pick in a look-alike group (F3 keep-best).
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }

    // 4x4 average-colour grid (48 bytes) captured with PerceptualHash. Null for
    // non-images and pre-colour rows. Similarity requires this to match too, so
    // brightness-similar-but-different-colour images are not grouped.
    public byte[]? ColorSignature { get; set; }

    // Sharpness score (variance of the Laplacian; higher = sharper) captured with
    // PerceptualHash (F9). Null for non-images and until the similarity/blur pass
    // runs. A low score flags a likely-blurry photo - a review candidate, never
    // an auto-delete (a legitimately smooth image also scores low).
    public double? Sharpness { get; set; }

    // True for a cloud-sync "online-only" placeholder (OneDrive/Dropbox/
    // Google Drive) whose content isn't actually on disk yet - reading it
    // triggers a download. See FileEnumerator's own doc comment on how
    // this is detected. Not persisted to SqliteStore - a placeholder's
    // status can flip (synced/evicted) between scans, so it's recomputed
    // fresh from the live file every time rather than cached.
    public bool IsCloudPlaceholder { get; set; }
}
