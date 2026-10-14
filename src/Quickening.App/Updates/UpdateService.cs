using System;
using System.Threading.Tasks;
using Quickening.Core.Logging;
using Velopack;
using Velopack.Sources;

namespace Quickening.App.Updates;

public enum UpdateCheckResult
{
    UpToDate,
    UpdateStaged,
    Failed,
}

/// <summary>
/// Thin wrapper over Velopack's UpdateManager pointed at the public GitHub
/// Releases of the project. Checks on launch and every few hours while running
/// (fire-and-forget), stages any newer build's delta, then raises UpdateReady
/// so the app can offer Restart to update (1.3.2). If the user leaves it, the
/// update installs quietly when the app next exits. Every path is guarded:
/// offline, GitHub unreachable, "no releases yet", or running from a
/// non-installed (dev) build all no-op and are logged, never thrown.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/tvirelli/Quickening";

    private readonly ILogger? _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _staged;

    /// <summary>
    /// Raised with the new version once an update has downloaded and is ready
    /// to install. Raised on whatever thread the check ran on - subscribers
    /// marshal to the UI thread themselves.
    /// </summary>
    public event Action<string>? UpdateReady;

    /// <summary>The version downloaded and waiting to install, or null.</summary>
    public string? StagedVersion => _staged?.TargetFullRelease.Version.ToString();

    public UpdateService(ILogger? logger)
    {
        _logger = logger;
        // prerelease: false - only stable releases are offered to users.
        // QUICKENING_UPDATE_FEED (a local folder of vpk output) is for testing
        // the update flow end to end without publishing a release.
        var testFeed = Environment.GetEnvironmentVariable("QUICKENING_UPDATE_FEED");
        IUpdateSource source = string.IsNullOrEmpty(testFeed)
            ? new GithubSource(RepoUrl, null, false)
            : new SimpleFileSource(new System.IO.DirectoryInfo(testFeed));
        _manager = new UpdateManager(source);
    }

    /// <summary>
    /// The running version. When launched from a Velopack install this is the
    /// installed package version; from a dev build (not installed) it falls
    /// back to the assembly version so Settings still shows something sane.
    /// </summary>
    public string CurrentVersion =>
        _manager.IsInstalled && _manager.CurrentVersion is { } v
            ? v.ToString()
            : typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public async Task CheckAndStageAsync()
    {
        try
        {
            await StageAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError("Background update check failed", ex);
        }
    }

    public async Task<UpdateCheckResult> CheckNowAsync()
    {
        try
        {
            return await StageAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError("Manual update check failed", ex);
            return UpdateCheckResult.Failed;
        }
    }

    private async Task<UpdateCheckResult> StageAsync()
    {
        if (!_manager.IsInstalled)
        {
            _logger?.LogInfo("Update check skipped: not a Velopack install (dev run).");
            return UpdateCheckResult.UpToDate;
        }

        var info = await _manager.CheckForUpdatesAsync();
        if (info is null)
        {
            return UpdateCheckResult.UpToDate;
        }

        // Already downloaded (a later periodic or manual check found the same
        // release): nothing to fetch, and the user has already been told.
        if (_staged is not null && _staged.TargetFullRelease.Version == info.TargetFullRelease.Version)
        {
            return UpdateCheckResult.UpdateStaged;
        }

        await _manager.DownloadUpdatesAsync(info);
        _staged = info;
        _logger?.LogInfo($"Update staged: {info.TargetFullRelease.Version}");
        UpdateReady?.Invoke(info.TargetFullRelease.Version.ToString());
        return UpdateCheckResult.UpdateStaged;
    }

    /// <summary>
    /// Restart to update: hands the staged update to Velopack's updater, which
    /// waits for this process to exit, installs, and relaunches the app. This
    /// call exits the process. The caller shuts down the app's services first.
    /// </summary>
    public void RestartToApply()
    {
        if (_staged is null)
        {
            return;
        }

        _manager.ApplyUpdatesAndRestart(_staged.TargetFullRelease);
    }

    /// <summary>
    /// If an update is staged and the user chose not to restart for it, hand it
    /// to Velopack's updater to apply after this process exits. silent: true -
    /// the user was already told it's ready (the banner), so no installer window
    /// pops up as they close the app. restart: false - they relaunch on their
    /// own schedule and get the new build then.
    /// </summary>
    public void ApplyPendingOnExit()
    {
        if (_staged is null)
        {
            return;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(_staged.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Applying staged update on exit failed", ex);
        }
    }
}
