using System.Runtime.InteropServices;
using Quickening.Core.Deletion;
using Xunit;

namespace Quickening.Core.Tests.Deletion;

public class RecycleBinServiceTests
{
    // Deliberately re-declares the same P/Invoke surface RecycleBinService
    // uses internally, rather than sharing it, so this test verifies the
    // real Recycle Bin's state independently of the class under test - not
    // by trusting its own counting logic. Do not "clean up" by extracting
    // a shared helper; that would defeat the point.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    private static long GetRecycleBinItemCount()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBinW(null, ref info);
        if (hr != 0)
        {
            throw new InvalidOperationException($"SHQueryRecycleBin failed with HRESULT 0x{hr:X8}.");
        }

        return info.i64NumItems;
    }

    [Fact]
    public void SendToRecycleBin_RemovesFileFromOriginalLocation_AndIncreasesRecycleBinCount()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "recycle me - RecycleBinServiceTests");

        var countBefore = GetRecycleBinItemCount();
        var service = new RecycleBinService();

        service.SendToRecycleBin(tempFile);

        Assert.False(File.Exists(tempFile), "file must be gone from its original location");

        var countAfter = GetRecycleBinItemCount();
        Assert.True(countAfter >= countBefore + 1, $"expected Recycle Bin item count to increase by at least 1 (was {countBefore}, now {countAfter})");

        // Take it back out: otherwise every run left one temp file in the
        // developer's real Recycle Bin.
        if (service.TryRestore(tempFile))
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SendToRecycleBin_Throws_ForNonexistentFile()
    {
        var service = new RecycleBinService();
        var missingPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".txt");

        Assert.ThrowsAny<IOException>(() => service.SendToRecycleBin(missingPath));
    }

    [Fact]
    public void SendToRecycleBin_Throws_ForUncPath()
    {
        var service = new RecycleBinService();
        // A syntactically-valid but unreachable UNC path - we're testing that
        // the network-location check rejects it before ever calling
        // SHFileOperationW, not that the path actually exists.
        var uncPath = @"\\nonexistent-server-for-testing\share\file.txt";

        Assert.Throws<IOException>(() => service.SendToRecycleBin(uncPath));
    }

    [Fact]
    public void TryRestore_BringsBackAFileJustSentToTheRecycleBin_ByThisSameInstance()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "restore me - RecycleBinServiceTests");
        var service = new RecycleBinService();
        service.SendToRecycleBin(tempFile);
        Assert.False(File.Exists(tempFile));

        var restored = service.TryRestore(tempFile);

        Assert.True(restored, "TryRestore should report success for a file this instance just recycled");
        Assert.True(File.Exists(tempFile), "the file should be back at its original path");

        File.Delete(tempFile);
    }

    [Fact]
    public void TryRestore_ReturnsFalse_ForAPathNeverSentToTheRecycleBinByThisInstance()
    {
        var service = new RecycleBinService();
        var neverDeletedPath = Path.Combine(Path.GetTempPath(), "never-recycled-" + Guid.NewGuid() + ".txt");

        Assert.False(service.TryRestore(neverDeletedPath));
    }

    [Fact]
    public void TryRestore_ReturnsFalse_WhenCalledTwiceForTheSameFile()
    {
        // A restored file's captured shell-item reference is consumed on
        // first use (TryRemove) - a second Undo click on a stale toast (or
        // any other double-invocation) must not attempt to restore the same
        // file twice, and definitely must not throw.
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "restore me twice - RecycleBinServiceTests");
        var service = new RecycleBinService();
        service.SendToRecycleBin(tempFile);

        Assert.True(service.TryRestore(tempFile));
        Assert.False(service.TryRestore(tempFile));

        File.Delete(tempFile);
    }

    [Fact]
    public void TryRestore_RepeatedRoundTrips_DoNotCorruptTheProcessHeap()
    {
        // Regression: TryRestore handed each enumerated Recycle Bin PIDL to
        // Vanara's owning PIDL wrapper AND freed it itself, a double free that
        // corrupted the heap. Nothing failed at the time - the process died
        // (0xC0000374) at the next heap churn or GC, which in the app was a
        // few seconds after every Undo and here was a later, unrelated test.
        // Forcing native and managed churn after each round makes damage
        // surface inside this test: a regression aborts the test host, and
        // the run's Total drops below the expected count.
        var service = new RecycleBinService();
        for (var round = 0; round < 10; round++)
        {
            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, $"heap round {round} - RecycleBinServiceTests");
            service.SendToRecycleBin(tempFile);

            Assert.True(service.TryRestore(tempFile));
            Assert.True(File.Exists(tempFile));
            File.Delete(tempFile);

            for (var i = 0; i < 2000; i++)
            {
                var block = Marshal.AllocHGlobal(64 + i % 512);
                Marshal.FreeHGlobal(block);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public void GetRecycleBinTotals_ReturnsNonNegativeCounts()
    {
        var service = new RecycleBinService();

        var (itemCount, totalSizeBytes) = service.GetRecycleBinTotals();

        Assert.True(itemCount >= 0);
        Assert.True(totalSizeBytes >= 0);
    }

    // EmptyRecycleBin is deliberately not exercised here - calling it for
    // real would actually empty the Windows Recycle Bin on whatever machine
    // runs this test suite, a genuinely destructive, irreversible action
    // with no safe way to verify or undo inside an automated test run.
}
