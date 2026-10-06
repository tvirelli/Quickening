using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Quickening.App.Settings;
using Quickening.App.Updates;
using Windows.UI;
using WinRT.Interop;

namespace Quickening.App.Views;

/// <summary>
/// Settings, shown as a centered dialog over whatever screen it was opened
/// from (matching screen 2i's Confirm-removal dialog - dimmed backdrop,
/// same NebulaContentDialogStyle/pill button styles), not a separate
/// full-page navigation. Full redo per design-handoff/new-screens 4c/4d -
/// five groups (Scanning, Duplicates, Trusted folders, Automation, Privacy),
/// all built for real (no "Phase 2" placeholders): the settings persist and
/// are read by their real consumers even where the consuming feature itself
/// (scheduled scans, the tray watcher, usage-stats collection) is still
/// being built in a later phase.
///
/// Built in code, not a .xaml file, same convention as the rest of this
/// dialog's history - content here is dynamic enough (toggle handlers,
/// folder lists, a confirm sub-view) that a XAML file would need code-behind
/// wiring anyway. The protected-paths first-time confirmation (4d) swaps
/// this same ContentDialog's Content rather than opening a second
/// ContentDialog - WinUI3 only supports one ContentDialog open at a time per
/// XamlRoot, so a nested dialog would throw.
/// </summary>
internal static class SettingsDialog
{
    // Session-scoped (not persisted) - per 4d, the confirmation is asked
    // once per session, not once ever. Turning the setting off and back on
    // later in the same session doesn't re-ask.
    private static bool _protectedPathsConfirmedThisSession;

    // WinUI allows one open ContentDialog per XamlRoot; a double-click on
    // Home's gear (or opening Settings from the tray while any dialog is
    // already up) would make the second ShowAsync throw inside an async
    // void handler and crash the app.
    private static bool _isOpen;

    // True while the dialog is showing a legal document instead of the
    // settings groups. The dialog's Close button (and Esc) then navigate
    // BACK to settings rather than dismissing the whole dialog - closing
    // Settings entirely from inside a policy is not what a user expects.
    private static bool _documentViewOpen;

    // Scroll position of the settings groups, captured when a document
    // opens and restored when the rebuilt settings view loads - coming
    // "back" from a policy should land where the user left (the About
    // group), not jump to the top.
    private static ScrollViewer? _settingsScroller;
    private static double _settingsScrollOffset;

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        if (_isOpen)
        {
            return;
        }

