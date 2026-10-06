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

            // Floor the window size: below ~1000 px the Results page's sidebar
            // + file rows run out of room and rows lose their file names and
            // paths entirely, while the header overlaps the select toolbar
            // (QA-16, seen at 700 px). A minimum is simpler and safer than
            // reflowing every results layout for narrow widths.
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 1024;
                presenter.PreferredMinimumHeight = 700;
            }

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
        ContentFrame.Loaded += (_, _) =>
        {
            ShowHome();
            PositionRecycleBinPill();
            RefreshRecycleBinPill();
        };

        // The pill tracks the REAL Windows bin, which other apps and Explorer
        // mutate too - refresh whenever this app changes screens (covers every
        // post-delete flow, which all navigate) plus a slow timer for changes
        // made outside the app while it sits idle.
        ContentFrame.Navigated += (_, _) => RefreshRecycleBinPill();
        var binTimer = DispatcherQueue.CreateTimer();
        binTimer.Interval = TimeSpan.FromSeconds(30);
        binTimer.IsRepeating = true;
        binTimer.Tick += (_, _) => RefreshRecycleBinPill();
        binTimer.Start();
    }

    // Stateless uses only (totals query + empty) - Undo restores go through the
    // page-owned instances that performed the deletes, never this one.
    private readonly Quickening.Core.Deletion.RecycleBinService _binService = new();

    // True while the Empty dialog is open - both suppresses re-entry (double
    // click) and stops the 30s timer's refresh from fighting the dialog.
    private bool _binDialogOpen;

    // Places the pill clear of the system caption buttons: RightInset is
    // their PHYSICAL width, converted to DIPs via the current rasterization
    // scale. Falls back to the XAML default margin when either is unavailable.
    private void PositionRecycleBinPill()
    {
        var inset = AppWindow?.TitleBar?.RightInset ?? 0;
        var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        if (inset > 0 && scale > 0)
        {
            RecycleBinPill.Margin = new Thickness(0, 6, (inset / scale) + 12, 0);
        }
    }

    internal void RefreshRecycleBinPill()
    {
        if (_binDialogOpen)
        {
            return;
        }

        try
        {
            var (items, sizeBytes) = _binService.GetRecycleBinTotals();
            if (items > 0)
            {
                RecycleBinPillText.Text =
                    $"{items} item{(items == 1 ? "" : "s")} · {Formatting.FileSizeFormatter.Format(sizeBytes)}";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    RecycleBinPill, $"Recycle Bin: {RecycleBinPillText.Text}. Click to empty it.");
                RecycleBinPill.Visibility = Visibility.Visible;
            }
            else
            {
                RecycleBinPill.Visibility = Visibility.Collapsed;
            }
        }
        catch (IOException)
        {
            // Bin query unavailable (e.g. transient shell error) - just hide
            // the pill rather than showing stale numbers.
            RecycleBinPill.Visibility = Visibility.Collapsed;
        }
    }

    private async void RecycleBinPill_Click(object sender, RoutedEventArgs e)
    {
        if (_binDialogOpen || Content?.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        _binDialogOpen = true;
        try
        {
            // The dialog only reports the user's hold-to-confirm decision -
            // ACTUALLY emptying is the caller's job (same contract as
            // HistoryPage.EmptyRecycleBinButton_Click). SHEmptyRecycleBin on a
            // multi-GB bin takes seconds to minutes; run it off the UI thread
            // so the window doesn't go "not responding", and swallow-and-log
            // rather than crash this async void handler on a COM failure.
            var confirmed = await Views.EmptyRecycleBinDialog.ShowAsync(xamlRoot, _binService);
            if (confirmed)
            {
                RecycleBinPill.IsEnabled = false;
                try
                {
                    await Task.Run(_binService.EmptyRecycleBin);
                }
                catch (Exception ex)
                {
                    App.Logger?.LogError($"Emptying the Recycle Bin failed: {ex}");
                }
                finally
                {
                    RecycleBinPill.IsEnabled = true;
                }
            }
        }
        finally
        {
            _binDialogOpen = false;
        }

        RefreshRecycleBinPill();
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

    // Returns false when the frame refused the navigation (e.g. it was issued
    // mid-navigation), so a caller holding the scan slot can release it -
    // ProgressPage is what runs the operation whose finally ends the scan.
    public bool ShowProgress(ProgressPageParameters parameters)
    {
        return ContentFrame.Navigate(typeof(ProgressPage), parameters);
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

    // Housekeeping review (F5) - the empty folders / zero-byte files a scan
    // found, reached via the "Tidy up" callout on ScanCompletePage.
    public void ShowHousekeeping(HousekeepingNavigationRequest request)
    {
        ContentFrame.Navigate(typeof(HousekeepingPage), request);
    }

    // Duplicate-folder review (F8) - sets of whole folders that are exact copies,
    // reached via the "duplicate folders" callout on ScanCompletePage.
    public void ShowDuplicateFolders(DuplicateFoldersNavigationRequest request)
    {
        ContentFrame.Navigate(typeof(DuplicateFoldersPage), request);
    }

    // Manage Ignored Files - reached from Settings; lets the user remove entries
    // from the ignore list.
    public void ShowManageIgnored()
    {
        ContentFrame.Navigate(typeof(ManageIgnoredPage));
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
