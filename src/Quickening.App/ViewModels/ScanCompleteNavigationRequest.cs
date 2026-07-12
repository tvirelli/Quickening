using Quickening.Core.Orchestration;

namespace Quickening.App.ViewModels;

/// <summary>
/// Navigation parameter for ScanCompletePage, wrapping the ScanResult with
/// which scan-type mode produced it - same wrapping-record shape as
/// ResultsNavigationRequest (see that file), rather than a bare ScanResult,
/// since ScanCompletePage needs to know Duplicates-vs-Large-Files (and, for
/// Large Files, the threshold that was used) to pick which visual state to
/// show and which breakdown math to run.
///
/// IsLargeFilesMode/ThresholdBytes/TargetLabel are all optional with safe
/// defaults so a plain single-argument ShowScanComplete(result) call would
/// still compile and produce a Duplicates-mode summary - see
/// MainWindow.ShowScanComplete. HomePage.RouteScanOutcome always passes
/// TargetLabel (the scanned folder/tile name, e.g. "Downloads") through in
/// practice - see ScanCompletePage.xaml.cs.
/// </summary>
public sealed record ScanCompleteNavigationRequest(
    ScanResult ScanResult,
    bool IsLargeFilesMode = false,
    long? ThresholdBytes = null,
    string? TargetLabel = null);
