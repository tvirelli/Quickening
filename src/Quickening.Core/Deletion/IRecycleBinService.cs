namespace Quickening.Core.Deletion;

public interface IRecycleBinService
{
    /// <summary>
    /// Mirrors Settings' "Allow scanning protected system paths" toggle.
    /// When false (default), SendToRecycleBin refuses paths under blocked
    /// system roots at delete time; callers whose results legitimately came
    /// from a protected-paths scan must set this before deleting, or every
    /// such delete fails. The hidden+system per-file rule is never
    /// bypassable regardless.
    /// </summary>
    bool AllowProtectedPaths { get; set; }

    /// <summary>
    /// Moves the file at <paramref name="path"/> to the Windows Recycle Bin.
    /// Never shows UI, never throws OperationCanceledException. Re-validates
    /// safety at delete time, because arbitrary time passes between scan and
    /// delete: rejects hard-blocked system paths, rejects paths with a
    /// reparse-point (junction/symlink) directory component - a component
    /// swapped for a junction after the scan would otherwise redirect the
    /// delete outside the scanned tree - and, when
    /// <paramref name="expectedSizeBytes"/>/<paramref name="expectedLastWriteTimeUtc"/>
    /// are provided, rejects a file whose content changed since the scan
    /// decided it was expendable. Throws IOException in several distinct
    /// cases the caller may want to handle differently:
    /// - Any of the delete-time re-validations above failed - rejected
    ///   upfront, the file is untouched.
    /// - The file is on a network/UNC location, where the Recycle Bin is
    ///   often unavailable - rejected upfront, the file is untouched.
    /// - The shell operation itself failed (locked, access denied, already
    ///   gone, or another shell operation failure) - the file is untouched
    ///   or in an unclear state, but not confirmed deleted.
    /// - The operation reported success but the file did not land in the
    ///   Recycle Bin (e.g. it exceeded the Recycle Bin's configured size
    ///   limit for that drive, and Windows silently permanently deleted it
    ///   instead) - in this case the file MAY ALREADY BE PERMANENTLY GONE.
    ///   This method cannot prevent that outcome, only detect and report it
    ///   after the fact.
    /// </summary>
    void SendToRecycleBin(string path, long? expectedSizeBytes = null, DateTime? expectedLastWriteTimeUtc = null);

    /// <summary>
    /// Current total item count and combined size of the Windows Recycle
    /// Bin, across all drives - not scoped to items Quickening itself put
    /// there. Used by the History screen's Empty-Bin confirmation to show a
    /// live "222 files - 51.9 GB" total before the user commits.
    /// </summary>
    (long ItemCount, long TotalSizeBytes) GetRecycleBinTotals();

    /// <summary>
    /// Empties the user's ENTIRE Windows Recycle Bin - every item in it,
    /// not just ones Quickening removed. This is the one genuinely
    /// permanent, unrecoverable action anywhere in this app; callers must
    /// get explicit, unambiguous confirmation before calling this (see
    /// History's hold-to-confirm dialog).
    /// </summary>
    void EmptyRecycleBin();

    /// <summary>
    /// Attempts to restore a file this same IRecycleBinService instance just
    /// sent to the Recycle Bin via SendToRecycleBin, back to its original
    /// path - powers the Results/Large Files "Undo" toast (new-screens 4j).
    /// Only works for deletions from THIS process session (the underlying
    /// shell item reference isn't persisted anywhere - restoring an older
    /// History entry isn't supported by this method, by design; History
    /// only ever links out to the Recycle Bin itself, never calls this).
    /// Returns false rather than throwing for every known failure mode:
    /// the path was never recorded, the bin has since been emptied, the
    /// original location is now occupied by something else, or the shell's
    /// own "undelete" verb otherwise refuses - a failed restore should just
    /// leave the toast's Undo button quietly ineffective, not crash the UI.
    /// </summary>
    bool TryRestore(string originalPath);
}
