using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Quickening.App.Controls;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Quickening.Core.Orchestration;
using Windows.UI;

namespace Quickening.App.Views;

public sealed partial class ResultsPage : Page
{
    // Tracks which ScanResult instance is currently loaded so that
    // re-navigating to this page without a new scan (e.g. Home -> Results
    // via the nav rail) is a no-op - NavigationCacheMode="Enabled" on this
    // page (see ResultsPage.xaml) means the Frame reuses this same page/
    // view-model instance, so without this check a re-navigation would
    // reload DuplicateGroups from the original ScanResult and silently
    // resurrect files the user already deleted.
    private ScanResult? _loadedResult;
    private string? _targetLabel;

    // Held as its own field (not just passed inline into ResultsViewModel's
    // constructor) so RemoveSelectedFilesAsync can hand this SAME instance
    // to CelebrationParameters - Undo only works against the exact
    // IRecycleBinService that performed the delete (see
    // RecycleBinService.TryRestore's own doc comment on why the captured
    // Recycle Bin locations live in that instance's memory, not anywhere
    // persisted/shared).
    private readonly IRecycleBinService _recycleBinService = new RecycleBinService();
    private readonly ResultsViewModel _viewModel;

    public ResultsPage()
    {
        _viewModel = new ResultsViewModel(_recycleBinService, App.Store);
        InitializeComponent();
        GroupsListView.ItemTemplateSelector = new RowTemplateSelector
        {
            SectionTemplate = (DataTemplate)Resources["SectionBarRowTemplate"],
            SubHeaderTemplate = (DataTemplate)Resources["SubHeaderRowTemplate"],
            HeaderTemplate = (DataTemplate)Resources["ResultsHeaderRowTemplate"],
            FileTemplate = (DataTemplate)Resources["ResultsFileRowTemplate"],
        };
        EnsureSectionRows();
        ComparisonViewer.CloseRequested += (_, _) => ComparisonViewer.Visibility = Visibility.Collapsed;
    }

    // Set while ClearFilters_Click is resetting each control's value, so
    // FilterChanged/CategorySidebar_SelectionChanged (whose own
    // ValueChanged/SelectionChanged events still fire even for a
    // programmatic assignment) don't apply filters once per control - up to
    // several back-to-back full ListView rebuilds (visible flicker) on a
    // single button click. Sync and apply exactly once after all controls
    // are reset instead.
    private bool _suppressFilterControlEvents;

    // Backs the "⚡ Recommended applied" indicator pill (screen 2g) - true
    // only when SelectRecommended() was actually invoked for the CURRENTLY
    // loaded result (i.e. the user arrived here via Scan-Completed's
    // "Select Recommended" button, per ResultsNavigationRequest.
    // PreSelectRecommended), false for a plain "Review Results" arrival or
    // for any later result load that didn't request pre-selection. Nothing
    // clears this once a user starts manually toggling checkboxes - the pill
    // is a one-time "this batch was pre-selected for you" indicator, not a
    // live "does the current selection still match Recommended" tracker.
    private bool _recommendedApplied;

    // A duplicate group's header row, flattened into the row list alongside its
    // own SelectableFile rows - see RebuildFlatRows's doc comment.
    private sealed class GroupHeaderRow
    {
        public required DuplicateGroupViewModel Group { get; init; }

        // The owning section's left-edge rail colour (see SectionRow.RailBrush).
        public Brush? SectionRailBrush { get; init; }

        // Screen readers announce a ListView item by its data item's ToString();
        // without this they read the class name (QA-6).
        public override string ToString() => Group.GroupLabel;
    }

    // A collapsible SECTION bar (Duplicates / Similar photos / … / Ignored). One
    // instance per section, kept alive across rebuilds so its IsExpanded state
    // persists. The whole results list is a SINGLE virtualized list of these
    // plus GroupHeaderRow / SubHeaderRow / SelectableFile rows; collapse and
    // filter just rebuild that lightweight data list, never touching UI
    // elements for off-screen rows. INotifyPropertyChanged so the visible bar's
    // chevron/count update in place on toggle.
    private sealed class SectionRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required Brush DotBrush { get; init; }
        public required Brush BarFillBrush { get; init; }
        public required Brush BarBorderBrush { get; init; }

        // The vertical rail drawn down the left edge of this section's child
        // rows (same hue as the dot) - what visually ties the children to
        // their collapsible bar.
        public required Brush RailBrush { get; init; }

        public string? BadgeText { get; init; }
        public Brush? BadgeFillBrush { get; init; }
        public Brush? BadgeBorderBrush { get; init; }
        public Brush? BadgeTextBrush { get; init; }
        public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

        private int _fileCount;
        public int FileCount
        {
            get => _fileCount;
            set { if (_fileCount != value) { _fileCount = value; Raise(nameof(CountLabel)); } }
        }

        public string CountLabel => $"{FileCount} file{(FileCount == 1 ? "" : "s")}";

