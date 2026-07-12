using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Quickening.App.Settings;
using Quickening.App.Views;
using Quickening.Core.Orchestration;
using Quickening.Core.Storage;

namespace Quickening.App.Tray;

/// <summary>
/// Automation group's "Scheduled scan" setting - an in-process timer, not
/// a Windows Task Scheduler registration (keeps this self-contained; see
/// the Phase 6 plan notes), that only ever fires while this process is
/// alive. Independent of TrayIconService/DuplicateWatcherService - a user
/// can turn this on without ever enabling "Watch for new duplicates", so it
/// is started/stopped purely from ScheduledScanEnabled, never from the
/// Watch toggle. Defers to ScanCoordinator: a tick that lands while the
/// user is running their own scan simply skips and retries next poll.
/// </summary>
public sealed class ScheduledScanService : IDisposable
{
    // Coarse polling interval rather than computing the exact due time and
    // sleeping until then - simpler, and 15 minutes of slop against a
    // daily/weekly cadence is not user-visible.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly SqliteStore _store;
    private Timer? _timer;
    private int _isRunning;
    private IDisposable? _notificationSubscription;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Everything the "Review" toast action needs, captured as ONE immutable
    /// object - four separate fields written on the timer thread and read on
    /// the notification thread could be observed mid-update (e.g. scan B's
    /// null threshold with scan A's LargeFiles flag → NullReferenceException
    /// on the UI thread).
    /// </summary>
    private sealed record ScheduledScanOutcome(
        ScanResult Result,
        bool IsLargeFilesMode,
        long? ThresholdBytes,
        string TargetPath);

    // Scoped to this process run only, same posture as RecycleBinService's
    // restore map (Phase 4) - a "Review" toast action is only ever offered
    // for the scan that just ran in this session.
    private volatile ScheduledScanOutcome? _lastOutcome;

    public ScheduledScanService(SqliteStore store)
    {
        _store = store;
    }

