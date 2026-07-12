using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Quickening.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// The active main window, exposed so other pages (e.g. HomePage's
    /// folder picker) can obtain an HWND for WinRT APIs that need explicit
    /// window association in an unpackaged app.
    /// </summary>
    public static Window? MainWindowInstance { get; private set; }

    /// <summary>
    /// App-lifetime SQLite store, shared by every page so scans, deletes,
    /// and stats all write to the same connection instead of each page
    /// opening (and leaking) its own.
    /// </summary>
    public static Quickening.Core.Storage.SqliteStore? Store { get; private set; }

    /// <summary>
    /// App-lifetime file logger, shared by every page.
    /// </summary>
    public static Quickening.Core.Logging.ILogger? Logger { get; private set; }

    /// <summary>
    /// App-lifetime user preferences (e.g. "Scan hidden files") - loaded once
    /// at startup, mutated in place by SettingsPage, and persisted via
    /// SettingsInstance.Save whenever a page changes one.
    /// </summary>
    public static Settings.AppSettings Settings { get; private set; } = new();

    private static Settings.SettingsService? SettingsInstance { get; set; }

    /// <summary>
    /// The single tray icon for this process - null until OnLaunched creates
    /// it (always created, regardless of the Watch setting; Start()/Stop()
    /// is what actually shows/hides the icon - see TrayIconService's own doc
    /// comment for why it's owned here rather than by MainWindow).
    /// </summary>
    public static Tray.TrayIconService? Tray { get; private set; }

    /// <summary>
    /// Background duplicate-on-arrival watcher (mockup 4l) - started/stopped
    /// in lockstep with Tray from the same WatchForNewDuplicates toggle (see
    /// SettingsDialog's toggle handler and OnLaunched below).
    /// </summary>
    public static Tray.DuplicateWatcherService? Watcher { get; private set; }

    /// <summary>
    /// In-process scheduled-scan timer (Automation group's other Phase 1
    /// setting) - runs independent of Watcher/Tray, since
    /// ScheduledScanEnabled and WatchForNewDuplicates are two separate
    /// toggles a user can mix and match.
    /// </summary>
    public static Tray.ScheduledScanService? ScheduledScan { get; private set; }

    /// <summary>
    /// App-side fan-out for toast activations. The one WinRT
    /// NotificationInvoked subscription is made in OnLaunched BEFORE
    /// Register() (a hard AppNotificationManager requirement - subscribing
    /// after Register() throws 0x80070490); services subscribe here
    /// instead, where attach/detach is unrestricted.
    /// </summary>
    public static Tray.NotificationDispatcher<Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs> Notifications { get; } = new();

    /// <summary>
    /// Set just before deliberately quitting (the tray menu's "Quit
    /// Quickening" - tray-menus 5a) so MainWindow's AppWindow.Closing
    /// handler lets the close through instead of hiding to tray. Nothing
    /// else in the app should ever set this.
    /// </summary>
    public static bool IsQuitting { get; set; }

    /// <summary>
    /// Persists the current value of Settings - call after mutating any of
    /// its properties (SettingsPage's toggle handlers do this immediately
    /// on change, not on some later "save" action).
    /// </summary>
    public static void SaveSettings() => SettingsInstance?.Save(Settings);

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Logger?.LogError($"UnhandledException: {e.Message}\n{e.Exception}");
            // A consumer utility should degrade (log + keep the window open)
            // rather than vanish: without Handled=true, any exception that
            // escapes an async void UI handler kills the process. This
            // includes LayoutCycleException - a swallowed layout cycle leaves
            // that one element in a degraded render, which is far better than
            // crashing the whole app. Known layout cycles are fixed at their
            // source (the History session list and the empty-bin dialog's
            // hold-fill); this is the backstop for any that slip through.
            e.Handled = true;
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var appDataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Quickening");
        System.IO.Directory.CreateDirectory(appDataDir);

        Logger = new Quickening.Core.Logging.FileLogger(System.IO.Path.Combine(appDataDir, "logs"));

        SettingsInstance = new Settings.SettingsService(System.IO.Path.Combine(appDataDir, "settings.json"));
        Settings = SettingsInstance.Load();

        var dbPath = System.IO.Path.Combine(appDataDir, "quickening.db");
        Store = new Quickening.Core.Storage.SqliteStore($"Data Source={dbPath}");
        Store.Initialize();

        Tray = new Tray.TrayIconService(Store);
        Watcher = new Tray.DuplicateWatcherService(Store, Tray);
        ScheduledScan = new Tray.ScheduledScanService(Store);

        // Registered once, app-wide, regardless of either Automation
        // toggle - Watcher's own duplicate-found toast and ScheduledScan's
        // completion toast both activate through the Notifications
        // dispatcher. The WinRT subscription below MUST come before
        // Register(): AppNotificationManager rejects any NotificationInvoked
        // subscription made after Register() with 0x80070490 ("Must register
        // event handlers before calling Register()"), which is why services
        // never touch the WinRT event directly. Register() itself needs
        // Microsoft.WindowsAppRuntime.Insights.Resource.dll, which the
        // WASDK 2.x self-contained payload omits - the csproj's
        // DeployWinAppRuntimeInsightsResourceDll target extracts it from the
        // runtime package's framework MSIX at build time. Caught anyway so a
        // future environment where registration fails can't take the rest of
        // the app down with it (toasts still post while the process lives;
        // only cold-launch-from-toast activation would be lost).
        try
        {
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.NotificationInvoked +=
                (_, e) => Notifications.Dispatch(e);
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            Logger?.LogError($"AppNotificationManager registration failed: {ex}");
        }

        _window = new MainWindow();
        MainWindowInstance = _window;
        MainWindowInstance.Closed += (_, _) =>
        {
            ScheduledScan?.Dispose();
            Watcher?.Dispose();
            Tray?.Dispose();
            Store?.Dispose();
            try
            {
                Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Unregister();
            }
            catch (Exception ex)
            {
                Logger?.LogError($"AppNotificationManager unregister failed: {ex}");
            }
        };
        _window.Activate();

        // Only actually starts anything if the user had already turned
        // watching on in a previous session - Start() itself is what makes
        // the icon appear/watcher run, this call just restores that state
        // on launch.
        if (Settings.WatchForNewDuplicates)
        {
            Tray.Start();
            Watcher.Start(Settings.WatchedFolderPaths);
        }

        // Independent of the Watch toggle - a user can enable scheduled
        // scans without ever turning on the tray watcher.
        ScheduledScan.Start();
    }
}
