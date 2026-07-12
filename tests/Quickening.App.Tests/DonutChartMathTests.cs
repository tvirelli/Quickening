using Quickening.App.Controls;
using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.App.Tests;

public class DonutChartMathTests
{
    [Fact]
    public void ComputeArcs_ReturnsEmpty_ForNoItems()
    {
        var arcs = DonutChartMath.ComputeArcs(Array.Empty<CategoryBreakdownItem>());

        Assert.Empty(arcs);
    }

    [Fact]
    public void ComputeArcs_ReturnsEmpty_WhenTotalBytesIsZero()
    {
        var items = new[] { new CategoryBreakdownItem(MimeCategory.Image, 0L) };

        var arcs = DonutChartMath.ComputeArcs(items);

        Assert.Empty(arcs);
    }

    [Fact]
    public void ComputeArcs_SingleItem_SpansFullCircleStartingAtTop()
    {
        var items = new[] { new CategoryBreakdownItem(MimeCategory.Image, 100L) };

        var arcs = DonutChartMath.ComputeArcs(items);

        var arc = Assert.Single(arcs);
        Assert.Equal(-90.0, arc.StartAngleDegrees, precision: 5);
        Assert.Equal(360.0, arc.SweepAngleDegrees, precision: 5);
    }

    [Fact]
    public void ComputeArcs_TwoEqualItems_SplitEvenlyAndAreContiguous()
    {
        var items = new[]
        {
            new CategoryBreakdownItem(MimeCategory.Image, 50L),
            new CategoryBreakdownItem(MimeCategory.Audio, 50L),
        };

        var arcs = DonutChartMath.ComputeArcs(items);

        Assert.Equal(2, arcs.Count);
        Assert.Equal(-90.0, arcs[0].StartAngleDegrees, precision: 5);
        Assert.Equal(180.0, arcs[0].SweepAngleDegrees, precision: 5);
        Assert.Equal(90.0, arcs[1].StartAngleDegrees, precision: 5); // starts where the first left off
        Assert.Equal(180.0, arcs[1].SweepAngleDegrees, precision: 5);
    }

    [Fact]
    public void ComputeArcs_SweepAnglesSumTo360_ForUnevenProportions()
    {
        var items = new[]
        {
            new CategoryBreakdownItem(MimeCategory.Image, 700L),
            new CategoryBreakdownItem(MimeCategory.Audio, 200L),
            new CategoryBreakdownItem(MimeCategory.Document, 100L),
        };

        var arcs = DonutChartMath.ComputeArcs(items);

        var totalSweep = arcs.Sum(a => a.SweepAngleDegrees);
        Assert.Equal(360.0, totalSweep, precision: 5);
    }

    [Fact]
    public void OrderForLegend_ReordersIntoScreen2esFixedLegendOrder()
    {
        // Deliberately not sorted by bytes (that's CategoryBreakdownCalculator's
        // own order) - Images, Documents, Videos, Archives, Audio,
        // Executables, Other per screen 2e's legend grid.
        var items = new[]
        {
            new CategoryBreakdownItem(MimeCategory.Other, 5L),
            new CategoryBreakdownItem(MimeCategory.Executable, 400L),
            new CategoryBreakdownItem(MimeCategory.Image, 300L),
            new CategoryBreakdownItem(MimeCategory.Video, 250L),
        };

        var ordered = DonutChartMath.OrderForLegend(items);

        Assert.Equal(
            new[] { MimeCategory.Image, MimeCategory.Video, MimeCategory.Executable, MimeCategory.Other },
            ordered.Select(i => i.Category));
    }

    [Fact]
    public void OrderForLegend_ReturnsEmpty_ForNoItems()
    {
        Assert.Empty(DonutChartMath.OrderForLegend(Array.Empty<CategoryBreakdownItem>()));
    }
}
