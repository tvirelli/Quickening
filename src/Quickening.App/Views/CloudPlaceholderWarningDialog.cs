using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quickening.App.Formatting;
using Windows.UI;

namespace Quickening.App.Views;

public enum CloudPlaceholderChoice
{
    SkipOnlineOnly,
    DownloadAndInclude,
    CancelScan,
}

/// <summary>
/// new-screens 4m - shown before a scan starts if FileEnumerator's
/// PreviewCloudPlaceholders pre-pass (see HomePage.CheckCloudPlaceholdersAsync)
/// finds any OneDrive/Dropbox/Google Drive "online-only" placeholder files
/// in the target folder. Choice cards rather than plain dialog buttons, per
/// 4m's own design note: two real options plus a separate Cancel, with the
/// recommended one visually distinct via an accent border - archive-blue,
/// not warning orange, since nothing here is actually at risk.
/// </summary>
internal static class CloudPlaceholderWarningDialog
{
    public static async Task<CloudPlaceholderChoice> ShowAsync(XamlRoot xamlRoot, int placeholderCount, long placeholderBytes)
    {
        var resources = Application.Current.Resources;
        var result = CloudPlaceholderChoice.CancelScan;

        var dialog = new ContentDialog
        {
            Style = (Style)resources["NebulaContentDialogStyle"],
            Width = 560,
            CloseButtonText = "Cancel scan",
            CloseButtonStyle = (Style)resources["PillSecondaryButtonStyle"],
            XamlRoot = xamlRoot,
        };

        var root = new StackPanel { Spacing = 0 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        titleRow.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0x40, 0xC4, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x40, 0xC4, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "☁",
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "Some of this lives in the cloud",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(titleRow);

        var isSingle = placeholderCount == 1;
        var countLabel = isSingle ? "1 file" : $"{placeholderCount:N0} files";
        var verb = isSingle ? "is" : "are";
        var sizeLabel = FileSizeFormatter.Format(placeholderBytes);
        root.Children.Add(new TextBlock
        {
            Text = $"{countLabel} in this folder {verb} online-only placeholders. Checking them for duplicates "
                + $"means downloading them first — about {sizeLabel}.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 14.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });

        var cardStack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 26, 0, 0) };

        var skipButton = BuildChoiceCard(resources, "Skip online-only files", "— scan what's already on this PC", recommended: true);
        var downloadButton = BuildChoiceCard(resources, "Download & include them", $"— slower, uses ~{sizeLabel} of bandwidth", recommended: false);

        skipButton.Click += (_, _) =>
        {
            result = CloudPlaceholderChoice.SkipOnlineOnly;
            dialog.Hide();
        };
        downloadButton.Click += (_, _) =>
        {
            result = CloudPlaceholderChoice.DownloadAndInclude;
            dialog.Hide();
        };

        cardStack.Children.Add(skipButton);
        cardStack.Children.Add(downloadButton);
        root.Children.Add(cardStack);

        dialog.Content = root;

        await dialog.ShowAsync();
        return result;
    }

    private static Button BuildChoiceCard(ResourceDictionary resources, string title, string subtitle, bool recommended)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var textStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = (Brush)resources["TextHeadingBrush"],
        });
        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)resources["TextMutedBrush"],
        });
        Grid.SetColumn(textStack, 0);
        row.Children.Add(textStack);

        if (recommended)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var badge = new TextBlock
            {
                Text = "RECOMMENDED",
                FontSize = 11,
                FontWeight = FontWeights.ExtraBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7F, 0xAD, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);
        }

        var button = new Button
        {
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(22, 15, 22, 15),
            BorderThickness = new Thickness(recommended ? 1.5 : 1),
            BorderBrush = recommended
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x6B, 0xFF))
                : (Brush)resources["SurfaceCardBorderBrush"],
            Background = recommended
                ? new SolidColorBrush(Color.FromArgb(0x17, 0x2E, 0x6B, 0xFF))
                : new SolidColorBrush(Colors.Transparent),
        };
        button.SetValue(Controls.PillCornerRadius.EnabledProperty, true);
        return button;
    }
}
