using System.Security.Cryptography;
using System.Text;
using Quickening.Core.Models;

namespace Quickening.Core.Duplicates;

/// <summary>
/// Finds whole folders that are exact copies of each other (F8), built entirely
/// from the file-level duplicate results the scan already produced - no extra
/// hashing. A true folder copy means every file in it is also an individual
/// duplicate, so a folder that contains any UNIQUE file simply can't be a copy
/// and is excluded for free. Two folders are copies when their files match 1:1
/// by relative path AND content (each duplicate group is one "content class"),
/// which encodes both structure and bytes. Only the TOP-MOST duplicate folders
/// are reported - if A and B match, their matching subfolders A/x and B/x are
/// not listed again.
/// </summary>
public sealed class DuplicateFolderEngine
{
    /// <summary>One folder in a duplicate set: its path, and the (identical
    /// across the set) total size and file count.</summary>
    public sealed record FolderCopy(string Path, long SizeBytes, int FileCount);

    /// <summary>A set of two or more folders that are exact copies of each
    /// other. SizeBytes/FileCount describe one folder (they're all the same).</summary>
    public sealed record DuplicateFolderGroup(IReadOnlyList<FolderCopy> Folders, long SizeBytes, int FileCount);

    /// <param name="rawStructureProvider">Returns a raw ON-DISK structure
    /// signature for a folder, or null when the folder can't be fully verified
    /// (see TryRawStructureSignature). Injectable for tests; null uses the real
    /// filesystem.</param>
    public IReadOnlyList<DuplicateFolderGroup> FindDuplicateFolders(
        IReadOnlyList<FileRecord> allFiles,
        IReadOnlyList<DuplicateGroup> duplicateGroups,
        CancellationToken cancellationToken = default,
        Func<string, string?>? rawStructureProvider = null)
    {
        rawStructureProvider ??= TryRawStructureSignature;
        // Content class per duplicated file path: two files with the same class
        // are byte-identical (they share a DuplicateGroup / FullHash).
        var classByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in duplicateGroups)
        {
            var cls = Convert.ToHexString(group.FullHash);
            foreach (var file in group.Files)
            {
                classByPath[file.Path] = cls;
            }
        }

        // folder -> its direct files; folder -> its direct child folders. Only
        // folders that (transitively) contain a file ever appear here, so truly
        // empty folders are naturally ignored (they're F5's concern).
        var directFiles = new Dictionary<string, List<FileRecord>>(StringComparer.OrdinalIgnoreCase);
        var childFolders = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            var parent = Path.GetDirectoryName(file.Path);
            if (string.IsNullOrEmpty(parent))
            {
                continue;
            }

            if (!directFiles.TryGetValue(parent, out var list))
            {
                directFiles[parent] = list = new List<FileRecord>();
            }

            list.Add(file);

            // Register the parent and every ancestor, wiring each into its own parent.
            var current = parent;
            while (!string.IsNullOrEmpty(current))
            {
                allFolders.Add(current);
                var up = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(up))
                {
                    break;
                }

