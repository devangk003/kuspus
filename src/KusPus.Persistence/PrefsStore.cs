// PrefsStore logs at startup and on rare validation errors — never on a hot path.
// The LoggerMessage source-generated delegate pattern that CA1848/CA1873 want would
// add ~30 lines of boilerplate for ~5 log calls with no measurable benefit at this
// volume. Future hot-path classes (HotkeyEngine, Coordinator) should NOT inherit
// this suppression; the rules stay active globally.
#pragma warning disable CA1848 // Use the LoggerMessage delegates
#pragma warning disable CA1873 // Avoid potentially expensive logging argument evaluation

using System.Reactive.Subjects;
using System.Text.Json;
using KusPus.Core.Defaults;
using KusPus.Core.Settings;
using KusPus.Persistence.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Persistence;

/// <summary>
/// File-backed implementation of <see cref="IPrefsStore"/>. See TECH_SPEC §9 and §20.
/// Writes are atomic (temp file + <see cref="File.Replace"/>) and serialised through
/// a semaphore. <see cref="Changes"/> emits on save and reload.
///
/// FileSystemWatcher integration so external edits trigger reload without a restart
/// is added in Cluster 2B — the <see cref="ReloadFromDiskAsync"/> entry point exists
/// today so tests can exercise the reload path.
/// </summary>
public sealed class PrefsStore : IPrefsStore, IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _path;
    private readonly ILogger<PrefsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(initialCount: 1, maxCount: 1);
    private readonly BehaviorSubject<AppSettings> _changes;

    public PrefsStore(string path, ILogger<PrefsStore>? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger<PrefsStore>.Instance;
        _changes = new BehaviorSubject<AppSettings>(LoadOrInitialise());
    }

    public AppSettings Current => _changes.Value;

    public IObservable<AppSettings> Changes => _changes;

    public async Task SaveAsync(AppSettings updated, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureDirectoryExists();
            var tempPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(updated, JsonOptions.Default);
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            // The constructor always writes settings.json on first run, so by the time
            // any SaveAsync executes the destination is guaranteed to exist. File.Replace
            // is atomic on NTFS — see TECH_SPEC §9.5.
            File.Replace(tempPath, _path, destinationBackupFileName: null);
            _changes.OnNext(updated);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task ReloadFromDiskAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _changes.OnNext(LoadOrInitialise());
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void Dispose()
    {
        _changes.Dispose();
        _saveLock.Dispose();
    }

    // ── private ──────────────────────────────────────────────────────────────

    private AppSettings LoadOrInitialise()
    {
        if (!File.Exists(_path))
        {
            var defaults = DefaultSettings.ForFirstRun();
            EnsureDirectoryExists();
            File.WriteAllText(_path, JsonSerializer.Serialize(defaults, JsonOptions.Default));
            _logger.LogInformation("Settings file did not exist; wrote defaults to {Path}.", _path);
            return defaults;
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Settings file unreadable; using in-memory defaults.");
            return DefaultSettings.ForFirstRun();
        }

        return DeserializeAndValidate(json);
    }

    private AppSettings DeserializeAndValidate(string json)
    {
        try
        {
            // TECH_SPEC §9.4: schemaVersion mismatch runs a migration chain.
            // v1 has no intra-chain migrations — only the version-bounds check.
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = root.TryGetProperty("schemaVersion", out var v) && v.TryGetInt32(out var i)
                ? i
                : CurrentSchemaVersion;

            if (version != CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "Unknown settings schemaVersion {Version}; resetting to defaults.",
                    version);
                return DefaultSettings.ForFirstRun();
            }

            // TECH_SPEC §9.4: "Unknown fields are warned + ignored." Walk the top-level
            // object and warn on anything not in our schema; deserialisation then
            // silently skips them (System.Text.Json default).
            LogUnknownTopLevelFields(root);

            var deserialised = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions.Default);
            if (deserialised is null)
            {
                _logger.LogWarning("Settings deserialised to null; resetting to defaults.");
                return DefaultSettings.ForFirstRun();
            }

            return Validate(deserialised);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Settings JSON corrupt; resetting to defaults.");
            return DefaultSettings.ForFirstRun();
        }
    }

    private static readonly HashSet<string> KnownTopLevelFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "hotkey", "audio", "models", "ui",
        "history", "privacy", "autostart", "onboarding",
    };

    private void LogUnknownTopLevelFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (!KnownTopLevelFields.Contains(prop.Name))
            {
                _logger.LogWarning("Unknown settings field '{Name}' will be ignored.", prop.Name);
            }
        }
    }

    private AppSettings Validate(AppSettings s)
    {
        // Per TECH_SPEC §9.4: invalid fields are reset to default + logged. Only
        // HoldThresholdMs is called out explicitly; extend this method when the
        // spec calls out more.
        if (s.Hotkey.HoldThresholdMs <= 0)
        {
            _logger.LogWarning(
                "Invalid HoldThresholdMs={Ms}; resetting that field to default.",
                s.Hotkey.HoldThresholdMs);
            s = s with { Hotkey = s.Hotkey with { HoldThresholdMs = 250 } };
        }

        return s;
    }

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

}
