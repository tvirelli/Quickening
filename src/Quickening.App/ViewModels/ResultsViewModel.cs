using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Quickening.App.Controls;
using Quickening.App.Formatting;
using Quickening.App.Settings;
using Quickening.Core.Deletion;
using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Quickening.Core.Safety;
using Quickening.Core.Storage;

namespace Quickening.App.ViewModels;

public sealed class SelectableFile : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required MimeCategory Category { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }

    // default(DateTime) = "unknown" for any construction that doesn't set it;
    // only the duplicate/similarity maps carry the real value from FileRecord.
    // Read by DuplicateGroupViewModel.IsLikelyCreatedTogether.
    public DateTime CreationTimeUtc { get; init; }

    // Backs the per-file icon's Foreground in ResultsPage.xaml, so a file's
    // row icon is colored to match its category - the same fixed palette
    // used by the donut chart and the Results sidebar (see CategoryColors).
    // Shared cached brush: this getter runs on every row re-realization
    // during virtualization, so allocating per call was pure GC churn.
    public SolidColorBrush CategoryBrush => CategoryColors.BrushFor(Category);

    // Backs the disabled-checkbox/"NETWORK DRIVE" pill row treatment
    // (new-screens 4i) - a mapped network drive or UNC path can't be
    // recycled (RecycleBinService.SendToRecycleBin refuses these outright),
    // so these rows are shown but excluded from selection entirely rather
    // than letting a user select one and then hit a delete failure. Cached:
    // Path never changes after construction, and this getter re-runs
    // (GetFullPath + a GetDriveTypeW syscall) on every row realization
    // during list virtualization.
    public bool IsOnNetworkDrive => _isOnNetworkDrive ??= NetworkPathDetector.IsNetworkPath(Path);
    private bool? _isOnNetworkDrive;

    // Bound Mode=TwoWay to a CheckBox in ResultsPage.xaml. Without
    // INotifyPropertyChanged here, mass-selection methods on
    // ResultsViewModel (SelectAll/ClearSelection/InvertSelection/
    // SelectBySizeThreshold/SelectByCategory) mutate this property on
    // already-realized rows but the already-rendered CheckBoxes never
    // learn about it - confirmed by manually running the app: clicking
    // "Select All" left every checkbox visually unchecked even though the
    // underlying model was updated. Raising PropertyChanged here is what
    // makes the bound CheckBoxes refresh.
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    // Backs the "KEEP" pill in ResultsPage/Media Preview (Task 8/10) - marks
    // whichever file in this group has the newest LastWriteTimeUtc. Computed
    // once per ApplyFilters() call (see ResultsViewModel.ApplyFilters) and
    // never mutated afterward by user interaction, unlike IsSelected, so
    // this is a plain settable property with no INotifyPropertyChanged
    // backing - no change notification is needed.
    public bool IsKeepRecommended { get; set; }

    // Backs the pill's own text ("KEEP — newest"/"— oldest"/"— shortest
    // path") so it always names whichever rule actually chose this file,
    // matching Settings' "Select Recommended keeps the…" choice instead of
    // being hardcoded to "newest". Same computed-once-per-ApplyFilters
    // lifecycle as IsKeepRecommended above.
    public string KeepRecommendedLabel { get; set; } = "";

    // Backs the similarity section's hint pill ("BIGGER FILE" - see
    // ResultsViewModel.LoadSimilarityGroups) - null for every file in the
    // regular Duplicates Groups collection, which uses IsKeepRecommended/
    // KeepRecommendedLabel instead. A distinct property rather than reusing
    // those: a "hint" isn't a recommendation to keep or remove anything -
    // similar (not identical) photos have no single correct answer
    // (new-screens 4k: "never auto-selected").
    public string? HintLabel { get; set; }

    // Backs the per-row corner-rounding/bottom-margin in ResultsPage.xaml -
    // the group "card" look (rounded corners, one outer border, a gap
    // before the next group) is now achieved per-row rather than via one
    // wrapping Border, since GroupsListView flattens headers and files into
    // a single virtualized list (see ResultsPage.xaml.cs's RebuildFlatRows)
    // instead of nesting a nother ItemsControl per group - the nested,
    // non-virtualizing ItemsControl was the root cause of a native
    // Microsoft.UI.Xaml crash under fast scrolling (confirmed via
    // crash-dump analysis). Computed once per ApplyFilters() call, same
    // pattern as IsKeepRecommended above.
    public bool IsLastInGroup { get; set; }

    private bool _thumbnailFailed;

    public bool IsImage => Category == MimeCategory.Image && !_thumbnailFailed;

    // Called from ResultsPage.xaml's Image.ImageFailed handler when a
    // decode fails after the fact (corrupt/truncated file, or the file
    // vanishing mid-decode) - unlike the synchronous construction errors
    // ThumbnailSource's own try/catch already covers, a failed async decode
    // never throws, it only raises this event. Without this, IsImage would
    // stay true forever and the FontIcon fallback in ResultsPage.xaml would
    // never reappear, leaving the row with a blank/broken image.
    public void ReportThumbnailFailed()
    {
        if (_thumbnailFailed)
        {
            return;
        }

        _thumbnailFailed = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCategoryGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFileTypeIcon)));
    }

    // Lazily decoded on first access rather than eagerly for every file at
    // LoadGroups time - a scan can surface thousands of files, and decoding
    // a thumbnail bitmap for every single one up front (including files never
    // scrolled into view) would be wasted work. BitmapImage's DecodePixelWidth
    // keeps the decode cheap (a small thumbnail, not the full-resolution
    // original) regardless of how large the source file is.
    private BitmapImage? _thumbnailSource;
    private bool _videoThumbReady;

    public bool IsVideo => Category == MimeCategory.Video;

    // The row shows a thumbnail image (vs the category icon) for images always,
    // and for videos once their poster frame has loaded (LoadVideoThumbnailAsync).
    public bool HasThumbnail => IsImage || (IsVideo && _videoThumbReady);

    public BitmapImage? ThumbnailSource
    {
        get
        {
            if (IsImage)
            {
                if (_thumbnailSource is null)
                {
                    try
                    {
                        _thumbnailSource = new BitmapImage { DecodePixelWidth = 64 };
                        _thumbnailSource.UriSource = new Uri(Path);
                    }
                    catch (Exception ex) when (ex is UriFormatException or IOException or UnauthorizedAccessException)
                    {
                        // A file can vanish/become inaccessible between the scan
                        // that found it and the user scrolling this row into view -
                        // fall back to null (the FontIcon fallback in ResultsPage.xaml
                        // takes over) rather than throwing out of a property getter.
                        return null;
                    }
                }

                return _thumbnailSource;
            }

            return IsVideo && _videoThumbReady ? _thumbnailSource : null;
        }
    }

    // Loads a poster frame for a video row (Windows generates it - no decoding on
    // our side). Best-effort, fire-and-forget from the row's Loaded handler; on
    // success the icon is swapped for the poster via PropertyChanged.
    public async System.Threading.Tasks.Task LoadVideoThumbnailAsync()
    {
        if (!IsVideo || _videoThumbReady)
        {
            return;
        }

        try
        {
            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path);
            using var thumb = await storageFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 64);
            if (thumb is null || thumb.Size == 0)
            {
                return;
            }

            var bmp = new BitmapImage { DecodePixelWidth = 64 };
            await bmp.SetSourceAsync(thumb);
            _thumbnailSource = bmp;
            _videoThumbReady = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailSource)));
            // The poster now wins over both the shell icon and the category glyph.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCategoryGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFileTypeIcon)));
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Video thumbnail load failed for '{Path}': {ex}");
        }
    }

    // Real Windows shell icon for non-media rows (what Explorer shows for the
    // type). Lazily loaded from the row's Loaded handler; on success it replaces
    // the generic category glyph. Media rows skip it - they show a content
    // thumbnail instead.
    private Microsoft.UI.Xaml.Media.ImageSource? _fileTypeIcon;
    private bool _fileTypeIconRequested;

    public Microsoft.UI.Xaml.Media.ImageSource? FileTypeIconSource => _fileTypeIcon;

    // The icon Border layers three mutually exclusive states: a content
    // thumbnail (images/videos), else the real shell type icon once loaded, else
    // the generic category glyph as the always-available fallback.
    public bool ShowFileTypeIcon => !HasThumbnail && _fileTypeIcon is not null;
    public bool ShowCategoryGlyph => !HasThumbnail && _fileTypeIcon is null;

    public void LoadFileTypeIcon()
    {
        if (_fileTypeIconRequested || HasThumbnail)
        {
            return;
        }
        _fileTypeIconRequested = true;

        // Synchronous, per-extension cached, and fast (GDI icon extraction), so it
        // runs inline on the UI thread from the row's Loaded handler.
        var source = Services.ShellIconProvider.GetIconSource(Path);
        if (source is null)
        {
            return;
        }

        _fileTypeIcon = source;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileTypeIconSource)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowFileTypeIcon)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowCategoryGlyph)));
    }
}

