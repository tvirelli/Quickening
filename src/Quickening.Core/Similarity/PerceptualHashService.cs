using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Quickening.Core.Similarity;

/// <summary>
/// dHash (difference hash): downsample to 9x8 grayscale, compare each pixel
/// to its right-hand neighbor, pack the 8x8 = 64 comparisons into one
/// ulong. Two images with a small Hamming distance between their hashes
/// look visually similar even when their bytes are completely different
/// (a re-save, a resize, a different JPEG quality level) - unlike
/// PartialHash/FullHash, which only ever match byte-identical files.
/// </summary>
public static class PerceptualHashService
{
    // Bumped whenever the hash ALGORITHM changes so cached values from an older
    // algorithm are recomputed instead of silently compared as if they were the
    // new kind (v1 = the original 9x8 difference hash; v2 = the DCT pHash below).
    public const int HashVersion = 2;

    // pHash works on a 32x32 luminance image and keeps the top-left 8x8 block of
    // low-frequency DCT coefficients - far more robust to resize, re-compression
    // and minor edits than the old 9x8 difference hash, and much less prone to
    // the false "these look nothing alike but matched" collisions dHash produced
    // on images that merely shared a coarse brightness layout.
    private const int PhashImageSize = 32;
    private const int PhashLowFreq = 8;

    // Precomputed DCT-II basis: DctBasis[u, x] = cos((2x+1)uπ / 2N). Built once,
    // reused for every image's separable row/column transform.
    private static readonly double[,] DctBasis = BuildDctBasis(PhashImageSize);

    private static double[,] BuildDctBasis(int n)
    {
        var basis = new double[n, n];
        for (var u = 0; u < n; u++)
        {
            for (var x = 0; x < n; x++)
            {
                basis[u, x] = Math.Cos(((2 * x + 1) * u * Math.PI) / (2.0 * n));
            }
        }

        return basis;
    }

    // A 4x4 grid of average colour, captured alongside the brightness dHash.
    // The dHash matches light/dark STRUCTURE but is colour-blind, so it falsely
    // groups a black-and-white cube with a colourful photo whenever their
    // coarse brightness layout is similar. Requiring the colour grids to also
    // match (see SimilarityEngine) fixes those false groups.
    private const int ColorGrid = 4;

    // Sharpness (blur) is measured as the variance of the Laplacian on a fixed
    // 128x128 grayscale (F9) - a sharp image has strong edges (high variance),
    // a blurry one is smooth (low). Fixed size makes scores comparable across
    // resolutions. Captured from the same decode as the hash.
    private const int SharpnessSize = 128;

