using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Quickening.App.Views;

public sealed partial class EmptyFolderPage : Page
{
    public EmptyFolderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not EmptyFolderParameters parameters)
        {
            return;
        }

        var emphasisBrush = (SolidColorBrush)Application.Current.Resources["TextBodyEmphasisBrush"];
        SubtitleText.Inlines.Clear();
        SubtitleText.Inlines.Add(new Run { Text = parameters.FolderPath, Foreground = emphasisBrush, FontWeight = FontWeights.Bold });
        SubtitleText.Inlines.Add(new Run { Text = " has no files we can read — it may be empty, or Windows may be keeping us out." });
    }

    // 3c's two actions are functionally identical: the folder that got us
    // here had nothing readable in it, so there's no folder path or scan
    // state worth carrying forward either way - both just return to a
    // clean Home, matching the handoff's own markup (same visual weight
    // difference as every other screen's primary/secondary pair, but no
    // behavioral difference here since there's nothing to prefill).
    private void PickDifferentFolder_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();

    private void BackHome_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();
}

public sealed record EmptyFolderParameters(string FolderPath);
