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
        GroupsListView.ItemsSource = _flatRows;
        GroupsListView.ItemTemplateSelector = new RowTemplateSelector
        {
            HeaderTemplate = (DataTemplate)Resources["ResultsHeaderRowTemplate"],
            FileTemplate = (DataTemplate)Resources["ResultsFileRowTemplate"],
        };
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

    // A group's header row, flattened into _flatRows alongside its own
    // SelectableFile rows - see RebuildFlatRows's own doc comment for why
    // this flattening exists at all.
    private sealed class GroupHeaderRow
    {
        public required DuplicateGroupViewModel Group { get; init; }
    }

    // Picks GroupsListView's per-item template by row kind - GroupHeaderRow
    // renders the group's swatch/name/copies/frees header, anything else
    // (a SelectableFile) renders a file row. Built and assigned in code
    // rather than declared as a XAML resource since GroupHeaderRow is a
    // private nested type XAML can't reference by name.
    private sealed class RowTemplateSelector : DataTemplateSelector
    {
        public required DataTemplate HeaderTemplate { get; init; }
        public required DataTemplate FileTemplate { get; init; }

        protected override DataTemplate SelectTemplateCore(object item) =>
            item is GroupHeaderRow ? HeaderTemplate : FileTemplate;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
            SelectTemplateCore(item);
    }

    // GroupsListView's real ItemsSource - one GroupHeaderRow followed by
    // that group's own SelectableFile rows, for every group, all in one
    // flat list. GroupsListView used to bind directly to _viewModel.Groups
    // and nest a second, non-virtualizing ItemsControl per group for the
    // file rows - that nested ItemsControl was the root cause of a native
    // Microsoft.UI.Xaml crash (confirmed via crash-dump analysis) under
    // fast scrollbar-drag scrolling: the outer ListView's virtualization
    // was rapidly creating/destroying containers that each carried a whole
    // second, unvirtualized collection. Flattening to a single level means
    // GroupsListView's own virtualization is the only one in play.
    private readonly ObservableCollection<object> _flatRows = new();

    // Rebuilds _flatRows from _viewModel.Groups - must run after anything
    // that changes group/file membership (a fresh load, a filter change, a
    // deletion), which in this file is exactly the same set of call sites
    // that already call RefreshSummary() for the same reason, so this is
    // invoked from there rather than needing its own separate call sites.
    private void RebuildFlatRows()
    {
        _flatRows.Clear();
        foreach (var group in _viewModel.Groups)
        {
            _flatRows.Add(new GroupHeaderRow { Group = group });
            foreach (var file in group.Files)
            {
                _flatRows.Add(file);
            }
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
            MinSizeBox.Value = double.NaN;
            MaxSizeBox.Value = double.NaN;
            MinGroupSizeBox.Value = double.NaN;
            ModifiedAfterBox.Text = "";
            ModifiedBeforeBox.Text = "";
            PathContainsBox.Text = "";
            _viewModel.CategoryFilter.Clear();
            _viewModel.MinSizeBytes = null;
            _viewModel.MaxSizeBytes = null;
            _viewModel.MinGroupSize = null;
            _viewModel.ModifiedAfter = null;
            _viewModel.ModifiedBefore = null;
            _viewModel.PathContains = null;

            _viewModel.LoadGroups(scanResult.DuplicateGroups);
            _viewModel.LoadSimilarityGroups(scanResult.SimilarityGroups);
            PopulateCategorySidebar(scanResult);
            _suppressFilterControlEvents = false;

            RefreshSummary();
            RefreshSelectedSizeStat();
            RefreshSimilaritySection();
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
        CategorySidebar.SelectedIndex = 0;
        items[0].IsSelected = true;
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

    private void CategorySidebar_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterControlEvents)
        {
            return;
        }

        if (CategorySidebar.SelectedItem is not CategorySidebarItem item)
        {
            return;
        }

        // Drives the chip's selected visual (see CategoryChipItemStyle's
        // comment in ResultsPage.xaml) - ListView's own native selection
        // brushes aren't available to set via ItemContainerStyle in this
        // WinUI3 version, so every chip's IsSelected is kept in sync with
        // the ListView's real SelectedItem here instead.
        if (CategorySidebar.ItemsSource is IEnumerable<CategorySidebarItem> allItems)
        {
            foreach (var chip in allItems)
            {
                chip.IsSelected = ReferenceEquals(chip, item);
            }
        }

        _viewModel.CategoryFilter.Clear();
        if (item.Category is { } category)
        {
            _viewModel.CategoryFilter.Add(category);
        }

        _viewModel.ApplyFilters();
        RefreshSummary();
        RefreshSelectedSizeStat();
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
        // (fresh load, filter change, deletion) - the exact moments
        // _flatRows also needs rebuilding, so this one call covers both.
        RebuildFlatRows();

        var groupCount = _viewModel.Groups.Count;
        var fileCount = _viewModel.Groups.Sum(g => g.Files.Count);
        var reclaimableBytes = _viewModel.Groups.Sum(g => (long)(g.Files.Count - 1) * g.Files[0].SizeBytes);

        SummaryText.Text = $"{groupCount} group{(groupCount == 1 ? "" : "s")} · "
            + $"{fileCount} file{(fileCount == 1 ? "" : "s")} · "
            + $"{FileSizeFormatter.Format(reclaimableBytes)} reclaimable";
    }

    // "Looks-alike photos" (new-screens 4k) - attached to GroupsListView's
    // own Header rather than a separate sibling control, so it scrolls
    // together with the main Duplicates list using that ListView's own
    // virtualization/scrolling instead of needing a second ScrollViewer.
    // Rebuilt wholesale on every call (group count is expected to be small -
    // a handful of "looks alike" clusters, not thousands of rows) rather
    // than incrementally patched, matching the simplicity of this page's
    // other hand-built sections.
    private void RefreshSimilaritySection()
    {
        GroupsListView.Header = _viewModel.SimilarityGroups.Count == 0
            ? null
            : BuildSimilaritySection();
    }

    private UIElement BuildSimilaritySection()
    {
        var resources = Application.Current.Resources;

        var section = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 20) };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock
        {
            Text = "Looks-alike photos",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
        });
        var phaseBadge = new Border
        {
            Padding = new Thickness(9, 3, 9, 3),
            // CornerRadius=999 bulges into an ellipse on a short element in
            // WinUI (it doesn't clamp to height/2 like CSS) - use the shared
            // pill helper that pins the radius to the badge's live height/2.
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0x8C, 0x6E, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8C, 0x6E, 0xFF)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "SIMILAR, NOT IDENTICAL",
                FontFamily = (FontFamily)resources["BodyFontFamily"],
                FontSize = 10,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xB9, 0xA5, 0xFF)),
            },
        };
        Quickening.App.Controls.PillCornerRadius.SetEnabled(phaseBadge, true);
        titleRow.Children.Add(phaseBadge);
        section.Children.Add(titleRow);

        foreach (var group in _viewModel.SimilarityGroups)
        {
            section.Children.Add(BuildSimilarityGroupCard(group));
        }

        return section;
    }

    private UIElement BuildSimilarityGroupCard(SimilarityGroupViewModel group)
    {
        var resources = Application.Current.Resources;

        var card = new Border
        {
            CornerRadius = (CornerRadius)resources["RadiusCard"],
            Background = new SolidColorBrush(Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };

        var outer = new StackPanel();

        var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(18, 13, 18, 13) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var swatch = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(CategoryColors.For(MimeCategory.Image)), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(swatch, 0);

        var labelText = new TextBlock
        {
            Text = group.GroupLabel,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelText, 1);

        var meter = BuildMatchMeter(group.MatchPercent);
        Grid.SetColumn(meter, 2);

        var compareButton = new Button
        {
            Content = "Compare side by side",
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = (Brush)resources["AccentLightBrush"],
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        compareButton.Click += (_, _) => ComparisonViewer.ShowGroup(group.Files.ToList(), isExactDuplicateGroup: false);
        Grid.SetColumn(compareButton, 4);

        header.Children.Add(swatch);
        header.Children.Add(labelText);
        header.Children.Add(meter);
        header.Children.Add(compareButton);
        outer.Children.Add(header);

        for (var i = 0; i < group.Files.Count; i++)
        {
            outer.Children.Add(BuildSimilarityFileRow(group.Files[i], isLast: i == group.Files.Count - 1));
        }

        card.Child = outer;
        return card;
    }

    // Small gradient meter bar + "94% match" text (4k) - not a real
    // progress control, just a two-layer Border (track + a narrower filled
    // Border on top sized to MatchPercent) since this never animates or
    // updates in place.
    private static FrameworkElement BuildMatchMeter(int matchPercent)
    {
        var resources = Application.Current.Resources;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var track = new Grid { Width = 92, Height = 6 };
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        });
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 92 * Math.Clamp(matchPercent, 0, 100) / 100.0,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0x8C, 0x6E, 0xFF), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0x7A, 0xB6), Offset = 1 },
                },
            },
        });
        row.Children.Add(track);

        row.Children.Add(new TextBlock
        {
            Text = $"{matchPercent}% match",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x7A, 0xB6)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return row;
    }

    private UIElement BuildSimilarityFileRow(SelectableFile file, bool isLast)
    {
        var resources = Application.Current.Resources;

        var row = new Grid { ColumnSpacing = 14, Margin = new Thickness(18, 10, 18, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

        var checkBox = new CheckBox
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            IsChecked = file.IsSelected,
            IsEnabled = !file.IsOnNetworkDrive,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        checkBox.Checked += (_, _) => { file.IsSelected = true; RefreshSelectedSizeStat(); };
        checkBox.Unchecked += (_, _) => { file.IsSelected = false; RefreshSelectedSizeStat(); };
        Grid.SetColumn(checkBox, 0);

        var thumbnailBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = (CornerRadius)resources["RadiusThumbnail"],
            Background = (Brush)resources["CategoryImageFillBrush"],
        };
        if (file.ThumbnailSource is { } thumbnail)
        {
            thumbnailBorder.Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill };
        }
        Grid.SetColumn(thumbnailBorder, 1);

        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        nameStack.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetFileName(file.Path),
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)resources["TextBodyBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetDirectoryName(file.Path) ?? "",
            FontFamily = (FontFamily)resources["MonoFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextDisabledHintBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(nameStack, 2);

        var hintPill = new Border
        {
            Padding = new Thickness(9, 3, 9, 3),
            // CornerRadius=999 bulges to an ellipse on a short element in
            // WinUI; the pill helper pins it to the badge's live height/2.
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x7F, 0xAD, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x7F, 0xAD, 0xFF)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrEmpty(file.HintLabel) ? Visibility.Collapsed : Visibility.Visible,
            Child = new TextBlock
            {
                Text = file.HintLabel ?? "",
                FontFamily = (FontFamily)resources["BodyFontFamily"],
                FontSize = 10,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7F, 0xAD, 0xFF)),
            },
        };
        Quickening.App.Controls.PillCornerRadius.SetEnabled(hintPill, true);
        Grid.SetColumn(hintPill, 3);

        var sizeText = new TextBlock
        {
            Text = FileSizeFormatter.Format(file.SizeBytes),
            FontFamily = (FontFamily)resources["MonoFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sizeText, 4);

        row.Children.Add(checkBox);
        row.Children.Add(thumbnailBorder);
        row.Children.Add(nameStack);
        row.Children.Add(hintPill);
        row.Children.Add(sizeText);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0, isLast ? 0 : 1, 0, 0),
            Child = row,
        };
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
        MinSizeBox.Value = double.NaN;
        MaxSizeBox.Value = double.NaN;
        MinGroupSizeBox.Value = double.NaN;
        ModifiedAfterBox.Text = "";
        ModifiedBeforeBox.Text = "";
        PathContainsBox.Text = "";
        CategorySidebar.SelectedIndex = 0; // "All"
        _suppressFilterControlEvents = false;

        _viewModel.CategoryFilter.Clear();
        SyncFiltersFromControlsAndApply();
    }

    private void SyncFiltersFromControlsAndApply()
    {
        _viewModel.MinSizeBytes = MinSizeBox.Value is double min && !double.IsNaN(min)
            ? (long)(min * 1024 * 1024)
            : null;
        _viewModel.MaxSizeBytes = MaxSizeBox.Value is double max && !double.IsNaN(max)
            ? (long)(max * 1024 * 1024)
            : null;
        _viewModel.MinGroupSize = MinGroupSizeBox.Value is double minGroupSize && !double.IsNaN(minGroupSize)
            ? (int)minGroupSize
            : null;
        _viewModel.ModifiedAfter = DateTime.TryParse(ModifiedAfterBox.Text, out var after) ? after : null;
        _viewModel.ModifiedBefore = DateTime.TryParse(ModifiedBeforeBox.Text, out var before) ? before : null;
        _viewModel.PathContains = string.IsNullOrWhiteSpace(PathContainsBox.Text) ? null : PathContainsBox.Text;

        _viewModel.ApplyFilters();
        RefreshSummary();
        RefreshSelectedSizeStat();
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
            await File.WriteAllTextAsync(result.Path, csv);
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
    // ResultsPage.xaml. Opens the side-by-side comparison viewer over every
    // file in the tapped file's duplicate group - only for media categories
    // (Image/Video/Audio) where there's something to actually preview;
    // tapping a document/archive/executable/other row's icon is a no-op.
    private void FileThumbnail_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SelectableFile file })
        {
            return;
        }

        if (file.Category is not (MimeCategory.Image or MimeCategory.Video or MimeCategory.Audio))
        {
            return;
        }

        var group = _viewModel.Groups.FirstOrDefault(g => g.Files.Contains(file));
        if (group is null)
        {
            return;
        }

        ComparisonViewer.ShowGroup(group.Files.ToList());
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
        confirmDialog.Content = BuildRemoveConfirmationContent(selectedCount, totalSizeBytes, riskyFiles, confirmDialog);

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
                RefreshSummary();
                RefreshSelectedSizeStat();
                RefreshSimilaritySection();
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
                RefreshSummary();
                RefreshSelectedSizeStat();
                RefreshSimilaritySection();

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
        int selectedCount, long totalSizeBytes, IReadOnlyList<(string Path, long SizeBytes)> riskyFiles, ContentDialog dialog)
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

        if (riskyFiles.Count == 0)
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

        var enableHint = new TextBlock
        {
            Text = "Check the box above to enable removal.",
            FontSize = 11.5,
            Foreground = (Brush)resources["TextDisabledHintBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var acknowledgeCheckBox = new CheckBox
        {
            Margin = new Thickness(0, 4, 0, 0),
            Content = new TextBlock
            {
                Text = "I understand these may be programs or installers, and I still want to remove them.",
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

        foreach (var item in flyout.Items)
        {
            if (item is MenuFlyoutItem menuItem)
            {
                menuItem.DataContext = target.DataContext;
            }
        }
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