public sealed class DuplicateGroupViewModel : INotifyPropertyChanged
{
    private string _groupLabel = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string GroupLabel
    {
        get => _groupLabel;
        set
        {
            _groupLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupLabel)));
        }
    }

    public required ObservableCollection<SelectableFile> Files { get; init; }

    // True when every copy in this group shares one creation second - a signal
    // they were written together as a set (installer/extractor) rather than
    // manually duplicated. Drives the "Created together" badge and is skipped by
    // SelectRecommended. See Quickening.Core.Safety.CreatedTogetherDetector.
    public bool IsLikelyCreatedTogether =>
        Quickening.Core.Safety.CreatedTogetherDetector.IsLikelyCreatedTogether(
            Files.Select(f => f.CreationTimeUtc).ToList());
}

/// <summary>
/// A group of images that look alike but aren't byte-identical (new-screens
/// 4k) - a parallel to DuplicateGroupViewModel, kept as its own type rather
/// than reused because it carries MatchPercent instead of a KEEP
/// recommendation, and is never touched by SelectRecommended.
/// </summary>
public sealed class SimilarityGroupViewModel : INotifyPropertyChanged
{
    private string _groupLabel = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string GroupLabel
    {
        get => _groupLabel;
        set
        {
            _groupLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GroupLabel)));
        }
    }

    public required int MatchPercent { get; init; }

    public required ObservableCollection<SelectableFile> Files { get; init; }
}

