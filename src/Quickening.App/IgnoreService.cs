using System.IO;

namespace Quickening.App;

/// <summary>
/// The user's Ignore list (persisted in AppSettings): specific files and whole
/// folders that should never appear in review or future scans. A file counts as
/// ignored if its exact path was ignored, or it sits at/under an ignored folder.
/// Backed by in-memory sets for O(1) file lookups during results filtering, and
/// written straight back to settings.json on every change.
/// </summary>
public static class IgnoreService
{
    private static HashSet<string>? _files;
    private static List<string>? _folders;

    private static HashSet<string> Files =>
        _files ??= new HashSet<string>(App.Settings.IgnoredFilePaths, StringComparer.OrdinalIgnoreCase);

    // Trailing separators are trimmed on load, not just on IgnoreFolder: a
    // hand-edited settings.json entry like "C:\foo\" would otherwise match
    // NOTHING (IsUnder's boundary check would look at the char after the
    // backslash and never see a separator).
    private static List<string> Folders =>
        _folders ??= App.Settings.IgnoredFolderPaths
            .Select(f => f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(f => f.Length > 0)
            .ToList();

    /// <summary>True when anything is ignored - lets callers skip the check entirely.</summary>
    public static bool HasAny => Files.Count > 0 || Folders.Count > 0;

    public static bool IsIgnored(string path)
    {
        if (Files.Contains(path))
        {
            return true;
        }

        foreach (var folder in Folders)
        {
            if (IsUnder(path, folder))
            {
                return true;
            }
        }

        return false;
    }

    public static void IgnoreFiles(IEnumerable<string> paths)
    {
        var changed = false;
        foreach (var path in paths)
        {
            changed |= Files.Add(path);
        }

        if (changed)
        {
            Persist();
        }
    }

    public static void IgnoreFolder(string folder)
    {
        var normalized = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Folders.Any(f => f.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            Folders.Add(normalized);
            Persist();
        }
    }

    public static void UnignoreFile(string path)
    {
        if (Files.Remove(path))
        {
            Persist();
        }
    }

    public static void UnignoreFolder(string folder)
    {
        if (Folders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Persist();
        }
    }

    public static IReadOnlyList<string> IgnoredFiles => Files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<string> IgnoredFolders => Folders.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsUnder(string path, string folder)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == folder.Length)
        {
            return true;
        }

        var boundary = path[folder.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private static void Persist()
    {
        App.Settings.IgnoredFilePaths = Files.ToList();
        App.Settings.IgnoredFolderPaths = Folders.ToList();
        App.SaveSettings();
    }
}
