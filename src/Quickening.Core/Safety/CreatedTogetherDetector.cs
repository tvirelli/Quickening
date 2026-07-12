namespace Quickening.Core.Safety;

/// <summary>
/// Heuristic signal that a duplicate group's copies were written together as a
/// set (installer/extractor) rather than manually duplicated over time. When
/// Windows copies a file the copy gets a fresh creation timestamp, but a set
/// written together shares one creation second - so identical creation seconds
/// across all copies flags "created together". A warning only; never a block.
/// </summary>
public static class CreatedTogetherDetector
{
    public static bool IsLikelyCreatedTogether(IReadOnlyList<DateTime> creationTimesUtc)
    {
        if (creationTimesUtc.Count < 2)
        {
            return false;
        }

        var first = TruncateToSecond(creationTimesUtc[0]);
        if (first == default)
        {
            return false;
        }

        foreach (var time in creationTimesUtc)
        {
            var truncated = TruncateToSecond(time);
            if (truncated == default || truncated != first)
            {
                return false;
            }
        }

        return true;
    }

    private static DateTime TruncateToSecond(DateTime t) =>
        t == default ? default : new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);
}
