using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vanara.PInvoke;
using static Vanara.PInvoke.Shell32;

namespace Quickening.Core.Deletion;

/// <summary>
/// Deletes via IFileOperation (the modern Vista+ shell delete API, wrapped by
/// Vanara.PInvoke.Shell32), not the older SHFileOperationW this class used
/// before Undo support existed. The switch was required, not cosmetic:
/// IFileOperationProgressSink.PostDeleteItem is the only way to learn WHERE
/// in the Recycle Bin a deleted file landed, and that "new location" shell
/// item is exactly what TryRestore needs to invoke the shell's own
/// "undelete" verb on later. The same sink is also the recoverability
/// verification: PostDeleteItem's psiNewlyCreated is non-null exactly when
/// the file landed in the bin, and null when IFileOperation silently
/// permanently-deleted it (e.g. it exceeds the bin's size limit for the
/// drive). This per-item signal replaced an older global before/after
/// SHQueryRecycleBin item-count comparison, which raced with every other
/// process using the bin - a concurrent bin-empty produced false failures,
/// and any concurrent delete masked a genuine permanent delete. See
/// IRecycleBinService's doc comment for exactly which IOException means
/// what.
/// </summary>
public sealed class RecycleBinService : IRecycleBinService
{
    /// <inheritdoc />
    public bool AllowProtectedPaths { get; set; }

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOSOUND = 0x00000004;

    // Some Windows versions return this instead of S_OK from
    // SHEmptyRecycleBinW when the bin is already empty - not a real failure.
    private const int E_UNEXPECTED_ALREADY_EMPTY = unchecked((int)0x8000FFFF);

    // BHID_SFObject - the well-known, stable GUID for binding a shell item
    // to its own IShellFolder (used to get the Recycle Bin's contents as an
    // enumerable folder, not just a single item). Hardcoded rather than
    // looked up via Vanara's BHID enum (which has no public enum-to-Guid
    // conversion) - this exact value is documented in the Windows SDK's
    // shlguid.h and does not change between Windows versions.
    private static readonly Guid BHID_SFObject = new("3981e224-f559-11d3-8e3a-00c04f6837d5");

    // The verb the shell's own Recycle Bin context menu registers for
    // "restore this item to where it came from" - confirmed by direct
    // experimentation (see TryRestore's own comment) that this only works
    // when resolved by canonical verb NAME via IContextMenu.GetCommandString,
    // not by invoking the string "undelete" directly via InvokeCommand's
    // lpVerbW (that fails with E_INVALIDARG even though the verb exists).
    private const string UndeleteVerb = "undelete";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // Keyed by the ORIGINAL full path; the value is the file's PHYSICAL path
    // once inside the Recycle Bin (e.g. "C:\$Recycle.Bin\S-1-5-...\$RXXXXX.ext"),
    // captured from IFileOperationProgressSink.PostDeleteItem - just a
    // string, not a live COM reference, since TryRestore re-resolves the
    // item fresh from the Recycle Bin's own shell namespace every time (see
    // TryRestore's own comment on why a plain IShellItem bound to that
    // physical path doesn't expose the "restore" verb at all). Scoped to
    // this instance's lifetime, per IRecycleBinService.TryRestore's own doc
    // comment on why this is a process-session-only feature, not a
    // persisted one. ConcurrentDictionary costs nothing here and removes any
    // doubt about SendToRecycleBin (called from a background Task.Run per
    // DeleteSelectedAsync's own comment) and TryRestore (called from the UI
    // thread, after deletes have finished) ever racing on the same key.
    private readonly ConcurrentDictionary<string, string> _restorableItems = new(StringComparer.OrdinalIgnoreCase);

