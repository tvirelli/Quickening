using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Deletion;
using Quickening.Core.Orchestration;
using Windows.UI;

namespace Quickening.App.Views;

public sealed partial class CelebrationPage : Page
{
    // "dismiss = gone for the session" (new-screens 4j's own annotation) -
    // a plain static bool rather than a persisted Settings flag, since the
    // mockup is explicit this is a per-session dismissal, not a permanent
    // preference. Resets naturally on every app restart.
    private static bool _reminderBannerDismissedThisSession;

    private CelebrationParameters? _parameters;
    private DispatcherTimer? _undoAutoDismissTimer;

    // Every running storyboard, so OnNavigatedFrom can stop them - six
    // RepeatBehavior.Forever confetti loops otherwise keep the discarded
    // page instance and its animation clocks alive after navigation.
    private readonly List<Storyboard> _activeStoryboards = new();

    // True once the user's Undo actually restored at least one file - the
    // cached Results/LargeFiles pages' pruned state (and this page's own
    // "what's left" references) are stale after that, so "Review what's
    // left" must not present them as current.
    private bool _undoRestoredFiles;

    public CelebrationPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _undoAutoDismissTimer?.Stop();
        foreach (var storyboard in _activeStoryboards)
        {
            storyboard.Stop();
        }

        _activeStoryboards.Clear();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not CelebrationParameters parameters)
        {
            return;
        }

        _parameters = parameters;

        var (bytesReclaimed, _) = App.Store!.GetLifetimeStats();
        HeadlineSizeText.Text = FileSizeFormatter.Format(parameters.BytesReclaimedThisRun);
        CoffeeLineSizeText.Text = FileSizeFormatter.Format(parameters.BytesReclaimedThisRun);
        SubtitleText.Text = parameters.FilesRemovedThisRun == 1
            ? "1 duplicate is waiting in the Recycle Bin, just in case you miss it."
            : $"{parameters.FilesRemovedThisRun} duplicates are waiting in the Recycle Bin, just in case you miss them.";
        LifetimeTotalText.Text = $"{FileSizeFormatter.Format(bytesReclaimed)} reclaimed ⚡";

        PlayEntranceAnimations();
        PlayConfettiLoops();

        if (!_reminderBannerDismissedThisSession)
        {
            ReminderBannerHost.Content = BuildReminderBanner();
            ReminderBannerHost.Visibility = Visibility.Visible;
        }

        if (parameters.RemovedFilePaths is { Count: > 0 } && parameters.RecycleBinService is not null)
        {
            ShowUndoToast(parameters.RemovedFilePaths, parameters.RecycleBinService);
        }
    }

    // qk-pop (DESIGN-SPEC.md §4): scale 0.6 -> 1.06 -> 1 with a spring-like
    // overshoot, approximated here with a BackEase (matches the overshoot
    // feel of cubic-bezier(0.2, 1.4, 0.4, 1) closely enough without needing
    // an exact keyframe reproduction of the curve).
    private void PlayEntranceAnimations()
    {
        var storyboard = new Storyboard();

        AddPopAnimation(storyboard, BoltScale, "ScaleX");
        AddPopAnimation(storyboard, BoltScale, "ScaleY");
        AddPopAnimation(storyboard, HeadlineScale, "ScaleX");
        AddPopAnimation(storyboard, HeadlineScale, "ScaleY");

        _activeStoryboards.Add(storyboard);
        storyboard.Begin();
    }

    private static void AddPopAnimation(Storyboard storyboard, ScaleTransform target, string property)
    {
        var animation = new DoubleAnimation
        {
            From = 0.6,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromSeconds(0.5)),
            EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    // qk-float (DESIGN-SPEC.md §4): translateY 0 -> -14 -> 0, 2.6-3.6s
    // ease-in-out infinite, staggered per dot per screen 2l's own per-dot
    // timing so the six dots don't all bob in lockstep.
    private void PlayConfettiLoops()
    {
        PlayFloat(Confetti1Transform, 3.0);
        PlayFloat(Confetti2Transform, 3.6);
        PlayFloat(Confetti3Transform, 2.8);
        PlayFloat(Confetti4Transform, 3.2);
        PlayFloat(Confetti5Transform, 3.4);
        PlayFloat(Confetti6Transform, 2.6);
    }

    private void PlayFloat(TranslateTransform target, double seconds)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = -14,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };

        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Y");
        storyboard.Children.Add(animation);
        _activeStoryboards.Add(storyboard);
        storyboard.Begin();
    }

    // Info-blue tint (not warning) per 4j's own annotation - this is a
    // gentle reminder, not something gone wrong. "See History" and the ✕
    // both permanently hide it for the rest of THIS app session (see
    // _reminderBannerDismissedThisSession).
    private UIElement BuildReminderBanner()
    {
        var resources = Application.Current.Resources;

        var banner = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 13, 20, 13),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0x7F, 0xAD, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x7F, 0xAD, 0xFF)),
            BorderThickness = new Thickness(1),
        };

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var messageText = new TextBlock
        {
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)resources["TextSecondaryBrush"],
        };
        messageText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Heads-up: those files are in the " });
        messageText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = "Recycle Bin",
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
        });
        messageText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " — the space isn't truly free until it's emptied. " });

        var seeHistoryRun = new Microsoft.UI.Xaml.Documents.Hyperlink
        {
            Foreground = (Brush)resources["AccentLightBrush"],
        };
        seeHistoryRun.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "See History", FontWeight = FontWeights.Bold });
        seeHistoryRun.Click += (_, _) =>
        {
            _reminderBannerDismissedThisSession = true;
            ((MainWindow)App.MainWindowInstance!).ShowHistory();
        };
        messageText.Inlines.Add(seeHistoryRun);

        Grid.SetColumn(messageText, 0);

        var dismissButton = new Button
        {
            Content = "✕",
            Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Foreground = (Brush)resources["TextFaintBrush"],
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(dismissButton, "Dismiss reminder");
        dismissButton.Click += (_, _) =>
        {
            _reminderBannerDismissedThisSession = true;
            ReminderBannerHost.Visibility = Visibility.Collapsed;
        };
        Grid.SetColumn(dismissButton, 1);

        grid.Children.Add(messageText);
        grid.Children.Add(dismissButton);
        banner.Child = grid;
        return banner;
    }

    // Undo toast (new-screens 4j) - "184 files moved to the Recycle Bin —
    // 9.1 GB back" with an Undo action and a 15-second auto-dismiss. The
    // mockup's own countdown ring visual is not reproduced here (a plain
    // timer drives the same 15-second dismiss without a hand-drawn arc) -
    // never blocks anything else on the page either way, it's a floating
    // overlay, not a dialog.
    private void ShowUndoToast(IReadOnlyList<string> removedFilePaths, IRecycleBinService recycleBinService)
    {
        var resources = Application.Current.Resources;

        var root = new StackPanel { Spacing = 0 };

        var pill = new Border
        {
            // CornerRadius=999 bulges to an ellipse on a short element in
            // WinUI (no CSS-style clamp to height/2); the shared pill helper
            // (set below) pins the radius to the toast's live height/2.
            // #151D3E - the toast pill's own surface color per new-screens
            // 4j, distinct from (darker than) SurfaceCardBrush's translucent
            // overlay tint - a toast needs to read as fully opaque over
            // whatever content is behind it, not blend with it.
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x15, 0x1D, 0x3E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 15, 20, 15),
        };
        Quickening.App.Controls.PillCornerRadius.SetEnabled(pill, true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };

        var checkBadge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x5E, 0xE7, 0xB7)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x5E, 0xE7, 0xB7)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)resources["SuccessBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(checkBadge);

        var messageText = new TextBlock
        {
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)resources["TextHeadingBrush"],
        };
        messageText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = removedFilePaths.Count == 1
                ? "1 file moved to the Recycle Bin — "
                : $"{removedFilePaths.Count} files moved to the Recycle Bin — ",
        });
        messageText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = $"{FileSizeFormatter.Format(_parameters!.BytesReclaimedThisRun)} back",
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["SuccessBrush"],
        });
        row.Children.Add(messageText);

        var undoButton = new Button
        {
            Content = "↩ Undo",
            Style = (Style)resources["PillSmallGradientButtonStyle"],
        };
        ToolTipService.SetToolTip(undoButton, "Restores to original locations — works as long as the Recycle Bin hasn't been emptied.");
        undoButton.Click += async (_, _) =>
        {
            _undoAutoDismissTimer?.Stop();
            undoButton.IsEnabled = false;

            var restoredPaths = await Task.Run(() => removedFilePaths.Where(recycleBinService.TryRestore).ToList());
            var restoredCount = restoredPaths.Count;

            // Roll the bookkeeping back for what actually came back:
            // otherwise History still lists the restored files as removed and
            // the lifetime "reclaimed" stat keeps counting bytes that are
            // back on disk.
            if (restoredCount > 0)
            {
                _undoRestoredFiles = true;
                try
                {
                    foreach (var restoredPath in restoredPaths)
                    {
                        App.Store?.UndoTrashedFile(restoredPath);
                    }

                    var (bytesReclaimed, _) = App.Store!.GetLifetimeStats();
                    LifetimeTotalText.Text = $"{FileSizeFormatter.Format(bytesReclaimed)} reclaimed ⚡";
                }
                catch (Exception ex)
                {
                    App.Logger?.LogError($"Failed to roll back trash-log entries after Undo: {ex}");
                }

                ShowUndoneState(restoredCount, removedFilePaths.Count);
            }

            messageText.Inlines.Clear();
            messageText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = restoredCount == removedFilePaths.Count
                    ? "Restored to where they came from."
                    : restoredCount == 0
                        ? "Couldn't restore those files — the Recycle Bin may have changed."
                        : $"Restored {restoredCount} of {removedFilePaths.Count} files.",
            });
            row.Children.Remove(undoButton);

            _ = HideUndoToastAfterDelayAsync();
        };
        row.Children.Add(undoButton);

        pill.Child = row;
        root.Children.Add(pill);

        UndoToastHost.Content = root;
        UndoToastHost.Visibility = Visibility.Visible;

        // 15 s (was 8): long enough to read the celebration and still reach
        // Undo - 8 s routinely expired before a user got to it.
        _undoAutoDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _undoAutoDismissTimer.Tick += (_, _) =>
        {
            _undoAutoDismissTimer!.Stop();
            UndoToastHost.Visibility = Visibility.Collapsed;
        };
        _undoAutoDismissTimer.Start();
    }

    // After a successful Undo the page was still celebrating "128 KB back! 2
    // duplicates are waiting in the Recycle Bin" (QA-12), and "Review what's
    // left" silently landed on Home. Say what actually happened, drop the
    // now-wrong bin reminder and coffee line, label the button for where it
    // goes, and refresh the title-bar bin pill right away instead of on its
    // next timer tick.
    private void ShowUndoneState(int restoredCount, int removedCount)
    {
        var allBack = restoredCount == removedCount;
        HeadlineTextBlock.Inlines.Clear();
        HeadlineTextBlock.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = allBack ? "Undone — nothing removed." : "Partly undone.",
        });
        SubtitleText.Text = allBack
            ? restoredCount == 1 ? "The file is back where it was." : $"All {restoredCount} files are back where they were."
            : $"{restoredCount} of {removedCount} files are back where they were; the rest are still in the Recycle Bin.";

        if (allBack)
        {
            ReminderBannerHost.Visibility = Visibility.Collapsed;
            CoffeeLineButton.Visibility = Visibility.Collapsed;
        }

        // ReviewWhatsLeft_Click goes Home after an Undo (the cached results
        // are stale), so the button now says so.
        ReviewWhatsLeftButton.Content = "Start a fresh scan";
        (App.MainWindowInstance as MainWindow)?.RefreshRecycleBinPill();
    }

    private async Task HideUndoToastAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        UndoToastHost.Visibility = Visibility.Collapsed;
    }

    private void ScanAnotherFolder_Click(object sender, RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHome();

    // coffee/6b -> 6c overlay (the line never opens the browser directly).
    private async void CoffeeLine_Click(object sender, RoutedEventArgs e) =>
        await CoffeeDialog.ShowAsync(XamlRoot);

    // Celebration is reached from both scan-type modes (ResultsPage's
    // grouped removal flow and LargeFilesResultsPage's flat one - see both
    // pages' RemoveSelectedFilesAsync), so "Review what's left" needs to
    // return to whichever one the user actually came from, not always
    // Results. RemainingLargeFiles being non-null is the signal that this
    // celebration came from Large Files mode - it's the only field the
    // Large Files removal flow populates and the Duplicates one doesn't.
    private void ReviewWhatsLeft_Click(object sender, RoutedEventArgs e)
    {
        // After a successful Undo, the cached results pages' pruned state no
        // longer matches the disk (the restored files would be missing from
        // the list even though they're back) - go Home for a fresh scan
        // rather than presenting a stale list as current.
        if (_undoRestoredFiles)
        {
            ((MainWindow)App.MainWindowInstance!).ShowHome();
            return;
        }

        if (_parameters?.RemainingLargeFiles is { } remainingLargeFiles)
        {
            ((MainWindow)App.MainWindowInstance!).ShowLargeFilesResults(
                remainingLargeFiles, _parameters.LargeFilesThresholdBytes ?? 0, _parameters.RemainingResult, null);
        }
        else if (_parameters?.RemainingResult is { } result)
        {
            ((MainWindow)App.MainWindowInstance!).ShowResults(result);
        }
        else
        {
            ((MainWindow)App.MainWindowInstance!).ShowHome();
        }
    }
}

