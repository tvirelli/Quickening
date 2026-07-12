using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Quickening.App.Formatting;
using Windows.Foundation;
using Windows.UI;

namespace Quickening.App.Views;

/// <summary>
/// coffee/6c - the "buy me a coffee" overlay opened by both the Home footer
/// pill (6a) and the Celebration done-screen line (6b). Ko-fi only, one way
/// out: the CTA hands off to ko-fi.com in the default browser so the payment
/// happens there, never inside the app (an embedded card form in a desktop
/// window reads as less trustworthy, not more). Never auto-opens and carries
/// no guilt copy - "Maybe later" is plain text, the personalised lifetime-GB
/// line is the only nudge.
/// </summary>
internal static class CoffeeDialog
{
    public const string KoFiUrl = "https://ko-fi.com/tvirelli";

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var resources = Application.Current.Resources;

        var dialog = new ContentDialog
        {
            Style = (Style)resources["NebulaContentDialogStyle"],
            Width = 520,
            XamlRoot = xamlRoot,
        };

        var root = new Grid();

        // ✕ close, top-right - a bare Hide() with no consequence, unlike the
        // failure dialogs' typed choices.
        var closeButton = new Button
        {
            Content = "✕",
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Foreground = (Brush)resources["TextMutedBrush"],
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(closeButton, "Close");
        closeButton.Click += (_, _) => dialog.Hide();

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // Warm 84px badge with the 40px coffee cup - the only warm-yellow
        // accents in an otherwise blue app, reserved for this one ask.
        var badge = new Border
        {
            Width = 84,
            Height = 84,
            CornerRadius = new CornerRadius(42),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xD6, 0x66)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xD6, 0x66)),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new ContentControl
            {
                ContentTemplate = (DataTemplate)resources["IconCoffeeLarge"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        content.Children.Add(badge);

        content.Children.Add(new TextBlock
        {
            Text = "Enjoying Quickening?",
            Margin = new Thickness(0, 22, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
        });

        content.Children.Add(BuildBodyText(resources));

        // Ko-fi CTA - warm gradient (#FFD666 -> #F0B429), the one place yellow
        // is a button. GradientPillButtonStyle strips the default WinUI hover
        // glow (same treatment as the app's blue CTAs).
        var ctaGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        ctaGradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0xD6, 0x66), Offset = 0 });
        ctaGradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, 0xF0, 0xB4, 0x29), Offset = 1 });

        var ctaBorder = new Border
        {
            Background = ctaGradient,
            Padding = new Thickness(40, 16, 40, 16),
            Child = new TextBlock
            {
                Text = "Buy me a coffee on Ko-fi",
                FontFamily = (FontFamily)resources["DisplayFontFamily"],
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x2B, 0x08)),
            },
        };
        Controls.PillCornerRadius.SetEnabled(ctaBorder, true);

        var ctaButton = new Button
        {
            Style = (Style)resources["GradientPillButtonStyle"],
            Content = ctaBorder,
            Margin = new Thickness(0, 30, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ctaButton, "Buy me a coffee on Ko-fi");
        ctaButton.Click += async (_, _) =>
        {
            await OpenKoFiAsync();
            dialog.Hide();
        };
        content.Children.Add(ctaButton);

        var maybeLater = new Button
        {
            Content = "Maybe later",
            Margin = new Thickness(0, 22, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = (Brush)resources["TextFaintBrush"],
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        maybeLater.Click += (_, _) => dialog.Hide();
        content.Children.Add(maybeLater);

        root.Children.Add(content);
        root.Children.Add(closeButton);

        dialog.Content = root;
        await dialog.ShowAsync();
    }

    // "…has rescued 42.7 GB of your disk so far" only reads right once the
    // user has actually reclaimed something; a fresh install has 0 lifetime
    // bytes, so drop the figure entirely rather than boast "rescued 0 B".
    private static TextBlock BuildBodyText(ResourceDictionary resources)
    {
        var body = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            MaxWidth = 400,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            Foreground = (Brush)resources["TextMutedBrush"],
        };

        var bytesReclaimed = App.Store?.GetLifetimeStats().BytesReclaimed ?? 0;

        if (bytesReclaimed > 0)
        {
            body.Inlines.Add(new Run { Text = "It's free, built by one person, and has rescued " });
            body.Inlines.Add(new Run
            {
                Text = FileSizeFormatter.Format(bytesReclaimed),
                FontFamily = (FontFamily)resources["DisplayFontFamily"],
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)resources["SuccessBrush"],
            });
            body.Inlines.Add(new Run { Text = " of your disk so far. If it's earned it, a coffee keeps the updates coming. ☕" });
        }
        else
        {
            body.Inlines.Add(new Run { Text = "It's free, and built by one person. If it's saving you time and space, a coffee keeps the updates coming. ☕" });
        }

        return body;
    }

    private static async Task OpenKoFiAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(KoFiUrl));
        }
        catch (Exception ex)
        {
            // Launch can fail if there's no default browser association - not
            // worth crashing a donation prompt over; just log and move on.
            App.Logger?.LogError($"Failed to open Ko-fi URL: {ex}");
        }
    }
}
