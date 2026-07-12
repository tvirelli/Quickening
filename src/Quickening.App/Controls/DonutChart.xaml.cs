using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Quickening.App.Formatting;
using Quickening.Core.Duplicates;
using WinPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Quickening.App.Controls;

public sealed partial class DonutChart : UserControl
{
    // Ring thickness in pixels - 240px diameter / 30px ring per screen 2e
    // and DESIGN-SPEC.md §4 ("summary donut"). The host still controls the
    // actual Width/Height (see ScanCompletePage.xaml's Chart element); this
    // is only the stroke thickness used to compute the arc radius.
    private const double StrokeThickness = 30;
    private IReadOnlyList<CategoryBreakdownItem> _segments = Array.Empty<CategoryBreakdownItem>();

    public DonutChart()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Render();
    }

    /// <summary>
    /// Re-sorts into DonutChartMath's fixed legend order before rendering,
    /// so a caller passing CategoryBreakdownCalculator's raw (descending-
    /// bytes) output still gets 2e's fixed arc/legend order for free.
    /// </summary>
    public void SetSegments(IReadOnlyList<CategoryBreakdownItem> segments)
    {
        _segments = DonutChartMath.OrderForLegend(segments);
        Render();
    }

    private void Render()
    {
        ArcCanvas.Children.Clear();

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || _segments.Count == 0)
        {
            CenterContent.Visibility = Visibility.Collapsed;
            return;
        }

        var radius = size / 2 - StrokeThickness / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        foreach (var arc in DonutChartMath.ComputeArcs(_segments))
        {
            var path = BuildArcPath(center, radius, arc.StartAngleDegrees, arc.SweepAngleDegrees);
            path.Stroke = new SolidColorBrush(CategoryColors.For(arc.Category));
            path.StrokeThickness = StrokeThickness;
            ArcCanvas.Children.Add(path);
        }

        CenterContent.Visibility = Visibility.Visible;
        CenterTotalText.Text = FileSizeFormatter.Format(_segments.Sum(s => s.ReclaimableBytes));
    }

    private static WinPath BuildArcPath(
        Point center, double radius, double startAngleDegrees, double sweepAngleDegrees)
    {
        // A sweep of exactly 360 degrees collapses ArcSegment's start/end
        // points onto each other and renders nothing - clamp just under a
        // full circle for the single-category case.
        var clampedSweep = Math.Min(sweepAngleDegrees, 359.999);

        var start = PointOnCircle(center, radius, startAngleDegrees);
        var end = PointOnCircle(center, radius, startAngleDegrees + clampedSweep);
        var isLargeArc = clampedSweep > 180;

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new WinPath { Data = geometry };
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var angleRadians = angleDegrees * Math.PI / 180.0;
        return new Point(
            center.X + radius * Math.Cos(angleRadians),
            center.Y + radius * Math.Sin(angleRadians));
    }
}
