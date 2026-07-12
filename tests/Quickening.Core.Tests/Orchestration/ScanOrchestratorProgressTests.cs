using System.Linq;
using Quickening.Core.Orchestration;
using Quickening.Core.Storage;
using Quickening.Tests.Shared;
using Xunit;

namespace Quickening.Core.Tests.Orchestration;

public class ScanOrchestratorProgressTests
{
    [Fact]
    public void Scan_ReportsProgressOncePerFile_WithIncreasingCount()
    {
        var tempDir = Directory.CreateTempSubdirectory("quickening-scan-progress-test");
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "a.txt"), "aaa");
            File.WriteAllText(Path.Combine(tempDir.FullName, "b.txt"), "bbb");
            File.WriteAllText(Path.Combine(tempDir.FullName, "c.txt"), "ccc");

            using var store = new SqliteStore("Data Source=:memory:");
            store.Initialize();
            var orchestrator = new ScanOrchestrator(store);

            var reports = new List<ScanProgress>();
            var progress = new SynchronousProgress<ScanProgress>(reports.Add);

            orchestrator.Scan(tempDir.FullName, progress);

            // Enumeration progress is throttled (~50ms) rather than per-file,
            // but the first file always reports immediately and the final
            // report always lands with the full count - and the counter must
            // never move backwards (reports are relayed synchronously, in
            // order).
            var enumerationReports = reports.Where(r => r.Phase == ScanPhase.Enumerating).ToList();
            Assert.NotEmpty(enumerationReports);
            Assert.Equal(1, enumerationReports[0].FilesProcessed);
            Assert.Equal(3, enumerationReports[^1].FilesProcessed);
            for (var i = 1; i < enumerationReports.Count; i++)
            {
                Assert.True(enumerationReports[i].FilesProcessed >= enumerationReports[i - 1].FilesProcessed);
            }

            Assert.All(enumerationReports, r => Assert.False(string.IsNullOrEmpty(r.CurrentPath)));
            Assert.All(enumerationReports, r => Assert.Null(r.TotalFiles));

            // All three files are the same size ("aaa"/"bbb"/"ccc" are each
            // 3 bytes), so they all land in one size-group and each gets
            // partial-hashed - the comparing phase is throttled too, but its
            // final "all files done" report is always delivered.
            var comparingReports = reports.Where(r => r.Phase == ScanPhase.Comparing).ToList();
            Assert.NotEmpty(comparingReports);
            Assert.All(comparingReports, r => Assert.Equal(3, r.TotalFiles));
            Assert.Equal(3, comparingReports[^1].FilesProcessed);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Scan_WithNoProgressArgument_StillCompletesNormally()
    {
        var tempDir = Directory.CreateTempSubdirectory("quickening-scan-progress-test");
        try
        {
            File.WriteAllText(Path.Combine(tempDir.FullName, "a.txt"), "aaa");

            using var store = new SqliteStore("Data Source=:memory:");
            store.Initialize();
            var orchestrator = new ScanOrchestrator(store);

            var result = orchestrator.Scan(tempDir.FullName);

            Assert.Equal(1, result.TotalFilesScanned);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
