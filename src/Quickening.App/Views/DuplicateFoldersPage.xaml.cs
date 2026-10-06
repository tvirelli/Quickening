using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Orchestration;

namespace Quickening.App.Views;

public sealed partial class DuplicateFoldersPage : Page
{
    private readonly IRecycleBinService _recycleBinService = new RecycleBinService();

    private ScanResult? _scanResult;
    private bool _isLargeFilesMode;
    private long? _thresholdBytes;
    private string? _targetLabel;

    // Mutable working copy of the sets: each inner list is one duplicate-folder
    // set, exactly one of which is kept. Rebuilt into cards after any removal.
    private readonly List<List<FolderChoice>> _groups = new();

    public DuplicateFoldersPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not DuplicateFoldersNavigationRequest request)
        {
            return;
        }

        _scanResult = request.ScanResult;
        _isLargeFilesMode = request.IsLargeFilesMode;
        _thresholdBytes = request.ThresholdBytes;
        _targetLabel = request.TargetLabel;

        _groups.Clear();
        foreach (var group in request.Groups)
        {
            var choices = group.Folders
                .Select(f => new FolderChoice { Path = f.Path, SizeBytes = f.SizeBytes, FileCount = f.FileCount })
                .ToList();
            // Default: keep the first copy (alphabetical), remove the rest.
            choices[0].Keep = true;
            _groups.Add(choices);
        }

        BuildCards();
    }

    private void BuildCards()
    {
        GroupsPanel.Children.Clear();
        var resources = Application.Current.Resources;
        var groupIndex = 0;
        foreach (var group in _groups)
        {
            GroupsPanel.Children.Add(BuildGroupCard(resources, group, groupIndex));
            groupIndex++;
        }

        var setCount = _groups.Count;
        SummaryText.Text = $"{setCount} set{(setCount == 1 ? "" : "s")} of identical folders — keep one copy of each";
        RefreshSelectedCallout();
    }

    private UIElement BuildGroupCard(ResourceDictionary resources, List<FolderChoice> group, int groupIndex)
    {
        var stack = new StackPanel { Spacing = 10 };

        var sizeEach = group[0].SizeBytes;
        var fileCount = group[0].FileCount;
        stack.Children.Add(new TextBlock
        {
            Text = $"{group.Count} identical copies · {fileCount} file{(fileCount == 1 ? "" : "s")} · {FileSizeFormatter.Format(sizeEach)} each",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = "The selected copy is kept; the others move to the Recycle Bin.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12,
            Foreground = (Brush)resources["TextMutedBrush"],
        });

        var groupName = $"dupfolder-group-{groupIndex}";
        foreach (var choice in group)
        {
            stack.Children.Add(BuildFolderRow(resources, group, choice, groupName));
        }

        return new Border
        {
            CornerRadius = (CornerRadius)resources["RadiusCard"],
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 16, 18, 16),
            Child = stack,
        };
    }

    private UIElement BuildFolderRow(ResourceDictionary resources, List<FolderChoice> group, FolderChoice choice, string groupName)
    {
        var grid = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var radio = new RadioButton
        {
            GroupName = groupName,
            IsChecked = choice.Keep,
            Content = "Keep",
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13,
            Foreground = (Brush)resources["TextSecondaryBrush"],
        };
        radio.Checked += (_, _) =>
        {
            foreach (var c in group)
            {
                c.Keep = ReferenceEquals(c, choice);
            }

            RefreshSelectedCallout();
        };
        Grid.SetColumn(radio, 0);
        grid.Children.Add(radio);

        var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        nameStack.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetFileName(choice.Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)),
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = (double)resources["FontSizeRowFilename"],
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)resources["TextBodyBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = choice.Path,
            FontFamily = (FontFamily)resources["MonoFontFamily"],
            FontSize = (double)resources["FontSizeFilePath"],
            Foreground = (Brush)resources["TextDisabledHintBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(nameStack, 1);
        grid.Children.Add(nameStack);

        var openButton = new Button
        {
            Content = "Open",
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Foreground = (Brush)resources["TextSecondaryBrush"],
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12,
            Padding = new Thickness(12, 5, 12, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Controls.PillCornerRadius.SetEnabled(openButton, true);
        openButton.Click += (_, _) => Quickening.Core.Shell.ExplorerLauncher.SelectInExplorer(choice.Path);
        Grid.SetColumn(openButton, 2);
        grid.Children.Add(openButton);

        return grid;
    }

    private void RefreshSelectedCallout()
    {
        var toRemove = _groups.SelectMany(g => g).Where(c => !c.Keep).ToList();
        var count = toRemove.Count;
        var bytes = toRemove.Sum(c => c.SizeBytes);

        SelectedCalloutText.Text = count == 0
            ? "Nothing to remove"
            : $"{count} extra folder{(count == 1 ? "" : "s")} · {FileSizeFormatter.Format(bytes)} to reclaim";
        RemoveButton.IsEnabled = count > 0;
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
        var toRemove = _groups.SelectMany(g => g).Where(c => !c.Keep).ToList();
        if (toRemove.Count == 0)
        {
            return;
        }

        _recycleBinService.AllowProtectedPaths = App.Settings.AllowProtectedPaths;

        var totalBytes = toRemove.Sum(c => c.SizeBytes);
        var confirmDialog = new ContentDialog
        {
            Style = (Style)Application.Current.Resources["NebulaContentDialogStyle"],
            Width = (double)Application.Current.Resources["DialogWidthConfirm"],
            Title = "Remove the extra copies?",
            Content = $"Move {toRemove.Count} whole folder{(toRemove.Count == 1 ? "" : "s")} "
                + $"({FileSizeFormatter.Format(totalBytes)}) to the Recycle Bin? One copy of each set is kept, "
                + "and everything is restorable from the bin.",
            PrimaryButtonText = $"Remove {toRemove.Count} folder{(toRemove.Count == 1 ? "" : "s")}",
            CloseButtonText = "Keep everything",
            PrimaryButtonStyle = (Style)Application.Current.Resources["PillCtaButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["PillSecondaryButtonStyle"],
            XamlRoot = XamlRoot,
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        RemoveButton.IsEnabled = false;
        var failed = new List<string>();
        foreach (var choice in toRemove)
        {
            try
            {
                if (System.IO.Directory.Exists(choice.Path))
                {
                    await Task.Run(() => _recycleBinService.SendToRecycleBin(choice.Path));
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                failed.Add(choice.Path);
            }
        }

        // Drop every recycled folder; a set that falls below 2 copies is done.
        var failedSet = new HashSet<string>(failed, StringComparer.OrdinalIgnoreCase);
        foreach (var group in _groups)
        {
            group.RemoveAll(c => !c.Keep && !failedSet.Contains(c.Path));
        }

        _groups.RemoveAll(g => g.Count < 2);

        if (_groups.Count == 0)
        {
            BackToSummary_Click(this, new RoutedEventArgs());
            return;
        }

        BuildCards();

        if (failed.Count > 0)
        {
            var dialog = new ContentDialog
            {
                Style = (Style)Application.Current.Resources["NebulaContentDialogStyle"],
                Width = (double)Application.Current.Resources["DialogWidthConfirm"],
                Title = "A few couldn't be removed",
                Content = $"{failed.Count} folder{(failed.Count == 1 ? "" : "s")} couldn't be moved to the Recycle Bin "
                    + "(likely open or in use elsewhere). The rest were removed.",
                CloseButtonText = "OK",
                CloseButtonStyle = (Style)Application.Current.Resources["PillSecondaryButtonStyle"],
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    private sealed class FolderChoice
    {
        public required string Path { get; init; }
        public required long SizeBytes { get; init; }
        public required int FileCount { get; init; }
        public bool Keep { get; set; }
    }
}