        // Screen readers announce a ListView item by its data item's ToString();
        // without this they read the class name (QA-6).
        public override string ToString() => $"{Title}, {CountLabel}";

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    Raise(nameof(ChevronGlyph));
                    Raise(nameof(AutomationLabel));
                }
            }
        }

        // ▾ open / ▸ closed - reads unambiguously as a collapse control.
        public string ChevronGlyph => IsExpanded ? "▾" : "▸";

        // Screen-reader name for the bar - announces the collapse state, which
        // the visual chevron alone doesn't convey through UIA.
        public string AutomationLabel => $"{Title}, {(IsExpanded ? "expanded" : "collapsed")}";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    // A sub-group header inside a section: a look-alike cluster, a song, or the
    // single card that caps a flat blurry/ignored list. Rounds the card's top.
    private sealed class SubHeaderRow
    {
        public required string Title { get; init; }
        public string? Hint { get; init; }
        public Brush? HintFillBrush { get; init; }
        public Brush? HintBorderBrush { get; init; }
        public Brush? HintTextBrush { get; init; }
        public bool HasHint => !string.IsNullOrEmpty(Hint);

        // The owning section's left-edge rail colour (see SectionRow.RailBrush).
        public Brush? SectionRailBrush { get; init; }

        // Screen readers announce a ListView item by its data item's ToString();
        // without this they read the class name (QA-6).
        public override string ToString() => HasHint ? $"{Title}, {Hint}" : Title;
    }

    // Picks GroupsListView's per-item template by row kind. Built in code rather
    // than declared in XAML since the row types are private nested types XAML
    // can't reference by name.
    private sealed class RowTemplateSelector : DataTemplateSelector
    {
        public required DataTemplate SectionTemplate { get; init; }
        public required DataTemplate SubHeaderTemplate { get; init; }
        public required DataTemplate HeaderTemplate { get; init; }
        public required DataTemplate FileTemplate { get; init; }

        protected override DataTemplate SelectTemplateCore(object item) => item switch
        {
            SectionRow => SectionTemplate,
            SubHeaderRow => SubHeaderTemplate,
            GroupHeaderRow => HeaderTemplate,
            _ => FileTemplate,
        };

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
            SelectTemplateCore(item);
    }

    // The result-type sections and their display order. Duplicates is always
    // first. The sidebar "SHOW" checklist is built from this list, one checkbox
    // per section actually present.
    private static readonly (string Key, string Label)[] SectionDefs =
    {
        ("duplicates", "Duplicates"),
        ("similar", "Similar photos"),
        ("video", "Similar videos"),
        ("music", "Same song"),
        ("blurry", "Blurry photos"),
        ("ignored", "Ignored"),
    };

    // Per-section visuals: title, dot colour, and the type badge (fill/border
    // are alpha-tinted from the base, text is the light shade).
    private static readonly Dictionary<string, (string Title, uint Dot, string Badge, uint BadgeBase, uint BadgeText)> SectionMeta = new()
    {
        ["duplicates"] = ("Duplicates", 0x5B8CFF, "EXACT COPIES", 0x5B8CFF, 0xB9CCFF),
        ["similar"] = ("Similar photos", 0x8C6EFF, "SIMILAR · NOT IDENTICAL", 0x8C6EFF, 0xB9A5FF),
        ["video"] = ("Similar videos", 0xB06EFF, "SAME CLIP · DIFFERENT FILE", 0xB06EFF, 0xD3B9FF),
        ["music"] = ("Same song", 0x5EE7B7, "SAME SONG · DIFFERENT FILE", 0x5EE7B7, 0x9CF0D0),
        ["blurry"] = ("Blurry photos", 0xFF9A3D, "LIKELY BLURRY · REVIEW", 0xFF9A3D, 0xFFC78F),
        ["ignored"] = ("Ignored", 0x6B7699, "KEPT ON PURPOSE", 0x6B7699, 0xB6BFD8),
    };

    // Which sections the SHOW filter currently includes. Seeded with everything
    // except "ignored" so the first RebuildFlatRows (before BuildSectionFilters
    // recomputes this) already shows the normal sections.
    private readonly HashSet<string> _visibleSections =
        new(StringComparer.Ordinal) { "duplicates", "similar", "video", "music", "blurry" };

    // The live SectionRow instances (one per key), kept across rebuilds so each
    // section remembers whether it's expanded.
    private readonly Dictionary<string, SectionRow> _sections = new(StringComparer.Ordinal);

    private static SolidColorBrush Rgb(uint rgb, byte a = 0xFF) =>
        new(Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    // Creates the six SectionRow instances once (idempotent). Every key exists
    // regardless of whether the scan has that content - RebuildFlatRows skips
    // empty ones - so a section that only appears later (e.g. "Ignored" after
    // the user ignores something) always has its row ready.
    private void EnsureSectionRows()
    {
        if (_sections.Count > 0)
        {
            return;
        }

        foreach (var (key, _) in SectionDefs)
        {
            var m = SectionMeta[key];
            _sections[key] = new SectionRow
            {
                Key = key,
                Title = m.Title,
                DotBrush = Rgb(m.Dot),
                BarFillBrush = Rgb(m.Dot, 0x1A),
                BarBorderBrush = Rgb(m.Dot, 0x40),
                RailBrush = Rgb(m.Dot, 0x8C),
                BadgeText = m.Badge,
                BadgeFillBrush = Rgb(m.BadgeBase, 0x24),
                BadgeBorderBrush = Rgb(m.BadgeBase, 0x66),
                BadgeTextBrush = Rgb(m.BadgeText),
            };
        }
    }

    // Rebuilds the SINGLE virtualized row list from the view-model - a section
    // bar, then (if expanded) that section's sub-headers and file rows,
    // section by section. Runs after anything that changes membership (load,
    // filter, collapse, deletion). It only touches lightweight DATA objects;
    // the ListView virtualizes elements, so this stays cheap even for tens of
    // thousands of files. Assigning a fresh List in one shot (rather than
    // mutating an ObservableCollection item-by-item) avoids a storm of
    // per-item CollectionChanged events on every rebuild.
    private void RebuildFlatRows()
    {
        EnsureSectionRows();
        var rows = new List<object>();

        foreach (var (key, _) in SectionDefs)
        {
            if (!_visibleSections.Contains(key))
            {
                continue;
            }

            var children = BuildSectionChildren(key, out var fileCount);
            if (fileCount == 0)
            {
                continue;
            }

            var section = _sections[key];
            section.FileCount = fileCount;
            rows.Add(section);
            if (section.IsExpanded)
            {
                rows.AddRange(children);
            }
        }

        GroupsListView.ItemsSource = rows;
        EmptyStateText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Builds one section's child rows (sub-headers + file rows) and reports its
    // visible file count. Never includes the section bar itself.
    private List<object> BuildSectionChildren(string key, out int fileCount)
    {
        EnsureSectionRows();
        var rows = new List<object>();
        fileCount = 0;

        // Every child row carries its section's rail colour - the vertical
        // stripe down the children's left edge that visually ties them to
        // their collapsible bar.
        var rail = _sections[key].RailBrush;

        switch (key)
        {
            case "duplicates":
                // _viewModel.Groups is already filtered + IsLastInGroup-stamped
                // by ApplyFilters.
                foreach (var group in _viewModel.Groups)
                {
                    rows.Add(new GroupHeaderRow { Group = group, SectionRailBrush = rail });
                    foreach (var file in group.Files)
                    {
                        file.SectionRailBrush = rail;
                        rows.Add(file);
                        fileCount++;
                    }
                }
                break;

            case "similar":
                fileCount += AppendClusters(rows, _viewModel.SimilarityGroups, 0x8C6EFF, 0xB9A5FF, rail);
                break;

            case "video":
                fileCount += AppendClusters(rows, _viewModel.VideoGroups, 0xB06EFF, 0xD3B9FF, rail);
                break;

            case "music":
                foreach (var group in _viewModel.MusicGroups)
                {
                    var visible = VisibleRows(group.Files);
                    if (visible.Count < 2)
                    {
                        continue;
                    }

                    StampLastInGroup(visible, rail);
                    rows.Add(new SubHeaderRow
                    {
                        Title = group.SongLabel,
                        Hint = string.IsNullOrEmpty(group.MatchHint)
                            ? $"{visible.Count} copies"
                            : $"{group.MatchHint} · {visible.Count} copies",
                        HintFillBrush = Rgb(0x5EE7B7, 0x24),
                        HintBorderBrush = Rgb(0x5EE7B7, 0x66),
                        HintTextBrush = Rgb(0x9CF0D0),
                        SectionRailBrush = rail,
                    });
                    rows.AddRange(visible);
                    fileCount += visible.Count;
                }
                break;

            case "blurry":
                fileCount += AppendFlatCard(rows, VisibleRows(_viewModel.BlurryPhotos), "Sharpness below your threshold", rail);
                break;

            case "ignored":
                var ignored = _viewModel.IgnoredFilesInScan().ToList();
                foreach (var file in ignored)
                {
                    file.IsIgnored = true; // so the row's context menu offers "Stop ignoring"
                }
                fileCount += AppendFlatCard(rows, ignored, "Won't appear in future scans", rail);
                break;
        }

        return rows;
    }

    // Similar-photo / similar-video clusters: a sub-header per cluster (rep
    // filename + match %), then its files.
    private int AppendClusters(List<object> rows, IEnumerable<SimilarityGroupViewModel> groups, uint hintBase, uint hintText, Brush rail)
    {
        var count = 0;
        foreach (var group in groups)
        {
            var visible = VisibleRows(group.Files);
            if (visible.Count < 2)
            {
                continue;
            }

            StampLastInGroup(visible, rail);
            rows.Add(new SubHeaderRow
            {
                Title = $"{System.IO.Path.GetFileName(visible[0].Path)} + {visible.Count - 1} more",
                Hint = $"{group.MatchPercent}% match",
                HintFillBrush = Rgb(hintBase, 0x24),
                HintBorderBrush = Rgb(hintBase, 0x66),
                HintTextBrush = Rgb(hintText),
                SectionRailBrush = rail,
            });
            rows.AddRange(visible);
            count += visible.Count;
        }

        return count;
    }

    // A flat, header-less section (blurry / ignored): one capping sub-header,
    // then all its files as a single card.
    private int AppendFlatCard(List<object> rows, List<SelectableFile> files, string subtitle, Brush rail)
    {
        if (files.Count == 0)
        {
            return 0;
        }

        StampLastInGroup(files, rail);
        rows.Add(new SubHeaderRow { Title = subtitle, SectionRailBrush = rail });
        rows.AddRange(files);
        return files.Count;
    }

    private static void StampLastInGroup(IReadOnlyList<SelectableFile> files, Brush rail)
    {
        for (var i = 0; i < files.Count; i++)
        {
            files[i].IsLastInGroup = i == files.Count - 1;
            files[i].SectionRailBrush = rail;
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var (scanResult, preSelectRecommended, targetLabel) = e.Parameter switch
        {
            ScanResult sr => (sr, false, (string?)null),
            ResultsNavigationRequest req => (req.ScanResult, req.PreSelectRecommended, req.TargetLabel),
            _ => ((ScanResult?)null, false, (string?)null),
        };

        if (scanResult is not null && !ReferenceEquals(scanResult, _loadedResult))
        {
            _loadedResult = scanResult;
            _targetLabel = targetLabel;
            // A NEW result load resets the pill - it describes the currently
            // loaded batch, not some earlier scan's pre-selection (see the
            // field's own doc comment).
            _recommendedApplied = false;
            ScreenTitleText.Text = string.IsNullOrWhiteSpace(targetLabel) ? "Duplicates" : $"Duplicates in {targetLabel}";

            // Reset all filter state up front - NavigationCacheMode="Enabled"
            // reuses this same page/view-model instance, so without this a
            // leftover filter (size, or the sidebar's own SelectedIndex ->
            // SelectionChanged -> CategoryFilter write) from a PREVIOUS scan
            // would either silently carry into this new scan's results, or
            // trigger a redundant second ApplyFilters() pass when
            // PopulateCategorySidebar resets the sidebar below - the same
            // double-rebuild class ClearFilters_Click guards against.
            _suppressFilterControlEvents = true;
            ResetFilterControls();
            _viewModel.CategoryFilter.Clear();
            _viewModel.MinSizeBytes = null;
            _viewModel.MaxSizeBytes = null;
            _viewModel.MinGroupSize = null;
            _viewModel.ModifiedAfter = null;
            _viewModel.ModifiedBefore = null;
            _viewModel.PathContains = null;
            _viewModel.ExtensionFilter = null;

            _viewModel.LoadGroups(scanResult.DuplicateGroups);
            _viewModel.LoadSimilarityGroups(scanResult.SimilarityGroups);
            _viewModel.LoadBlurryPhotos(scanResult.BlurryPhotos);
            _viewModel.LoadMusicGroups(scanResult.MusicGroups);
            _viewModel.LoadSoundGroups(scanResult.SoundGroups);
            _viewModel.LoadVideoGroups(scanResult.VideoGroups);
            PopulateCategorySidebar(scanResult);
            PopulateExtensionCombo();
            _suppressFilterControlEvents = false;

            BuildSectionFilters();
            RefreshSummary();
            RefreshSelectedSizeStat();
        }
        else if (_loadedResult is not null)
        {
            // Same ScanResult re-entering the cached page. The ignore list may
            // have changed elsewhere in the meantime (Manage Ignored Files,
            // the Large Files page), and an abnormal removal exit (unexpected
            // error mid-delete, navigating away from the progress screen)
            // skips the Completed/OnCancelled refreshes - either way the flat
            // rows/counts here are stale. Re-derive from data (cheap, no
            // element construction) so what's shown always matches reality.
            _viewModel.ClearIgnoredSelections();
            _viewModel.ApplyFilters();
            BuildSectionFilters();
            RefreshSummary();
            RefreshSelectedSizeStat();
        }

        // Applied whenever the caller asks for it, independent of the
        // ReferenceEquals guard above - a user can arrive here once via a
        // plain "Review Results" (guard runs, pill stays hidden), navigate
        // back, then click "Select Recommended" for that SAME already-loaded
        // ScanResult. Nesting this inside the guard would silently skip
        // both the selection and the pill on that second, explicit request.
        if (preSelectRecommended)
        {
            _recommendedApplied = true;
            _viewModel.SelectRecommended();
            RefreshSummary();
            RefreshSelectedSizeStat();
        }

        RecommendedAppliedPill.Visibility = _recommendedApplied ? Visibility.Visible : Visibility.Collapsed;
    }

    // Replaces the pre-Task-3 shared MainWindow.SetBackAction mechanism -
    // the new design has no persistent top-bar back button (see Task 3),
    // so each page that needs one draws its own inline link (the
    // "← Summary" TextBlock in ResultsPage.xaml) and wires it directly to
    // the same destination the old back button used to navigate to.
    private void BackToSummary_Click(object sender, RoutedEventArgs e) =>
        // _targetLabel rides along so the summary's eyebrow still reads
        // "SCAN COMPLETE — DOWNLOADS" (not the generic "— YOUR FILES") after
        // the round trip, matching LargeFilesResultsPage's equivalent.
        ((MainWindow)App.MainWindowInstance!).ShowScanComplete(_loadedResult!, targetLabel: _targetLabel);

    private void PopulateCategorySidebar(ScanResult scanResult)
    {
        var breakdown = CategoryBreakdownCalculator.Calculate(scanResult.DuplicateGroups);

        var items = new List<CategorySidebarItem> { new(Category: null, Label: "All") };
        items.AddRange(breakdown.Select(b => new CategorySidebarItem(b.Category, ChipLabel(b.Category))));

        CategorySidebar.ItemsSource = items;
        items[0].IsSelected = true; // "All"
        RefreshCategoryCounts();
    }

    // Recounts the CATEGORY checklist from the loaded duplicates, leaving out
    // ignored files - they're hidden from every section, so counting them made
    // "All 13 / Images 2" disagree with "Duplicates 11" after an ignore (QA-5).
    // Updates in place, so the current category selection is kept.
    private void RefreshCategoryCounts()
    {
        if (_loadedResult is null || CategorySidebar.ItemsSource is not IEnumerable<CategorySidebarItem> items)
        {
            return;
        }

        // Per group, like the list itself: drop ignored files, and drop a group
        // left with one file - its survivor is no longer a duplicate and isn't
        // shown, so counting it would still disagree with the Duplicates count.
        var files = _loadedResult.DuplicateGroups
            .Select(g => g.Files.Where(f => !IgnoreService.IsIgnored(f.Path)).ToList())
            .Where(visible => visible.Count >= 2)
            .SelectMany(visible => visible)
            .ToList();
        foreach (var item in items)
        {
            item.Count = item.Category is { } category ? files.Count(f => f.Category == category) : files.Count;
        }
    }

    // Short chip labels exactly as screen 2g shows them ("Docs"/"Exe", not
    // CategoryLabelFormatter.PluralName's "Documents"/"Executables" - that
    // formatter's fuller wording is right for the Scan-Completed donut
    // legend it was built for, but 2g's filter chips use these shorter
    // forms with no byte count alongside).
    private static string ChipLabel(MimeCategory category) => category switch
    {
        MimeCategory.Image => "Images",
        MimeCategory.Video => "Videos",
        MimeCategory.Audio => "Audio",
        MimeCategory.Document => "Docs",
        MimeCategory.Archive => "Archives",
        MimeCategory.Executable => "Exe",
        MimeCategory.Other => "Other",
        _ => category.ToString(),
    };

    // The category rail is a multi-select checklist: tap specific categories to
    // include several at once, or tap "All" to clear the category filter. "All"
    // is checked exactly when no specific category is.
    private void CategoryItem_Click(object sender, ItemClickEventArgs e)
    {
        if (_suppressFilterControlEvents || e.ClickedItem is not CategorySidebarItem clicked
            || CategorySidebar.ItemsSource is not IEnumerable<CategorySidebarItem> source)
        {
            return;
        }

        var items = source.ToList();
        if (clicked.Category is null)
        {
            // "All" resets the category filter.
            foreach (var item in items)
            {
                item.IsSelected = item.Category is null;
            }
        }
        else
        {
            clicked.IsSelected = !clicked.IsSelected;
            var anySpecific = items.Any(item => item.Category is not null && item.IsSelected);
            foreach (var item in items.Where(item => item.Category is null))
            {
                item.IsSelected = !anySpecific; // "All" mirrors "nothing specific chosen"
            }
        }

        _viewModel.CategoryFilter.Clear();
        foreach (var item in items.Where(item => item is { Category: not null, IsSelected: true }))
        {
            _viewModel.CategoryFilter.Add(item.Category!.Value);
        }

        _viewModel.ApplyFilters();
        RefreshSummary();
        RefreshSelectedSizeStat();
    }

    // Resets the category checklist to "All" (no category filter).
    private void ResetCategorySelection()
    {
        if (CategorySidebar.ItemsSource is IEnumerable<CategorySidebarItem> items)
        {
            foreach (var item in items)
            {
                item.IsSelected = item.Category is null;
            }
        }
    }

    // "128 groups · 312 files · 9.4 GB reclaimable" - screen 2g's exact
    // stats-line wording, computed from the currently-visible (filtered)
    // Groups rather than the old "N duplicate group(s) out of M files
    // scanned" copy, which no longer matches the new design's copy. All
    // three numbers are derived directly from _viewModel.Groups so they can
    // never drift from what the list below is actually showing.
    private void RefreshSummary()
    {
        // Every call site here follows a group/file-membership change
        // (fresh load, filter change, deletion) - the exact moments the flat
        // row list also needs rebuilding, so this one call covers both.
        RebuildFlatRows();

        var groupCount = _viewModel.Groups.Count;
        var fileCount = _viewModel.Groups.Sum(g => g.Files.Count);
        var reclaimableBytes = _viewModel.Groups.Sum(g => (long)(g.Files.Count - 1) * g.Files[0].SizeBytes);

        // "duplicate group(s)", not just "group(s)": the list below now holds
        // several section types, and these numbers cover the Duplicates
        // section only (each other section shows its own count on its bar).
        SummaryText.Text = $"{groupCount} duplicate group{(groupCount == 1 ? "" : "s")} · "
            + $"{fileCount} file{(fileCount == 1 ? "" : "s")} · "
            + $"{FileSizeFormatter.Format(reclaimableBytes)} reclaimable";

        // Slim per-list-header count next to the Select toolbar.
        ResultCountText.Text = $"{groupCount} duplicate group{(groupCount == 1 ? "" : "s")}";
    }

    // Is this section present in the loaded scan at all (independent of the SHOW
    // filter)? Drives which SHOW checkboxes get drawn.
    private bool SectionPresent(string key) => key switch
    {
        "duplicates" => (_loadedResult?.DuplicateGroups.Count ?? 0) > 0,
        "similar" => _viewModel.SimilarityGroups.Count > 0,
        "video" => _viewModel.VideoGroups.Count > 0,
        "music" => _viewModel.MusicGroups.Count > 0,
        "blurry" => _viewModel.BlurryPhotos.Count > 0,
        "ignored" => _viewModel.IgnoredFilesInScan().Any(),
        _ => false,
    };

    // (Re)draws the sidebar SHOW checklist - one row per section present in this
    // scan, styled exactly like the CATEGORY checklist (check box + colour dot
    // matching the section bar + live file count). Everything but "ignored"
    // starts checked; the user's choices are preserved across rebuilds.
    private void BuildSectionFilters()
    {
        var prior = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (SectionSidebar.ItemsSource is IEnumerable<SectionFilterItem> existing)
        {
            foreach (var item in existing)
            {
                prior[item.Key] = item.IsSelected;
            }
        }

        _visibleSections.Clear();
        var items = new List<SectionFilterItem>();

        foreach (var (key, _) in SectionDefs)
        {
            if (!SectionPresent(key))
            {
                continue;
            }

            // Count the section's currently-visible files (skips ignored + the
            // sub-group-of-one clusters), so a "present but nothing to show"
            // section (e.g. every similar cluster collapsed to one file) is
            // omitted rather than listed with a 0.
            var count = SectionVisibleCount(key);
            if (count == 0)
            {
                continue;
            }

            var on = prior.TryGetValue(key, out var was) ? was : key != "ignored";
            if (on)
            {
                _visibleSections.Add(key);
            }

            var meta = SectionMeta[key];
            items.Add(new SectionFilterItem(key, meta.Title, Rgb(meta.Dot)) { Count = count, IsSelected = on });
        }

        SectionSidebar.ItemsSource = items;

        // Hide the whole SHOW group when there's only one kind of result (just
        // duplicates) - a lone "Duplicates" toggle is noise.
        SectionFilterGroup.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Visible file count for a section (ignore + <2-cluster filtering applied),
    // without emitting rows.
    private int SectionVisibleCount(string key)
    {
        BuildSectionChildren(key, out var count);
        return count;
    }

    private void SectionItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SectionFilterItem item)
        {
            return;
        }

        item.IsSelected = !item.IsSelected;
        if (item.IsSelected)
        {
            _visibleSections.Add(item.Key);
        }
        else
        {
            _visibleSections.Remove(item.Key);
        }

        // Every section lives in the one virtualized list - a single rebuild
        // adds/drops it. No per-kind branching, no element construction.
        RebuildFlatRows();
    }

    // Fold/unfold a section from its bar. Rebuilds the flat list (data only, so
    // instant) and keeps the clicked bar in view so the list doesn't jump.
    private void SectionHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SectionRow section })
        {
            return;
        }

        var hadFocus = sender is Control { FocusState: not FocusState.Unfocused };
        section.IsExpanded = !section.IsExpanded;
        RebuildFlatRows();
        GroupsListView.ScrollIntoView(section, ScrollIntoViewAlignment.Leading);

        // The rebuild recycles the bar's container, so focus fell through to
        // the next focusable control - Remove Selected, where a keyboard user's
        // next Enter would start a removal (QA-8). Once the rebuilt row is
        // realized, hand focus back to this section's bar.
        if (hadFocus)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (GroupsListView.ContainerFromItem(section) is DependencyObject container
                    && FindDescendant<Button>(container) is { } bar)
                {
                    bar.Focus(FocusState.Programmatic);
                }
            });
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    // Stamps each file's IsIgnored and returns only the non-ignored ones -
    // ignored files are surfaced in their own "Ignored" section, not inline.
    // The returned list references the same SelectableFile instances, so
    // selection/deletion still act on the originals.
    private List<SelectableFile> VisibleRows(IEnumerable<SelectableFile> files)
    {
        var result = new List<SelectableFile>();
        foreach (var file in files)
        {
            file.IsIgnored = IgnoreService.IsIgnored(file.Path);
            if (!file.IsIgnored)
            {
                result.Add(file);
            }
        }

        return result;
    }

    // Applying filters rebuilds every group view-model and the whole
    // flattened ListView collection - doing that per KEYSTROKE in the
    // path/date boxes is O(rows) of UI churn per character. Debounce so
    // typing settles first; the category sidebar and Clear button still
    // apply immediately (they're single deliberate clicks).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterDebounceTimer;

    private void FilterChanged(object sender, object e)
    {
        if (_suppressFilterControlEvents)
        {
            return;
        }

        if (_filterDebounceTimer is null)
        {
            _filterDebounceTimer = DispatcherQueue.CreateTimer();
            _filterDebounceTimer.Interval = TimeSpan.FromMilliseconds(250);
            _filterDebounceTimer.IsRepeating = false;
            _filterDebounceTimer.Tick += (_, _) => SyncFiltersFromControlsAndApply();
        }

        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _filterDebounceTimer?.Stop();
        _suppressFilterControlEvents = true;
        ResetFilterControls();
        ResetCategorySelection();
        _suppressFilterControlEvents = false;

        _viewModel.CategoryFilter.Clear();
        SyncFiltersFromControlsAndApply();
    }

    // Empties every filter input to its "no filter" state. Callers wrap this in
    // _suppressFilterControlEvents so the resets don't each trigger a rebuild.
    private void ResetFilterControls()
    {
        MinSizeValueBox.Text = "";
        MaxSizeValueBox.Text = "";
        MinSizeUnitCombo.SelectedIndex = 0; // MB
        MaxSizeUnitCombo.SelectedIndex = 0; // MB
        CopiesCombo.SelectedIndex = -1;     // "any"
        ExtensionCombo.SelectedIndex = -1;  // "any"
        ModifiedAfterPicker.Date = null;
        ModifiedBeforePicker.Date = null;
        ClearModifiedAfterButton.Visibility = Visibility.Collapsed;
        ClearModifiedBeforeButton.Visibility = Visibility.Collapsed;
        PathContainsBox.Text = "";
    }

    // Fills the Extension dropdown with exactly the extensions present in the
    // loaded results (view-model AvailableExtensions), so it never offers a type
    // that isn't there. Runs under _suppressFilterControlEvents (set by callers).
    private void PopulateExtensionCombo()
    {
        ExtensionCombo.Items.Clear();
        foreach (var ext in _viewModel.AvailableExtensions)
        {
            ExtensionCombo.Items.Add(new ComboBoxItem { Content = ext });
        }
        ExtensionCombo.SelectedIndex = -1; // "any"
    }

    // Clear just one end of the MODIFIED range (QA-19) - previously only
    // "Clear all" could undo a date. Setting Date raises DateChanged, which
    // runs FilterChanged like any other filter edit.
    private void ClearModifiedAfter_Click(object sender, RoutedEventArgs e) => ModifiedAfterPicker.Date = null;

    private void ClearModifiedBefore_Click(object sender, RoutedEventArgs e) => ModifiedBeforePicker.Date = null;

    private void SyncFiltersFromControlsAndApply()
    {
        _viewModel.MinSizeBytes = SizeFieldToBytes(MinSizeValueBox.Text, MinSizeUnitCombo);
        _viewModel.MaxSizeBytes = SizeFieldToBytes(MaxSizeValueBox.Text, MaxSizeUnitCombo);
        _viewModel.MinGroupSize = SelectedCopies(CopiesCombo);
        _viewModel.ModifiedAfter = ModifiedAfterPicker.Date?.Date;
        _viewModel.ModifiedBefore = ModifiedBeforePicker.Date?.Date;
        // Each date's ✕ shows only while that date is set (QA-19).
        ClearModifiedAfterButton.Visibility = ModifiedAfterPicker.Date is null ? Visibility.Collapsed : Visibility.Visible;
        ClearModifiedBeforeButton.Visibility = ModifiedBeforePicker.Date is null ? Visibility.Collapsed : Visibility.Visible;
        _viewModel.PathContains = string.IsNullOrWhiteSpace(PathContainsBox.Text) ? null : PathContainsBox.Text;
        _viewModel.ExtensionFilter = (ExtensionCombo.SelectedItem as ComboBoxItem)?.Content as string;

        _viewModel.ApplyFilters();
        RefreshSummary();
        RefreshSelectedSizeStat();
    }

    // Converts a size field (numeric text + MB/GB unit dropdown) to bytes. Blank
    // or non-positive input means "no bound".
    private static long? SizeFieldToBytes(string text, ComboBox unitCombo)
    {
        if (!double.TryParse(text, out var value) || value <= 0)
        {
            return null;
        }

        var unitBytes = unitCombo.SelectedIndex == 1 ? 1024L * 1024 * 1024 : 1024L * 1024; // GB : MB
        return (long)(value * unitBytes);
    }

    // Reads the min-copies dropdown ("2".."9", "10+"); null when nothing is picked.
    private static int? SelectedCopies(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem { Content: string content }
            && int.TryParse(content.TrimEnd('+'), out var copies))
        {
            return copies;
        }

        return null;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectAll();
        RefreshSelectedSizeStat();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearSelection();
        RefreshSelectedSizeStat();
    }

    private void InvertSelection_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.InvertSelection();
        RefreshSelectedSizeStat();
    }

    private void SelectOver100Mb_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectBySizeThreshold(ResultsViewModel.LargeFileThresholdBytes);
        RefreshSelectedSizeStat();
    }

    // "By folder…" (new-screens 4i) - reuses the same unpackaged-app
    // FolderPicker pattern HomePage.BrowseButton_Click and SettingsDialog.
    // PickFolderAsync already establish (WindowNative.GetWindowHandle +
    // Microsoft.Windows.Storage.Pickers, not the classic Windows.Storage
    // picker this app hit a COMException on elsewhere).
    private async void SelectByFolder_Click(object sender, RoutedEventArgs e)
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        _viewModel.SelectByFolder(folder.Path);
        RefreshSelectedSizeStat();
    }

    // "Keep best (similar photos)" (F3) - in each look-alike group, selects
    // every copy except the best (highest resolution, then largest) for
    // removal. A manual opt-in, since similar photos are never auto-selected.
    private void KeepBestSimilar_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectAllButBestInSimilarityGroups();
        RefreshSelectedSizeStat();
    }

    // Export CSV (new-screens 4i - shown as "PHASE 2" in the mockup, built
    // for real per this batch's "everything gets built, no placeholders"
    // decision). Writes exactly the currently-VISIBLE (filtered) files, not
    // the full unfiltered result set - matching what the user can actually
    // see on screen right now.
    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(windowId)
        {
            SuggestedFileName = $"quickening-duplicates-{DateTime.Now:yyyy-MM-dd}",
        };
        picker.FileTypeChoices.Add("CSV file", new List<string> { ".csv" });

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            var csv = BuildDuplicatesCsv(_viewModel.Groups);
            // UTF-8 WITH a byte-order mark: Excel opens a BOM-less .csv in the
            // ANSI code page, which turned "KEEP — newest" into "KEEP â€” newest"
            // (and would mangle any non-ASCII file path) - QA-18.
            await File.WriteAllTextAsync(result.Path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // async void handler: uncaught, this would be silently swallowed
            // by App's UnhandledException handler and the export would just
            // quietly produce nothing.
            App.Logger?.LogError($"CSV export failed: {ex}");
            await ShowExportFailedDialogAsync(XamlRoot, result.Path);
        }
    }

    // Shared by both results pages' CSV export failure paths.
    internal static async Task ShowExportFailedDialogAsync(XamlRoot xamlRoot, string path)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Couldn't save the CSV",
                Content = $"The file couldn't be written to \"{path}\". It may be open in another app, or the location may be read-only or unavailable.",
                CloseButtonText = "OK",
                XamlRoot = xamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception dialogEx)
        {
            App.Logger?.LogError($"Failed to show the export-failed dialog: {dialogEx}");
        }
    }

    private static string BuildDuplicatesCsv(IEnumerable<DuplicateGroupViewModel> groups)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Group,Path,Category,SizeBytes,LastModifiedUtc,Recommendation");

        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                builder.Append(CsvField(group.GroupLabel)).Append(',');
                builder.Append(CsvField(file.Path)).Append(',');
                builder.Append(CsvField(file.Category.ToString())).Append(',');
                builder.Append(file.SizeBytes).Append(',');
                builder.Append(CsvField(file.LastWriteTimeUtc.ToString("O"))).Append(',');
                builder.AppendLine(CsvField(file.IsKeepRecommended ? file.KeepRecommendedLabel : ""));
            }
        }

        return builder.ToString();
    }

    // Minimal RFC 4180 quoting - wraps a field in quotes (doubling any
    // embedded quotes) only when it actually contains a comma, quote, or
    // newline, matching the standard "only quote when necessary" convention
    // most spreadsheet tools produce/expect.
    private static string CsvField(string value)
    {
        // Formula-injection hardening: a cell starting with = + - @ (or a
        // stray tab/CR) executes as a formula when the export is opened in
        // Excel/Sheets. File names are attacker-influenced, so neutralize by
        // prefixing an apostrophe and force-quoting the field.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // Bound to each file row's CheckBox Checked/Unchecked in
    // ResultsPage.xaml, so toggling an individual file's selection updates
    // the bottom-bar "X selected" stat immediately, same as the mass-select
    // buttons above.
    private void FileSelectionChanged(object sender, RoutedEventArgs e) => RefreshSelectedSizeStat();

    // Bound to each file row's thumbnail/icon Border's Tapped event in
    // Kicks off the lazy video poster-frame load when a row realizes (video rows
    // only - LoadVideoThumbnailAsync no-ops otherwise). Fire-and-forget: it
    // handles its own errors and swaps the icon for the poster via PropertyChanged.
    private void FileThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        // Videos lazily load a poster frame here; the shell type icon loads itself
        // on first bind (see SelectableFile.FileTypeIconSource) so it survives
        // container recycling in the virtualized list.
        if (sender is FrameworkElement { DataContext: SelectableFile file })
        {
            _ = file.LoadVideoThumbnailAsync();
        }
    }

    // ResultsPage.xaml. Opens the side-by-side comparison viewer over every
    // file in the tapped file's duplicate group - for any file the compare
    // viewer can preview (media, code/markdown/text, PDF, archive contents).
    // Tapping a row whose type has no viewer (proprietary/binary, e.g. .psd,
    // .docx) instead offers to open it in its default app, behind a confirm so
    // an accidental tap doesn't launch heavy software.
    private async void FileThumbnail_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SelectableFile file })
        {
            return;
        }

        if (Controls.FileViewerRouter.ForPath(file.Path, file.Category) == Controls.FileViewerKind.None)
        {
            await ConfirmAndOpenExternallyAsync(XamlRoot, file);
            return;
        }

        var group = _viewModel.Groups.FirstOrDefault(g => g.Files.Contains(file));
        if (group is not null)
        {
            ComparisonViewer.ShowGroup(group.Files.ToList());
            return;
        }

        // Similar-photo rows live in SimilarityGroups, not Groups - tapping one
        // opens the whole look-alike set side by side, same as a duplicate group.
        var similarGroup = _viewModel.SimilarityGroups.FirstOrDefault(g => g.Files.Contains(file));
        if (similarGroup is not null)
        {
            ComparisonViewer.ShowGroup(similarGroup.Files.ToList(), isExactDuplicateGroup: false);
            return;
        }

        // Any other row with no group - a Blurry-photos row (F9), or a future
        // flat list - opens just that single file in the viewer, rather than
        // silently doing nothing.
        ComparisonViewer.ShowGroup(new[] { file });
    }

    // Confirm-then-open for file types with no in-app preview. Names the
    // registered app when Windows exposes a friendly name ("Open in Adobe
    // Photoshop?") and degrades to generic wording otherwise. Static + XamlRoot-
    // parameterized so LargeFilesResultsPage reuses the exact same prompt.
    internal static async Task ConfirmAndOpenExternallyAsync(XamlRoot xamlRoot, SelectableFile file)
    {
        var appName = Quickening.Core.Shell.DefaultAppResolver.FriendlyAppName(file.Path);
        var fileName = System.IO.Path.GetFileName(file.Path);

        var dialog = new ContentDialog
        {
            Style = (Style)Application.Current.Resources["NebulaContentDialogStyle"],
            Title = "No preview for this file type",
            Content = appName is null
                ? $"Open \"{fileName}\" in its default app?"
                : $"Open \"{fileName}\" in {appName}?",
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["PillCtaButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["PillSecondaryButtonStyle"],
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Opening file '{file.Path}' in default app failed: {ex}");
        }
    }

    private void FileThumbnail_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SelectableFile file })
        {
            file.ReportThumbnailFailed();
        }
    }

    private void RefreshSelectedSizeStat()
    {
        var selectedCount = _viewModel.GetSelectedFilePaths().Count;
        var riskyCount = _viewModel.GetSelectedRiskyFilePaths().Count;

        SelectedSizeText.Text = FileSizeFormatter.Format(_viewModel.GetSelectedSizeBytes());

        // "will come home · **N files** selected [· **N program files**]" -
        // the count phrases are bold per the design (screen 2g/2h), not
        // plain text throughout.
        var resources = Application.Current.Resources;
        SelectedCalloutText.Inlines.Clear();
        SelectedCalloutText.Inlines.Add(new Run { Text = "will come home · " });
        SelectedCalloutText.Inlines.Add(new Run
        {
            Text = $"{selectedCount} file{(selectedCount == 1 ? "" : "s")}",
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyEmphasisBrush"],
        });
        SelectedCalloutText.Inlines.Add(new Run { Text = " selected" });

        if (riskyCount > 0)
        {
            SelectedCalloutText.Inlines.Add(new Run { Text = " · " });
            SelectedCalloutText.Inlines.Add(new Run
            {
                Text = $"{riskyCount} program file{(riskyCount == 1 ? "" : "s")}",
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)resources["WarningBrush"],
            });
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e) => await RemoveSelectedFilesAsync();

    // The single removal flow: confirm -> Progress (removal) -> either
    // Celebration (nothing failed) or back to Results with the
    // partial-failure dialog on top. Extracted out of RemoveButton_Click's
    // event-handler body (rather than left inline) so the partial-failure
    // dialog's "Try those N again" button (BuildPartialFailureDialog below)
    // can invoke this exact same flow a second time - since DeleteSelectedAsync
    // never deselects a file that failed, simply re-running this method
    // naturally retries only the still-selected failed files (plus anything
    // else the user re-selects in between), with no separate "retry" code
    // path to keep in sync.
    private async Task RemoveSelectedFilesAsync()
    {
        // Delete-time hard-block re-validation must honor the same
        // protected-paths choice the scan ran under, or results from a
        // protected-paths scan become undeletable.
        _recycleBinService.AllowProtectedPaths = App.Settings.AllowProtectedPaths;

        var selectedCount = _viewModel.GetSelectedFilePaths().Count;
        if (selectedCount == 0)
        {
            return;
        }

        var riskyPaths = _viewModel.GetSelectedRiskyFilePaths();
        var riskySet = new HashSet<string>(riskyPaths);
        var riskyFiles = _viewModel.Groups.SelectMany(g => g.Files)
            .Concat(_viewModel.SimilarityGroups.SelectMany(g => g.Files))
            .Where(f => riskySet.Contains(f.Path))
            .Select(f => (f.Path, f.SizeBytes))
            .ToList();

        var createdTogetherSet = new HashSet<string>(_viewModel.GetSelectedCreatedTogetherPaths());
        var createdTogetherFiles = _viewModel.Groups.SelectMany(g => g.Files)
            .Where(f => createdTogetherSet.Contains(f.Path))
            .Select(f => (f.Path, f.SizeBytes))
            .ToList();

        var totalSizeBytes = _viewModel.GetSelectedSizeBytes();

        // Captured before DeleteSelectedAsync runs (it removes files from
        // both collections as it processes them) - the difference against
        // outcome.Failed below is exactly the set of paths that actually
        // made it to the Recycle Bin this run, which is what the Undo
        // toast needs.
        var selectedPaths = _viewModel.GetSelectedFilePaths();

        var confirmDialog = new ContentDialog
        {
            Style = (Style)Application.Current.Resources["NebulaContentDialogStyle"],
            Width = (double)Application.Current.Resources["DialogWidthConfirm"],
            PrimaryButtonText = $"Remove {selectedCount} file{(selectedCount == 1 ? "" : "s")}",
            CloseButtonText = "Keep everything",
            PrimaryButtonStyle = (Style)Application.Current.Resources["PillCtaButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["PillSecondaryButtonStyle"],
            XamlRoot = XamlRoot,
        };
        confirmDialog.Content = BuildRemoveConfirmationContent(selectedCount, totalSizeBytes, riskyFiles, confirmDialog, createdTogetherFiles);

        var choice = await confirmDialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            return;
        }

        // The actual delete work (and its per-file error handling - see
        // ResultsViewModel.DeleteSelectedAsync/IRecycleBinService's doc
        // comment) now runs inside ProgressPage's Operation, which already
        // wraps the call in its own try/catch (error dialog / navigate home
        // on failure or cancellation) - nothing left to catch here.
        var parameters = new ProgressPageParameters(
            Headline: RandomPhrases.RemovalHeadline(),
            RingHue: ProgressRingHue.Success,
            Operation: async (progress, cancellationToken) =>
            {
                var sizeBefore = totalSizeBytes;
                var failed = await _viewModel.DeleteSelectedAsync(
                    new Progress<DeleteProgress>(p =>
                        progress.Report(new ProgressUpdate(p.Processed, p.Total, p.Path))),
                    cancellationToken,
                    targetLabel: _targetLabel ?? "your files");
                return (object?)new RemovalOutcome(failed, sizeBefore, selectedCount);
            },
            OnCancelled: () =>
            {
                // Stopping mid-removal doesn't undo files already sent to
                // the Recycle Bin (see DeleteSelectedAsync's cancellation
                // handling) - Results' groups are already pruned/relabeled
                // for whatever completed before Stop was clicked, so return
                // there (refreshed) instead of ProgressPage's default
                // navigate-home, which would strand the user with no easy
                // path back to the results they were just working through.
                BuildSectionFilters();
                RefreshSummary();
                RefreshSelectedSizeStat();
                ((MainWindow)App.MainWindowInstance!).ShowResults(_loadedResult!);
            },
            Completed: async outcomeObj =>
            {
                var outcome = (RemovalOutcome)outcomeObj!;

                // Refresh both stat labels regardless of outcome - "Review
                // what's left" from Celebration returns to this SAME cached
                // page instance (NavigationCacheMode="Enabled") with the
                // SAME _loadedResult reference, so OnNavigatedTo's
                // ReferenceEquals reload guard skips its own refresh call -
                // this is the only place left that would otherwise update
                // these labels, so skipping it here left them showing
                // stale pre-removal numbers after every fully-successful
                // removal (SummaryText/SelectedSizeText are plain x:Name
                // TextBlocks, not bound to anything live).
                BuildSectionFilters();
                RefreshSummary();
                RefreshSelectedSizeStat();

                if (outcome.Failed.Count == 0)
                {
                    // Nothing failed in this branch, so every selected path
                    // really did make it to the Recycle Bin.
                    ((MainWindow)App.MainWindowInstance!).ShowCelebration(
                        new CelebrationParameters(
                            outcome.SizeBytesAttempted, outcome.FileCountAttempted, _loadedResult,
                            RemovedFilePaths: selectedPaths, RecycleBinService: _recycleBinService));
                    return;
                }

                ((MainWindow)App.MainWindowInstance!).ShowResults(_loadedResult!);

                await ShowPartialFailureDialogAsync(
                    outcome.Failed, outcome.SizeBytesAttempted, outcome.FileCountAttempted,
                    _viewModel.Groups.SelectMany(g => g.Files), XamlRoot, RemoveSelectedFilesAsync);
            });

        ((MainWindow)App.MainWindowInstance!).ShowProgress(parameters);
    }

    private sealed record RemovalOutcome(IReadOnlyList<string> Failed, long SizeBytesAttempted, int FileCountAttempted);

    // Builds the confirm-delete dialog's body (screen 2i). Normal deletes (no
    // risky files selected) are unaffected - the primary button stays enabled
    // from the moment the dialog opens, same as before this method existed.
    // When ResultsViewModel.GetSelectedRiskyFilePaths() reports any risky
    // files (see RiskyExtensions/the design doc's "Risky-extension warning"
    // step), they're itemized separately (with size, matching 2i) and the
    // primary button starts disabled until the user explicitly checks the
    // acknowledgment CheckBox - IsPrimaryButtonEnabled bound to a checkbox's
    // checked state is the standard WinUI pattern for gating a ContentDialog's
    // primary action on an extra confirmation. This is a straight visual
    // restyle of the pre-existing method (Task 9, plan Step 6) - the gating
    // logic itself is unchanged from before this restyle. internal (not
    // private) so LargeFilesResultsPage.xaml.cs's own removal flow can
    // reuse this exact dialog body rather than duplicating it - nothing
    // here is specific to the grouped ResultsViewModel.
    internal static UIElement BuildRemoveConfirmationContent(
        int selectedCount, long totalSizeBytes, IReadOnlyList<(string Path, long SizeBytes)> riskyFiles,
        ContentDialog dialog, IReadOnlyList<(string Path, long SizeBytes)>? createdTogetherFiles = null)
    {
        var resources = Application.Current.Resources;
        var panel = new StackPanel { Spacing = 0 };

        panel.Children.Add(new TextBlock
        {
            Text = $"Send {selectedCount} file{(selectedCount == 1 ? "" : "s")} to the Recycle Bin?",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = (double)resources["FontSizeDialogTitle"],
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var bodyText = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 14.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)resources["TextMutedBrush"],
        };
        bodyText.Inlines.Add(new Run { Text = "That's " });
        bodyText.Inlines.Add(new Run
        {
            Text = FileSizeFormatter.Format(totalSizeBytes),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["SuccessBrush"],
        });
        bodyText.Inlines.Add(new Run
        {
            Text = " coming home. Nothing is deleted for good — you can restore any of it from the Recycle Bin later.",
        });
        panel.Children.Add(bodyText);

        var createdTogether = createdTogetherFiles ?? System.Array.Empty<(string Path, long SizeBytes)>();

        if (riskyFiles.Count == 0 && createdTogether.Count == 0)
        {
            dialog.IsPrimaryButtonEnabled = true;
            return panel;
        }

        var riskyBorder = new Border
        {
            Margin = new Thickness(0, 22, 0, 0),
            CornerRadius = new CornerRadius(16),
            Background = (Brush)resources["WarningFillBrush"],
            BorderBrush = (Brush)resources["WarningFillBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18),
        };
        var riskyStack = new StackPanel { Spacing = 12 };

        if (riskyFiles.Count > 0)
        {
        riskyStack.Children.Add(new TextBlock
        {
            Text = $"⚠ {riskyFiles.Count} of these look like program files",
            FontSize = 14,
            FontWeight = FontWeights.ExtraBold,
            Foreground = (Brush)resources["WarningBrush"],
        });

        // The risky-file list is unbounded (a mass-select rule like "Select
        // Over 100MB" could match dozens of files) - ContentDialog does not
        // auto-scroll its content, so without a capped ScrollViewer here a
        // large selection could push the acknowledgment CheckBox below the
        // dialog's visible area, making the gated primary button effectively
        // unreachable. Only this list scrolls; the title/body/checkbox stay
        // pinned in view regardless of how many files match.
        var riskyListPanel = new StackPanel { Spacing = 8 };
        var riskyTextBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xC9, 0xB8)); // one-off warm warning text tint, matches 2i exactly (#E8C9B8), not one of the shared category/semantic tokens
        foreach (var (path, sizeBytes) in riskyFiles)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path),
                FontFamily = (FontFamily)resources["MonoFontFamily"],
                FontSize = 12.5,
                Foreground = riskyTextBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var sizeText = new TextBlock
            {
                Text = FileSizeFormatter.Format(sizeBytes),
                FontFamily = (FontFamily)resources["MonoFontFamily"],
                FontSize = 12.5,
                Foreground = riskyTextBrush,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(nameText, 0);
            Grid.SetColumn(sizeText, 1);
            row.Children.Add(nameText);
            row.Children.Add(sizeText);
            riskyListPanel.Children.Add(row);
        }

        riskyStack.Children.Add(new ScrollViewer
        {
            Content = riskyListPanel,
            MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        }

        if (createdTogether.Count > 0)
        {
            var ctTextBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0xC9, 0xB8));
            riskyStack.Children.Add(new TextBlock
            {
                Text = $"⚠ {createdTogether.Count} were created together — may belong to an app or set",
                FontSize = 14,
                FontWeight = FontWeights.ExtraBold,
                Foreground = (Brush)resources["WarningBrush"],
            });
            riskyStack.Children.Add(new TextBlock
            {
                Text = "Files written at the same moment are often part of one program's install — removing part of a set can break it.",
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ctTextBrush,
            });

            var ctListPanel = new StackPanel { Spacing = 8 };
            foreach (var (path, sizeBytes) in createdTogether)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var nameText = new TextBlock
                {
                    Text = System.IO.Path.GetFileName(path),
                    FontFamily = (FontFamily)resources["MonoFontFamily"],
                    FontSize = 12.5,
                    Foreground = ctTextBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var sizeText = new TextBlock
                {
                    Text = FileSizeFormatter.Format(sizeBytes),
                    FontFamily = (FontFamily)resources["MonoFontFamily"],
                    FontSize = 12.5,
                    Foreground = ctTextBrush,
                    Margin = new Thickness(12, 0, 0, 0),
                };
                Grid.SetColumn(nameText, 0);
                Grid.SetColumn(sizeText, 1);
                row.Children.Add(nameText);
                row.Children.Add(sizeText);
                ctListPanel.Children.Add(row);
            }

            riskyStack.Children.Add(new ScrollViewer
            {
                Content = ctListPanel,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
        }

        var enableHint = new TextBlock
        {
            Text = "Check the box above to enable removal.",
            FontSize = 11.5,
            Foreground = (Brush)resources["TextDisabledHintBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var ackText = (riskyFiles.Count > 0, createdTogether.Count > 0) switch
        {
            (true, true) => "I understand some of these are program files or created-together sets, and I still want to remove them.",
            (false, true) => "I understand these were created together and may belong to an app, and I still want to remove them.",
            _ => "I understand these may be programs or installers, and I still want to remove them.",
        };

        var acknowledgeCheckBox = new CheckBox
        {
            Margin = new Thickness(0, 4, 0, 0),
            Content = new TextBlock
            {
                Text = ackText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD8, 0xC0, 0xB2)),
            },
        };
        acknowledgeCheckBox.Checked += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = true;
            enableHint.Visibility = Visibility.Collapsed;
        };
        acknowledgeCheckBox.Unchecked += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = false;
            enableHint.Visibility = Visibility.Visible;
        };
        riskyStack.Children.Add(acknowledgeCheckBox);

        riskyBorder.Child = riskyStack;
        panel.Children.Add(riskyBorder);
        panel.Children.Add(enableHint);

        dialog.IsPrimaryButtonEnabled = false;
        return panel;
    }

    // Builds the partial-failure dialog (screen 2m), shown over Results (or
    // LargeFilesResultsPage - see that page's own RemoveSelectedFilesAsync,
    // which calls this same method) after a removal that didn't fully
    // succeed. The specific "in use by X" lock-reason text 2m shows is
    // aspirational for this dialog - neither ResultsViewModel.
    // DeleteSelectedAsync nor LargeFilesResultsViewModel's counterpart
    // captures WHICH process holds a file open (they only distinguish
    // IOException/UnauthorizedAccessException from success), so fabricating
    // a specific program name per file would be dishonest. Every failed row
    // instead gets the same honest, generic reason.
    //
    // internal static (not a private instance method) and parameterized
    // over currentFiles/xamlRoot/retryAction rather than reading _viewModel/
    // XamlRoot/RemoveSelectedFilesAsync directly, so LargeFilesResultsPage
    // (whose current-files collection is a flat ObservableCollection, not
    // ResultsViewModel.Groups) can reuse this exact dialog rather than
    // duplicating it.
    internal static async Task ShowPartialFailureDialogAsync(
        IReadOnlyList<string> failedPaths,
        long sizeBytesAttempted,
        int fileCountAttempted,
        IEnumerable<SelectableFile> currentFiles,
        XamlRoot xamlRoot,
        Func<Task> retryAction)
    {
        var resources = Application.Current.Resources;

        // Failed files are never deselected by DeleteSelectedAsync, so they
        // are still present (and still selected) in currentFiles right now -
        // summing their current SizeBytes gives the exact bytes that did NOT
        // come home this pass, without DeleteSelectedAsync needing to grow a
        // new "succeeded bytes" return value just for this dialog's copy.
        var failedPathSet = new HashSet<string>(failedPaths);
        var failedSizeBytes = currentFiles
            .Where(f => failedPathSet.Contains(f.Path))
            .Sum(f => f.SizeBytes);
        var succeededSizeBytes = Math.Max(0, sizeBytesAttempted - failedSizeBytes);
        var succeededCount = Math.Max(0, fileCountAttempted - failedPaths.Count);

        var panel = new StackPanel { Spacing = 0 };

        panel.Children.Add(new TextBlock
        {
            Text = succeededCount > 0
                ? $"Almost all of it — {succeededCount} of {fileCountAttempted} moved."
                : "None of it moved this time.",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = (double)resources["FontSizeDialogTitle"],
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var bodyText = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 14.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)resources["TextMutedBrush"],
        };
        if (succeededCount > 0)
        {
            bodyText.Inlines.Add(new Run
            {
                Text = FileSizeFormatter.Format(succeededSizeBytes),
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)resources["SuccessBrush"],
            });
            bodyText.Inlines.Add(new Run { Text = " is back. " });
        }
        bodyText.Inlines.Add(new Run
        {
            Text = failedPaths.Count == 1
                ? "1 file wouldn't budge — it couldn't be moved right now."
                : $"{failedPaths.Count} files wouldn't budge — they couldn't be moved right now.",
        });
        panel.Children.Add(bodyText);

        var listBorder = new Border
        {
            Margin = new Thickness(0, 20, 0, 0),
            CornerRadius = new CornerRadius(16),
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
        };
        var listPanel = new StackPanel { Spacing = 12 };
        foreach (var path in failedPaths)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse { Width = 8, Height = 8, Fill = (Brush)resources["WarningBrush"], VerticalAlignment = VerticalAlignment.Center };
            var pathText = new TextBlock
            {
                Text = TruncatePathForDialog(path),
                FontFamily = (FontFamily)resources["MonoFontFamily"],
                FontSize = 12.5,
                Foreground = (Brush)resources["TextSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var reasonText = new TextBlock
            {
                Text = "couldn't be moved right now",
                FontSize = 12,
                Foreground = (Brush)resources["TextFaintBrush"],
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(pathText, 1);
            Grid.SetColumn(reasonText, 2);
            row.Children.Add(dot);
            row.Children.Add(pathText);
            row.Children.Add(reasonText);
            listPanel.Children.Add(row);
        }

        listBorder.Child = listPanel;
        panel.Children.Add(listBorder);

        var failureDialog = new ContentDialog
        {
            Style = (Style)resources["NebulaContentDialogStyle"],
            Width = (double)resources["DialogWidthFailure"],
            Content = panel,
            CloseButtonText = "Skip them",
            PrimaryButtonText = $"Try those {failedPaths.Count} again",
            CloseButtonStyle = (Style)resources["PillSecondaryButtonStyle"],
            PrimaryButtonStyle = (Style)resources["PillSmallGradientButtonStyle"],
            XamlRoot = xamlRoot,
        };

        // "Try those N again" re-invokes the exact same removal flow -
        // DeleteSelectedAsync never deselected the files that failed, so
        // this naturally retries only them (plus anything else still
        // selected). Crucially the retry runs AFTER ShowAsync completes:
        // running it from PrimaryButtonClick re-entered the removal flow
        // (which opens its own confirm ContentDialog) while this dialog was
        // still open - WinUI allows one open ContentDialog per XamlRoot, so
        // the retry path crashed the app every time it was used. "Skip
        // them" needs no handler at all: the default CloseButton behavior
        // (just close) is already correct - the failed files simply remain
        // in the list, still selected, same as DeleteSelectedAsync already
        // leaves them.
        var result = await failureDialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await retryAction();
        }
    }

    private static string TruncatePathForDialog(string path)
    {
        var parts = path.Split('\\');
        return parts.Length <= 3 ? path : "…\\" + string.Join("\\", parts.Skip(parts.Length - 3));
    }

    // WinUI does not propagate a ContextFlyout's target element's DataContext
    // into the flyout's content (the flyout is realized in a separate Popup
    // root, disconnected from the normal DataContext inheritance chain - a
    // long-documented WinUI/UWP limitation: microsoft/microsoft-ui-xaml#911).
    // Without this handler, OpenFileLocation_Click's DataContext pattern
    // match below would always fail (DataContext null) and silently no-op.
    // FlyoutBase.Target is set by the framework to whatever element the
    // flyout was opened from, so copy its DataContext onto each menu item
    // here before the flyout is shown.
    private void FileRowContextMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout { Target: FrameworkElement target } flyout)
        {
            return;
        }

        var ignored = target.DataContext is SelectableFile { IsIgnored: true };
        foreach (var item in flyout.Items)
        {
            if (item is not MenuFlyoutItem menuItem)
            {
                continue;
            }

            menuItem.DataContext = target.DataContext;
            // Show "Ignore…" for a normal file, "Remove from ignore list" for one
            // that's already ignored - never both.
            menuItem.Visibility = (menuItem.Tag as string) switch
            {
                "unignore" => ignored ? Visibility.Visible : Visibility.Collapsed,
                "ignore-file" or "ignore-folder" => ignored ? Visibility.Collapsed : Visibility.Visible,
                _ => Visibility.Visible,
            };
        }
    }

    private void IgnoreFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SelectableFile file })
        {
            ApplyIgnoreChange(() => IgnoreService.IgnoreFiles(new[] { file.Path }));
        }
    }

    private void IgnoreFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SelectableFile file }
            && System.IO.Path.GetDirectoryName(file.Path) is { } folder)
        {
            ApplyIgnoreChange(() => IgnoreService.IgnoreFolder(folder));
        }
    }

    private void UnignoreFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SelectableFile file })
        {
            ApplyIgnoreChange(() => IgnoreService.UnignoreFile(file.Path));
        }
    }

    // "Ignore selected" - the checked files across every section.
    private void IgnoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var paths = _viewModel.SelectedFilePaths();
        if (paths.Count > 0)
        {
            ApplyIgnoreChange(() => IgnoreService.IgnoreFiles(paths));
        }
    }

    // After any ignore-list change, drop the selection of anything that just
    // became ignored (a checked file must never carry its checkmark into
    // Remove Selected after the user said "keep this"), re-derive the visible
    // results, rebuild the SHOW checklist (the "Ignored" row may
    // appear/disappear), refresh the flat row list, and refresh the stats so
    // the change appears immediately.
    private void ApplyIgnoreChange(Action mutate)
    {
        mutate();
        _viewModel.ClearIgnoredSelections();
        _viewModel.ApplyFilters();
        BuildSectionFilters();
        RefreshCategoryCounts();
        RefreshSummary();
        RefreshSelectedSizeStat();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { DataContext: SelectableFile file })
        {
            return;
        }

        try
        {
            // UseShellExecute opens the file in whatever app is registered for
            // its type - the point of "Open file".
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Opening file '{file.Path}' failed: {ex}");
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { DataContext: SelectableFile file })
        {
            return;
        }

        Quickening.Core.Shell.ExplorerLauncher.SelectInExplorer(file.Path);
    }

    /// <summary>
    /// Category filter chip data (screen 2g's chip row) - Category is null
    /// only for the leading "All" chip, which has no swatch dot. A plain
    /// INotifyPropertyChanged class rather than a record: IsSelected needs
    /// to raise PropertyChanged so the chip ItemTemplate's bound
    /// Background/BorderBrush/Foreground (see ChipBackgroundConverter et
    /// al. in ResultsPage.xaml) refresh when CategorySidebar_SelectionChanged
    /// mutates it - the same reason KnownFolderTile.IsSelected in
    /// HomePage.xaml.cs (Task 4) isn't a record either.
    /// </summary>
    // Backs one row of the sidebar SHOW checklist - same shape as
    // CategorySidebarItem (so it drops into the identical ItemTemplate), but
    // keyed by section instead of MIME category and always carrying a swatch.
    private sealed class SectionFilterItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Key { get; }
        public string Label { get; }
        public SolidColorBrush SwatchBrush { get; }

        // Screen readers announce a ListView item by its data item's ToString();
        // without this they read the class name (QA-6).
        public override string ToString() => Label;

        public SectionFilterItem(string key, string label, SolidColorBrush swatch)
        {
            Key = key;
            Label = label;
            SwatchBrush = swatch;
        }

        public int Count { get; init; }
        public string CountText => Count.ToString();
        public bool HasSwatch => true;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    private sealed class CategorySidebarItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public MimeCategory? Category { get; }
        public string Label { get; }

        public CategorySidebarItem(MimeCategory? Category, string Label)
        {
            this.Category = Category;
            this.Label = Label;
        }

        // File count for this category in the loaded results, ignored files
        // excluded (total for "All"), shown at the right of each checklist row.
        // Settable + notifying: an ignore change recounts in place (QA-5).
        private int _count;

        public int Count
        {
            get => _count;
            set
            {
                _count = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(CountText)));
            }
        }

        public string CountText => Count.ToString();

        // Screen readers announce a ListView item by its data item's ToString();
        // without this they read the class name (QA-6).
        public override string ToString() => $"{Label}, {Count} file{(Count == 1 ? "" : "s")}";

        public bool HasSwatch => Category is not null;

        public SolidColorBrush? SwatchBrush => Category is { } category
            ? CategoryColors.BrushFor(category)
            : null;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