/// <summary>
/// Reported once per file by ResultsViewModel.DeleteSelectedAsync. A named
/// record rather than a raw tuple, matching the one other IProgress&lt;T&gt;
/// precedent in this codebase (Quickening.Core.Orchestration.ScanProgress).
/// </summary>
public sealed record DeleteProgress(int Processed, int Total, string Path);

public sealed class ResultsViewModel
{
    private readonly IRecycleBinService _recycleBinService;
    private readonly SqliteStore? _store;

    // The full, unfiltered set of groups from the last scan. Groups holds
    // only what's currently visible after ApplyFilters() runs, so this is
    // retained separately - otherwise clearing a filter would have nothing
    // to restore from.
    private List<List<SelectableFile>> _allGroups = new();

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    // "Looks-alike photos" (new-screens 4k) - unlike Groups, not re-derived
    // from a retained unfiltered source on every ApplyFilters() call: the
    // Duplicates filter row (category/size/date/path/copies) has no visual
    // presence in that section at all, so there's nothing to re-apply.
    public ObservableCollection<SimilarityGroupViewModel> SimilarityGroups { get; } = new();

    public HashSet<MimeCategory> CategoryFilter { get; } = new();
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }
    public DateTime? ModifiedAfter { get; set; }
    public DateTime? ModifiedBefore { get; set; }

    // "Copies 3+" (new-screens 4i) - a group-level filter, not a per-file
    // one: a group is only visible if it still has at least this many
    // VISIBLE files, checked after every per-file MatchesFilters has
    // already run (same place/order as the existing "fewer than 2 isn't a
    // duplicate" prune below).
    public int? MinGroupSize { get; set; }

    // "Path contains" search box (4i) - plain case-insensitive substring
    // match against the full path, same casing rule PathContains's sibling
    // filters (category/size/date) don't need since they're not free text.
    public string? PathContains { get; set; }

    public ResultsViewModel() : this(new RecycleBinService(), null)
    {
    }

    public ResultsViewModel(IRecycleBinService recycleBinService, SqliteStore? store = null)
    {
        _recycleBinService = recycleBinService;
        _store = store;
    }

    public void LoadGroups(IReadOnlyList<DuplicateGroup> duplicateGroups)
    {
        _allGroups = duplicateGroups
            .Select(g => g.Files.Select(f => new SelectableFile
            {
                Path = f.Path,
                SizeBytes = f.SizeBytes,
                Category = f.Category,
                LastWriteTimeUtc = f.LastWriteTimeUtc,
                CreationTimeUtc = f.CreationTimeUtc,
            }).ToList())
            .ToList();

        ApplyFilters();
    }

    /// <summary>
    /// Populates the "Looks-alike photos" section from a scan's
    /// SimilarityGroups - see new-screens 4k. Called once per fresh
    /// ScanResult load, same lifecycle as LoadGroups.
    /// </summary>
    public void LoadSimilarityGroups(IReadOnlyList<Quickening.Core.Similarity.SimilarityGroup> similarityGroups)
    {
        SimilarityGroups.Clear();

        foreach (var group in similarityGroups)
        {
            var files = group.Files.Select(f => new SelectableFile
            {
                Path = f.Path,
                SizeBytes = f.SizeBytes,
                Category = f.Category,
                LastWriteTimeUtc = f.LastWriteTimeUtc,
                CreationTimeUtc = f.CreationTimeUtc,
            }).ToList();

            // Hint pill (4k's "SHARPER · LARGER") - simplified to file size
            // alone, since no resolution/sharpness metric is computed
            // anywhere in this pipeline; fabricating "sharper" without
            // actually measuring it would be dishonest. A tie gets no hint
            // at all rather than an arbitrary pick.
            var biggest = files.OrderByDescending(f => f.SizeBytes).First();
            if (files.Count(f => f.SizeBytes == biggest.SizeBytes) == 1)
            {
                biggest.HintLabel = "BIGGER FILE";
            }

            SimilarityGroups.Add(new SimilarityGroupViewModel
            {
                GroupLabel = files.Count == 1 ? "1 similar photo" : $"{files.Count} similar photos",
                MatchPercent = group.MatchPercent,
                Files = new ObservableCollection<SelectableFile>(files),
            });
        }
    }

    /// <summary>
    /// Rebuilds the visible Groups collection from the retained, unfiltered
    /// _allGroups source by applying the current filter state. A file is
    /// visible only if it matches every active filter; a group is visible
    /// only if it still has 2+ visible files - the same "fewer than 2 isn't
    /// a duplicate" rule used when pruning after a delete.
    /// </summary>
    public void ApplyFilters()
    {
        Groups.Clear();

        foreach (var groupFiles in _allGroups)
        {
            var visibleFiles = groupFiles.Where(MatchesFilters).ToList();
            if (visibleFiles.Count < 2)
            {
                continue;
            }

            if (MinGroupSize is { } minGroupSize && visibleFiles.Count < minGroupSize)
            {
                continue;
            }

            var rule = App.Settings.PreferredKeepRule;
            var keeper = ChooseKeeper(visibleFiles, rule);
            var keepLabel = KeepLabelFor(rule);
            foreach (var file in visibleFiles)
            {
                file.IsKeepRecommended = ReferenceEquals(file, keeper);
                file.KeepRecommendedLabel = keepLabel;
                file.IsLastInGroup = ReferenceEquals(file, visibleFiles[^1]);
            }

            Groups.Add(new DuplicateGroupViewModel
            {
                GroupLabel = $"{visibleFiles.Count} copies - {FileSizeFormatter.Format(visibleFiles[0].SizeBytes)} each",
                Files = new ObservableCollection<SelectableFile>(visibleFiles),
            });
        }
    }

    private bool MatchesFilters(SelectableFile file)
    {
        if (CategoryFilter.Count > 0 && !CategoryFilter.Contains(file.Category))
        {
            return false;
        }

        if (MinSizeBytes is { } min && file.SizeBytes < min)
        {
            return false;
        }

        if (MaxSizeBytes is { } max && file.SizeBytes > max)
        {
            return false;
        }

        if (ModifiedAfter is { } after && file.LastWriteTimeUtc < after)
        {
            return false;
        }

        if (ModifiedBefore is { } before && file.LastWriteTimeUtc > before)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(PathContains) && file.Path.IndexOf(PathContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the paths of currently-selected files that match
    /// RiskyExtensions.IsRisky (VM disks, databases, backups, encrypted
    /// containers, game saves, etc. - see the design doc's "Risky-extension
    /// warning" step). This is a soft warning, distinct from HardBlockRules:
    /// these files are already shown and are still deletable, this just
    /// tells the caller (ResultsPage's delete-confirmation dialog) which of
    /// the selected files need to be itemized separately and require one
    /// extra explicit acknowledgment before the batch proceeds. Files under
    /// a Settings-configured trusted folder are excluded even if their
    /// extension would otherwise be risky - the user has already vouched
    /// for that folder.
    /// </summary>
    public IReadOnlyList<string> GetSelectedRiskyFilePaths() =>
        AllSelectableFiles()
            .Where(f => f.IsSelected && RiskyExtensions.IsRisky(f.Path) && !App.Settings.IsPathTrusted(f.Path))
            .Select(f => f.Path)
            .ToList();

    /// <summary>
    /// Selected files that belong to a "created together" duplicate group (all
    /// copies share one creation second - likely an app/installer set). Drives
    /// the extra confirmation before removal, mirroring the risky-file gate. A
    /// group-level property, so this iterates Groups rather than flat files.
    /// </summary>
    public IReadOnlyList<string> GetSelectedCreatedTogetherPaths() =>
        Groups.Where(g => g.IsLikelyCreatedTogether)
            .SelectMany(g => g.Files)
            .Where(f => f.IsSelected)
            .Select(f => f.Path)
            .ToList();

    /// <summary>
    /// Sums SizeBytes across every currently-selected file (Duplicates and
    /// Looks-alike photos both), for the bottom-bar "X selected" stat in
    /// ResultsPage.
    /// </summary>
    public long GetSelectedSizeBytes() =>
        AllSelectableFiles().Where(f => f.IsSelected).Sum(f => f.SizeBytes);

    /// <summary>
    /// The paths of every currently-selected file (Duplicates and
    /// Looks-alike photos both) - callers capture this BEFORE
    /// DeleteSelectedAsync runs (which removes files from both collections
    /// as it processes them) when they need to know afterward exactly which
    /// paths a since-completed removal touched (e.g. the Undo toast on
    /// Celebration - see ResultsPage.xaml.cs's RemoveSelectedFilesAsync).
    /// </summary>
    public IReadOnlyList<string> GetSelectedFilePaths() =>
        AllSelectableFiles().Where(f => f.IsSelected).Select(f => f.Path).ToList();

    private IEnumerable<SelectableFile> AllSelectableFiles() =>
        Groups.SelectMany(g => g.Files).Concat(SimilarityGroups.SelectMany(g => g.Files));

    /// <summary>
    /// Sends every currently-selected file to the Recycle Bin, reporting
    /// progress once per file. Only the blocking Recycle-Bin syscall itself
    /// runs off the UI thread (via Task.Run per file) - the surrounding
    /// loop and all Groups/_allGroups mutations stay on the calling thread,
    /// since ObservableCollection isn't safe to mutate from a background
    /// thread. Honors cancellationToken between files (already-deleted
    /// files stay deleted - cancelling only stops processing further
    /// selected files, it never attempts to "undo" anything already sent to
    /// the Recycle Bin). Returns the paths that failed to delete (e.g.
    /// locked files) rather than throwing, so the caller can report a
    /// "couldn't remove" list per the design doc rather than aborting the
    /// whole batch on one failure. Groups that drop below 2 remaining files
    /// (no longer a duplicate) are removed entirely, and surviving groups'
    /// labels are recomputed to reflect their new count - the try/finally
    /// nesting below guarantees this pruning/relabeling and the final
    /// _allGroups cleanup both still happen for a group that was only
    /// partway through when cancellation fired, not just on a clean finish.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteSelectedAsync(
        IProgress<DeleteProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string targetLabel = "")
    {
        var failed = new List<string>();
        var totalFiles = AllSelectableFiles().Count(f => f.IsSelected);
        var processed = 0;
        var bytesRemoved = 0L;

        // One ScanSessions row per removal batch (not per file) - see
        // SqliteStore.BeginRemovalBatch's own doc comment for why this is
        // what gives History's session-grouped view real grouping. Null
        // when there's no store (e.g. a ResultsViewModel() test instance),
        // matching the same "_store?." pattern already used below.
        var removalBatchId = _store?.BeginRemovalBatch(targetLabel, "Duplicates");

        try
        {
            foreach (var group in Groups.ToList())
            {
                try
                {
                    foreach (var file in group.Files.Where(f => f.IsSelected).ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            // No cancellationToken passed to Task.Run here:
                            // SendToRecycleBin is a single bounded shell call
                            // with no interior cancellation point, and the
                            // check above already covers "cancelled before
                            // this file starts" - a token on Task.Run only
                            // affects whether the delegate starts at all, not
                            // an already-running one. Expected size/mtime ride
                            // along so delete-time re-validation can refuse a
                            // file that changed since the scan snapshot.
                            await Task.Run(() => _recycleBinService.SendToRecycleBin(file.Path, file.SizeBytes, file.LastWriteTimeUtc));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            failed.Add(file.Path);
                            processed++;
                            progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                            continue;
                        }

                        // The physical delete above already succeeded - this is
                        // just bookkeeping. A DB write conflict here (e.g. Stats/
                        // TrashLog contention with a future writer) must never
                        // crash the app or leave the UI showing a file that's
                        // already gone from disk, so it's isolated from the
                        // "did the delete actually work" failure path above.
                        try
                        {
                            _store?.RecordTrashedFile(file.Path, recycleBinPath: null, file.SizeBytes, removalBatchId);
                            // Drop the hash-cache row too, so the tray watcher
                            // can never claim a new arrival "matches" this
                            // just-deleted path.
                            _store?.RemoveFileRecord(file.Path);
                        }
                        catch (Microsoft.Data.Sqlite.SqliteException ex)
                        {
                            App.Logger?.LogError("Failed to record trashed file", ex);
                        }

                        bytesRemoved += file.SizeBytes;
                        group.Files.Remove(file);

                        // Also remove from the retained unfiltered source so it
                        // doesn't reappear if filters are cleared/changed later.
                        foreach (var sourceGroup in _allGroups)
                        {
                            sourceGroup.RemoveAll(f => f.Path == file.Path);
                        }

                        processed++;
                        progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                    }
                }
                finally
                {
                    // Runs even if cancellation interrupted the inner loop
                    // mid-group, so a cancelled batch can't leave a stale
                    // "3 copies" header over fewer remaining files. A group
                    // that drops below 2 is pruned even if one of its files
                    // FAILED to delete: the survivor is no longer a duplicate
                    // (its partner is already gone to the bin), so retrying it
                    // would remove the last copy - deliberately NOT offered.
                    // The failure is still surfaced via the returned list.
                    if (group.Files.Count < 2)
                    {
                        Groups.Remove(group);
                    }
                    else
                    {
                        group.GroupLabel = $"{group.Files.Count} copies - {FileSizeFormatter.Format(group.Files[0].SizeBytes)} each";

                        // The file IsLastInGroup pointed at may have just
                        // been removed above - re-flag whichever file is now
                        // actually last so ResultsPage's per-row corner-
                        // rounding/margin doesn't leave the "closing" row
                        // missing after a deletion shrinks this group.
                        foreach (var file in group.Files)
                        {
                            file.IsLastInGroup = ReferenceEquals(file, group.Files[^1]);
                        }
                    }
                }
            }

            // Same delete/prune loop as above, over the Looks-alike photos
            // section instead of Groups - see SimilarityGroupViewModel's own
            // doc comment for why this is a distinct collection rather than
            // reusing DuplicateGroupViewModel. No _allGroups-equivalent
            // retained source to also clean up: SimilarityGroups isn't
            // re-derived from a filtered source the way Groups is (see
            // LoadSimilarityGroups's own comment).
            foreach (var group in SimilarityGroups.ToList())
            {
                try
                {
                    foreach (var file in group.Files.Where(f => f.IsSelected).ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            await Task.Run(() => _recycleBinService.SendToRecycleBin(file.Path, file.SizeBytes, file.LastWriteTimeUtc));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            failed.Add(file.Path);
                            processed++;
                            progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                            continue;
                        }

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
                        group.Files.Remove(file);
                        processed++;
                        progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                    }
                }
                finally
                {
                    if (group.Files.Count < 2)
                    {
                        SimilarityGroups.Remove(group);
                    }
                    else
                    {
                        group.GroupLabel = group.Files.Count == 1 ? "1 similar photo" : $"{group.Files.Count} similar photos";
                    }
                }
            }
        }
        finally
        {
            // Runs even on cancellation, for the same reason as the inner
            // finally above - _allGroups must stay consistent with Groups
            // regardless of how this method exits.
            _allGroups.RemoveAll(g => g.Count < 2);

            // Completes even on cancellation/failure - a partial batch is
            // still a real batch that really removed `processed - failed`
            // files, and History should show what actually happened.
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

    // Threshold used by the "Select Over 100 MB" toolbar button (see
    // ResultsPage.xaml.cs) - named here so the button's behavior and its
    // label can't drift out of sync.
    public const long LargeFileThresholdBytes = 100L * 1024 * 1024;

    // These mass-select methods all operate on Groups (the currently
    // filtered/visible set), not _allGroups - mass-selecting is meant to
    // only affect what the user can currently see, consistent with how
    // filters and selection compose per the design doc ("rules operate
    // only on files currently matching the active filters").
    public void SelectAll() => SetSelection(_ => true);

    public void ClearSelection() => SetSelection(_ => false);

    public void InvertSelection() => SetSelection(f => !f.IsSelected);

    public void SelectBySizeThreshold(long minSizeBytes) =>
        SetSelection(f => f.SizeBytes >= minSizeBytes);

    public void SelectByCategory(MimeCategory category) =>
        SetSelection(f => f.Category == category);

    // "By folder…" (4i) - selects every visible file whose path is under the
    // given folder (the folder itself or any depth of subfolder), matching
    // the same trailing-separator-aware boundary check Settings' trusted-
    // folder matching already uses (AppSettings.IsUnderFolder), so a folder
    // named "camera" doesn't also match a sibling "camera-export".
    public void SelectByFolder(string folderPath) =>
        SetSelection(f => IsUnderFolder(f.Path, folderPath));

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

    /// <summary>
    /// Pre-selects every file except the most recently modified one in each
    /// visible group ("keep the newest, remove the rest") - a simple, defensible
    /// default for the Scan-Completed screen's "Select Recommended" button. The
    /// user can freely override this afterward via any other mass-select rule
    /// or individual checkboxes before actually removing anything - this only
    /// sets checkboxes, it never deletes anything itself.
    /// </summary>
    public void SelectRecommended()
    {
        var rule = App.Settings.PreferredKeepRule;
        foreach (var group in Groups)
        {
            // "Created together" sets (installer/extractor output) are never
            // pre-ticked - deleting part of such a set can break whatever wrote
            // it. The user can still remove them manually.
            if (group.IsLikelyCreatedTogether)
            {
                continue;
            }

            var keeper = ChooseKeeper(group.Files, rule);
            foreach (var file in group.Files)
            {
                // Never pre-select a network-drive file - see SetSelection's
                // identical guard for why (SendToRecycleBin refuses these).
                file.IsSelected = !ReferenceEquals(file, keeper) && !file.IsOnNetworkDrive;
            }
        }
    }

    // Shared by ApplyFilters (which file gets the KEEP pill) and
    // SelectRecommended (which file stays unticked) - both need to agree on
    // the same choice, driven by Settings' "Select Recommended keeps the…"
    // rule, or the pill and the pre-selection could point at different files.
    private static SelectableFile ChooseKeeper(IReadOnlyList<SelectableFile> files, KeepRule rule) => rule switch
    {
        KeepRule.Oldest => files.OrderBy(f => f.LastWriteTimeUtc).First(),
        KeepRule.ShortestPath => files.OrderBy(f => f.Path.Length).First(),
        _ => files.OrderByDescending(f => f.LastWriteTimeUtc).First(),
    };

    private static string KeepLabelFor(KeepRule rule) => rule switch
    {
        KeepRule.Oldest => "KEEP — oldest",
        KeepRule.ShortestPath => "KEEP — shortest path",
        _ => "KEEP — newest",
    };

    // Network-drive files (4i) can never be selected via any mass-select
    // rule - SendToRecycleBin refuses to delete them outright, so letting a
    // rule like "Select All" tick one just sets the user up for a delete
    // failure. `selected(file) && !file.IsOnNetworkDrive` rather than a
    // separate filter step means every current AND future SetSelection
    // caller gets this exclusion automatically. Deselecting (selected
    // returns false) is unaffected either way.
    private void SetSelection(Func<SelectableFile, bool> selected)
    {
        foreach (var file in AllSelectableFiles())
        {
            file.IsSelected = selected(file) && !file.IsOnNetworkDrive;
        }
    }
}
