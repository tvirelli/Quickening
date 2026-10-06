using Quickening.Core.Scanning;
using Xunit;

namespace Quickening.Core.Tests;

public class EmptyItemScannerTests : IDisposable
{
    private readonly string _root;

    public EmptyItemScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qk-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Find_ReportsEmptyFoldersAndZeroByteFiles_ButNotRealContent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "emptyDir"));
        Directory.CreateDirectory(Path.Combine(_root, "nested", "emptyChild"));
        Directory.CreateDirectory(Path.Combine(_root, "withZero"));
        File.WriteAllBytes(Path.Combine(_root, "withZero", "zero.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        File.WriteAllBytes(Path.Combine(_root, "real", "data.bin"), new byte[] { 1, 2, 3, 4, 5 });

        var result = new EmptyItemScanner().Find(_root);

        // Paths are asserted by suffix so HardBlockRules.NormalizePath's
        // canonicalization of the temp root can't cause a spurious mismatch.
        Assert.Contains(result.EmptyFolders, p => p.EndsWith(@"\emptyDir"));
        Assert.Contains(result.EmptyFolders, p => p.EndsWith(@"\nested\emptyChild"));
        Assert.Contains(result.EmptyFolders, p => p.EndsWith(@"\nested"));       // parent of an only-empty child
        Assert.Contains(result.EmptyFolders, p => p.EndsWith(@"\withZero"));     // holds only a zero-byte file
        Assert.Contains(result.ZeroByteFiles, p => p.EndsWith(@"\withZero\zero.txt"));

        Assert.DoesNotContain(result.EmptyFolders, p => p.EndsWith(@"\real"));
        Assert.DoesNotContain(result.ZeroByteFiles, p => p.EndsWith(@"\data.bin"));
        Assert.False(result.IsEmpty);
    }

    // QA-1: a subfolder the scan can't read was treated as EMPTY, so its
    // parent - which really holds data inside that subfolder - was offered in
    // Tidy up as an empty folder. Unreadable must count as content.
    [Fact]
    public void Find_TreatsAnUnreadableSubfolderAsContent_SoItsParentIsNotReportedEmpty()
    {
        var parent = Path.Combine(_root, "parent");
        var locked = Path.Combine(parent, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "precious.txt"), "real data");

        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var denyList = new System.Security.AccessControl.FileSystemAccessRule(
            user, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        security.AddAccessRule(denyList);
        info.SetAccessControl(security);
        try
        {
            var result = new EmptyItemScanner().Find(_root);

            Assert.DoesNotContain(result.EmptyFolders, p => p.EndsWith(@"\parent"));
            Assert.DoesNotContain(result.EmptyFolders, p => p.EndsWith(@"\parent\locked"));
        }
        finally
        {
            security.RemoveAccessRule(denyList);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public void Find_OnAllRealContent_ReturnsNothing()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), new byte[] { 9 });

        var result = new EmptyItemScanner().Find(_root);

        Assert.True(result.IsEmpty);
    }
}
