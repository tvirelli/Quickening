namespace Quickening.App.Settings;

/// <summary>
/// Which copy "Select Recommended" pre-selects as the one to keep
/// (unselected) in each duplicate group.
/// </summary>
public enum KeepRule
{
    Newest,
    Oldest,
    ShortestPath,
}

/// <summary>
/// Which of Home's two scan types a scheduled scan should run - named
/// distinctly from HomePage's own private ScanMode (that one's UI-local;
/// this one is persisted and also read by the tray watcher's scheduler).
/// </summary>
public enum ScheduledScanMode
{
    Duplicates,
    LargeFiles,
}

public enum ScheduledScanFrequency
{
    Daily,
    Weekly,
}

/// <summary>
/// Persisted user preferences - loaded once at startup (App.xaml.cs) and
/// saved back to disk (SettingsService) whenever SettingsDialog changes one.
/// Deliberately a plain settable-property class, not a record - it's a
/// single long-lived mutable instance shared app-wide (App.Settings), not
/// a value passed around and compared/replaced wholesale.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Off by default - Quickening.Core.Scanning.FileEnumerator already
    /// skips hidden (and always skips system) files/folders unless this is
    /// true. See SettingsDialog's own copy for the user-facing
    /// explanation of why this defaults off.
    /// </summary>
    public bool ScanHiddenFiles { get; set; }

    /// <summary>
    /// Off by default - see Quickening.Core.Safety.HardBlockRules.IsHardBlocked's
    /// allowProtectedPaths parameter. Only ever flips true after the
    /// first-time confirmation dialog (SettingsDialog/ProtectedPathsConfirmDialog).
    /// </summary>
    public bool AllowProtectedPaths { get; set; }

    /// <summary>
    /// Off by default - see Quickening.Core.Duplicates.DuplicateEngine.FindDuplicates's
    /// paranoidMode parameter (extra byte-for-byte verification after a
    /// full-hash match).
    /// </summary>
    public bool ParanoidMode { get; set; }

    /// <summary>
    /// Newest by default (matches the app's original hardcoded behavior).
    /// Read by ResultsViewModel.SelectRecommended.
    /// </summary>
    public KeepRule PreferredKeepRule { get; set; } = KeepRule.Newest;

    /// <summary>
    /// Folders where files are never flagged as "risky" regardless of
    /// extension - see Quickening.Core.Safety.RiskyExtensions and its
    /// callers. Empty by default.
    /// </summary>
    public List<string> TrustedFolderPaths { get; set; } = new();

    /// <summary>
    /// Automation group, Settings screen. The scheduler itself is an
    /// in-process timer owned by the tray-lifetime component (Phase 6) -
    /// these properties are only the persisted configuration.
    /// </summary>
    public bool ScheduledScanEnabled { get; set; }
    public ScheduledScanFrequency ScheduledScanFrequency { get; set; } = ScheduledScanFrequency.Weekly;
    public ScheduledScanMode ScheduledScanMode { get; set; } = ScheduledScanMode.Duplicates;
    public string? ScheduledScanTargetPath { get; set; }

    /// <summary>
    /// UTC timestamp of the last scan ScheduledScanService actually ran -
    /// null until the first one fires. Compared against ScheduledScanFrequency
    /// to decide whether a new one is due; persisted so the interval survives
    /// an app restart instead of resetting to "due immediately" every launch.
    /// </summary>
    public DateTime? LastScheduledScanRunUtc { get; set; }

    /// <summary>
    /// Automation group. WatchedFolderPaths is empty by default - the
    /// watcher (Phase 6) has nothing to watch until the user adds at least
    /// one folder, same empty-by-default posture as TrustedFolderPaths.
    /// </summary>
    public bool WatchForNewDuplicates { get; set; }
    public List<string> WatchedFolderPaths { get; set; } = new();

    /// <summary>
    /// The folder targeted by the most recent Duplicates scan (HomePage sets
    /// this at the start of every such scan) - powers the tray icon's
    /// right-click "Scan {folder} for duplicates" item (new-screens/
    /// tray-menus 5a), which needs a real, current target rather than a
    /// hardcoded guess. Null until the very first scan this install has
    /// ever run.
    /// </summary>
    public string? LastScannedFolderPath { get; set; }

    /// <summary>
    /// Privacy group. Off by default. There is no telemetry backend/server
    /// anywhere in this project - enabling this only turns on local
    /// instrumentation (counts of scans run, space freed; never paths or
    /// filenames) written to a local rolling log, not an actual network
    /// send. See UsageStatsService's own doc comment.
    /// </summary>
    public bool ShareAnonymousUsageStats { get; set; }

    /// <summary>
    /// True if path is under (or exactly equal to) one of TrustedFolderPaths.
    /// Used to skip the risky-extension warning for files a user has
    /// explicitly vouched for (e.g. a folder of VM disks or DB backups) -
    /// see ResultsViewModel/LargeFilesResultsViewModel's own
    /// GetSelectedRiskyFilePaths. A plain prefix match with a directory-
    /// separator boundary check, so "C:\Trusted2" never matches a trusted
    /// entry of "C:\Trusted".
    /// </summary>
    public bool IsPathTrusted(string path) => TrustedFolderPaths.Any(trusted => IsUnderFolder(path, trusted));

    private static bool IsUnderFolder(string path, string folder)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == folder.Length)
        {
            return true;
        }

        var boundaryChar = path[folder.Length];
        return boundaryChar == System.IO.Path.DirectorySeparatorChar || boundaryChar == System.IO.Path.AltDirectorySeparatorChar;
    }
}