                if (!childFolders.TryGetValue(up, out var kids))
                {
                    childFolders[up] = kids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                kids.Add(current);
                current = up;
            }
        }

        // Signature (null = incomplete: contains a unique file somewhere below),
        // total size, and file count per folder. Computed deepest-first so every
        // child is done before its parent (a descendant's path is always longer
        // than its ancestor's, so descending length order suffices).
        var signature = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var folderSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var folderCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in allFolders.OrderByDescending(f => f.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var complete = true;
            var totalSize = 0L;
            var totalCount = 0;
            var components = new List<string>();

            if (directFiles.TryGetValue(folder, out var files))
            {
                foreach (var file in files)
                {
                    if (!classByPath.TryGetValue(file.Path, out var cls))
                    {
                        complete = false; // a unique file -> this folder can't be a copy
                        break;
                    }

                    components.Add("F:" + Path.GetFileName(file.Path).ToLowerInvariant() + ":" + cls);
                    totalSize += file.SizeBytes;
                    totalCount++;
                }
            }

            if (complete && childFolders.TryGetValue(folder, out var kids))
            {
                foreach (var kid in kids)
                {
                    var kidSig = signature[kid]; // kid processed already (it's deeper)
                    if (kidSig is null)
                    {
                        complete = false; // incomplete subfolder taints the parent
                        break;
                    }

                    components.Add("D:" + Path.GetFileName(kid).ToLowerInvariant() + ":" + kidSig);
                    totalSize += folderSize[kid];
                    totalCount += folderCount[kid];
                }
            }

            if (!complete || totalCount == 0)
            {
                signature[folder] = null;
                folderSize[folder] = 0;
                folderCount[folder] = 0;
                continue;
            }

            components.Sort(StringComparer.Ordinal);
            signature[folder] = HashComponents(components);
            folderSize[folder] = totalSize;
            folderCount[folder] = totalCount;
        }

        // Group complete folders by signature; a set of 2+ is a duplicate-folder set.
        var bySignature = new Dictionary<string, List<string>>();
        foreach (var folder in allFolders)
        {
            if (signature.TryGetValue(folder, out var sig) && sig is not null)
            {
                if (!bySignature.TryGetValue(sig, out var members))
                {
                    bySignature[sig] = members = new List<string>();
                }

                members.Add(folder);
            }
        }

        var duplicatedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var members in bySignature.Values)
        {
            if (members.Count >= 2)
            {
                foreach (var folder in members)
                {
                    duplicatedFolders.Add(folder);
                }
            }
        }

        // Top-most only: drop any folder that sits under another folder which is
        // itself a duplicate (that ancestor already covers it).
        bool HasDuplicatedAncestor(string folder)
        {
            var up = Path.GetDirectoryName(folder);
            while (!string.IsNullOrEmpty(up))
            {
                if (duplicatedFolders.Contains(up))
                {
                    return true;
                }

                up = Path.GetDirectoryName(up);
            }

            return false;
        }

        var result = new List<DuplicateFolderGroup>();
        foreach (var members in bySignature.Values)
        {
            if (members.Count < 2)
            {
                continue;
            }

            var topmost = members.Where(f => !HasDuplicatedAncestor(f)).ToList();
            if (topmost.Count < 2)
            {
                continue; // the whole set was nested under a larger duplicate
            }

            // RAW-DISK verification: the content signatures above only cover
            // what the scan ENUMERATED - hidden/system files, symlinked files,
            // junction subfolders and skipped cloud placeholders are invisible
            // to them. Without this step, a folder holding a hidden folder of
            // unique documents could be declared an "exact copy" of one that
            // doesn't, and deleting it wholesale would lose that data. Each
            // candidate must also agree on its raw on-disk structure (every
            // entry regardless of attributes, by relative path + file size),
            // and anything unverifiable (reparse point inside, IO error) is
            // dropped. Split candidates by raw signature - subsets that still
            // agree remain groups.
            foreach (var rawBucket in topmost
                .Select(f => (Folder: f, Raw: rawStructureProvider(f)))
                .Where(x => x.Raw is not null)
                .GroupBy(x => x.Raw!, StringComparer.Ordinal))
            {
                var verified = rawBucket.Select(x => x.Folder).ToList();
                if (verified.Count < 2)
                {
                    continue;
                }

                var copies = verified
                    .Select(f => new FolderCopy(f, folderSize[f], folderCount[f]))
                    .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                result.Add(new DuplicateFolderGroup(copies, copies[0].SizeBytes, copies[0].FileCount));
            }
        }

        return result.OrderByDescending(g => g.SizeBytes).ToList();
    }

    /// <summary>
    /// A signature of a folder's COMPLETE on-disk structure: every file and
    /// directory below it - hidden and system included - by lowercased relative
    /// path, with file sizes. Returns null (folder unverifiable, caller must
    /// exclude it) when the folder or anything inside it is a reparse point
    /// (junction/symlink - contents aren't really "in" this folder), or when
    /// enumeration fails. Name+size for hidden entries (their contents were
    /// never hashed) still guarantees the visible-content proof isn't extended
    /// to folders whose invisible contents differ in structure or size.
    /// </summary>
    private static string? TryRawStructureSignature(string folder)
    {
        try
        {
            var root = new DirectoryInfo(folder);
            if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var components = new List<string>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.None, // hidden + system INCLUDED
                IgnoreInaccessible = false,
            };
            foreach (var entry in root.EnumerateFileSystemInfos("*", options))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return null; // bail before the enumerator recurses into it
                }

                var rel = Path.GetRelativePath(folder, entry.FullName).ToLowerInvariant();
                components.Add(entry is FileInfo file ? $"F:{rel}:{file.Length}" : $"D:{rel}");
            }

            components.Sort(StringComparer.Ordinal);
            return HashComponents(components);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string HashComponents(IEnumerable<string> components)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", components)));
        return Convert.ToHexString(bytes);
    }
}
