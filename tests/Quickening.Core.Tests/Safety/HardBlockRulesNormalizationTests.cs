using Quickening.Core.Safety;
using Xunit;

namespace Quickening.Core.Tests.Safety;

/// <summary>
/// Regression tests for the path-normalization hardening: \\?\ prefixes and
/// 8.3 short names must not slip past the BlockedRoots prefix comparison.
/// </summary>
public class HardBlockRulesNormalizationTests
{
    [Fact]
    public void NormalizePath_StripsExtendedLengthPrefix()
    {
        Assert.Equal(@"C:\Windows\System32", HardBlockRules.NormalizePath(@"\\?\C:\Windows\System32"));
    }

    [Fact]
    public void NormalizePath_StripsUncExtendedPrefix()
    {
        Assert.Equal(@"\\server\share\file.txt", HardBlockRules.NormalizePath(@"\\?\UNC\server\share\file.txt"));
    }

    [Fact]
    public void IsHardBlocked_RejectsExtendedLengthWindowsPath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(HardBlockRules.IsHardBlocked(@"\\?\" + windows + @"\System32\notepad.exe"));
    }

    [Fact]
    public void IsHardBlocked_RejectsShortNameProgramFiles()
    {
        // C:\PROGRA~1 is the conventional 8.3 alias for C:\Program Files;
        // skip gracefully on systems with 8.3 generation disabled (the
        // short path simply won't resolve there).
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var root = Path.GetPathRoot(programFiles)!;
        var shortPath = Path.Combine(root, "PROGRA~1");
        if (!Directory.Exists(shortPath))
        {
            return;
        }

        Assert.True(HardBlockRules.IsHardBlocked(shortPath + @"\anything.dll"));
    }

    [Fact]
    public void IsHardBlocked_StillAllowsOrdinaryUserPaths()
    {
        Assert.False(HardBlockRules.IsHardBlocked(Path.Combine(Path.GetTempPath(), "quickening-test.txt")));
    }

    [Fact]
    public void IsUnderBlockedRoot_DoesNotFalsePositiveOnSiblingPrefix()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // "C:\Windows.old" starts with "C:\Windows" as a string but is not
        // under it.
        Assert.False(HardBlockRules.IsUnderBlockedRoot(windows + ".old"));
    }

    [Theory]
    [InlineData(@"\\?\Volume{ac54ef13-0000-0000-0000-100000000000}\Windows\System32\x.dll")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Windows\System32\x.dll")]
    [InlineData(@"\\.\Volume{ac54ef13-0000-0000-0000-100000000000}\x.dll")]
    public void IsHardBlocked_FailsClosed_OnUnresolvableDevicePaths(string path)
    {
        // Volume-GUID / GLOBALROOT device forms can name a real protected file
        // yet can't be canonicalised to a drive path, so they slip past the
        // blocked-root prefix compare - must be blocked (fail closed).
        Assert.True(HardBlockRules.IsHardBlocked(path));
    }

    [Fact]
    public void IsHardBlocked_DevicePath_HonorsProtectedPathsOptOut()
    {
        // The device-path guard respects the same opt-out as the blocked-root
        // check (a nonexistent volume then falls through to a normal false).
        Assert.False(HardBlockRules.IsHardBlocked(
            @"\\?\Volume{ac54ef13-0000-0000-0000-100000000000}\x.dll", allowProtectedPaths: true));
    }
}
