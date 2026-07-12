using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Orchestration;
using WinRT.Interop;

namespace Quickening.App.Views;

public sealed partial class HomePage : Page
{
    private enum ScanMode { Duplicates, LargeFiles }

    private const long BytesPerMegabyte = 1024L * 1024;
    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    // The Large Files threshold slider snaps to these fixed stops instead of
    // a free MB-or-GB range - a linear slider can't offer useful sizes
    // between 100 MB and 1 GB (the old MB/GB design's dead zone). Binary
    // (1024-based) counts so they round-trip cleanly through
    // FileSizeFormatter (also 1024-based) - e.g. 50 MB stays "50 MB", not
    // "47.7 MB". The slider's Value is an INDEX into this array.
    internal static readonly IReadOnlyList<long> ThresholdTicks = new[]
    {
        50 * BytesPerMegabyte,
        100 * BytesPerMegabyte,
        250 * BytesPerMegabyte,
        500 * BytesPerMegabyte,
        1 * BytesPerGigabyte,
        5 * BytesPerGigabyte,
        10 * BytesPerGigabyte,
        25 * BytesPerGigabyte,
        50 * BytesPerGigabyte,
        100 * BytesPerGigabyte,
    };

    // Default stop = 100 MB (index 1) - a sensible "large" floor, matching
    // the app's previous default.
    private const int DefaultTickIndex = 1;

    internal static long DefaultLargeFileThresholdBytes => ThresholdTicks[DefaultTickIndex];

