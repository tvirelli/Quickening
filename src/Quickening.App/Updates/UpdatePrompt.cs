namespace Quickening.App.Updates;

/// <summary>
/// Pure decisions for the "update ready" prompt (1.3.2). Before this, an
/// update downloaded silently and Velopack's installer window appeared out of
/// nowhere when the user closed the app. Now a banner says it's ready and
/// offers Restart to update. No Velopack/WinUI dependency, so it is
/// unit-tested directly.
/// </summary>
public static class UpdatePrompt
{
    public static string BannerTitle(string version) => $"Quickening {version} is ready to install";

    /// <summary>
    /// Why Restart to update has to wait, or null when it can go now. A
    /// restart mid-scan loses the scan; mid-removal it would stop a batch
    /// halfway through.
    /// </summary>
    public static string? RestartBlockedReason(bool scanActive, bool removalActive) =>
        scanActive ? "Finish your scan first, then restart to update."
        : removalActive ? "Wait for the removal to finish, then restart to update."
        : null;

    /// <summary>
    /// The Windows notification is for users who keep Quickening in the tray
    /// (window hidden, still watching for duplicates) - they'd never see the
    /// banner. One per version, so a recheck doesn't nag.
    /// </summary>
    public static bool ShouldNotify(bool windowVisible, string version, string? lastNotifiedVersion) =>
        !windowVisible && !string.Equals(version, lastNotifiedVersion, System.StringComparison.Ordinal);
}
