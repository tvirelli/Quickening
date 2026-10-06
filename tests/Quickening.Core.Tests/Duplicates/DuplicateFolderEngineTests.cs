using Quickening.Core.Duplicates;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.Core.Tests.Duplicates;

public class DuplicateFolderEngineTests
{
    // Every test passes a constant rawStructureProvider stub: these tests use
    // fake C:\ paths that don't exist on disk, and they exercise the SIGNATURE
    // logic. The raw on-disk verification layer has its own tests (see
    // DetectionAccuracyTests.DuplicateFolders_*).

    private static FileRecord File(string path, long size = 100) => new()
    {
        Path = path,
        SizeBytes = size,
        LastWriteTimeUtc = DateTime.UnixEpoch,
        Category = MimeCategory.Other,
    };

    private static DuplicateGroup Group(byte hashByte, params FileRecord[] files) => new()
    {
        FullHash = new[] { hashByte },
        Files = files.ToList(),
    };

    [Fact]
    public void FindsTwoSiblingFolderCopies()
    {
        var a1 = File(@"C:\root\copy1\f1.txt");
        var a2 = File(@"C:\root\copy1\f2.txt");
        var b1 = File(@"C:\root\copy2\f1.txt");
        var b2 = File(@"C:\root\copy2\f2.txt");
        // A unique file in the parent keeps C:\root itself from being a candidate,
        // so copy1/copy2 are the top-most duplicates.
        var readme = File(@"C:\root\readme.txt");

        var groups = new[]
        {
            Group(0xA1, a1, b1),
            Group(0xB2, a2, b2),
        };

        var result = new DuplicateFolderEngine().FindDuplicateFolders(
            new[] { a1, a2, b1, b2, readme }, groups, rawStructureProvider: _ => "verified");

        var group = Assert.Single(result);
        Assert.Equal(2, group.Folders.Count);
        Assert.Equal(2, group.FileCount);
        Assert.Contains(group.Folders, f => f.Path == @"C:\root\copy1");
        Assert.Contains(group.Folders, f => f.Path == @"C:\root\copy2");
    }

    [Fact]
    public void FolderWithAUniqueFileIsNotACopy()
    {
        var a1 = File(@"C:\root\copy1\f1.txt");
        var b1 = File(@"C:\root\copy2\f1.txt");
        var extra = File(@"C:\root\copy1\only-here.txt"); // unique -> copy1 incomplete

        var groups = new[] { Group(0xA1, a1, b1) };

        var result = new DuplicateFolderEngine().FindDuplicateFolders(
            new[] { a1, b1, extra }, groups, rawStructureProvider: _ => "verified");

        Assert.Empty(result);
    }

    [Fact]
    public void ReportsTopMostFolders_NotNestedSubfolders()
    {
        // C:\a and C:\b each contain only sub\f.txt (identical). The top-most
        // duplicates are a and b; a\sub and b\sub must NOT be reported separately.
        var a = File(@"C:\a\sub\f.txt");
        var b = File(@"C:\b\sub\f.txt");

        var groups = new[] { Group(0xF0, a, b) };

        var result = new DuplicateFolderEngine().FindDuplicateFolders(new[] { a, b }, groups, rawStructureProvider: _ => "verified");

        var group = Assert.Single(result);
        Assert.Equal(2, group.Folders.Count);
        Assert.Contains(group.Folders, f => f.Path == @"C:\a");
        Assert.Contains(group.Folders, f => f.Path == @"C:\b");
        Assert.DoesNotContain(group.Folders, f => f.Path.EndsWith(@"\sub"));
    }

    [Fact]
    public void SameContentsDifferentFolderNamesStillMatch()
    {
        // A folder's OWN name isn't part of its signature - only its internal
        // structure + contents. So identically-filled folders with different
        // names are still copies (a renamed copy is a copy). The parents carry a
        // unique file each so only photos/images are candidates.
        var a = File(@"C:\a\photos\f.txt");
        var b = File(@"C:\b\images\f.txt");
        var parentA = File(@"C:\a\readme.txt");
        var parentB = File(@"C:\b\readme2.txt");

        var groups = new[] { Group(0xF0, a, b) };

        var result = new DuplicateFolderEngine().FindDuplicateFolders(
            new[] { a, b, parentA, parentB }, groups, rawStructureProvider: _ => "verified");

        var group = Assert.Single(result);
        Assert.Contains(group.Folders, f => f.Path == @"C:\a\photos");
        Assert.Contains(group.Folders, f => f.Path == @"C:\b\images");
    }

    [Fact]
    public void DifferentInternalStructureIsNotACopy()
    {
        // Same file content but different relative layout inside -> NOT a copy.
        var a = File(@"C:\a\dir\f.txt");   // nested one level
        var b = File(@"C:\b\f.txt");        // flat
        var parentA = File(@"C:\a\readme.txt");
        var parentB = File(@"C:\b\readme2.txt");

        var groups = new[] { Group(0xF0, a, b) };

        var result = new DuplicateFolderEngine().FindDuplicateFolders(
            new[] { a, b, parentA, parentB }, groups, rawStructureProvider: _ => "verified");

        Assert.Empty(result);
    }
}
