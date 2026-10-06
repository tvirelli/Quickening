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

    // Image pixel dimensions (F3). Null for non-images / when the similarity
    // pass didn't run. Drive the look-alike "best copy" pick and the
    // resolution shown on similarity rows.
    public int? PixelWidth { get; init; }
    public int? PixelHeight { get; init; }

    // "4032×3024" for display, or "" when unknown.
    public string ResolutionLabel => PixelWidth is { } w && PixelHeight is { } h ? $"{w}×{h}" : "";

    // width*height, 0 when unknown - the "best copy" sort key (highest first).
    public long PixelCount => PixelWidth is { } w && PixelHeight is { } h ? (long)w * h : 0;

    // Laplacian-variance sharpness from the image pass (higher = sharper).
    // Null for non-images or when it wasn't computed. The "best copy" pick's
    // second criterion, after resolution.
    public double? Sharpness { get; init; }

    // Screen readers announce a ListView row by its data item's ToString();
    // without this every file row was read as
    // "Quickening.App.ViewModels.SelectableFile" (QA-6).
    public override string ToString() => $"{System.IO.Path.GetFileName(Path)}, in {System.IO.Path.GetDirectoryName(Path)}";

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

    private bool _isIgnored;

    // True when this file is on the user's Ignore list. Bindable so the row's
    // dimming + IGNORED badge update the instant it's ignored / un-ignored.
    public bool IsIgnored
    {
        get => _isIgnored;
        set
        {
            _isIgnored = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIgnored)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectable)));
        }
    }

    // Drives the row CheckBox's IsEnabled: network-drive files can't be
    // removed from here (SendToRecycleBin refuses them), and ignored files
    // must never be selectable for deletion - the Ignored section reuses the
    // same row template, so without this its checkboxes would LOOK live while
    // selection/deletion (rightly) exclude them.
    public bool IsSelectable => !IsOnNetworkDrive && !IsIgnored;

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

    // Visibility source for the hint pill - HintLabel is assigned at load time
    // (before any row realizes), so a plain computed property suffices.
    public bool HasHintLabel => !string.IsNullOrEmpty(HintLabel);

    // Audio format for same-song rows ("WAV · 24-bit · 48 kHz"), so the user
    // can see why one copy is badged BEST QUALITY. Null elsewhere.
    public string? FormatLabel { get; set; }

    public bool HasFormatLabel => !string.IsNullOrEmpty(FormatLabel);

    // The owning section's left-edge rail colour on the Results page (stamped
    // by ResultsPage.BuildSectionChildren before every rebuild, so it's set
    // before any row realizes - a plain property is enough). Null on pages
    // without sections (Large Files), which simply draw no rail.
    public Microsoft.UI.Xaml.Media.Brush? SectionRailBrush { get; set; }

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
        // Now that HasThumbnail is false the shell type icon is allowed to load;
        // raise its source too so the fallback Image re-reads it instead of
        // keeping the null it saw while the thumbnail was still expected. Without
        // this, formats WIC can't decode (e.g. SVG) left a blank icon cell.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileTypeIconSource)));
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
    // type). Media rows skip it - they show a content thumbnail instead.
    private Microsoft.UI.Xaml.Media.ImageSource? _fileTypeIcon;
    private bool _fileTypeIconRequested;

    // Resolved lazily on first bind, NOT from the row's Loaded event: a
    // virtualized list recycles its containers without re-firing Loaded, so
    // rows scrolled into a recycled container never got their icon. The getter
    // is safe to call on every (re)bind - the provider caches per extension and
    // the result is memoised here per file, so the GDI extraction runs at most
    // once per distinct type.
    public Microsoft.UI.Xaml.Media.ImageSource? FileTypeIconSource
    {
        get
        {
            if (_fileTypeIcon is null && !_fileTypeIconRequested && !HasThumbnail)
            {
                _fileTypeIconRequested = true;
                _fileTypeIcon = Services.ShellIconProvider.GetIconSource(Path);
            }

            return _fileTypeIcon;
        }
    }

    // The icon Border layers three mutually exclusive states: a content
    // thumbnail (images/videos), else the real shell type icon, else the generic
    // category glyph as the always-available fallback.
    public bool ShowFileTypeIcon => !HasThumbnail && FileTypeIconSource is not null;
    public bool ShowCategoryGlyph => !HasThumbnail && FileTypeIconSource is null;
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
/// One "duplicate songs" group (F10) - the same track in several encodings. The
/// label mutates as copies are removed, so it raises PropertyChanged like
/// SimilarityGroupViewModel.
/// </summary>
public sealed class MusicGroupViewModel : INotifyPropertyChanged
{
    private string _songLabel = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string SongLabel
    {
        get => _songLabel;
        set
        {
            _songLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SongLabel)));
        }
    }

    public required ObservableCollection<SelectableFile> Files { get; init; }

    // How the group matched, shown in its header: "Tags match" for the
    // tag-based pass, "Sounds the same · 94%" for deep audio matching.
    public string MatchHint { get; init; } = "";
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

    /// <summary>Duplicate groups before filters - lets the header tell "none found" from "filtered out".</summary>
    public int TotalDuplicateGroupCount => _allGroups.Count;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    // "Looks-alike photos" (new-screens 4k) - unlike Groups, not re-derived
    // from a retained unfiltered source on every ApplyFilters() call: the
    // Duplicates filter row (category/size/date/path/copies) has no visual
    // presence in that section at all, so there's nothing to re-apply.
    public ObservableCollection<SimilarityGroupViewModel> SimilarityGroups { get; } = new();

    // "Blurry photos" review list (F9) - a flat set of likely-blurry images,
    // blurriest first. Never auto-selected; the user reviews and ticks what to
    // remove. Selection/size/deletion flow through AllSelectableFiles() like the
    // other collections.
    public ObservableCollection<SelectableFile> BlurryPhotos { get; } = new();

    // "Duplicate songs" groups (F10) - same track encoded differently. Best copy
    // (highest bitrate) first, tagged BEST QUALITY. Never auto-selected.
    public ObservableCollection<MusicGroupViewModel> MusicGroups { get; } = new();

    // "Duplicate videos" groups (F11) - near-duplicate clips. Reuses
    // SimilarityGroupViewModel (they carry a match %). Never auto-selected.
    public ObservableCollection<SimilarityGroupViewModel> VideoGroups { get; } = new();

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

    // "Extension" dropdown - an exact (case-insensitive) file-extension match,
    // e.g. ".png". The picklist is AvailableExtensions, populated from the loaded
    // results so it only ever offers extensions that are actually present.
    public string? ExtensionFilter { get; set; }

    // Distinct file extensions across the last-loaded results (lower-cased, with
    // the leading dot, sorted), for the Extension dropdown to bind to.
    public IReadOnlyList<string> AvailableExtensions { get; private set; } = Array.Empty<string>();

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

        // Only offer an extension the user can actually filter to a result. A
        // group is pruned once it drops below 2 files (ApplyFilters), so an
        // extension is only useful if SOME group holds 2+ files of it - e.g. a
        // lone "photo.jpg.bak" paired with "photo.jpg" would otherwise list
        // ".bak" but filtering to it prunes the group and shows nothing.
        AvailableExtensions = _allGroups
            .SelectMany(g => g
                .GroupBy(f => System.IO.Path.GetExtension(f.Path).ToLowerInvariant())
                .Where(byExt => !string.IsNullOrEmpty(byExt.Key) && byExt.Count() >= 2)
                .Select(byExt => byExt.Key))
            .Distinct()
            .OrderBy(ext => ext, StringComparer.Ordinal)
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
                PixelWidth = f.PixelWidth,
                PixelHeight = f.PixelHeight,
                Sharpness = f.Sharpness,
            }).ToList();

            // Hint pill (4k's "SHARPER · LARGER"): names the criterion that
            // actually separated the best copy from the rest. A full tie gets
            // no hint rather than an arbitrary pick.
            var (best, reason) = ChooseBestCopy(files);
            if (best is not null && reason is not null)
            {
                best.HintLabel = reason;
            }

            SimilarityGroups.Add(new SimilarityGroupViewModel
            {
                GroupLabel = files.Count == 1 ? "1 similar photo" : $"{files.Count} similar photos",
                MatchPercent = group.MatchPercent,
                Files = new ObservableCollection<SelectableFile>(files),
            });
        }
    }

    // A copy must be at least this much sharper to win on sharpness: the score
    // is noisy, and JPEG blocking can nudge a re-saved copy within a few
    // percent of its original (903.6 vs 885.9 in the QA arena) - that is a
    // tie, not a reason to keep the re-encode.
    private const double ClearlySharperRatio = 1.15;

    // The "best" copy in a look-alike group, by narrowing the candidates one
    // criterion at a time: highest resolution, then clearly sharper, then
    // original format (RAW > lossless > lossy), then largest file. The reason
    // is the criterion that left a single winner - null on a full tie, in which
    // case Best is still the first remaining candidate (keep-best always keeps
    // exactly one) but the UI shows no hint. The old rule (resolution, then
    // bigger file) kept a 117 KB blurry PNG over its sharp 7 KB original and a
    // quality-35 JPEG over the lossless original.
    private static (SelectableFile? Best, string? Reason) ChooseBestCopy(IReadOnlyList<SelectableFile> files)
    {
        if (files.Count < 2)
        {
            return (null, null);
        }

        var candidates = files.ToList();

        string? Narrow(Func<List<SelectableFile>, List<SelectableFile>> keep, string reason)
        {
            var kept = keep(candidates);
            if (kept.Count == 0 || kept.Count == candidates.Count)
            {
                return null;
            }

            candidates = kept;
            return candidates.Count == 1 ? reason : null;
        }

        var reason =
            Narrow(c => { var max = c.Max(f => f.PixelCount); return max > 0 ? c.Where(f => f.PixelCount == max).ToList() : c; }, "HIGHER RES")
            ?? Narrow(c => c.All(f => f.Sharpness is not null)
                ? c.Where(f => f.Sharpness * ClearlySharperRatio >= c.Max(x => x.Sharpness!.Value)).ToList()
                : c, "SHARPER")
            ?? Narrow(c => { var max = c.Max(f => FormatRank(f.Path)); return c.Where(f => FormatRank(f.Path) == max).ToList(); }, "ORIGINAL FORMAT")
            ?? Narrow(c => { var max = c.Max(f => f.SizeBytes); return c.Where(f => f.SizeBytes == max).ToList(); }, "BIGGER FILE");

        return (candidates[0], reason);
    }

    // RAW camera output beats lossless, which beats lossy re-encodes. Unknown
    // extensions (and video) rank equal, so the criterion is skipped for them.
    private static int FormatRank(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".raf" or ".raw" => 3,
        ".png" or ".tif" or ".tiff" or ".bmp" => 2,
        ".jpg" or ".jpeg" or ".heic" or ".heif" or ".webp" or ".avif" => 1,
        _ => 0,
    };

    // F3 keep-best: for each look-alike group, select every copy EXCEPT the
    // best (highest resolution, then largest). A MANUAL action - similar photos
    // are never auto-selected (4k), so the user opts into this explicitly.
    // Always keeps exactly one (the best, or the first when truly tied), and
    // never selects a network-drive file (SendToRecycleBin refuses those).
    public void SelectAllButBestInSimilarityGroups()
    {
        foreach (var group in SimilarityGroups)
        {
            // Ignored files sit outside the keep-best decision entirely: they
            // must be neither chosen as "best" (which would tick every visible
            // file) nor selected for removal.
            var files = group.Files.Where(f => !IgnoreService.IsIgnored(f.Path)).ToList();
            if (files.Count < 2)
            {
                continue;
            }

            var best = ChooseBestCopy(files).Best!;
            foreach (var file in files)
            {
                file.IsSelected = !ReferenceEquals(file, best) && !file.IsOnNetworkDrive;
            }
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
            var keepLabel = KeepLabelFor(rule, keeper, visibleFiles);
            foreach (var file in visibleFiles)
            {
                file.IsKeepRecommended = ReferenceEquals(file, keeper);
                file.KeepRecommendedLabel = keepLabel;
                file.IsLastInGroup = ReferenceEquals(file, visibleFiles[^1]);
                file.IsIgnored = IgnoreService.IsIgnored(file.Path);
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
        if (IgnoreService.IsIgnored(file.Path))
        {
            return false;
        }

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

        if (!string.IsNullOrEmpty(ExtensionFilter)
            && !System.IO.Path.GetExtension(file.Path).Equals(ExtensionFilter, StringComparison.OrdinalIgnoreCase))
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
    /// Sums SizeBytes across every currently-selected file, for the bottom-bar
    /// "X selected" stat in ResultsPage. Distinct by path: the same physical
    /// file can appear in two sections (e.g. blurry AND similar) as two
    /// SelectableFile instances, and counting it twice overstated the stat and
    /// the confirm dialog.
    /// </summary>
    public long GetSelectedSizeBytes() =>
        AllSelectableFiles()
            .Where(f => f.IsSelected)
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Sum(g => g.First().SizeBytes);

    /// <summary>
    /// The paths of every currently-selected file (distinct - see
    /// GetSelectedSizeBytes) - callers capture this BEFORE
    /// DeleteSelectedAsync runs (which removes files from both collections
    /// as it processes them) when they need to know afterward exactly which
    /// paths a since-completed removal touched (e.g. the Undo toast on
    /// Celebration - see ResultsPage.xaml.cs's RemoveSelectedFilesAsync).
    /// </summary>
    public IReadOnlyList<string> GetSelectedFilePaths() =>
        AllSelectableFiles()
            .Where(f => f.IsSelected)
            .Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The single choke point for selection, stats and deletion. Ignored files
    // are EXCLUDED here: unlike Groups (rebuilt ignore-filtered by
    // ApplyFilters), the similarity/blurry/music/video collections are loaded
    // once and never re-filtered, so without this check a hidden ignored file
    // could be swept up by Select All / Invert and then DELETED - the one
    // thing Ignore promises will never happen. Checked live against
    // IgnoreService (an O(1) lookup) rather than the stamped IsIgnored flag so
    // it can never act on a stale stamp.
    private IEnumerable<SelectableFile> AllSelectableFiles() =>
        Groups.SelectMany(g => g.Files)
            .Concat(SimilarityGroups.SelectMany(g => g.Files))
            .Concat(BlurryPhotos)
            .Concat(MusicGroups.SelectMany(g => g.Files))
            .Concat(VideoGroups.SelectMany(g => g.Files))
            .Where(f => !IgnoreService.IsIgnored(f.Path));

    /// <summary>
    /// Clears the selection (and stale KEEP recommendation) of every file that
    /// is now on the ignore list, across ALL sections including the raw
    /// duplicate source. Called after any ignore-list mutation: a file the
    /// user checked and THEN ignored must never ride its leftover checkmark
    /// into Remove Selected.
    /// </summary>
    public void ClearIgnoredSelections()
    {
        var all = _allGroups.SelectMany(g => g)
            .Concat(SimilarityGroups.SelectMany(g => g.Files))
            .Concat(BlurryPhotos)
            .Concat(MusicGroups.SelectMany(g => g.Files))
            .Concat(VideoGroups.SelectMany(g => g.Files));

        foreach (var file in all)
        {
            if (IgnoreService.IsIgnored(file.Path))
            {
                file.IsSelected = false;
                file.IsKeepRecommended = false;
            }
        }
    }

    /// <summary>Every distinct file found in this scan that the user has
    /// ignored - the source for the results page's "Ignored" section. Draws
    /// from the RAW duplicate groups (Groups is already ignore-filtered) plus
    /// the un-filtered similarity/video/music/blurry collections.</summary>
    public IEnumerable<SelectableFile> IgnoredFilesInScan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = _allGroups.SelectMany(g => g)
            .Concat(SimilarityGroups.SelectMany(g => g.Files))
            .Concat(VideoGroups.SelectMany(g => g.Files))
            .Concat(MusicGroups.SelectMany(g => g.Files))
            .Concat(BlurryPhotos);

        foreach (var file in all)
        {
            if (IgnoreService.IsIgnored(file.Path) && seen.Add(file.Path))
            {
                yield return file;
            }
        }
    }

    /// <summary>Distinct paths of every checked file across all sections - for
    /// the "Ignore selected" action.</summary>
    public IReadOnlyList<string> SelectedFilePaths() =>
        AllSelectableFiles().Where(f => f.IsSelected).Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Loads the near-duplicate video groups (F11). Best copy (highest
    /// resolution, then largest) is tagged. Never auto-selected.
    /// </summary>
    public void LoadVideoGroups(IReadOnlyList<Quickening.Core.Similarity.VideoSimilarityEngine.VideoGroup> videoGroups)
    {
        VideoGroups.Clear();
        foreach (var group in videoGroups)
        {
            var files = group.Files.Select(f => new SelectableFile
            {
                Path = f.Path,
                SizeBytes = f.SizeBytes,
                Category = f.Category,
                LastWriteTimeUtc = f.LastWriteTimeUtc,
                CreationTimeUtc = f.CreationTimeUtc,
                PixelWidth = f.PixelWidth,
                PixelHeight = f.PixelHeight,
            }).ToList();

            var (best, reason) = ChooseBestCopy(files);
            if (best is not null && reason is not null)
            {
                best.HintLabel = reason;
            }

            VideoGroups.Add(new SimilarityGroupViewModel
            {
                GroupLabel = files.Count == 1 ? "1 similar video" : $"{files.Count} similar videos",
                MatchPercent = group.MatchPercent,
                Files = new ObservableCollection<SelectableFile>(files),
            });
        }
    }

    /// <summary>
    /// Loads the same-song groups (F10). Copies arrive best-first (highest
    /// bitrate); the best one is tagged BEST QUALITY. Never auto-selected.
    /// </summary>
    public void LoadMusicGroups(IReadOnlyList<Quickening.Core.Audio.AudioDuplicateEngine.MusicGroup> musicGroups)
    {
        MusicGroups.Clear();
        foreach (var group in musicGroups)
        {
            var files = group.Copies.Select(c => new SelectableFile
            {
                Path = c.File.Path,
                SizeBytes = c.File.SizeBytes,
                Category = c.File.Category,
                LastWriteTimeUtc = c.File.LastWriteTimeUtc,
                CreationTimeUtc = c.File.CreationTimeUtc,
            }).ToList();

            if (files.Count > 0)
            {
                files[0].HintLabel = "BEST QUALITY";
            }

            MusicGroups.Add(new MusicGroupViewModel
            {
                SongLabel = group.SongLabel,
                MatchHint = "Tags match",
                Files = new ObservableCollection<SelectableFile>(files),
            });
        }
    }

    /// <summary>
    /// Adds deep-audio "same recording" groups to the songs section, after the
    /// tag-based groups (call LoadMusicGroups first). A group whose files are
    /// all already in one tag group is skipped - the same songs listed twice
    /// would just be noise. Copies are ordered by fidelity; the top one gets
    /// BEST QUALITY only when it's strictly better than the runner-up. Never
    /// auto-selected.
    /// </summary>
    public void LoadSoundGroups(IReadOnlyList<Quickening.Core.Audio.SoundGroup> soundGroups)
    {
        var tagGroups = MusicGroups
            .Select(g => (Group: g, Paths: g.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var byFidelity = Comparer<Quickening.Core.Audio.AudioFidelity>.Create(Quickening.Core.Audio.AudioFidelity.Compare);
        static SelectableFile ToFile(Quickening.Core.Audio.AcousticSignature m) => new()
        {
            Path = m.File.Path,
            SizeBytes = m.File.SizeBytes,
            Category = m.File.Category,
            LastWriteTimeUtc = m.File.LastWriteTimeUtc,
            CreationTimeUtc = m.File.CreationTimeUtc,
            FormatLabel = m.Fidelity.Label,
        };

        foreach (var group in soundGroups)
        {
            // Every file appears in exactly one group: two groups sharing a file
            // would each offer its own checkbox for it, and "all but one" ticked
            // in both could recycle every copy. A sound group overlapping a tag
            // group adds its extra copies to that group instead.
            var overlapping = tagGroups.FirstOrDefault(t => group.Members.Any(m => t.Paths.Contains(m.File.Path)));
            if (overlapping.Group is not null)
            {
                foreach (var extra in group.Members.Where(m => tagGroups.All(t => !t.Paths.Contains(m.File.Path))))
                {
                    overlapping.Group.Files.Add(ToFile(extra));
                    overlapping.Paths.Add(extra.File.Path);
                }

                continue;
            }

            var ordered = group.Members
                .OrderByDescending(m => m.Fidelity, byFidelity)
                .ThenByDescending(m => m.File.SizeBytes)
                .ToList();
            var files = ordered.Select(ToFile).ToList();

            if (Quickening.Core.Audio.AudioFidelity.Compare(ordered[0].Fidelity, ordered[1].Fidelity) > 0)
            {
                files[0].HintLabel = "BEST QUALITY";
            }

            MusicGroups.Add(new MusicGroupViewModel
            {
                // Shortest name, not the first copy's: mastering services prefix
                // their output ("Mixea_MediumNeutral_hd_Reset"), and the plain
                // original name ("Reset") is the better title.
                SongLabel = ordered
                    .Select(m => System.IO.Path.GetFileNameWithoutExtension(m.File.Path))
                    .OrderBy(name => name.Length)
                    .First(),
                MatchHint = $"Sounds the same · {group.MatchPercent}%",
                Files = new ObservableCollection<SelectableFile>(files),
            });
        }
    }

    /// <summary>
    /// Loads the likely-blurry photos (F9) as a flat, never-auto-selected review
    /// list. Carries pixel dimensions so the row can show resolution, matching
    /// the look-alike rows.
    /// </summary>
    public void LoadBlurryPhotos(IReadOnlyList<Quickening.Core.Models.FileRecord> blurry)
    {
        BlurryPhotos.Clear();
        foreach (var f in blurry)
        {
            BlurryPhotos.Add(new SelectableFile
            {
                Path = f.Path,
                SizeBytes = f.SizeBytes,
                Category = f.Category,
                LastWriteTimeUtc = f.LastWriteTimeUtc,
                CreationTimeUtc = f.CreationTimeUtc,
                PixelWidth = f.PixelWidth,
                PixelHeight = f.PixelHeight,
            });
        }
    }

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
                    // !IsIgnored: last-line defense, same as every other section's
                    // loop below. Groups is rebuilt ignore-filtered by every
                    // ignore action today, but this loop must not depend on that.
                    foreach (var file in group.Files.Where(f => f.IsSelected && !IgnoreService.IsIgnored(f.Path)).ToList())
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
                    // !IsIgnored: last-line defense - an ignored file must never
                    // be deleted even if a stale IsSelected survived (the same
                    // guard appears on every non-duplicates loop below; Groups
                    // itself is already rebuilt ignore-filtered).
                    foreach (var file in group.Files.Where(f => f.IsSelected && !IgnoreService.IsIgnored(f.Path)).ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Already recycled via another section this batch (the
                        // same physical file can appear in two sections as two
                        // instances) - skip, and still count it toward progress
                        // so the bar can reach N/N.
                        if (!File.Exists(file.Path))
                        {
                            group.Files.Remove(file);
                            processed++;
                            progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                            continue;
                        }

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

            // Same delete loop over the Blurry-photos review list (F9). Flat, so
            // no group-prune step. A file already recycled via another section
            // this batch (a blurry photo that was also a duplicate/look-alike the
            // user ticked) is skipped, not re-deleted or counted as failed.
            foreach (var file in BlurryPhotos.Where(f => f.IsSelected && !IgnoreService.IsIgnored(f.Path)).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(file.Path))
                {
                    BlurryPhotos.Remove(file);
                    processed++;
                    progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                    continue;
                }

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
                BlurryPhotos.Remove(file);
                processed++;
                progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
            }

            // Same delete/prune loop over the Duplicate-songs groups (F10).
            foreach (var group in MusicGroups.ToList())
            {
                try
                {
                    foreach (var file in group.Files.Where(f => f.IsSelected && !IgnoreService.IsIgnored(f.Path)).ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!File.Exists(file.Path))
                        {
                            group.Files.Remove(file);
                            processed++;
                            progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                            continue;
                        }

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
                        MusicGroups.Remove(group);
                    }
                }
            }

            // Same delete/prune loop over the Duplicate-videos groups (F11).
            foreach (var group in VideoGroups.ToList())
            {
                try
                {
                    foreach (var file in group.Files.Where(f => f.IsSelected && !IgnoreService.IsIgnored(f.Path)).ToList())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!File.Exists(file.Path))
                        {
                            group.Files.Remove(file);
                            processed++;
                            progress?.Report(new DeleteProgress(processed, totalFiles, file.Path));
                            continue;
                        }

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
                        VideoGroups.Remove(group);
                    }
                    else
                    {
                        group.GroupLabel = group.Files.Count == 1 ? "1 similar video" : $"{group.Files.Count} similar videos";
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

    // "KEEP — newest" etc., or plain "KEEP" when the rule didn't really
    // decide: rows show times to the second, so copies made in one go all
    // read e.g. 8:08:16 PM and calling one "newest" (won on fractions of a
    // second) looked arbitrary. Same for equal-length paths.
    private static string KeepLabelFor(KeepRule rule, SelectableFile keeper, IReadOnlyList<SelectableFile> files)
    {
        static long Second(DateTime t) => t.Ticks / TimeSpan.TicksPerSecond;

        var tied = rule switch
        {
            KeepRule.ShortestPath => files.Any(f => !ReferenceEquals(f, keeper) && f.Path.Length == keeper.Path.Length),
            _ => files.Any(f => !ReferenceEquals(f, keeper) && Second(f.LastWriteTimeUtc) == Second(keeper.LastWriteTimeUtc)),
        };

        return tied
            ? "KEEP"
            : rule switch
            {
                KeepRule.Oldest => "KEEP — oldest",
                KeepRule.ShortestPath => "KEEP — shortest path",
                _ => "KEEP — newest",
            };
    }

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