    // Directories already verified reparse-free this session. Deletes batch
    // heavily within the same folders, so this collapses the per-file
    // ancestor walk to one attributes read per distinct directory. A
    // junction swapped in AFTER a directory was verified is outside this
    // cache's protection window by definition (the same is true of any
    // TOCTOU check) - the point is rejecting the pre-planted case.
    private readonly ConcurrentDictionary<string, bool> _verifiedDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects a delete when any directory component of the path is a
    /// reparse point (junction/symlink). A path captured at scan time names
    /// a location, not a file identity - if a component was later replaced
    /// with a junction, the shell would happily resolve through it and
    /// delete a file physically outside the tree the user scanned.
    /// </summary>
    private void RejectReparseAncestors(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directory))
        {
            if (_verifiedDirectories.TryGetValue(directory, out _))
            {
                return; // This directory and all its ancestors already checked.
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Component gone: the file can't exist either; let the
                // delete itself surface the real error.
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Could not verify '{directory}' before deleting '{fullPath}': {ex.Message}", ex);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"'{fullPath}' passes through '{directory}', which is a junction or symbolic link. " +
                    "Refusing to delete through a redirected path.");
            }

            _verifiedDirectories[directory] = true;
            directory = Path.GetDirectoryName(directory);
        }
    }

    public void SendToRecycleBin(string path, long? expectedSizeBytes = null, DateTime? expectedLastWriteTimeUtc = null)
    {
        var fullPath = Safety.HardBlockRules.NormalizePath(path);

        if (NetworkPathDetector.IsNetworkPath(fullPath))
        {
            throw new IOException(
                $"'{fullPath}' is on a network location, where the Recycle Bin is often " +
                "unavailable. Refusing to delete rather than risk an undetectable permanent delete.");
        }

        // Delete-time re-validation: the scan's verdict on this path can be
        // arbitrarily old by the time the user clicks Remove.
        if (Safety.HardBlockRules.IsHardBlocked(fullPath, AllowProtectedPaths))
        {
            throw new IOException($"'{fullPath}' is under a protected system location. Refusing to delete.");
        }

        RejectReparseAncestors(fullPath);

        if (expectedSizeBytes is not null || expectedLastWriteTimeUtc is not null)
        {
            FileInfo current;
            try
            {
                current = new FileInfo(fullPath);
                if (!current.Exists)
                {
                    throw new IOException($"'{fullPath}' no longer exists.");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException)
            {
                throw new IOException($"'{fullPath}' could not be re-validated before deletion: {ex.Message}", ex);
            }

            if ((expectedSizeBytes is { } size && current.Length != size) ||
                (expectedLastWriteTimeUtc is { } mtime && current.LastWriteTimeUtc != mtime))
            {
                throw new IOException(
                    $"'{fullPath}' has changed since it was scanned (size or modified time differs). " +
                    "Refusing to delete - it may no longer be a redundant copy. Re-scan to refresh results.");
            }
        }

        IShellItem? item = null;
        IFileOperation? fileOp = null;
        var sink = new DeleteCaptureSink();
        try
        {
            item = SHCreateItemFromParsingName<IShellItem>(fullPath, null)
                ?? throw new IOException($"Could not resolve a shell item for '{fullPath}'.");

            fileOp = new IFileOperation();
            // FOF_ALLOWUNDO is the flag that actually routes the delete to
            // the Recycle Bin - IFileOperation shares FILEOP_FLAGS with the
            // older SHFileOperation, and (confirmed the hard way, by a unit
            // test failure against a real file) permanently deletes without
            // it despite several blog posts claiming Recycle Bin is
            // IFileOperation's default behavior. Do not remove this flag.
            fileOp.SetOperationFlags(
                FILEOP_FLAGS.FOF_ALLOWUNDO | FILEOP_FLAGS.FOF_NOCONFIRMATION | FILEOP_FLAGS.FOF_SILENT
                | FILEOP_FLAGS.FOF_NOERRORUI | FILEOP_FLAGS.FOF_NO_UI);
            fileOp.DeleteItem(item, sink);
            fileOp.PerformOperations();

            if (fileOp.GetAnyOperationsAborted())
            {
                throw new IOException($"IFileOperation reported the operation was aborted for '{fullPath}'.");
            }

            if (sink.DeleteResultHResult is { } hrDelete && hrDelete.Failed)
            {
                throw new IOException($"IFileOperation reported the delete failed for '{fullPath}' (0x{(int)hrDelete:X8}).");
            }

            // Recoverability verification: PostDeleteItem hands back the
            // item's new Recycle Bin location on a recycle, and null when
            // the shell permanently deleted instead (no error code, no
            // aborted flag - this is the ONLY signal). Per-item and
            // race-free, unlike any global bin-count comparison.
            if (sink.NewlyCreatedItem is not { } newLocation)
            {
                throw new IOException(
                    $"'{fullPath}' was removed but did not land in the Recycle Bin - " +
                    "it was permanently deleted instead of recycled (e.g. it may exceed " +
                    "the Recycle Bin's configured size limit for this drive).");
            }

            try
            {
                _restorableItems[fullPath] = newLocation.GetDisplayName(SIGDN.SIGDN_FILESYSPATH);
            }
            catch (COMException)
            {
                // No physical path could be resolved for the new Recycle
                // Bin location - Undo simply won't be offered for this
                // file (TryRestore will report false, see its own
                // fallback path), the delete itself already succeeded.
            }
        }
        catch (COMException ex)
        {
            throw new IOException($"IFileOperation failed for '{fullPath}': {ex.Message}", ex);
        }
        finally
        {
            // Batch deletes hold hundreds of shell RCWs alive until a Gen-2
            // GC if left to finalizers - release them eagerly instead.
            sink.ReleaseCapturedItem();
            if (fileOp is not null)
            {
                Marshal.ReleaseComObject(fileOp);
            }

            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    // Restoring a Recycle Bin item only works through the shell's OWN
    // Recycle Bin namespace (FOLDERID_RecycleBinFolder) - confirmed by
    // direct experimentation that binding an IContextMenu straight off an
    // IShellItem created from the item's physical "$RXXXXX.ext" path (via
    // SHCreateItemFromParsingName or the psiNewlyCreated item
    // SendToRecycleBin already captured) gives back an ordinary FILE context
    // menu (Cut/Copy/Delete/Properties) with no "Restore" verb at all - the
    // Recycle Bin folder view is what actually contributes that verb to the
    // item's context menu, so the item has to be re-resolved as one of ITS
    // children, not addressed directly by path.
    public bool TryRestore(string originalPath)
    {
        // Key with the SAME canonicaliser SendToRecycleBin stored the entry
        // under (NormalizePath, not Path.GetFullPath) - otherwise a \\?\ /
        // 8.3 form stores one key and looks up another, and Undo silently
        // no-ops.
        var fullPath = Safety.HardBlockRules.NormalizePath(originalPath);
        // TryGetValue, NOT TryRemove: a transient failure below (shell
        // enumeration hiccup, verb invocation failure) must not consume the
        // undo entry - the item is still sitting in the bin and a second
        // Undo click should be able to succeed. The entry is removed only
        // after a confirmed restore (or when the item is verifiably gone
        // from the bin).
        if (!_restorableItems.TryGetValue(fullPath, out var binPhysicalPath))
        {
            return false;
        }

        // If something already occupies the original path, do NOT attempt the
        // shell restore. Two hazards otherwise: the no-UI undelete could
        // OVERWRITE that occupant (real data loss), and the "did it work?"
        // check below (File.Exists) would falsely pass on the occupant for a
        // restore that never happened - consuming the undo entry and
        // stranding our file in the bin. Keep the entry; the user can clear
        // the collision and retry. (A successful restore removes the entry,
        // so an occupied path with the entry still present means a DIFFERENT
        // file arrived there - refuse.)
        if (File.Exists(fullPath))
        {
            return false;
        }

        IShellItem? recycleBinItem = null;
        IShellFolder? recycleBinFolder = null;
        IEnumIDList? enumIdList = null;
        IContextMenu? contextMenu = null;
        var matchedPidl = IntPtr.Zero;
        var pidlsToFree = new List<IntPtr>();

        try
        {
            recycleBinItem = SHCreateItemInKnownFolder<IShellItem>(
                KNOWNFOLDERID.FOLDERID_RecycleBinFolder, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, null);
            if (recycleBinItem is null || recycleBinItem.BindToHandler(null, BHID_SFObject, typeof(IShellFolder).GUID) is not IShellFolder folder)
            {
                return false;
            }

            recycleBinFolder = folder;
            recycleBinFolder.EnumObjects(
                HWND.NULL, SHCONTF.SHCONTF_FOLDERS | SHCONTF.SHCONTF_NONFOLDERS | SHCONTF.SHCONTF_INCLUDEHIDDEN, out enumIdList);
            if (enumIdList is null)
            {
                return false;
            }

            var buffer = new IntPtr[1];
            while (true)
            {
                var hr = enumIdList.Next(1, buffer, out var fetched);
                if (hr.Failed || fetched == 0)
                {
                    break;
                }

                var pidl = buffer[0];
                recycleBinFolder.GetDisplayNameOf(pidl, SHGDNF.SHGDN_FORPARSING, out var strret);
                string parsingName = strret.ToString() ?? "";

                if (matchedPidl == IntPtr.Zero && string.Equals(parsingName, binPhysicalPath, StringComparison.OrdinalIgnoreCase))
                {
                    matchedPidl = pidl;
                }
                else
                {
                    pidlsToFree.Add(pidl);
                }
            }

            if (matchedPidl == IntPtr.Zero)
            {
                // The bin has since been emptied, or the item was otherwise
                // removed from it (e.g. manually restored/deleted already
                // through Explorer) - nothing left to restore, ever, so the
                // undo entry is dead weight and can go.
                _restorableItems.TryRemove(fullPath, out _);
                return false;
            }

            recycleBinFolder.GetUIObjectOf(HWND.NULL, 1, new[] { matchedPidl }, typeof(IContextMenu).GUID, IntPtr.Zero, out var contextMenuObj);
            if (contextMenuObj is not IContextMenu resolvedContextMenu)
            {
                return false;
            }

            contextMenu = resolvedContextMenu;
            if (!InvokeUndelete(contextMenu))
            {
                return false;
            }

            // The shell may complete the restore asynchronously - poll
            // briefly rather than declaring failure the same millisecond
            // InvokeCommand returns. The original path was verified EMPTY
            // above, so its appearance here genuinely means our file was
            // restored (not a pre-existing occupant).
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (File.Exists(fullPath))
                {
                    _restorableItems.TryRemove(fullPath, out _);
                    return true;
                }

                Thread.Sleep(50);
            }

            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            foreach (var pidl in pidlsToFree)
            {
                Marshal.FreeCoTaskMem(pidl);
            }

            if (matchedPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(matchedPidl);
            }

            if (contextMenu is not null)
            {
                Marshal.ReleaseComObject(contextMenu);
            }

            if (enumIdList is not null)
            {
                Marshal.ReleaseComObject(enumIdList);
            }

            if (recycleBinFolder is not null)
            {
                Marshal.ReleaseComObject(recycleBinFolder);
            }

            if (recycleBinItem is not null)
            {
                Marshal.ReleaseComObject(recycleBinItem);
            }
        }
    }

    // idCmdFirst=1 below (matching QueryContextMenu's own first-command
    // offset) means every menu item's raw ID is 1-based; InvokeCommand's
    // lpVerb, when used as a numeric offset rather than a string, must be
    // re-based back to 0 (id - idCmdFirst) - this is the standard IContextMenu
    // convention, not specific to the Recycle Bin. Resolving "which numeric
    // offset is undelete" via GetCommandString(GCS_VERBW) rather than
    // hardcoding position 0 keeps this correct even if a shell extension
    // inserts extra items before Restore, or on a non-English Windows
    // install (GetCommandString's verb names are canonical/locale-invariant,
    // unlike the visible menu text like "Restore"/"Wiederherstellen").
    private static bool InvokeUndelete(IContextMenu contextMenu)
    {
        var hMenu = User32.CreatePopupMenu();
        try
        {
            const uint idCmdFirst = 1;
            var queryResult = contextMenu.QueryContextMenu(hMenu, 0, idCmdFirst, 0x7FFF, CMF.CMF_NORMAL);
            if (queryResult.Failed)
            {
                return false;
            }

            var itemCount = User32.GetMenuItemCount(hMenu);
            for (var i = 0; i < itemCount; i++)
            {
                var id = User32.GetMenuItemID(hMenu, i);
                if (id == 0 || id == uint.MaxValue)
                {
                    continue; // separator
                }

                var offset = (int)(id - idCmdFirst);
                if (!TryGetVerb(contextMenu, offset, out var verb) || !string.Equals(verb, UndeleteVerb, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var invokeInfo = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    // NO_UI: without it, a name collision at the original
                    // path pops an ownerless shell prompt from whatever
                    // background thread this runs on.
                    fMask = CMIC.CMIC_MASK_FLAG_NO_UI,
                    lpVerb = offset,
                    nShow = ShowWindowCommand.SW_HIDE,
                };
                return contextMenu.InvokeCommand(invokeInfo).Succeeded;
            }

            return false;
        }
        finally
        {
            User32.DestroyMenu(hMenu);
        }
    }

    private static bool TryGetVerb(IContextMenu contextMenu, int offset, out string verb)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            var hr = contextMenu.GetCommandString(new IntPtr(offset), GCS.GCS_VERBW, IntPtr.Zero, buffer, 256);
            verb = hr.Succeeded ? Marshal.PtrToStringUni(buffer) ?? "" : "";
            return hr.Succeeded;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Fires once per file IFileOperation.DeleteItem processes - every other
    // IFileOperationProgressSink member is a no-op here (return S_OK) since
    // SendToRecycleBin only ever queues a single DeleteItem per call; this
    // sink exists purely to capture PostDeleteItem's psiNewlyCreated (the
    // deleted file's new location inside the Recycle Bin), the one piece of
    // information no non-IFileOperation delete API exposes at all.
    private sealed class DeleteCaptureSink : IFileOperationProgressSink
    {
        public IShellItem? NewlyCreatedItem { get; private set; }

        /// <summary>Delete outcome as reported by the shell; null if PostDeleteItem never fired.</summary>
        public HRESULT? DeleteResultHResult { get; private set; }

        public void ReleaseCapturedItem()
        {
            if (NewlyCreatedItem is { } captured)
            {
                NewlyCreatedItem = null;
                Marshal.ReleaseComObject(captured);
            }
        }

        public HRESULT PostDeleteItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem? psiNewlyCreated)
        {
            DeleteResultHResult = hrDelete;
            if (hrDelete.Succeeded && psiNewlyCreated is not null)
            {
                NewlyCreatedItem = psiNewlyCreated;
            }

            return HRESULT.S_OK;
        }

        public HRESULT StartOperations() => HRESULT.S_OK;
        public HRESULT FinishOperations(HRESULT hrResult) => HRESULT.S_OK;
        public HRESULT PreRenameItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem, string pszNewName) => HRESULT.S_OK;
        public HRESULT PostRenameItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem, string pszNewName, HRESULT hrRename, IShellItem psiNewlyCreated) => HRESULT.S_OK;
        public HRESULT PreMoveItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => HRESULT.S_OK;
        public HRESULT PostMoveItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated) => HRESULT.S_OK;
        public HRESULT PreCopyItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => HRESULT.S_OK;
        public HRESULT PostCopyItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated) => HRESULT.S_OK;
        public HRESULT PreDeleteItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiItem) => HRESULT.S_OK;
        public HRESULT PreNewItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiDestinationFolder, string pszNewName) => HRESULT.S_OK;
        public HRESULT PostNewItem(TRANSFER_SOURCE_FLAGS dwFlags, IShellItem psiDestinationFolder, string pszNewName, string? pszTemplateName, uint dwFileAttributes, HRESULT hrNew, IShellItem psiNewItem) => HRESULT.S_OK;
        public HRESULT UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => HRESULT.S_OK;
        public HRESULT ResetTimer() => HRESULT.S_OK;
        public HRESULT PauseTimer() => HRESULT.S_OK;
        public HRESULT ResumeTimer() => HRESULT.S_OK;
    }

    public (long ItemCount, long TotalSizeBytes) GetRecycleBinTotals()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        var hr = SHQueryRecycleBinW(null, ref info);
        if (hr != 0)
        {
            throw new IOException($"SHQueryRecycleBin failed with HRESULT 0x{hr:X8}.");
        }

        return (info.i64NumItems, info.i64Size);
    }

    public void EmptyRecycleBin()
    {
        var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOSOUND);
        if (hr != 0 && hr != E_UNEXPECTED_ALREADY_EMPTY)
        {
            throw new IOException($"SHEmptyRecycleBin failed with HRESULT 0x{hr:X8}.");
        }
    }

}
