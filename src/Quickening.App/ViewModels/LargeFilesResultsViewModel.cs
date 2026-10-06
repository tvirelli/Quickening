using System.Collections.ObjectModel;
using Quickening.Core.Deletion;
using Quickening.Core.Models;
using Quickening.Core.Safety;
using Quickening.Core.Storage;

namespace Quickening.App.ViewModels;

/// <summary>
/// A flat counterpart to ResultsViewModel for Large Files mode - reuses
/// SelectableFile (the same per-file selection state Results and Media
/// Preview already share) but has no grouping concept: every candidate file
/// is its own row, sorted biggest-first per the design doc's "Large Files =
/// flat, biggest-first, no recommend" rule (behavioral rule 5).
/// </summary>
public sealed class LargeFilesResultsViewModel
{
    private readonly IRecycleBinService _recycleBinService;
    private readonly SqliteStore? _store;
    private List<SelectableFile> _allFiles = new();

    public ObservableCollection<SelectableFile> Files { get; } = new();

    public HashSet<MimeCategory> CategoryFilter { get; } = new();
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }

    // See ResultsViewModel.PathContains's identical doc comment.
    public string? PathContains { get; set; }

    // "Modified" date-range filter, mirroring ResultsViewModel's identical pair
    // so Large Files and Find Duplicates share one filter interface. Keeps only
    // files last modified on/after ModifiedAfter and on/before ModifiedBefore
    // (null = unbounded on that end). Uses modified time (LastWriteTimeUtc) -
    // Windows last-ACCESS time is unreliable (NTFS access-time updates are off
    // by default), so modified time is the dependable age signal.
    public DateTime? ModifiedAfter { get; set; }
    public DateTime? ModifiedBefore { get; set; }

    // See ResultsViewModel.ExtensionFilter - exact (case-insensitive) extension
    // match, e.g. ".mp4". The picklist is AvailableExtensions.
    public string? ExtensionFilter { get; set; }

    // Distinct file extensions across the loaded files (lower-cased, leading
    // dot, sorted), for the Extension dropdown to bind to.
    public IReadOnlyList<string> AvailableExtensions { get; private set; } = Array.Empty<string>();

    public LargeFilesResultsViewModel(IRecycleBinService recycleBinService, SqliteStore? store = null)
    {
        _recycleBinService = recycleBinService;
        _store = store;
    }

    public void LoadFiles(IEnumerable<SelectableFile> files)
    {
        _allFiles = files.OrderByDescending(f => f.SizeBytes).ToList();

        // Only offer extensions actually present in the loaded files (no group
        // prune here, unlike ResultsViewModel - every large file is its own row).
        AvailableExtensions = _allFiles
            .Select(f => System.IO.Path.GetExtension(f.Path).ToLowerInvariant())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        ApplyFilters();
    }

    // When on, ignored files are shown (dimmed + badged) instead of hidden.
    public bool ShowIgnored { get; set; }

    public void ApplyFilters()
    {
        Files.Clear();
        foreach (var file in _allFiles.Where(MatchesFilters))
        {
            file.IsIgnored = IgnoreService.IsIgnored(file.Path);
            Files.Add(file);
        }
    }

    private bool MatchesFilters(SelectableFile file)
    {
        if (!ShowIgnored && IgnoreService.IsIgnored(file.Path)) return false;
        if (CategoryFilter.Count > 0 && !CategoryFilter.Contains(file.Category)) return false;
        if (MinSizeBytes is { } min && file.SizeBytes < min) return false;
        if (MaxSizeBytes is { } max && file.SizeBytes > max) return false;
        if (!string.IsNullOrEmpty(PathContains) && file.Path.IndexOf(PathContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (ModifiedAfter is { } after && file.LastWriteTimeUtc < after) return false;
        if (ModifiedBefore is { } before && file.LastWriteTimeUtc > before) return false;
        if (!string.IsNullOrEmpty(ExtensionFilter)
            && !System.IO.Path.GetExtension(file.Path).Equals(ExtensionFilter, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Files under a Settings-configured trusted folder are excluded even if
    // their extension would otherwise be risky - see ResultsViewModel's own
    // doc comment on the equivalent method for why.
    public IReadOnlyList<string> GetSelectedRiskyFilePaths() =>
        Files.Where(f => f.IsSelected && RiskyExtensions.IsRisky(f.Path) && !App.Settings.IsPathTrusted(f.Path))
            .Select(f => f.Path).ToList();

    public long GetSelectedSizeBytes() => Files.Where(f => f.IsSelected).Sum(f => f.SizeBytes);

    /// <summary>Unticks (and un-recommends) every file now on the ignore list -
    /// see ResultsViewModel.ClearIgnoredSelections. Runs over _allFiles, not
    /// just the visible Files, so a selection made before the ignore can't
    /// survive hidden.</summary>
    public void ClearIgnoredSelections()
    {
        foreach (var file in _allFiles)
        {
            if (IgnoreService.IsIgnored(file.Path))
            {
                file.IsSelected = false;
            }
        }
    }

    public void SelectAll() => SetSelection(_ => true);
    public void ClearSelection() => SetSelection(_ => false);
    public void InvertSelection() => SetSelection(f => !f.IsSelected);
    public void SelectBySizeThreshold(long minSizeBytes) => SetSelection(f => f.SizeBytes >= minSizeBytes);

    // See ResultsViewModel.SelectByFolder's identical doc comment.
    public void SelectByFolder(string folderPath) => SetSelection(f => IsUnderFolder(f.Path, folderPath));

    private static bool IsUnderFolder(string path, string folder)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == folder.Length) return true;
        var boundaryChar = path[folder.Length];
        return boundaryChar == System.IO.Path.DirectorySeparatorChar || boundaryChar == System.IO.Path.AltDirectorySeparatorChar;
    }

    // See ResultsViewModel.SetSelection's identical doc comment on why
    // network-drive files are excluded from every mass-select rule. Ignored
    // files are excluded too: with "Show ignored" on they're VISIBLE in Files,
    // and a Select All must never tee up a kept-on-purpose file for deletion.
    private void SetSelection(Func<SelectableFile, bool> selected)
    {
        foreach (var file in Files)
        {
            file.IsSelected = selected(file) && !file.IsOnNetworkDrive && !IgnoreService.IsIgnored(file.Path);
        }
    }

    /// <summary>
    /// Same per-file Recycle-Bin semantics as ResultsViewModel.DeleteSelectedAsync
    /// (per-file cancellation check, failures collected not thrown), simplified
    /// by having no group-pruning/relabeling step since there are no groups -
    /// a removed file's row just disappears.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteSelectedAsync(
        IProgress<DeleteProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string targetLabel = "")
    {
        var failed = new List<string>();
        // !IsIgnored: last-line defense mirroring ResultsViewModel's delete
        // loops - an ignored file must never be deleted even if a stale
        // IsSelected somehow survived.
        var selected = Files.Where(f => f.IsSelected && !IgnoreService.IsIgnored(f.Path)).ToList();
        var processed = 0;
        var bytesRemoved = 0L;

        // See ResultsViewModel.DeleteSelectedAsync's own comment on
        // BeginRemovalBatch for why this exists.
        var removalBatchId = _store?.BeginRemovalBatch(targetLabel, "Large files");

        try
        {
            foreach (var file in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Expected size/mtime ride along so SendToRecycleBin's
                    // delete-time re-validation can refuse a file that
                    // changed since the scan snapshot said it was expendable.
                    await Task.Run(() => _recycleBinService.SendToRecycleBin(file.Path, file.SizeBytes, file.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(file.Path);
                    processed++;
                    progress?.Report(new DeleteProgress(processed, selected.Count, file.Path));
                    continue;
                }

                // The physical delete above already succeeded - this is just
                // bookkeeping, same as ResultsViewModel.DeleteSelectedAsync. A DB
                // write conflict here must never crash the app or leave the UI
                // showing a file that's already gone from disk, so it's isolated
                // from the "did the delete actually work" failure path above.
                try
                {
                    _store?.RecordTrashedFile(file.Path, recycleBinPath: null, file.SizeBytes, removalBatchId);
                    _store?.RemoveFileRecord(file.Path);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    App.Logger?.LogError("Failed to record trashed file", ex);
                }

                bytesRemoved += file.SizeBytes;
                Files.Remove(file);
                _allFiles.Remove(file);
                processed++;
                progress?.Report(new DeleteProgress(processed, selected.Count, file.Path));
            }
        }
        finally
        {
            // In a finally (mirroring ResultsViewModel.DeleteSelectedAsync):
            // pressing Stop mid-removal throws OperationCanceledException
            // above, and without this the History session row was never
            // completed - files genuinely recycled in the batch just
            // vanished from History.
            if (removalBatchId is { } id)
            {
                try
                {
                    _store!.CompleteRemovalBatch(id, processed - failed.Count, bytesRemoved);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    App.Logger?.LogError("Failed to complete removal batch", ex);
                }
            }
        }

        return failed;
    }
}
