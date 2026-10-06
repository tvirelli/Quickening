using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Data.Sqlite;
using Quickening.Core.Orchestration;
using Quickening.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Quickening.Core.Tests;

// Measures whether the similarity phase is a HANG or just slow-with-no-progress,
// and whether a re-scan against cached rows that have PerceptualHash but NULL
// PixelWidth/PixelHeight (exactly the state an existing DB is in after the F3
// migration ALTER) still completes.
public class SimilarityScanScaleReproTests
{
    private readonly ITestOutputHelper _out;

    public SimilarityScanScaleReproTests(ITestOutputHelper output) => _out = output;

    private static void SaveBitmap(string path, int w, int h, Color color)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    [Fact]
    public void Scan_WithSimilarity_AtScale_ThenCachedWithNullDims_Completes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qk-scale-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(Path.GetTempPath(), "qk-scaledb-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(dir);
        try
        {
            const int count = 400;
            for (var i = 0; i < count; i++)
            {
                SaveBitmap(Path.Combine(dir, $"img_{i}.png"), 64, 64,
                    Color.FromArgb(255, i % 256, (i * 7) % 256, (i * 13) % 256));
            }

            using (var store = new SqliteStore($"Data Source={dbPath}"))
            {
                store.Initialize();
                var sw = Stopwatch.StartNew();
                new ScanOrchestrator(store).Scan(dir, computeSimilarity: true);
                _out.WriteLine($"pass1 (uncached, {count} images): {sw.ElapsedMilliseconds} ms");
            }

            // Mimic an existing DB's post-ALTER state: cached hashes, NULL dims.
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Files SET PixelWidth = NULL, PixelHeight = NULL;";
                _out.WriteLine($"nulled dims on {cmd.ExecuteNonQuery()} rows");
            }

            using (var store = new SqliteStore($"Data Source={dbPath}"))
            {
                store.Initialize();
                var sw = Stopwatch.StartNew();
                var result = new ScanOrchestrator(store).Scan(dir, computeSimilarity: true);
                _out.WriteLine($"pass2 (cached, null dims): {sw.ElapsedMilliseconds} ms");
                Assert.NotNull(result);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
            try { File.Delete(dbPath); } catch { }
        }
    }
}
