using Quickening.App.Settings;

namespace Quickening.App.ViewModels;

/// <summary>
/// Orders Results groups for the Sort dropdown. A group ranks by its largest
/// file. OrderBy is stable, so equal-size groups keep their scan order.
/// </summary>
public static class GroupSorter
{
    public static IEnumerable<T> Order<T>(
        IEnumerable<T> groups, Func<T, IEnumerable<SelectableFile>> filesOf, ResultsSortOrder order) => order switch
    {
        ResultsSortOrder.LargestFirst => groups.OrderByDescending(g => LargestFile(filesOf(g))),
        ResultsSortOrder.SmallestFirst => groups.OrderBy(g => LargestFile(filesOf(g))),
        _ => groups,
    };

    private static long LargestFile(IEnumerable<SelectableFile> files) => files.Select(f => f.SizeBytes).DefaultIfEmpty(0).Max();
}
