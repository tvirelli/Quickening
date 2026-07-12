using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Quickening.App.Controls;

/// <summary>
/// WinUI3's CornerRadius doesn't clamp to half of an element's actual
/// rendered size the way CSS's border-radius does - on a wide, short
/// element (e.g. the Home mode-toggle bar), a literal CornerRadius="999"
/// renders as a bulging ellipse instead of a stadium/capsule, because the
/// oversized corner arcs from adjacent corners overlap across the whole
/// span. Per DESIGN-SPEC.md's translation note ("Pills (r999) -> CornerRadius
/// = height/2"), setting PillCornerRadius.Enabled="True" on a Border or
/// Control keeps its CornerRadius pinned to exactly half its own live
/// ActualHeight instead, which is what a browser's border-radius:999px
/// actually computes to for that element.
/// </summary>
public static class PillCornerRadius
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PillCornerRadius), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(FrameworkElement element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(FrameworkElement element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        element.SizeChanged += (_, args) => Apply(element, args.NewSize.Height);
        Apply(element, element.ActualHeight);
    }

    private static void Apply(FrameworkElement element, double height)
    {
        if (height <= 0)
        {
            return;
        }

        var radius = new CornerRadius(height / 2);
        switch (element)
        {
            case Border border:
                border.CornerRadius = radius;
                break;
            case Control control:
                control.CornerRadius = radius;
                break;
        }
    }
}
