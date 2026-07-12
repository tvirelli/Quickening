using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Quickening.Core.Archives;
using Xunit;

namespace Quickening.Core.Tests.Archives;

public sealed class ArchiveInspectorTests : IDisposable
{
    private readonly string _dir;

    public ArchiveInspectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "qk-archive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteZip(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void Zip_ListsEntriesWithSizes()
    {
        var path = WriteZip("a.zip", ("readme.txt", "hello"), ("data/x.bin", "1234567890"));

        var listing = ArchiveInspector.List(path);

        Assert.Null(listing.Error);
        Assert.False(listing.Truncated);
        Assert.Equal(2, listing.Entries.Count);
        var readme = listing.Entries.Single(e => e.Name == "readme.txt");
        Assert.Equal(5, readme.Length);
        Assert.False(readme.IsDirectory);
    }

    [Fact]
    public void Zip_HonoursMaxEntriesAndSetsTruncated()
    {
        var path = WriteZip("big.zip", ("a", "x"), ("b", "x"), ("c", "x"));

        var listing = ArchiveInspector.List(path, maxEntries: 2);

        Assert.True(listing.Truncated);
        Assert.Equal(2, listing.Entries.Count);
    }

    [Fact]
    public void Tar_ListsEntries()
    {
        var path = Path.Combine(_dir, "a.tar");
        using (var stream = File.Create(path))
        using (var tar = new TarWriter(stream))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "hello.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("hi there")),
            });
        }

        var listing = ArchiveInspector.List(path);

        Assert.Null(listing.Error);
        var entry = Assert.Single(listing.Entries);
        Assert.Equal("hello.txt", entry.Name);
        Assert.Equal(8, entry.Length);
    }

    [Fact]
    public void UnsupportedFormat_ReturnsError()
    {
        var path = Path.Combine(_dir, "a.7z");
        File.WriteAllText(path, "not really a 7z");

        var listing = ArchiveInspector.List(path);

        Assert.NotNull(listing.Error);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void CorruptZip_ReturnsErrorNotThrow()
    {
        var path = Path.Combine(_dir, "corrupt.zip");
        File.WriteAllText(path, "PK totally not a zip");

        var listing = ArchiveInspector.List(path);

        Assert.NotNull(listing.Error);
        Assert.Empty(listing.Entries);
    }
}
