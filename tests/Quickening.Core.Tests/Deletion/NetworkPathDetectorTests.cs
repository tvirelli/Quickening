using Quickening.Core.Deletion;
using Xunit;

namespace Quickening.Core.Tests.Deletion;

public class NetworkPathDetectorTests
{
    [Theory]
    [InlineData(@"\\nas\photos\file.jpg")]
    [InlineData(@"\\192.168.1.1\share\file.txt")]
    public void IsNetworkPath_ReturnsTrue_ForUncPaths(string path)
    {
        Assert.True(NetworkPathDetector.IsNetworkPath(path));
    }

    [Fact]
    public void IsNetworkPath_ReturnsFalse_ForLocalFixedDrivePath()
    {
        // C:\ is a fixed local drive on any machine this test suite runs on
        // (dev box or CI) - not a mapped network drive, so this exercises
        // the GetDriveTypeW fallback path (not the UNC fast path above)
        // without depending on any specific network configuration.
        Assert.False(NetworkPathDetector.IsNetworkPath(@"C:\Users\someone\file.txt"));
    }
}
