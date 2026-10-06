using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Quickening.App.Controls;
using Quickening.App.Formatting;
using Quickening.App.ViewModels;
using Quickening.Core.Duplicates;
using Quickening.Core.Orchestration;

namespace Quickening.App.Views;

public sealed partial class ScanCompletePage : Page
{
    private ScanResult? _scanResult;
    private bool _isLargeFilesMode;
    private string? _targetLabel;

    // Populated by ShowLargeFilesSummary from the same threshold-filtered
    // file list it builds for the segmented bar chart - ReviewResults_Click
    // reuses this instead of recomputing it a second time (see
    // ShowLargeFilesSummary below).
    private IReadOnlyList<SelectableFile>? _largeFilesCandidates;
    private long _largeFilesThresholdBytes;

    public ScanCompletePage()
    {
        InitializeComponent();
    }

    // Unlike ResultsPage, this page doesn't set NavigationCacheMode="Enabled",
    // so the Frame constructs a fresh instance on every navigation here -
    // there's no cached _scanResult that could go stale, so no
    // ReferenceEquals guard is needed the way ResultsPage.OnNavigatedTo has one.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not ScanCompleteNavigationRequest request)
        {
            return;
        }

        _scanResult = request.ScanResult;
        _isLargeFilesMode = request.IsLargeFilesMode;
        _targetLabel = request.TargetLabel;

        if (_isLargeFilesMode)
        {
            ShowLargeFilesSummary(request);
        }
        else
        {
            ShowDuplicatesSummary(request);
        }

