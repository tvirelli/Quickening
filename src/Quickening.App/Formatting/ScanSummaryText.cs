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
