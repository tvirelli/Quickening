using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

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
    private const int Width = 9;
    private const int Height = 8;

    /// <summary>
    /// Returns null rather than throwing for anything that isn't a decodable
    /// raster image - a corrupt file, an unsupported format, or the file
    /// vanishing mid-scan should just be skipped for similarity purposes,
    /// not abort the whole scan the way DuplicateEngine's own per-file
    /// hash-failure isolation already treats equivalent failures elsewhere.
    /// </summary>
    public static ulong? TryComputeHash(string path)
    {
        try
        {
            using var original = Image.FromFile(path);
            using var resized = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(original, 0, 0, Width, Height);
            }

            var brightness = new double[Width, Height];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var pixel = resized.GetPixel(x, y);
                    brightness[x, y] = (0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B);
                }
            }

            ulong hash = 0;
            var bitIndex = 0;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width - 1; x++)
                {
                    if (brightness[x, y] < brightness[x + 1, y])
                    {
                        hash |= 1UL << bitIndex;
                    }

                    bitIndex++;
                }
            }

            return hash;
        }
        // Image.FromFile's well-known GDI+ quirk: an unrecognized/corrupt
        // image format surfaces as OutOfMemoryException, not a more
        // sensible exception type - not real memory exhaustion.
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
