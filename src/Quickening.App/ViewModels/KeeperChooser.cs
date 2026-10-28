using Quickening.App.Settings;

namespace Quickening.App.ViewModels;

/// <summary>
/// Picks the one copy in a duplicate group that stays (the KEEP pill, and the
/// file "Select Recommended" leaves unticked), plus the pill's label. Settings'
/// Keep overrides come first: the highest-ranked override folder holding any
/// copy wins, and the normal KeepRule only decides among the copies in that
/// folder. With no copy in any override folder the KeepRule decides alone.
/// Pure and static so the ranking is testable without App.Settings.
/// </summary>
public static class KeeperChooser
{
    public static (SelectableFile Keeper, string Label) Choose(
        IReadOnlyList<SelectableFile> files, KeepRule rule, IReadOnlyList<string> overrideFolders)
    {
        foreach (var folder in overrideFolders)
        {
            var inFolder = files.Where(f => AppSettings.IsUnderFolder(f.Path, folder)).ToList();
            if (inFolder.Count > 0)
            {
                return (ByRule(inFolder, rule), $"KEEP — in {FolderLabel(folder)}");
            }
        }

        var keeper = ByRule(files, rule);
        return (keeper, RuleLabel(rule, keeper, files));
    }

    private static SelectableFile ByRule(IReadOnlyList<SelectableFile> files, KeepRule rule) => rule switch
    {
        KeepRule.Oldest => files.OrderBy(f => f.LastWriteTimeUtc).First(),
        KeepRule.ShortestPath => files.OrderBy(f => f.Path.Length).First(),
        _ => files.OrderByDescending(f => f.LastWriteTimeUtc).First(),
    };

    // "Keepers" for C:\...\Keepers; a drive root has no folder name, so it
    // shows as "D:\".
    private static string FolderLabel(string folder)
    {
        var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) || name.EndsWith(':') ? folder.TrimEnd('\\', '/') + @"\" : name;
    }

    // "KEEP — newest" etc., or plain "KEEP" when the rule didn't really
    // decide: rows show times to the second, so copies made in one go all
    // read e.g. 8:08:16 PM and calling one "newest" (won on fractions of a
    // second) looked arbitrary. Same for equal-length paths.
    private static string RuleLabel(KeepRule rule, SelectableFile keeper, IReadOnlyList<SelectableFile> files)
    {
        static long Second(DateTime t) => t.Ticks / TimeSpan.TicksPerSecond;

        var tied = rule switch
        {
            KeepRule.ShortestPath => files.Any(f => !ReferenceEquals(f, keeper) && f.Path.Length == keeper.Path.Length),
            _ => files.Any(f => !ReferenceEquals(f, keeper) && Second(f.LastWriteTimeUtc) == Second(keeper.LastWriteTimeUtc)),
        };

        return tied
            ? "KEEP"
            : rule switch
            {
                KeepRule.Oldest => "KEEP — oldest",
                KeepRule.ShortestPath => "KEEP — shortest path",
                _ => "KEEP — newest",
            };
    }
}
