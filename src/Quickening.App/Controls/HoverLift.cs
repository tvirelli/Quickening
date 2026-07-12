using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Quickening.App.Controls;

/// <summary>
/// A subtle "lift" hover for the gradient pill CTAs (Scan, Remove Selected,
/// Scan another folder). The default WinUI Button pointer-over background is
/// an opaque-gradient-covered overlay that only peeks through the pill's
/// rounded corners - which read as a wrong-radius glow, or (once the button
/// radius is pinned to match the pill) as no hover at all. This replaces that
/// with a deliberate, shape-independent effect: a small scale-up via a
/// RenderTransform, which renders past the layout slot so it never clips and
/// never reflows neighbors.
/// </summary>
public static class HoverLift
{
    private const double HoverScale = 1.03;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(120);

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(HoverLift), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(FrameworkElement element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(FrameworkElement element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        element.PointerEntered += (_, _) => AnimateTo(element, scale, HoverScale);
        element.PointerExited += (_, _) => AnimateTo(element, scale, 1.0);
        // Pointer capture can be lost mid-hover (e.g. a click that navigates
        // away) without an Exited - reset then too so nothing is left scaled.
        element.PointerCaptureLost += (_, _) => AnimateTo(element, scale, 1.0);
        element.PointerCanceled += (_, _) => AnimateTo(element, scale, 1.0);
    }

    private static void AnimateTo(FrameworkElement element, ScaleTransform scale, double target)
    {
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var animation = new DoubleAnimation
            {
                To = target,
                Duration = new Duration(Duration),
                EnableDependentAnimation = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, scale);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        storyboard.Begin();
    }
}
