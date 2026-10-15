using System.Runtime.InteropServices;
using AppKit;
using AVFoundation;
using Foundation;

namespace KusPus.Mac.Platform;

/// <summary>
/// The three TCC grants KusPus needs, and deep links to their System Settings panes.
/// <list type="bullet">
/// <item><b>Microphone</b> — AVFoundation prompts on first capture.</item>
/// <item><b>Input Monitoring</b> (<c>kTCCServiceListenEvent</c>) — required for the
/// listen-only <c>CGEventTap</c> hotkey.</item>
/// <item><b>Accessibility</b> (<c>kTCCServicePostEvent</c>) — required to post the
/// synthetic ⌘V. Without it KusPus degrades to clipboard-only delivery.</item>
/// </list>
/// Grants are tied to the app's code signature; an ad-hoc-signed dev build loses
/// them on every rebuild.
/// </summary>
public static class Permissions
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    public const string MicrophoneSettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";
    public const string InputMonitoringSettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent";
    public const string AccessibilitySettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightListenEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRequestListenEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGPreflightPostEventAccess();

    [DllImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGRequestPostEventAccess();

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    // ── microphone ───────────────────────────────────────────────────────────

    public static AVAuthorizationStatus MicrophoneStatus() =>
        AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Audio);

    public static bool HasMicrophone() => MicrophoneStatus() == AVAuthorizationStatus.Authorized;

    /// <summary>Shows the system prompt if status is NotDetermined; otherwise returns the current grant.</summary>
    public static Task<bool> RequestMicrophoneAsync() =>
        AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Audio);

    // ── input monitoring ─────────────────────────────────────────────────────

    public static bool HasInputMonitoring() => CGPreflightListenEventAccess();

    /// <summary>Prompts (once per app identity) and adds KusPus to the Input Monitoring list.</summary>
    public static bool RequestInputMonitoring() => CGRequestListenEventAccess();

    // ── accessibility (post events) ──────────────────────────────────────────

    public static bool HasAccessibility() => CGPreflightPostEventAccess() || AXIsProcessTrustedWithOptions(IntPtr.Zero);

    /// <summary>Prompts and adds KusPus to the Accessibility list (user still has to tick it).</summary>
    public static bool RequestAccessibility()
    {
        using var options = NSDictionary.FromObjectAndKey(NSNumber.FromBoolean(true), new NSString("AXTrustedCheckOptionPrompt"));
        _ = AXIsProcessTrustedWithOptions(options.Handle);
        return CGRequestPostEventAccess();
    }

    // ── settings deep links ──────────────────────────────────────────────────

    public static void OpenSettings(string url)
    {
        using var nsUrl = new NSUrl(url);
        NSWorkspace.SharedWorkspace.OpenUrl(nsUrl);
    }
}
