using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Quickening.App.Views;

public sealed partial class ScanFailedPage : Page
{
    private ScanFailedParameters? _parameters;
    private string _errorDetailsText = string.Empty;

    public ScanFailedPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not ScanFailedParameters parameters)
        {
            return;
        }

        _parameters = parameters;

        var emphasisBrush = (SolidColorBrush)Application.Current.Resources["TextBodyEmphasisBrush"];
        SubtitleText.Inlines.Clear();
        SubtitleText.Inlines.Add(new Run { Text = "The scan of " });
        SubtitleText.Inlines.Add(new Run { Text = parameters.FolderLabel, Foreground = emphasisBrush, FontWeight = FontWeights.Bold });
        SubtitleText.Inlines.Add(new Run { Text = " hit an unexpected snag and had to stop." });
        SubtitleText.Inlines.Add(new LineBreak());
        SubtitleText.Inlines.Add(new Run { Text = "Your files are exactly as they were — nothing was moved or changed." });

        // The REAL exception's HResult/Message - never a fabricated Windows
        // error code, per the plan's explicit instruction and DESIGN-SPEC.md
        // §7's "errors stay warm... error code copyable, never a stack trace".
        _errorDetailsText = $"error 0x{parameters.Error.HResult:X8} — {parameters.Error.Message}";
        ErrorCodeText.Text = _errorDetailsText;
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(_errorDetailsText);
        Clipboard.SetContent(dataPackage);
    }

    // Re-runs the exact same scan that just failed (same folder, same mode,
    // same threshold if it was a Large Files scan) via HomePage's own
    // StartDuplicatesScan/StartLargeFilesScan, reached through
    // HomePage.HomePagePrefillRequest's AutoStart - see that record's doc
    // comment for why this doesn't duplicate ScanOrchestrator wiring here.
    private void TryAgain_Click(object sender, RoutedEventArgs e)
    {
        if (_parameters is null)
        {
            return;
        }

        ((MainWindow)App.MainWindowInstance!).ShowHome(new HomePage.HomePagePrefillRequest(
            _parameters.FolderPath,
            LargeFilesMode: _parameters.IsLargeFilesMode,
            AutoStart: true,
            ThresholdBytes: _parameters.ThresholdBytes));
    }

    private void BackHome_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();
}

public sealed record ScanFailedParameters(string FolderLabel, string FolderPath, bool IsLargeFilesMode, long? ThresholdBytes, Exception Error);