    public void Start()
    {
        Stop();

        // Via App.Notifications, not the WinRT event - subscribing to
        // AppNotificationManager after Register() throws (see
        // NotificationDispatcher's doc comment).
        _notificationSubscription = App.Notifications.Subscribe(OnNotificationInvoked);

        _cts = new CancellationTokenSource();
        _timer = new Timer(_ => CheckAndRunIfDue(), null, PollInterval, PollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;

        // Timer.Dispose (the no-WaitHandle overload) does NOT wait for an
        // in-flight callback; cancelling the token is what actually makes a
        // long-running scheduled scan wind down instead of racing app
        // shutdown for the store connection.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _notificationSubscription?.Dispose();
        _notificationSubscription = null;
    }

    public void Dispose() => Stop();

    private void CheckAndRunIfDue()
    {
        // Interlocked, not a plain bool check-then-set: with a scan that can
        // outlive the 15-minute poll period, the guard is the only thing
        // between one scheduled scan and two.
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        var claimedScanSlot = false;
        try
        {
            var settings = App.Settings;
            var cancellationToken = _cts?.Token ?? CancellationToken.None;
            if (cancellationToken.IsCancellationRequested
                || !settings.ScheduledScanEnabled
                || string.IsNullOrEmpty(settings.ScheduledScanTargetPath)
                || !Directory.Exists(settings.ScheduledScanTargetPath))
            {
                return;
            }

            var interval = settings.ScheduledScanFrequency == ScheduledScanFrequency.Daily
                ? TimeSpan.FromDays(1)
                : TimeSpan.FromDays(7);
            if (settings.LastScheduledScanRunUtc is { } last && DateTime.UtcNow - last < interval)
            {
                return;
            }

            // The user's own scan wins; try again next poll.
            if (!ScanCoordinator.TryBegin())
            {
                return;
            }

            claimedScanSlot = true;
            RunScan(settings, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // App shutdown or Stop() mid-scan - not a failure.
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Scheduled scan failed: {ex}");
        }
        finally
        {
            if (claimedScanSlot)
            {
                ScanCoordinator.End();
            }

            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    private void RunScan(AppSettings settings, CancellationToken cancellationToken)
    {
        var targetPath = settings.ScheduledScanTargetPath!;
        var orchestrator = new ScanOrchestrator(_store);
        var isLargeFilesMode = settings.ScheduledScanMode == ScheduledScanMode.LargeFiles;

        ScanResult result;
        long? thresholdBytes = null;
        if (isLargeFilesMode)
        {
            // Same 100 MB default HomePage's own ThresholdSlider starts at -
            // a scheduled scan has no UI moment to ask the user for a
            // threshold, so it uses that same out-of-the-box default.
            // excludeCloudPlaceholders: true for the same no-UI-moment
            // reason - interactive scans warn before hydrating online-only
            // files; a background scan must never silently download
            // gigabytes of cloud content overnight.
            thresholdBytes = HomePage.DefaultLargeFileThresholdBytes;
            result = orchestrator.ScanForLargeFiles(
                targetPath,
                includeHiddenFiles: settings.ScanHiddenFiles,
                allowProtectedPaths: settings.AllowProtectedPaths,
                excludeCloudPlaceholders: true,
                cancellationToken: cancellationToken);
        }
        else
        {
            result = orchestrator.Scan(
                targetPath,
                includeHiddenFiles: settings.ScanHiddenFiles,
                allowProtectedPaths: settings.AllowProtectedPaths,
                paranoidMode: settings.ParanoidMode,
                excludeCloudPlaceholders: true,
                cancellationToken: cancellationToken);
        }

        // Settings are a plain POCO the UI thread mutates freely; touching
        // (and serializing) them from this timer thread races the UI - e.g.
        // JsonSerializer enumerating WatchedFolderPaths while SettingsDialog
        // adds an entry throws. All mutation+save stays on the UI thread.
        var persisted = App.MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
        {
            App.Settings.LastScheduledScanRunUtc = DateTime.UtcNow;
            App.SaveSettings();
        }) ?? false;
        if (!persisted)
        {
            // No dispatcher (shutdown race) - at least record the run
            // in-memory so the next 15-minute tick doesn't immediately
            // re-run a scan that just finished; it persists with the next
            // UI-thread save.
            settings.LastScheduledScanRunUtc = DateTime.UtcNow;
        }

        _lastOutcome = new ScheduledScanOutcome(result, isLargeFilesMode, thresholdBytes, targetPath);

        ShowCompletionToast(result, isLargeFilesMode, thresholdBytes, targetPath);
    }

    private static void ShowCompletionToast(ScanResult result, bool isLargeFilesMode, long? thresholdBytes, string targetPath)
    {
        var folderLabel = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderLabel))
        {
            folderLabel = targetPath;
        }

        string body;
        if (isLargeFilesMode)
        {
            var overThreshold = result.AllFiles?.Count(f => f.SizeBytes >= thresholdBytes!.Value) ?? 0;
            body = overThreshold == 0
                ? $"No large files found in {folderLabel}."
                : $"Found {overThreshold} large file(s) in {folderLabel}.";
        }
        else
        {
            var groupCount = result.DuplicateGroups.Count;
            body = groupCount == 0
                ? $"No duplicates found in {folderLabel}."
                : $"Found {groupCount} duplicate group(s) in {folderLabel}.";
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText("Scheduled scan complete")
                .AddText(body);

            var hasFindings = isLargeFilesMode
                ? (result.AllFiles?.Any(f => f.SizeBytes >= thresholdBytes!.Value) ?? false)
                : result.DuplicateGroups.Count > 0;
            if (hasFindings)
            {
                builder.AddButton(new AppNotificationButton("Review").AddArgument("action", "reviewScheduledScan"));
            }

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Failed to show scheduled-scan completion toast: {ex}");
        }
    }

    private void OnNotificationInvoked(AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out var action) || action != "reviewScheduledScan")
        {
            return;
        }

        // One volatile read; every field used below comes from this single
        // immutable snapshot, so a scan finishing concurrently can't mix
        // two scans' state.
        var outcome = _lastOutcome;
        if (outcome is null)
        {
            return;
        }

        App.MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
        {
            var window = App.MainWindowInstance!;
            H.NotifyIcon.WindowExtensions.Show(window);
            H.NotifyIcon.WindowExtensions.ShowInTaskbar(window);

            var mainWindow = (MainWindow)window;
            if (outcome.IsLargeFilesMode)
            {
                var candidates = (outcome.Result.AllFiles ?? Array.Empty<Core.Models.FileRecord>())
                    .Where(f => f.SizeBytes >= outcome.ThresholdBytes!.Value)
                    .Select(f => new ViewModels.SelectableFile
                    {
                        Path = f.Path,
                        SizeBytes = f.SizeBytes,
                        Category = f.Category,
                        LastWriteTimeUtc = f.LastWriteTimeUtc,
                    })
                    .ToList();
                mainWindow.ShowLargeFilesResults(candidates, outcome.ThresholdBytes!.Value, outcome.Result, outcome.TargetPath);
            }
            else
            {
                mainWindow.ShowResults(outcome.Result, targetLabel: outcome.TargetPath);
            }
        });
    }
}
