using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Quickening.Core.Models;

namespace Quickening.Core.Storage;

/// <summary>
/// Thread-safe: every public method serializes on an internal lock, because
/// one shared instance is reached concurrently from user-initiated scans
/// (thread pool), the scheduled-scan timer, the tray watcher's per-file
/// tasks, and UI-thread deletes. SQLite itself is fast enough that a plain
/// lock costs nothing next to the disk I/O around it. WAL mode is enabled
/// so a separate process can read/write the same database file concurrently
/// via its own SqliteStore instance.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _sync = new();

    public SqliteStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
    }

    public void Initialize()
    {
        lock (_sync)
        {
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "schema.sql";

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
            using var reader = new StreamReader(stream);
            var schemaSql = reader.ReadToEnd();

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = schemaSql;
                command.ExecuteNonQuery();
            }

            // 001_initial_schema.sql's CREATE TABLE IF NOT EXISTS only applies
            // to a brand-new database file, so databases created before
            // PerceptualHash existed need this ALTER. pragma_table_info is the
            // stable way to ask "does the column exist" - matching on SQLite's
            // error-message wording is not a contract.
            if (!ColumnExists("Files", "PerceptualHash"))
            {
                using var alterCommand = _connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Files ADD COLUMN PerceptualHash INTEGER;";
                alterCommand.ExecuteNonQuery();
            }

            // NTFS paths are case-insensitive; SQLite's default BINARY
            // collation is not. A Files table whose Path column lacks
            // COLLATE NOCASE produces duplicate rows, cache misses, and the
            // watcher matching a file against itself when only casing
            // differs - so rebuild any legacy table in place.
            MigratePathCollationIfNeeded();

            using (var pragmaCommand = _connection.CreateCommand())
            {
                pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
                pragmaCommand.ExecuteNonQuery();
            }
        }
    }

    private bool ColumnExists(string table, string column)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return (long)command.ExecuteScalar()! > 0;
    }

    private void MigratePathCollationIfNeeded()
    {
        using (var checkCommand = _connection.CreateCommand())
        {
            checkCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Files';";
            var createSql = checkCommand.ExecuteScalar() as string ?? string.Empty;
            if (createSql.Contains("NOCASE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var transaction = _connection.BeginTransaction();
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            // INSERT OR IGNORE: a legacy table may hold case-duplicate rows
            // for the same physical file; the first row wins, the rest are
            // stale cache entries.
            command.CommandText = """
                CREATE TABLE Files_new (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    SizeBytes INTEGER NOT NULL,
                    LastWriteTimeUtc TEXT NOT NULL,
                    PartialHash BLOB,
                    FullHash BLOB,
                    PerceptualHash INTEGER,
                    MimeCategory TEXT NOT NULL,
                    FirstSeenUtc TEXT NOT NULL,
                    LastSeenUtc TEXT NOT NULL
                );
                INSERT OR IGNORE INTO Files_new (Id, Path, SizeBytes, LastWriteTimeUtc, PartialHash, FullHash, PerceptualHash, MimeCategory, FirstSeenUtc, LastSeenUtc)
                    SELECT Id, Path, SizeBytes, LastWriteTimeUtc, PartialHash, FullHash, PerceptualHash, MimeCategory, FirstSeenUtc, LastSeenUtc FROM Files;
                DROP TABLE Files;
                ALTER TABLE Files_new RENAME TO Files;
                CREATE INDEX IF NOT EXISTS IX_Files_SizeBytes ON Files (SizeBytes);
                CREATE INDEX IF NOT EXISTS IX_Files_FullHash ON Files (FullHash);
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void UpsertFile(FileRecord record)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            UpsertFileCore(command, record);
        }
    }

    /// <summary>
    /// Upserts a whole scan's records in chunked transactions. Per-row
    /// autocommit costs one fsync per file - on a 200k-file scan that is
    /// minutes of dead time after the compare phase finishes; batched it is
    /// a handful. Chunked (rather than one giant transaction) so the
    /// internal lock is released periodically - the UI thread reads stats
    /// through this same instance and must not stall behind the whole batch.
    /// </summary>
    public void UpsertFiles(IEnumerable<FileRecord> records, CancellationToken cancellationToken = default)
    {
        const int chunkSize = 5000;
        foreach (var chunk in records.Chunk(chunkSize))
        {
            lock (_sync)
            {
                using var transaction = _connection.BeginTransaction();
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                foreach (var record in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UpsertFileCore(command, record);
                }

                transaction.Commit();
            }
        }
    }

    private static void UpsertFileCore(SqliteCommand command, FileRecord record)
    {
        // Hash columns: a scan only computes hashes for files that need
        // comparing *this* scan, so a record often arrives with null hashes
        // for a file whose cached hashes are still perfectly valid. Only
        // discard stored hashes when size/mtime actually changed; otherwise
        // keep whichever value is non-null.
        command.CommandText = """
            INSERT INTO Files (Path, SizeBytes, LastWriteTimeUtc, PartialHash, FullHash, PerceptualHash, MimeCategory, FirstSeenUtc, LastSeenUtc)
            VALUES ($path, $size, $mtime, $partialHash, $fullHash, $perceptualHash, $category, $now, $now)
            ON CONFLICT(Path) DO UPDATE SET
                PartialHash = CASE WHEN Files.SizeBytes = excluded.SizeBytes AND Files.LastWriteTimeUtc = excluded.LastWriteTimeUtc
                    THEN COALESCE(excluded.PartialHash, Files.PartialHash) ELSE excluded.PartialHash END,
                FullHash = CASE WHEN Files.SizeBytes = excluded.SizeBytes AND Files.LastWriteTimeUtc = excluded.LastWriteTimeUtc
                    THEN COALESCE(excluded.FullHash, Files.FullHash) ELSE excluded.FullHash END,
                PerceptualHash = CASE WHEN Files.SizeBytes = excluded.SizeBytes AND Files.LastWriteTimeUtc = excluded.LastWriteTimeUtc
                    THEN COALESCE(excluded.PerceptualHash, Files.PerceptualHash) ELSE excluded.PerceptualHash END,
                SizeBytes = excluded.SizeBytes,
                LastWriteTimeUtc = excluded.LastWriteTimeUtc,
                MimeCategory = excluded.MimeCategory,
                LastSeenUtc = excluded.LastSeenUtc;
            """;

        var now = DateTime.UtcNow.ToString("O");
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$path", record.Path);
        command.Parameters.AddWithValue("$size", record.SizeBytes);
        command.Parameters.AddWithValue("$mtime", record.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$partialHash", (object?)record.PartialHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$fullHash", (object?)record.FullHash ?? DBNull.Value);
        // SQLite INTEGER is a signed 64-bit value with no unsigned variant -
        // reinterpreting the same 64 bits via unchecked((long)...) round-trips
        // perfectly back through unchecked((ulong)...) on read, regardless of
        // whether the top bit is set (a dHash frequently sets it - it's a
        // plain bit pattern, not a magnitude).
        command.Parameters.AddWithValue("$perceptualHash", record.PerceptualHash is { } hash ? unchecked((long)hash) : DBNull.Value);
        command.Parameters.AddWithValue("$category", record.Category.ToString());
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes rows for files under <paramref name="rootPath"/> that were not
    /// seen since <paramref name="seenSinceUtc"/> AND are verifiably gone
    /// from disk. Without pruning, the watcher's FindFileByFullHash happily
    /// "matches" a new file against a row whose file is long gone and tells
    /// the user a copy exists when it does not. The on-disk existence check
    /// matters because "not seen by this scan" is NOT "deleted": a re-scan
    /// with hidden files off (or cloud placeholders excluded) skips files
    /// that are still right there, and blindly pruning their rows would
    /// destroy valid hash cache and silence the watcher for them.
    /// </summary>
    public int PruneFilesNotSeenSince(string rootPath, DateTime seenSinceUtc)
    {
        var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        var escaped = prefix
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

        var stalePaths = new List<string>();
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Path FROM Files
                WHERE Path LIKE $prefix ESCAPE '\' AND LastSeenUtc < $seenSince;
                """;
            command.Parameters.AddWithValue("$prefix", escaped + "%");
            command.Parameters.AddWithValue("$seenSince", seenSinceUtc.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                stalePaths.Add(reader.GetString(0));
            }
        }

        // Existence checks run OUTSIDE the lock - they hit the disk and
        // must not stall every other store user for their duration.
        var gonePaths = stalePaths.Where(p => !File.Exists(p)).ToList();
        if (gonePaths.Count == 0)
        {
            return 0;
        }

        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Files WHERE Path = $path;";
            var parameter = command.Parameters.Add("$path", SqliteType.Text);
            foreach (var path in gonePaths)
            {
                parameter.Value = path;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return gonePaths.Count;
    }

    public FileRecord? GetFileByPath(string path)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT SizeBytes, LastWriteTimeUtc, MimeCategory, PartialHash, FullHash, PerceptualHash
                FROM Files
                WHERE Path = $path;
                """;
            command.Parameters.AddWithValue("$path", path);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new FileRecord
            {
                Path = path,
                SizeBytes = reader.GetInt64(0),
                LastWriteTimeUtc = DateTime.ParseExact(
                    reader.GetString(1),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Category = Enum.Parse<MimeCategory>(reader.GetString(2)),
                PartialHash = reader.IsDBNull(3) ? null : (byte[])reader[3],
                FullHash = reader.IsDBNull(4) ? null : (byte[])reader[4],
                PerceptualHash = reader.IsDBNull(5) ? null : unchecked((ulong)reader.GetInt64(5)),
            };
        }
    }

    /// <summary>
    /// First file (other than excludingPath) already recorded with the same
    /// FullHash - used by the tray watcher (DuplicateWatcherService) to
    /// decide whether a newly-arrived file matches something from a past
    /// scan. Unlike DuplicateEngine's own matching, this only ever compares
    /// against what's already in the store - a file the watcher's own scan
    /// has never indexed simply won't be found, by design. Rows can be stale
    /// (file deleted/moved since the scan that recorded it), so callers MUST
    /// re-verify the returned path on disk before claiming a duplicate
    /// exists to the user.
    /// </summary>
    public FileRecord? FindFileByFullHash(byte[] fullHash, string excludingPath)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Path, SizeBytes, LastWriteTimeUtc, MimeCategory, PartialHash, PerceptualHash
                FROM Files
                WHERE FullHash = $hash AND Path != $excludingPath
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$hash", fullHash);
            command.Parameters.AddWithValue("$excludingPath", excludingPath);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new FileRecord
            {
                Path = reader.GetString(0),
                SizeBytes = reader.GetInt64(1),
                LastWriteTimeUtc = DateTime.ParseExact(
                    reader.GetString(2),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Category = Enum.Parse<MimeCategory>(reader.GetString(3)),
                PartialHash = reader.IsDBNull(4) ? null : (byte[])reader[4],
                FullHash = fullHash,
                PerceptualHash = reader.IsDBNull(5) ? null : unchecked((ulong)reader.GetInt64(5)),
            };
        }
    }

    /// <summary>
    /// Removes a single Files row (e.g. after this app recycles the file, so
    /// the watcher never matches new arrivals against a path we know is gone).
    /// </summary>
    public void RemoveFileRecord(string path)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM Files WHERE Path = $path;";
            command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }
    }

    public void RecordTrashedFile(string originalPath, string? recycleBinPath, long sizeBytes, long? scanSessionId = null)
    {
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();

            using (var insertCommand = _connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT INTO TrashLog (OriginalPath, RecycleBinPath, SizeBytes, DeletedUtc, ScanSessionId)
                    VALUES ($originalPath, $recycleBinPath, $size, $now, $scanSessionId);
                    """;
                insertCommand.Parameters.AddWithValue("$originalPath", originalPath);
                insertCommand.Parameters.AddWithValue("$recycleBinPath", (object?)recycleBinPath ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("$size", sizeBytes);
                insertCommand.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                insertCommand.Parameters.AddWithValue("$scanSessionId", (object?)scanSessionId ?? DBNull.Value);
                insertCommand.ExecuteNonQuery();
            }

            using (var statsCommand = _connection.CreateCommand())
            {
                statsCommand.Transaction = transaction;
                // Seed-then-update so a database file that somehow skipped
                // Initialize() doesn't silently drop lifetime-stat increments
                // (UPDATE affecting 0 rows raises no error).
                statsCommand.CommandText = """
                    INSERT OR IGNORE INTO Stats (Id, LifetimeBytesReclaimed, LifetimeFilesRemoved) VALUES (1, 0, 0);
                    UPDATE Stats
                    SET LifetimeBytesReclaimed = LifetimeBytesReclaimed + $size,
                        LifetimeFilesRemoved = LifetimeFilesRemoved + 1
                    WHERE Id = 1;
                    """;
                statsCommand.Parameters.AddWithValue("$size", sizeBytes);
                statsCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Reverses one RecordTrashedFile call after a successful Undo restore:
    /// removes the most recent TrashLog row for the path, decrements lifetime
    /// stats, and (when the row belonged to a removal batch) decrements that
    /// session's totals so History matches what is actually gone.
    /// </summary>
    public void UndoTrashedFile(string originalPath)
    {
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();

            long? rowId = null;
            long size = 0;
            long? sessionId = null;
            using (var findCommand = _connection.CreateCommand())
            {
                findCommand.Transaction = transaction;
                findCommand.CommandText = """
                    SELECT Id, SizeBytes, ScanSessionId FROM TrashLog
                    WHERE OriginalPath = $path
                    ORDER BY Id DESC LIMIT 1;
                    """;
                findCommand.Parameters.AddWithValue("$path", originalPath);
                using var reader = findCommand.ExecuteReader();
                if (reader.Read())
                {
                    rowId = reader.GetInt64(0);
                    size = reader.GetInt64(1);
                    sessionId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                }
            }

            if (rowId is null)
            {
                transaction.Commit();
                return;
            }

            using (var deleteCommand = _connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM TrashLog WHERE Id = $id;";
                deleteCommand.Parameters.AddWithValue("$id", rowId.Value);
                deleteCommand.ExecuteNonQuery();
            }

            using (var statsCommand = _connection.CreateCommand())
            {
                statsCommand.Transaction = transaction;
                statsCommand.CommandText = """
                    UPDATE Stats
                    SET LifetimeBytesReclaimed = MAX(0, LifetimeBytesReclaimed - $size),
                        LifetimeFilesRemoved = MAX(0, LifetimeFilesRemoved - 1)
                    WHERE Id = 1;
                    """;
                statsCommand.Parameters.AddWithValue("$size", size);
                statsCommand.ExecuteNonQuery();
            }

            if (sessionId is { } id)
            {
                using var sessionCommand = _connection.CreateCommand();
                sessionCommand.Transaction = transaction;
                sessionCommand.CommandText = """
                    UPDATE ScanSessions
                    SET TotalFilesScanned = MAX(0, TotalFilesScanned - 1),
                        TotalBytesScanned = MAX(0, TotalBytesScanned - $size)
                    WHERE Id = $id;
                    """;
                sessionCommand.Parameters.AddWithValue("$size", size);
                sessionCommand.Parameters.AddWithValue("$id", id);
                sessionCommand.ExecuteNonQuery();

                // If every file in this removal batch has now been undone,
                // drop the session (and any leftover trash-log rows for it)
                // so History doesn't show an empty "0 files - 0 B" session.
                using var pruneCommand = _connection.CreateCommand();
                pruneCommand.Transaction = transaction;
                pruneCommand.CommandText = """
                    DELETE FROM TrashLog WHERE ScanSessionId = $id
                        AND NOT EXISTS (SELECT 1 FROM ScanSessions WHERE Id = $id AND TotalFilesScanned > 0);
                    DELETE FROM ScanSessions WHERE Id = $id AND TotalFilesScanned <= 0;
                    """;
                pruneCommand.Parameters.AddWithValue("$id", id);
                pruneCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public (long BytesReclaimed, long FilesRemoved) GetLifetimeStats()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT LifetimeBytesReclaimed, LifetimeFilesRemoved FROM Stats WHERE Id = 1;";

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Stats row not found - was Initialize() called?");
            }

            return (reader.GetInt64(0), reader.GetInt64(1));
        }
    }

    /// <summary>
    /// Starts a "removal batch" - one round of DeleteSelectedAsync, not a
    /// scan - by inserting a ScanSessions row, reusing that table's shape
    /// rather than adding a parallel one (RootPaths holds the target
    /// label/folder, FilterSnapshot holds the scan-mode label). Returns the
    /// new row's Id, to pass into every RecordTrashedFile call in the same
    /// batch and later into CompleteRemovalBatch.
    /// </summary>
    public long BeginRemovalBatch(string targetLabel, string modeLabel)
    {
        lock (_sync)
        {
            using var insertCommand = _connection.CreateCommand();
            // RETURNING makes insert + id retrieval one statement, so no other
            // write on this connection can slip between them.
            insertCommand.CommandText = """
                INSERT INTO ScanSessions (StartedUtc, RootPaths, FilterSnapshot)
                VALUES ($now, $target, $mode)
                RETURNING Id;
                """;
            insertCommand.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            insertCommand.Parameters.AddWithValue("$target", targetLabel);
            insertCommand.Parameters.AddWithValue("$mode", modeLabel);
            return (long)insertCommand.ExecuteScalar()!;
        }
    }

    public void CompleteRemovalBatch(long removalBatchId, int filesRemoved, long bytesRemoved)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE ScanSessions
                SET CompletedUtc = $now, TotalFilesScanned = $files, TotalBytesScanned = $bytes
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$files", filesRemoved);
            command.Parameters.AddWithValue("$bytes", bytesRemoved);
            command.Parameters.AddWithValue("$id", removalBatchId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Completed removal batches (CompletedUtc IS NOT NULL - an in-progress
    /// or abandoned batch never shows in History), most recent first. Only
    /// batches created via BeginRemovalBatch appear here - a TrashLog row
    /// with a NULL ScanSessionId (from before this feature existed, or any
    /// future call site that doesn't thread one through) simply won't be
    /// grouped into any session.
    /// </summary>
    public IReadOnlyList<RemovalSession> GetRemovalSessions(int limit = 100)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, StartedUtc, RootPaths, FilterSnapshot, TotalFilesScanned, TotalBytesScanned
                FROM ScanSessions
                WHERE CompletedUtc IS NOT NULL
                ORDER BY StartedUtc DESC, Id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            var sessions = new List<RemovalSession>();
            while (reader.Read())
            {
                sessions.Add(new RemovalSession(
                    Id: reader.GetInt64(0),
                    StartedUtc: DateTime.ParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    TargetLabel: reader.GetString(2),
                    ModeLabel: reader.IsDBNull(3) ? null : reader.GetString(3),
                    FilesRemoved: reader.GetInt32(4),
                    BytesRemoved: reader.GetInt64(5)));
            }

            return sessions;
        }
    }

    public IReadOnlyList<TrashLogEntry> GetTrashLogForSession(long scanSessionId)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT Id, OriginalPath, SizeBytes, DeletedUtc
                FROM TrashLog
                WHERE ScanSessionId = $sessionId
                ORDER BY DeletedUtc;
                """;
            command.Parameters.AddWithValue("$sessionId", scanSessionId);

            using var reader = command.ExecuteReader();
            var entries = new List<TrashLogEntry>();
            while (reader.Read())
            {
                entries.Add(new TrashLogEntry(
                    Id: reader.GetInt64(0),
                    OriginalPath: reader.GetString(1),
                    SizeBytes: reader.GetInt64(2),
                    DeletedUtc: DateTime.ParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }

            return entries;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _connection.Dispose();
        }
    }
}

public sealed record RemovalSession(
    long Id,
    DateTime StartedUtc,
    string TargetLabel,
    string? ModeLabel,
    int FilesRemoved,
    long BytesRemoved);

public sealed record TrashLogEntry(
    long Id,
    string OriginalPath,
    long SizeBytes,
    DateTime DeletedUtc);
