using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Models;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Quickening.App.Controls;

public sealed partial class MediaComparisonViewer : UserControl
{
    public event EventHandler? CloseRequested;

    public MediaComparisonViewer()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Populates the viewer with every file in one group - ShowGroup should
    /// only be called for media groups (Image/Video/Audio), never for a
    /// group of documents/archives/etc. where there's nothing to preview.
    /// Makes this control Visible itself as its first action - callers
    /// don't need to (and shouldn't redundantly) set Visibility afterward.
    /// isExactDuplicateGroup distinguishes the two callers' very different
    /// groups: Results' Duplicates groups are content-identical by
    /// construction ("Same file, N homes"), but Results' Looks-alike-photos
    /// groups (new-screens 4k) are only visually similar, not byte-identical -
    /// claiming "same file" there would be actively misleading.
    /// </summary>
    public void ShowGroup(IReadOnlyList<SelectableFile> groupFiles, bool isExactDuplicateGroup = true)
    {
        Visibility = Visibility.Visible;

        DisposeControllers();
        PanelsGrid.Children.Clear();
        PanelsGrid.RowDefinitions.Clear();
        PanelsGrid.ColumnDefinitions.Clear();

        var representative = groupFiles.FirstOrDefault(f => f.IsKeepRecommended) ?? groupFiles.FirstOrDefault();
        var fileName = representative is null ? string.Empty : System.IO.Path.GetFileName(representative.Path);
        TitleText.Text = groupFiles.Count <= 1
            ? fileName
            : isExactDuplicateGroup
                ? $"{fileName} — {groupFiles.Count} copies, compared"
                : $"{groupFiles.Count} similar photos, compared";
        SubtitleText.Text = groupFiles.Count <= 1
            ? "Preview and confirm before this one comes home."
            : isExactDuplicateGroup
                ? $"Same file, {groupFiles.Count} homes. Untick the one you want to keep."
                : "These look alike but aren't identical - take a look before deciding what to keep.";

        LayoutPanels(groupFiles);
    }

    // Builds the responsive tile grid: min(count,4) star columns, ceil(count
    // / cols) star rows, so 1 item fills the whole area (large, centered),
    // 2/3/4 split it 50/33/25% and 5+ wrap at 4 per row. Star sizing (not a
    // fixed per-tile width) is what makes each tile fluidly fill its cell and
    // resize with the window.
    private void LayoutPanels(IReadOnlyList<SelectableFile> groupFiles)
    {
        var count = Math.Max(groupFiles.Count, 1);
        var cols = Math.Min(count, 4);
        var rows = (count + cols - 1) / cols;

        for (var c = 0; c < cols; c++)
        {
            PanelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < rows; r++)
        {
            PanelsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }

        for (var i = 0; i < groupFiles.Count; i++)
        {
            var panel = BuildPanel(groupFiles[i]);
            Grid.SetColumn(panel, i % cols);
            Grid.SetRow(panel, i / cols);
            PanelsGrid.Children.Add(panel);
        }
    }

    // Every video/audio panel's owned MediaPlayer + wired controls, tracked
    // so DisposeControllers can tear each down in the safe order (a stray
    // decoder session or a player disposed while still attached to its
    // element is a known WinUI crash). Images register nothing here.
    private readonly List<MediaController> _controllers = new();

    private void DisposeControllers()
    {
        foreach (var controller in _controllers)
        {
            controller.Dispose();
        }

        _controllers.Clear();
    }

