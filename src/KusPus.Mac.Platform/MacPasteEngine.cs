#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Runtime.InteropServices;
using AppKit;
using CoreGraphics;
using KusPus.Core;
using KusPus.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.Platform;

/// <summary>
/// macOS <see cref="IPasteEngine"/>. The opaque "foreground handle" is the frontmost
/// application's <b>pid</b> (there is no cross-process window handle on macOS).
///
/// <list type="bullet">
/// <item>Capture: <c>NSWorkspace.frontmostApplication.processIdentifier</c>.</item>
/// <item>Restore: <c>NSRunningApplication.activate</c>, then poll up to 300 ms for it
/// to actually become frontmost (activation is asynchronous).</item>
/// <item>Keystroke: ⌘V via two <c>CGEvent</c>s posted to the HID tap. Terminals on macOS
/// also paste with ⌘V, so there is no Shift variant.</item>
/// <item>Accessibility (PostEvent) not granted → text stays on the clipboard and the
/// outcome says so, mirroring the Windows "Window gone — text in clipboard" path.</item>
/// </list>
/// </summary>
public sealed class MacPasteEngine : IPasteEngine
{
    public const string NeedsAccessibilityMessage = "Text copied — grant Accessibility to auto-paste";

    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphicsLib)]
    private static extern void CGEventPost(CGEventTapLocation tap, IntPtr @event);

    private readonly IClipboardWriter _clipboard;
    private readonly ILogger<MacPasteEngine> _logger;

    public MacPasteEngine(IClipboardWriter clipboard, ILogger<MacPasteEngine>? logger = null)
    {
        _clipboard = clipboard;
        _logger = logger ?? NullLogger<MacPasteEngine>.Instance;
    }

    public Result<IntPtr> CaptureForegroundHwnd()
    {
        var app = NSWorkspace.SharedWorkspace.FrontmostApplication;
        if (app is null)
        {
            return Result.Fail<IntPtr>("No frontmost application.");
        }
        // Don't target ourselves (pill/menu interaction can briefly make KusPus frontmost).
        if (app.ProcessIdentifier == Environment.ProcessId)
        {
            return Result.Fail<IntPtr>("KusPus itself is frontmost.");
        }
        return Result.Ok(new IntPtr(app.ProcessIdentifier));
    }

    public async Task<PasteOutcome> DeliverAsync(string text, IntPtr targetHwnd, CancellationToken ct = default)
    {
        // Mirror the Windows engine's 3× retry; NSPasteboard rarely fails but can
        // throw if another app has it open for writing.
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                _clipboard.SetText(text);
                break;
            }
            catch (Exception ex) when (ex is ObjCRuntime.ObjCException or InvalidOperationException)
            {
                if (attempt == maxAttempts - 1)
                {
                    _logger.LogWarning(ex, "Pasteboard write failed after {Max} attempts.", maxAttempts);
                    return new PasteOutcome(false, "?", $"Clipboard failed: {ex.Message}");
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        // Everything below touches AppKit (NSWorkspace / NSRunningApplication), which the
        // binding restricts to the main thread — and this method is typically continuing
        // on a thread-pool thread by now (the coordinator awaits with ConfigureAwait(false)).
        var pid = targetHwnd == IntPtr.Zero ? 0 : targetHwnd.ToInt32();
        var (target, targetApp) = await MainThread.RunAsync(() =>
        {
            var app = pid > 0 ? NSRunningApplication.GetRunningApplication(pid) : null;
            return (app, app?.LocalizedName ?? "Unknown");
        }).ConfigureAwait(false);

        if (target is null)
        {
            _logger.LogWarning("Target pid {Pid} is gone.", pid);
            return new PasteOutcome(false, targetApp, "Window gone — text in clipboard");
        }

        if (!Permissions.HasAccessibility())
        {
            _logger.LogWarning("Accessibility not granted; leaving transcript on the pasteboard.");
            return new PasteOutcome(false, targetApp, NeedsAccessibilityMessage);
        }

        if (!await RestoreForegroundAsync(target, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Could not re-activate {App} (pid {Pid}).", targetApp, pid);
            return new PasteOutcome(false, targetApp, "Window gone — text in clipboard");
        }

        // Layout lookup uses Text Input Sources, which are main-thread only.
        var vKey = await MainThread.RunAsync(() => MacKeyboardLayout.KeyCodeFor('v', MacKeyCodes.V)).ConfigureAwait(false);
        PostCommandV(vKey);
        await Task.Delay(50, ct).ConfigureAwait(false);

        if (await FrontmostPidAsync().ConfigureAwait(false) != pid)
        {
            _logger.LogWarning("Foreground was lost mid-paste for pid {Pid}.", pid);
            return new PasteOutcome(false, targetApp, "Foreground lost");
        }

        return new PasteOutcome(true, targetApp, null);
    }

    private static Task<int> FrontmostPidAsync() =>
        MainThread.RunAsync(() => NSWorkspace.SharedWorkspace.FrontmostApplication?.ProcessIdentifier ?? -1);

    private static async Task<bool> RestoreForegroundAsync(NSRunningApplication target, CancellationToken ct)
    {
        int pid = target.ProcessIdentifier;
        if (await FrontmostPidAsync().ConfigureAwait(false) == pid)
        {
            return true;
        }

        await MainThread.RunAsync(() => target.Activate(NSApplicationActivationOptions.ActivateIgnoringOtherWindows)).ConfigureAwait(false);

        // Activation is async; give the WindowServer up to ~300 ms.
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
            if (await FrontmostPidAsync().ConfigureAwait(false) == pid)
            {
                return true;
            }
        }
        return false;
    }

    private static void PostCommandV(ushort vKey)
    {
        using var down = new CGEvent(null, vKey, keyDown: true) { Flags = CGEventFlags.Command };
        using var up = new CGEvent(null, vKey, keyDown: false) { Flags = CGEventFlags.Command };
        CGEventPost(CGEventTapLocation.HID, down.Handle);
        CGEventPost(CGEventTapLocation.HID, up.Handle);
    }
}
