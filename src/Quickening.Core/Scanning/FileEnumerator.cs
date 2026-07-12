using System.IO.Enumeration;
using Quickening.Core.Models;
using Quickening.Core.Safety;

namespace Quickening.Core.Scanning;

public sealed class FileEnumerator
{
    private static readonly Dictionary<string, MimeCategory> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MimeCategory.Image,
        [".jpeg"] = MimeCategory.Image,
        [".png"] = MimeCategory.Image,
        [".gif"] = MimeCategory.Image,
        [".mp4"] = MimeCategory.Video,
        [".mov"] = MimeCategory.Video,
        [".mkv"] = MimeCategory.Video,
        [".mp3"] = MimeCategory.Audio,
        [".wav"] = MimeCategory.Audio,
        [".flac"] = MimeCategory.Audio,
        [".pdf"] = MimeCategory.Document,
        [".docx"] = MimeCategory.Document,
        [".txt"] = MimeCategory.Document,
        [".zip"] = MimeCategory.Archive,
        [".rar"] = MimeCategory.Archive,
        [".7z"] = MimeCategory.Archive,
        [".exe"] = MimeCategory.Executable,
        [".msi"] = MimeCategory.Executable,
    };

    /// <summary>
    /// Lazily walks <paramref name="rootPath"/> and yields a <see cref="FileRecord"/> per
    /// file found, skipping hard-blocked paths. This is a single-pass, deferred-execution
    /// sequence: enumerating it more than once re-walks the filesystem and produces fresh
    /// FileRecord instances, discarding any PartialHash/FullHash a previous pass set.
    /// Callers that mutate records (e.g. after hashing) should materialize the result
    /// once with .ToList() before doing so.
    /// </summary>
    /// <param name="includeHiddenFiles">
    /// Hidden files/directories are skipped by default (Settings' "Scan
    /// hidden files" toggle, off by default) - pass true to include them.
    /// System-attributed items (regardless of this flag) are always
    /// skipped unconditionally; OS-internal System-attributed content
    /// should never be scannable regardless of user preference.
    /// </param>
    /// <param name="excludeCloudPlaceholders">
    /// Skips cloud-sync "online-only" placeholder files entirely (new-
    /// screens 4m's "Skip online-only files" choice) - false by default so
    /// a plain Enumerate call (e.g. the 4m pre-scan count itself) still
    /// sees every file, placeholder or not.
    /// </param>
    public IEnumerable<FileRecord> Enumerate(
        string rootPath, bool includeHiddenFiles = false, bool allowProtectedPaths = false,
        bool excludeCloudPlaceholders = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        // Canonicalize once so \\?\ prefixes and 8.3 short names
        // (C:\PROGRA~1) can't dodge the blocked-roots comparison below.
        var normalizedRoot = HardBlockRules.NormalizePath(rootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Root path not found: {rootPath}");
        }

        if (HardBlockRules.IsUnderBlockedRoot(normalizedRoot) && !allowProtectedPaths)
        {
            return Enumerable.Empty<FileRecord>();
        }

        return EnumerateCore(normalizedRoot, includeHiddenFiles, allowProtectedPaths, excludeCloudPlaceholders, cancellationToken);
    }

    /// <summary>
    /// Cheap pre-scan pass for new-screens 4m's cloud-placeholder warning -
    /// same walk as Enumerate (metadata only, no hashing), just reduced to
    /// the count and total size of files that are cloud placeholders. Called
    /// before the real scan starts; a non-zero count is what triggers 4m's
    /// dialog.
    /// </summary>
    public (int Count, long TotalBytes) PreviewCloudPlaceholders(
        string rootPath, bool includeHiddenFiles = false, bool allowProtectedPaths = false,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        var totalBytes = 0L;
        foreach (var file in Enumerate(rootPath, includeHiddenFiles, allowProtectedPaths, excludeCloudPlaceholders: false, cancellationToken))
        {
            if (!file.IsCloudPlaceholder)
            {
                continue;
            }

            count++;
            totalBytes += file.SizeBytes;
        }

        return (count, totalBytes);
    }

    // Cloud-sync providers (OneDrive, Dropbox, Google Drive) mark an
    // "online-only" placeholder file with FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    // (0x00400000) - not exposed as a named member of .NET's FileAttributes
    // enum, so it's checked via a raw bitwise test. FileAttributes.Offline
    // (0x1000, "data is not immediately available") is treated the same way,
    // since some providers/APIs surface placeholders through that bit
    // instead.
    private const int RecallOnDataAccess = 0x00400000;

    private static bool IsCloudPlaceholder(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.Offline) || ((int)attributes & RecallOnDataAccess) != 0;

    private IEnumerable<FileRecord> EnumerateCore(
        string rootPath, bool includeHiddenFiles, bool allowProtectedPaths, bool excludeCloudPlaceholders,
        CancellationToken cancellationToken)
    {
        // Blocked roots only matter when the scan root actually contains one
        // (e.g. scanning C:\ contains C:\Windows). Precomputing the relevant
        // set means the common case (scanning Downloads etc.) pays zero
        // per-directory path allocations for the check.
        var blockedRootsUnderScan = allowProtectedPaths
            ? Array.Empty<string>()
            : HardBlockRules.BlockedRootPaths
                .Where(r => r.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
                            r.StartsWith(EnsureTrailingSeparator(rootPath), StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Attribute filtering is done in the predicates below so that
            // reparse-point FILES (OneDrive/Dropbox cloud placeholders carry
            // FILE_ATTRIBUTE_REPARSE_POINT even when fully hydrated) are
            // still enumerated, while reparse-point DIRECTORIES (junctions,
            // symlinked dirs - the cycle risk) are never recursed into.
            AttributesToSkip = FileAttributes.None,
        };

        var enumerable = new FileSystemEnumerable<FileRecord>(
            rootPath,
            (ref FileSystemEntry entry) => new FileRecord
            {
                Path = entry.ToFullPath(),
                SizeBytes = entry.Length,
                LastWriteTimeUtc = entry.LastWriteTimeUtc.UtcDateTime,
                Category = ExtensionMap.GetValueOrDefault(Path.GetExtension(entry.FileName).ToString(), MimeCategory.Other),
                IsCloudPlaceholder = IsCloudPlaceholder(entry.Attributes),
            },
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                !entry.IsDirectory && ShouldIncludeFile(ref entry, includeHiddenFiles),
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                ShouldRecurseInto(ref entry, includeHiddenFiles, blockedRootsUnderScan),
        };

        foreach (var record in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.IsCloudPlaceholder && excludeCloudPlaceholders)
            {
                continue;
            }

            if (JunkFileRules.IsJunkFile(record.Path))
            {
                continue;
            }

            yield return record;
        }
    }

    private static bool ShouldIncludeFile(ref FileSystemEntry entry, bool includeHiddenFiles)
    {
        var attributes = entry.Attributes;

        // Never scannable: System-attributed files, regardless of the hidden
        // toggle ($Recycle.Bin contents, OS internals).
        if ((attributes & FileAttributes.System) != 0)
        {
            return false;
        }

        if (!includeHiddenFiles && (attributes & FileAttributes.Hidden) != 0)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Cloud-filter placeholders are reparse points but NOT links;
            // symlinked files ARE links and must stay excluded - hashing one
            // would follow the link and "duplicate" a file against its own
            // target, making the target deletable as a redundant copy.
            try
            {
                if (new FileInfo(entry.ToFullPath()).LinkTarget is not null)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldRecurseInto(ref FileSystemEntry entry, bool includeHiddenFiles, string[] blockedRootsUnderScan)
    {
        var attributes = entry.Attributes;

        // Junctions/symlinked directories: never recurse (cycle protection
        // and "don't scan outside what the user picked"). System dirs:
        // never. Hidden dirs: only when the toggle allows.
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0)
        {
            return false;
        }

        if (!includeHiddenFiles && (attributes & FileAttributes.Hidden) != 0)
        {
            return false;
        }

        // Prune junk directories at the tree level (cheaper than the
        // per-file ancestor walk it replaces).
        if (entry.FileName.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (blockedRootsUnderScan.Length > 0)
        {
            var fullPath = entry.ToFullPath();
            foreach (var blockedRoot in blockedRootsUnderScan)
            {
                if (fullPath.Equals(blockedRoot, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(EnsureTrailingSeparator(blockedRoot), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