    // One tile: a Border card whose inner Grid stacks the media (star row,
    // fills the tile), the docked transport controls (video/audio only), and
    // the checkbox/name/size footer. The media letterboxes (Uniform) inside
    // its own dark host so nothing is ever cropped, and the controls live in
    // their own row BELOW it rather than overlaying it.
    private FrameworkElement BuildPanel(SelectableFile file)
    {
        var resources = Application.Current.Resources;

        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(10),
            CornerRadius = (CornerRadius)resources["RadiusCard"],
            Background = (Brush)resources["BgElevatedBrush"],
            BorderThickness = new Thickness(1.5),
            // Success-green border for the file being kept (IsKeepRecommended,
            // the group's newest file), accent-blue for every other
            // (selected-for-removal) copy - matches 2j exactly.
            BorderBrush = (Brush)resources[file.IsKeepRecommended
                ? "MediaPreviewKeptBorderBrush"
                : "MediaPreviewSelectedBorderBrush"],
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // media
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // transport controls
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // footer
        border.Child = grid;

        // Dark media host - the media letterboxes onto this so the fit bars
        // read as intentional matting rather than clipped content.
        var mediaHost = new Border
        {
            Margin = new Thickness(10, 10, 10, 0),
            MinHeight = 120,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x07, 0x0B, 0x18)),
        };
        Grid.SetRow(mediaHost, 0);
        grid.Children.Add(mediaHost);

        if (file.Category is MimeCategory.Video or MimeCategory.Audio)
        {
            var controller = new MediaController(file, mediaHost, DispatcherQueue, resources);
            _controllers.Add(controller);

            mediaHost.Child = controller.Element;

            Grid.SetRow(controller.Controls, 1);
            grid.Children.Add(controller.Controls);
        }
        else if (file.Category == MimeCategory.Image)
        {
            mediaHost.Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file.Path)),
                // Uniform + Stretch alignment fits the whole image inside the
                // host, centered and letterboxed - never cropped, however wide
                // or tall it is.
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
        }
        else
        {
            mediaHost.Child = new TextBlock
            {
                Text = "No preview available",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)resources["TextMutedBrush"],
            };
        }

        grid.Children.Add(BuildFooter(file, resources));
        return border;
    }

    private static FrameworkElement BuildFooter(SelectableFile file, ResourceDictionary resources)
    {
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Padding = new Thickness(16, 14, 16, 16),
        };
        Grid.SetRow(footer, 2);

        var checkBox = new CheckBox
        {
            Width = 20,
            Height = 20,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };
        checkBox.SetBinding(CheckBox.IsCheckedProperty, new Microsoft.UI.Xaml.Data.Binding
        {
            Source = file,
            Path = new PropertyPath(nameof(SelectableFile.IsSelected)),
            Mode = Microsoft.UI.Xaml.Data.BindingMode.TwoWay,
        });
        footer.Children.Add(checkBox);

        var nameStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(nameStack);

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameStack.Children.Add(nameRow);

        nameRow.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetFileName(file.Path),
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (file.IsKeepRecommended)
        {
            nameRow.Children.Add(BuildKeepPill(resources));
        }

        nameStack.Children.Add(new TextBlock
        {
            // Local time, matching LastWriteDateConverter - raw UTC can be a
            // day off from what Explorer shows for the same file.
            Text = $"{FileSizeFormatter.Format(file.SizeBytes)} · modified {DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime():MMM d, yyyy}",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextFaintBrush"],
        });

        return footer;
    }

    // Mirrors the "KEEP" pill ResultsPage.xaml declares inline for the grouped
    // Results row (SuccessFillBrush/SuccessFillBorderBrush/FontSizeMicroPill),
    // built here in code since this whole panel is code-behind, reusing the
    // exact same resource keys so both pills read as one visual language.
    private static Border BuildKeepPill(ResourceDictionary resources)
    {
        var border = new Border
        {
            Padding = new Thickness(9, 2, 9, 2),
            Background = (Brush)resources["SuccessFillBrush"],
            BorderBrush = (Brush)resources["SuccessFillBorderBrush"],
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "KEEP",
                FontFamily = (FontFamily)resources["BodyFontFamily"],
                FontSize = (double)resources["FontSizeMicroPill"],
                FontWeight = FontWeights.ExtraBold,
                Foreground = (Brush)resources["SuccessBrush"],
            },
        };
        PillCornerRadius.SetEnabled(border, true);
        return border;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DisposeControllers();
        PanelsGrid.Children.Clear();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Owns one video/audio panel's MediaPlayer and the custom, always-visible
    /// transport controls docked below the media (play/pause, a seek slider,
    /// and a current/total time readout). We drive these ourselves rather than
    /// using MediaPlayerElement.AreTransportControlsEnabled because the
    /// built-in overlay controls sit ON TOP of the video (covering part of it)
    /// and auto-hide; the built-in end-of-media terminal state also faulted
    /// natively (see OnMediaEnded). All MediaPlayer/PlaybackSession events fire
    /// on background threads, so every UI touch marshals through the captured
    /// DispatcherQueue.
    /// </summary>
    private sealed class MediaController
    {
        private const string PlayGlyph = "▶";   // ▶
        private const string PauseGlyph = "⏸";  // ⏸

        public MediaPlayerElement Element { get; }
        public FrameworkElement Controls { get; }

        private readonly MediaPlayer _player;
        private readonly MediaPlaybackSession _session;
        private readonly DispatcherQueue _dispatcher;
        private readonly Border _mediaHost;
        private readonly ResourceDictionary _resources;
        private readonly string _path;
        private readonly Button _playPause;
        private readonly Slider _seek;
        private readonly TextBlock _timeText;

        // Guards the two-way tug between the player (which pushes position
        // into the slider) and the user (who drags the slider to seek):
        // _suppressSeek is set while WE write _seek.Value from a position
        // update so its ValueChanged doesn't loop back into a seek, and
        // _scrubbing pauses those position-driven writes while the user drags.
        private bool _suppressSeek;
        private bool _scrubbing;
        private TimeSpan _duration;

        public MediaController(SelectableFile file, Border mediaHost, DispatcherQueue dispatcher, ResourceDictionary resources)
        {
            _dispatcher = dispatcher;
            _mediaHost = mediaHost;
            _resources = resources;
            _path = file.Path;

            _player = new MediaPlayer
            {
                Source = MediaSource.CreateFromUri(new Uri(file.Path)),
                AutoPlay = false,
            };
            _session = _player.PlaybackSession;

            Element = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Element.SetMediaPlayer(_player);

            _playPause = new Button
            {
                Content = PlayGlyph,
                Width = 36,
                Height = 36,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(18),
                Background = (Brush)resources["AccentBrush"],
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_playPause, "Play");
            _playPause.Click += OnPlayPauseClick;

            _seek = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                StepFrequency = 0.1,
                IsThumbToolTipEnabled = false,
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _seek.ValueChanged += OnSeekValueChanged;
            // The slider swallows pointer events internally, so observe them
            // with handledEventsToo to know when the user is actively dragging.
            _seek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSeekPressed), true);
            _seek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSeekReleased), true);
            _seek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnSeekReleased), true);

            _timeText = new TextBlock
            {
                Text = "0:00 / 0:00",
                FontFamily = (FontFamily)resources["MonoFontFamily"],
                FontSize = 12,
                Foreground = (Brush)resources["TextMutedBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 92,
                TextAlignment = TextAlignment.Right,
            };

            var controls = new Grid { Padding = new Thickness(12, 8, 12, 12), ColumnSpacing = 12 };
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_playPause, 0);
            Grid.SetColumn(_seek, 1);
            Grid.SetColumn(_timeText, 2);
            controls.Children.Add(_playPause);
            controls.Children.Add(_seek);
            controls.Children.Add(_timeText);
            Controls = controls;

            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;
            _session.PlaybackStateChanged += OnPlaybackStateChanged;
            _session.PositionChanged += OnPositionChanged;
        }

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_session.PlaybackState == MediaPlaybackState.Playing)
                {
                    _player.Pause();
                }
                else
                {
                    _player.Play();
                }
            }
            catch (Exception ex)
            {
                App.Logger?.LogError($"Play/pause failed: {ex}");
            }
        }

        private void OnSeekPressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

        private void OnSeekReleased(object sender, PointerRoutedEventArgs e) => _scrubbing = false;

        private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSeek)
            {
                return;
            }

            try
            {
                _session.Position = TimeSpan.FromSeconds(e.NewValue);
            }
            catch (Exception ex)
            {
                App.Logger?.LogError($"Seek failed: {ex}");
            }
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            _dispatcher.TryEnqueue(() =>
            {
                _duration = _session.NaturalDuration;
                var totalSeconds = _duration.TotalSeconds;
                if (totalSeconds > 0)
                {
                    _suppressSeek = true;
                    _seek.Maximum = totalSeconds;
                    _suppressSeek = false;
                    _seek.IsEnabled = true;
                }

                UpdateTimeText(TimeSpan.Zero);
            });
        }

        private void OnPositionChanged(MediaPlaybackSession sender, object args)
        {
            var position = sender.Position;
            _dispatcher.TryEnqueue(() =>
            {
                if (!_scrubbing)
                {
                    _suppressSeek = true;
                    _seek.Value = Math.Clamp(position.TotalSeconds, _seek.Minimum, _seek.Maximum);
                    _suppressSeek = false;
                }

                UpdateTimeText(position);
            });
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            var isPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
            _dispatcher.TryEnqueue(() =>
            {
                _playPause.Content = isPlaying ? PauseGlyph : PlayGlyph;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_playPause, isPlaying ? "Pause" : "Play");
            });
        }

        // MediaPlayer raises MediaEnded on a background thread; the built-in
        // transport controls' terminal "ended" state transition can fault
        // natively there (bypassing the app's UnhandledException handler and
        // taking the whole process down). We don't use those controls, but we
        // still pull the session back to a non-terminal state - paused at
        // 0:00, ready to replay - on the UI thread, so end-of-playback is a
        // clean, crash-free reset.
        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    _player.Pause();
                    _session.Position = TimeSpan.Zero;
                    _playPause.Content = PlayGlyph;
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_playPause, "Play");
                }
                catch (Exception ex)
                {
                    App.Logger?.LogError($"Resetting media player after end failed: {ex}");
                }
            });
        }

        // Media Foundation (what MediaPlayerElement decodes through) only plays
        // codecs Windows actually ships. A .mov is just a container - if the
        // codec inside is HEVC (needs a codec extension Windows omits by
        // default) or ProRes (Media Foundation can't decode it at all), the
        // source fails to open with SourceNotSupported even though the file is
        // perfectly fine (VLC plays it because it bundles its own decoders).
        // Rather than a dead "unplayable" label, swap the preview for a plain-
        // language explanation plus the two things that still help the user
        // decide which copy to keep: open it in whatever app normally plays it,
        // or reveal it in its folder.
        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            App.Logger?.LogError($"Media playback failed: {args.ErrorMessage} ({args.Error})");
            _dispatcher.TryEnqueue(() =>
            {
                _mediaHost.Child = BuildUnsupportedFallback();
                Controls.Visibility = Visibility.Collapsed;
            });
        }

        private FrameworkElement BuildUnsupportedFallback()
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 10,
                Padding = new Thickness(24),
            };

            panel.Children.Add(new TextBlock
            {
                Text = "🎞",
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Can't preview this format here",
                FontFamily = (FontFamily)_resources["DisplayFontFamily"],
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)_resources["TextSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Windows doesn't include a codec for it — some .mov files use HEVC or ProRes. The file isn't damaged; open it in your usual player to check.",
                FontFamily = (FontFamily)_resources["BodyFontFamily"],
                FontSize = 12.5,
                Foreground = (Brush)_resources["TextMutedBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
            });

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var openButton = new Button
            {
                Content = "Open in default player",
                Padding = new Thickness(16, 8, 16, 8),
                CornerRadius = new CornerRadius(10),
                Background = (Brush)_resources["AccentBrush"],
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold,
            };
            openButton.Click += (_, _) => OpenExternally();

            var showButton = new Button
            {
                Content = "Show in folder",
                Padding = new Thickness(16, 8, 16, 8),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Foreground = (Brush)_resources["TextSecondaryBrush"],
                BorderBrush = (Brush)_resources["SecondaryButtonBorderBrush"],
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
            };
            showButton.Click += (_, _) => ShowInFolder();

            buttonRow.Children.Add(openButton);
            buttonRow.Children.Add(showButton);
            panel.Children.Add(buttonRow);

            return panel;
        }

        private void OpenExternally()
        {
            try
            {
                // UseShellExecute launches whatever app is registered for the
                // file's type (VLC, Photos, etc.) - the point of the fallback.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger?.LogError($"Opening media externally failed: {ex}");
            }
        }

        private void ShowInFolder() => Quickening.Core.Shell.ExplorerLauncher.SelectInExplorer(_path);

        private void UpdateTimeText(TimeSpan position)
        {
            _timeText.Text = $"{Format(position)} / {Format(_duration)}";
        }

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        // Teardown order matters: unhook every event, detach the player from
        // its element (SetMediaPlayer(null)) BEFORE disposing, then pause,
        // drop the source, and dispose. Disposing a still-attached, still-
        // playing player is the classic MediaPlayerElement crash.
        public void Dispose()
        {
            try
            {
                _player.MediaOpened -= OnMediaOpened;
                _player.MediaEnded -= OnMediaEnded;
                _player.MediaFailed -= OnMediaFailed;
                _session.PlaybackStateChanged -= OnPlaybackStateChanged;
                _session.PositionChanged -= OnPositionChanged;
                _playPause.Click -= OnPlayPauseClick;
                _seek.ValueChanged -= OnSeekValueChanged;

                Element.SetMediaPlayer(null);
                _player.Pause();
                _player.Source = null;
                _player.Dispose();
            }
            catch (Exception ex)
            {
                App.Logger?.LogError($"Disposing media controller failed: {ex}");
            }
        }
    }
}
