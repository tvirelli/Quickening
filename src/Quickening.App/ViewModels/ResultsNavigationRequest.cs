using Quickening.Core.Orchestration;

namespace Quickening.App.ViewModels;

/// <summary>
/// Navigation parameter for ResultsPage, wrapping the ScanResult with
/// whether to pre-select files after loading ("Select Recommended" on
/// Scan-Completed) and the scanned folder/tile's display name for the
/// screen title (e.g. "Duplicates in Downloads", matching
/// ScanCompletePage/LargeFilesResultsPage's own titles) - both optional
/// with safe defaults so a plain ScanResult (no wrapper) still works for
/// callers that don't have a label on hand (e.g. re-navigating here after
/// a delete completes, where ResultsPage.OnNavigatedTo's ReferenceEquals
/// guard means the label from the FIRST load already persists and doesn't
/// need to be re-supplied). The "&lt; Summary" back-link goes the other
/// direction (Results -&gt; ScanCompletePage), so it's never a caller of
/// ShowResults.
/// </summary>
public sealed record ResultsNavigationRequest(ScanResult ScanResult, bool PreSelectRecommended = false, string? TargetLabel = null);
