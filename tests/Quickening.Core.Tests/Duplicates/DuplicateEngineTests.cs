using Quickening.Core.Duplicates;
using Quickening.Core.Hashing;
using Quickening.Core.Models;
using Quickening.Core.Scanning;
using Quickening.Tests.Shared;
using Xunit;

namespace Quickening.Core.Tests.Duplicates;

public class DuplicateEngineTests : IDisposable
{
    private readonly string _tempDir;

    public DuplicateEngineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "QuickeningDupTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void FindDuplicates_GroupsFilesWithIdenticalContent()
    {
        var content = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "unique.txt"), "different"u8.ToArray());

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Files.Count);
    }

    [Fact]
    public void FindDuplicates_ExcludesZeroByteFiles_EvenWhenSeveralExist()
    {
        // A batch of unrelated failed/interrupted downloads (different
        // intended types) can each leave a genuinely empty file behind -
        // these are trivially byte-identical to each other (nothing to
        // differ), but grouping them as "duplicates" has no reclaimable
        // space behind it and misrepresents unrelated files as copies of
        // each other, so they must never be grouped regardless of count.
        File.WriteAllBytes(Path.Combine(_tempDir, "empty1.htm"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_tempDir, "empty2.zip"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_tempDir, "empty3.jpg"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_tempDir, "real.txt"), "not empty"u8.ToArray());

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Empty(groups);
    }

    [Fact]
    public void FindDuplicates_ReportsHashingProgress_ReachingFullTotalWithGroupFound()
    {
        var content = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "unique.txt"), "different, and a different length too"u8.ToArray());

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var reports = new List<HashingProgress>();
        var progress = new SynchronousProgress<HashingProgress>(reports.Add);

        var groups = engine.FindDuplicates(files, progress).ToList();

        Assert.Single(groups);
        Assert.NotEmpty(reports);
        // Reported up front, before any hashing - lets a progress UI switch
        // to "total known" the instant the comparing phase starts.
        Assert.Equal(0, reports[0].FilesHashed);
        Assert.Equal(3, reports[0].TotalFiles);
        Assert.All(reports, r => Assert.Equal(3, r.TotalFiles));
        Assert.Equal(3, reports[^1].FilesHashed);
        Assert.Equal(1, reports[^1].DuplicateGroupsFoundSoFar);
        Assert.Equal(content.Length, reports[^1].ReclaimableBytesSoFar);
    }

    [Fact]
    public void FindDuplicates_ReturnsNoGroups_WhenAllFilesAreUnique()
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), "content a"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), "content b"u8.ToArray());

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Empty(groups);
    }

    [Fact]
    public void FindDuplicates_DoesNotGroupFilesOfDifferentSize()
    {
        // Same prefix, different length - would collide on a naive check
        // but must never collide on size, so this also proves the size
        // pre-filter doesn't accidentally over-match.
        File.WriteAllBytes(Path.Combine(_tempDir, "short.txt"), "abc"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_tempDir, "long.txt"), "abcdef"u8.ToArray());

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Empty(groups);
    }

    [Fact]
    public void FindDuplicates_Throws_WhenCancellationAlreadyRequested()
    {
        var content = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());
        var cancelledToken = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(
            () => engine.FindDuplicates(files, cancellationToken: cancelledToken).ToList());
    }

    [Fact]
    public void FindDuplicates_SkipsFile_WhenDeletedBetweenEnumerationAndHashing()
    {
        var content = "duplicate content"u8.ToArray();
        var pathA = Path.Combine(_tempDir, "a.txt");
        var pathB = Path.Combine(_tempDir, "b.txt");
        File.WriteAllBytes(pathA, content);
        File.WriteAllBytes(pathB, content);

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        File.Delete(pathA); // simulate TOCTOU: vanished after enumeration, before hashing

        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        // The vanished file can't be hashed and is excluded; with only one
        // survivor left in the size group, no duplicate group forms - but
        // critically, FindDuplicates must not throw.
        Assert.Empty(groups);
    }

    [Fact]
    public void FindDuplicates_DoesNotGroup_WhenPartialHashCollidesButFullHashDiffers()
    {
        // Identical head+tail samples (same partial hash) but a differing
        // middle byte (different full hash) - proves stage 3 is load-bearing,
        // not redundant with stage 2.
        var size = 200 * 1024;
        var contentA = new byte[size];
        var contentB = new byte[size];
        for (var i = 0; i < size; i++)
        {
            contentA[i] = (byte)(i % 256);
            contentB[i] = (byte)(i % 256);
        }
        contentB[size / 2] = (byte)(contentB[size / 2] + 1); // flip a byte well outside the 64KB head/tail samples

        File.WriteAllBytes(Path.Combine(_tempDir, "a.bin"), contentA);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.bin"), contentB);

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Empty(groups);
    }

    [Fact]
    public void FindDuplicates_GroupsThreeIdenticalFiles_IntoOneGroup()
    {
        var content = "triplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "c.txt"), content);

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Files.Count);
    }

    [Fact]
    public void FindDuplicates_FindsMultipleIndependentGroups_InASingleScan()
    {
        var contentX = "content x"u8.ToArray();
        var contentY = "content y"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "x1.txt"), contentX);
        File.WriteAllBytes(Path.Combine(_tempDir, "x2.txt"), contentX);
        File.WriteAllBytes(Path.Combine(_tempDir, "y1.txt"), contentY);
        File.WriteAllBytes(Path.Combine(_tempDir, "y2.txt"), contentY);

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(2, g.Files.Count));

        var groupedNames = groups
            .Select(g => g.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToArray())
            .ToList();
        Assert.Contains(groupedNames, names => names.SequenceEqual(new[] { "x1.txt", "x2.txt" }));
        Assert.Contains(groupedNames, names => names.SequenceEqual(new[] { "y1.txt", "y2.txt" }));
    }

    [Fact]
    public void FindDuplicates_DoesNotComputeFullHash_ForFilesWithDifferingPartialHash()
    {
        // Same size, different content (and therefore different partial hash) -
        // stage 3 must never even run on these, proving the funnel genuinely
        // short-circuits rather than hashing every same-size file in full.
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), "aaaaaaaaaa"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), "bbbbbbbbbb"u8.ToArray());

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var spy = new FullHashCountingHashProvider(new HashProvider());
        var engine = new DuplicateEngine(spy);

        var groups = engine.FindDuplicates(files).ToList();

        Assert.Empty(groups);
        Assert.Equal(0, spy.FullHashCallCount);
    }

    [Fact]
    public void FindDuplicates_ThrowsImmediately_WhenFilesIsNull()
    {
        var engine = new DuplicateEngine(new HashProvider());

        Assert.Throws<ArgumentNullException>(() => engine.FindDuplicates(null!));
    }

    [Fact]
    public void FindDuplicates_StillGroupsGenuineDuplicates_WhenParanoidModeEnabled()
    {
        var content = "duplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "unique.txt"), "different"u8.ToArray());

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files, paranoidMode: true).ToList();

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Files.Count);
    }

    [Fact]
    public void FindDuplicates_StillGroupsThreeIdenticalFiles_WhenParanoidModeEnabled()
    {
        var content = "triplicate content"u8.ToArray();
        File.WriteAllBytes(Path.Combine(_tempDir, "a.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "b.txt"), content);
        File.WriteAllBytes(Path.Combine(_tempDir, "c.txt"), content);

        var files = new FileEnumerator().Enumerate(_tempDir).ToList();
        var engine = new DuplicateEngine(new HashProvider());

        var groups = engine.FindDuplicates(files, paranoidMode: true).ToList();

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Files.Count);
    }

    private sealed class FullHashCountingHashProvider : IHashProvider
    {
        private readonly IHashProvider _inner;

        public FullHashCountingHashProvider(IHashProvider inner)
        {
            _inner = inner;
        }

        public int FullHashCallCount { get; private set; }

        public byte[] ComputePartialHash(string path, CancellationToken cancellationToken = default) =>
            _inner.ComputePartialHash(path, cancellationToken);

        public byte[] ComputeFullHash(string path, CancellationToken cancellationToken = default)
        {
            FullHashCallCount++;
            return _inner.ComputeFullHash(path, cancellationToken);
        }
    }
}
