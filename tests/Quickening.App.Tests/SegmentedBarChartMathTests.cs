using Quickening.App.Controls;
using Windows.UI;
using Xunit;

namespace Quickening.App.Tests;

public class SegmentedBarChartMathTests
{
    private static readonly Color AnyColor = Color.FromArgb(0xFF, 0xFF, 0x7A, 0xB6);

    [Fact]
    public void ComputeLayout_ReturnsEmpty_ForNoSegments()
    {
        var layout = SegmentedBarChartMath.ComputeLayout(Array.Empty<BarSegment>());

        Assert.Empty(layout);
    }

    [Fact]
    public void ComputeLayout_ReturnsEmpty_WhenTotalBytesIsZero()
    {
        var segments = new[] { new BarSegment("Images", AnyColor, 0L, 0) };

        var layout = SegmentedBarChartMath.ComputeLayout(segments);

        Assert.Empty(layout);
    }

    [Fact]
    public void ComputeLayout_SingleSegment_TakesTheWholeWidth()
    {
        var segments = new[] { new BarSegment("Images", AnyColor, 100L, 3) };

        var layout = SegmentedBarChartMath.ComputeLayout(segments);

        var item = Assert.Single(layout);
        Assert.Equal(1.0, item.WidthFraction, precision: 10);
        Assert.Same(segments[0], item.Segment);
    }

    [Fact]
    public void ComputeLayout_TwoEqualSegments_SplitEvenly()
    {
        var segments = new[]
        {
            new BarSegment("Images", AnyColor, 50L, 1),
            new BarSegment("Videos", AnyColor, 50L, 1),
        };

        var layout = SegmentedBarChartMath.ComputeLayout(segments);

        Assert.Equal(2, layout.Count);
        Assert.Equal(0.5, layout[0].WidthFraction, precision: 10);
        Assert.Equal(0.5, layout[1].WidthFraction, precision: 10);
    }

    [Fact]
    public void ComputeLayout_WidthFractionsSumToOne_ForUnevenProportions()
    {
        var segments = new[]
        {
            new BarSegment("Videos", AnyColor, 700L, 14),
            new BarSegment("Archives", AnyColor, 200L, 7),
            new BarSegment("Images", AnyColor, 100L, 9),
        };

        var layout = SegmentedBarChartMath.ComputeLayout(segments);

        var totalFraction = layout.Sum(l => l.WidthFraction);
        Assert.Equal(1.0, totalFraction, precision: 10);
    }

    [Fact]
    public void ComputeLayout_PreservesInputOrder()
    {
        var segments = new[]
        {
            new BarSegment("Videos", AnyColor, 700L, 14),
            new BarSegment("Archives", AnyColor, 200L, 7),
        };

        var layout = SegmentedBarChartMath.ComputeLayout(segments);

        Assert.Equal("Videos", layout[0].Segment.Label);
        Assert.Equal("Archives", layout[1].Segment.Label);
    }
}
