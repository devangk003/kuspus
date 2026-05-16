using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KusPus.Persistence.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public HistoryStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "KusPus_HistoryStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "history.db");
    }

    public void Dispose()
    {
        // SQLite occasionally holds the .db-wal / .db-shm files for a tick after dispose.
        SqliteConnection.ClearAllPools();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AppendAsync_round_trips_every_field_via_SearchAsync()
    {
        using var store = new HistoryStore(_dbPath);
        var input = new TranscriptRecord(
            Id: 0,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(1_747_345_200_000),
            Text: "hello world",
            Duration: TimeSpan.FromSeconds(3),
            Model: "ggml-tiny.en",
            TargetApp: "Slack",
            Status: TranscriptStatus.Ok,
            FailedWavPath: null,
            Outcome: PasteOutcome.Pasted);

        await store.AppendAsync(input);
        var results = await store.SearchAsync(null);

        results.Should().HaveCount(1);
        var r = results[0];
        r.Id.Should().BeGreaterThan(0);
        r.Timestamp.ToUnixTimeMilliseconds().Should().Be(1_747_345_200_000);
        r.Text.Should().Be("hello world");
        r.Duration.Should().Be(TimeSpan.FromSeconds(3));
        r.Model.Should().Be("ggml-tiny.en");
        r.TargetApp.Should().Be("Slack");
        r.Status.Should().Be(TranscriptStatus.Ok);
        r.FailedWavPath.Should().BeNull();
        r.Outcome.Should().Be(PasteOutcome.Pasted);
    }

    [Fact]
    public async Task SearchAsync_with_FTS_query_returns_only_matching_rows()
    {
        using var store = new HistoryStore(_dbPath);
        await store.AppendAsync(MakeRecord(text: "the quick brown fox"));
        await store.AppendAsync(MakeRecord(text: "lazy dog jumps high"));
        await store.AppendAsync(MakeRecord(text: "fox news today"));

        var results = await store.SearchAsync("fox");

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Text.Contains("fox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchAsync_without_query_orders_by_timestamp_descending()
    {
        using var store = new HistoryStore(_dbPath);
        await store.AppendAsync(MakeRecord(text: "first", ts: 1_000));
        await store.AppendAsync(MakeRecord(text: "second", ts: 2_000));
        await store.AppendAsync(MakeRecord(text: "third", ts: 3_000));

        var results = await store.SearchAsync(null);

        results.Select(r => r.Text).Should().Equal("third", "second", "first");
    }

    [Fact]
    public async Task SearchAsync_respects_limit_and_offset()
    {
        using var store = new HistoryStore(_dbPath);
        for (int i = 0; i < 10; i++)
        {
            await store.AppendAsync(MakeRecord(text: $"row {i}", ts: 1_000 + i));
        }

        var page = await store.SearchAsync(null, limit: 3, offset: 2);

        page.Should().HaveCount(3);
        // Most recent first; offset 2 skips rows 9 and 8.
        page.Select(r => r.Text).Should().Equal("row 7", "row 6", "row 5");
    }

    [Fact]
    public async Task DeleteAsync_removes_row_from_main_table_and_FTS_index()
    {
        using var store = new HistoryStore(_dbPath);
        await store.AppendAsync(MakeRecord(text: "delete me"));
        await store.AppendAsync(MakeRecord(text: "keep me"));

        var all = await store.SearchAsync(null);
        var idToDelete = all.First(r => r.Text == "delete me").Id;
        await store.DeleteAsync(idToDelete);

        var remaining = await store.SearchAsync(null);
        remaining.Should().HaveCount(1);
        remaining[0].Text.Should().Be("keep me");

        var ftsAfterDelete = await store.SearchAsync("delete");
        ftsAfterDelete.Should().BeEmpty(because: "the AFTER DELETE trigger should clear the FTS row too");
    }

    [Fact]
    public async Task PurgeAllAsync_empties_the_store()
    {
        using var store = new HistoryStore(_dbPath);
        await store.AppendAsync(MakeRecord(text: "a"));
        await store.AppendAsync(MakeRecord(text: "b"));
        await store.AppendAsync(MakeRecord(text: "c"));

        await store.PurgeAllAsync();

        (await store.SearchAsync(null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_status_with_failed_wav_path_round_trips()
    {
        using var store = new HistoryStore(_dbPath);
        await store.AppendAsync(MakeRecord(
            status: TranscriptStatus.Failed,
            failedWav: @"C:\tmp\bad.wav"));

        var results = await store.SearchAsync(null);

        results[0].Status.Should().Be(TranscriptStatus.Failed);
        results[0].FailedWavPath.Should().Be(@"C:\tmp\bad.wav");
    }

    [Fact]
    public async Task All_PasteOutcome_values_round_trip_including_null()
    {
        using var store = new HistoryStore(_dbPath);
        await store.AppendAsync(MakeRecord(text: "a", ts: 1, outcome: PasteOutcome.Pasted));
        await store.AppendAsync(MakeRecord(text: "b", ts: 2, outcome: PasteOutcome.ClipboardOnly));
        await store.AppendAsync(MakeRecord(text: "c", ts: 3, outcome: PasteOutcome.WindowGone));
        await store.AppendAsync(MakeRecord(text: "d", ts: 4, outcome: null));

        var byText = (await store.SearchAsync(null)).ToDictionary(r => r.Text);

        byText["a"].Outcome.Should().Be(PasteOutcome.Pasted);
        byText["b"].Outcome.Should().Be(PasteOutcome.ClipboardOnly);
        byText["c"].Outcome.Should().Be(PasteOutcome.WindowGone);
        byText["d"].Outcome.Should().BeNull();
    }

    [Fact]
    public void Backup_file_is_created_on_first_construction()
    {
        var backupPath = _dbPath + ".bak";
        File.Exists(backupPath).Should().BeFalse();

        using var store = new HistoryStore(_dbPath);

        File.Exists(backupPath).Should().BeTrue();
    }

    [Fact]
    public void Backup_is_refreshed_when_older_than_24_hours()
    {
        var backupPath = _dbPath + ".bak";

        using (var first = new HistoryStore(_dbPath)) { }

        // The default Microsoft.Data.Sqlite pool retains file handles after dispose;
        // clearing pools releases them so SetLastWriteTimeUtc isn't a silent no-op.
        SqliteConnection.ClearAllPools();

        // Force the backup to look stale.
        File.SetLastWriteTimeUtc(backupPath, DateTime.UtcNow - TimeSpan.FromHours(25));
        var staleTime = File.GetLastWriteTimeUtc(backupPath);

        using (var second = new HistoryStore(_dbPath)) { }

        var newTime = File.GetLastWriteTimeUtc(backupPath);
        newTime.Should().BeAfter(staleTime);
    }

    [Fact]
    public void Backup_is_left_alone_when_fresh()
    {
        var backupPath = _dbPath + ".bak";

        using (var first = new HistoryStore(_dbPath)) { }
        SqliteConnection.ClearAllPools();
        var firstBackupTime = File.GetLastWriteTimeUtc(backupPath);

        using (var second = new HistoryStore(_dbPath)) { }

        var secondBackupTime = File.GetLastWriteTimeUtc(backupPath);
        secondBackupTime.Should().Be(firstBackupTime);
    }

    [Fact]
    public async Task SearchAsync_does_not_throw_on_FTS5_special_characters_in_query()
    {
        // Raw user input often contains FTS5 syntax (* " : NEAR ( )) — unsanitised it
        // crashes SQLite. HistoryStore.SanitiseFtsQuery wraps each token in quotes.
        using var store = new HistoryStore(_dbPath);
        await store.AppendAsync(MakeRecord(text: "the quick brown fox"));

        var malicious = new[]
        {
            "fox*",
            "\"quick\"",
            "fox: news",
            "(fox)",
            "NEAR(fox, news)",
            "double\"\"quote",
        };

        foreach (var q in malicious)
        {
            var act = async () => await store.SearchAsync(q);
            await act.Should().NotThrowAsync(because: $"query '{q}' should be sanitised");
        }
    }

    [Fact]
    public void Schema_enables_WAL_and_sets_user_version_to_1()
    {
        using var store = new HistoryStore(_dbPath);

        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();

        using var jm = conn.CreateCommand();
        jm.CommandText = "PRAGMA journal_mode;";
        ((string)jm.ExecuteScalar()!).Should().Be("wal", because: "TECH_SPEC §17 requires WAL");

        using var uv = conn.CreateCommand();
        uv.CommandText = "PRAGMA user_version;";
        ((long)uv.ExecuteScalar()!).Should().Be(1, because: "TECH_SPEC §17 specifies user_version = 1");
    }

    private static TranscriptRecord MakeRecord(
        string text = "test",
        long ts = 1_000,
        string model = "ggml-tiny.en",
        string? targetApp = null,
        TranscriptStatus status = TranscriptStatus.Ok,
        string? failedWav = null,
        PasteOutcome? outcome = null) =>
        new(
            Id: 0,
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(ts),
            Text: text,
            Duration: TimeSpan.FromSeconds(1),
            Model: model,
            TargetApp: targetApp,
            Status: status,
            FailedWavPath: failedWav,
            Outcome: outcome);
}
