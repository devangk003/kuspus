using Foundation;

namespace KusPus.Mac.App;

/// <summary>
/// macOS counterpart of the WPF app's <c>AppPaths</c>. Follows the Apple file-system
/// guide: user data under <c>~/Library/Application Support/KusPus</c>, logs under
/// <c>~/Library/Logs/KusPus</c>, the whisper binary + bundled model inside the signed
/// <c>.app</c> (read-only), downloaded models in Application Support (writable).
/// The same <c>KUSPUS_*</c> environment overrides as Windows apply.
/// </summary>
internal static class MacPaths
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static readonly string AppSupportDir = Path.Combine(Home, "Library", "Application Support", "KusPus");
    public static readonly string SettingsPath = Path.Combine(AppSupportDir, "settings.json");
    public static readonly string HistoryDbPath = Path.Combine(AppSupportDir, "history.db");
    public static readonly string FailedDir = Path.Combine(AppSupportDir, "failed");
    public static readonly string LogsDir = Path.Combine(Home, "Library", "Logs", "KusPus");

    /// <summary>Where whisper-cli + its dylibs live: <c>Contents/Resources/whisper/</c>.</summary>
    public static string WhisperDir =>
#if DEBUG
        // Dev override only: in a release build, an environment variable must not be able
        // to point KusPus at a different whisper binary.
        Environment.GetEnvironmentVariable("KUSPUS_WHISPER_DIR") ??
#endif
        Path.Combine(NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory, "whisper");

    /// <summary>Read-only bundled models (tiny.en ships here). Never written to.</summary>
    public static string BundledModelsDir => Path.Combine(WhisperDir, "models");

    /// <summary>Writable models dir for downloads.</summary>
    public static string ModelsDir =>
        Environment.GetEnvironmentVariable("KUSPUS_MODELS_DIR")
        ?? Path.Combine(AppSupportDir, "models");

    /// <summary>
    /// Expected SHA-256 of <c>whisper-cli</c>. Resolution order:
    /// <list type="number">
    ///   <item><c>KUSPUS_WHISPER_SHA256</c> env override (Debug builds only).</item>
    ///   <item>The <c>SHA256SUMS</c> shipped next to the binary. Code signing rewrites
    ///   <c>whisper-cli</c> after compilation, so a compile-time constant can never match
    ///   a signed build; the packaging script regenerates this manifest after signing the
    ///   nested binaries, and the bundle signature then seals it.</item>
    ///   <item><c>BuildConstants.ExpectedWhisperSha256</c> (unsigned dev builds).</item>
    /// </list>
    /// </summary>
    public static string ExpectedWhisperSha256
    {
        get
        {
#if DEBUG
            var envOverride = Environment.GetEnvironmentVariable("KUSPUS_WHISPER_SHA256");
            if (!string.IsNullOrEmpty(envOverride))
            {
                return envOverride;
            }
#endif
            var sealed_ = KusPus.Whisper.Sha256Sums.FindHash(Path.Combine(WhisperDir, "SHA256SUMS"), KusPus.Whisper.WhisperRunner.MacExeName);
            return sealed_ ?? BuildConstants.ExpectedWhisperSha256;
        }
    }

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
