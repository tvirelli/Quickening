using Quickening.Core.Models;
using Quickening.Core.Scanning;
using Xunit;

namespace Quickening.Core.Tests.Scanning;

public class FileEnumeratorTests : IDisposable
{
    private readonly string _tempDir;

    public FileEnumeratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "QuickeningTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Enumerate_FindsAllRegularFiles_Recursively()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.jpg"), "aaa");
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "b.mp3"), "bbb");

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir).ToList();

        Assert.Equal(2, results.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Enumerate_Throws_ForNullOrEmptyRootPath(string? rootPath)
    {
        var enumerator = new FileEnumerator();

        // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for null
        // (a subtype of ArgumentException) and ArgumentException for empty - xUnit's
        // Assert.Throws<T> requires an exact type match, so ThrowsAny is needed here
        // to cover both without asserting the wrong exact type for either case.
        Assert.ThrowsAny<ArgumentException>(() => enumerator.Enumerate(rootPath!));
    }

    [Fact]
    public void Enumerate_Throws_ForNonexistentRootPath()
    {
        var nonexistentPath = Path.Combine(_tempDir, "does-not-exist-" + Guid.NewGuid());
        var enumerator = new FileEnumerator();

        Assert.Throws<DirectoryNotFoundException>(() => enumerator.Enumerate(nonexistentPath));
    }

    [Fact]
    public void Enumerate_SkipsFiles_UnderHardBlockedRootPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var testDir = Path.Combine(programData, "QuickeningTests_" + Guid.NewGuid());
        Directory.CreateDirectory(testDir);
        try
        {
            File.WriteAllText(Path.Combine(testDir, "blocked.txt"), "should never surface");

            var enumerator = new FileEnumerator();
            var results = enumerator.Enumerate(testDir).ToList();

            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_AssignsCorrectSize_ForKnownFileContent()
    {
        var content = "0123456789"; // 10 bytes, ASCII
        File.WriteAllText(Path.Combine(_tempDir, "sized.txt"), content);

        var enumerator = new FileEnumerator();
        var result = enumerator.Enumerate(_tempDir).Single();

        Assert.Equal(content.Length, result.SizeBytes);
    }

    [Fact]
    public void Enumerate_AssignsOtherCategory_ForUnknownExtension()
    {
        File.WriteAllText(Path.Combine(_tempDir, "mystery.xyz"), "data");

        var enumerator = new FileEnumerator();
        var result = enumerator.Enumerate(_tempDir).Single();

        Assert.Equal(MimeCategory.Other, result.Category);
    }

    [Fact]
    public void Enumerate_Throws_WhenCancellationAlreadyRequested()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.jpg"), "aaa");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var enumerator = new FileEnumerator();

        Assert.Throws<OperationCanceledException>(() => enumerator.Enumerate(_tempDir, cancellationToken: cts.Token).ToList());
    }

    [Fact]
    public void Enumerate_SkipsHiddenSystemFiles()
    {
        var hiddenFile = Path.Combine(_tempDir, "hidden.dat");
        File.WriteAllText(hiddenFile, "hidden");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden | FileAttributes.System);

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Enumerate_FlagsIsCloudPlaceholder_ForOfflineAttributedFile()
    {
        var placeholderFile = Path.Combine(_tempDir, "placeholder.txt");
        File.WriteAllText(placeholderFile, "not really here");
        File.SetAttributes(placeholderFile, File.GetAttributes(placeholderFile) | FileAttributes.Offline);

        var enumerator = new FileEnumerator();
        var result = enumerator.Enumerate(_tempDir).Single();

        Assert.True(result.IsCloudPlaceholder);
    }

    [Fact]
    public void Enumerate_LeavesIsCloudPlaceholderFalse_ForAnOrdinaryFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, "ordinary.txt"), "just a file");

        var enumerator = new FileEnumerator();
        var result = enumerator.Enumerate(_tempDir).Single();

        Assert.False(result.IsCloudPlaceholder);
    }

    [Fact]
    public void Enumerate_ExcludesCloudPlaceholders_WhenAsked()
    {
        var placeholderFile = Path.Combine(_tempDir, "placeholder.txt");
        File.WriteAllText(placeholderFile, "not really here");
        File.SetAttributes(placeholderFile, File.GetAttributes(placeholderFile) | FileAttributes.Offline);
        File.WriteAllText(Path.Combine(_tempDir, "ordinary.txt"), "just a file");

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir, excludeCloudPlaceholders: true).ToList();

        Assert.Single(results);
        Assert.EndsWith("ordinary.txt", results[0].Path);
    }

    [Fact]
    public void PreviewCloudPlaceholders_CountsAndSumsOnlyPlaceholders()
    {
        var placeholderFile = Path.Combine(_tempDir, "placeholder.txt");
        File.WriteAllText(placeholderFile, "12345"); // 5 bytes
        File.SetAttributes(placeholderFile, File.GetAttributes(placeholderFile) | FileAttributes.Offline);
        File.WriteAllText(Path.Combine(_tempDir, "ordinary.txt"), "just a regular file, ignored");

        var enumerator = new FileEnumerator();
        var (count, totalBytes) = enumerator.PreviewCloudPlaceholders(_tempDir);

        Assert.Equal(1, count);
        Assert.Equal(5, totalBytes);
    }

    [Fact]
    public void PreviewCloudPlaceholders_ReturnsZero_WhenNoneArePlaceholders()
    {
        File.WriteAllText(Path.Combine(_tempDir, "ordinary.txt"), "just a file");

        var enumerator = new FileEnumerator();
        var (count, totalBytes) = enumerator.PreviewCloudPlaceholders(_tempDir);

        Assert.Equal(0, count);
        Assert.Equal(0, totalBytes);
    }

    [Fact]
    public void Enumerate_AssignsImageCategory_ForJpgExtension()
    {
        File.WriteAllText(Path.Combine(_tempDir, "photo.jpg"), "data");

        var enumerator = new FileEnumerator();
        var result = enumerator.Enumerate(_tempDir).Single();

        Assert.Equal(MimeCategory.Image, result.Category);
    }

    [Fact]
    public void Enumerate_DoesNotRecurseInto_HiddenSystemDirectories()
    {
        // Simulates $Recycle.Bin / System Volume Information: a hidden+system
        // directory containing an ordinary (non-hidden, non-system) file.
        // The directory's own attributes must stop enumeration from
        // descending into it at all - HardBlockRules only checks individual
        // file attributes, so this has to be enforced by the enumerator's
        // EnumerationOptions instead.
        var hiddenDir = Path.Combine(_tempDir, "HiddenSystemDir");
        Directory.CreateDirectory(hiddenDir);
        File.SetAttributes(hiddenDir, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory);

        var innerFile = Path.Combine(hiddenDir, "ordinary.txt");
        File.WriteAllText(innerFile, "should never be surfaced");

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir).ToList();

        Assert.Empty(results);
    }

    [Theory]
    [InlineData(".DS_Store")]
    [InlineData("Thumbs.db")]
    [InlineData("ehthumbs.db")]
    [InlineData("desktop.ini")]
    [InlineData("._shims.yml")]
    [InlineData("._resume.pdf")]
    public void Enumerate_SkipsKnownJunkFileNames(string junkFileName)
    {
        File.WriteAllText(Path.Combine(_tempDir, junkFileName), "junk");
        File.WriteAllText(Path.Combine(_tempDir, "real.txt"), "real content");

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir).ToList();

        Assert.Single(results);
        Assert.EndsWith("real.txt", results[0].Path);
    }

    [Fact]
    public void Enumerate_SkipsEverythingUnder_MACOSXDirectory()
    {
        // A macOS-authored zip extracted on Windows commonly includes a
        // __MACOSX folder full of AppleDouble sidecar files - these often
        // aren't individually caught by the ._ filename check alone if
        // nested under further subfolders, so the whole directory is
        // excluded by name regardless of depth or the files' own names.
        var macosxDir = Path.Combine(_tempDir, "__MACOSX", "nested");
        Directory.CreateDirectory(macosxDir);
        File.WriteAllText(Path.Combine(macosxDir, "not-dot-prefixed.txt"), "junk");
        File.WriteAllText(Path.Combine(_tempDir, "real.txt"), "real content");

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir).ToList();

        Assert.Single(results);
        Assert.EndsWith("real.txt", results[0].Path);
    }

    [Fact]
    public void Enumerate_SurvivesFileDeletedMidWalk_WithoutThrowing()
    {
        // Metadata now comes straight from the directory entry (no per-file
        // FileInfo round-trip), so a file deleted mid-enumeration may still
        // be yielded with its snapshot metadata - the TOCTOU contract moved
        // downstream: DuplicateEngine/hashing excludes a vanished file when
        // it actually tries to open it. What the enumerator itself must
        // guarantee is that a concurrent delete never aborts the walk.
        var fileA = Path.Combine(_tempDir, "a_file.txt");
        var fileB = Path.Combine(_tempDir, "b_file.txt");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");

        var enumerator = new FileEnumerator();
        var results = new List<FileRecord>();
        string? deletedPath = null;

        using (var e = enumerator.Enumerate(_tempDir).GetEnumerator())
        {
            while (e.MoveNext())
            {
                results.Add(e.Current);

                if (deletedPath is null)
                {
                    // First file survived intact - now delete whichever file
                    // has NOT been reached yet.
                    deletedPath = e.Current.Path == fileA ? fileB : fileA;
                    File.Delete(deletedPath);
                }
            }
        }

        // The walk completed, the surviving file is present, and any record
        // for the deleted file still carries coherent snapshot metadata.
        Assert.Contains(results, r => r.Path != deletedPath);
        Assert.All(results, r => Assert.True(r.SizeBytes >= 0));
    }

    [Fact]
    public void Enumerate_IncludesCloudPlaceholderStyleFiles_ButSkipsSymlinkedFiles()
    {
        // Cloud placeholders are reparse-point FILES; the old enumerator's
        // blanket AttributesToSkip=ReparsePoint silently excluded every
        // OneDrive/Dropbox file from every scan. True file symlinks must
        // stay excluded (hashing one would "duplicate" it against its own
        // target). CreateSymbolicLink needs Developer Mode or admin on
        // Windows - skip that half gracefully when unavailable.
        var realFile = Path.Combine(_tempDir, "real_target.txt");
        File.WriteAllText(realFile, "content");

        var linkPath = Path.Combine(_tempDir, "link_to_real.txt");
        var symlinkSupported = true;
        try
        {
            File.CreateSymbolicLink(linkPath, realFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            symlinkSupported = false;
        }

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir).ToList();

        Assert.Contains(results, r => r.Path == realFile);
        if (symlinkSupported)
        {
            Assert.DoesNotContain(results, r => r.Path == linkPath);
        }
    }

    [Fact]
    public void Enumerate_DoesNotRecurse_IntoDirectoryJunctions()
    {
        // Junction cycles were the reason reparse points were skipped
        // wholesale; verify the directory half of that protection survives
        // the switch to per-entry filtering. Directory junctions don't
        // require special privileges.
        var targetDir = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "inside.txt"), "content");

        var junctionPath = Path.Combine(_tempDir, "junction");
        var junctionSupported = true;
        try
        {
            Directory.CreateSymbolicLink(junctionPath, targetDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            junctionSupported = false;
        }

        var enumerator = new FileEnumerator();
        var results = enumerator.Enumerate(_tempDir).ToList();

        // The real file is found once via its real parent, and never again
        // through the junction.
        Assert.Single(results, r => r.Path.EndsWith("inside.txt", StringComparison.Ordinal));
        if (junctionSupported)
        {
            Assert.DoesNotContain(results, r => r.Path.Contains("junction", StringComparison.OrdinalIgnoreCase));
        }
    }
}
