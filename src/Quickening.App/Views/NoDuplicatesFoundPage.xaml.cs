using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Quickening.App.Views;

public sealed partial class NoDuplicatesFoundPage : Page
{
    private NoDuplicatesFoundParameters? _parameters;

    public NoDuplicatesFoundPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not NoDuplicatesFoundParameters parameters)
        {
            return;
        }

        _parameters = parameters;

        var emphasisBrush = (SolidColorBrush)Application.Current.Resources["TextBodyEmphasisBrush"];
        SubtitleText.Inlines.Clear();
        SubtitleText.Inlines.Add(new Run { Text = "We compared " });
        SubtitleText.Inlines.Add(new Run
        {
            Text = $"{parameters.TotalFilesScanned:N0} files",
            Foreground = emphasisBrush,
            FontWeight = FontWeights.Bold,
        });
        SubtitleText.Inlines.Add(new Run { Text = " in " });
        SubtitleText.Inlines.Add(new Run
        {
            Text = parameters.FolderLabel,
            Foreground = emphasisBrush,
            FontWeight = FontWeights.Bold,
        });
        SubtitleText.Inlines.Add(new Run { Text = " — no two are alike. Nothing to remove, nothing to worry about." });

        HeavyweightHintText.Text = $"Big folders often hide {RandomPhrases.Heavyweights()} even when there are no duplicates.";
    }

    private void ScanAnotherFolder_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();

    // Cross-sells Large Files mode on the SAME folder that was just scanned
    // clean, rather than making the user re-pick it - see
    // HomePage.HomePagePrefillRequest's doc comment. AutoStart is
    // deliberately false here: unlike a rescan/retry, this is switching the
    // user into a mode they haven't chosen yet, so they still press Home's
    // own CTA once they see the threshold they want.
    private void TryLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_parameters is null)
        {
            return;
        }

        ((MainWindow)App.MainWindowInstance!).ShowHome(
            new HomePage.HomePagePrefillRequest(_parameters.FolderPath, LargeFilesMode: true));
    }
}

public sealed record NoDuplicatesFoundParameters(int TotalFilesScanned, string FolderLabel, string FolderPath);
