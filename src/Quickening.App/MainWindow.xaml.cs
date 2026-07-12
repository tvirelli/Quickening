using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Quickening.App.ViewModels;
using Quickening.App.Views;
using Quickening.Core.Orchestration;
using Windows.UI;

namespace Quickening.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // ExtendsContentIntoTitleBar suppresses the drawn system title bar,
        // but Title still drives the taskbar tooltip/Alt-Tab/Task Manager
        // entry - without setting it explicitly there's no manifest
        // DisplayName fallback either (this app is unpackaged), so it would
        // otherwise show up blank.
        Title = "Quickening";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarRegion);
        ConfigureTitleBarButtons();

        var appWindow = AppWindow;
        if (appWindow is not null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

            // Explicit - a custom-titlebar (ExtendsContentIntoTitleBar) WinUI3
            // window doesn't reliably inherit the exe's embedded icon resource
            // for its own taskbar/Alt-Tab/system-menu icon, even though
            // ApplicationIcon is set in the csproj. AppIcon.ico ships as
            // Content (see the csproj), so it's a real file next to the exe
            // at runtime, not just a build-time-only resource.
            appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

            // Hide-to-tray (Phase 6) - only intercepts the close when
            // watching is actually on; a normal install that never enables
            // it keeps today's plain close-to-exit behavior unchanged.
            // App.IsQuitting is the one escape hatch, set by the tray menu's
            // "Quit Quickening" (the only place a user can exit once
            // watching is on - tray-menus 5a's own note: Quit lives only
            // in the right-click menu, never the left-click flyout).
            appWindow.Closing += (_, args) =>
            {
                if (App.Settings.WatchForNewDuplicates && !App.IsQuitting)
                {
                    args.Cancel = true;
                    H.NotifyIcon.WindowExtensions.Hide(this);
                }
            };
        }
        else
        {
            App.Logger?.LogWarning("AppWindow was null; skipped resizing to 1280x860");
        }

        // Deferred rather than called directly here - HomePage.OnNavigatedTo
        // dereferences App.MainWindowInstance, which is only assigned AFTER
        // this constructor returns (in App.xaml.cs's OnLaunched). Calling
        // ShowHome() here directly would crash with a NullReferenceException
        // on every launch.
        ContentFrame.Loaded += (_, _) => ShowHome();
    }

    private void ConfigureTitleBarButtons()
    {
        var titleBar = AppWindow?.TitleBar;
        if (titleBar is null)
        {
            App.Logger?.LogWarning("AppWindowTitleBar was null; skipped styling caption buttons");
            return;
        }

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0x6B, 0x76, 0x99);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Color.FromArgb(0xFF, 0xE8, 0xED, 0xFF);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>
    /// Shows the subtle one-time "Updated to vX" note in the InfoBar and
    /// auto-dismisses it after a few seconds. Safe to call from OnLaunched
    /// (runs on the UI thread that owns this window).
    /// </summary>
    public void ShowUpdateNote(string message)
    {
        UpdateNote.Title = message;
        UpdateNote.IsOpen = true;

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(6);
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            UpdateNote.IsOpen = false;
            t.Stop();
        };
        timer.Start();
    }

    // prefill is optional so every existing zero-argument call site (this
    // file's own launch-time ContentFrame.Loaded handler, ScanCompletePage's
    // "New scan", CelebrationPage's "Scan another folder", etc.) keeps
    // compiling unchanged - see HomePage.HomePagePrefillRequest's own doc
    // comment for what it lets a caller pre-select.
    public void ShowHome(HomePage.HomePagePrefillRequest? prefill = null)
    {
        ContentFrame.Navigate(typeof(HomePage), prefill);
    }

    public void ShowProgress(ProgressPageParameters parameters)
    {
        ContentFrame.Navigate(typeof(ProgressPage), parameters);
    }

    public void ShowHistory()
    {
        ContentFrame.Navigate(typeof(HistoryPage));
    }

    public void ShowScanComplete(
        ScanResult result,
        bool isLargeFilesMode = false,
        long? thresholdBytes = null,
        string? targetLabel = null)
    {
        ContentFrame.Navigate(
            typeof(ScanCompletePage),
            new ScanCompleteNavigationRequest(result, isLargeFilesMode, thresholdBytes, targetLabel));
    }

    public void ShowResults(ScanResult result, bool preSelectRecommended = false, string? targetLabel = null)
    {
        if (preSelectRecommended || targetLabel is not null)
        {
            ContentFrame.Navigate(
                typeof(ResultsPage),
                new ResultsNavigationRequest(result, preSelectRecommended, targetLabel));
        }
        else
        {
            ContentFrame.Navigate(typeof(ResultsPage), result);
        }
    }

    // scanResult/targetLabel are optional so a plain two-argument call
    // (candidateFiles, thresholdBytes) still compiles, while letting
    // LargeFilesResultsPage's own "← Summary" back-link work for real when a
    // caller has the full ScanResult on hand (falls back to Home when it
    // doesn't - see LargeFilesResultsPage.xaml.cs's BackToSummary_Click).
    // ScanCompletePage.ReviewResults_Click passes all four.
    public void ShowLargeFilesResults(
        IReadOnlyList<ViewModels.SelectableFile> candidateFiles,
        long thresholdBytes,
        ScanResult? scanResult = null,
        string? targetLabel = null)
    {
        ContentFrame.Navigate(
            typeof(LargeFilesResultsPage),
            new LargeFilesResultsNavigationRequest(candidateFiles, thresholdBytes, scanResult, targetLabel));
    }

    public void ShowCelebration(CelebrationParameters parameters)
    {
        ContentFrame.Navigate(typeof(CelebrationPage), parameters);
    }

    // The four empty/error-state screens - triggered from
    // HomePage.RouteScanOutcome's branching logic and, for ScanFailedPage,
    // from ProgressPageParameters.OnError on both scan-kickoff methods.
    public void ShowNoDuplicatesFound(NoDuplicatesFoundParameters parameters)
    {
        ContentFrame.Navigate(typeof(NoDuplicatesFoundPage), parameters);
    }

    public void ShowNothingOverThreshold(NothingOverThresholdParameters parameters)
    {
        ContentFrame.Navigate(typeof(NothingOverThresholdPage), parameters);
    }

    public void ShowEmptyFolder(EmptyFolderParameters parameters)
    {
        ContentFrame.Navigate(typeof(EmptyFolderPage), parameters);
    }

    public void ShowScanFailed(ScanFailedParameters parameters)
    {
        ContentFrame.Navigate(typeof(ScanFailedPage), parameters);
    }
}
