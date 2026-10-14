namespace Quickening.App;

/// <summary>
/// Process-wide "files are being removed right now" flag - the removal
/// counterpart to ScanCoordinator. Restart to update (1.3.2) checks it so a
/// restart can't stop a removal batch halfway. A counter, not a bool, so
/// overlapping removals (e.g. Tidy up while a results removal finishes) can't
/// clear each other.
/// </summary>
public static class RemovalActivity
{
    private static int _active;

    public static bool IsActive => Volatile.Read(ref _active) > 0;

    /// <summary>Marks a removal as running until the returned handle is disposed.</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Handle();
    }

    private sealed class Handle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