        _isOpen = true;
        try
        {
            var resources = Application.Current.Resources;

            var dialog = new ContentDialog
            {
                Style = (Style)resources["NebulaContentDialogStyle"],
                Width = (double)resources["DialogWidthSettings"],
                CloseButtonText = "Done",
                CloseButtonStyle = (Style)resources["PillSecondaryButtonStyle"],
                XamlRoot = xamlRoot,
            };
            _settingsScrollOffset = 0;
            dialog.Content = BuildMainSettingsView(resources, dialog);

            _documentViewOpen = false;
            dialog.Closing += (d, args) =>
            {
                if (_documentViewOpen)
                {
                    args.Cancel = true;
                    _documentViewOpen = false;
                    d.Content = BuildMainSettingsView(resources, d);
                    d.CloseButtonText = "Done";
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another ContentDialog (confirm-removal, cloud warning, …) may
            // already be open on this XamlRoot - opening Settings then is a
            // silent no-op, not a crash.
            App.Logger?.LogError($"SettingsDialog could not be shown: {ex}");
        }
        finally
        {
            _isOpen = false;
        }
    }

    private static UIElement BuildMainSettingsView(ResourceDictionary resources, ContentDialog dialog)
    {
        var root = new StackPanel { Spacing = 0 };

        var header = new Grid();
        header.Children.Add(new TextBlock
        {
            Text = "Settings",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
        });
        root.Children.Add(header);

        var scroller = new ScrollViewer
        {
            MaxHeight = 560,
            Margin = new Thickness(0, 18, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _settingsScroller = scroller;
        if (_settingsScrollOffset > 0)
        {
            // disableAnimation: restoring a remembered position, not
            // performing a visible scroll.
            scroller.Loaded += (_, _) => scroller.ChangeView(null, _settingsScrollOffset, null, true);
        }
        var groups = new StackPanel { Spacing = 14 };
        groups.Children.Add(BuildScanningGroup(resources, dialog));
        groups.Children.Add(BuildDuplicatesGroup(resources));
        groups.Children.Add(BuildIgnoreListGroup(resources, dialog));
        groups.Children.Add(BuildTrustedFoldersGroup(resources));
        groups.Children.Add(BuildAutomationGroup(resources));
        groups.Children.Add(BuildAboutGroup(resources, dialog));
        scroller.Content = groups;
        root.Children.Add(scroller);

        return root;
    }

    // ===== Ignore list group =====

    private static UIElement BuildIgnoreListGroup(ResourceDictionary resources, ContentDialog dialog)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = "Files and folders you've chosen to skip. Ignored items never appear in review or future scans.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        // Same recipe as the About group's "Check for updates" button so the
        // dialog's pill buttons all read identically.
        var button = new Button
        {
            Content = "Manage Ignored Files",
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Foreground = (Brush)resources["TextSecondaryBrush"],
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Controls.PillCornerRadius.SetEnabled(button, true);
        button.Click += (_, _) =>
        {
            dialog.Hide();
            ((MainWindow)App.MainWindowInstance!).ShowManageIgnored();
        };
        stack.Children.Add(button);

        return BuildGroupCard(resources, "Ignore list", stack);
    }

    // ===== Scanning group =====

    private static UIElement BuildScanningGroup(ResourceDictionary resources, ContentDialog dialog)
    {
        var scanHiddenToggle = BuildToggleRow(
            resources,
            "Scan hidden files",
            "Off by default, so scans skip hidden files and folders (not system-protected "
                + "files - those are never scanned regardless of this setting).",
            App.Settings.ScanHiddenFiles,
            isOn =>
            {
                App.Settings.ScanHiddenFiles = isOn;
                App.SaveSettings();
            });

        UIElement allowProtectedPathsToggle = null!;
        allowProtectedPathsToggle = BuildToggleRowWithBadge(
            resources,
            "Allow scanning protected system paths",
            "POWER USER",
            "Lets scans reach folders Windows depends on, like C:\\Windows and Program Files. "
                + "Asks you to confirm the first time.",
            App.Settings.AllowProtectedPaths,
            out var protectedPathsToggle);
        protectedPathsToggle.Toggled += (_, _) =>
        {
            if (protectedPathsToggle.IsOn && !_protectedPathsConfirmedThisSession)
            {
                dialog.Content = BuildProtectedPathsConfirmView(resources, dialog);
                return;
            }

            App.Settings.AllowProtectedPaths = protectedPathsToggle.IsOn;
            App.SaveSettings();
        };

        var paranoidToggle = BuildToggleRow(
            resources,
            "Paranoid mode",
            "Double-checks every match byte-for-byte. Duplicates are already detected "
                + "extremely reliably - this just adds reassurance, and slower scans.",
            App.Settings.ParanoidMode,
            isOn =>
            {
                App.Settings.ParanoidMode = isOn;
                App.SaveSettings();
            });

        return BuildGroupCard(resources, "Scanning", scanHiddenToggle, allowProtectedPathsToggle, paranoidToggle);
    }

    private static UIElement BuildProtectedPathsConfirmView(ResourceDictionary resources, ContentDialog dialog)
    {
        var root = new StackPanel { Spacing = 0 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        titleRow.Children.Add(new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0x8A, 0x5C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0x8A, 0x5C)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "⚠", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "Scan Windows' own folders?",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(titleRow);

        root.Children.Add(new TextBlock
        {
            Text = "This lets scans reach places like C:\\Windows and Program Files - folders that can "
                + "contain files Windows needs to run. Removing the wrong file there can break programs "
                + "or Windows itself.\n\nQuickening will still warn you about risky files and still asks "
                + "before removing anything. Only continue if you know what you're doing.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 14.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 16, 0, 0),
        });

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };

        var cancelButton = new Button
        {
            Content = "Keep them off-limits",
            Style = (Style)resources["PillSecondaryButtonStyle"],
        };
        cancelButton.Click += (_, _) => dialog.Content = BuildMainSettingsView(resources, dialog);

        var confirmButton = new Button
        {
            Content = "I understand — allow it",
            Padding = new Thickness(30, 13, 30, 13),
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xFF, 0x9A, 0x6C), Offset = 0 },
                    new GradientStop { Color = Color.FromArgb(0xFF, 0xE0, 0x6A, 0x38), Offset = 1 },
                },
            },
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x14, 0x08)),
            BorderThickness = new Thickness(0),
        };
        confirmButton.SetValue(Controls.PillCornerRadius.EnabledProperty, true);
        confirmButton.Click += (_, _) =>
        {
            _protectedPathsConfirmedThisSession = true;
            App.Settings.AllowProtectedPaths = true;
            App.SaveSettings();
            dialog.Content = BuildMainSettingsView(resources, dialog);
        };

        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(confirmButton);
        root.Children.Add(buttonRow);

        root.Children.Add(new TextBlock
        {
            Text = "Asked once. You can turn this off again anytime.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextDisabledHintBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        });

        return root;
    }

    // ===== Duplicates group =====

    private static UIElement BuildDuplicatesGroup(ResourceDictionary resources)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = "\"Select Recommended\" keeps the…",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        content.Children.Add(new TextBlock
        {
            Text = "Which copy stays unticked when we pre-select a duplicate group for you.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var pillBorder = new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Padding = new Thickness(5),
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = pillRow,
        };
        pillBorder.SetValue(Controls.PillCornerRadius.EnabledProperty, true);

        var options = new (KeepRule Rule, string Label)[]
        {
            (KeepRule.Newest, "Newest copy"),
            (KeepRule.Oldest, "Oldest copy"),
            (KeepRule.ShortestPath, "Shortest path"),
        };

        var buttons = new List<(KeepRule Rule, Button Button)>();
        foreach (var (rule, label) in options)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(24, 9, 24, 9),
                BorderThickness = new Thickness(0),
                FontFamily = (FontFamily)resources["DisplayFontFamily"],
                FontSize = 13.5,
            };
            button.SetValue(Controls.PillCornerRadius.EnabledProperty, true);
            buttons.Add((rule, button));
            pillRow.Children.Add(button);
        }

        void UpdateSelection()
        {
            foreach (var (rule, button) in buttons)
            {
                var isSelected = rule == App.Settings.PreferredKeepRule;
                button.Background = isSelected
                    ? (Brush)resources["CtaSmallGradientBrush"]
                    : new SolidColorBrush(Colors.Transparent);
                button.Foreground = isSelected
                    ? new SolidColorBrush(Colors.White)
                    : (Brush)resources["TextMutedBrush"];
                button.FontWeight = isSelected ? FontWeights.Bold : FontWeights.SemiBold;
            }
        }

        foreach (var (rule, button) in buttons)
        {
            button.Click += (_, _) =>
            {
                App.Settings.PreferredKeepRule = rule;
                App.SaveSettings();
                UpdateSelection();
            };
        }

        UpdateSelection();
        content.Children.Add(pillBorder);

        return BuildGroupCard(resources, "Duplicates", content);
    }

    // ===== Trusted folders group =====

    private static UIElement BuildTrustedFoldersGroup(ResourceDictionary resources)
    {
        var intro = new TextBlock
        {
            Text = "Files in these folders never get the risky-file warning - for folders you know are "
                + "safe, like VM disks or database backups.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        };

        var editor = BuildFolderListEditor(
            resources,
            App.Settings.TrustedFolderPaths,
            "No trusted folders yet — everything gets the standard safety checks.");

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(intro);
        content.Children.Add(editor);

        return BuildGroupCard(resources, "Trusted folders", content);
    }

    // ===== Automation group =====

    private static UIElement BuildAutomationGroup(ResourceDictionary resources)
    {
        var scheduleContent = new StackPanel { Spacing = 10 };
        var scheduleToggle = BuildToggleRow(
            resources,
            "Scheduled scan",
            "Run a scan automatically and get a summary when it's done.",
            App.Settings.ScheduledScanEnabled,
            isOn =>
            {
                App.Settings.ScheduledScanEnabled = isOn;
                App.SaveSettings();
            },
            out var scheduleToggleSwitch);

        var scheduleControls = BuildScheduleControls(resources);
        scheduleControls.Visibility = App.Settings.ScheduledScanEnabled ? Visibility.Visible : Visibility.Collapsed;
        scheduleToggleSwitch.Toggled += (_, _) =>
            scheduleControls.Visibility = scheduleToggleSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;

        scheduleContent.Children.Add(scheduleToggle);
        scheduleContent.Children.Add(scheduleControls);

        var watchContent = new StackPanel { Spacing = 10 };
        var watchToggle = BuildToggleRow(
            resources,
            "Watch for new duplicates",
            "Quickening sits in the system tray and quietly flags duplicates as they arrive.",
            App.Settings.WatchForNewDuplicates,
            isOn =>
            {
                App.Settings.WatchForNewDuplicates = isOn;
                App.SaveSettings();

                // Actually create/tear down the tray icon and background
                // watcher here, not just the setting - App.Tray.Start()/Stop()
                // and App.Watcher.Start()/Stop() are idempotent, so this is
                // the one place both this dialog and App.xaml.cs's own
                // launch-time check need to call into.
                if (isOn)
                {
                    App.Tray?.Start();
                    App.Watcher?.Start(App.Settings.WatchedFolderPaths);
                }
                else
                {
                    App.Tray?.Stop();
                    App.Watcher?.Stop();
                }
            },
            out var watchToggleSwitch);

        var watchedFoldersEditor = BuildFolderListEditor(
            resources,
            App.Settings.WatchedFolderPaths,
            "No watched folders yet — add one to start watching.",
            onChanged: () =>
            {
                // Re-Start() rebuilds the FileSystemWatcher list from the
                // current WatchedFolderPaths - only meaningful while
                // watching is actually on (Start() no-ops on an empty tray
                // otherwise harmlessly, since Watching only ever starts if
                // WatchForNewDuplicates is on to begin with).
                if (App.Settings.WatchForNewDuplicates)
                {
                    App.Watcher?.Start(App.Settings.WatchedFolderPaths);
                }
            });
        watchedFoldersEditor.Visibility = App.Settings.WatchForNewDuplicates ? Visibility.Visible : Visibility.Collapsed;
        watchToggleSwitch.Toggled += (_, _) =>
            watchedFoldersEditor.Visibility = watchToggleSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;

        watchContent.Children.Add(watchToggle);
        watchContent.Children.Add(watchedFoldersEditor);

        return BuildGroupCard(resources, "Automation", scheduleContent, watchContent);
    }

    private static UIElement BuildScheduleControls(ResourceDictionary resources)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };

        var frequencyCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<ScheduledScanFrequency>(),
            SelectedItem = App.Settings.ScheduledScanFrequency,
        };
        frequencyCombo.SelectionChanged += (_, _) =>
        {
            if (frequencyCombo.SelectedItem is ScheduledScanFrequency frequency)
            {
                App.Settings.ScheduledScanFrequency = frequency;
                App.SaveSettings();
            }
        };

        var modeCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<ScheduledScanMode>(),
            SelectedItem = App.Settings.ScheduledScanMode,
        };
        modeCombo.SelectionChanged += (_, _) =>
        {
            if (modeCombo.SelectedItem is ScheduledScanMode mode)
            {
                App.Settings.ScheduledScanMode = mode;
                App.SaveSettings();
            }
        };

        var targetButton = new Button
        {
            Content = string.IsNullOrEmpty(App.Settings.ScheduledScanTargetPath)
                ? "Choose a folder…"
                : GetFolderLabel(App.Settings.ScheduledScanTargetPath),
            Padding = new Thickness(14, 8, 14, 8),
            FontFamily = (FontFamily)resources["MonoFontFamily"],
            FontSize = 12,
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Foreground = (Brush)resources["TextSecondaryBrush"],
            CornerRadius = (CornerRadius)resources["RadiusInput"],
        };
        targetButton.Click += async (_, _) =>
        {
            var folder = await PickFolderAsync();
            if (folder is null)
            {
                return;
            }

            App.Settings.ScheduledScanTargetPath = folder;
            App.SaveSettings();
            targetButton.Content = GetFolderLabel(folder);
        };

        row.Children.Add(frequencyCombo);
        row.Children.Add(modeCombo);
        row.Children.Add(targetButton);

        return row;
    }

    // ===== About group =====

    private static UIElement BuildAboutGroup(ResourceDictionary resources, ContentDialog dialog)
    {
        var version = App.Updater?.CurrentVersion
            ?? typeof(SettingsDialog).Assembly.GetName().Version?.ToString(3)
            ?? "dev";
        var versionRow = new TextBlock
        {
            Text = $"Quickening {version} — © Tony Virelli. Use of this app is governed by the license agreement below.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        };

        var updateRow = BuildUpdateCheckRow(resources);

        // All three documents ship beside the exe and render in-app (a
        // scrollable view swapped into this same dialog), so they always
        // match THIS installed version and work offline - CCPA's 2026
        // regulations also expect the privacy policy reachable from an
        // app's settings menu.
        var eulaRow = BuildLinkRow(resources, "License agreement",
            "The terms you accepted by installing Quickening.",
            () => ShowDocument(resources, dialog, "License agreement", "EULA.md"));
        var privacyRow = BuildLinkRow(resources, "Privacy policy",
            "Short version: the app collects nothing.",
            () => ShowDocument(resources, dialog, "Privacy policy", "PRIVACY.md"));
        var noticesRow = BuildLinkRow(resources, "Third-party notices",
            "Open-source components and fonts Quickening is built with.",
            () => ShowDocument(resources, dialog, "Third-party notices", "THIRD-PARTY-NOTICES.md"));

        return BuildGroupCard(resources, "About", versionRow, updateRow, eulaRow, privacyRow, noticesRow);
    }

    // "Check for updates" row: a label plus a button that runs a manual check
    // through the same UpdateService the launch-time check uses. Status text
    // reflects the result. Reuses the plain hover-row look via a Grid.
    private static UIElement BuildUpdateCheckRow(ResourceDictionary resources)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { Spacing = 3 };
        textStack.Children.Add(new TextBlock
        {
            Text = "Updates",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        var status = new TextBlock
        {
            Text = "Quickening updates itself automatically.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        textStack.Children.Add(status);
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        var button = new Button
        {
            Content = "Check for updates",
            Padding = new Thickness(14, 8, 14, 8),
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 13,
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Foreground = (Brush)resources["TextSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetValue(Controls.PillCornerRadius.EnabledProperty, true);
        button.Click += async (_, _) =>
        {
            if (App.Updater is null)
            {
                status.Text = "Updates are unavailable in this build.";
                return;
            }

            button.IsEnabled = false;
            status.Text = "Checking…";
            var result = await App.Updater.CheckNowAsync();
            status.Text = result switch
            {
                UpdateCheckResult.UpToDate => "You're on the latest version.",
                UpdateCheckResult.UpdateStaged => "Update downloaded — it'll finish next time you reopen Quickening.",
                _ => "Couldn't check right now. Try again later.",
            };
            button.IsEnabled = true;
        };
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return grid;
    }

    private static UIElement BuildLinkRow(
        ResourceDictionary resources, string title, string description, Action onOpen)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { Spacing = 3 };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        var chevron = new TextBlock
        {
            Text = "", // ChevronRight - docs open in-app now, no
                        // "external link" glyph; this just marks the
                        // row as navigable.
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Foreground = (Brush)resources["TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        // A hand-rolled hover row instead of a Button: the default Button
        // template only paints its hover state behind the measured content,
        // not the full stretched row, which looked like the highlight was
        // "behind the text" only.
        var row = new Border
        {
            Child = grid,
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(-10, -6, -10, -6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var hoverBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        var restBrush = new SolidColorBrush(Colors.Transparent);
        row.PointerEntered += (_, _) => row.Background = hoverBrush;
        row.PointerExited += (_, _) => row.Background = restBrush;
        row.Tapped += (_, _) => onOpen();
        return row;
    }

    // ===== In-dialog document viewer (EULA / privacy / notices) =====

    private static void ShowDocument(ResourceDictionary resources, ContentDialog dialog, string title, string fileName)
    {
        // Same content-swap trick as the protected-paths confirm view -
        // WinUI3 allows one ContentDialog per XamlRoot, so a "second dialog"
        // is really this dialog wearing different content. The Closing
        // handler wired in ShowAsync turns the dialog's Close button into
        // "back to settings" while _documentViewOpen is set.
        _settingsScrollOffset = _settingsScroller?.VerticalOffset ?? 0;
        dialog.Content = BuildDocumentView(resources, dialog, title, fileName);
        dialog.CloseButtonText = "Back";
        _documentViewOpen = true;
    }

    private static UIElement BuildDocumentView(ResourceDictionary resources, ContentDialog dialog, string title, string fileName)
    {
        var root = new StackPanel { Spacing = 0 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var backButton = new Button
        {
            Content = new TextBlock
            {
                Text = "", // Back glyph
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 15,
            },
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        backButton.Click += (_, _) =>
        {
            _documentViewOpen = false;
            dialog.Content = BuildMainSettingsView(resources, dialog);
            dialog.CloseButtonText = "Done";
        };
        titleRow.Children.Add(backButton);
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(titleRow);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, fileName));
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"SettingsDialog: could not read '{fileName}': {ex}");
            lines = new[] { $"This document could not be loaded from the installation folder ({fileName})." };
        }

        var scroller = new ScrollViewer
        {
            MaxHeight = 560,
            Margin = new Thickness(0, 18, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        scroller.Content = new Border
        {
            CornerRadius = (CornerRadius)resources["RadiusCard"],
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18),
            Child = Controls.MarkdownLiteRenderer.Render(resources, lines),
        };
        root.Children.Add(scroller);

        return root;
    }

    // ===== Shared building blocks =====

    private static Border BuildGroupCard(ResourceDictionary resources, string label, params UIElement[] rows)
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 11.5,
            FontWeight = FontWeights.ExtraBold,
            CharacterSpacing = 180,
            Foreground = (Brush)resources["AccentLightBrush"],
        });

        for (var i = 0; i < rows.Length; i++)
        {
            stack.Children.Add(rows[i]);
            if (i < rows.Length - 1)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)resources["SurfaceCardBorderBrush"],
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }

        return new Border
        {
            CornerRadius = (CornerRadius)resources["RadiusCard"],
            Background = (Brush)resources["SurfaceCardBrush"],
            BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18),
            Child = stack,
        };
    }

    private static UIElement BuildToggleRow(
        ResourceDictionary resources, string title, string description, bool initialValue, Action<bool> onToggled) =>
        BuildToggleRow(resources, title, description, initialValue, onToggled, out _);

    private static UIElement BuildToggleRow(
        ResourceDictionary resources, string title, string description, bool initialValue, Action<bool> onToggled,
        out ToggleSwitch toggleSwitch)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { Spacing = 3 };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        var toggle = new ToggleSwitch
        {
            IsOn = initialValue,
            OnContent = string.Empty,
            OffContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // Named for screen readers - the visible title is a separate TextBlock,
        // so the switch itself was announced unlabeled (QA-6).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, title);
        toggle.Toggled += (_, _) => onToggled(toggle.IsOn);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        toggleSwitch = toggle;
        return grid;
    }

    // Same as BuildToggleRow but with a small badge (e.g. "POWER USER") next
    // to the title - the toggle's Toggled handler is wired by the caller
    // (via the out param) rather than taking an Action here, since the
    // protected-paths toggle needs to intercept the event to show a confirm
    // sub-view before actually committing the change.
    private static UIElement BuildToggleRowWithBadge(
        ResourceDictionary resources, string title, string badgeText, string description, bool initialValue,
        out ToggleSwitch toggleSwitch)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { Spacing = 3 };
        // A Grid, not a horizontal StackPanel: a StackPanel measures its
        // children with infinite width, so a long title ("Allow scanning
        // protected system paths") pushed the badge past the column edge and
        // it was clipped to nothing (QA-15). Here the title wraps and the badge
        // keeps its own Auto column.
        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        // Warning* tokens (these hexes were byte-for-byte duplicates of them),
        // and the shared pill helper - CornerRadius=999 bulges into an ellipse
        // on short elements in WinUI (it doesn't clamp to height/2 like CSS).
        var badge = new Border
        {
            Padding = new Thickness(9, 2, 9, 2),
            Background = (Brush)resources["WarningFillBrush"],
            BorderBrush = (Brush)resources["WarningFillBorderBrush"],
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badgeText,
                FontFamily = (FontFamily)resources["BodyFontFamily"],
                FontSize = 10,
                FontWeight = FontWeights.ExtraBold,
                Foreground = (Brush)resources["WarningBrush"],
            },
        };
        Controls.PillCornerRadius.SetEnabled(badge, true);
        Grid.SetColumn(badge, 1);
        titleRow.Children.Add(badge);
        textStack.Children.Add(titleRow);
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        var toggle = new ToggleSwitch
        {
            IsOn = initialValue,
            OnContent = string.Empty,
            OffContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // Named for screen readers - the visible title is a separate TextBlock,
        // so the switch itself was announced unlabeled (QA-6).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, title);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        toggleSwitch = toggle;
        return grid;
    }

    // Reused for both TrustedFolderPaths and WatchedFolderPaths - paths is a
    // live reference to the real AppSettings list, mutated in place and
    // persisted immediately on every add/remove.
    private static UIElement BuildFolderListEditor(ResourceDictionary resources, List<string> paths, string emptyText, Action? onChanged = null)
    {
        var container = new StackPanel { Spacing = 8 };

        void Rebuild()
        {
            container.Children.Clear();

            if (paths.Count == 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = emptyText,
                    FontFamily = (FontFamily)resources["BodyFontFamily"],
                    FontSize = 12.5,
                    Foreground = (Brush)resources["TextFaintBrush"],
                });
            }

            foreach (var path in paths.ToList())
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var pathText = new TextBlock
                {
                    Text = path,
                    FontFamily = (FontFamily)resources["MonoFontFamily"],
                    FontSize = 12.5,
                    Foreground = (Brush)resources["TextSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(pathText, 0);

                var removeButton = new Button
                {
                    Content = "Remove",
                    Padding = new Thickness(10, 4, 10, 4),
                    FontFamily = (FontFamily)resources["BodyFontFamily"],
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = (Brush)resources["TextFaintBrush"],
                };
                removeButton.Click += (_, _) =>
                {
                    paths.Remove(path);
                    App.SaveSettings();
                    onChanged?.Invoke();
                    Rebuild();
                };
                Grid.SetColumn(removeButton, 1);

                var rowBorder = new Border
                {
                    Padding = new Thickness(16, 11, 8, 11),
                    Background = (Brush)resources["SurfaceCardBrush"],
                    BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Child = row,
                };
                row.Children.Add(pathText);
                row.Children.Add(removeButton);

                container.Children.Add(rowBorder);
            }

            var addButton = new Button
            {
                Content = "+ Add a folder…",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(0, 11, 0, 11),
                FontFamily = (FontFamily)resources["BodyFontFamily"],
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = (Brush)resources["SurfaceCardBorderBrush"],
                BorderThickness = new Thickness(1.5),
                Foreground = (Brush)resources["TextMutedBrush"],
                CornerRadius = new CornerRadius(12),
            };
            addButton.Click += async (_, _) =>
            {
                var folder = await PickFolderAsync();
                if (folder is null || paths.Contains(folder, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                paths.Add(folder);
                App.SaveSettings();
                onChanged?.Invoke();
                Rebuild();
            };
            container.Children.Add(addButton);
        }

        Rebuild();
        return container;
    }

    private static async Task<string?> PickFolderAsync()
    {
        var windowHandle = WindowNative.GetWindowHandle(App.MainWindowInstance);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        var picker = new FolderPicker(windowId);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    // Same as HomePage.GetFolderLabel - duplicated rather than shared since
    // that one's private to HomePage and this dialog has no dependency on
    // HomePage otherwise.
    private static string GetFolderLabel(string path)
    {
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
