using Quickening.Core.Shell;
using Xunit;

namespace Quickening.Core.Tests.Shell;

public class DefaultAppResolverTests
{
    // QA-9: an extension nothing is registered for resolves to the Open With
    // picker, whose friendly name "Pick an app" read as an app name in
    // `Open "report.bin" in Pick an app?`. It must come back as no name.
    [Fact]
    public void FriendlyAppName_ReturnsNull_ForAnExtensionWithNoRegisteredApp()
    {
        var unregistered = "file." + Guid.NewGuid().ToString("N")[..10];

        Assert.Null(DefaultAppResolver.FriendlyAppName(unregistered));
    }

    [Fact]
    public void FriendlyAppName_ReturnsNull_ForAFileWithNoExtension()
    {
        Assert.Null(DefaultAppResolver.FriendlyAppName(@"C:\folder\README"));
    }
}