/// <summary>
/// Navigation parameter for CelebrationPage - the size/count actually
/// removed THIS run (for the headline/subtitle) plus enough of the
/// "what's left" state for the Review-what's-left action to return to the
/// right screen. RemainingResult is set by BOTH removal flows (it's the
/// scan's own ScanResult, reused either way) - it is NOT how the two modes
/// are told apart. The actual disambiguator is RemainingLargeFiles:
/// LargeFilesResultsPage.RemoveSelectedFilesAsync sets RemainingResult AND
/// RemainingLargeFiles/LargeFilesThresholdBytes together, while
/// ResultsPage.RemoveSelectedFilesAsync (Duplicates mode) sets only
/// RemainingResult. ReviewWhatsLeft_Click checks RemainingLargeFiles first
/// for exactly this reason - do not add logic elsewhere that assumes
/// RemainingResult being non-null implies Duplicates mode. All
/// optional/null-falls-back-to-Home rather than crashing.
/// </summary>
public sealed record CelebrationParameters(
    long BytesReclaimedThisRun,
    int FilesRemovedThisRun,
    ScanResult? RemainingResult,
    IReadOnlyList<SelectableFile>? RemainingLargeFiles = null,
    long? LargeFilesThresholdBytes = null,
    // The exact paths removed THIS run and the same IRecycleBinService
    // instance that removed them (see RecycleBinService.TryRestore's own
    // doc comment on why it must be the SAME instance - the captured
    // Recycle Bin locations are held in that instance's memory, not
    // persisted anywhere) - both null skips the undo toast entirely (e.g.
    // if a future caller reaches Celebration some other way).
    IReadOnlyList<string>? RemovedFilePaths = null,
    IRecycleBinService? RecycleBinService = null);
