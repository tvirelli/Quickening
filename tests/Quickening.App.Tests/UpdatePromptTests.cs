using Quickening.App.Updates;
using Xunit;

namespace Quickening.App.Tests;

/// <summary>
/// 1.3.2: tell the user an update is ready and offer Restart to update,
/// instead of Velopack's installer appearing out of nowhere when they close
/// the app.
/// </summary>
public class UpdatePromptTests
{
    [Fact]
    public void Banner_NamesTheVersion()
    {
        Assert.Equal("Quickening 1.3.2 is ready to install", UpdatePrompt.BannerTitle("1.3.2"));
    }

    [Fact]
    public void Restart_IsAllowed_WhenNothingIsRunning()
    {
        Assert.Null(UpdatePrompt.RestartBlockedReason(scanActive: false, removalActive: false));
    }

    [Fact]
    public void Restart_Waits_ForAScanOrARemoval()
    {
        Assert.Equal("Finish your scan first, then restart to update.",
            UpdatePrompt.RestartBlockedReason(scanActive: true, removalActive: false));
        Assert.Equal("Wait for the removal to finish, then restart to update.",
            UpdatePrompt.RestartBlockedReason(scanActive: false, removalActive: true));
    }

    [Fact]
    public void Notification_OnlyWhenTheWindowIsHidden_OncePerVersion()
    {
        Assert.True(UpdatePrompt.ShouldNotify(windowVisible: false, version: "1.3.2", lastNotifiedVersion: null));
        Assert.False(UpdatePrompt.ShouldNotify(windowVisible: true, version: "1.3.2", lastNotifiedVersion: null));
        Assert.False(UpdatePrompt.ShouldNotify(windowVisible: false, version: "1.3.2", lastNotifiedVersion: "1.3.2"));
        Assert.True(UpdatePrompt.ShouldNotify(windowVisible: false, version: "1.3.3", lastNotifiedVersion: "1.3.2"));
    }
}
