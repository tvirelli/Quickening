namespace Quickening.App;

/// <summary>
/// Rotating copy for a handful of screens - each load shows a different
/// random line from the relevant pool, for a bit of personality. Each pool
/// avoids repeating the line it handed out last time, so consecutive loads
/// always change. Random.Shared is fine here (no determinism needed, and it
/// is seeded by the runtime, not at construction).
/// </summary>
public static class RandomPhrases
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, int> LastIndexByPool = new();

    private static T Pick<T>(string poolKey, IReadOnlyList<T> pool)
    {
        if (pool.Count <= 1)
        {
            return pool[0];
        }

        lock (Sync)
        {
            LastIndexByPool.TryGetValue(poolKey, out var last);
            int index;
            do
            {
                index = Random.Shared.Next(pool.Count);
            }
            while (index == last);

            LastIndexByPool[poolKey] = index;
            return pool[index];
        }
    }

    // Home hero: headline + its sub-line, kept together so the pairing always
    // makes sense.
    private static readonly (string Headline, string Subtitle)[] HomeHeroPool =
    {
        ("Your disk called.", "There's space in there it would love back. Let's go get it."),
        ("Room to breathe.", "Your drive's a little stuffed. Let's clear some elbow room."),
        ("Spring cleaning time.", "Duplicates pile up quietly. Let's tidy the place."),
        ("Let's reclaim some space.", "Every copy you don't need is space you do."),
        ("Time for a declutter.", "Somewhere in there are gigabytes you'll never miss."),
        ("Make some room.", "Old copies are hiding real space. Let's go find it."),
        ("Your storage, lighter.", "Let's find what's doubling up and send it packing."),
    };

    public static (string Headline, string Subtitle) HomeHero() => Pick(nameof(HomeHeroPool), HomeHeroPool);

    // Scan-starting headline (the enumeration phase).
    private static readonly string[] ScanStartingPool =
    {
        "Getting the lay of the land…",
        "Taking inventory…",
        "Counting every last file…",
        "Sizing things up…",
        "Peeking into every corner…",
        "Rounding up the files…",
        "Scoping out your folders…",
        "Doing a headcount…",
    };

    public static string ScanStarting() => Pick(nameof(ScanStartingPool), ScanStartingPool);

    // The clause AFTER the reclaimable size on the scan summary - the caller
    // keeps the "{N} MB/GB" prefix and appends one of these.
    private static readonly string[] ReclaimClausePool =
    {
        "is ready to come home.",
        "is yours to reclaim.",
        "can come back to you.",
        "is waiting to be freed.",
        "is ready to be reclaimed.",
        "you can win back.",
        "is up for grabs.",
        "of breathing room, right here.",
    };

    public static string ReclaimClause() => Pick(nameof(ReclaimClausePool), ReclaimClausePool);

    // Synonyms for "heavyweights" (large files). All plural nouns that fit
    // both "{N} X" and "No X" / "hide X" templates, and singularise by simply
    // trimming the trailing 's' (see callers).
    private static readonly string[] HeavyweightPool =
    {
        "heavyweights",
        "space hogs",
        "big spenders",
        "gigabyte guzzlers",
        "storage giants",
        "disk hogs",
        "behemoths",
    };

    /// <summary>A random plural "heavyweights" synonym.</summary>
    public static string Heavyweights() => Pick(nameof(HeavyweightPool), HeavyweightPool);

    /// <summary>Random synonym, singular when <paramref name="plural"/> is false.</summary>
    public static string Heavyweights(bool plural)
    {
        var word = Heavyweights();
        return plural ? word : word.TrimEnd('s');
    }

    // Removal-progress headline - generic ("them") so it fits both the
    // Duplicates and Large Files removal flows.
    private static readonly string[] RemovalPool =
    {
        "Off to the Recycle Bin…",
        "Sending them packing…",
        "Making room…",
        "Clearing house…",
        "Tidying up…",
        "Sweeping up the extras…",
        "To the Recycle Bin they go…",
    };

    public static string RemovalHeadline() => Pick(nameof(RemovalPool), RemovalPool);
}
