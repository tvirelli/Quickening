using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Quickening.App.Controls;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Models;
using Quickening.Core.Orchestration;
using Quickening.Core.Safety;

namespace Quickening.App.Views;

public sealed partial class LargeFilesResultsPage : Page
{
    // Same NavigationCacheMode="Enabled" reload guard ResultsPage.xaml.cs
    // uses, for the same reason: without it, re-navigating here (e.g. via
    // the "← Summary" round trip) would reload the original candidate list
    // and silently resurrect files already removed this session.
    private IReadOnlyList<SelectableFile>? _loadedFiles;
    private ScanResult? _scanResult;
    private long _thresholdBytes;
    private string? _targetLabel;
    private List<MimeCategory> _overflowCategories = new();
    private long _selectOverThresholdBytes;

    // Held as its own field so RemoveSelectedFilesAsync can hand this SAME
    // instance to CelebrationParameters - see ResultsPage.xaml.cs's
    // identical field for why Undo only works against the exact
    // IRecycleBinService that performed the delete.
    private readonly IRecycleBinService _recycleBinService = new RecycleBinService();
    private readonly LargeFilesResultsViewModel _viewModel;

    private bool _suppressFilterControlEvents;

    public LargeFilesResultsPage()
    {
        _viewModel = new LargeFilesResultsViewModel(_recycleBinService, App.Store);
        InitializeComponent();
        FilesListView.ItemsSource = _viewModel.Files;
        ComparisonViewer.CloseRequested += (_, _) => ComparisonViewer.Visibility = Visibility.Collapsed;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not LargeFilesResultsNavigationRequest request)
        {
            return;
        }

        if (ReferenceEquals(request.CandidateFiles, _loadedFiles))
        {
            return;
        }

        _loadedFiles = request.CandidateFiles;
        _scanResult = request.ScanResult;
        _thresholdBytes = request.ThresholdBytes;
        _targetLabel = request.TargetLabel;

        _suppressFilterControlEvents = true;
        MinSizeBox.Value = _thresholdBytes / (1024.0 * 1024.0);
        MaxSizeBox.Value = double.NaN;
        PathContainsBox.Text = "";
        _viewModel.CategoryFilter.Clear();
        _viewModel.MinSizeBytes = _thresholdBytes;
        _viewModel.MaxSizeBytes = null;
        _viewModel.PathContains = null;

        _viewModel.LoadFiles(request.CandidateFiles);
        PopulateCategoryChips(request.CandidateFiles);
        _suppressFilterControlEvents = false;

        _selectOverThresholdBytes = Math.Max(_thresholdBytes * 5, _thresholdBytes + 1);
        SelectOverButton.Content = $"Over {FileSizeFormatter.Format(_selectOverThresholdBytes)}…";

