using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Quickening.App.Formatting;

namespace Quickening.App.Controls;

public sealed partial class SegmentedBarChart : UserControl
{
    // Fixed pixel dimensions per screen 2f - unlike DonutChart (sized by
    // its host, see DonutChart.xaml.cs), this bar is always exactly
    // 880x46, so there's no need to react to SizeChanged.
    private const double CornerRadiusPixels = 14;

    public SegmentedBarChart()
    {
        InitializeComponent();
    }

    public void SetSegments(IReadOnlyList<BarSegment> segments)
    {
        Render(segments);
    }

    private void Render(IReadOnlyList<BarSegment> segments)
    {
        BarGrid.ColumnDefinitions.Clear();
        BarGrid.Children.Clear();

        var layout = SegmentedBarChartMath.ComputeLayout(segments);

        for (var i = 0; i < layout.Count; i++)
        {
            BarGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(layout[i].WidthFraction, GridUnitType.Star),
            });

            // WinUI's Shapes.Rectangle has no per-corner radius, so a
            // single Rectangle can't reproduce 2f's "rounded overall bar,
            // square segment boundaries" look. A Border's own
            // Background/CornerRadius rendering is rounded natively
            // (unlike clipping arbitrary child content, which WinUI
            // doesn't do for free) - only the outer edge of the first/last
            // segment gets rounded, giving the whole bar rounded ends
            // without needing a separate clip geometry.
            var isFirst = i == 0;
            var isLast = i == layout.Count - 1;
            var cornerRadius = new CornerRadius(
                topLeft: isFirst ? CornerRadiusPixels : 0,
                topRight: isLast ? CornerRadiusPixels : 0,
                bottomRight: isLast ? CornerRadiusPixels : 0,
                bottomLeft: isFirst ? CornerRadiusPixels : 0);

            var segment = new Border
            {
                Background = new SolidColorBrush(layout[i].Segment.Color),
                CornerRadius = cornerRadius,
            };
            Grid.SetColumn(segment, i);
            BarGrid.Children.Add(segment);
        }

        LegendList.ItemsSource = layout
            .Select(l => new LegendRow(
                l.Segment.Label,
                new SolidColorBrush(l.Segment.Color),
                $"{FileSizeFormatter.Format(l.Segment.SizeBytes)} · {l.Segment.Count}"))
            .ToList();
    }

    private sealed record LegendRow(string Label, SolidColorBrush SwatchBrush, string SizeAndCount);
}
