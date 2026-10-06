using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Quickening.App.Formatting;
using Quickening.Core.Deletion;
using Windows.UI;

namespace Quickening.App.Views;

/// <summary>
/// The one genuinely permanent, unrecoverable action anywhere in this app
/// (new-screens 4f) - emptying the Recycle Bin doesn't just undo a
/// Quickening removal, it erases everything in the bin, including files
/// other programs or the user put there. Deliberately does NOT use a plain
/// "type to confirm" or a normal button click - a 2-second press-and-hold
/// gesture (the button's own fill animates left-to-right as visual
/// feedback) makes an accidental single click impossible to mistake for a
/// confirmation.
/// </summary>
internal static class EmptyRecycleBinDialog
{
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, IRecycleBinService recycleBinService)
    {
        var resources = Application.Current.Resources;
        var confirmed = false;

        var dialog = new ContentDialog
        {
            Style = (Style)resources["NebulaContentDialogStyle"],
            Width = (double)resources["DialogWidthFailure"],
            XamlRoot = xamlRoot,
        };

        var (itemCount, totalSizeBytes) = recycleBinService.GetRecycleBinTotals();

        var root = new StackPanel { Spacing = 0 };
        root.Children.Add(new TextBlock
        {
            Text = "Empty the entire Windows Recycle Bin?",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var bodyText = new TextBlock
        {
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 14.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        };
        bodyText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "This is Windows' bin, not just Quickening's - it will permanently erase " });
        bodyText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "everything", FontWeight = FontWeights.Bold, Foreground = (Brush)resources["TextHeadingBrush"] });
        bodyText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " in it. This is the only action in Quickening that can't be undone." });
        root.Children.Add(bodyText);

        var statRow = new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(18, 14, 18, 14),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
        };
        var statGrid = new Grid();
        statGrid.Children.Add(new TextBlock
        {
            Text = "In the bin right now",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13,
            Foreground = (Brush)resources["TextMutedBrush"],
        });
        statGrid.Children.Add(new TextBlock
        {
            Text = $"{itemCount:N0} file{(itemCount == 1 ? "" : "s")} · {FileSizeFormatter.Format(totalSizeBytes)}",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        statRow.Child = statGrid;
        root.Children.Add(statRow);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };

        var keepButton = new Button
        {
            Content = "Keep it",
            Style = (Style)resources["PillSecondaryButtonStyle"],
        };
        keepButton.Click += (_, _) => dialog.Hide();

        var holdBorder = new Border
        {
            // No padding on the border itself: the progress fill lives edge
            // to edge inside it (the border clips the fill to the pill
            // shape), and the text carries its own margin instead. With
            // padding here the fill only covered the inner content area and
            // left an unfilled ring around the text.
            // CornerRadius=999 bulges to an ellipse on a short element in
            // WinUI; the pill helper pins it to the button's live height/2.
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x8A, 0x5C)),
            BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        Quickening.App.Controls.PillCornerRadius.SetEnabled(holdBorder, true);
        var fill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0x8A, 0x5C)),
            // Stretch to fill the button cell so the ScaleX 0->1 animation
            // grows it from the left edge to full width. Previously this was
            // Left-aligned with its Width set to the parent's ActualWidth from
            // the parent's SizeChanged - which fed the child width back into
            // the parent's measure and produced a layout cycle (LayoutCycle-
            // Exception, crashing the app when the empty-bin dialog opened).
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5),
        };
        var fillScale = new ScaleTransform { ScaleX = 0 };
        fill.RenderTransform = fillScale;
        var holdText = new TextBlock
        {
            Text = "Hold to empty…",
            // The button's inset lives here now (not on holdBorder) so the
            // fill can span the full button behind it.
            Margin = new Thickness(30, 13, 30, 13),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x9A, 0x6C)),
        };
        var holdGrid = new Grid();
        holdGrid.Children.Add(fill);
        holdGrid.Children.Add(holdText);
        holdBorder.Child = holdGrid;

        var animation = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(2) };
        Storyboard.SetTarget(animation, fillScale);
        Storyboard.SetTargetProperty(animation, "ScaleX");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            confirmed = true;
            dialog.Hide();
        };

        holdBorder.PointerPressed += (_, e) =>
        {
            holdBorder.CapturePointer(e.Pointer);
            storyboard.Begin();
        };
        void CancelHold(object? sender, PointerRoutedEventArgs e)
        {
            storyboard.Stop();
            fillScale.ScaleX = 0;
        }
        holdBorder.PointerReleased += CancelHold;
        holdBorder.PointerExited += CancelHold;
        holdBorder.PointerCaptureLost += CancelHold;

        buttonRow.Children.Add(keepButton);
        buttonRow.Children.Add(holdBorder);
        root.Children.Add(buttonRow);

        dialog.Content = root;
        await dialog.ShowAsync();

        return confirmed;
    }
}
