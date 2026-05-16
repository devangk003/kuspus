using Microsoft.Win32;

namespace KusPus.App;

/// <summary>
/// HKCU\Run autostart toggle per APP_DESIGN §3.3 Tab 1 "Startup" + §8.5. Writes
/// the current-user Run key; no admin elevation required. Defaults to OFF and is
/// fully reversible — no other registry surface touched.
///
/// Why HKCU\Run (and not the Startup folder or Task Scheduler):
/// - Survives roaming profiles.
/// - Trivially inspectable by the user (Settings → Apps → Startup).
/// - Doesn't depend on a Task Scheduler service.
/// </summary>
internal static class AutostartRegistry
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KusPus";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    /// <summary>
    /// Writes or removes the autostart entry. The command is the current process
    /// executable quoted in case its path contains spaces.
    /// </summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
        {
            return;
        }
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? AppDomain.CurrentDomain.FriendlyName;
            // Quote the path so spaces in user-profile dirs don't break the cmd parser.
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
