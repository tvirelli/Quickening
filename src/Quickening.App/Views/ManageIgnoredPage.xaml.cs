using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Quickening.App.Views;

public sealed partial class ManageIgnoredPage : Page
{
    public ManageIgnoredPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Rebuild();
    }

    private void Rebuild()
    {
        ListPanel.Children.Clear();

        var folders = IgnoreService.IgnoredFolders;
        var files = IgnoreService.IgnoredFiles;

        SummaryText.Text = $"{Count(folders.Count, "folder")} · {Count(files.Count, "file")} ignored";

        if (folders.Count == 0 && files.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = "Nothing is ignored. Right-click a file in review and choose Ignore to add it here.",
                FontFamily = (FontFamily)Application.Current.Resources["BodyFontFamily"],
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
            });
            return;
        }

        if (folders.Count > 0)
        {
            ListPanel.Children.Add(BuildSection("IGNORED FOLDERS", folders, isFolder: true));
        }

        if (files.Count > 0)
        {
            ListPanel.Children.Add(BuildSection("IGNORED FILES", files, isFolder: false));
        }
    }

    private UIElement BuildSection(string label, IReadOnlyList<string> paths, bool isFolder)
    {
        var resources = Application.Current.Resources;
        var section = new StackPanel { Spacing = 9 };

        // Matches FilterSectionLabelStyle (the filter rails' uppercase heading)
        // and HistoryPage's card tokens - this is a management page, so it uses
        // the shared SurfaceCard brushes, not the results pages' lighter trio.
        section.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 10.5,
            FontWeight = FontWeights.ExtraBold,
            CharacterSpacing = 120,
            Foreground = (Brush)resources["TextDisabledHintBrush"],
        });

        var card = new Border
        {
            CornerRadius = (CornerRadius)resources["RadiusCard"],
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
        };
        var rows = new StackPanel();
        for (var i = 0; i < paths.Count; i++)
        {
            rows.Children.Add(BuildRow(paths[i], isFolder, isFirst: i == 0));
        }

        card.Child = rows;
        section.Children.Add(card);
        return section;
    }

    private UIElement BuildRow(string path, bool isFolder, bool isFirst)
    {
        var resources = Application.Current.Resources;

        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(18, 10, 18, 10) };
        grid.BorderBrush = (Brush)resources["SurfaceCardBorderBrush"];
        // Divider ABOVE every row except the first (HistoryPage's pattern) -
        // the old isLast variant doubled the card's own top border on row 0
        // and left the last two rows with no divider between them.
        grid.BorderThickness = new Thickness(0, isFirst ? 0 : 1, 0, 0);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = isFolder ? "\U0001F4C1" : "\U0001F4C4",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new TextBlock
        {
            Text = path,
            FontFamily = (FontFamily)resources["MonoFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // BulkChipButtonStyle's exact recipe (the results pages' chip buttons)
        // rather than a near-miss one-off.
        var remove = new Button
        {
            Content = "Stop ignoring",
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Foreground = (Brush)resources["TextSecondaryBrush"],
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(14, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Controls.PillCornerRadius.SetEnabled(remove, true);
        // Every row's button says "Stop ignoring" - the announced name carries
        // the path so a screen-reader user knows WHICH entry they're acting on.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Stop ignoring {path}");
        remove.Click += (_, _) =>
        {
            if (isFolder)
            {
                IgnoreService.UnignoreFolder(path);
            }
            else
            {
                IgnoreService.UnignoreFile(path);
            }

            Rebuild();
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        return grid;
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private void Back_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();
}
