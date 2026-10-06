using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Orchestration;
using Quickening.Core.Scanning;

namespace Quickening.App.Views;

public sealed partial class HousekeepingPage : Page
{
    private readonly IRecycleBinService _recycleBinService = new RecycleBinService();
    private readonly HousekeepingViewModel _viewModel;

    // Enough of the originating scan to return to the exact ScanCompletePage.
    private ScanResult? _scanResult;
    private bool _isLargeFilesMode;
    private long? _thresholdBytes;
    private string? _targetLabel;

    public HousekeepingPage()
    {
        _viewModel = new HousekeepingViewModel(_recycleBinService);
        InitializeComponent();
        EmptyFoldersList.ItemsSource = _viewModel.EmptyFolders;
        ZeroByteFilesList.ItemsSource = _viewModel.ZeroByteFiles;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HousekeepingNavigationRequest request)
        {
            return;
        }

        _scanResult = request.ScanResult;
        _isLargeFilesMode = request.IsLargeFilesMode;
        _thresholdBytes = request.ThresholdBytes;
        _targetLabel = request.TargetLabel;

        _viewModel.Load(request.EmptyItems);
        RefreshSectionsAndCounts();
    }

    private void RefreshSectionsAndCounts()
    {
        var folderCount = _viewModel.EmptyFolders.Count;
        var fileCount = _viewModel.ZeroByteFiles.Count;

        EmptyFoldersSection.Visibility = folderCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        ZeroByteSection.Visibility = fileCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Collapse an empty section's column so the other spans the full width
        // instead of leaving a blank half.
        EmptyFoldersColumn.Width = folderCount > 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ZeroByteColumn.Width = fileCount > 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        EmptyFoldersLabel.Text = $"EMPTY FOLDERS ({folderCount})";
        ZeroByteLabel.Text = $"ZERO-BYTE FILES ({fileCount})";

        SummaryText.Text = $"{Pluralize(folderCount, "empty folder")} · {Pluralize(fileCount, "zero-byte file")}";
        RefreshSelectedCallout();
    }

    private void RefreshSelectedCallout()
    {
        var selected = _viewModel.SelectedCount;
        SelectedCalloutText.Text = selected == 0
            ? "Nothing selected"
            : $"{Pluralize(selected, "item")} selected to tidy";
        RemoveButton.IsEnabled = selected > 0;
    }

    private static string Pluralize(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private void ItemSelectionChanged(object sender, RoutedEventArgs e) => RefreshSelectedCallout();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectAll();
        RefreshSelectedCallout();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectNone();
        RefreshSelectedCallout();
    }

    private void BackToSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is { } result)
        {
            ((MainWindow)App.MainWindowInstance!).ShowScanComplete(
                result, _isLargeFilesMode, _thresholdBytes, _targetLabel);
        }
        else
        {
            ((MainWindow)App.MainWindowInstance!).ShowHome();
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.AllItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        _recycleBinService.AllowProtectedPaths = App.Settings.AllowProtectedPaths;

        var folderCount = selected.Count(i => i.IsFolder);
        var fileCount = selected.Count - folderCount;

        var confirmDialog = new ContentDialog
        {
            Style = (Style)Application.Current.Resources["NebulaContentDialogStyle"],
            Width = (double)Application.Current.Resources["DialogWidthConfirm"],
            Title = "Tidy these up?",
            Content = $"Move {DescribeSelection(folderCount, fileCount)} to the Recycle Bin? "
                + "They're empty, so nothing inside is lost — and you can restore them from the bin.",
            PrimaryButtonText = $"Tidy up {Pluralize(selected.Count, "item")}",
            CloseButtonText = "Keep them",
            PrimaryButtonStyle = (Style)Application.Current.Resources["PillCtaButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["PillSecondaryButtonStyle"],
            XamlRoot = XamlRoot,
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        RemoveButton.IsEnabled = false;
        var failed = await _viewModel.DeleteSelectedAsync(selected);

        // Back on the UI thread (WinUI resumes await here): drop every selected
        // item that wasn't refused from its collection.
        var failedSet = new HashSet<string>(failed, StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (failedSet.Contains(item.Path))
            {
                continue;
            }

            if (item.IsFolder)
            {
                _viewModel.EmptyFolders.Remove(item);
            }
            else
            {
                _viewModel.ZeroByteFiles.Remove(item);
            }
        }

        RefreshSectionsAndCounts();

        if (failed.Count > 0)
        {
            await ShowPartialFailureDialogAsync(failed.Count);
        }

        // Nothing left to tidy - return to the summary the user came from.
        if (_viewModel.EmptyFolders.Count == 0 && _viewModel.ZeroByteFiles.Count == 0)
        {
            BackToSummary_Click(this, new RoutedEventArgs());
        }
    }

    private static string DescribeSelection(int folderCount, int fileCount)
    {
        if (folderCount > 0 && fileCount > 0)
        {
            return $"{Pluralize(folderCount, "empty folder")} and {Pluralize(fileCount, "zero-byte file")}";
        }

        return folderCount > 0 ? Pluralize(folderCount, "empty folder") : Pluralize(fileCount, "zero-byte file");
    }

    private async Task ShowPartialFailureDialogAsync(int failedCount)
    {
        var dialog = new ContentDialog
        {
            Style = (Style)Application.Current.Resources["NebulaContentDialogStyle"],
            Width = (double)Application.Current.Resources["DialogWidthConfirm"],
            Title = "A few couldn't be tidied",
            Content = $"{Pluralize(failedCount, "item")} couldn't be removed — most likely something landed "
                + "inside since the scan, or it's in use. The rest were tidied up.",
            CloseButtonText = "OK",
            CloseButtonStyle = (Style)Application.Current.Resources["PillSecondaryButtonStyle"],
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }
}
