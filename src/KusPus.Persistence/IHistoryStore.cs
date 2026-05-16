namespace KusPus.Persistence;

/// <summary>
/// Append-only log of dictation results plus FTS5 search. See TECH_SPEC §17.
/// </summary>
public interface IHistoryStore
{
    /// <summary>Append a new transcript row. <see cref="TranscriptRecord.Id"/> is ignored — SQLite assigns it.</summary>
    Task AppendAsync(TranscriptRecord record);

    /// <summary>
    /// Search the history. With a non-empty <paramref name="query"/> the result is filtered by FTS5
    /// MATCH; otherwise it returns the most recent rows. Results are ordered by timestamp descending.
    /// </summary>
    Task<IReadOnlyList<TranscriptRecord>> SearchAsync(string? query, int limit = 200, int offset = 0);

    /// <summary>Delete a single row by id. Cascades to the FTS index via the spec's trigger.</summary>
    Task DeleteAsync(long id);

    /// <summary>Delete every row. Used by the Preferences → History "Purge history" button.</summary>
    Task PurgeAllAsync();
}