    /// <summary>
    /// Returns null rather than throwing for anything that isn't a decodable
    /// raster image - a corrupt file, an unsupported format, or the file
    /// vanishing mid-scan should just be skipped for similarity purposes,
    /// not abort the whole scan the way DuplicateEngine's own per-file
    /// hash-failure isolation already treats equivalent failures elsewhere.
    /// </summary>
    public static PerceptualImageInfo? TryCompute(string path)
    {
        try
        {
            using var original = Image.FromFile(path);
            var pixelWidth = original.Width;
            var pixelHeight = original.Height;
            var hash = ComputePhash(original);

            // Colour signature: a 4x4 grid of average RGB (48 bytes). Robust to
            // resize/re-compression (averaging cancels JPEG noise) but very
            // different between images of different colours.
            var colorSignature = new byte[ColorGrid * ColorGrid * 3];
            using (var colorBitmap = new Bitmap(ColorGrid, ColorGrid, PixelFormat.Format32bppArgb))
            {
                using (var cg = Graphics.FromImage(colorBitmap))
                {
                    cg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    cg.DrawImage(original, 0, 0, ColorGrid, ColorGrid);
                }

                var i = 0;
                for (var y = 0; y < ColorGrid; y++)
                {
                    for (var x = 0; x < ColorGrid; x++)
                    {
                        var p = colorBitmap.GetPixel(x, y);
                        colorSignature[i++] = p.R;
                        colorSignature[i++] = p.G;
                        colorSignature[i++] = p.B;
                    }
                }
            }

            var sharpness = ComputeSharpness(original);

            return new PerceptualImageInfo(hash, pixelWidth, pixelHeight, colorSignature, sharpness);
        }
        // Image.FromFile's well-known GDI+ quirk: an unrecognized/corrupt
        // image format surfaces as OutOfMemoryException, not a more
        // sensible exception type - not real memory exhaustion.
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The DCT perceptual hash: downsample to 32x32 luminance, take the top-left
    // 8x8 low-frequency DCT block (the coarse structure that survives scaling and
    // compression), and set each bit to whether its coefficient exceeds the block
    // median. The DC term (average brightness) is excluded from BOTH the median
    // and the hash, so the same subject shot at a different exposure still matches.
    private static ulong ComputePhash(Image original)
    {
        const int n = PhashImageSize;
        var f = new double[n, n]; // f[row, col] luminance
        using (var small = new Bitmap(n, n, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(original, 0, 0, n, n);
            }

            for (var row = 0; row < n; row++)
            {
                for (var col = 0; col < n; col++)
                {
                    var p = small.GetPixel(col, row);
                    f[row, col] = (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);
                }
            }
        }

        // Separable 2D DCT-II, computing only the top-left 8x8 outputs (all we
        // hash). Transform each row into the low-frequency columns, then transform
        // those down the columns.
        var rowPass = new double[n, PhashLowFreq];
        for (var row = 0; row < n; row++)
        {
            for (var v = 0; v < PhashLowFreq; v++)
            {
                var sum = 0.0;
                for (var col = 0; col < n; col++)
                {
                    sum += f[row, col] * DctBasis[v, col];
                }

                rowPass[row, v] = sum;
            }
        }

        var block = new double[PhashLowFreq, PhashLowFreq];
        for (var u = 0; u < PhashLowFreq; u++)
        {
            for (var v = 0; v < PhashLowFreq; v++)
            {
                var sum = 0.0;
                for (var row = 0; row < n; row++)
                {
                    sum += rowPass[row, v] * DctBasis[u, row];
                }

                block[u, v] = sum;
            }
        }

        // Median over the 63 AC coefficients (everything but the DC term at 0,0).
        var ac = new double[(PhashLowFreq * PhashLowFreq) - 1];
        var k = 0;
        for (var u = 0; u < PhashLowFreq; u++)
        {
            for (var v = 0; v < PhashLowFreq; v++)
            {
                if (u == 0 && v == 0)
                {
                    continue;
                }

                ac[k++] = block[u, v];
            }
        }

        Array.Sort(ac);
        var median = ac[ac.Length / 2]; // 63 values -> index 31

        ulong hash = 0;
        var bit = 0;
        for (var u = 0; u < PhashLowFreq; u++)
        {
            for (var v = 0; v < PhashLowFreq; v++)
            {
                if (!(u == 0 && v == 0) && block[u, v] > median)
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    // Variance of the Laplacian - a standard blur metric (F9). Reads the resized
    // grayscale via LockBits (not GetPixel, which is far too slow per pixel over
    // thousands of images) and returns the variance of the 4-neighbour Laplacian
    // over the interior. Content-dependent - a legitimately smooth photo (sky,
    // solid backdrop) also scores low - so it's a review signal, never an
    // auto-delete.
    private static double ComputeSharpness(Image original)
    {
        const int n = SharpnessSize;
        var lum = new double[n * n];
        using (var small = new Bitmap(n, n, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(original, 0, 0, n, n);
            }

            var data = small.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * n];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (var y = 0; y < n; y++)
                {
                    var rowOffset = y * data.Stride;
                    for (var x = 0; x < n; x++)
                    {
                        var idx = rowOffset + (x * 4); // BGRA
                        lum[(y * n) + x] = (0.299 * bytes[idx + 2]) + (0.587 * bytes[idx + 1]) + (0.114 * bytes[idx]);
                    }
                }
            }
            finally
            {
                small.UnlockBits(data);
            }
        }

        double sum = 0, sumSq = 0;
        var count = 0;
        for (var y = 1; y < n - 1; y++)
        {
            for (var x = 1; x < n - 1; x++)
            {
                var c = (y * n) + x;
                var lap = lum[c - n] + lum[c + n] + lum[c - 1] + lum[c + 1] - (4 * lum[c]);
                sum += lap;
                sumSq += lap * lap;
                count++;
            }
        }

        if (count == 0)
        {
            return 0;
        }

        var mean = sum / count;
        return (sumSq / count) - (mean * mean);
    }

    /// <summary>The perceptual (DCT pHash) hash, the image's real pixel
    /// dimensions, a 4x4 average-colour grid, and a sharpness score (variance
    /// of the Laplacian; higher = sharper) - all from the single decode
    /// TryCompute already does.</summary>
    public readonly record struct PerceptualImageInfo(ulong Hash, int PixelWidth, int PixelHeight, byte[] ColorSignature, double Sharpness);

    /// <summary>Hash only, for callers that don't need dimensions/colour.</summary>
    public static ulong? TryComputeHash(string path) => TryCompute(path)?.Hash;

    /// <summary>The DCT pHash of an already-decoded bitmap (a video frame, F11) -
    /// same algorithm as TryCompute uses on a file. Lets the App layer hash the
    /// frames it extracts via Windows Media without going back through a path.</summary>
    public static ulong HashImage(Image image) => ComputePhash(image);

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    /// <summary>Sum of per-channel absolute differences between two colour
    /// signatures (0 = identical). Mismatched-length or null signatures return
    /// int.MaxValue so a missing signature never counts as a colour match.</summary>
    public static int ColorDistance(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return int.MaxValue;
        }

        var total = 0;
        for (var i = 0; i < a.Length; i++)
        {
            total += Math.Abs(a[i] - b[i]);
        }

        return total;
    }

    /// <summary>The largest single-cell colour difference (the worst 4x4 cell's
    /// sum of three channel absolute differences). Catches "same background,
    /// different-coloured subject" pairs a whole-image average distance lets
    /// through - the small subject barely moves the average when a large
    /// uniform background dominates. Null/mismatched signatures return
    /// int.MaxValue so a missing signature never counts as a match.</summary>
    public static int MaxCellColorDistance(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return int.MaxValue;
        }

        var max = 0;
        for (var i = 0; i + 2 < a.Length; i += 3)
        {
            var cell = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            if (cell > max)
            {
                max = cell;
            }
        }

        return max;
    }
}
