using System.Drawing;
using System.Drawing.Imaging;
using Quickening.Core.Orchestration;
using Quickening.Core.Storage;
using Xunit;

namespace Quickening.Core.Tests;

// Repro harness for the "stuck on Finalizing when similar-photos is on" report.
// If Scan(computeSimilarity: true) hangs, this test hangs (and the run times
// out) - proving a logic hang. If it returns quickly, the field symptom is
// scale/slowness (no progress on the perceptual phase), not a deadlock.
public class SimilarityScanReproTests : IDisposable
{
    private readonly string _dir;

    public SimilarityScanReproTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qk-sim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

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
    public void Scan_WithSimilarity_CompletesAndCapturesDimensions()
    {
        SaveBitmap(Path.Combine(_dir, "a.png"), 64, 64, Color.Red);
        SaveBitmap(Path.Combine(_dir, "a-resized.png"), 48, 48, Color.Red);
        SaveBitmap(Path.Combine(_dir, "b.png"), 64, 64, Color.Blue);

        using var store = new SqliteStore("Data Source=:memory:");
        store.Initialize();

        var result = new ScanOrchestrator(store).Scan(_dir, computeSimilarity: true);

        Assert.NotNull(result);

        // Re-read a record to confirm dimensions round-tripped through the store.
        var rec = store.GetFileByPath(Path.Combine(_dir, "a.png"));
        Assert.NotNull(rec);
        Assert.Equal(64, rec!.PixelWidth);
        Assert.Equal(64, rec.PixelHeight);
    }
}
