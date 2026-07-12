using Quickening.Core.Safety;
using Xunit;

namespace Quickening.Core.Tests.Safety;

public class HardBlockRulesTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Program Files\SomeApp\app.exe")]
    [InlineData(@"C:\Program Files (x86)\SomeApp\app.exe")]
    [InlineData(@"C:\ProgramData\SomeApp\config.dat")]
    public void IsHardBlocked_ReturnsTrue_ForSystemPaths(string path)
    {
        Assert.True(HardBlockRules.IsHardBlocked(path));
    }

    [Theory]
    [InlineData(@"C:\Users\Tony\Downloads\movie.mp4")]
    [InlineData(@"D:\Photos\vacation.jpg")]
    public void IsHardBlocked_ReturnsFalse_ForOrdinaryUserPaths(string path)
    {
        Assert.False(HardBlockRules.IsHardBlocked(path));
    }

    [Fact]
    public void IsHardBlocked_ReturnsTrue_ForHiddenSystemAttributeFile()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.SetAttributes(tempFile, FileAttributes.Hidden | FileAttributes.System);
            Assert.True(HardBlockRules.IsHardBlocked(tempFile));
        }
        finally
        {
            File.SetAttributes(tempFile, FileAttributes.Normal);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void IsHardBlocked_ReturnsFalse_ForHiddenOnlyAttributeFile()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.SetAttributes(tempFile, FileAttributes.Hidden);
            Assert.False(HardBlockRules.IsHardBlocked(tempFile));
        }
        finally
        {
            File.SetAttributes(tempFile, FileAttributes.Normal);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void IsHardBlocked_ReturnsFalse_ForSystemOnlyAttributeFile()
    {
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.SetAttributes(tempFile, FileAttributes.System);
            Assert.False(HardBlockRules.IsHardBlocked(tempFile));
        }
        finally
        {
            File.SetAttributes(tempFile, FileAttributes.Normal);
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Program Files\SomeApp\app.exe")]
    public void IsHardBlocked_ReturnsFalse_ForSystemPaths_WhenAllowProtectedPathsIsTrue(string path)
    {
        Assert.False(HardBlockRules.IsHardBlocked(path, allowProtectedPaths: true));
    }

    [Fact]
    public void IsHardBlocked_ReturnsTrue_ForHiddenSystemAttributeFile_EvenWhenAllowProtectedPathsIsTrue()
    {
        // allowProtectedPaths only bypasses the BlockedRoots location check -
        // the hidden+system attribute marker is never bypassable.
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            File.SetAttributes(tempFile, FileAttributes.Hidden | FileAttributes.System);
            Assert.True(HardBlockRules.IsHardBlocked(tempFile, allowProtectedPaths: true));
        }
        finally
        {
            File.SetAttributes(tempFile, FileAttributes.Normal);
            File.Delete(tempFile);
        }
    }
}
