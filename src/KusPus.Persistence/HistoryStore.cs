// HistoryStore logs only on startup (backup rotation) and on backup failures. The
// LoggerMessage source-gen pattern that CA1848 / CA1873 want is overkill for that
// volume — same rationale as PrefsStore.cs.
#pragma warning disable CA1848 // Use the LoggerMessage delegates
#pragma warning disable CA1873 // Avoid potentially expensive logging argument evaluation

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Persistence;

/// <summary>
/// SQLite + FTS5 implementation of <see cref="IHistoryStore"/>. See TECH_SPEC §17.
///
/// Concurrency: a single <see cref="SqliteConnection"/> guarded by a
/// <see cref="SemaphoreSlim"/>. Spec §17 envisions a separate read-only connection
/// for concurrent reads under WAL; for v1's expected load (a few writes/minute, a
/// handful of reads/day) the single-connection design is simpler and adequate. The
/// connection string still enables WAL so concurrent readers from other processes
/// (e.g. a SQLite browser) can inspect the file safely.
/// </summary>
public sealed class HistoryStore : IHistoryStore, IDisposable
{
    private readonly string _dbPath;
    private readonly string _backupPath;
    private readonly ILogger<HistoryStore> _logger;
    private readonly SemaphoreSlim _lock = new(initialCount: 1, maxCount: 1);
    private readonly SqliteConnection _connection;

