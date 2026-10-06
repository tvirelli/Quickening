using Quickening.Core.Orchestration;

namespace Quickening.App.Formatting;

/// <summary>
/// The scan summary's reassurance line: what Recommended does, plus every
/// other kind of close match the scan found (similar photos, same songs,
/// similar videos, blurry photos), so a scan whose finds are mostly in those
/// sections doesn't read as if it found nothing.
/// </summary>
public static class ScanSummaryText
{
    public static string Reassurance(ScanResult result)
    {
        var extras = new List<string>();
        AddCount(extras, result.SimilarityGroups.Count, "similar-photo group");
        // Tag and sound groups both count as "same-song" here; the results page
        // drops a sound group already covered by a tag group, so this can
        // slightly overcount when both passes find the same pair.
        AddCount(extras, result.MusicGroups.Count + result.SoundGroups.Count, "same-song group");
        AddCount(extras, result.VideoGroups.Count, "similar-video group");
        AddCount(extras, result.BlurryPhotos.Count, "blurry photo");

        var list = JoinWithAnd(extras);
        var plural = extras.Count > 1 || (extras.Count == 1 && !extras[0].StartsWith("1 ", StringComparison.Ordinal));

        if (result.DuplicateGroups.Count == 0)
        {
            return extras.Count == 0
                ? "Nothing to review."
                : $"No exact copies here, but there {(plural ? "are" : "is")} {list} worth a look in Review Results.";
        }

        return extras.Count == 0
            ? "Recommended keeps the newest copy in every group. You'll still confirm before anything moves."
            : $"Recommended keeps the newest copy in every group. There {(plural ? "are" : "is")} also {list} worth a look in Review Results.";
    }

    /// <summary>
    /// The Results page header. With no exact duplicates but close matches
    /// below, "0 duplicate groups · 0 files · 0 B reclaimable" sat above a list
    /// of files and read as a contradiction - say what's there instead.
    /// </summary>
    public static string ResultsHeader(int groupCount, int fileCount, long reclaimableBytes, bool hasCloseMatches)
    {
        if (groupCount == 0 && hasCloseMatches)
        {
            return "No exact duplicates · close matches below";
        }

        return $"{groupCount} duplicate group{(groupCount == 1 ? "" : "s")} · "
            + $"{fileCount} file{(fileCount == 1 ? "" : "s")} · "
            + $"{FileSizeFormatter.Format(reclaimableBytes)} reclaimable";
    }

    private static void AddCount(List<string> parts, int count, string noun)
    {
        if (count > 0)
        {
            parts.Add($"{count} {noun}{(count == 1 ? "" : "s")}");
        }
    }

    private static string JoinWithAnd(List<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };
}
