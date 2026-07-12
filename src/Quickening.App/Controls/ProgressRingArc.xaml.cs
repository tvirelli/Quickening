using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Quickening.App.Controls;

public sealed partial class ProgressRingArc : UserControl
{
    // Centerline radius the stroke is drawn along: (260 diameter - 14
    // stroke) / 2 = 123, so the 14px-thick stroke's outer edge sits
    // exactly at the 260px box's edge (matches DonutChart.xaml.cs's
    // identical `size / 2 - StrokeThickness / 2` formula for the same
    // "ring hugs the edge of its own box" effect). NOTE: this was
    // transcribed from the plan as 116 - that value doesn't satisfy the
    // plan's own comment ("(260 - 14 stroke) / 2"), which computes to 123,
    // not 116; 116 would leave a stray 7px gap between the ring and the
    // edge of its 260x260 box. Corrected to 123 here.
    private const double Radius = 123;
    private const double Center = 130;

    private Storyboard? _spinStoryboard;
    private bool _isIndeterminate;

    public ProgressRingArc()
    {
        InitializeComponent();
        TrackPath.Data = BuildRingGeometry(0, 360);
        Loaded += (_, _) => SetIndeterminate(true);
    }

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(ProgressRingArc),
        new PropertyMetadata(null, (d, e) => ((ProgressRingArc)d).ArcPath.Stroke = (Brush)e.NewValue));

    public Brush RingBrush
    {
        get => (Brush)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    /// <summary>
    /// Switches between the spinning fixed-length arc (percentage unknown,
    /// screen 2c) and a static arc whose sweep represents a 0-100 percentage
    /// (screen 2d/2k). Real arc geometry via ArcSegment, not a Composition
    /// conic-gradient (DESIGN-SPEC.md suggests Composition, but a hand-drawn
    /// arc is simpler to get right reliably and produces the same visual
    /// result: a colored ring segment on a faint full-circle track).
    /// </summary>
    public void SetIndeterminate(bool isIndeterminate)
    {
        // Callers (ProgressPage's constructor-time Loaded handler, its own
        // explicit OnNavigatedTo call, and every OnProgress tick while the
        // total is still unknown) can and do call SetIndeterminate(true)
        // repeatedly with no intervening false in between - without this
        // guard, each call would start a brand new Storyboard on top of the
        // still-running previous one, piling up overlapping
        // RepeatBehavior.Forever animations all driving the same
        // RotateTransform.Angle.
        if (isIndeterminate == _isIndeterminate)
        {
            return;
        }

        _isIndeterminate = isIndeterminate;
        _spinStoryboard?.Stop();

        if (isIndeterminate)
        {
            ArcPath.Data = BuildRingGeometry(0, 90);
            _spinStoryboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromSeconds(1.4)),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(animation, ArcRotation);
            Storyboard.SetTargetProperty(animation, "Angle");
            _spinStoryboard.Children.Add(animation);
            _spinStoryboard.Begin();
        }
        else
        {
            ArcRotation.Angle = 0;
        }
    }

    public void SetPercentage(double percentage)
    {
        ArcPath.Data = BuildRingGeometry(0, Math.Clamp(percentage, 0, 100) * 3.6);
    }

    private static PathGeometry BuildRingGeometry(double startAngleDegrees, double sweepAngleDegrees)
    {
        if (sweepAngleDegrees >= 359.99)
        {
            sweepAngleDegrees = 359.99; // a 360deg ArcSegment is degenerate (start == end point)
        }

        var startPoint = PointOnCircle(startAngleDegrees);
        var endPoint = PointOnCircle(startAngleDegrees + sweepAngleDegrees);

        var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(Radius, Radius),
            IsLargeArc = sweepAngleDegrees > 180,
            SweepDirection = SweepDirection.Clockwise,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointOnCircle(double angleDegrees)
    {
        var radians = (Math.PI / 180) * (angleDegrees - 90); // -90 so 0deg starts at 12 o'clock
        return new Point(Center + Radius * Math.Cos(radians), Center + Radius * Math.Sin(radians));
    }
}
