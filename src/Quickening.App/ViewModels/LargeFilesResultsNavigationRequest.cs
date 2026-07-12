using Quickening.Core.Orchestration;

namespace Quickening.App.ViewModels;

/// <summary>
/// Navigation parameter for LargeFilesResultsPage - same wrapping-record
/// shape as ResultsNavigationRequest/ScanCompleteNavigationRequest (see
/// those files). CandidateFiles is the threshold-filtered flat file list,
/// built from ScanOrchestrator.ScanForLargeFiles's ScanResult.AllFiles -
/// every file in the scanned folder, not just ones with a duplicate
/// elsewhere. ScanResult/TargetLabel are optional so the two-argument
/// MainWindow.ShowLargeFilesResults call still works when a caller has no
/// ScanResult on hand - LargeFilesResultsPage's own "← Summary" link falls
/// back to Home in that case (see LargeFilesResultsPage.xaml.cs).
/// </summary>
public sealed record LargeFilesResultsNavigationRequest(
    IReadOnlyList<SelectableFile> CandidateFiles,
    long ThresholdBytes,
    ScanResult? ScanResult = null,
    string? TargetLabel = null);
