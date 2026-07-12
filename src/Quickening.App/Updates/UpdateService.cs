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
/// Releases of the project. Checks on launch (fire-and-forget), stages any
/// newer build's delta, and applies it when the app next exits so the swap
/// happens between sessions. Every path is guarded: offline, GitHub
/// unreachable, "no releases yet", or running from a non-installed (dev) build
/// all no-op and are logged, never thrown.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/tvirelli/Quickening";

    private readonly ILogger? _logger;
    private readonly UpdateManager _manager;
    private UpdateInfo? _staged;

    public UpdateService(ILogger? logger)
    {
        _logger = logger;
        // prerelease: false - only stable releases are offered to users.
        _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
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

        await _manager.DownloadUpdatesAsync(info);
        _staged = info;
        _logger?.LogInfo($"Update staged: {info.TargetFullRelease.Version}");
        return UpdateCheckResult.UpdateStaged;
    }

    /// <summary>
    /// If an update is staged, hand it to Velopack's updater to apply after
    /// this process exits. restart: false - the user relaunches on their own
    /// schedule and gets the new build then; we never force a restart.
    /// </summary>
    public void ApplyPendingOnExit()
    {
        if (_staged is null)
        {
            return;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(_staged.TargetFullRelease, silent: false, restart: false);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Applying staged update on exit failed", ex);
        }
    }
}
