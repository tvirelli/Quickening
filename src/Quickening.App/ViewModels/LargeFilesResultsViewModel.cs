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

    public LargeFilesResultsViewModel(IRecycleBinService recycleBinService, SqliteStore? store = null)
    {
        _recycleBinService = recycleBinService;
        _store = store;
    }

    public void LoadFiles(IEnumerable<SelectableFile> files)
    {
        _allFiles = files.OrderByDescending(f => f.SizeBytes).ToList();
        ApplyFilters();
    }

    public void ApplyFilters()
    {
        Files.Clear();
        foreach (var file in _allFiles.Where(MatchesFilters))
        {
            Files.Add(file);
        }
    }

    private bool MatchesFilters(SelectableFile file)
    {
        if (CategoryFilter.Count > 0 && !CategoryFilter.Contains(file.Category)) return false;
        if (MinSizeBytes is { } min && file.SizeBytes < min) return false;
        if (MaxSizeBytes is { } max && file.SizeBytes > max) return false;
        if (!string.IsNullOrEmpty(PathContains) && file.Path.IndexOf(PathContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }

    // Files under a Settings-configured trusted folder are excluded even if
    // their extension would otherwise be risky - see ResultsViewModel's own
    // doc comment on the equivalent method for why.
    public IReadOnlyList<string> GetSelectedRiskyFilePaths() =>
        Files.Where(f => f.IsSelected && RiskyExtensions.IsRisky(f.Path) && !App.Settings.IsPathTrusted(f.Path))
            .Select(f => f.Path).ToList();

    public long GetSelectedSizeBytes() => Files.Where(f => f.IsSelected).Sum(f => f.SizeBytes);

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
    // network-drive files are excluded from every mass-select rule.
    private void SetSelection(Func<SelectableFile, bool> selected)
    {
        foreach (var file in Files) file.IsSelected = selected(file) && !file.IsOnNetworkDrive;
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
        var selected = Files.Where(f => f.IsSelected).ToList();
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
