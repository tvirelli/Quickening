using Quickening.Core.Storage;

namespace Quickening.App.ViewModels;

/// <summary>
/// Thin read-only wrapper over SqliteStore's History-related queries
/// (GetRemovalSessions/GetTrashLogForSession/GetLifetimeStats) - RemovalSession
/// and TrashLogEntry are Core's own record types, reused directly rather than
/// duplicated here, since there's no App-specific shaping needed for them.
/// </summary>
public sealed class HistoryViewModel
{
    private readonly SqliteStore? _store;

    public HistoryViewModel(SqliteStore? store)
    {
        _store = store;
    }

    public (long BytesReclaimed, long FilesRemoved) GetLifetimeStats() =>
        _store?.GetLifetimeStats() ?? (0, 0);

    public IReadOnlyList<RemovalSession> GetSessions() =>
        _store?.GetRemovalSessions() ?? Array.Empty<RemovalSession>();

    public IReadOnlyList<TrashLogEntry> GetEntriesForSession(long sessionId) =>
        _store?.GetTrashLogForSession(sessionId) ?? Array.Empty<TrashLogEntry>();

    public (long ItemCount, long TotalSizeBytes) GetRecycleBinTotals(Core.Deletion.IRecycleBinService recycleBinService) =>
        recycleBinService.GetRecycleBinTotals();
}
