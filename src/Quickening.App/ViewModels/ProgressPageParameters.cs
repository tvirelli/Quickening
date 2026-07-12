using Microsoft.UI.Xaml.Controls;

namespace Quickening.App.ViewModels;

public sealed record ProgressUpdate(
    int ItemsProcessed,
    int? TotalItems,
    string? CurrentItemLabel,
    int? DuplicateGroupsFoundSoFar = null,
    long? ReclaimableBytesSoFar = null,
    // When non-null, ProgressPage swaps its headline/subtitle to reflect the
    // current stage (e.g. Enumerating -> Comparing -> Finishing up). Null on
    // ticks that don't change the stage, so the page only re-renders the
    // text on an actual transition.
    string? StageHeadline = null,
    string? StageSubtitle = null);

public enum ProgressRingHue { Accent, Success }

/// <summary>
/// Everything ProgressPage needs to run and display one long operation.
/// Operation is the actual work (wrapped by the caller, which knows how to
/// invoke the specific Core method and translate its progress type into
/// ProgressUpdate), returning whatever result Completed needs as a plain
/// object. Completed receives that value directly once Operation finishes
/// successfully. If Operation throws (other than OperationCanceledException),
/// ProgressPage handles that itself unless OnError is supplied - HomePage's
/// scan-kickoff methods use OnError to route scan failures to a dedicated
/// full-screen error state (ScanFailedPage) instead of a generic dialog.
/// On cancellation, ProgressPage navigates
/// home by default - OnCancelled lets a caller override that (a cancelled
/// removal should return to the already-pruned Results screen instead).
/// </summary>
public sealed record ProgressPageParameters(
    string Headline,
    Func<IProgress<ProgressUpdate>, CancellationToken, Task<object?>> Operation,
    Func<object?, Task> Completed,
    ProgressRingHue RingHue = ProgressRingHue.Accent,
    Action? OnCancelled = null,
    Func<Exception, Task>? OnError = null,
    string? TargetLabel = null);
