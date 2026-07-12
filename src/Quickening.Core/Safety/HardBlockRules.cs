using System.Runtime.InteropServices;

namespace Quickening.Core.Safety;

public static class HardBlockRules
{
    private static readonly string[] BlockedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // ProgramData
    };

    /// <summary>Non-empty blocked roots, long-form absolute paths.</summary>
    public static IReadOnlyList<string> BlockedRootPaths { get; } =
        BlockedRoots.Where(r => !string.IsNullOrEmpty(r)).ToArray();

    /// <summary>
    /// Canonicalizes a path for safety comparisons: resolves relative
    /// segments, strips the \\?\ extended-length prefix, and expands 8.3
    /// short names (C:\PROGRA~1 → C:\Program Files). Prefix compares against
    /// BlockedRoots are meaningless on un-normalized input - both forms
    /// reach real files while dodging a naive StartsWith.
    /// </summary>
    public static string NormalizePath(string path)
    {
        // Strip the device prefix BEFORE GetFullPath: for \\?\-prefixed
        // paths GetFullPath deliberately skips normalization (that's the
        // prefix's documented meaning), so '..' and '.' segments would
        // survive into the "canonical" output and every prefix-based safety
        // comparison downstream would run against the wrong path.
        var working = path;
        if (working.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            working = @"\\" + working[8..];
        }
        else if ((working.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                  working.StartsWith(@"\\.\", StringComparison.Ordinal)) &&
                 working.Length > 5 && working[5] == ':')
        {
            // Only for drive-letter forms (\\?\C:\...) - a \\?\Volume{guid}\
            // path stripped of its prefix would parse as a relative path.
            working = working[4..];
        }

        var fullPath = Path.GetFullPath(working);

        // '~' is the only cheap tell for a possible 8.3 short name; skip the
        // syscall for the overwhelmingly common paths without one.
        if (fullPath.Contains('~'))
        {
            fullPath = TryExpandShortPath(fullPath);
        }

        return fullPath;
    }

    /// <param name="allowProtectedPaths">
    /// Settings' "Allow scanning protected system paths" toggle - off by
    /// default. Only bypasses the BlockedRoots check; the hidden+system
    /// attribute check is never bypassable regardless of this flag, since
    /// that's a per-file marker rather than a location-based rule.
    /// </param>
    public static bool IsHardBlocked(string path, bool allowProtectedPaths = false)
    {
        var fullPath = NormalizePath(path);

        // Defense-in-depth: NormalizePath resolves \\?\C:\ (drive-letter)
        // forms, but a device path we can't canonicalise - \\?\Volume{GUID}\,
        // \\?\GLOBALROOT\..., \\.\... - keeps its prefix and so slips past the
        // BlockedRoots prefix compare even when it names a real protected
        // file. We can't prove such a path is safe, so fail closed. (The
        // shell delete resolver also rejects these, but don't rely on one
        // layer.) Respects the protected-paths opt-out like the root check.
        if (!allowProtectedPaths &&
            (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)))
        {
            return true;
        }

        if (!allowProtectedPaths && IsUnderBlockedRoot(fullPath))
        {
            return true;
        }

        if (File.Exists(fullPath))
        {
            try
            {
                var attributes = File.GetAttributes(fullPath);
                var isHiddenSystem = attributes.HasFlag(FileAttributes.Hidden) &&
                                      attributes.HasFlag(FileAttributes.System);
                if (isHiddenSystem)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fail CLOSED: a rule documented as "never bypassable" must
                // not turn into "not blocked" just because reading the
                // attributes failed - access-denied is most likely on exactly
                // the protected files this check exists to guard.
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Prefix check against BlockedRoots only. Expects a path already
    /// canonicalized by <see cref="NormalizePath"/>.
    /// </summary>
    public static bool IsUnderBlockedRoot(string normalizedPath)
    {
        foreach (var root in BlockedRootPaths)
        {
            if (normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TryExpandShortPath(string path)
    {
        // GetLongPathNameW fails (returns 0) for nonexistent paths; keep the
        // original in that case - a path that doesn't resolve can't be
        // enumerated or deleted anyway.
        var buffer = new char[520];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }

        return length > 0 && length <= buffer.Length ? new string(buffer, 0, (int)length) : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);
}
