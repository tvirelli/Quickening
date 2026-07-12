using Windows.UI;

namespace Quickening.App.Controls;

/// <summary>
/// One category's slice of a Large-Files-mode segmented bar (screen 2f).
/// Label/Color are already display-ready (a plural category name and its
/// CategoryColors.For swatch) rather than a bare MimeCategory, since
/// SegmentedBarChart itself has no category lookup of its own - the caller
/// (ScanCompletePage) does that mapping once when building this list.
/// Count is the number of files contributing to SizeBytes, shown alongside
/// the size in 2f's legend ("35.6 GB · 14") unlike the donut's legend
/// (2e), which is size-only.
/// </summary>
public sealed record BarSegment(string Label, Color Color, long SizeBytes, int Count);

/// <summary>
/// A BarSegment paired with its computed share of the bar's total width.
/// </summary>
public readonly record struct BarSegmentLayout(BarSegment Segment, double WidthFraction);

/// <summary>
/// Pure proportional-width computation, kept separate from
/// SegmentedBarChart's rendering so the math is unit-testable without any
/// WinUI dependency - the same split DonutChartMath keeps from DonutChart.
/// </summary>
public static class SegmentedBarChartMath
{
    public static IReadOnlyList<BarSegmentLayout> ComputeLayout(IReadOnlyList<BarSegment> segments)
    {
        var totalBytes = segments.Sum(s => s.SizeBytes);
        if (totalBytes <= 0)
        {
            return Array.Empty<BarSegmentLayout>();
        }

        return segments
            .Select(s => new BarSegmentLayout(s, s.SizeBytes / (double)totalBytes))
            .ToList();
    }
}
