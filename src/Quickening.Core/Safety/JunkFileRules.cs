namespace Quickening.Core.Safety;

/// <summary>
/// Recognizes OS-generated metadata clutter that's never meaningful scan
/// content - the same well-established skip-list most cross-platform
/// duplicate/sync tools hardcode (macOS AppleDouble sidecar files, Windows
/// thumbnail/folder-view caches). These aren't hidden on Windows (the
/// Hidden file attribute is a separate, unrelated concept FileEnumerator
/// already respects on its own), so they'd otherwise sail through as
/// ordinary-looking files with unusual names - e.g. dozens of AppleDouble
/// files extracted from a Mac-authored zip are often byte-identical to each
/// other (a generic, content-free resource-fork placeholder), which reads
/// as a scanner bug ("58 unrelated file types marked identical") rather
/// than the expected, harmless clutter it actually is.
/// </summary>
public static class JunkFileRules
{
    private static readonly HashSet<string> JunkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "ehthumbs.db",
        "desktop.ini",
    };

    private static readonly HashSet<string> JunkDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__MACOSX",
    };

    // Pre-wrapped as "\name\" so the per-file ancestor check below is a
    // single allocation-free substring probe.
    private static readonly string[] JunkDirectorySegments =
        JunkDirectoryNames.Select(n => $"{Path.DirectorySeparatorChar}{n}{Path.DirectorySeparatorChar}").ToArray();

    public static bool IsJunkFile(string path)
    {
        var fileName = Path.GetFileName(path);

        if (JunkFileNames.Contains(fileName))
        {
            return true;
        }

        // AppleDouble sidecar files - macOS's per-file resource-fork/extended-
        // attribute companion, named "._" + the original file's name.
        if (fileName.StartsWith("._", StringComparison.Ordinal))
        {
            return true;
        }

        // Ancestor check without the per-level GetDirectoryName allocations
        // the old walk paid for every file: a junk directory name can only
        // appear as a whole path segment, so one substring probe suffices.
        foreach (var junkSegment in JunkDirectorySegments)
        {
            if (path.Contains(junkSegment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
