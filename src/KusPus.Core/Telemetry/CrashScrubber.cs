using System.Diagnostics.CodeAnalysis;

namespace KusPus.Core.Telemetry;

/// <summary>
/// Pure helpers for the Sentry <c>BeforeSend</c> hook per TECH_SPEC §19 +
/// PRD §10.3. Kept in <c>KusPus.Core</c> so the scrubbing rules are exercisable
/// in tests without pulling the Sentry SDK into the test surface.
///
/// Three responsibilities:
/// <list type="bullet">
///   <item><see cref="IsSensitiveKey"/> — does a payload key match the
///   transcript / clipboard / target-app denylist? If yes, the event is dropped
///   wholesale (per spec: "Drops every event whose payload contains keys from a
///   denylist").</item>
///   <item><see cref="ScrubPath"/> — replace user-profile prefixes
///   (<c>%USERPROFILE%</c>, <c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c>,
///   <c>%TEMP%</c>) with literal env-var placeholders so a stack-trace path like
///   <c>C:\Users\alice\AppData\Local\KusPus\logs\…</c> becomes
///   <c>%LOCALAPPDATA%\KusPus\logs\…</c>.</item>
///   <item><see cref="ScrubString"/> — combines path-prefix scrubbing with
///   <c>Environment.UserName</c> redaction (replaced with literal <c>&lt;user&gt;</c>),
///   for free-form strings such as exception messages and breadcrumb data where
///   the username can appear outside a path context.</item>
/// </list>
/// </summary>
public static class CrashScrubber
{
    private const string UserNameReplacement = "<user>";

    private static readonly string UserName = Environment.UserName;
    // Keys whose presence anywhere in a payload causes the event to be dropped.
    // Ordering / case: matched case-insensitively, exact-key only — substring
    // matches would catch innocent keys like "context_target_app_role".
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "transcript",
        "clipboard",
        "password",
        "target_app",
        "hwnd",
    };

    public static bool IsSensitiveKey(string key) =>
        !string.IsNullOrEmpty(key) && SensitiveKeys.Contains(key);

    public static bool ContainsAnySensitiveKey(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var k in keys)
        {
            if (IsSensitiveKey(k))
            {
                return true;
            }
        }
        return false;
    }

    // Prefix → env-var token, ordered most-specific first. Windows nests these:
    //   TEMP          = LOCALAPPDATA\Temp
    //   LOCALAPPDATA  = USERPROFILE\AppData\Local
    //   APPDATA       = USERPROFILE\AppData\Roaming
    // so the deeper path must win, otherwise %LOCALAPPDATA% would eat %TEMP% paths.
    private static readonly (string Prefix, string Token)[] PathPrefixes = BuildPathPrefixes();

    private static (string, string)[] BuildPathPrefixes()
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            (temp, "%TEMP%"),
            (local, "%LOCALAPPDATA%"),
            (roaming, "%APPDATA%"),
            (profile, "%USERPROFILE%"),
        ];
    }

    /// <summary>
    /// Start-anchored prefix replacement for fields whose value <em>is</em> a path
    /// (stack-frame <c>AbsolutePath</c>, <c>FileName</c>). Use <see cref="ScrubString"/>
    /// instead for free-form text where the path is embedded in a sentence.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? ScrubPath(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        foreach (var (prefix, token) in PathPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix) &&
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return token + value[prefix.Length..];
            }
        }
        return value;
    }

    /// <summary>
    /// Strongest scrubbing pass for free-form strings (exception messages,
    /// breadcrumb data, <see cref="SentryEvent.Message"/>-like fields). Replaces
    /// every case-insensitive occurrence of each path prefix (anywhere in the
    /// string, not just at the start) and every occurrence of
    /// <see cref="Environment.UserName"/> per PRD §10.3.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? ScrubString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        var scrubbed = value;
        foreach (var (prefix, token) in PathPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                scrubbed = ReplaceCaseInsensitive(scrubbed, prefix, token);
            }
        }
        if (!string.IsNullOrEmpty(UserName) && UserName.Length > 1)
        {
            // Length > 1 guard: a 1-char username would match too aggressively
            // ("A" inside any English sentence). Vanishingly rare on Windows.
            scrubbed = ReplaceCaseInsensitive(scrubbed, UserName, UserNameReplacement);
        }
        return scrubbed;
    }

    private static string ReplaceCaseInsensitive(string input, string find, string replacement)
    {
        int idx = input.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return input;
        }
        var sb = new System.Text.StringBuilder(input.Length);
        int cursor = 0;
        while (idx >= 0)
        {
            sb.Append(input, cursor, idx - cursor);
            sb.Append(replacement);
            cursor = idx + find.Length;
            idx = input.IndexOf(find, cursor, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(input, cursor, input.Length - cursor);
        return sb.ToString();
    }
}
