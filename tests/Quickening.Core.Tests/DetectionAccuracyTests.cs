using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Quickening.Core.Audio;
using Quickening.Core.Models;
using Quickening.Core.Orchestration;
using Quickening.Core.Similarity;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests;

/// <summary>
/// End-to-end accuracy suite for the identification engines: every test pairs a
/// TRUE-POSITIVE assertion (a genuine match is found) with a FALSE-POSITIVE
/// guard (things that merely LOOK related are NOT matched). False positives
/// are the dangerous direction for a deletion tool, so most of this file is
/// about what must NOT group. Images are generated with seeded randomness -
/// deterministic across runs.
/// </summary>
public class DetectionAccuracyTests
{
    // ===== helpers =====

    private static (string Dir, string DbPath) NewScanArena()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qk-acc-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(Path.GetTempPath(), "qk-accdb-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(dir);
        return (dir, dbPath);
    }

    private static void Cleanup(string dir, string dbPath)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
        try { File.Delete(dbPath); } catch { }
    }

    private static ScanResult Scan(string dir, string dbPath, bool computeSimilarity = false)
    {
        using var store = new SqliteStore($"Data Source={dbPath}");
        store.Initialize();
        return new ScanOrchestrator(store).Scan(dir, computeSimilarity: computeSimilarity);
    }

    /// <summary>A deterministic "photo-like" bitmap: gradient background plus
    /// seeded random rectangles and lines, so the DCT hash has real structure
    /// (a flat image would degenerate to an all-equal DCT).</summary>
    private static Bitmap MakePhoto(int seed, int width = 256, int height = 256)
    {
        var bmp = new Bitmap(width, height);
        var rng = new Random(seed);
        using (var g = Graphics.FromImage(bmp))
        {
            using (var gradient = new LinearGradientBrush(
                new Rectangle(0, 0, width, height),
                Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256)),
                Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256)),
                45f))
            {
                g.FillRectangle(gradient, 0, 0, width, height);
            }

            for (var i = 0; i < 24; i++)
            {
                using var brush = new SolidBrush(Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256)));
                g.FillRectangle(brush, rng.Next(width - 40), rng.Next(height - 40), 20 + rng.Next(60), 20 + rng.Next(60));
            }

            for (var i = 0; i < 12; i++)
            {
                using var pen = new Pen(Color.FromArgb(255, rng.Next(256), rng.Next(256), rng.Next(256)), 3 + rng.Next(5));
                g.DrawLine(pen, rng.Next(width), rng.Next(height), rng.Next(width), rng.Next(height));
            }
        }

        return bmp;
    }

    private static void SavePng(Bitmap bmp, string path) => bmp.Save(path, ImageFormat.Png);

    private static void SaveJpeg(Bitmap bmp, string path, long quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        bmp.Save(path, codec, parameters);
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var resized = new Bitmap(width, height);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static FileRecord Record(string path) => new()
    {
        Path = path,
        SizeBytes = 1,
        LastWriteTimeUtc = DateTime.UnixEpoch,
        Category = MimeCategory.Video,
    };

    // ===== EXACT duplicates =====

    // True positive: byte-identical content under different names/subfolders is
    // one group. False-positive guard IN THE SAME ARENA: a file with the SAME
    // SIZE but different content must stay out of that group - this is the
    // exact case a size-plus-partial-hash shortcut would get wrong.
    [Fact]
    public void ExactDuplicates_GroupsIdenticalBytes_ButNeverSameSizeDifferentContent()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            var payload = new byte[64 * 1024];
            new Random(1).NextBytes(payload);

            File.WriteAllBytes(Path.Combine(dir, "original.bin"), payload);
            Directory.CreateDirectory(Path.Combine(dir, "backup"));
            File.WriteAllBytes(Path.Combine(dir, "backup", "copy with another name.bin"), payload);

            // Same size, differs ONLY in the final byte - middle bytes also
            // identical, so even a "first block + last block" partial hash has
            // minimal signal. A correct engine must full-hash before grouping.
            var nearTwin = (byte[])payload.Clone();
            nearTwin[^1] ^= 0xFF;
            File.WriteAllBytes(Path.Combine(dir, "near-twin.bin"), nearTwin);

            // And one differing only in the FIRST byte.
            var nearTwin2 = (byte[])payload.Clone();
            nearTwin2[0] ^= 0xFF;
            File.WriteAllBytes(Path.Combine(dir, "near-twin-2.bin"), nearTwin2);

            var result = Scan(dir, dbPath);

            var group = Assert.Single(result.DuplicateGroups);
            Assert.Equal(2, group.Files.Count);
            Assert.All(group.Files, f => Assert.DoesNotContain("near-twin", f.Path));
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    [Fact]
    public void ExactDuplicates_ThreeIdenticalCopies_OneGroupOfThree()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            var payload = new byte[8192];
            new Random(2).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(dir, "a.dat"), payload);
            File.WriteAllBytes(Path.Combine(dir, "b.dat"), payload);
            File.WriteAllBytes(Path.Combine(dir, "c.dat"), payload);

            var result = Scan(dir, dbPath);

            var group = Assert.Single(result.DuplicateGroups);
            Assert.Equal(3, group.Files.Count);
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // Unique files (all different sizes AND contents) must produce no groups.
    [Fact]
    public void ExactDuplicates_AllUniqueFiles_NoGroups()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var bytes = new byte[1000 + i];
                new Random(i).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(dir, $"unique{i}.dat"), bytes);
            }

            var result = Scan(dir, dbPath);

            Assert.Empty(result.DuplicateGroups);
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // ===== SIMILAR images (pHash + colour signature) =====

    // True positive: the same photo shrunk to half size and re-encoded as a
    // low-quality JPEG is still "the same picture". False-positive guard in the
    // same arena: a structurally DIFFERENT photo stays out.
    [Fact]
    public void SimilarImages_FindsResizedAndRecompressedVariants_NotDifferentPhotos()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            using (var photo = MakePhoto(seed: 42))
            {
                SavePng(photo, Path.Combine(dir, "photo-original.png"));
                using var half = Resize(photo, 128, 128);
                SavePng(half, Path.Combine(dir, "photo-half.png"));
                SaveJpeg(photo, Path.Combine(dir, "photo-lowq.jpg"), quality: 35);
            }

            using (var other = MakePhoto(seed: 99))
            {
                SavePng(other, Path.Combine(dir, "different-photo.png"));
            }

            var result = Scan(dir, dbPath, computeSimilarity: true);

            var group = Assert.Single(result.SimilarityGroups);
            Assert.Equal(3, group.Files.Count);
            Assert.All(group.Files, f => Assert.Contains("photo-", f.Path));
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // The classic pHash degenerate case: a FLAT image has no structure, so its
    // DCT hash carries no signal and any two flat images look structurally
    // "identical". The colour signature is what must keep two different-
    // coloured solids apart.
    [Fact]
    public void SimilarImages_TwoDifferentSolidColours_DoNotGroup()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            foreach (var (name, color) in new[] { ("red", Color.Firebrick), ("blue", Color.RoyalBlue) })
            {
                using var bmp = new Bitmap(200, 200);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(color);
                }

                SavePng(bmp, Path.Combine(dir, $"solid-{name}.png"));
            }

            var result = Scan(dir, dbPath, computeSimilarity: true);

            Assert.Empty(result.SimilarityGroups);
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // Several genuinely different photos must not chain into any group.
    [Fact]
    public void SimilarImages_DistinctPhotos_NoGroups()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            foreach (var seed in new[] { 7, 21, 63, 189 })
            {
                using var photo = MakePhoto(seed);
                SavePng(photo, Path.Combine(dir, $"distinct-{seed}.png"));
            }

            var result = Scan(dir, dbPath, computeSimilarity: true);

            Assert.Empty(result.SimilarityGroups);
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // Byte-identical images belong to EXACT duplicates, never double-reported
    // as "similar" too.
    [Fact]
    public void SimilarImages_ExactCopies_ReportedAsDuplicatesNotSimilar()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            using (var photo = MakePhoto(seed: 5))
            {
                SavePng(photo, Path.Combine(dir, "one.png"));
            }

            File.Copy(Path.Combine(dir, "one.png"), Path.Combine(dir, "two.png"));

            var result = Scan(dir, dbPath, computeSimilarity: true);

            Assert.Single(result.DuplicateGroups);
            Assert.Empty(result.SimilarityGroups);
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // ===== SIMILAR videos (multi-frame pHash) =====

    private static ulong[] Frames(params ulong[] hashes) => hashes;

    /// <summary>Flips <paramref name="bits"/> LOW bits in every frame hash -
    /// a per-frame Hamming distance of exactly that many bits.</summary>
    private static ulong[] Perturb(ulong[] frames, int bits)
    {
        var mask = (1UL << bits) - 1;
        return frames.Select(f => f ^ mask).ToArray();
    }

    [Fact]
    public void SimilarVideos_NearIdenticalFrameHashes_Group_DistantOnesDoNot()
    {
        var engine = new VideoSimilarityEngine();
        var baseFrames = Frames(0xA5A5_5A5A_F00D_BEEF, 0x1234_5678_9ABC_DEF0, 0x0F0F_F0F0_AAAA_5555, 0xDEAD_BEEF_CAFE_F00D, 0x1111_2222_3333_4444);

        var signatures = new[]
        {
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\clip.mp4"), baseFrames),
            // Re-encode: ~4 bits per frame off - same clip.
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\clip-reencoded.mp4"), Perturb(baseFrames, 4)),
            // Different video: ~32 bits per frame off - far past the threshold.
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\other.mp4"), Perturb(baseFrames, 32)),
        };

        var groups = engine.FindSimilarVideos(signatures);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.All(group.Files, f => Assert.Contains("clip", f.Path));
    }

    [Fact]
    public void SimilarVideos_OneOddFrame_StillGroups_ButConsistentDifferencesDoNot()
    {
        var engine = new VideoSimilarityEngine();
        var baseFrames = Frames(0xAAAA_BBBB_CCCC_DDDD, 0x1111_9999_2222_8888, 0x7777_0000_FFFF_3333, 0x4444_CCCC_5555_AAAA, 0xF0F0_0F0F_A5A5_5A5A);

        // One badly-off frame (a title card / black frame) among matches:
        // 4+4+4+4+30 bits = average 9.2 <= 10 - still the same clip.
        var oddFrame = Perturb(baseFrames, 4);
        oddFrame[4] = baseFrames[4] ^ ((1UL << 30) - 1);

        // EVERY frame moderately different (14 bits, average 14 > 10): a
        // different edit/clip - must NOT group.
        var consistentlyOff = Perturb(baseFrames, 14);

        var groups = engine.FindSimilarVideos(new[]
        {
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\a.mp4"), baseFrames),
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\a-with-intro-frame.mp4"), oddFrame),
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\different-edit.mp4"), consistentlyOff),
        });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.DoesNotContain(group.Files, f => f.Path.Contains("different-edit"));
    }

    // ===== Duplicate songs (tag-based) =====

    private static (FileRecord, AudioInfo) Song(string path, string title, string artist, int durationSeconds, int bitrate, long size = 1) =>
        (new FileRecord
        {
            Path = path,
            SizeBytes = size,
            LastWriteTimeUtc = DateTime.UnixEpoch,
            Category = MimeCategory.Audio,
        }, new AudioInfo(title, artist, Album: "", durationSeconds, bitrate));

    [Fact]
    public void DuplicateSongs_SameTitleArtist_DifferentBitrates_Group_BestQualityFirst()
    {
        var engine = new AudioDuplicateEngine();
        var groups = engine.FindDuplicateSongs(new[]
        {
            Song(@"C:\m\song-128.mp3", "Comfortably Numb", "Pink Floyd", 382, 128),
            Song(@"C:\m\song-320.mp3", "comfortably numb", "PINK FLOYD", 384, 320), // case + padding differ
            Song(@"C:\m\unrelated.mp3", "Time", "Pink Floyd", 413, 320),
        });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Copies.Count);
        Assert.Equal(320, group.Copies[0].BitrateKbps); // best copy leads
    }

    // Same song title by DIFFERENT artists (covers) must never group.
    [Fact]
    public void DuplicateSongs_SameTitleDifferentArtist_DoesNotGroup()
    {
        var engine = new AudioDuplicateEngine();
        var groups = engine.FindDuplicateSongs(new[]
        {
            Song(@"C:\m\original.mp3", "Hallelujah", "Leonard Cohen", 274, 256),
            Song(@"C:\m\cover.mp3", "Hallelujah", "Jeff Buckley", 273, 256),
        });

        Assert.Empty(groups);
    }

    // Same title+artist but far-apart durations (single vs extended mix) must
    // never group - 15s is the tolerance.
    [Fact]
    public void DuplicateSongs_SameSongVeryDifferentDurations_DoesNotGroup()
    {
        var engine = new AudioDuplicateEngine();
        var groups = engine.FindDuplicateSongs(new[]
        {
            Song(@"C:\m\radio-edit.mp3", "Blue Monday", "New Order", 250, 256),
            Song(@"C:\m\extended.mp3", "Blue Monday", "New Order", 450, 256),
        });

        Assert.Empty(groups);
    }

    // The upstream guard for the worst tag false positive: a file with NO
    // title never yields an AudioInfo at all, so a folder of untagged files
    // can never mass-group as "the same (untitled) song".
    [Fact]
    public void AudioSignature_UntaggedFile_YieldsNoSignature()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mp3");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 }); // not real audio; certainly no title tag
        try
        {
            Assert.Null(AudioSignatureService.TryRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // CD-rip default titles with no artist are not a song identity: two
    // different albums' rips both tagged "Track 01" (same-ish length) must not
    // group. The same title WITH an artist stays groupable.
    [Fact]
    public void DuplicateSongs_GenericTitleWithoutArtist_DoesNotGroup()
    {
        var engine = new AudioDuplicateEngine();

        Assert.Empty(engine.FindDuplicateSongs(new[]
        {
            Song(@"C:\m\albumA\track01.mp3", "Track 01", "", 210, 192),
            Song(@"C:\m\albumB\track01.mp3", "Track 01", "", 214, 256),
        }));

        Assert.Single(engine.FindDuplicateSongs(new[]
        {
            Song(@"C:\m\a.mp3", "Track 01", "Boards of Canada", 210, 192),
            Song(@"C:\m\b.mp3", "Track 01", "Boards of Canada", 214, 256),
        }));
    }

    // Duration runs are anchored to their FIRST member: 200/214/228 must not
    // chain into one group (228 is 28s from the anchor) - only 200+214 group.
    [Fact]
    public void DuplicateSongs_DurationRuns_DoNotChainPastTolerance()
    {
        var engine = new AudioDuplicateEngine();
        var groups = engine.FindDuplicateSongs(new[]
        {
            Song(@"C:\m\a.mp3", "One More Time", "Daft Punk", 200, 192),
            Song(@"C:\m\b.mp3", "One More Time", "Daft Punk", 214, 256),
            Song(@"C:\m\c.mp3", "One More Time", "Daft Punk", 228, 320),
        });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Copies.Count);
        Assert.DoesNotContain(group.Copies, c => c.DurationSeconds == 228);
    }

    // Flat/black frames carry no structure - two videos that are mostly black
    // (4/5 zero-hash frames) must NOT group on their empty frames, even though
    // those frames "match" perfectly.
    [Fact]
    public void SimilarVideos_MostlyBlackFrames_DoNotGroup()
    {
        var engine = new VideoSimilarityEngine();
        var groups = engine.FindSimilarVideos(new[]
        {
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\concert-a.mp4"),
                Frames(0, 0, 0, 0, 0xAAAA_BBBB_CCCC_DDDD)),
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\concert-b.mp4"),
                Frames(0, 0, 0, 0, 0xAAAA_BBBB_CCCC_DDDD ^ 0x3F)), // near frame, but only ONE comparable
        });

        Assert.Empty(groups);
    }

    // No transitive chaining: A~B and B~C at the threshold must not pull C
    // (which is 2x the threshold from A) into A's group.
    [Fact]
    public void SimilarVideos_DoNotChainTransitively()
    {
        var engine = new VideoSimilarityEngine();
        var a = Frames(0xA5A5_5A5A_F00D_BEEF, 0x1234_5678_9ABC_DEF0, 0x0F0F_F0F0_AAAA_5555, 0xDEAD_BEEF_CAFE_F00D, 0x1111_2222_3333_4444);
        var maskLow10 = (1UL << 10) - 1;               // A<->B distance 10 (threshold)
        var maskLow20 = (1UL << 20) - 1;               // A<->C distance 20, B<->C distance 10
        var b = a.Select(f => f ^ maskLow10).ToArray();
        var c = a.Select(f => f ^ maskLow20).ToArray();

        var groups = engine.FindSimilarVideos(new[]
        {
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\a.mp4"), a),
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\b.mp4"), b),
            new VideoSimilarityEngine.VideoSignature(Record(@"C:\v\c.mp4"), c),
        });

        // a anchors {a,b}; c is 20 from the anchor and must stay out (it may
        // then pair with nothing - b is already taken).
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.DoesNotContain(group.Files, f => f.Path.EndsWith("c.mp4"));
    }

    // ===== DUPLICATE FOLDERS: raw on-disk verification =====

    // Two folders identical in every ENUMERATED file, but one hides an extra
    // (Hidden-attribute) file the scan never saw: they must NOT be reported as
    // exact folder copies - deleting the "copy" would lose the hidden data.
    [Fact]
    public void DuplicateFolders_HiddenExtraFileOnDisk_PreventsExactCopyClaim()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            var payload = new byte[4096];
            new Random(11).NextBytes(payload);
            Directory.CreateDirectory(Path.Combine(dir, "copy1"));
            Directory.CreateDirectory(Path.Combine(dir, "copy2"));
            File.WriteAllBytes(Path.Combine(dir, "copy1", "f.bin"), payload);
            File.WriteAllBytes(Path.Combine(dir, "copy2", "f.bin"), payload);
            // Root gets a unique file so the ROOT itself never becomes the
            // reported "topmost" pair.
            File.WriteAllBytes(Path.Combine(dir, "unique.bin"), new byte[] { 1, 2, 3 });

            // The invisible difference: a hidden file only in copy1.
            var hidden = Path.Combine(dir, "copy1", "secret-notes.txt");
            File.WriteAllText(hidden, "unique documents the scan never enumerated");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var result = Scan(dir, dbPath);

            Assert.Single(result.DuplicateGroups); // the files still match 1:1...
            Assert.Empty(result.DuplicateFolders); // ...but the FOLDERS must not
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // The positive control for the raw check: genuinely identical folders are
    // still reported.
    [Fact]
    public void DuplicateFolders_TrulyIdenticalFolders_StillReported()
    {
        var (dir, dbPath) = NewScanArena();
        try
        {
            var payload = new byte[4096];
            new Random(12).NextBytes(payload);
            Directory.CreateDirectory(Path.Combine(dir, "copy1"));
            Directory.CreateDirectory(Path.Combine(dir, "copy2"));
            File.WriteAllBytes(Path.Combine(dir, "copy1", "f.bin"), payload);
            File.WriteAllBytes(Path.Combine(dir, "copy2", "f.bin"), payload);
            File.WriteAllBytes(Path.Combine(dir, "unique.bin"), new byte[] { 1, 2, 3 });

            var result = Scan(dir, dbPath);

            var group = Assert.Single(result.DuplicateFolders);
            Assert.Equal(2, group.Folders.Count);
        }
        finally
        {
            Cleanup(dir, dbPath);
        }
    }

    // ===== Hash cache staleness =====

    // The cache must invalidate on a SIZE change even when mtime matches (only
    // the mtime-staleness direction had coverage).
    [Fact]
    public void CachedHash_Invalidated_WhenSizeChangesButMtimeMatches()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 1, 2, 3, 4, 5 });
            var mtime = File.GetLastWriteTimeUtc(tempFile);

            using var store = new SqliteStore("Data Source=:memory:");
            store.Initialize();
            store.UpsertFile(new FileRecord
            {
                Path = tempFile,
                SizeBytes = 999_999, // cached size does NOT match the real file
                LastWriteTimeUtc = mtime,
                Category = MimeCategory.Other,
                FullHash = new byte[32], // a stale, wrong hash
            });

            var caching = new Quickening.Core.Hashing.CachingHashProvider(
                new Quickening.Core.Hashing.HashProvider(), store);

            var hash = caching.ComputeFullHash(tempFile);

            Assert.NotEqual(new byte[32], hash); // recomputed, not the stale cache
            Assert.Equal(new Quickening.Core.Hashing.HashProvider().ComputeFullHash(tempFile), hash);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ===== Paranoid mode =====

    // If full hashes ever COLLIDED for different content, paranoid mode's
    // byte-for-byte comparison must drop the group. Forced via a stub provider
    // that returns identical hashes for everything.
    [Fact]
    public void ParanoidMode_DropsGroup_WhenBytesActuallyDiffer()
    {
        var a = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var b = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(b, new byte[] { 9, 8, 7, 6 }); // same size, different bytes
        try
        {
            var records = new[]
            {
                new FileRecord { Path = a, SizeBytes = 4, LastWriteTimeUtc = DateTime.UnixEpoch, Category = MimeCategory.Other },
                new FileRecord { Path = b, SizeBytes = 4, LastWriteTimeUtc = DateTime.UnixEpoch, Category = MimeCategory.Other },
            };

            var colliding = new CollidingHashProvider();
            var withoutParanoid = new Quickening.Core.Duplicates.DuplicateEngine(colliding)
                .FindDuplicates(records).ToList();
            var withParanoid = new Quickening.Core.Duplicates.DuplicateEngine(colliding)
                .FindDuplicates(records, paranoidMode: true).ToList();

            Assert.Single(withoutParanoid);  // the (simulated) collision groups them...
            Assert.Empty(withParanoid);      // ...and paranoid's byte compare catches it
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    private sealed class CollidingHashProvider : Quickening.Core.Hashing.IHashProvider
    {
        public byte[] ComputePartialHash(string path, CancellationToken cancellationToken = default) => new byte[16];
        public byte[] ComputeFullHash(string path, CancellationToken cancellationToken = default) => new byte[32];
    }
}
