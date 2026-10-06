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

    // The loaded candidates, kept so the header and CATEGORY counts can be
    // recounted (ignored files excluded) after every ignore change.
    private IReadOnlyList<SelectableFile> _allFiles = Array.Empty<SelectableFile>();
    private long _thresholdBytes;
    private string? _targetLabel;
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
        // Min size pre-fills with the scan's own threshold (in MB); everything
        // below it wasn't loaded onto this page, so "no min" would be misleading.
        MinSizeValueBox.Text = (_thresholdBytes / (1024.0 * 1024.0)).ToString("0.##");
        MinSizeUnitCombo.SelectedIndex = 0; // MB
        MaxSizeValueBox.Text = "";
        MaxSizeUnitCombo.SelectedIndex = 0; // MB
        PathContainsBox.Text = "";
        ModifiedAfterPicker.Date = null;
        ModifiedBeforePicker.Date = null;
        ClearModifiedAfterButton.Visibility = Visibility.Collapsed;
        ClearModifiedBeforeButton.Visibility = Visibility.Collapsed;
        _viewModel.CategoryFilter.Clear();
        _viewModel.MinSizeBytes = _thresholdBytes;
        _viewModel.MaxSizeBytes = null;
        _viewModel.PathContains = null;
        _viewModel.ModifiedAfter = null;
        _viewModel.ModifiedBefore = null;
        _viewModel.ExtensionFilter = null;
        // "Show ignored" resets with the rest of the filters - without this a
        // NEW scan silently inherited the toggle from the previous one.
        _viewModel.ShowIgnored = false;
        UpdateShowIgnoredChip();

        _allFiles = request.CandidateFiles;
        _viewModel.LoadFiles(request.CandidateFiles);
        PopulateCategorySidebar(request.CandidateFiles);
        PopulateExtensionCombo();
        _suppressFilterControlEvents = false;

        _selectOverThresholdBytes = Math.Max(_thresholdBytes * 5, _thresholdBytes + 1);
        SelectOverButton.Content = $"Over {FileSizeFormatter.Format(_selectOverThresholdBytes)}…";

        RefreshHeaderStats();
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
    // from the full candidate list, not the currently-filtered view (unlike
    // ShowingCountText below, which tracks the active filters). Ignored files
    // are left out, though: they're kept on purpose and hidden everywhere, so
    // counting them disagreed with the list after an ignore (QA-5). Re-run on
    // every ignore change.
    private void RefreshHeaderStats()
    {
        TitleText.Text = $"Large files in {(string.IsNullOrWhiteSpace(_targetLabel) ? "your files" : _targetLabel)}";

        var allFiles = _allFiles.Where(f => !IgnoreService.IsIgnored(f.Path)).ToList();
        var totalBytes = allFiles.Sum(f => f.SizeBytes);
        SummaryText.Text = $"{allFiles.Count} file{(allFiles.Count == 1 ? "" : "s")} over "
            + $"{FileSizeFormatter.Format(_thresholdBytes)} · {FileSizeFormatter.Format(totalBytes)} total · biggest first";
    }

    // Multi-select category checklist with live match counts - identical to
    // ResultsPage's PopulateCategorySidebar. Every category present is shown (no
    // "+N" collapse), plus an "All" row that clears the category filter.
    private void PopulateCategorySidebar(IReadOnlyList<SelectableFile> allFiles)
    {
        var breakdown = allFiles
            .GroupBy(f => f.Category)
            .Select(g => (Category: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        var items = new List<CategorySidebarItem> { new(Category: null, Label: "All") };
        items.AddRange(breakdown.Select(b => new CategorySidebarItem(b.Category, ChipLabel(b.Category))));

        CategorySidebar.ItemsSource = items;
        items[0].IsSelected = true; // "All"
        RefreshCategoryCounts();
    }

    // Same as ResultsPage.RefreshCategoryCounts: ignored files excluded,
    // updated in place so the category selection survives (QA-5).
    private void RefreshCategoryCounts()
    {
        if (CategorySidebar.ItemsSource is not IEnumerable<CategorySidebarItem> items)
        {
            return;
        }

        var files = _allFiles.Where(f => !IgnoreService.IsIgnored(f.Path)).ToList();
        foreach (var item in items)
        {
            item.Count = item.Category is { } category ? files.Count(f => f.Category == category) : files.Count;
        }
    }

    // Fills the Extension dropdown with exactly the extensions present in the
    // loaded files (LargeFilesResultsViewModel.AvailableExtensions), same as
    // ResultsPage.PopulateExtensionCombo. Runs under _suppressFilterControlEvents.
    private void PopulateExtensionCombo()
    {
        ExtensionCombo.Items.Clear();
        foreach (var ext in _viewModel.AvailableExtensions)
        {
            ExtensionCombo.Items.Add(new ComboBoxItem { Content = ext });
        }

        ExtensionCombo.SelectedIndex = -1; // "any"
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

    // The category rail is a multi-select checklist - identical to
    // ResultsPage.CategoryItem_Click: tap specific categories to include several
    // at once, or tap "All" to clear the category filter. "All" is checked
    // exactly when no specific category is.
    private void CategoryItem_Click(object sender, Microsoft.UI.Xaml.Controls.ItemClickEventArgs e)
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

        ApplyFiltersAndRefresh();
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
        // Clear resets min to the scan's own threshold (everything below it
        // wasn't even loaded onto this page); every other filter goes empty.
        MinSizeValueBox.Text = (_thresholdBytes / (1024.0 * 1024.0)).ToString("0.##");
        MinSizeUnitCombo.SelectedIndex = 0; // MB
        MaxSizeValueBox.Text = "";
        MaxSizeUnitCombo.SelectedIndex = 0; // MB
        PathContainsBox.Text = "";
        ModifiedAfterPicker.Date = null;
        ModifiedBeforePicker.Date = null;
        ClearModifiedAfterButton.Visibility = Visibility.Collapsed;
        ClearModifiedBeforeButton.Visibility = Visibility.Collapsed;
        ExtensionCombo.SelectedIndex = -1; // "any"
        ResetCategorySelection();
        _suppressFilterControlEvents = false;

        _viewModel.CategoryFilter.Clear();
        SyncFiltersFromControlsAndApply();
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
        _viewModel.PathContains = string.IsNullOrWhiteSpace(PathContainsBox.Text) ? null : PathContainsBox.Text;
        _viewModel.ModifiedAfter = ModifiedAfterPicker.Date?.Date;
        _viewModel.ModifiedBefore = ModifiedBeforePicker.Date?.Date;
        // Each date's ✕ shows only while that date is set (QA-19).
        ClearModifiedAfterButton.Visibility = ModifiedAfterPicker.Date is null ? Visibility.Collapsed : Visibility.Visible;
        ClearModifiedBeforeButton.Visibility = ModifiedBeforePicker.Date is null ? Visibility.Collapsed : Visibility.Visible;
        _viewModel.ExtensionFilter = (ExtensionCombo.SelectedItem as ComboBoxItem)?.Content as string;

        ApplyFiltersAndRefresh();
    }

    // Converts a size field (numeric text + MB/GB unit dropdown) to bytes. Blank
    // or non-positive input means "no bound". Identical to ResultsPage.SizeFieldToBytes.
    private static long? SizeFieldToBytes(string text, ComboBox unitCombo)
    {
        if (!double.TryParse(text, out var value) || value <= 0)
        {
            return null;
        }

        var unitBytes = unitCombo.SelectedIndex == 1 ? 1024L * 1024 * 1024 : 1024L * 1024; // GB : MB
        return (long)(value * unitBytes);
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
            // UTF-8 WITH a byte-order mark: Excel opens a BOM-less .csv in the
            // ANSI code page, which turned "KEEP — newest" into "KEEP â€” newest"
            // (and would mangle any non-ASCII file path) - QA-18.
            await File.WriteAllTextAsync(result.Path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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

        var ignored = target.DataContext is SelectableFile { IsIgnored: true };
        foreach (var item in flyout.Items)
        {
            if (item is not MenuFlyoutItem menuItem)
            {
                continue;
            }

            menuItem.DataContext = target.DataContext;
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

    // "Show ignored" is styled as a CATEGORY-checklist row (rounded check + tick)
    // rather than a stock CheckBox, so its selected state is painted here to
    // match those chips (accent fill/border + visible tick + white label).
    private void ShowIgnored_Toggle(object sender, RoutedEventArgs e)
    {
        _viewModel.ShowIgnored = !_viewModel.ShowIgnored;
        UpdateShowIgnoredChip();
        ApplyIgnoreChange(() => { });
    }

    private void UpdateShowIgnoredChip()
    {
        var on = _viewModel.ShowIgnored;
        var resources = Application.Current.Resources;
        ShowIgnoredCheck.Background = on
            ? (Brush)resources["AccentBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ShowIgnoredCheck.BorderBrush = (Brush)resources[on ? "AccentBrush" : "SurfaceCardBorderBrush"];
        ShowIgnoredCheckMark.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ShowIgnoredLabel.Foreground = on
            ? new SolidColorBrush(Microsoft.UI.Colors.White)
            : (Brush)resources["TextMutedBrush"];

        // The check state is otherwise invisible to UIA (this is a Button, not
        // a ToggleButton) - fold it into the announced name.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ShowIgnoredButton, on ? "Show ignored, on" : "Show ignored, off");
    }

    private void ApplyIgnoreChange(Action mutate)
    {
        mutate();
        // A checked file the user just ignored must not carry its checkmark
        // into Remove Selected - same rule as ResultsPage.ApplyIgnoreChange.
        _viewModel.ClearIgnoredSelections();
        _viewModel.ApplyFilters();
        RefreshHeaderStats();
        RefreshCategoryCounts();
        RefreshShowingCount();
        RefreshSelectedSizeStat();
    }

    // Mirrors ResultsPage.OpenFile_Click - the two row menus offer the same
    // superset of actions.
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
    /// Category checklist row data - identical to ResultsPage.xaml.cs's private
    /// CategorySidebarItem (not a record so mutating IsSelected raises
    /// PropertyChanged and the bound Background/BorderBrush/Foreground refresh).
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

        // File count for this category in the loaded results, ignored files
        // excluded (total for "All"). Settable + notifying so an ignore change
        // recounts in place (QA-5).
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
