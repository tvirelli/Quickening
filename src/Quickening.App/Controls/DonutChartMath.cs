using Quickening.Core.Duplicates;
using Quickening.Core.Models;

namespace Quickening.App.Controls;

public readonly record struct DonutChartArc(MimeCategory Category, double StartAngleDegrees, double SweepAngleDegrees);

/// <summary>
/// Pure arc-angle computation, kept separate from DonutChart's rendering
/// so the math is unit-testable without any WinUI dependency.
/// </summary>
public static class DonutChartMath
{
    /// <summary>
    /// Fixed display order for the donut's arcs and legend - screen 2e's
    /// legend grid reads Images, Documents, Videos, Archives, Audio,
    /// Executables, Other (design-handoff/Quickening Screens.dc.html,
    /// id="2e") - deliberately NOT the descending-bytes order
    /// CategoryBreakdownCalculator.Calculate returns (that order is right
    /// for the Results sidebar, which this donut used to share verbatim,
    /// but it would make the ring's colour positions and legend rows
    /// reshuffle from scan to scan, which 2e's fixed grid never does).
    /// </summary>
    private static readonly MimeCategory[] LegendOrder =
    {
        MimeCategory.Image,
        MimeCategory.Document,
        MimeCategory.Video,
        MimeCategory.Archive,
        MimeCategory.Audio,
        MimeCategory.Executable,
        MimeCategory.Other,
    };

    /// <summary>
    /// Re-sorts a CategoryBreakdownCalculator result into the fixed legend
    /// order above. Categories with zero reclaimable bytes are already
    /// absent from `items` (CategoryBreakdownCalculator.Calculate's GroupBy
    /// only ever produces present categories), so there's nothing further
    /// to filter here - just a re-sort.
    /// </summary>
    public static IReadOnlyList<CategoryBreakdownItem> OrderForLegend(IReadOnlyList<CategoryBreakdownItem> items) =>
        items.OrderBy(i => Array.IndexOf(LegendOrder, i.Category)).ToList();

    public static IReadOnlyList<DonutChartArc> ComputeArcs(IReadOnlyList<CategoryBreakdownItem> items)
    {
        var totalBytes = items.Sum(i => i.ReclaimableBytes);
        if (totalBytes <= 0)
        {
            return Array.Empty<DonutChartArc>();
        }

        var arcs = new List<DonutChartArc>();
        var currentAngle = -90.0; // 12 o'clock, matching the reference chart's start point
        foreach (var item in items)
        {
            var sweep = item.ReclaimableBytes / (double)totalBytes * 360.0;
            arcs.Add(new DonutChartArc(item.Category, currentAngle, sweep));
            currentAngle += sweep;
        }

        return arcs;
    }
}