    // Nearest tick index at or (failing that) closest to an arbitrary byte
    // count - used to position the slider for a prefilled/incoming threshold.
    internal static int ClosestTickIndex(long thresholdBytes)
    {
        var bestIndex = 0;
        var bestDelta = long.MaxValue;
        for (var i = 0; i < ThresholdTicks.Count; i++)
        {
            var delta = Math.Abs(ThresholdTicks[i] - thresholdBytes);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    // The ticks strictly smaller than the given threshold, largest first -
    // powers NothingOverThresholdPage's "rescan at a lower size" quick picks.
    internal static long[] ThresholdTicksBelow(long thresholdBytes) =>
        ThresholdTicks.Where(t => t < thresholdBytes).OrderByDescending(t => t).ToArray();

    private readonly DispatcherQueue _dispatcherQueue;

    // Session-scoped (static): the app remembers the last-used scan mode and
    // look-alike choice while it stays open, so returning Home doesn't reset
    // to Duplicates every time. NOT persisted across launches - a fresh
    // launch starts on Duplicates, by design.
    private static ScanMode _lastMode = ScanMode.Duplicates;
    private static bool _lastIncludeSimilar;

    private ScanMode _mode = _lastMode;
    private string? _selectedFolderPath;
    private string _selectedFolderLabel = "Downloads";

    public HomePage()
    {
        InitializeComponent();

        // Captured explicitly at construction time rather than relying on
        // this.DispatcherQueue - both should be equivalent here since the
        // constructor always runs on the UI thread, but capturing it this
        // way is the documented-safe fallback regardless of Page/
        // FrameworkElement's own DispatcherQueue property lifecycle.
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Both DuplicatesTileList and LargeFilesTileList are ItemsControls
        // bound to the SAME underlying tile list, just with different
        // ItemsPanel/ItemTemplate (2a's 3-column large tiles with a size
        // subtitle vs. 2b's compact 6-across row) - this is the same
        // "one ItemsSource, XAML picks the presentation" pattern the
        // previous redesign's HomePage used for its known-folder tiles,
        // just bound twice into two differently-templated ItemsControls
        // instead of once.
        _tiles = KnownFolderTiles();
        DuplicatesTileList.ItemsSource = _tiles;
        LargeFilesTileList.ItemsSource = _tiles;

        // First tile starts selected so the CTA has a real target the
        // instant the page loads, matching 2a's "Downloads" tile already
        // shown selected with no prior user interaction.
        SelectTile(_tiles[0]);

        // Set in code, not as XAML attributes - see HomePage.xaml's comment
        // on ThresholdSlider for why literal Minimum/Maximum/Value attributes
        // crash this app's unpackaged/self-contained build. The slider's
        // value is a tick INDEX into ThresholdTicks (0..N-1). Minimum/Maximum
        // before Value so the default index isn't clamped against the
        // Slider's own default 0-10 range first.
        ThresholdSlider.Minimum = 0;
        ThresholdSlider.Maximum = ThresholdTicks.Count - 1;
        ThresholdSlider.Value = DefaultTickIndex;
        ThresholdValueText.Text = EffectiveThresholdLabel();

        // Restore the session's last look-alike choice (mode is already
        // restored via the _mode = _lastMode field initializer).
        IncludeSimilarToggle.IsOn = _lastIncludeSimilar;

        // Fresh random hero line on each load (HomePage isn't cached, so this
        // runs every time the user lands on Home).
        var hero = RandomPhrases.HomeHero();
        HeroHeadlineText.Text = hero.Headline;
        HeroSubtitleText.Text = hero.Subtitle;

        UpdateModeVisuals();
        RefreshHistoryLifetimeStat();

        foreach (var tile in _tiles)
        {
            StartTileSizeCalculation(tile);
        }
    }

    // HomePage isn't NavigationCacheMode="Enabled", so a fresh instance (and
    // fresh call to this) happens every time the user lands back on Home -
    // no explicit refresh-on-return hook is needed beyond the constructor.
    private void RefreshHistoryLifetimeStat()
    {
        if (App.Store is null)
        {
            return;
        }

        var (bytesReclaimed, _) = App.Store.GetLifetimeStats();
        HistoryLifetimeStatText.Text = bytesReclaimed > 0 ? $"{FileSizeFormatter.Format(bytesReclaimed)} reclaimed" : "";
    }

    // Applies an optional HomePagePrefillRequest - see that record's own
    // doc comment below. Every existing caller that navigates here with no
    // parameter (MainWindow's initial ShowHome() on launch, ScanCompletePage's
    // "New scan", CelebrationPage's "Scan another folder", etc.) passes
    // e.Parameter as null, so this is a no-op for them.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not HomePagePrefillRequest prefill)
        {
            return;
        }

        ApplyPrefill(prefill);
    }

    private async void ApplyPrefill(HomePagePrefillRequest prefill)
    {
        // async void invoked from OnNavigatedTo: an exception escaping here
        // crashes the process rather than routing through ProgressPage's own
        // error handling, so the whole body is guarded.
        try
        {
            if (prefill.LargeFilesMode)
            {
                _mode = ScanMode.LargeFiles;
                UpdateModeVisuals();
            }

            if (prefill.FolderPath is { } folderPath)
            {
                SelectFolderPath(folderPath);
            }

            if (prefill.ThresholdBytes is { } thresholdBytes)
            {
                ThresholdSlider.Value = ClosestTickIndex(thresholdBytes);
                ThresholdValueText.Text = EffectiveThresholdLabel();
            }

            if (prefill.AutoStart)
            {
                var folder = _selectedFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (_mode == ScanMode.LargeFiles)
                {
                    await StartLargeFilesScan(folder, _selectedFolderLabel, prefill.ThresholdBytes ?? EffectiveThresholdBytes());
                }
                else
                {
                    await StartDuplicatesScan(folder, _selectedFolderLabel);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"HomePage prefill failed: {ex}");
        }
    }

    // Selects an arbitrary folder path for the CTA target - reuses SelectTile
    // when the path happens to match one of the six known-folder tiles
    // (so that tile's selected-state visuals light up correctly instead of
    // leaving a stale tile highlighted), and otherwise clears every tile's
    // IsSelected and sets the free-form path/label directly, the same state
    // BrowseButton_Click already puts the page into for an arbitrary
    // picked folder.
    private void SelectFolderPath(string folderPath)
    {
        var matchingTile = _tiles.FirstOrDefault(t => string.Equals(t.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (matchingTile is not null)
        {
            SelectTile(matchingTile);
            return;
        }

        _selectedTile = null;
        foreach (var t in _tiles)
        {
            t.IsSelected = false;
        }

        _selectedFolderPath = folderPath;
        _selectedFolderLabel = GetFolderLabel(folderPath);
        UpdateCtaText();
    }

    // Click handler for BOTH ItemsControls' per-tile Button (wired in XAML
    // as Click="FolderTile_Click" on each DataTemplate's root Button) -
    // selecting a tile does NOT start a scan by itself; per 2a/2b the CTA
    // button is the only thing that starts a scan, tapping a tile just
    // changes what the CTA will act on (and which tile shows the blue
    // selected treatment).
    private void FolderTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: KnownFolderTile tile })
        {
            return;
        }

        SelectTile(tile);
    }

    private KnownFolderTile? _selectedTile;
    private List<KnownFolderTile> _tiles = new();

    private void SelectTile(KnownFolderTile tile)
    {
        _selectedTile = tile;
        _selectedFolderPath = tile.Path;
        _selectedFolderLabel = tile.Name;
        UpdateCtaText();

        // Each tile's Border in the DataTemplate binds its selected-state
        // visuals (blue border + tinted fill per 2a) to {Binding IsSelected}
        // - IsSelected raises PropertyChanged (same pattern as
        // SelectableFile.IsSelected in ResultsViewModel.cs) specifically so
        // this loop's mutations refresh both already-realized ItemsControls
        // (Duplicates and Large Files tile lists share the same underlying
        // KnownFolderTile instances, so this one loop updates both at once).
        foreach (var t in _tiles)
        {
            t.IsSelected = ReferenceEquals(t, tile);
        }
    }

    // Wired as Click on BOTH segmented-pill Buttons (DuplicatesModeToggle /
    // LargeFilesModeToggle) rather than a SelectorBar.SelectionChanged or
    // ToggleButton.Click pair - see HomePage.xaml's comment on that Border
    // for why plain Buttons were chosen. The (object, object) signature
    // still matches a plain Button's Click delegate (RoutedEventHandler)
    // via C#'s standard contravariant method-group-to-delegate conversion,
    // so this keeps the exact signature the plan specifies.
    private void ModeToggle_SelectionChanged(object sender, object e)
    {
        _mode = ReferenceEquals(sender, LargeFilesModeToggle) ? ScanMode.LargeFiles : ScanMode.Duplicates;
        _lastMode = _mode; // remembered for the rest of this app session
        UpdateModeVisuals();
    }

    private void IncludeSimilar_Changed(object sender, RoutedEventArgs e)
    {
        _lastIncludeSimilar = IncludeSimilarToggle.IsOn;
    }

    private void UpdateModeVisuals()
    {
        var isDuplicates = _mode == ScanMode.Duplicates;
        DuplicatesTilesPanel.Visibility = isDuplicates ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        LargeFilesTilesPanel.Visibility = isDuplicates ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        ThresholdCard.Visibility = isDuplicates ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        // Look-alike matching only applies to a Duplicates scan.
        IncludeSimilarRow.Visibility = isDuplicates ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        // Segmented-pill selected/unselected recolor - done directly here
        // in code (rather than via a Style/converter) since these are
        // plain Buttons with no bound "am I selected" property of their
        // own; this is the simplest thing that reliably works given that
        // choice.
        var selectedBackground = (Brush)Application.Current.Resources["CtaSmallGradientBrush"];
        var unselectedBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var selectedForeground = new SolidColorBrush(Microsoft.UI.Colors.White);
        var unselectedForeground = (Brush)Application.Current.Resources["TextMutedBrush"];

        DuplicatesModeToggle.Background = isDuplicates ? selectedBackground : unselectedBackground;
        DuplicatesModeToggleText.Foreground = isDuplicates ? selectedForeground : unselectedForeground;
        LargeFilesModeToggle.Background = isDuplicates ? unselectedBackground : selectedBackground;
        LargeFilesModeToggleText.Foreground = isDuplicates ? unselectedForeground : selectedForeground;

        UpdateCtaText();
    }

    private void ThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ThresholdValueText.Text = EffectiveThresholdLabel();
        UpdateCtaText();
    }

