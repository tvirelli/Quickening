using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quickening.App.Controls;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Storage;

namespace Quickening.App.Views;

public sealed partial class HistoryPage : Page
{
    private readonly HistoryViewModel _viewModel = new(App.Store);
    private readonly IRecycleBinService _recycleBinService = new RecycleBinService();

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var (bytesReclaimed, filesRemoved) = _viewModel.GetLifetimeStats();
        var sessions = _viewModel.GetSessions();

        var hasHistory = sessions.Count > 0;
        PopulatedPanel.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;

        if (!hasHistory)
        {
            return;
        }

        LifetimeBytesText.Text = FileSizeFormatter.Format(bytesReclaimed);
        LifetimeFilesText.Text = filesRemoved.ToString("N0");

        // One StackPanel holding all cards, added as a single ItemsControl
        // item. NOTE: SessionsList is an ItemsControl inside a ScrollViewer
        // (HistoryPage.xaml) - it does NOT virtualize either way, so there's
        // nothing to gain from adding cards individually, and doing so put
        // each card's star-column header Grid directly under a
        // ContentPresenter, which triggered a WinUI layout cycle
        // (LayoutCycleException) that froze the page. The single-panel form
        // is the shipped, cycle-free layout.
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 16) };
        foreach (var session in sessions)
        {
            stack.Children.Add(BuildSessionCard(session));
        }

        SessionsList.Items.Clear();
        SessionsList.Items.Add(stack);
    }

    private UIElement BuildSessionCard(RemovalSession session)
    {
        var resources = Application.Current.Resources;

        var card = new Border
        {
            CornerRadius = (CornerRadius)resources["RadiusCard"],
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
        };

        var outer = new StackPanel();

        var headerRow = new Grid { ColumnSpacing = 12, Margin = new Thickness(18, 13, 18, 13) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var whenText = new TextBlock
        {
            Text = FormatWhen(session.StartedUtc),
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        };
        Grid.SetColumn(whenText, 0);

        var modeAndTarget = new TextBlock
        {
            Text = $"{session.ModeLabel ?? "Removal"} · {session.TargetLabel}",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextFaintBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(modeAndTarget, 1);

        var statsText = new TextBlock
        {
            Text = $"{session.FilesRemoved} file{(session.FilesRemoved == 1 ? "" : "s")} · {FileSizeFormatter.Format(session.BytesRemoved)}", // QA-17: not "1 files"
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["SuccessBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(statsText, 3);

        var toggleText = new TextBlock
        {
            Text = "Expand ▾",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["AccentLightBrush"],
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(toggleText, 4);

        headerRow.Children.Add(whenText);
        headerRow.Children.Add(modeAndTarget);
        headerRow.Children.Add(statsText);
        headerRow.Children.Add(toggleText);

        var entriesPanel = new StackPanel { Visibility = Visibility.Collapsed };
        var loaded = false;

        var headerButton = new Button
        {
            Content = headerRow,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        headerButton.Click += (_, _) =>
        {
            if (!loaded)
            {
                loaded = true;
                foreach (var entry in _viewModel.GetEntriesForSession(session.Id))
                {
                    entriesPanel.Children.Add(BuildEntryRow(resources, entry));
                }
            }

            var expanding = entriesPanel.Visibility != Visibility.Visible;
            entriesPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            toggleText.Text = expanding ? "Collapse ▴" : "Expand ▾";
        };

        outer.Children.Add(headerButton);
        outer.Children.Add(entriesPanel);

        card.Child = outer;
        return card;
    }

    private static UIElement BuildEntryRow(ResourceDictionary resources, TrashLogEntry entry)
    {
        var row = new Grid { ColumnSpacing = 14, Margin = new Thickness(18, 9, 18, 9) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var fileName = System.IO.Path.GetFileName(entry.OriginalPath);
        var directory = System.IO.Path.GetDirectoryName(entry.OriginalPath) ?? entry.OriginalPath;

        var nameStack = new StackPanel();
        nameStack.Children.Add(new TextBlock
        {
            Text = fileName,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = $"was in {directory}\\",
            FontFamily = (FontFamily)resources["MonoFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextDisabledHintBrush"],
        });
        Grid.SetColumn(nameStack, 0);

        var timeText = new TextBlock
        {
            Text = entry.DeletedUtc.ToLocalTime().ToString("h:mm tt"),
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12,
            Foreground = (Brush)resources["TextFaintBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(timeText, 1);

        var sizeText = new TextBlock
        {
            Text = FileSizeFormatter.Format(entry.SizeBytes),
            FontFamily = (FontFamily)resources["MonoFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sizeText, 2);

        row.Children.Add(nameStack);
        row.Children.Add(timeText);
        row.Children.Add(sizeText);

        return new Border
        {
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = row,
        };
    }

    private static string FormatWhen(DateTime startedUtc)
    {
        var local = startedUtc.ToLocalTime();
        var today = DateTime.Now.Date;

        if (local.Date == today)
        {
            return $"Today, {local:h:mm tt}";
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday, {local:h:mm tt}";
        }

        return local.ToString("MMM d, h:mm tt");
    }

    private void BackHome_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();

    private void RunFirstScan_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();

    private async void EmptyRecycleBinButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await EmptyRecycleBinDialog.ShowAsync(XamlRoot, _recycleBinService);
        if (!confirmed)
        {
            return;
        }

        // SHEmptyRecycleBin on a multi-GB bin takes seconds to minutes; run
        // it off the UI thread so the window doesn't go "not responding".
        // Also: this runs in an async void handler - an uncaught COM failure
        // here used to be an app crash.
        EmptyRecycleBinButton.IsEnabled = false;
        try
        {
            await Task.Run(_recycleBinService.EmptyRecycleBin);
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Emptying the Recycle Bin failed: {ex}");
        }
        finally
        {
            EmptyRecycleBinButton.IsEnabled = true;
        }

        Refresh();
    }
}
