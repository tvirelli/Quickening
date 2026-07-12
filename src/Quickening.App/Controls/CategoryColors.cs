using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Quickening.Core.Models;

namespace Quickening.App.Controls;

/// <summary>
/// Fixed brand palette per category, shared by the donut chart and the
/// Results sidebar so a category reads as the same color in both places.
/// Deliberately not the user's Windows accent color - see
/// docs/plans/2026-07-07-quickening-visual-redesign-design.md. These hex
/// values are duplicated in Styles/Theme.xaml's Category*Color resources
/// (XAML can't reference C# constants) - keep both in sync if a swatch
/// changes.
/// </summary>
public static class CategoryColors
{
    public static Color For(MimeCategory category) => category switch
    {
        MimeCategory.Image => Color.FromArgb(0xFF, 0xFF, 0x7A, 0xB6),
        MimeCategory.Video => Color.FromArgb(0xFF, 0x8C, 0x6E, 0xFF),
        MimeCategory.Audio => Color.FromArgb(0xFF, 0x5E, 0xE7, 0xB7),
        MimeCategory.Document => Color.FromArgb(0xFF, 0xFF, 0xD6, 0x66),
        MimeCategory.Archive => Color.FromArgb(0xFF, 0x40, 0xC4, 0xFF),
        MimeCategory.Executable => Color.FromArgb(0xFF, 0xFF, 0x8A, 0x5C),
        MimeCategory.Other => Color.FromArgb(0xFF, 0x8B, 0x96, 0xBC),
        _ => Color.FromArgb(0xFF, 0x8B, 0x96, 0xBC),
    };

    // SolidColorBrush is immutable in practice here (nobody animates these),
    // so one shared frozen-ish brush per category beats allocating a fresh
    // one on every row realization during list virtualization.
    private static readonly ConcurrentDictionary<MimeCategory, SolidColorBrush> Brushes = new();

    public static SolidColorBrush BrushFor(MimeCategory category) =>
        Brushes.GetOrAdd(category, c => new SolidColorBrush(For(c)));
}
