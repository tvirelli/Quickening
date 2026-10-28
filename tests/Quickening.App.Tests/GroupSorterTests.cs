using Quickening.App.Settings;
using Quickening.App.ViewModels;
using Quickening.Core.Models;
using Xunit;

namespace Quickening.App.Tests;

public class GroupSorterTests
{
    private static SelectableFile File(string path, long size) => new()
    {
        Path = path,
        SizeBytes = size,
        Category = MimeCategory.Other,
        LastWriteTimeUtc = DateTime.UtcNow,
    };

    // Each "group" is just its file list; the sorter only needs the files.
    private static readonly SelectableFile[] Small = { File(@"C:\s1", 10), File(@"C:\s2", 10) };
    private static readonly SelectableFile[] Big = { File(@"C:\b1", 900), File(@"C:\b2", 900) };
    private static readonly SelectableFile[] Medium = { File(@"C:\m1", 50), File(@"C:\m2", 500) };

    private static IReadOnlyList<SelectableFile[]> Scan => new[] { Small, Big, Medium };

    [Fact]
    public void ScanOrder_LeavesGroupsAsTheyCame()
    {
        Assert.Equal(Scan, GroupSorter.Order(Scan, g => g, ResultsSortOrder.ScanOrder));
    }

    [Fact]
    public void LargestFirst_RanksEachGroupByItsLargestFile()
    {
        // Medium's largest file (500) puts it above Small even though its
        // other copy is only 50.
        Assert.Equal(new[] { Big, Medium, Small }, GroupSorter.Order(Scan, g => g, ResultsSortOrder.LargestFirst));
    }

    [Fact]
    public void SmallestFirst_IsTheReverse()
    {
        Assert.Equal(new[] { Small, Medium, Big }, GroupSorter.Order(Scan, g => g, ResultsSortOrder.SmallestFirst));
    }

    [Fact]
    public void Ties_KeepScanOrder()
    {
        var first = new[] { File(@"C:\x1", 70), File(@"C:\x2", 70) };
        var second = new[] { File(@"C:\y1", 70), File(@"C:\y2", 70) };

        Assert.Equal(new[] { first, second }, GroupSorter.Order(new[] { first, second }, g => g, ResultsSortOrder.LargestFirst));
    }
}
