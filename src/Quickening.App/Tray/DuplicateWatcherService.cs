using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Quickening.App.Views;
using Quickening.Core.Deletion;
using Quickening.Core.Hashing;
using Quickening.Core.Safety;
using Quickening.Core.Storage;

namespace Quickening.App.Tray;

/// <summary>
/// Background half of mockup 4l - one FileSystemWatcher per
/// Settings.WatchedFolderPaths entry, hashing each newly-arrived file
/// (reusing the same CachingHashProvider/HashProvider pipeline a scan
/// uses) and checking it against SqliteStore for a prior match. A hit is
/// re-verified on disk (the store can hold rows for files deleted long ago)
/// before surfacing a toast with the mockup's three actions via
/// Microsoft.Windows.AppNotifications - not TaskbarIcon.ShowNotification,
/// which only supports plain title+body text and has no room for custom
/// action buttons. Owned by App (started/stopped alongside TrayIconService,
/// from the same Settings.WatchForNewDuplicates toggle) so it keeps
/// watching even while MainWindow is hidden.
/// </summary>
public sealed class DuplicateWatcherService : IDisposable
{
    private readonly SqliteStore _store;
    private readonly TrayIconService _tray;
    private readonly IHashProvider _hashProvider;
    private readonly IRecycleBinService _recycleBinService = new RecycleBinService();
    private readonly List<FileSystemWatcher> _watchers = new();
    private IDisposable? _notificationSubscription;

    // A bulk copy into a watched folder raises one Created event per file;
    // without a gate that is one concurrent full-file hash task per event,
    // saturating disk and thread pool. Two at a time keeps the tray
    // feature effectively invisible to system load.
    private readonly SemaphoreSlim _concurrencyGate = new(2, 2);

    public DuplicateWatcherService(SqliteStore store, TrayIconService tray)
    {
        _store = store;
        _tray = tray;
        // Deliberately the RAW HashProvider, NOT the CachingHashProvider: the
        // watcher's whole job is to re-verify a file's CURRENT content on
        // disk before claiming (or one-click deleting) a duplicate. The
        // caching provider returns the stored hash whenever size+mtime match,
        // which makes "re-hash the matched file" a tautology (the row was
        // selected *because* its FullHash equals the query) and hides a
        // content change that preserves size+mtime (archive extract with
        // stored timestamps, robocopy /COPY:T, "restore previous version").
        // Every hash here must be a fresh read of the bytes. The watcher is
        // low-frequency and gated, so skipping the cache costs nothing.
        _hashProvider = new HashProvider();
    }

    public void Start(IReadOnlyList<string> folderPaths)
    {
        Stop();

        foreach (var folder in folderPaths)
        {
            if (!Directory.Exists(folder))
            {
                App.Logger?.LogWarning($"DuplicateWatcherService: watched folder does not exist, skipping: '{folder}'");
                continue;
            }

            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            watcher.Created += OnFileCreated;
            watcher.Error += (_, e) => App.Logger?.LogError($"DuplicateWatcherService watcher error for '{folder}': {e.GetException()}");
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        // Subscribed via App.Notifications, never the WinRT event directly:
        // AppNotificationManager rejects subscriptions made after Register()
        // (which ran in OnLaunched long before any Settings-toggle Start()),
        // so App.xaml.cs owns the one WinRT subscription and services
        // attach/detach against the dispatcher instead.
        _notificationSubscription = App.Notifications.Subscribe(OnNotificationInvoked);
    }

    public void Stop()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        _notificationSubscription?.Dispose();
        _notificationSubscription = null;
    }

    public void Dispose() => Stop();

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (_tray.IsWatchingPaused)
        {
            return;
        }

