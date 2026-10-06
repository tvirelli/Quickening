using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Quickening.App.Views;

/// <summary>
/// "Advanced options" overlay, opened from Home (Duplicates mode). Holds the
/// persistent "deepen the scan" opt-ins that used to be an inline toggle on
/// Home. Same centered-ContentDialog pattern as SettingsDialog (dimmed
/// backdrop, NebulaContentDialogStyle), built in code for consistency with
/// that dialog. Each toggle persists immediately to AppSettings so the choice
/// survives app restarts.
///
/// Only "similar photos" is wired to a real engine today; "similar videos" and
/// "deep audio matching" are shown as disabled "Soon" rows so the overlay's
/// final shape is visible now and they light up when their engines ship.
/// </summary>
internal static class AdvancedOptionsDialog
{
    // WinUI allows one open ContentDialog per XamlRoot; guard against a
    // double-open (e.g. a double-click on the Home button) throwing.
    private static bool _isOpen;

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
                Content = BuildContent(resources),
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // Another ContentDialog may already be open on this XamlRoot -
            // opening this then is a silent no-op, not a crash (matches
            // SettingsDialog's own guard).
            App.Logger?.LogError($"AdvancedOptionsDialog could not be shown: {ex}");
        }
        finally
        {
            _isOpen = false;
        }
    }

    private static UIElement BuildContent(ResourceDictionary resources)
    {
        var root = new StackPanel { Spacing = 0 };

        root.Children.Add(new TextBlock
        {
            Text = "Advanced Options",
            FontFamily = (FontFamily)resources["DisplayFontFamily"],
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextHeadingBrush"],
        });
        root.Children.Add(new TextBlock
        {
            Text = "Extra ways to catch near-duplicates. All off by default — these deepen the scan and make it slower.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 13,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var strictnessRow = BuildStrictnessRow(resources, out var strictnessSlider);
        strictnessSlider.IsEnabled = App.Settings.IncludeSimilarPhotos;

        var photos = BuildToggleRow(
            resources,
            "Also find similar photos",
            "Catches resized, re-saved, or lightly edited copies of the same photo — not just exact duplicates.",
            App.Settings.IncludeSimilarPhotos,
            isOn =>
            {
                App.Settings.IncludeSimilarPhotos = isOn;
                App.SaveSettings();
                strictnessSlider.IsEnabled = isOn; // strictness only bites when photo matching runs
            });

        var videos = BuildToggleRow(
            resources,
            "Also find similar videos",
            "Near-duplicate videos — re-encoded or resized copies of the same clip. Samples a few frames per video, so it's slower than the other passes.",
            App.Settings.IncludeSimilarVideos,
            isOn =>
            {
                App.Settings.IncludeSimilarVideos = isOn;
                App.SaveSettings();
            });

        var songs = BuildToggleRow(
            resources,
            "Find duplicate songs",
            "Groups the same track saved in different formats or bitrates — matched by title, artist and length — so you can keep the best-quality copy.",
            App.Settings.FindDuplicateSongs,
            isOn =>
            {
                App.Settings.FindDuplicateSongs = isOn;
                App.SaveSettings();
            });

        var audio = BuildToggleRow(
            resources,
            "Deep audio matching",
            "Finds the same recording even when names and tags differ — mastered vs original, MP3 vs WAV. Slower: it listens to every song.",
            App.Settings.DeepAudioMatching,
            isOn =>
            {
                App.Settings.DeepAudioMatching = isOn;
                App.SaveSettings();
            });

        var card = BuildGroupCard(resources, "Deep scan", photos, strictnessRow, videos, songs, audio);
        card.Margin = new Thickness(0, 20, 0, 0);
        root.Children.Add(card);

        // Photo quality (F9): flag blurry shots for review.
        var blurStrictnessRow = BuildBlurStrictnessRow(resources, out var blurSlider);
        blurSlider.IsEnabled = App.Settings.FlagBlurryPhotos;

        var blur = BuildToggleRow(
            resources,
            "Flag blurry photos",
            "Scores each photo's sharpness and lists the likely-blurry ones to review — keep the sharp frame, bin the soft misfires. Never auto-selected.",
            App.Settings.FlagBlurryPhotos,
            isOn =>
            {
                App.Settings.FlagBlurryPhotos = isOn;
                App.SaveSettings();
                blurSlider.IsEnabled = isOn;
            });

        var blurCard = BuildGroupCard(resources, "Photo quality", blur, blurStrictnessRow);
        blurCard.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(blurCard);

        // The overlay now has two cards - taller than the dialog on smaller
        // windows, so scroll it rather than clipping the second card off-screen.
        return new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 620,
        };
    }

    // Blur strictness slider (F9): maps left->right to a LOOSER flag - the value
    // is the max sharpness a photo may score and still be called blurry (small =
    // strict/only-clearly-blurry, large = loose/also-soft). Persists on change.
    private static UIElement BuildBlurStrictnessRow(ResourceDictionary resources, out Slider slider)
    {
        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(new TextBlock
        {
            Text = "Blur strictness",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = "How soft a photo must be to get flagged. Stricter flags only the clearly blurry; looser also catches slightly soft shots (with more false flags).",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        slider = new Slider
        {
            Minimum = 40,
            Maximum = 200,
            Value = Math.Clamp(App.Settings.BlurryMaxSharpness, 40, 200),
            StepFrequency = 5,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "Blur strictness"); // QA-6

        var endLabels = new Grid();
        endLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        endLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stricter = new TextBlock
        {
            Text = "Stricter",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextFaintBrush"],
        };
        var looser = new TextBlock
        {
            Text = "Looser",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextFaintBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(looser, 1);
        endLabels.Children.Add(stricter);
        endLabels.Children.Add(looser);

        var s = slider;
        s.ValueChanged += (_, _) =>
        {
            App.Settings.BlurryMaxSharpness = s.Value;
            App.SaveSettings();
        };

        stack.Children.Add(slider);
        stack.Children.Add(endLabels);
        return stack;
    }

    // Toggle row: title (+ optional badge) and description on the left, a
    // ToggleSwitch on the right. A disabled row (enabled: false) with a badge
    // is how the not-yet-built video/audio options preview their final shape.
    private static UIElement BuildToggleRow(
        ResourceDictionary resources, string title, string description, bool value,
        Action<bool>? onToggled, bool enabled = true, string? badge = null)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { Spacing = 3 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources[enabled ? "TextBodyBrush" : "TextMutedBrush"],
        });
        if (badge is not null)
        {
            // PillCornerRadius keeps the badge a true capsule (radius = height/2);
            // a literal CornerRadius(999) renders as a bulging ellipse on a wide-
            // short element (see PillCornerRadius' own remarks / the radius rule).
            var badgeBorder = new Border
            {
                Padding = new Thickness(9, 2, 9, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x8C, 0x6E, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8C, 0x6E, 0xFF)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = badge,
                    FontFamily = (FontFamily)resources["BodyFontFamily"],
                    FontSize = 10,
                    FontWeight = FontWeights.ExtraBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x8C, 0x6E, 0xFF)),
                },
            };
            badgeBorder.SetValue(Controls.PillCornerRadius.EnabledProperty, true);
            titleRow.Children.Add(badgeBorder);
        }
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
            IsOn = value,
            IsEnabled = enabled,
            OnContent = string.Empty,
            OffContent = string.Empty,
            VerticalAlignment = VerticalAlignment.Top,
        };
        // Named for screen readers - the visible title is a separate TextBlock,
        // so the switch itself was announced unlabeled (QA-6).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, title);
        if (onToggled is not null)
        {
            toggle.Toggled += (_, _) => onToggled(toggle.IsOn);
        }
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        return grid;
    }

    // Look-alike strictness slider (F2). Maps left->right to a LOOSER match: the
    // slider value is the max pHash Hamming distance the engine will group within
    // (small = strict/surer, large = loose/more but with occasional wrong ones).
    // Persists to AppSettings on every change and updates a plain-language caption.
    private static UIElement BuildStrictnessRow(ResourceDictionary resources, out Slider slider)
    {
        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(new TextBlock
        {
            // "Similar photos" is the user-facing term everywhere (the results
            // section, SHOW filter, keep-best action); "look-alike" stays in
            // code comments only.
            Text = "Similar-photo strictness",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)resources["TextBodyBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = "How close two photos must look to be grouped. Stricter finds fewer, surer matches; looser catches more, with the occasional wrong one.",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12.5,
            Foreground = (Brush)resources["TextMutedBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        slider = new Slider
        {
            Minimum = Quickening.Core.Similarity.SimilarityEngine.MinMaxHammingDistance,
            Maximum = Quickening.Core.Similarity.SimilarityEngine.MaxMaxHammingDistance,
            Value = Math.Clamp(
                App.Settings.SimilarityMaxDistance,
                Quickening.Core.Similarity.SimilarityEngine.MinMaxHammingDistance,
                Quickening.Core.Similarity.SimilarityEngine.MaxMaxHammingDistance),
            StepFrequency = 1,
            Margin = new Thickness(0, 6, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, "Similar-photo strictness"); // QA-6

        // End labels: "Stricter" (left) <-> "Looser" (right).
        var endLabels = new Grid();
        endLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        endLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stricter = new TextBlock
        {
            Text = "Stricter",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextFaintBrush"],
        };
        var looser = new TextBlock
        {
            Text = "Looser",
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 11.5,
            Foreground = (Brush)resources["TextFaintBrush"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(looser, 1);
        endLabels.Children.Add(stricter);
        endLabels.Children.Add(looser);

        var caption = new TextBlock
        {
            FontFamily = (FontFamily)resources["BodyFontFamily"],
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)resources["AccentLightBrush"],
            Margin = new Thickness(0, 2, 0, 0),
        };

        var s = slider;
        void UpdateCaption() => caption.Text = DescribeStrictness((int)s.Value);
        UpdateCaption();
        slider.ValueChanged += (_, _) =>
        {
            App.Settings.SimilarityMaxDistance = (int)s.Value;
            App.SaveSettings();
            UpdateCaption();
        };

        stack.Children.Add(slider);
        stack.Children.Add(endLabels);
        stack.Children.Add(caption);
        return stack;
    }

    private static string DescribeStrictness(int distance) => distance switch
    {
        <= 4 => "Very strict — only near-identical copies.",
        <= 8 => "Strict — close matches only.",
        <= 11 => "Balanced — the recommended middle ground.",
        <= 15 => "Loose — catches more edits, a few may be off.",
        _ => "Very loose — widest net, expect some wrong matches.",
    };

    // Same "labelled card with divider-separated rows" block SettingsDialog
    // uses, so the two dialogs match. Card corner uses RadiusCard per the
    // project convention for card elements.
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
}
