using System.Formats.Tar;
using System.IO.Compression;

namespace Quickening.Core.Archives;

/// <summary>One entry inside an archive - a file or a directory marker.</summary>
public sealed record ArchiveEntry(string Name, long Length, bool IsDirectory);

/// <summary>
/// The result of listing an archive. <see cref="Error"/> is non-null when the
/// archive couldn't be read (corrupt, unsupported, locked); <see cref="Truncated"/>
/// is true when the archive held more entries than the cap and the tail was dropped.
/// </summary>
public sealed record ArchiveListing(IReadOnlyList<ArchiveEntry> Entries, bool Truncated, string? Error)
{
    public static ArchiveListing Failed(string error) => new(Array.Empty<ArchiveEntry>(), false, error);
}

/// <summary>
/// Lists the contents of an archive so the compare viewer can show what's inside
/// (and let the user eyeball two archives side by side) without extracting
/// anything. Only formats the .NET base class library can read without a native
/// dependency are supported: .zip (System.IO.Compression) and .tar
/// (System.Formats.Tar). .7z/.rar and compressed tarballs (.gz/.tgz/.bz2) return
/// an Unsupported error so the viewer can fall back to "open externally".
/// </summary>
public static class ArchiveInspector
{
    // Guards against a zip with millions of entries turning the preview into a
    // multi-second UI build. The tail is dropped and Truncated is set.
    public const int DefaultMaxEntries = 2000;

    public static ArchiveListing List(string path, int maxEntries = DefaultMaxEntries)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".zip" => ListZip(path, maxEntries),
                ".tar" => ListTar(path, maxEntries),
                _ => ArchiveListing.Failed($"Listing {ext} archives isn't supported here."),
            };
        }
        catch (Exception ex)
        {
            return ArchiveListing.Failed($"Couldn't read this archive: {ex.Message}");
        }
    }

    private static ArchiveListing ListZip(string path, int maxEntries)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = new List<ArchiveEntry>();
        var truncated = false;
        foreach (var e in zip.Entries)
        {
            if (entries.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            // A zip directory marker has an empty Name and a FullName ending in '/'.
            var isDir = e.FullName.EndsWith('/') && string.IsNullOrEmpty(e.Name);
            entries.Add(new ArchiveEntry(e.FullName, isDir ? 0 : e.Length, isDir));
        }

        return new ArchiveListing(entries, truncated, null);
    }

    private static ArchiveListing ListTar(string path, int maxEntries)
    {
        using var stream = File.OpenRead(path);
        using var reader = new TarReader(stream);
        var entries = new List<ArchiveEntry>();
        var truncated = false;
        while (reader.GetNextEntry() is { } e)
        {
            if (entries.Count >= maxEntries)
            {
                truncated = true;
                break;
            }

            var isDir = e.EntryType is TarEntryType.Directory;
            entries.Add(new ArchiveEntry(e.Name, isDir ? 0 : e.Length, isDir));
        }

        return new ArchiveListing(entries, truncated, null);
    }
}
