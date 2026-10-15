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

    /// <summary>
    /// Atomic read-modify-write: <paramref name="mutate"/> runs against the latest saved
    /// settings while holding the save lock, so concurrent updates from different
    /// windows can't overwrite each other's fields (unlike <c>Current with {…}</c> +
    /// <see cref="SaveAsync"/>, which reads a snapshot that may be stale by the time it
    /// is written). Returns the settings that were saved.
    /// </summary>
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        // Fallback for simple test fakes; PrefsStore overrides with a locked version.
        var next = mutate(Current);
        return SaveAsync(next, ct).ContinueWith(_ => next, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Re-read settings.json from disk. Used by the FileSystemWatcher (Cluster 2B).</summary>
    Task ReloadFromDiskAsync(CancellationToken ct = default);
}
