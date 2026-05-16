using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using KusPus.Core.Defaults;
using KusPus.Core.Settings;
using Xunit;

namespace KusPus.Persistence.Tests;

public class PrefsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public PrefsStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "KusPus_PrefsStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; some watchers may keep handles transiently.
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_writes_defaults_when_file_does_not_exist()
    {
        using var store = new PrefsStore(_settingsPath);

        File.Exists(_settingsPath).Should().BeTrue();
        store.Current.Should().BeEquivalentTo(DefaultSettings.ForFirstRun());
    }

    [Fact]
    public void Constructor_loads_existing_file()
    {
        var saved = DefaultSettings.ForFirstRun() with
        {
            Hotkey = new HotkeySettings { HoldThresholdMs = 500 },
        };
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(saved, TestJsonOptions));

        using var store = new PrefsStore(_settingsPath);

        store.Current.Hotkey.HoldThresholdMs.Should().Be(500);
    }

    [Fact]
    public void Corrupt_JSON_resets_to_defaults()
    {
        File.WriteAllText(_settingsPath, "{not valid json");

        using var store = new PrefsStore(_settingsPath);

        store.Current.Should().BeEquivalentTo(DefaultSettings.ForFirstRun());
    }

    [Fact]
    public void Unknown_schemaVersion_resets_to_defaults()
    {
        File.WriteAllText(_settingsPath, """{"schemaVersion": 999, "hotkey": {}}""");

        using var store = new PrefsStore(_settingsPath);

        store.Current.Should().BeEquivalentTo(DefaultSettings.ForFirstRun());
    }

    [Fact]
    public void Invalid_holdThresholdMs_resets_only_that_field()
    {
        var malformed =
            """{"schemaVersion": 1, "hotkey": {"holdThresholdMs": 0}, "audio": {"captureSampleRate": 22050}}""";
        File.WriteAllText(_settingsPath, malformed);

        using var store = new PrefsStore(_settingsPath);

        store.Current.Hotkey.HoldThresholdMs.Should().Be(250);
        store.Current.Audio.CaptureSampleRate.Should().Be(22050);
    }

    [Fact]
    public async Task SaveAsync_persists_atomically_and_updates_Current()
    {
        using var store = new PrefsStore(_settingsPath);
        var updated = store.Current with { Autostart = true };

        await store.SaveAsync(updated);

        store.Current.Autostart.Should().BeTrue();
        File.Exists(_settingsPath + ".tmp").Should().BeFalse();

        var roundTrip = JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(_settingsPath),
            TestJsonOptions);
        roundTrip.Should().NotBeNull();
        roundTrip!.Autostart.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_emits_on_Changes()
    {
        using var store = new PrefsStore(_settingsPath);
        AppSettings? received = null;
        using var sub = store.Changes.Subscribe(s => received = s);

        var updated = store.Current with { Autostart = true };
        await store.SaveAsync(updated);

        received.Should().NotBeNull();
        received!.Autostart.Should().BeTrue();
    }

    [Fact]
    public void Changes_replays_current_value_to_new_subscribers()
    {
        using var store = new PrefsStore(_settingsPath);
        AppSettings? received = null;

        using var sub = store.Changes.Subscribe(s => received = s);

        received.Should().NotBeNull();
        received.Should().Be(store.Current);
    }

    [Fact]
    public async Task ReloadFromDiskAsync_picks_up_external_edits()
    {
        using var store = new PrefsStore(_settingsPath);
        var snapshot = store.Current;

        var changed = snapshot with { Autostart = true };
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(changed, TestJsonOptions));

        await store.ReloadFromDiskAsync();

        store.Current.Autostart.Should().BeTrue();
    }

    [Fact]
    public void Unknown_top_level_fields_are_warned_and_ignored()
    {
        // TECH_SPEC §9.4: "Unknown fields are warned + ignored."
        var withExtra =
            """{"schemaVersion": 1, "futureFeature": true, "hotkey": {"holdThresholdMs": 300}}""";
        File.WriteAllText(_settingsPath, withExtra);
        var logger = new CapturingLogger<PrefsStore>();

        using var store = new PrefsStore(_settingsPath, logger);

        // Known fields still load.
        store.Current.Hotkey.HoldThresholdMs.Should().Be(300);
        // Unknown field triggered a warning.
        logger.HasWarningContaining("futureFeature").Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_serialises_concurrent_calls_without_corrupting_the_file()
    {
        // TECH_SPEC §9.5: "Concurrent writes serialized by a SemaphoreSlim(1,1)."
        using var store = new PrefsStore(_settingsPath);

        var baseline = store.Current;
        var tasks = Enumerable.Range(0, 10)
            .Select(i => store.SaveAsync(baseline with { Autostart = i % 2 == 0 }))
            .ToArray();
        await Task.WhenAll(tasks);

        // If the semaphore is doing its job the file is well-formed JSON.
        var json = File.ReadAllText(_settingsPath);
        var parsed = JsonSerializer.Deserialize<AppSettings>(json, TestJsonOptions);
        parsed.Should().NotBeNull();
        parsed!.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void JSON_property_names_are_camelCase_per_spec()
    {
        using var store = new PrefsStore(_settingsPath);
        var json = File.ReadAllText(_settingsPath);

        json.Should().Contain(@"""schemaVersion""");
        json.Should().Contain(@"""hotkey""");
        json.Should().Contain(@"""holdThresholdMs""");
        json.Should().NotContain(@"""SchemaVersion""");
        json.Should().NotContain(@"""Hotkey""");
    }

    [Fact]
    public void VirtualKey_enum_serialises_as_PascalCase_string()
    {
        using var store = new PrefsStore(_settingsPath);
        var json = File.ReadAllText(_settingsPath);

        json.Should().Contain(@"""LeftCtrl""");
        json.Should().Contain(@"""LeftWin""");
    }

    private static JsonSerializerOptions TestJsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