    // Pointer-driven discrete stepping - see the overlay's comment in
    // HomePage.xaml. The overlay intercepts pointer input so the Slider's
    // own smooth drag never engages; we map the pointer's X to the nearest
    // whole tick index and set Value, so the thumb jumps stop-to-stop.
    private void ThresholdOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ThresholdSliderOverlay.CapturePointer(e.Pointer);
        SnapThresholdToPointer(e);
    }

    private void ThresholdOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.Pointer.IsInContact)
        {
            SnapThresholdToPointer(e);
        }
    }

    private void ThresholdOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ThresholdSliderOverlay.ReleasePointerCaptures();
    }

    private void SnapThresholdToPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var width = ThresholdSliderOverlay.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        // Inset by roughly half the slider thumb so the track's usable span
        // (thumb-center travel) maps 1:1 to the pointer, and the first/last
        // stops are reachable at the track ends.
        const double thumbInset = 9.0;
        var usable = Math.Max(1.0, width - (2 * thumbInset));
        var x = e.GetCurrentPoint(ThresholdSliderOverlay).Position.X;
        var fraction = Math.Clamp((x - thumbInset) / usable, 0.0, 1.0);
        var index = (int)Math.Round(fraction * (ThresholdTicks.Count - 1));
        if ((int)Math.Round(ThresholdSlider.Value) != index)
        {
            ThresholdSlider.Value = index;
        }
    }

    private long EffectiveThresholdBytes()
    {
        var index = Math.Clamp((int)Math.Round(ThresholdSlider.Value), 0, ThresholdTicks.Count - 1);
        return ThresholdTicks[index];
    }

    private string EffectiveThresholdLabel() => FileSizeFormatter.Format(EffectiveThresholdBytes());

    private void UpdateCtaText()
    {
        CtaButtonText.Text = _mode == ScanMode.Duplicates
            ? $"⚡ Scan {_selectedFolderLabel} for duplicates"
            : $"⚡ Find files over {EffectiveThresholdLabel()} in {_selectedFolderLabel}";
    }

    private async void BrowseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Guards against a rapid double-click launching two concurrent
        // FolderPicker instances while the first await is still pending.
        BrowseButton.IsEnabled = false;
        try
        {
            var windowHandle = WindowNative.GetWindowHandle(App.MainWindowInstance);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
            var picker = new FolderPicker(windowId);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            // PickFolderResult (Microsoft.Windows.Storage.Pickers, the
            // WindowsAppSDK picker required for this unpackaged app - see the
            // comment on the classic-picker COMException this app already hit
            // elsewhere) exposes Path only, not Name (unlike the classic
            // Windows.Storage.StorageFolder) - derive the display label from
            // the path's leaf segment instead.
            _selectedFolderPath = folder.Path;
            _selectedFolderLabel = GetFolderLabel(folder.Path);
            UpdateCtaText();
        }
        finally
        {
            BrowseButton.IsEnabled = true;
        }
    }

    // Path.GetFileName returns "" for a bare drive root ("C:\") since there's
    // no leaf segment to trim to - fall back to the full path in that case
    // rather than showing an empty label.
    private static string GetFolderLabel(string path)
    {
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private async void CtaButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // The cloud-placeholder pre-pass inside Start*Scan can take seconds
        // on a big folder; without this guard a second click during that
        // await launches a second scan pipeline.
        CtaButton.IsEnabled = false;
        try
        {
            var folderPath = _selectedFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if (_mode == ScanMode.Duplicates)
            {
                await StartDuplicatesScan(folderPath, _selectedFolderLabel);
            }
            else
            {
                await StartLargeFilesScan(folderPath, _selectedFolderLabel, EffectiveThresholdBytes());
            }
        }
        finally
        {
            CtaButton.IsEnabled = true;
        }
    }

    private async void SettingsButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await SettingsDialog.ShowAsync(XamlRoot);

    private void HistoryButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ((MainWindow)App.MainWindowInstance!).ShowHistory();

    // coffee/6a -> 6c overlay (the pill never opens the browser directly).
    private async void CoffeeButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await CoffeeDialog.ShowAsync(XamlRoot);

    // Drag-and-drop (new-screens 4h, single-folder half only - see
    // HomePage.xaml's DragOverlay comment for why the mockup's multi-select
    // half is out of scope). A dragged item can be a file too, not just a
    // folder - GetDraggedFolderPathAsync resolves either case down to "the
    // folder to scan" (a dropped file's containing folder), matching what
    // BrowseButton_Click already lets a user pick directly.
    private async void RootGrid_DragEnter(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        var deferral = e.GetDeferral();
        try
        {
            DragOverlayPathText.Text = await GetDraggedFolderPathAsync(e) ?? "";
            DragOverlay.Visibility = Visibility.Visible;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void RootGrid_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private void RootGrid_DragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs e) =>
        DragOverlay.Visibility = Visibility.Collapsed;

    private async void RootGrid_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var folderPath = await GetDraggedFolderPathAsync(e);
            if (folderPath is not null)
            {
                SelectFolderPath(folderPath);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    // A dropped folder is used directly; a dropped file falls back to its
    // containing directory, so dropping either a folder or a stray file
    // onto Home always resolves to something Enumerate can scan.
    private static async Task<string?> GetDraggedFolderPathAsync(Microsoft.UI.Xaml.DragEventArgs e)
    {
        var items = await e.DataView.GetStorageItemsAsync();
        var first = items.FirstOrDefault();
        return first switch
        {
            Windows.Storage.StorageFolder folder => folder.Path,
            Windows.Storage.StorageFile file => System.IO.Path.GetDirectoryName(file.Path),
            _ => null,
        };
    }

    // new-screens 4m: a quick metadata-only pre-pass (FileEnumerator.
    // PreviewCloudPlaceholders - no hashing, same cost class as a Large
    // Files scan's own walk) counts OneDrive/Dropbox/Google Drive
    // "online-only" placeholder files before committing to a real scan.
    // Returns null if the user cancelled at the warning dialog - the two
    // callers treat that as "don't scan at all". A folder with no
    // placeholders skips the dialog entirely and returns false immediately.
    private async Task<bool?> CheckCloudPlaceholdersAsync(string folderPath)
    {
        var (count, bytes) = await Task.Run(() => new Quickening.Core.Scanning.FileEnumerator().PreviewCloudPlaceholders(
            folderPath, App.Settings.ScanHiddenFiles, App.Settings.AllowProtectedPaths));

        if (count == 0)
        {
            return false;
        }

        var choice = await CloudPlaceholderWarningDialog.ShowAsync(XamlRoot, count, bytes);
        return choice switch
        {
            CloudPlaceholderChoice.SkipOnlineOnly => true,
            CloudPlaceholderChoice.DownloadAndInclude => false,
            _ => null,
        };
    }

    private async Task StartDuplicatesScan(string folderPath, string targetLabel)
    {
        var store = App.Store ?? throw new InvalidOperationException("App.Store was not initialized.");

        var excludeCloudPlaceholders = await CheckCloudPlaceholdersAsync(folderPath);
        if (excludeCloudPlaceholders is null)
        {
            return;
        }

        if (!await TryClaimScanSlotAsync())
        {
            return;
        }

        // Powers the tray icon's right-click "Scan {folder} for duplicates"
        // item (tray-menus 5a) - recorded at scan START (not on success),
        // since the user's intent to target this folder is real regardless
        // of whether the scan itself later succeeds.
        App.Settings.LastScannedFolderPath = folderPath;
        App.SaveSettings();

        var parameters = new ProgressPageParameters(
            Headline: RandomPhrases.ScanStarting(),
            Operation: async (progress, cancellationToken) =>
            {
                try
                {
                    // Progress<T> is constructed HERE, on the UI thread, so it
                    // captures the UI SynchronizationContext and delivers
                    // reports in order - constructed inside Task.Run it would
                    // post each report to the thread pool unordered, and the
                    // ring percentage could visibly run backwards.
                    var paranoid = App.Settings.ParanoidMode;
                    var scanProgress = new Progress<ScanProgress>(p =>
                        progress.Report(MapScanProgress(p, paranoid)));
                    var includeSimilar = IncludeSimilarToggle.IsOn;
                    var result = await Task.Run(
                        () => new ScanOrchestrator(store).Scan(
                            folderPath,
                            scanProgress,
                            App.Settings.ScanHiddenFiles,
                            App.Settings.AllowProtectedPaths,
                            App.Settings.ParanoidMode,
                            excludeCloudPlaceholders.Value,
                            cancellationToken,
                            computeSimilarity: includeSimilar),
                        cancellationToken);
                    return (object?)new ScanOutcome(result, IsLargeFilesMode: false, ThresholdBytes: null, TargetLabel: targetLabel, FolderPath: folderPath);
                }
                finally
                {
                    ScanCoordinator.End();
                }
            },
            Completed: outcomeObj => HomePage.RouteScanOutcome((ScanOutcome)outcomeObj!),
            RingHue: ProgressRingHue.Accent,
            OnError: ex =>
            {
                ((MainWindow)App.MainWindowInstance!).ShowScanFailed(new ScanFailedParameters(
                    targetLabel, folderPath, IsLargeFilesMode: false, ThresholdBytes: null, Error: ex));
                return Task.CompletedTask;
            },
            TargetLabel: targetLabel);

        ((MainWindow)App.MainWindowInstance!).ShowProgress(parameters);
    }

    private async Task StartLargeFilesScan(string folderPath, string targetLabel, long thresholdBytes)
    {
        var store = App.Store ?? throw new InvalidOperationException("App.Store was not initialized.");

        var excludeCloudPlaceholders = await CheckCloudPlaceholdersAsync(folderPath);
        if (excludeCloudPlaceholders is null)
        {
            return;
        }

        if (!await TryClaimScanSlotAsync())
        {
            return;
        }

        var parameters = new ProgressPageParameters(
            Headline: RandomPhrases.ScanStarting(),
            Operation: async (progress, cancellationToken) =>
            {
                try
                {
                    // See StartDuplicatesScan for why this must be created on
                    // the UI thread, not inside Task.Run.
                    var scanProgress = new Progress<ScanProgress>(p =>
                        progress.Report(MapScanProgress(p, paranoid: false)));
                    var result = await Task.Run(
                        () => new ScanOrchestrator(store).ScanForLargeFiles(
                            folderPath,
                            scanProgress,
                            App.Settings.ScanHiddenFiles,
                            App.Settings.AllowProtectedPaths,
                            excludeCloudPlaceholders.Value,
                            cancellationToken),
                        cancellationToken);
                    return (object?)new ScanOutcome(result, IsLargeFilesMode: true, ThresholdBytes: thresholdBytes, TargetLabel: targetLabel, FolderPath: folderPath);
                }
                finally
                {
                    ScanCoordinator.End();
                }
            },
            Completed: outcomeObj => HomePage.RouteScanOutcome((ScanOutcome)outcomeObj!),
            RingHue: ProgressRingHue.Accent,
            OnError: ex =>
            {
                ((MainWindow)App.MainWindowInstance!).ShowScanFailed(new ScanFailedParameters(
                    targetLabel, folderPath, IsLargeFilesMode: true, ThresholdBytes: thresholdBytes, Error: ex));
                return Task.CompletedTask;
            },
            TargetLabel: targetLabel);

        ((MainWindow)App.MainWindowInstance!).ShowProgress(parameters);
    }

    /// <summary>
    /// Claims the process-wide scan slot, telling the user why nothing
    /// happened if another scan (user-started or scheduled) already holds it.
    /// </summary>
    private async Task<bool> TryClaimScanSlotAsync()
    {
        if (ScanCoordinator.TryBegin())
        {
            return true;
        }

        try
        {
            var dialog = new ContentDialog
            {
                Title = "A scan is already running",
                Content = "Another scan is in progress (it may be a scheduled scan). Wait for it to finish, or stop it first.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Failed to show the scan-busy dialog: {ex}");
        }

        return false;
    }

    // Routes a finished scan to the right next screen depending on outcome:
    // an empty folder always wins regardless of mode, then Duplicates and
    // Large Files modes each have their own "nothing found" screen versus
    // the normal ShowScanComplete path.
    private static Task RouteScanOutcome(ScanOutcome outcome)
    {
        var window = (MainWindow)App.MainWindowInstance!;

        if (outcome.Result.TotalFilesScanned == 0)
        {
            window.ShowEmptyFolder(new EmptyFolderParameters(outcome.FolderPath));
            return Task.CompletedTask;
        }

        if (!outcome.IsLargeFilesMode)
        {
            // A scan with no exact duplicates can still have "looks-alike"
            // photos worth showing (new-screens 4k) - only fall back to the
            // empty state when there's truly nothing in either list.
            if (outcome.Result.DuplicateGroups.Count == 0 && outcome.Result.SimilarityGroups.Count == 0)
            {
                window.ShowNoDuplicatesFound(new NoDuplicatesFoundParameters(
                    outcome.Result.TotalFilesScanned, outcome.TargetLabel, outcome.FolderPath));
            }
            else
            {
                window.ShowScanComplete(outcome.Result, targetLabel: outcome.TargetLabel);
            }

            return Task.CompletedTask;
        }

        // outcome.Result came from ScanForLargeFiles here, so AllFiles is
        // every file in the folder, not just ones with a duplicate -
        // ScanCompletePage.ShowLargeFilesSummary re-filters this same list
        // once the summary screen needs the actual file rows for its bar
        // chart / "Review Results".
        var candidateCount = outcome.Result.AllFiles!
            .Count(f => f.SizeBytes >= outcome.ThresholdBytes!.Value);

        if (candidateCount == 0)
        {
            window.ShowNothingOverThreshold(new NothingOverThresholdParameters(
                outcome.TargetLabel, outcome.FolderPath, outcome.ThresholdBytes!.Value));
        }
        else
        {
            window.ShowScanComplete(
                outcome.Result,
                isLargeFilesMode: true,
                thresholdBytes: outcome.ThresholdBytes,
                targetLabel: outcome.TargetLabel);
        }

        return Task.CompletedTask;
    }

    // Maps Core scan progress to the UI's ProgressUpdate, attaching a stage
    // headline/subtitle only when the phase changes so the ProgressPage shows
    // "Comparing…" (byte-for-byte in paranoid mode) and a live "Finishing
    // up…" state instead of sitting frozen at 100% during post-compare work.
    private static ProgressUpdate MapScanProgress(ScanProgress p, bool paranoid)
    {
        string? headline = null;
        string? subtitle = null;
        switch (p.Phase)
        {
            case ScanPhase.Comparing:
                headline = paranoid ? "Comparing files, byte for byte…" : "Comparing files for duplicates…";
                subtitle = paranoid
                    ? "Double-checking every match, byte for byte."
                    : "Matching file contents — the part that actually finds duplicates.";
                break;
            case ScanPhase.Finalizing:
                headline = "Almost done…";
                subtitle = "Saving results and tidying up.";
                break;
            // Enumerating: leave null so the page keeps its initial headline
            // and per-target "Counting every file in {folder}…" subtitle.
        }

        return new ProgressUpdate(
            p.FilesProcessed, p.TotalFiles, p.CurrentPath,
            p.DuplicateGroupsFoundSoFar, p.ReclaimableBytesSoFar,
            headline, subtitle);
    }

    private sealed record ScanOutcome(ScanResult Result, bool IsLargeFilesMode, long? ThresholdBytes, string TargetLabel, string FolderPath);

    /// <summary>
    /// Optional navigation parameter for HomePage - lets a caller (added in
    /// Task 11 for the four empty/error-state screens) pre-select a scan
    /// mode/folder before the user sees the page, and optionally kick off
    /// that exact scan immediately rather than making the user press the CTA
    /// again. FolderPath null means "leave whatever tile is already
    /// selected"; ThresholdBytes null (with AutoStart set) means "use
    /// whatever the slider is already at" - both let a caller prefill only
    /// the parts it actually knows about.
    ///
    /// Used by:
    /// - NoDuplicatesFoundPage's "Try Large Files here instead" (LargeFilesMode:
    ///   true, FolderPath set, no AutoStart - just switches Home into the
    ///   right mode/folder so the user still presses the CTA themselves).
    /// - NothingOverThresholdPage's "Rescan at {lower value}" (LargeFilesMode:
    ///   true, FolderPath + ThresholdBytes set, AutoStart: true - re-runs the
    ///   scan immediately at the picked threshold via StartLargeFilesScan).
    /// - ScanFailedPage's "Try that scan again" (LargeFilesMode/FolderPath/
    ///   ThresholdBytes all carried over from the failed attempt, AutoStart:
    ///   true - re-runs the exact same scan that just failed, via whichever
    ///   of StartDuplicatesScan/StartLargeFilesScan matches LargeFilesMode).
    /// </summary>
    public sealed record HomePagePrefillRequest(
        string? FolderPath,
        bool LargeFilesMode,
        bool AutoStart = false,
        long? ThresholdBytes = null);

    private List<KnownFolderTile> KnownFolderTiles() => new()
    {
        new KnownFolderTile("Desktop", "IconDesktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        new KnownFolderTile("Documents", "IconDocuments", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        new KnownFolderTile("Downloads", "IconDownloads", Windows.Storage.UserDataPaths.GetDefault().Downloads),
        new KnownFolderTile("Pictures", "IconPictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        new KnownFolderTile("Videos", "IconVideos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        new KnownFolderTile("Music", "IconMusic", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
    };

    // HomePage is rebuilt on every navigation back to it; without a cache
    // that means six recursive size walks of Desktop/Documents/Downloads/
    // Pictures/Videos/Music per visit, all discarded on the next
    // navigation. Sizes drift slowly, so a short-lived process-wide cache
    // is the right trade.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Bytes, DateTime ComputedUtc)> TileSizeCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TileSizeCacheLifetime = TimeSpan.FromMinutes(5);

    // Cancelled in OnNavigatedFrom so walks don't keep grinding the disk
    // (and enqueueing to a dead page's tiles) after the user leaves Home.
    private readonly CancellationTokenSource _tileSizeCts = new();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _tileSizeCts.Cancel();
    }

    // Kicks off the recursive size walk for one tile on a background thread
    // and marshals the result back via the DispatcherQueue captured in the
    // constructor - tile.SizeBytes's setter raises PropertyChanged for both
    // SizeBytes and SizeLabel, so the tile's bound size subtitle just
    // updates in place whenever this finishes, however long it takes; the
    // page never blocks waiting for it.
    private void StartTileSizeCalculation(KnownFolderTile tile)
    {
        // Environment.GetFolderPath returns "" when a known folder is
        // unavailable/redirected; DirectoryInfo("") throws ArgumentException,
        // which the walk's IO-only catches would let escape as an unobserved
        // task exception.
        if (string.IsNullOrEmpty(tile.Path))
        {
            return;
        }

        if (TileSizeCache.TryGetValue(tile.Path, out var cached) &&
            DateTime.UtcNow - cached.ComputedUtc < TileSizeCacheLifetime)
        {
            tile.SizeBytes = cached.Bytes;
            return;
        }

        var token = _tileSizeCts.Token;
        _ = Task.Run(() =>
        {
            try
            {
                var bytes = CalculateDirectorySize(tile.Path, token);
                TileSizeCache[tile.Path] = (bytes, DateTime.UtcNow);
                _dispatcherQueue.TryEnqueue(() => tile.SizeBytes = bytes);
            }
            catch (OperationCanceledException)
            {
                // User left the page - the partial total is simply discarded.
            }
            catch (Exception ex)
            {
                // Fire-and-forget: anything escaping here would be silently
                // dropped as an unobserved task exception, with the tile's
                // subtitle just never filling and no trace of why.
                App.Logger?.LogError($"Tile size calculation failed for '{tile.Path}': {ex}");
            }
        });
    }

    // Manual recursive walk rather than
    // DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories) -
    // that single-call form throws (and stops enumerating entirely) the
    // moment it hits ANY inaccessible subfolder anywhere in the tree, which
    // would blank out a known folder's whole size subtitle just because one
    // deeply-nested subfolder is locked down. Recursing manually and
    // catching per-directory/per-file means one bad subfolder is simply
    // skipped (contributes 0) while every sibling still gets counted.
    private static long CalculateDirectorySize(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return 0;
            }

            foreach (var file in directory.EnumerateFiles())
            {
                try
                {
                    total += file.Length;
                }
                catch (IOException)
                {
                    // File vanished, or is otherwise unreadable mid-walk -
                    // skip it rather than aborting the rest of the folder.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            foreach (var subdirectory in directory.EnumerateDirectories())
            {
                // Skip reparse points (symlinks/junctions) rather than
                // recursing into them - an ancestor-pointing junction would
                // otherwise recurse forever (thread-pool hang, eventual
                // stack overflow), and even a non-cyclic symlink would risk
                // double-counting bytes that physically live elsewhere.
                if (subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                try
                {
                    total += CalculateDirectorySize(subdirectory.FullName, cancellationToken);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return total;
    }
}

/// <summary>
/// Not a record - IsSelected needs to raise PropertyChanged so the same
/// instances bound into both DuplicatesTileList and LargeFilesTileList
/// (HomePage.xaml.cs binds one shared list into both ItemsControls) refresh
/// their selected-state visuals in both places when SelectTile mutates them,
/// the same INotifyPropertyChanged pattern ResultsViewModel.cs's
/// SelectableFile.IsSelected already uses for the same reason.
/// </summary>
public sealed class KnownFolderTile : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;
    private long? _sizeBytes;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string IconTemplateKey { get; }
    public string Path { get; }

    public KnownFolderTile(string name, string iconTemplateKey, string path)
    {
        Name = name;
        IconTemplateKey = iconTemplateKey;
        Path = path;
    }

    public DataTemplate IconTemplate => (DataTemplate)Application.Current.Resources[IconTemplateKey];

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    // Filled in asynchronously after construction (a recursive directory
    // size walk is too slow to block page load on) - see Task 4 Step 2's
    // note on tile size subtitles. Null until that background calculation
    // finishes, at which point the tile's size-subtitle TextBlock (bound to
    // SizeLabel) updates via this same PropertyChanged event.
    public long? SizeBytes
    {
        get => _sizeBytes;
        set
        {
            _sizeBytes = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SizeBytes)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SizeLabel)));
        }
    }

    public string SizeLabel => SizeBytes is { } bytes ? Quickening.App.Formatting.FileSizeFormatter.Format(bytes) : "";
}
