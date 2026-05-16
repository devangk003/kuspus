namespace KusPus.Persistence;

/// <summary>
/// One row of the history log. Mirrors the SQL schema in TECH_SPEC §17. <see cref="Id"/>
/// is assigned by SQLite on insert — callers pass <c>0</c> to <see cref="IHistoryStore.AppendAsync"/>
/// and read the assigned id back via <see cref="IHistoryStore.SearchAsync"/>.
/// </summary>
public sealed record TranscriptRecord(
    long Id,
    DateTimeOffset Timestamp,
    string Text,
    TimeSpan Duration,
    string Model,
    string? TargetApp,
    TranscriptStatus Status,
    string? FailedWavPath,
    PasteOutcome? Outcome);

public enum TranscriptStatus
{
    Ok,
    Failed,
}

public enum PasteOutcome
{
    Pasted,
    ClipboardOnly,
    WindowGone,
}
