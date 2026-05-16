using System.IO;

namespace KusPus.App;

/// <summary>
/// Resolved on-disk locations per TECH_SPEC §7.2. <c>%APPDATA%\KusPus</c> for
/// roaming settings, <c>%LOCALAPPDATA%\KusPus</c> for per-machine state.
/// </summary>
internal static class AppPaths
{
    public static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KusPus");

    public static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static readonly string LocalDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KusPus");

    public static readonly string ModelsDir = Path.Combine(LocalDataDir, "models");

    public static readonly string LogsDir = Path.Combine(LocalDataDir, "logs");

    public static readonly string FailedDir = Path.Combine(LocalDataDir, "failed");

    public static readonly string HistoryDbPath = Path.Combine(LocalDataDir, "history.db");

    /// <summary>
    /// Where whisper.exe + DLLs live. Per TECH_SPEC §7.2 this is alongside the app exe;
    /// developers running from <c>dotnet run</c> can override by setting
    /// <c>KUSPUS_WHISPER_DIR</c> in the environment.
    /// </summary>
    public static string WhisperDir =>
        Environment.GetEnvironmentVariable("KUSPUS_WHISPER_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "whisper");

    /// <summary>
    /// Optional expected SHA-256 of <c>whisper.exe</c>. Empty/null skips the integrity
    /// check (dev mode). Phase 12 release builds will set this from <c>SHA256SUMS</c>.
    /// </summary>
    public static string ExpectedWhisperSha256 =>
        Environment.GetEnvironmentVariable("KUSPUS_WHISPER_SHA256") ?? string.Empty;
}
