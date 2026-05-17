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
    /// Expected SHA-256 of <c>whisper.exe</c>. Empty string ⇒ integrity check
    /// is a no-op (dev mode). Resolution order:
    /// <list type="number">
    ///   <item><c>KUSPUS_WHISPER_SHA256</c> env-var override (debug-only escape hatch).</item>
    ///   <item><c>BuildConstants.ExpectedWhisperSha256</c> embedded at build time
    ///         by the <c>EmitWhisperShaConstant</c> MSBuild target in
    ///         <c>KusPus.App.csproj</c> — reads <c>installer/payload/whisper/SHA256SUMS</c>
    ///         and emits the SHA into a generated <c>WhisperSha.g.cs</c>.</item>
    ///   <item>Empty string fallback (dev / unit-test hosts).</item>
    /// </list>
    /// Release builds run <c>tools/build-whisper-windows.ps1</c> first, which
    /// populates <c>SHA256SUMS</c>, so the build constant carries the real hash.
    /// </summary>
    public static string ExpectedWhisperSha256
    {
        get
        {
            var envOverride = Environment.GetEnvironmentVariable("KUSPUS_WHISPER_SHA256");
            if (!string.IsNullOrEmpty(envOverride))
            {
                return envOverride;
            }
            return BuildConstants.ExpectedWhisperSha256;
        }
    }

    /// <summary>
    /// Where intermediate <c>.wav</c> files live during recording. Defaults to the
    /// system <c>%TEMP%</c>; override with <c>KUSPUS_TEMP_DIR</c> when the system
    /// temp drive is space-constrained.
    /// </summary>
    public static string TempDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("KUSPUS_TEMP_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return Path.GetTempPath();
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
