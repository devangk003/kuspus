using Foundation;
using ServiceManagement;

namespace KusPus.Mac.Platform;

/// <summary>
/// Launch-at-login via <c>SMAppService.mainApp</c> (macOS 13+). Shows up under
/// System Settings → General → Login Items, is per-user, needs no admin, and is
/// fully reversible — the counterpart of the Windows HKCU\Run entry.
/// </summary>
public static class MacAutostart
{
    /// <summary>
    /// True when registered — including <c>RequiresApproval</c> (registered, waiting for the
    /// user to allow it in Login Items). Reporting that as "off" made the toggle lie and
    /// turned the user's "off" click into an Unregister error.
    /// </summary>
    public static bool IsEnabled() => SMAppService.MainApp.Status
        is SMAppServiceStatus.Enabled or SMAppServiceStatus.RequiresApproval;

    /// <summary>Registers or unregisters the login item. Returns an error message, or null on success.</summary>
    public static string? Set(bool enabled)
    {
        var svc = SMAppService.MainApp;
        if (!enabled && svc.Status == SMAppServiceStatus.NotRegistered)
        {
            return null; // already off; Unregister would report an error
        }
        NSError? error;
        bool ok = enabled ? svc.Register(out error) : svc.Unregister(out error);
        return ok ? null : error?.LocalizedDescription ?? "SMAppService failed.";
    }
}
