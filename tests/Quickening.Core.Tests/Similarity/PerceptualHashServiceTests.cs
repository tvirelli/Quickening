using System.Drawing;
using System.Drawing.Imaging;
using Quickening.Core.Similarity;
using Xunit;

namespace Quickening.Core.Tests.Similarity;

public class PerceptualHashServiceTests
{
    private static string SaveTestImage(Action<Bitmap> paint)
    {
        using var bitmap = new Bitmap(64, 64);
        paint(bitmap);
        var path = Path.Combine(Path.GetTempPath(), $"qk-hash-test-{Guid.NewGuid()}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    // A left-to-right gradient (not a flat fill) - dHash compares each pixel
    // to its right-hand neighbor, so a flat single-color image would hash
    // to all-zero regardless of which color, making it useless for telling
    // "identical" and "totally different" images apart in this test.
    private static string SaveGradientImage(bool reversed = false)
    {
        return SaveTestImage(bitmap =>
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var value = reversed ? 255 - (x * 255 / bitmap.Width) : x * 255 / bitmap.Width;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(value, value, value));
                }
            }
        });
    }

    [Fact]
    public void TryComputeHash_ReturnsSameHash_ForIdenticalImages()
    {
        var pathA = SaveGradientImage();
        var pathB = SaveGradientImage();
        try
        {
            var hashA = PerceptualHashService.TryComputeHash(pathA);
            var hashB = PerceptualHashService.TryComputeHash(pathB);

            Assert.NotNull(hashA);
            Assert.NotNull(hashB);
            Assert.Equal(hashA, hashB);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void TryComputeHash_ReturnsFarApartHashes_ForOppositeGradients()
    {
        var pathA = SaveGradientImage();
        var pathB = SaveGradientImage(reversed: true);
        try
        {
            var hashA = PerceptualHashService.TryComputeHash(pathA)!.Value;
            var hashB = PerceptualHashService.TryComputeHash(pathB)!.Value;

            var distance = PerceptualHashService.HammingDistance(hashA, hashB);
            Assert.True(distance > 30, $"expected a large Hamming distance for opposite gradients, got {distance}");
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    // A structured "scene" (real 2D low-frequency content) rendered at a given
    // size. Unlike a flat fill or a 1D gradient - which have almost no frequency
    // structure, so pHash bits near the median are noise - this has genuine
    // shapes, the case pHash is meant to handle: two resolutions of the same
    // scene must hash close together (F2's whole point; the old dHash was far
    // less reliable here).
    private static string SaveSceneImage(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(40, 40, 40));
            g.FillRectangle(Brushes.White, 0, 0, size / 2f, size / 2f);
            g.FillEllipse(Brushes.DimGray, size / 4f, size / 4f, size / 2f, size / 2f);
            g.FillRectangle(Brushes.Black, size * 0.6f, size * 0.6f, size / 3f, size / 3f);
        }

        var path = Path.Combine(Path.GetTempPath(), $"qk-hash-test-{Guid.NewGuid()}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public void TryComputeHash_IsRobustToResize_SmallDistanceForSameSceneRescaled()
    {
        var pathSmall = SaveSceneImage(64);
        var pathLarge = SaveSceneImage(160);
        try
        {
            var a = PerceptualHashService.TryComputeHash(pathSmall)!.Value;
            var b = PerceptualHashService.TryComputeHash(pathLarge)!.Value;

            var distance = PerceptualHashService.HammingDistance(a, b);
            Assert.True(distance <= SimilarityEngine.DefaultMaxHammingDistance,
                $"expected a small distance for the same scene rescaled, got {distance}");
        }
        finally
        {
            File.Delete(pathSmall);
            File.Delete(pathLarge);
        }
    }

    [Fact]
    public void Sharpness_IsNearZeroForFlatImage_AndHigherForStructured()
    {
        // A flat fill has no edges -> Laplacian variance ~0 (maximally "blurry").
        // A structured scene has real edges -> clearly higher. (F9)
        var flat = SaveTestImage(b =>
        {
            using var g = Graphics.FromImage(b);
            g.Clear(Color.Gray);
        });
        var structured = SaveSceneImage(128);
        try
        {
            var flatSharp = PerceptualHashService.TryCompute(flat)!.Value.Sharpness;
            var structuredSharp = PerceptualHashService.TryCompute(structured)!.Value.Sharpness;

            Assert.True(flatSharp < 5, $"flat image sharpness should be ~0, got {flatSharp}");
            Assert.True(structuredSharp > flatSharp,
                $"structured sharpness {structuredSharp} should exceed flat {flatSharp}");
        }
        finally
        {
            File.Delete(flat);
            File.Delete(structured);
        }
    }

    [Fact]
    public void TryComputeHash_ReturnsNull_ForNonImageFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qk-hash-test-{Guid.NewGuid()}.txt");
        File.WriteAllText(path, "not an image");
        try
        {
            Assert.Null(PerceptualHashService.TryComputeHash(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryComputeHash_ReturnsNull_ForNonexistentFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qk-hash-test-does-not-exist-{Guid.NewGuid()}.jpg");

        Assert.Null(PerceptualHashService.TryComputeHash(path));
    }

    [Fact]
    public void HammingDistance_IsZero_ForEqualHashes()
    {
        Assert.Equal(0, PerceptualHashService.HammingDistance(12345UL, 12345UL));
    }

    [Fact]
    public void HammingDistance_CountsDifferingBits()
    {
        Assert.Equal(2, PerceptualHashService.HammingDistance(0b0000UL, 0b0011UL));
    }
}
