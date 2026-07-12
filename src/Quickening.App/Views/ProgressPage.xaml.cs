using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Navigation;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;

namespace Quickening.App.Views;

public sealed partial class ProgressPage : Page
{
    private ProgressPageParameters? _parameters;
    private CancellationTokenSource? _cancellationTokenSource;

    // True once the frame has navigated away from this page - the
    // cancellation that OnNavigatedFrom fires must NOT then have the
    // OperationCanceledException handler navigate again (it would stomp
    // whatever destination the user just went to).
    private bool _navigatedAway;

    // Set once in OnNavigatedTo from parameters.RingHue and read by
    // OnProgress to decide which RingCenterPanel child is active and what
    // its text should say - Success hue means removal (2k's "131/184"
    // count-style center), Accent hue means a scan (2d's "64%" style
    // center) once the total is known.
    private bool _isRemoval;

    public ProgressPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not ProgressPageParameters parameters)
        {
            return;
        }

        _parameters = parameters;
        _isRemoval = parameters.RingHue == ProgressRingHue.Success;

        HeadlineText.Text = parameters.Headline;
        // 2c's exact subtitle needs the target folder's name, bolded -
        // reproduced verbatim when the caller supplied one via TargetLabel
        // (both of HomePage's scan-kickoff methods do); removal has no
        // per-target copy in the design, so it keeps its own generic
        // wording either way.
        SubtitleText.Inlines.Clear();
        if (_isRemoval)
        {
            SubtitleText.Inlines.Add(new Run { Text = "Sending duplicates to the Recycle Bin - nothing is deleted, just moved." });
        }
        else if (parameters.TargetLabel is { } targetLabel)
        {
            SubtitleText.Inlines.Add(new Run { Text = "Counting every file in " });
            SubtitleText.Inlines.Add(new Run
            {
                Text = targetLabel,
                FontWeight = FontWeights.Bold,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextBodyEmphasisBrush"],
            });
            SubtitleText.Inlines.Add(new Run { Text = " before we start comparing." });
        }
        else
        {
            SubtitleText.Inlines.Add(new Run { Text = "Finding every file so we can compare them for duplicates." });
        }
        StopHintText.Text = _isRemoval
            ? "Stopping keeps what's already been moved - you'll see the updated list right after."
            : "Stopping a scan just takes you back home - nothing has been touched.";

        Ring.RingBrush = parameters.RingHue == ProgressRingHue.Success
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SuccessBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBrush"];
        Ring.SetIndeterminate(true);
        ShowIndeterminateCenter();
        SetAmbientGlowHue(_isRemoval);

        // Start hidden so the pill never flashes empty before the first
        // progress report names a file (OnProgress reveals it for scans;
        // removal keeps it hidden for good - see OnProgress).
        PathChipBorder.Visibility = Visibility.Collapsed;

        using var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        var progress = new Progress<ProgressUpdate>(OnProgress);

        try
        {
            var result = await parameters.Operation(progress, cancellationTokenSource.Token);
            if (!_navigatedAway)
            {
                await parameters.Completed(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (_navigatedAway)
            {
                // Cancelled BY navigation - the user is already where they
                // asked to go; navigating anywhere now would yank them away.
            }
            else if (parameters.OnCancelled is { } onCancelled)
            {
                onCancelled();
            }
            else
            {
                ((MainWindow)App.MainWindowInstance!).ShowHome();
            }
        }
        catch (Exception ex)
        {
            App.Logger?.LogError("Progress operation failed", ex);

            if (_navigatedAway)
            {
                // Same rule as cancellation: the page is gone, don't fight
                // the user's navigation. The failure is already logged.
            }
            else if (parameters.OnError is { } onError)
            {
                await onError(ex);
            }
            else
            {
                try
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Something went wrong",
                        Content = "The operation couldn't complete. Please try again.",
                        CloseButtonText = "OK",
                        XamlRoot = XamlRoot,
                    };
                    await errorDialog.ShowAsync();
                }
                catch (Exception dialogEx)
                {
                    App.Logger?.LogError("Failed to show the operation-failed dialog", dialogEx);
                }

                ((MainWindow)App.MainWindowInstance!).ShowHome();
            }
        }
    }

    // Last stage headline applied, so we only rewrite the headline/subtitle
    // TextBlocks on an actual phase transition rather than every tick.
    private string? _currentStageHeadline;

    private void OnProgress(ProgressUpdate update)
    {
        if (update.StageHeadline is { } stageHeadline && stageHeadline != _currentStageHeadline)
        {
            _currentStageHeadline = stageHeadline;
            HeadlineText.Text = stageHeadline;
            SubtitleText.Inlines.Clear();
            if (!string.IsNullOrEmpty(update.StageSubtitle))
            {
                SubtitleText.Inlines.Add(new Run { Text = update.StageSubtitle });
            }
        }

        // Hide the path chip entirely when there's no file to name (the
        // "Almost done…" finalizing phase, and the initial "sizing up"
        // moment) - an empty pill with just the green dot reads as broken.
        //
        // Removal suppresses it outright: each file goes to the Recycle Bin
        // via a single fast shell call, so the per-file path either flashes
        // by too fast to read or (for a small selection) never paints a
        // frame, leaving an empty-looking pill. The "131/184 files moved"
        // count center already conveys live removal progress; the path chip
        // stays for scans, where it streams visibly over thousands of files.
        var hasPath = !_isRemoval && !string.IsNullOrEmpty(update.CurrentItemLabel);
        PathChipBorder.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        PathChipText.Text = hasPath ? update.CurrentItemLabel! : "";

        if (update.TotalItems is { } total && total > 0)
        {
            Ring.SetIndeterminate(false);
            var percentage = 100.0 * update.ItemsProcessed / total;
            Ring.SetPercentage(percentage);

            // Swap RingCenterPanel to the big-percentage-number layout (2d)
            // vs. the big-count layout (2k) depending on which screen this
            // operation corresponds to - both read from the same
            // ProgressUpdate, the difference is purely which center-content
            // panel is active, decided by _isRemoval (set once in
            // OnNavigatedTo from parameters.RingHue).
            if (_isRemoval)
            {
                ShowCountCenter(update.ItemsProcessed, total);
            }
            else
            {
                ShowPercentCenter(percentage, update.ItemsProcessed, total);
            }
        }
        else
        {
            Ring.SetIndeterminate(true);
            ShowIndeterminateCenter();
        }

        UpdateFindsPill(update);
    }

    // 2d's live "found so far" pill - only meaningful for a scan (not
    // removal) once at least one duplicate group has actually been
    // confirmed; ScanOrchestrator reports DuplicateGroupsFoundSoFar as 0
    // from the very start of the comparing phase; this pill stays hidden
    // until that first real group lands rather than flashing "0 found".
    private void UpdateFindsPill(ProgressUpdate update)
    {
        if (_isRemoval || update.DuplicateGroupsFoundSoFar is not { } groupsFound || groupsFound <= 0)
        {
            FindsPillPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var reclaimableBytes = update.ReclaimableBytesSoFar ?? 0;
        FindsPillText.Text = groupsFound == 1
            ? $"⚡ 1 duplicate found so far — {FileSizeFormatter.Format(reclaimableBytes)} reclaimable"
            : $"⚡ {groupsFound:N0} duplicates found so far — {FileSizeFormatter.Format(reclaimableBytes)} reclaimable";
        FindsPillPanel.Visibility = Visibility.Visible;
    }

    private void ShowIndeterminateCenter()
    {
        IndeterminateCenterPanel.Visibility = Visibility.Visible;
        PercentCenterPanel.Visibility = Visibility.Collapsed;
        CountCenterPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowPercentCenter(double percentage, int processed, int total)
    {
        IndeterminateCenterPanel.Visibility = Visibility.Collapsed;
        PercentCenterPanel.Visibility = Visibility.Visible;
        CountCenterPanel.Visibility = Visibility.Collapsed;

        PercentValueRun.Text = ((int)Math.Round(percentage)).ToString();
        PercentSubText.Text = $"{processed:N0} of {total:N0}";
    }

    private void ShowCountCenter(int processed, int total)
    {
        IndeterminateCenterPanel.Visibility = Visibility.Collapsed;
        PercentCenterPanel.Visibility = Visibility.Collapsed;
        CountCenterPanel.Visibility = Visibility.Visible;

        CountValueRun.Text = processed.ToString("N0");
        CountTotalRun.Text = $"/{total:N0}";
    }

    // Ambient glow behind the ring is blue at ~0.14 alpha for scanning
    // (2c/2d) and green at ~0.10 alpha for removal (2k) - see
    // design-handoff/Quickening Screens.dc.html's two radial-gradient
    // values for those screens.
    private void SetAmbientGlowHue(bool isRemoval)
    {
        if (isRemoval)
        {
            AmbientGlowInnerStop.Color = Windows.UI.Color.FromArgb(0x1A, 0x5E, 0xE7, 0xB7);
            AmbientGlowOuterStop.Color = Windows.UI.Color.FromArgb(0x00, 0x5E, 0xE7, 0xB7);
        }
        else
        {
            AmbientGlowInnerStop.Color = Windows.UI.Color.FromArgb(0x24, 0x2E, 0x6B, 0xFF);
            AmbientGlowOuterStop.Color = Windows.UI.Color.FromArgb(0x00, 0x2E, 0x6B, 0xFF);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Anything that navigates the frame away mid-operation (tray menu
    // items, watcher/scheduled-scan toast actions) previously left the
    // operation running as an orphan: no Stop button anymore, disk still
    // grinding, and its Completed callback would eventually force-navigate
    // the user away from wherever they'd gone. Leaving this page IS
    // abandoning the operation, so cancel it.
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _navigatedAway = true;
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
