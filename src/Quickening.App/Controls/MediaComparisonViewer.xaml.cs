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

        // Building tiles can briefly block the UI thread (a code file's syntax
        // highlight, a PDF or archive read). Show a spinner and defer the build
        // one tick so the overlay paints first - the tap registers immediately
        // instead of looking like nothing happened.
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                LayoutPanels(groupFiles);
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        });
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
        CloseWebViews();

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

        var kind = FileViewerRouter.ForPath(file.Path, file.Category);
        switch (kind)
        {
            case FileViewerKind.Video:
            case FileViewerKind.Audio:
            {
                var controller = new MediaController(file, mediaHost, DispatcherQueue, resources, isAudio: kind == FileViewerKind.Audio);
                _controllers.Add(controller);
                mediaHost.Child = controller.Element;
                Grid.SetRow(controller.Controls, 1);
                grid.Children.Add(controller.Controls);
                break;
            }
            case FileViewerKind.Image:
            {
                var image = new Image
                {
                    // Uniform + Stretch fits the whole image inside the host,
                    // centered and letterboxed - never cropped.
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                if (System.IO.Path.GetExtension(file.Path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    // SVG is vector - BitmapImage can't decode it. An SvgImageSource
                    // whose source is set via UriSource silently renders nothing when
                    // the SVG declares only a viewBox (no width/height), so force a
                    // concrete raster size and load from a stream, logging failures.
                    var svg = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource
                    {
                        RasterizePixelWidth = 512,
                        RasterizePixelHeight = 512,
                    };
                    svg.OpenFailed += (_, e) => App.Logger?.LogError($"SVG open failed ({e.Status}) for '{file.Path}'");
                    image.Source = svg;
                    _ = LoadSvgAsync(svg, file.Path);
                }
                else
                {
                    image.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file.Path));
                }

                mediaHost.Child = image;
                // A transparent or dark image vanishes against the near-black media
                // host; pick a backing that contrasts with the image's own luminance
                // so it stays visible (also fixes transparent PNG/WebP).
                ApplyAdaptiveImageBackground(mediaHost, file.Path);
                break;
            }
            case FileViewerKind.Markdown:
                mediaHost.Child = BuildTextScroller(MarkdownLiteRenderer.Render(resources, ReadTextLines(file.Path)));
                break;
            case FileViewerKind.Code:
            case FileViewerKind.PlainText:
                mediaHost.Child = BuildTextScroller(BuildCodeOrText(file.Path, resources));
                break;
            case FileViewerKind.Pdf:
                mediaHost.Child = BuildPdfViewer(file, resources);
                break;
            case FileViewerKind.Archive:
                mediaHost.Child = BuildArchiveViewer(file, resources);
                break;
            default:
                mediaHost.Child = BuildNoPreview(file, resources);
                break;
        }

        if (kind == FileViewerKind.Audio)
        {
            // Audio has no tall visual - a full-height tile leaves a thin waveform
            // floating in a big empty box. Collapse the media row to a fixed band
            // and let the card size to its content, vertically centered in the cell.
            grid.RowDefinitions[0].Height = GridLength.Auto;
            mediaHost.MinHeight = 0;
            mediaHost.Height = 150;
            border.VerticalAlignment = VerticalAlignment.Center;
        }

        grid.Children.Add(BuildFooter(file, resources));
        return border;
    }

    // Loads an SVG from its file stream into an already-attached SvgImageSource.
    // Stream loading (vs UriSource) avoids the file:// quirks that left SVGs blank,
    // and the load status is logged so an unsupported SVG is diagnosable rather
    // than a silent empty tile.
    private static async System.Threading.Tasks.Task LoadSvgAsync(
        Microsoft.UI.Xaml.Media.Imaging.SvgImageSource svg, string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var result = await svg.SetSourceAsync(stream);
            if (result != Microsoft.UI.Xaml.Media.Imaging.SvgImageSourceLoadStatus.Success)
            {
                App.Logger?.LogError($"SVG load status {result} for '{path}'");
            }
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"SVG load failed for '{path}': {ex}");
        }
    }

    // Two backings the adaptive image logic chooses between: a light one behind
    // dark/transparent content, a dark one behind light content.
    private static readonly Brush LightImageBacking = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEC, 0xEC, 0xEC));
    private static readonly Brush DarkImageBacking = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x16, 0x22));

    private static void ApplyAdaptiveImageBackground(Border mediaHost, string path)
    {
        // SVG is vector - we can't cheaply sample its pixels - and SVG art is
        // usually dark line work on transparency, so a light backing is the safe
        // default.
        if (System.IO.Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            mediaHost.Background = LightImageBacking;
            return;
        }

        _ = ApplyAdaptiveImageBackgroundAsync(mediaHost, path);
    }

    private static async System.Threading.Tasks.Task ApplyAdaptiveImageBackgroundAsync(Border mediaHost, string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            // Sample a tiny 24x24 version - enough to judge overall lightness,
            // negligible to decode.
            var transform = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 24, ScaledHeight = 24 };
            var pixels = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var data = pixels.DetachPixelData();

            double lumSum = 0, alphaSum = 0;
            for (var i = 0; i + 3 < data.Length; i += 4)
            {
                double b = data[i], g = data[i + 1], r = data[i + 2];
                var a = data[i + 3] / 255.0;
                lumSum += (0.2126 * r + 0.7152 * g + 0.0722 * b) * a;
                alphaSum += a;
            }

            // Near-fully-transparent images read as "dark content" -> light
            // backing so the visible strokes show. Otherwise use the alpha-
            // weighted mean luminance (0-255).
            var avgLuminance = alphaSum > 0.5 ? lumSum / alphaSum : 0;
            mediaHost.Background = avgLuminance < 128 ? LightImageBacking : DarkImageBacking;
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Adaptive image background failed for '{path}': {ex}");
        }
    }

    // Cap in-viewer text loads so a giant log can't hang the UI.
    private const long MaxPreviewBytes = 2 * 1024 * 1024;

    private static string[] ReadTextLines(string path)
    {
        try
        {
            if (new System.IO.FileInfo(path).Length > MaxPreviewBytes)
            {
                return new[] { "This file is too large to preview here — open it in your usual app." };
            }
            return System.IO.File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Reading '{path}' for preview failed: {ex}");
            return new[] { "Couldn't read this file to preview it." };
        }
    }

    private static ScrollViewer BuildTextScroller(UIElement content) => new()
    {
        Padding = new Thickness(14),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = content,
    };

    private static UIElement PlainMono(string text, ResourceDictionary resources) => new TextBlock
    {
        Text = text,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12.5,
        Foreground = (Brush)resources["TextBodyBrush"],
        TextWrapping = TextWrapping.NoWrap,
        IsTextSelectionEnabled = true,
    };

    // Highlighted source via ColorCode (dark theme to match the app). Unknown
    // languages / any failure fall through to plain, still-readable monospace.
    private static UIElement BuildCodeOrText(string path, ResourceDictionary resources)
    {
        var text = string.Join("\n", ReadTextLines(path));
        var language = ColorCodeLanguageFor(System.IO.Path.GetExtension(path));
        if (language is null)
        {
            return PlainMono(text, resources);
        }
        try
        {
            var rtb = new RichTextBlock { IsTextSelectionEnabled = true };
            new ColorCode.RichTextBlockFormatter(ElementTheme.Dark).FormatRichTextBlock(text, language, rtb);
            return rtb;
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Syntax highlighting failed for '{path}': {ex}");
            return PlainMono(text, resources);
        }
    }

    private static ColorCode.ILanguage? ColorCodeLanguageFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".cs" => ColorCode.Languages.CSharp,
        ".js" or ".jsx" or ".json" => ColorCode.Languages.JavaScript,
        ".ts" or ".tsx" => ColorCode.Languages.Typescript,
        ".html" or ".htm" => ColorCode.Languages.Html,
        ".css" or ".less" or ".scss" => ColorCode.Languages.Css,
        ".xml" or ".xaml" or ".svg" or ".csproj" => ColorCode.Languages.Xml,
        ".sql" => ColorCode.Languages.Sql,
        ".py" => ColorCode.Languages.Python,
        ".php" => ColorCode.Languages.Php,
        ".ps1" => ColorCode.Languages.PowerShell,
        ".c" or ".cpp" or ".h" or ".hpp" => ColorCode.Languages.Cpp,
        ".java" => ColorCode.Languages.Java,
        ".fs" => ColorCode.Languages.FSharp,
        ".vb" => ColorCode.Languages.VbDotNet,
        _ => null,
    };

    // "No preview" tile with an Open-in-default-app escape hatch (mirrors the
    // video-unsupported fallback). Also the current PDF placeholder until Task 7.
    private FrameworkElement BuildNoPreview(SelectableFile file, ResourceDictionary resources)
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
            Text = "No preview for this file type",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = (Brush)resources["TextSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });
        var open = new Button
        {
            Content = "Open in default app",
            Padding = new Thickness(16, 8, 16, 8),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)resources["AccentBrush"],
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderThickness = new Thickness(0),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        open.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger?.LogError($"Open failed: {ex}");
            }
        };
        panel.Children.Add(open);
        return panel;
    }

    // Archive contents: a flat, scrollable list of every entry (name + size)
    // so two archives can be eyeballed side by side. Reads via ArchiveInspector
    // (zip/tar, no extraction, no native dependency); an unreadable or empty
    // archive degrades to the open-externally tile.
    private FrameworkElement BuildArchiveViewer(SelectableFile file, ResourceDictionary resources)
    {
        var listing = Quickening.Core.Archives.ArchiveInspector.List(file.Path);
        if (listing.Error is not null || listing.Entries.Count == 0)
        {
            return BuildNoPreview(file, resources);
        }

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = listing.Truncated
                ? $"{listing.Entries.Count}+ items (showing first {listing.Entries.Count})"
                : $"{listing.Entries.Count} items",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = (Brush)resources["TextSecondaryBrush"],
            Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var entry in listing.Entries)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = entry.IsDirectory ? "📁 " + entry.Name : entry.Name,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = (Brush)resources[entry.IsDirectory ? "TextMutedBrush" : "TextBodyBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            if (!entry.IsDirectory)
            {
                var size = new TextBlock
                {
                    Text = FileSizeFormatter.Format(entry.Length),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = (Brush)resources["TextFaintBrush"],
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(size, 1);
                row.Children.Add(size);
            }

            stack.Children.Add(row);
        }

        return BuildTextScroller(stack);
    }

    // PDF ladder: WebView2 (real Edge viewer) -> Windows.Data.Pdf bitmap pages
    // -> "open externally". WebView2 is created lazily only for a PDF tile; the
    // runtime is present on ~all Win10/11, and any absence/failure degrades.
    private FrameworkElement BuildPdfViewer(SelectableFile file, ResourceDictionary resources)
    {
        var host = new Grid();
        bool webView2Available;
        try
        {
            webView2Available = !string.IsNullOrEmpty(
                Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            webView2Available = false;
        }

        if (webView2Available)
        {
            try
            {
                var web = new Microsoft.UI.Xaml.Controls.WebView2();
                host.Children.Add(web);
                _ = InitPdfWebViewAsync(web, file, host, resources);
                return host;
            }
            catch (Exception ex)
            {
                App.Logger?.LogError($"WebView2 PDF host failed: {ex}");
            }
        }

        _ = RenderPdfBitmapsAsync(host, file, resources);
        return host;
    }

    private async System.Threading.Tasks.Task InitPdfWebViewAsync(
        Microsoft.UI.Xaml.Controls.WebView2 web, SelectableFile file, Grid host, ResourceDictionary resources)
    {
        try
        {
            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.Navigate(new Uri(file.Path).AbsoluteUri); // file:// -> Edge PDF viewer
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"WebView2 PDF nav failed: {ex}");
            host.Children.Clear();
            await RenderPdfBitmapsAsync(host, file, resources);
        }
    }

    private async System.Threading.Tasks.Task RenderPdfBitmapsAsync(Grid host, SelectableFile file, ResourceDictionary resources)
    {
        try
        {
            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(file.Path);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(storageFile);
            var stack = new StackPanel { Spacing = 8 };
            for (uint i = 0; i < pdf.PageCount; i++)
            {
                using var page = pdf.GetPage(i);
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream);
                stream.Seek(0);
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(stream);
                stack.Children.Add(new Image
                {
                    Source = bmp,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                });
            }
            host.Children.Clear();
            host.Children.Add(new ScrollViewer
            {
                Content = stack,
                Padding = new Thickness(8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
        }
        catch (Exception ex)
        {
            App.Logger?.LogError($"Windows.Data.Pdf render failed: {ex}");
            host.Children.Clear();
            host.Children.Add(BuildNoPreview(file, resources));
        }
    }

    // Close any WebView2 (PDF) instances before the panels are torn down/rebuilt,
    // so their browser processes don't linger. Called from DisposeControllers,
    // which both ShowGroup and Close_Click invoke before clearing the grid.
    private void CloseWebViews()
    {
        foreach (var web in FindDescendants<Microsoft.UI.Xaml.Controls.WebView2>(PanelsGrid))
        {
            try { web.Close(); } catch { /* best-effort teardown */ }
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
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

        // The tile's main visual: the video surface for video, or an audio
        // visualizer (glyph + animated bars) for audio - audio plays through the
        // MediaPlayer with no video surface, so it gets its own look.
        public FrameworkElement Element { get; }
        public FrameworkElement Controls { get; }

        private readonly MediaPlayer _player;
        private readonly MediaPlaybackSession _session;
        private readonly DispatcherQueue _dispatcher;
        private readonly Border _mediaHost;
        private readonly ResourceDictionary _resources;
        private readonly string _path;
        private readonly MediaPlayerElement? _videoElement;                              // null for audio
        private readonly Microsoft.UI.Xaml.Media.Animation.Storyboard? _eqStoryboard;    // audio bars, null for video
        private readonly Grid? _audioContainer;                                          // audio tile root (EQ, then waveform)
        private readonly List<Border> _waveformBars = new();                             // real per-column amplitude bars
        private Brush? _wavePlayed;
        private Brush? _waveRemaining;
        private bool _waveformReady;
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
        private TimeSpan _observedEnd; // true end captured at MediaEnded; corrects a bogus NaturalDuration

        public MediaController(SelectableFile file, Border mediaHost, DispatcherQueue dispatcher, ResourceDictionary resources, bool isAudio)
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

            if (isAudio)
            {
                // Audio has no video surface - the MediaPlayer plays sound on its
                // own. Start with a note glyph + pulsing equalizer bars, then
                // replace them with a real waveform of the actual samples once the
                // file has been decoded (see LoadWaveformAsync).
                _eqStoryboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                _audioContainer = BuildAudioVisualizer(resources, _eqStoryboard);
                Element = _audioContainer;
                _ = LoadWaveformAsync();
            }
            else
            {
                var videoElement = new MediaPlayerElement
                {
                    AreTransportControlsEnabled = false,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                videoElement.SetMediaPlayer(_player);
                _videoElement = videoElement;
                Element = videoElement;
            }

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
            _session.NaturalDurationChanged += OnNaturalDurationChanged;
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

        private void OnMediaOpened(MediaPlayer sender, object args) => _dispatcher.TryEnqueue(ApplyDuration);

        private void OnNaturalDurationChanged(MediaPlaybackSession sender, object args) => _dispatcher.TryEnqueue(ApplyDuration);

        // Recomputes the displayed total + slider max from the best-known
        // duration (see VideoDuration.Best - the observed true end wins over a
        // bogus NaturalDuration once known). Runs on the UI thread.
        private void ApplyDuration()
        {
            _duration = VideoDuration.Best(_session.NaturalDuration, _observedEnd);
            var totalSeconds = _duration.TotalSeconds;
            if (totalSeconds > 0)
            {
                _suppressSeek = true;
                _seek.Maximum = totalSeconds;
                _suppressSeek = false;
                _seek.IsEnabled = true;
            }

            UpdateTimeText(_session.Position);
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
                UpdateWaveformProgress(position);
            });
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            var isPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
            _dispatcher.TryEnqueue(() =>
            {
                _playPause.Content = isPlaying ? PauseGlyph : PlayGlyph;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_playPause, isPlaying ? "Pause" : "Play");

                // Pulse the placeholder equalizer bars only while sound is playing
                // and only until the real waveform has replaced them.
                if (_eqStoryboard is not null && !_waveformReady)
                {
                    if (isPlaying)
                    {
                        _eqStoryboard.Resume();
                    }
                    else
                    {
                        _eqStoryboard.Pause();
                    }
                }
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
            var endPos = sender.PlaybackSession.Position;
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    // Position at end-of-media is the true duration - lets a
                    // bogus NaturalDuration self-correct once the clip plays out.
                    if (endPos > _observedEnd)
                    {
                        _observedEnd = endPos;
                        ApplyDuration();
                    }
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

        // Audio look: a note glyph above a row of equalizer bars. Each bar
        // scales vertically on its own loop (varied durations -> lively), all
        // added to the shared storyboard the controller starts on play / pauses
        // on stop. Built once per audio tile.
        private static Grid BuildAudioVisualizer(
            ResourceDictionary resources, Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard)
        {
            var accent = (Brush)resources["AccentBrush"];

            var eq = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 18,
            };

            eq.Children.Add(new TextBlock
            {
                Text = "♫", // ♫
                FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = accent,
            });

            var bars = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            for (var i = 0; i < 5; i++)
            {
                var scale = new ScaleTransform { ScaleY = 0.35 };
                bars.Children.Add(new Border
                {
                    Width = 7,
                    Height = 44,
                    CornerRadius = new CornerRadius(3.5),
                    Background = accent,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                    RenderTransform = scale,
                });

                var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0.3 + (i % 3) * 0.15,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(360 + i * 80)),
                    AutoReverse = true,
                    RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
                    EnableDependentAnimation = true,
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, scale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "ScaleY");
                storyboard.Children.Add(animation);
            }

            eq.Children.Add(bars);

            // Start (so the animation clock is live) then immediately pause, so the
            // bars hold still until playback resumes them.
            eq.Loaded += (_, _) =>
            {
                storyboard.Begin();
                storyboard.Pause();
            };

            // Wrapped in a Grid so LoadWaveformAsync can swap the placeholder EQ
            // out for the real waveform once decoding finishes.
            var container = new Grid();
            container.Children.Add(eq);
            return container;
        }

        // Decodes the audio to 16-bit PCM (via MediaTranscoder to an in-memory WAV),
        // reduces it to per-column peak amplitudes, and draws a real waveform in
        // place of the placeholder equalizer. Best-effort: very long files, non-
        // transcodable codecs, or any error just leave the pulsing EQ in place.
        private async System.Threading.Tasks.Task LoadWaveformAsync()
        {
            try
            {
                var source = await Windows.Storage.StorageFile.GetFileFromPathAsync(_path);
                var music = await source.Properties.GetMusicPropertiesAsync();
                if (music.Duration > TimeSpan.FromMinutes(20))
                {
                    return; // decoding a very long track to PCM in memory isn't worth it
                }

                var transcoder = new Windows.Media.Transcoding.MediaTranscoder();
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateWav(
                    Windows.Media.MediaProperties.AudioEncodingQuality.Low);
                using var srcStream = await source.OpenReadAsync();
                using var dest = new Windows.Storage.Streams.InMemoryRandomAccessStream();

                var prep = await transcoder.PrepareStreamTranscodeAsync(srcStream, dest, profile);
                if (!prep.CanTranscode)
                {
                    return;
                }
                await prep.TranscodeAsync();

                dest.Seek(0);
                var bytes = new byte[dest.Size];
                using (var reader = new Windows.Storage.Streams.DataReader(dest))
                {
                    await reader.LoadAsync((uint)dest.Size);
                    reader.ReadBytes(bytes);
                }

                var peaks = Quickening.Core.Audio.WaveformSampler.ComputePeaks(bytes, 160);
                if (peaks.Length > 0 && _audioContainer is not null)
                {
                    BuildWaveform(peaks);
                }
            }
            catch (Exception ex)
            {
                App.Logger?.LogError($"Waveform build failed for '{_path}': {ex}");
            }
        }

        private void BuildWaveform(float[] peaks)
        {
            _wavePlayed = (Brush)_resources["AccentBrush"];
            _waveRemaining = new SolidColorBrush(Windows.UI.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

            var bars = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 96,
                Spacing = 2,
            };

            const double maxBarHeight = 88;
            _waveformBars.Clear();
            foreach (var peak in peaks)
            {
                var bar = new Border
                {
                    Width = 3,
                    Height = Math.Max(2, peak * maxBarHeight),
                    CornerRadius = new CornerRadius(1.5),
                    Background = _waveRemaining,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _waveformBars.Add(bar);
                bars.Children.Add(bar);
            }

            // The fixed-width bar strip is wider than a split tile; a Viewbox
            // stretched to the tile width scales it down uniformly to fit (never
            // upscaling on a wide single-file tile).
            var viewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = Microsoft.UI.Xaml.Controls.StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                Child = bars,
            };

            _eqStoryboard?.Stop();
            _waveformReady = true;
            _audioContainer!.Children.Clear();
            _audioContainer.Children.Add(viewbox);
            UpdateWaveformProgress(_session.Position);
        }

        // Colours the bars up to the current playback position with the accent, the
        // rest faint - the "played so far" fill of a waveform scrubber.
        private void UpdateWaveformProgress(TimeSpan position)
        {
            if (_waveformBars.Count == 0 || _wavePlayed is null || _waveRemaining is null)
            {
                return;
            }

            var total = _duration.TotalSeconds;
            var ratio = total > 0 ? Math.Clamp(position.TotalSeconds / total, 0, 1) : 0;
            var playedCount = (int)(ratio * _waveformBars.Count);
            for (var i = 0; i < _waveformBars.Count; i++)
            {
                _waveformBars[i].Background = i < playedCount ? _wavePlayed : _waveRemaining;
            }
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
                _session.NaturalDurationChanged -= OnNaturalDurationChanged;
                _playPause.Click -= OnPlayPauseClick;
                _seek.ValueChanged -= OnSeekValueChanged;

                _eqStoryboard?.Stop();
                _videoElement?.SetMediaPlayer(null);
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
