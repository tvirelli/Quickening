using Quickening.Core.Duplicates;
using Quickening.Core.Orchestration;

namespace Quickening.App.ViewModels;

/// <summary>
/// Parameters for DuplicateFoldersPage (F8): the sets of exact folder copies to
/// review, plus enough of the originating scan for the "← Summary" back-link to
/// return to the exact ScanCompletePage the user came from.
/// </summary>
public sealed record DuplicateFoldersNavigationRequest(
    IReadOnlyList<DuplicateFolderEngine.DuplicateFolderGroup> Groups,
    ScanResult ScanResult,
    bool IsLargeFilesMode,
    long? ThresholdBytes,
    string? TargetLabel);
