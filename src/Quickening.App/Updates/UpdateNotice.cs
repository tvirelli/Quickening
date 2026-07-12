namespace Quickening.App.Updates;

/// <summary>
/// Pure decision for the subtle post-update note shown once after Velopack
/// swaps in a new build. Compares the version this install last launched as
/// (AppSettings.LastSeenVersion) against the version now running: returns the
/// note text only when a prior version was recorded AND it differs, so a fresh
/// install shows nothing. No Velopack/WinUI dependency, so it is unit-tested
/// directly.
/// </summary>
public static class UpdateNotice
{
    public static string? Evaluate(string? lastSeenVersion, string currentVersion)
    {
        if (string.IsNullOrEmpty(lastSeenVersion))
        {
            return null;
        }

        if (string.Equals(lastSeenVersion, currentVersion, System.StringComparison.Ordinal))
        {
            return null;
        }

        return $"Updated to v{currentVersion}";
    }
}