        _ = Task.Run(() => HandleNewFileAsync(e.FullPath));
    }

    private async Task HandleNewFileAsync(string newFilePath)
    {
        try
        {
            await _concurrencyGate.WaitAsync();
            try
            {
                await DetectAndNotifyAsync(newFilePath);
            }
            finally
            {
                _concurrencyGate.Release();
            }
        }
        catch (Exception ex)
        {
            // A fire-and-forget Task.Run (OnFileCreated never awaits this)
            // means an exception here would otherwise become an unobserved
            // task exception - silently dropped, not logged anywhere. Caught
            // and logged explicitly so a failure here is at least visible in
            // the log file rather than indistinguishable from "no match
            // found".
            App.Logger?.LogError($"DuplicateWatcherService failed to process '{newFilePath}': {ex}");
        }
    }

    private async Task DetectAndNotifyAsync(string newFilePath)
    {
        // The scan pipeline's own safety filters apply here too - the
        // watcher must never hash (or offer to delete) something a scan
        // would refuse to touch.
        if (JunkFileRules.IsJunkFile(newFilePath) || HardBlockRules.IsHardBlocked(newFilePath))
        {
            return;
        }

        // A just-created file is frequently still being written to (a copy,
        // a download, an export) - retry a handful of times rather than
        // hashing a half-written file or throwing on a sharing violation.
        byte[]? hash = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(500);

            if (!File.Exists(newFilePath))
            {
                return;
            }

            try
            {
                hash = _hashProvider.ComputeFullHash(newFilePath);
                break;
            }
            catch (IOException)
            {
            }
        }

        if (hash is null)
        {
            App.Logger?.LogWarning($"DuplicateWatcherService: could not hash '{newFilePath}' after 5 attempts - it may still be locked by whatever created it");
            return;
        }

        var match = FindVerifiedMatch(hash, newFilePath);
        if (match is null)
        {
            return;
        }

        ShowDuplicateToast(newFilePath, match, hash);
    }

    /// <summary>
    /// A store row is a claim about the past, not the present - the file it
    /// names may have been deleted, moved, or rewritten since the scan that
    /// recorded it. Before telling the user "you already have this" (the
    /// exact assertion that convinces them deleting the new copy is safe),
    /// verify the matched file still exists AND still hashes to the same
    /// value. Rows for vanished files are pruned on the spot so the next
    /// lookup doesn't trip over them.
    /// </summary>
    private string? FindVerifiedMatch(byte[] hash, string newFilePath)
    {
        // Generous cap: each STALE row consumes one iteration (it's removed
        // and the query re-runs), so a folder with many deleted-then-
        // re-downloaded copies shouldn't exhaust the loop before reaching a
        // live row.
        const int maxRowsToWalk = 25;
        for (var attempt = 0; attempt < maxRowsToWalk; attempt++)
        {
            var match = _store.FindFileByFullHash(hash, excludingPath: newFilePath);
            if (match is null)
            {
                return null;
            }

            if (!File.Exists(match.Path))
            {
                _store.RemoveFileRecord(match.Path);
                continue;
            }

            try
            {
                var currentHash = _hashProvider.ComputeFullHash(match.Path);
                if (currentHash.AsSpan().SequenceEqual(hash))
                {
                    return match.Path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked/unreadable right now - can't verify, so don't claim.
            }

            // Exists but no longer matches (rewritten since the scan): the
            // row is stale for matching purposes; leave it for the next scan
            // to refresh, and stop rather than walking further rows - the
            // LIMIT 1 query would return this same row forever.
            return null;
        }

        App.Logger?.LogWarning(
            $"DuplicateWatcherService: gave up matching '{newFilePath}' after walking {maxRowsToWalk} stale store rows.");
        return null;
    }

    private static void ShowDuplicateToast(string newPath, string existingPath, byte[] verifiedHash)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("Duplicate found")
                .AddText($"\"{Path.GetFileName(newPath)}\" matches a file you already have.")
                .AddButton(new AppNotificationButton("Show me")
                    .AddArgument("action", "show")
                    .AddArgument("path", existingPath))
                .AddButton(new AppNotificationButton("Remove the new one")
                    .AddArgument("action", "removeNew")
                    .AddArgument("path", newPath)
                    .AddArgument("existingPath", existingPath)
                    // The claim is re-proven at click time (arbitrary time
                    // may pass with the toast sitting in Action Center).
                    .AddArgument("expectedHash", Convert.ToHexString(verifiedHash)))
                .AddButton(new AppNotificationButton("Keep both")
                    .AddArgument("action", "keepBoth"))
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Failed to show duplicate toast for '{newPath}': {ex}");
        }
    }

    private void OnNotificationInvoked(AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("action", out var action))
        {
            return;
        }

        switch (action)
        {
            case "show" when args.Arguments.TryGetValue("path", out var existingPath):
                App.MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
                {
                    var window = App.MainWindowInstance!;
                    H.NotifyIcon.WindowExtensions.Show(window);
                    H.NotifyIcon.WindowExtensions.ShowInTaskbar(window);
                    var folder = Path.GetDirectoryName(existingPath);
                    ((MainWindow)window).ShowHome(new HomePage.HomePagePrefillRequest(folder, LargeFilesMode: false));
                });
                break;

            case "removeNew" when args.Arguments.TryGetValue("path", out var newPath):
                args.Arguments.TryGetValue("expectedHash", out var expectedHashHex);
                args.Arguments.TryGetValue("existingPath", out var existingCopyPath);
                _ = Task.Run(() => RemoveNewCopy(newPath, existingCopyPath, expectedHashHex));
                break;

            case "keepBoth":
                break;
        }
    }

    /// <summary>
    /// One-click delete with no confirmation dialog demands the strongest
    /// re-verification in the app: the "existing" copy must still exist and
    /// still hash to the verified value, and the new file must still hash to
    /// it too - otherwise this click could remove the only copy of the data.
    /// </summary>
    private void RemoveNewCopy(string newPath, string? existingPath, string? expectedHashHex)
    {
        try
        {
            if (existingPath is null || expectedHashHex is null ||
                !File.Exists(existingPath) || !File.Exists(newPath))
            {
                App.Logger?.LogWarning($"Refusing toast delete of '{newPath}': the matched copy could not be re-verified.");
                return;
            }

            var expectedHash = Convert.FromHexString(expectedHashHex);
            var existingHash = _hashProvider.ComputeFullHash(existingPath);
            var newHash = _hashProvider.ComputeFullHash(newPath);
            if (!existingHash.AsSpan().SequenceEqual(expectedHash) || !newHash.AsSpan().SequenceEqual(expectedHash))
            {
                App.Logger?.LogWarning($"Refusing toast delete of '{newPath}': file contents changed since the duplicate was detected.");
                return;
            }

            var sizeBytes = new FileInfo(newPath).Length;
            _recycleBinService.SendToRecycleBin(newPath, expectedSizeBytes: sizeBytes);
            _store.RemoveFileRecord(newPath);
            _store.RecordTrashedFile(newPath, recycleBinPath: null, sizeBytes);
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Failed to recycle '{newPath}' from a duplicate toast action: {ex}");
        }
    }
}
