using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;
using Quickening.App.Views;
using Quickening.Core.Storage;

namespace Quickening.App.Tray;

/// <summary>
/// Owns the single H.NotifyIcon.WinUI TaskbarIcon for this process - created
/// only while Settings.WatchForNewDuplicates is on (Start/Stop, called from
/// App.xaml.cs at launch and from SettingsDialog whenever that toggle
/// changes), so an install that never enables watching never puts an icon
/// in the tray at all. Deliberately owned at the App level, not MainWindow -
/// per tray-menus 5a's own design, the tray icon and its menu need to keep
/// working even while MainWindow itself is hidden.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly SqliteStore _store;
    private TaskbarIcon? _icon;

    // Stored as raw ticks (0 = not paused) rather than DateTime?: this is
    // written on the UI thread (menu clicks) and read on FileSystemWatcher
    // callback threads, and a multi-word nullable struct can tear under
    // that access pattern. A single long read/written via Volatile cannot.
    private long _pausedUntilTicksUtc;

    public TrayIconService(SqliteStore store)
    {
        _store = store;
    }

    /// <summary>
    /// True while a "Pause" choice from the right-click menu (tray-menus 5a)
    /// is still in effect - DuplicateWatcherService checks this before
    /// acting on a new file, so pausing doesn't need to actually stop/start
    /// any FileSystemWatcher instances.
    /// </summary>
    public bool IsWatchingPaused => DateTime.UtcNow.Ticks < Volatile.Read(ref _pausedUntilTicksUtc);

    public void Start()
    {
        if (_icon is not null)
        {
            return;
        }

        _icon = new TaskbarIcon
        {
            ToolTipText = "Quickening",
            Icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")),
            // ContextMenuMode.PopupMenu (the native-Win32-menu option, and
            // the closer visual match to tray-menus 5a's "native-quiet
            // menu" spec) reproducibly crashed this unpackaged app with no
            // managed exception logged - a native fault, not something
            // try/catch can guard against. SecondWindow is what
            // H.NotifyIcon's own unpackaged sample app
            // (H.NotifyIcon.Apps.WinUI.Windowless) actually uses and is
            // confirmed stable for this exact deployment model. Do not
            // change this back to PopupMenu without re-verifying live -
            // this isn't a style preference, it's a crash fix.
            ContextMenuMode = ContextMenuMode.SecondWindow,
            MenuActivation = PopupActivationMode.RightClick,
            // 5c's custom mini-dashboard flyout (H.NotifyIcon's TrayPopup)
            // reproducibly crashed this unpackaged app with no managed
            // exception logged - a native fault, confirmed twice live, and
            // never demonstrated by H.NotifyIcon's own unpackaged sample app
            // either (it only shows ContextFlyout + a plain window-show
            // command). Left-click instead just shows the main window,
            // matching that sample's own approach - do not reintroduce
            // TrayPopup without re-verifying live first.
            LeftClickCommand = new RelayCommand(OpenQuickening),
        };
        _icon.ContextFlyout = BuildContextMenu();
        _icon.ForceCreate();
    }

    public void Stop()
    {
        _icon?.Dispose();
        _icon = null;
    }

    public void Dispose() => Stop();

    // Right-click menu (tray-menus 5a). SecondWindow mode (set in Start)
    // renders this as its own small WinUI window - the one ContextMenuMode
    // confirmed stable for this unpackaged app (see Start's own comment).
    // Items are rebuilt every time the menu opens: the scan-folder label
    // and the "Watching…/paused" status line reflect live state, not
    // whatever was true when the tray icon was created.
    private MenuFlyout BuildContextMenu()
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => PopulateMenu(flyout);
        PopulateMenu(flyout);
        return flyout;
    }

    private void PopulateMenu(MenuFlyout flyout)
    {
        flyout.Items.Clear();

        var openItem = new MenuFlyoutItem { Text = "Open Quickening" };
        openItem.Click += (_, _) => OpenQuickening();
        flyout.Items.Add(openItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Falls back to Downloads (matching tray-menus 5a's own mockup
        // example) until this install has ever run a Duplicates scan.
        var scanFolder = App.Settings.LastScannedFolderPath
            ?? Windows.Storage.UserDataPaths.GetDefault().Downloads;
        var scanItem = new MenuFlyoutItem { Text = $"Scan {GetFolderLabel(scanFolder)} for duplicates" };
        scanItem.Click += (_, _) =>
        {
            OpenQuickening();
            ((MainWindow)App.MainWindowInstance!).ShowHome(new HomePage.HomePagePrefillRequest(scanFolder, LargeFilesMode: false));
        };
        flyout.Items.Add(scanItem);

        var largeFilesItem = new MenuFlyoutItem { Text = "Find large files…" };
        largeFilesItem.Click += (_, _) =>
        {
            OpenQuickening();
            ((MainWindow)App.MainWindowInstance!).ShowHome(new HomePage.HomePagePrefillRequest(FolderPath: null, LargeFilesMode: true));
        };
        flyout.Items.Add(largeFilesItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var watchingItem = new MenuFlyoutItem
        {
            Text = IsWatchingPaused ? "Watching paused" : "Watching for duplicates",
            IsEnabled = false,
        };
        flyout.Items.Add(watchingItem);

        if (IsWatchingPaused)
        {
            var resumeItem = new MenuFlyoutItem { Text = "Resume watching" };
            resumeItem.Click += (_, _) => Volatile.Write(ref _pausedUntilTicksUtc, 0);
            flyout.Items.Add(resumeItem);
        }

        var pauseSubItem = new MenuFlyoutSubItem { Text = "Pause" };
        AddPauseOption(pauseSubItem, "15 min", TimeSpan.FromMinutes(15));
        AddPauseOption(pauseSubItem, "1 hr", TimeSpan.FromHours(1));
        var untilResumeItem = new MenuFlyoutItem { Text = "Until I resume" };
        untilResumeItem.Click += (_, _) => Volatile.Write(ref _pausedUntilTicksUtc, DateTime.MaxValue.Ticks);
        pauseSubItem.Items.Add(untilResumeItem);
        flyout.Items.Add(pauseSubItem);

        var settingsItem = new MenuFlyoutItem { Text = "Settings" };
        settingsItem.Click += async (_, _) =>
        {
            OpenQuickening();
            await SettingsDialog.ShowAsync(App.MainWindowInstance!.Content.XamlRoot);
        };
        flyout.Items.Add(settingsItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Quit lives only here, per Windows convention (tray-menus 5a's own
        // note). This is the one action that must bypass the hide-to-tray
        // Closing handler (see MainWindow.xaml.cs) entirely, so it sets
        // App.IsQuitting first rather than just closing the window.
        var quitItem = new MenuFlyoutItem { Text = "Quit Quickening" };
        quitItem.Click += (_, _) =>
        {
            App.IsQuitting = true;
            App.MainWindowInstance!.Close();
        };
        flyout.Items.Add(quitItem);
    }

    private void AddPauseOption(MenuFlyoutSubItem parent, string label, TimeSpan duration)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += (_, _) => Volatile.Write(ref _pausedUntilTicksUtc, DateTime.UtcNow.Add(duration).Ticks);
        parent.Items.Add(item);
    }

    private static void OpenQuickening()
    {
        var window = App.MainWindowInstance!;
        WindowExtensions.Show(window);
        WindowExtensions.ShowInTaskbar(window);
    }

    private static string GetFolderLabel(string path)
    {
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    // TaskbarIcon.LeftClickCommand needs a real System.Windows.Input.ICommand
    // (the WPF/UWP-era interface H.NotifyIcon standardized on across all its
    // XAML flavors) - no ready-made implementation exists elsewhere in this
    // codebase (App.Store's LiveStats/etc. never needed one), so this is
    // deliberately minimal rather than pulling in a full MVVM toolkit for
    // one callback.
    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;

        public RelayCommand(Action execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