        ConfigureHousekeepingCallout();
        ConfigureDuplicateFoldersCallout();
    }

    // Shows the "also found N sets of duplicate folders — Review folders" nudge
    // only when the scan turned any up (F8). Duplicates mode only (Large Files
    // has no duplicate groups to derive folder copies from).
    private void ConfigureDuplicateFoldersCallout()
    {
        var groups = _scanResult?.DuplicateFolders;
        if (_isLargeFilesMode || groups is null || groups.Count == 0)
        {
            DuplicateFoldersCallout.Visibility = Visibility.Collapsed;
            return;
        }

        DuplicateFoldersText.Text = groups.Count == 1
            ? "Also found 1 set of identical folders worth de-duplicating."
            : $"Also found {groups.Count} sets of identical folders worth de-duplicating.";
        DuplicateFoldersCallout.Visibility = Visibility.Visible;
    }

    private void DuplicateFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is not { DuplicateFolders.Count: > 0 } result)
        {
            return;
        }

        ((MainWindow)App.MainWindowInstance!).ShowDuplicateFolders(new DuplicateFoldersNavigationRequest(
            result.DuplicateFolders, result, _isLargeFilesMode,
            _isLargeFilesMode ? _largeFilesThresholdBytes : null, _targetLabel));
    }

    // Shows the "also found N empty folders · M zero-byte files — Tidy up" nudge
    // only when the scan produced any (F5). Purely passive: it never competes
    // with the primary Review/Select action above.
    private void ConfigureHousekeepingCallout()
    {
        var items = _scanResult?.EmptyItems;
        if (items is null || items.IsEmpty)
        {
            HousekeepingCallout.Visibility = Visibility.Collapsed;
            return;
        }

        var parts = new List<string>();
        if (items.EmptyFolders.Count > 0)
        {
            parts.Add($"{items.EmptyFolders.Count} empty folder{(items.EmptyFolders.Count == 1 ? "" : "s")}");
        }

        if (items.ZeroByteFiles.Count > 0)
        {
            parts.Add($"{items.ZeroByteFiles.Count} zero-byte file{(items.ZeroByteFiles.Count == 1 ? "" : "s")}");
        }

        HousekeepingText.Text = $"Also found {string.Join(" and ", parts)} worth tidying.";
        HousekeepingCallout.Visibility = Visibility.Visible;
    }

    private void Housekeeping_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is not { EmptyItems: { IsEmpty: false } items } result)
        {
            return;
        }

        ((MainWindow)App.MainWindowInstance!).ShowHousekeeping(new HousekeepingNavigationRequest(
            items, result, _isLargeFilesMode,
            _isLargeFilesMode ? _largeFilesThresholdBytes : null, _targetLabel));
    }

    private void ShowDuplicatesSummary(ScanCompleteNavigationRequest request)
    {
        DuplicatesPanel.Visibility = Visibility.Visible;
        BarChart.Visibility = Visibility.Collapsed;
        SelectRecommendedButton.Visibility = Visibility.Visible;
        ReviewResultsSecondaryButton.Visibility = Visibility.Visible;
        ReviewResultsPrimaryButton.Visibility = Visibility.Collapsed;

        var scanResult = request.ScanResult;
        var breakdown = DonutChartMath.OrderForLegend(CategoryBreakdownCalculator.Calculate(scanResult.DuplicateGroups));

        Chart.SetSegments(breakdown);

        BreakdownList.ItemsSource = breakdown
            .Select(item => new BreakdownRow(
                CategoryLabelFormatter.PluralName(item.Category),
                new SolidColorBrush(CategoryColors.For(item.Category)),
                FileSizeFormatter.Format(item.ReclaimableBytes)))
            .ToList();

        var totalBytes = breakdown.Sum(item => item.ReclaimableBytes);
        // "Duplicate files" = every file skipped as a keeper's group-mate
        // (Files.Count - 1 per group) - the same reclaimable-file
        // semantics CategoryBreakdownCalculator already uses.
        var fileCount = scanResult.DuplicateGroups.Sum(g => g.Files.Count - 1);
        var groupCount = scanResult.DuplicateGroups.Count;

        EyebrowText.Text = BuildEyebrowText(request.TargetLabel, isLargeFilesMode: false, thresholdBytes: null);
        HeadlineText.Text = $"{FileSizeFormatter.Format(totalBytes)} {RandomPhrases.ReclaimClause()}";

        var emphasisBrush = EmphasisBrush();
        SubtitleText.Inlines.Clear();
        SubtitleText.Inlines.Add(new Run { Text = $"{fileCount} duplicate files", Foreground = emphasisBrush, FontWeight = FontWeights.Bold });
        SubtitleText.Inlines.Add(new Run { Text = " in " });
        SubtitleText.Inlines.Add(new Run { Text = $"{groupCount} groups", Foreground = emphasisBrush, FontWeight = FontWeights.Bold });
        SubtitleText.Inlines.Add(new Run { Text = " — keep one of each, and that space is yours." });

        ReassuranceText.Text = scanResult.SimilarityGroups.Count > 0
            ? $"Recommended keeps the newest copy in every group. There {(scanResult.SimilarityGroups.Count == 1 ? "is" : "are")} also {scanResult.SimilarityGroups.Count} similar-photo group{(scanResult.SimilarityGroups.Count == 1 ? "" : "s")} worth a look in Review Results."
            : "Recommended keeps the newest copy in every group. You'll still confirm before anything moves.";
    }

    private void ShowLargeFilesSummary(ScanCompleteNavigationRequest request)
    {
        DuplicatesPanel.Visibility = Visibility.Collapsed;
        BarChart.Visibility = Visibility.Visible;
        SelectRecommendedButton.Visibility = Visibility.Collapsed;
        ReviewResultsSecondaryButton.Visibility = Visibility.Collapsed;
        ReviewResultsPrimaryButton.Visibility = Visibility.Visible;

        var scanResult = request.ScanResult;
        var thresholdBytes = request.ThresholdBytes ?? 0;

        // scanResult came from ScanForLargeFiles here, so AllFiles is every
        // file in the folder, not just ones with a duplicate elsewhere.
        var candidates = scanResult.AllFiles!
            .Where(f => f.SizeBytes >= thresholdBytes)
            .ToList();

        // Stash the same list ReviewResults_Click needs so it doesn't have
        // to filter AllFiles a second time - see the field doc comment above.
        _largeFilesThresholdBytes = thresholdBytes;
        _largeFilesCandidates = candidates
            .Select(f => new SelectableFile
            {
                Path = f.Path,
                SizeBytes = f.SizeBytes,
                Category = f.Category,
                LastWriteTimeUtc = f.LastWriteTimeUtc,
            })
            .ToList();

        var segments = candidates
            .GroupBy(f => f.Category)
            .Select(g => new BarSegment(
                CategoryLabelFormatter.PluralName(g.Key),
                CategoryColors.For(g.Key),
                g.Sum(f => f.SizeBytes),
                g.Count()))
            .OrderByDescending(s => s.SizeBytes)
            .ToList();

        BarChart.SetSegments(segments);

        var totalBytes = candidates.Sum(f => f.SizeBytes);
        var thresholdText = FileSizeFormatter.Format(thresholdBytes);

        EyebrowText.Text = BuildEyebrowText(request.TargetLabel, isLargeFilesMode: true, thresholdBytes: thresholdBytes);
        HeadlineText.Text = candidates.Count == 1
            ? $"1 {RandomPhrases.Heavyweights(plural: false)}, {FileSizeFormatter.Format(totalBytes)} total."
            : $"{candidates.Count} {RandomPhrases.Heavyweights(plural: true)}, {FileSizeFormatter.Format(totalBytes)} total.";

        var emphasisBrush = EmphasisBrush();
        SubtitleText.Inlines.Clear();
        SubtitleText.Inlines.Add(new Run { Text = "Every one of these is " });
        SubtitleText.Inlines.Add(new Run { Text = $"{thresholdText} or more", Foreground = emphasisBrush, FontWeight = FontWeights.Bold });
        SubtitleText.Inlines.Add(new Run { Text = ". Removing one frees exactly its size." });

        ReassuranceText.Text = "No auto-selection for large files — each one deserves a human look first.";
    }

    private static string BuildEyebrowText(string? targetLabel, bool isLargeFilesMode, long? thresholdBytes)
    {
        var label = string.IsNullOrWhiteSpace(targetLabel) ? "your files" : targetLabel;
        var text = isLargeFilesMode && thresholdBytes is { } threshold
            ? $"Scan complete — {label} · over {FileSizeFormatter.Format(threshold)}"
            : $"Scan complete — {label}";

        // XAML has no text-transform; 2e/2f's eyebrow renders uppercase, so
        // build the already-uppercase string here instead.
        return text.ToUpperInvariant();
    }

    private static SolidColorBrush EmphasisBrush() =>
        (SolidColorBrush)Application.Current.Resources["TextBodyEmphasisBrush"];

    private void ReviewResults_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null)
        {
            return;
        }

        if (_isLargeFilesMode)
        {
            if (_largeFilesCandidates is null)
            {
                return;
            }

            ((MainWindow)App.MainWindowInstance!).ShowLargeFilesResults(
                _largeFilesCandidates, _largeFilesThresholdBytes, _scanResult, _targetLabel);
            return;
        }

        ((MainWindow)App.MainWindowInstance!).ShowResults(_scanResult, targetLabel: _targetLabel);
    }

    private void SelectRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null || _isLargeFilesMode)
        {
            return;
        }

        ((MainWindow)App.MainWindowInstance!).ShowResults(_scanResult, preSelectRecommended: true, targetLabel: _targetLabel);
    }

    private void NewScan_Click(object sender, RoutedEventArgs e)
    {
        ((MainWindow)App.MainWindowInstance!).ShowHome();
    }

    private sealed record BreakdownRow(string Label, SolidColorBrush SwatchBrush, string SizeText);
}
