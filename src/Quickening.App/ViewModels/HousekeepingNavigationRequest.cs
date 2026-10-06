using Quickening.Core.Orchestration;
using Quickening.Core.Scanning;

namespace Quickening.App.ViewModels;

/// <summary>
/// Parameters for HousekeepingPage (F5). Carries the empty-folder / zero-byte
/// findings to review, plus enough of the originating scan (result, mode,
/// threshold, label) for the page's "← Summary" back-link to return to the
/// exact ScanCompletePage the user tidied up from.
/// </summary>
public sealed record HousekeepingNavigationRequest(
    EmptyItemScanner.Result EmptyItems,
    ScanResult ScanResult,
    bool IsLargeFilesMode,
    long? ThresholdBytes,
    string? TargetLabel);
