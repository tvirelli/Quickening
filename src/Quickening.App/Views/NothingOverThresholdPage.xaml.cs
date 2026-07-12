using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Quickening.App.Formatting;
using Windows.UI;

namespace Quickening.App.Views;

public sealed partial class NothingOverThresholdPage : Page
{
    private NothingOverThresholdParameters? _parameters;
    private long[] _quickPickBytes = Array.Empty<long>();
    private long _selectedQuickPickBytes;

    public NothingOverThresholdPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not NothingOverThresholdParameters parameters)
        {
            return;
        }

        _parameters = parameters;

        NoHeavyweightsHeadline.Text = $"No {RandomPhrases.Heavyweights()} up here.";

        var thresholdLabel = FileSizeFormatter.Format(parameters.ThresholdBytesUsed);
        var emphasisBrush = (SolidColorBrush)Application.Current.Resources["TextBodyEmphasisBrush"];

        SubtitleText.Inlines.Clear();
        SubtitleText.Inlines.Add(new Run { Text = "Nothing in " });
        SubtitleText.Inlines.Add(new Run { Text = parameters.FolderLabel, Foreground = emphasisBrush, FontWeight = FontWeights.Bold });
        SubtitleText.Inlines.Add(new Run { Text = " weighs " });
        SubtitleText.Inlines.Add(new Run { Text = $"{thresholdLabel} or more", Foreground = emphasisBrush, FontWeight = FontWeights.Bold });
        SubtitleText.Inlines.Add(new Run { Text = ". The big stuff might just be a little smaller." });

        _quickPickBytes = ComputeLowerQuickPicks(parameters.ThresholdBytesUsed);

        var chipButtons = new[] { Chip1Button, Chip2Button, Chip3Button };
        for (var i = 0; i < chipButtons.Length; i++)
        {
            if (i < _quickPickBytes.Length)
            {
                chipButtons[i].Visibility = Visibility.Visible;
                chipButtons[i].Content = FileSizeFormatter.Format(_quickPickBytes[i]);
                chipButtons[i].Tag = _quickPickBytes[i];
            }
            else
            {
                chipButtons[i].Visibility = Visibility.Collapsed;
                chipButtons[i].Tag = null;
            }
        }

        var hasQuickPicks = _quickPickBytes.Length > 0;
        QuickPicksRow.Visibility = hasQuickPicks ? Visibility.Visible : Visibility.Collapsed;
        RescanButton.Visibility = hasQuickPicks ? Visibility.Visible : Visibility.Collapsed;

        if (hasQuickPicks)
        {
            SelectQuickPick(_quickPickBytes[0]);
        }
    }

    // HomePage's threshold slider snaps to a fixed set of stops
    // (HomePage.ThresholdTicks); the "rescan lower" quick picks are simply
    // the stops below the one this scan used, largest first (up to three, per
    // 3b's descending example ordering).
    private static long[] ComputeLowerQuickPicks(long thresholdBytesUsed) =>
        HomePage.ThresholdTicksBelow(thresholdBytesUsed).Take(3).ToArray();

    private void ThresholdChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: long bytes })
        {
            SelectQuickPick(bytes);
        }
    }

    // Recolors the three chips (selected = AccentSelectedFillBrush/Border,
    // matching 3b's "500 MB" chip; unselected = plain outline, matching its
    // "250 MB"/"100 MB" chips) and updates the rescan CTA's text/target to
    // whichever chip is now picked - same direct-code-driven recolor pattern
    // HomePage.UpdateModeVisuals already uses for its segmented pill.
    private void SelectQuickPick(long bytes)
    {
        _selectedQuickPickBytes = bytes;

        var resources = Application.Current.Resources;
        var selectedBackground = (Brush)resources["AccentSelectedFillBrush"];
        var selectedBorder = (Brush)resources["AccentSelectedFillBorderBrush"];
        var selectedForeground = (Brush)resources["TextBodyEmphasisBrush"];
        var unselectedBackground = new SolidColorBrush(Colors.Transparent);
        var unselectedBorder = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        var unselectedForeground = (Brush)resources["TextSecondaryBrush"];

        foreach (var button in new[] { Chip1Button, Chip2Button, Chip3Button })
        {
            if (button.Tag is not long tagBytes)
            {
                continue;
            }

            var isSelected = tagBytes == bytes;
            button.Background = isSelected ? selectedBackground : unselectedBackground;
            button.BorderBrush = isSelected ? selectedBorder : unselectedBorder;
            button.BorderThickness = new Thickness(1.5);
            button.Foreground = isSelected ? selectedForeground : unselectedForeground;
        }

        RescanButtonText.Text = $"⚡ Rescan at {FileSizeFormatter.Format(bytes)}";
    }

    // Re-runs the Large Files scan immediately at the picked threshold via
    // HomePage's own StartLargeFilesScan, reached through
    // HomePage.HomePagePrefillRequest's AutoStart - see that record's doc
    // comment for why this doesn't duplicate ScanOrchestrator wiring here.
    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_parameters is null || _quickPickBytes.Length == 0)
        {
            return;
        }

        ((MainWindow)App.MainWindowInstance!).ShowHome(new HomePage.HomePagePrefillRequest(
            _parameters.FolderPath,
            LargeFilesMode: true,
            AutoStart: true,
            ThresholdBytes: _selectedQuickPickBytes));
    }

    private void BackHome_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();
}

public sealed record NothingOverThresholdParameters(string FolderLabel, string FolderPath, long ThresholdBytesUsed);
