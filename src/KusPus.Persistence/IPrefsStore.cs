using KusPus.Core.Settings;

namespace KusPus.Persistence;

/// <summary>
/// Reads, writes, and watches <c>%APPDATA%\KusPus\settings.json</c>.
/// See TECH_SPEC §20.
/// </summary>
public interface IPrefsStore
{
    /// <summary>The currently-loaded settings snapshot.</summary>
    AppSettings Current { get; }

    /// <summary>
    /// Emits whenever <see cref="Current"/> changes (via <see cref="SaveAsync"/>
    /// or <see cref="ReloadFromDiskAsync"/>). Replays the current value to new
    /// subscribers — implemented with a <c>BehaviorSubject</c>.
    /// </summary>
    IObservable<AppSettings> Changes { get; }

    /// <summary>Atomically write the given settings and update <see cref="Current"/>.</summary>
    Task SaveAsync(AppSettings updated, CancellationToken ct = default);

    /// <summary>Re-read settings.json from disk. Used by the FileSystemWatcher (Cluster 2B).</summary>
    Task ReloadFromDiskAsync(CancellationToken ct = default);
}