        RefreshHeaderStats(request.CandidateFiles);
        RefreshShowingCount();
        RefreshSelectedSizeStat();
    }

    // Same pattern as ResultsPage.xaml.cs's BackToSummary_Click, replacing
    // the old shared MainWindow.SetBackAction mechanism (Task 3) - falls
    // back to Home when no ScanResult is available (e.g. a caller that only
    // had candidateFiles/thresholdBytes on hand - see
    // MainWindow.ShowLargeFilesResults's optional trailing parameters).
    private void BackToSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is { } result)
        {
            ((MainWindow)App.MainWindowInstance!).ShowScanComplete(
                result, isLargeFilesMode: true, thresholdBytes: _thresholdBytes, targetLabel: _targetLabel);
        }
        else
        {
            ((MainWindow)App.MainWindowInstance!).ShowHome();
        }
    }

    // "42 files over 100 MB · 61.3 GB total · biggest first" (screen 2h) -
    // computed once from the full unfiltered candidate list at load time,
    // not re-derived from the currently-filtered view (unlike ShowingCountText
    // below, which does track the active filters).
    private void RefreshHeaderStats(IReadOnlyList<SelectableFile> allFiles)
    {
        TitleText.Text = $"Large files in {(string.IsNullOrWhiteSpace(_targetLabel) ? "your files" : _targetLabel)}";

        var totalBytes = allFiles.Sum(f => f.SizeBytes);
        SummaryText.Text = $"{allFiles.Count} file{(allFiles.Count == 1 ? "" : "s")} over "
            + $"{FileSizeFormatter.Format(_thresholdBytes)} · {FileSizeFormatter.Format(totalBytes)} total · biggest first";
    }

    // Caps individually-rendered category chips (screen 2h shows "All" plus
    // up to four categories before folding the rest into a single "+N"
    // chip) - MimeCategory has seven values, so without a cap every scan
    // touching enough categories would overflow the header's available
    // width. Categories beyond the cap are still fully filterable, just via
    // the "+N" chip's flyout (see CategoryChipsList_SelectionChanged) rather
    // than their own permanent chip.
    private const int MaxVisibleCategoryChips = 4;

    private void PopulateCategoryChips(IReadOnlyList<SelectableFile> allFiles)
    {
        var breakdown = allFiles
            .GroupBy(f => f.Category)
            .Select(g => (Category: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        var items = new List<CategoryChipItem> { new(Category: null, Label: "All") };
        var visible = breakdown.Take(MaxVisibleCategoryChips).ToList();
        var overflow = breakdown.Skip(MaxVisibleCategoryChips).ToList();

        items.AddRange(visible.Select(b => new CategoryChipItem(b.Category, ChipLabel(b.Category))));

        _overflowCategories = overflow.Select(o => o.Category).ToList();
        if (_overflowCategories.Count > 0)
        {
            items.Add(new CategoryChipItem(Category: null, Label: $"+{_overflowCategories.Count}", IsOverflow: true));
        }

        CategoryChipsList.ItemsSource = items;
        CategoryChipsList.SelectedIndex = 0;
        items[0].IsSelected = true;
    }

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

    private void CategoryChipsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterControlEvents)
        {
            return;
        }

        if (CategoryChipsList.SelectedItem is not CategoryChipItem item)
        {
            return;
        }

        if (item.IsOverflow)
        {
            ShowOverflowCategoryFlyout(item);
            return;
        }

        if (CategoryChipsList.ItemsSource is IEnumerable<CategoryChipItem> allItems)
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

        ApplyFiltersAndRefresh();
    }

    // The "+N" chip isn't itself a category - clicking it opens a flyout of
    // the categories that didn't fit as their own chip (see
    // PopulateCategoryChips); picking one filters to just that category,
    // same as clicking a regular chip would, and the "+N" chip itself
    // stays visually selected as feedback that an overflow category is the
    // active filter (there's no individual chip for it to highlight instead).
    private void ShowOverflowCategoryFlyout(CategoryChipItem overflowItem)
    {
        var flyout = new MenuFlyout();
        foreach (var category in _overflowCategories)
        {
            var menuItem = new MenuFlyoutItem { Text = ChipLabel(category) };
            menuItem.Click += (_, _) =>
            {
                if (CategoryChipsList.ItemsSource is IEnumerable<CategoryChipItem> allItems)
                {
                    foreach (var chip in allItems)
                    {
                        chip.IsSelected = ReferenceEquals(chip, overflowItem);
                    }
                }

                _viewModel.CategoryFilter.Clear();
                _viewModel.CategoryFilter.Add(category);
                ApplyFiltersAndRefresh();
            };
            flyout.Items.Add(menuItem);
        }

        flyout.ShowAt(CategoryChipsList);
    }

    // Same per-keystroke rebuild problem (and same debounce) as
    // ResultsPage.FilterChanged - see its comment.
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
        MinSizeBox.Value = _thresholdBytes / (1024.0 * 1024.0); // Clear resets to the scan's own threshold, not to nothing - everything below it wasn't even loaded onto this page.
        MaxSizeBox.Value = double.NaN;
        PathContainsBox.Text = "";
        CategoryChipsList.SelectedIndex = 0; // "All"
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
        _viewModel.PathContains = string.IsNullOrWhiteSpace(PathContainsBox.Text) ? null : PathContainsBox.Text;

        ApplyFiltersAndRefresh();
    }

    private void ApplyFiltersAndRefresh()
    {
        _viewModel.ApplyFilters();
        RefreshShowingCount();
        RefreshSelectedSizeStat();
    }

    // "Showing 11 of 42" (screen 2h) - unlike RefreshHeaderStats above, this
    // reflects the CURRENTLY filtered view against the full candidate set.
    private void RefreshShowingCount()
    {
        var total = _loadedFiles?.Count ?? 0;
        var shown = _viewModel.Files.Count;
        ShowingCountText.Text = shown == total
            ? $"Showing all {total}"
            : $"Showing {shown} of {total} (filters applied)";
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

    private void SelectOverThreshold_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectBySizeThreshold(_selectOverThresholdBytes);
        RefreshSelectedSizeStat();
    }

    // See ResultsPage.xaml.cs's SelectByFolder_Click for why this specific
    // FolderPicker pattern (Microsoft.Windows.Storage.Pickers, not the
    // classic Windows.Storage picker).
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

    // See ResultsPage.xaml.cs's ExportCsv_Click for the shared design intent
    // (currently-visible/filtered rows only, "PHASE 2" in the mockup but
    // built for real per this batch's decision).
    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(windowId)
        {
            SuggestedFileName = $"quickening-large-files-{DateTime.Now:yyyy-MM-dd}",
        };
        picker.FileTypeChoices.Add("CSV file", new List<string> { ".csv" });

        var result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return;
        }

        try
        {
            var csv = BuildLargeFilesCsv(_viewModel.Files);
            await File.WriteAllTextAsync(result.Path, csv);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Logger?.LogError($"CSV export failed: {ex}");
            await ResultsPage.ShowExportFailedDialogAsync(XamlRoot, result.Path);
        }
    }

    private static string BuildLargeFilesCsv(IEnumerable<SelectableFile> files)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Path,Category,SizeBytes,LastModifiedUtc");

        foreach (var file in files)
        {
            builder.Append(CsvField(file.Path)).Append(',');
            builder.Append(CsvField(file.Category.ToString())).Append(',');
            builder.Append(file.SizeBytes).Append(',');
            builder.AppendLine(CsvField(file.LastWriteTimeUtc.ToString("O")));
        }

        return builder.ToString();
    }

    // See ResultsPage.xaml.cs's identical CsvField for the quoting rule.
    private static string CsvField(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void FileSelectionChanged(object sender, RoutedEventArgs e) => RefreshSelectedSizeStat();

    // "{size} will come home · N files selected · M program file(s)" (screen 2h)
    private void RefreshSelectedSizeStat()
    {
        var selectedFiles = _viewModel.Files.Where(f => f.IsSelected).ToList();
        var selectedCount = selectedFiles.Count;
        var riskyCount = selectedFiles.Count(f => RiskyExtensions.IsRisky(f.Path));

        SelectedSizeText.Text = FileSizeFormatter.Format(_viewModel.GetSelectedSizeBytes());

        var callout = $"will come home · {selectedCount} file{(selectedCount == 1 ? "" : "s")} selected";
        if (riskyCount > 0)
        {
            callout += $" · {riskyCount} program file{(riskyCount == 1 ? "" : "s")}";
        }

        SelectedCalloutText.Text = callout;
    }

    // Kicks off the lazy video poster frame for a realized row; the shell type
    // icon loads itself on first bind (SelectableFile.FileTypeIconSource) so it
    // survives container recycling in the virtualized list.
    private void FileThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SelectableFile file })
        {
            _ = file.LoadVideoThumbnailAsync();
        }
    }

    private async void FileThumbnail_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SelectableFile file })
        {
            await PreviewOrOpenAsync(file);
        }
    }

    // Opens the single-file preview for any previewable type (media, code/text,
    // PDF, archive contents); a type with no viewer (proprietary/binary) offers
    // to open in its default app behind a confirm. Unlike Results' grouped view,
    // Large Files has no duplicate group, so ShowGroup gets just this one file
    // (its "1 panel" support handles that).
    private async Task PreviewOrOpenAsync(SelectableFile file)
    {
        if (Controls.FileViewerRouter.ForPath(file.Path, file.Category) == Controls.FileViewerKind.None)
        {
            await ResultsPage.ConfirmAndOpenExternallyAsync(XamlRoot, file);
            return;
        }

        ComparisonViewer.ShowGroup(new[] { file });
    }

    private void FileThumbnail_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SelectableFile file })
        {
            file.ReportThumbnailFailed();
        }
    }

    // Same WinUI ContextFlyout/DataContext limitation ResultsPage.xaml.cs's
    // identical handler documents (microsoft/microsoft-ui-xaml#911).
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

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { DataContext: SelectableFile file })
        {
            return;
        }

        Quickening.Core.Shell.ExplorerLauncher.SelectInExplorer(file.Path);
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SelectableFile file })
        {
            await PreviewOrOpenAsync(file);
        }
    }

    // "Select this file" checks this row's box without touching any other
    // row's selection state - a context-menu equivalent of ticking the
    // checkbox directly, not an exclusive "select only this one" action.
    private void SelectThisFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { DataContext: SelectableFile file })
        {
            return;
        }

        // Network-drive files can't be recycled (SendToRecycleBin refuses
        // them) - honor the same guard every other selection path uses (mass
        // select, the row checkbox's IsEnabled binding) so this menu can't
        // tick one into a doomed delete.
        if (file.IsOnNetworkDrive)
        {
            return;
        }

        file.IsSelected = true;
        RefreshSelectedSizeStat();
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e) => await RemoveSelectedFilesAsync();

    // Mirrors ResultsPage.xaml.cs's RemoveSelectedFilesAsync (Task 9) -
    // confirm -> Progress (removal) -> Celebration on full success, or back
    // to this page with the partial-failure dialog on top. No group
    // pruning/relabeling here since LargeFilesResultsViewModel has no
    // grouping concept - a removed file's row just disappears.
    private async Task RemoveSelectedFilesAsync()
    {
        // Same protected-paths pass-through as ResultsPage - see its comment.
        _recycleBinService.AllowProtectedPaths = App.Settings.AllowProtectedPaths;

        var selectedCount = _viewModel.Files.Count(f => f.IsSelected);
        if (selectedCount == 0)
        {
            return;
        }

        var riskyPaths = _viewModel.GetSelectedRiskyFilePaths();
        var riskySet = new HashSet<string>(riskyPaths);
        var riskyFiles = _viewModel.Files
            .Where(f => riskySet.Contains(f.Path))
            .Select(f => (f.Path, f.SizeBytes))
            .ToList();
        var totalSizeBytes = _viewModel.GetSelectedSizeBytes();

        // See ResultsPage.xaml.cs's identical capture in RemoveSelectedFilesAsync
        // for why this must happen before DeleteSelectedAsync runs.
        var selectedPaths = _viewModel.Files.Where(f => f.IsSelected).Select(f => f.Path).ToList();

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
        confirmDialog.Content = ResultsPage.BuildRemoveConfirmationContent(selectedCount, totalSizeBytes, riskyFiles, confirmDialog);

        var choice = await confirmDialog.ShowAsync();
        if (choice != ContentDialogResult.Primary)
        {
            return;
        }

        var parameters = new ProgressPageParameters(
            // The removal-headline pool is generic ("them" / "the extras"),
            // so it reads fine for large files too - not just duplicates.
            Headline: RandomPhrases.RemovalHeadline(),
            RingHue: ProgressRingHue.Success,
            Operation: async (progress, cancellationToken) =>
            {
                var failed = await _viewModel.DeleteSelectedAsync(
                    new Progress<DeleteProgress>(p =>
                        progress.Report(new ProgressUpdate(p.Processed, p.Total, p.Path))),
                    cancellationToken,
                    targetLabel: _targetLabel ?? "your files");
                return (object?)new RemovalOutcome(failed, totalSizeBytes, selectedCount);
            },
            OnCancelled: () =>
            {
                RefreshShowingCount();
                RefreshSelectedSizeStat();
                ((MainWindow)App.MainWindowInstance!).ShowLargeFilesResults(
                    _loadedFiles ?? Array.Empty<SelectableFile>(), _thresholdBytes, _scanResult, _targetLabel);
            },
            Completed: async outcomeObj =>
            {
                var outcome = (RemovalOutcome)outcomeObj!;

                // Refresh both stat labels regardless of outcome - see the
                // identical fix/comment in ResultsPage.xaml.cs's
                // RemoveSelectedFilesAsync for why: "Review what's left"
                // returns to this SAME cached page instance with the SAME
                // _loadedFiles reference (see RemainingLargeFiles below),
                // so OnNavigatedTo's ReferenceEquals reload guard skips its
                // own refresh - this is the only place left that would
                // otherwise update these labels.
                RefreshShowingCount();
                RefreshSelectedSizeStat();

                if (outcome.Failed.Count == 0)
                {
                    ((MainWindow)App.MainWindowInstance!).ShowCelebration(
                        new CelebrationParameters(
                            outcome.SizeBytesAttempted, outcome.FileCountAttempted, _scanResult,
                            // _loadedFiles (not a fresh _viewModel.Files.ToList()) so
                            // ReviewWhatsLeft_Click's ShowLargeFilesResults call passes
                            // back the SAME reference this page's OnNavigatedTo already
                            // holds - matching it trips the ReferenceEquals reload guard,
                            // which is what preserves whatever category/size filters the
                            // user had applied instead of silently resetting them back to
                            // the scan's own default threshold.
                            RemainingLargeFiles: _loadedFiles, LargeFilesThresholdBytes: _thresholdBytes,
                            // Nothing failed in this branch, so every selected
                            // path really did make it to the Recycle Bin.
                            RemovedFilePaths: selectedPaths, RecycleBinService: _recycleBinService));
                    return;
                }

                ((MainWindow)App.MainWindowInstance!).ShowLargeFilesResults(
                    _loadedFiles ?? Array.Empty<SelectableFile>(), _thresholdBytes, _scanResult, _targetLabel);

                await ResultsPage.ShowPartialFailureDialogAsync(
                    outcome.Failed, outcome.SizeBytesAttempted, outcome.FileCountAttempted,
                    _viewModel.Files, XamlRoot, RemoveSelectedFilesAsync);
            });

        ((MainWindow)App.MainWindowInstance!).ShowProgress(parameters);
    }

    private sealed record RemovalOutcome(IReadOnlyList<string> Failed, long SizeBytesAttempted, int FileCountAttempted);

    /// <summary>
    /// Category filter chip data (screen 2h's chip row) - same shape as
    /// ResultsPage.xaml.cs's private CategorySidebarItem, plus IsOverflow for
    /// the "+N" chip (see PopulateCategoryChips/ShowOverflowCategoryFlyout).
    /// </summary>
    private sealed class CategoryChipItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public MimeCategory? Category { get; }
        public string Label { get; }
        public bool IsOverflow { get; }

        public CategoryChipItem(MimeCategory? Category, string Label, bool IsOverflow = false)
        {
            this.Category = Category;
            this.Label = Label;
            this.IsOverflow = IsOverflow;
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