    public HistoryStore(string dbPath, ILogger<HistoryStore>? logger = null)
    {
        _dbPath = dbPath;
        _backupPath = dbPath + ".bak";
        _logger = logger ?? NullLogger<HistoryStore>.Instance;

        EnsureDirectoryExists();
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        InitialiseSchema();
        RotateBackupIfStale();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public async Task AppendAsync(TranscriptRecord record)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO transcripts (ts, text, duration_ms, model, target_app, status, failed_wav, paste_outcome)
                VALUES ($ts, $text, $duration_ms, $model, $target_app, $status, $failed_wav, $paste_outcome);";
            cmd.Parameters.AddWithValue("$ts", record.Timestamp.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$text", record.Text);
            cmd.Parameters.AddWithValue("$duration_ms", (long)record.Duration.TotalMilliseconds);
            cmd.Parameters.AddWithValue("$model", record.Model);
            cmd.Parameters.AddWithValue("$target_app", (object?)record.TargetApp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", StatusToSql(record.Status));
            cmd.Parameters.AddWithValue("$failed_wav", (object?)record.FailedWavPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$paste_outcome", (object?)OutcomeToSql(record.Outcome) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<TranscriptRecord>> SearchAsync(string? query, int limit = 200, int offset = 0)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(query))
            {
                cmd.CommandText = @"
                    SELECT id, ts, text, duration_ms, model, target_app, status, failed_wav, paste_outcome
                    FROM transcripts
                    ORDER BY ts DESC
                    LIMIT $limit OFFSET $offset;";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT t.id, t.ts, t.text, t.duration_ms, t.model, t.target_app, t.status, t.failed_wav, t.paste_outcome
                    FROM transcripts t
                    INNER JOIN transcripts_fts f ON f.rowid = t.id
                    WHERE transcripts_fts MATCH $query
                    ORDER BY t.ts DESC
                    LIMIT $limit OFFSET $offset;";
                cmd.Parameters.AddWithValue("$query", SanitiseFtsQuery(query));
            }

            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);

            var results = new List<TranscriptRecord>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                results.Add(MapToRecord(reader));
            }

            return results;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(long id)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM transcripts WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PurgeAllAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM transcripts;";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }

    // ── private ──────────────────────────────────────────────────────────────

    private void InitialiseSchema()
    {
        // WAL is idempotent — safe to set on every open.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL;";
            pragma.ExecuteScalar();
        }

        // Read the existing schema version. Only set user_version when the DB is
        // fresh (== 0); future migrations will dispatch off this value, and
        // overwriting it unconditionally would defeat that. See TECH_SPEC §17
        // ("Driven off PRAGMA user_version").
        long currentVersion;
        using (var readVer = _connection.CreateCommand())
        {
            readVer.CommandText = "PRAGMA user_version;";
            currentVersion = (long)(readVer.ExecuteScalar() ?? 0L);
        }

        // Schema per TECH_SPEC §17 (table, index, FTS5 virtual table, triggers).
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS transcripts (
              id            INTEGER PRIMARY KEY AUTOINCREMENT,
              ts            INTEGER NOT NULL,
              text          TEXT    NOT NULL,
              duration_ms   INTEGER NOT NULL,
              model         TEXT    NOT NULL,
              target_app    TEXT,
              status        TEXT    NOT NULL CHECK (status IN ('ok','failed')),
              failed_wav    TEXT,
              paste_outcome TEXT    CHECK (paste_outcome IN ('pasted','clipboard_only','window_gone'))
            );

            CREATE INDEX IF NOT EXISTS ix_transcripts_ts ON transcripts(ts DESC);

            CREATE VIRTUAL TABLE IF NOT EXISTS transcripts_fts USING fts5(
              text, content='transcripts', content_rowid='id'
            );

            CREATE TRIGGER IF NOT EXISTS transcripts_ai AFTER INSERT ON transcripts
              BEGIN INSERT INTO transcripts_fts(rowid, text) VALUES (new.id, new.text); END;

            CREATE TRIGGER IF NOT EXISTS transcripts_ad AFTER DELETE ON transcripts
              BEGIN INSERT INTO transcripts_fts(transcripts_fts, rowid, text) VALUES('delete', old.id, old.text); END;";
        cmd.ExecuteNonQuery();

        if (currentVersion == 0)
        {
            using var setVer = _connection.CreateCommand();
            setVer.CommandText = "PRAGMA user_version = 1;";
            setVer.ExecuteNonQuery();
        }
    }

    private void RotateBackupIfStale()
    {
        try
        {
            var stale = !File.Exists(_backupPath) ||
                DateTime.UtcNow - File.GetLastWriteTimeUtc(_backupPath) > TimeSpan.FromHours(24);

            if (!stale)
            {
                return;
            }

            // Pooling=False — the backup destination is a one-shot connection; we want
            // the file handle to release immediately on dispose so external tools (and
            // tests verifying mtime) aren't blocked by a pooled handle.
            using var dest = new SqliteConnection($"Data Source={_backupPath};Pooling=False");
            dest.Open();
            _connection.BackupDatabase(dest);
            _logger.LogInformation("History database backed up to {Path}.", _backupPath);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            _logger.LogWarning(ex, "History backup rotation failed; continuing without backup.");
        }
    }

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static TranscriptRecord MapToRecord(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
        Text: r.GetString(2),
        Duration: TimeSpan.FromMilliseconds(r.GetInt64(3)),
        Model: r.GetString(4),
        TargetApp: r.IsDBNull(5) ? null : r.GetString(5),
        Status: StatusFromSql(r.GetString(6)),
        FailedWavPath: r.IsDBNull(7) ? null : r.GetString(7),
        Outcome: r.IsDBNull(8) ? null : OutcomeFromSql(r.GetString(8)));

    /// <summary>
    /// Quote-wraps each whitespace-separated token so FTS5 operators (<c>* "" : NEAR ( )</c>)
    /// in raw user input are treated as literal characters of a phrase. Preserves AND-across-tokens
    /// semantics from <c>SELECT ... WHERE fts MATCH 'tok1 tok2'</c>.
    /// </summary>
    private static string SanitiseFtsQuery(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
    }

    private static string StatusToSql(TranscriptStatus s) => s switch
    {
        TranscriptStatus.Ok => "ok",
        TranscriptStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static TranscriptStatus StatusFromSql(string s) => s switch
    {
        "ok" => TranscriptStatus.Ok,
        "failed" => TranscriptStatus.Failed,
        _ => throw new InvalidDataException($"Unknown status in DB: '{s}'"),
    };

    private static string? OutcomeToSql(PasteOutcome? o) => o switch
    {
        PasteOutcome.Pasted => "pasted",
        PasteOutcome.ClipboardOnly => "clipboard_only",
        PasteOutcome.WindowGone => "window_gone",
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };

    private static PasteOutcome OutcomeFromSql(string s) => s switch
    {
        "pasted" => PasteOutcome.Pasted,
        "clipboard_only" => PasteOutcome.ClipboardOnly,
        "window_gone" => PasteOutcome.WindowGone,
        _ => throw new InvalidDataException($"Unknown paste_outcome in DB: '{s}'"),
    };
}
