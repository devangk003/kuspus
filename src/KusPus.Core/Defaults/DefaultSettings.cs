using KusPus.Core.Settings;

namespace KusPus.Core.Defaults;

/// <summary>
/// Source of truth for first-run settings and migration backfill.
/// Per TECH_SPEC §9.4 — referenced by name from PrefsStore.
/// </summary>
public static class DefaultSettings
{
    /// <summary>A freshly defaulted <see cref="AppSettings"/> instance.</summary>
    public static AppSettings ForFirstRun() => new();
}
