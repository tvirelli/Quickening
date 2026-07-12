namespace Quickening.App;

/// <summary>
/// Process-wide "one scan at a time" gate. Three things can start a scan -
/// the user (HomePage CTA / prefill auto-start), the scheduled-scan timer,
/// and tray/toast navigation back into a new scan - and they were written
/// independently; without this gate two of them can run concurrently
/// against the same SqliteStore and disk. The store itself is thread-safe
/// (internal lock), so this is about not burning the disk twice over and
/// not letting an orphaned scan's completion yank navigation around.
/// </summary>
public static class ScanCoordinator
{
    private static int _active;

    /// <summary>Attempts to claim the scan slot; the caller MUST call End() when the scan finishes (success, cancel, or fault).</summary>
    public static bool TryBegin() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;

    public static void End() => Interlocked.Exchange(ref _active, 0);

    public static bool IsScanActive => Volatile.Read(ref _active) == 1;
}
