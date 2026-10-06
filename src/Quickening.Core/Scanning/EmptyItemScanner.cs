using Quickening.Core.Safety;

namespace Quickening.Core.Scanning;

/// <summary>
/// Housekeeping byproduct of a scan (F5): the empty folders and zero-byte
/// files under the scanned root. Cheap - a single metadata walk, no hashing.
/// Empty folders are those that (recursively) contain no non-empty files and
/// no non-empty subfolders, so a folder holding only other empty folders is
/// itself reported empty. Kept separate from the duplicate/large-file results
/// because these aren't "copies" or "space hogs" - they're clutter to tidy.
/// </summary>
public sealed class EmptyItemScanner
{
    public sealed class Result
    {
        public required IReadOnlyList<string> EmptyFolders { get; init; }
        public required IReadOnlyList<string> ZeroByteFiles { get; init; }
        public bool IsEmpty => EmptyFolders.Count == 0 && ZeroByteFiles.Count == 0;
    }

    /// <summary>
    /// Walks rootPath once. A zero-byte file is any real (non-placeholder,
    /// non-reparse) file whose length is 0. Skips hard-blocked roots the same
    /// way FileEnumerator does. Best-effort: unreadable directories are
    /// skipped rather than throwing.
    /// </summary>
    public Result Find(string rootPath, bool includeHiddenFiles = false, bool allowProtectedPaths = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        var normalizedRoot = HardBlockRules.NormalizePath(rootPath);
        if (!Directory.Exists(normalizedRoot) ||
            (HardBlockRules.IsUnderBlockedRoot(normalizedRoot) && !allowProtectedPaths))
        {
            return new Result { EmptyFolders = Array.Empty<string>(), ZeroByteFiles = Array.Empty<string>() };
        }

        var emptyFolders = new List<string>();
        var zeroByteFiles = new List<string>();
        Visit(normalizedRoot, includeHiddenFiles, emptyFolders, zeroByteFiles, cancellationToken);
        return new Result { EmptyFolders = emptyFolders, ZeroByteFiles = zeroByteFiles };
    }

    // Returns true when `dir` HAS content - a non-empty file, or a subfolder
    // that itself has content - and false when it's (recursively) empty.
    // Records empty folders and zero-byte files as it goes. A post-order walk
    // so a parent can see whether all its children turned out empty.
    private static bool Visit(string dir, bool includeHidden, List<string> emptyFolders, List<string> zeroByteFiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasContent = false;

        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't read it - treat as non-empty so we never suggest deleting
            // a folder we couldn't actually inspect. (true = "has content":
            // returning false here told the PARENT this child was empty, so
            // the parent was offered in Tidy up - QA-1.)
            return true;
        }

        foreach (var sub in subdirs)
        {
            var info = new DirectoryInfo(sub);
            // Reparse-point dirs (junctions/symlinks) are never recursed - same
            // cycle/scope guard FileEnumerator uses. System dirs never; hidden
            // only when the toggle allows. Such a dir counts as "content" so
            // its parent isn't reported empty.
            if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0 ||
                (!includeHidden && (info.Attributes & FileAttributes.Hidden) != 0))
            {
                hasContent = true;
                continue;
            }

            if (Visit(sub, includeHidden, emptyFolders, zeroByteFiles, cancellationToken))
            {
                hasContent = true;
            }
        }

        string[] filePaths;
        try
        {
            filePaths = Directory.GetFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same rule as an unreadable directory listing above: files we
            // can't see count as content.
            return true;
        }

        foreach (var file in filePaths)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hasContent = true;
                continue;
            }

            if ((info.Attributes & FileAttributes.System) != 0 ||
                (!includeHidden && (info.Attributes & FileAttributes.Hidden) != 0))
            {
                hasContent = true;
                continue;
            }

            // A reparse-point file (symlink) counts as content and is never a
            // "zero-byte" candidate - deleting it could orphan its target.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                hasContent = true;
                continue;
            }

            if (info.Length == 0)
            {
                zeroByteFiles.Add(file);
            }
            else
            {
                hasContent = true;
            }
        }

        if (!hasContent)
        {
            emptyFolders.Add(dir);
        }

        return hasContent;
    }
}
