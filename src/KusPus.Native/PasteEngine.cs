#pragma warning disable CA1848
#pragma warning disable CA1873

using System.ComponentModel;
using System.Runtime.InteropServices;
using KusPus.Core;
using KusPus.Native.PInvoke;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreVK = KusPus.Core.Hotkeys.VirtualKey;

namespace KusPus.Native;

/// <summary>
/// Win32 implementation of <see cref="IPasteEngine"/>. See TECH_SPEC §16.
///
/// <list type="bullet">
/// <item>Foreground capture at engage time is a simple <c>GetForegroundWindow</c>.</item>
/// <item>Clipboard.SetText is delegated to <see cref="IClipboardWriter"/> (Phase 6
/// provides the WPF impl) so this class stays free of UI-framework deps.</item>
/// <item>Foreground restore uses <c>AttachThreadInput</c> + <c>SetForegroundWindow</c>
/// with a defensive <c>AllowSetForegroundWindow(ASFW_ANY)</c> retry — the spec's
/// fix for the "shell briefly locks foreground" production bug.</item>
/// <item>Paste keystroke is <c>Ctrl+V</c> for ordinary apps and <c>Ctrl+Shift+V</c>
/// for known terminal processes (Windows Terminal, cmd, powershell, pwsh, ConEmu,
/// Alacritty, WezTerm) per §16.</item>
/// <item>Friendly app name resolved via <c>GetWindowThreadProcessId</c> →
/// <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c> →
/// <c>K32GetModuleFileNameExW</c>, then mapped through a small static dictionary.</item>
/// </list>
/// </summary>
public sealed class PasteEngine : IPasteEngine
{
    private static readonly HashSet<string> TerminalProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "WindowsTerminal.exe",
            "OpenConsole.exe",
            "cmd.exe",
            "powershell.exe",
            "pwsh.exe",
            "ConEmu64.exe",
            "ConEmuC64.exe",
            "Cmder.exe",
            "alacritty.exe",
            "wezterm-gui.exe",
        };

    private static readonly Dictionary<string, string> FriendlyAppNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "slack.exe", "Slack" },
            { "discord.exe", "Discord" },
            { "chrome.exe", "Chrome" },
            { "firefox.exe", "Firefox" },
            { "msedge.exe", "Edge" },
            { "Code.exe", "VS Code" },
            { "devenv.exe", "Visual Studio" },
            { "notepad.exe", "Notepad" },
            { "WindowsTerminal.exe", "Windows Terminal" },
            { "cmd.exe", "Command Prompt" },
            { "powershell.exe", "PowerShell" },
            { "pwsh.exe", "PowerShell" },
            { "explorer.exe", "File Explorer" },
        };

    private readonly IClipboardWriter _clipboard;
    private readonly ILogger<PasteEngine> _logger;

    public PasteEngine(IClipboardWriter clipboard, ILogger<PasteEngine>? logger = null)
    {
        _clipboard = clipboard;
        _logger = logger ?? NullLogger<PasteEngine>.Instance;
    }

    public Result<IntPtr> CaptureForegroundHwnd()
    {
        var hwnd = User32.GetForegroundWindow();
        return hwnd == IntPtr.Zero
            ? Result.Fail<IntPtr>("GetForegroundWindow returned 0 — no foreground window.")
            : Result.Ok(hwnd);
    }

    public async Task<PasteOutcome> DeliverAsync(string text, IntPtr targetHwnd, CancellationToken ct = default)
    {
        // §16 step 1: retry up to 3× on transient access-denied (another app holding
        // the clipboard briefly is common). 50 ms between attempts.
        const int maxAttempts = 3;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                _clipboard.SetText(text);
                break;
            }
            catch (ExternalException ex) when (attempt < maxAttempts - 1)
            {
                _logger.LogDebug(ex,
                    "Clipboard.SetText attempt {Attempt}/{Max} failed; retrying.",
                    attempt + 1, maxAttempts);
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (ExternalException ex)
            {
                _logger.LogWarning(ex, "Clipboard.SetText failed after {Max} attempts.", maxAttempts);
                return new PasteOutcome(
                    Pasted: false, TargetApp: "?",
                    Error: $"Clipboard failed: {ex.Message}");
            }
        }

        var targetApp = ResolveTargetApp(targetHwnd, out var processFileName);

        if (!RestoreForeground(targetHwnd))
        {
            _logger.LogWarning("Foreground restore failed for HWND {Hwnd}.", targetHwnd);
            return new PasteOutcome(Pasted: false, TargetApp: targetApp, Error: "Window gone — text in clipboard");
        }

        bool useShift = processFileName is not null
            && TerminalProcessNames.Contains(processFileName);
        SendPasteKeystroke(useShift);

        await Task.Delay(50, ct).ConfigureAwait(false);

        if (User32.GetForegroundWindow() != targetHwnd)
        {
            _logger.LogWarning("Foreground was lost mid-paste for HWND {Hwnd}.", targetHwnd);
            return new PasteOutcome(Pasted: false, TargetApp: targetApp, Error: "Foreground lost");
        }

        return new PasteOutcome(Pasted: true, TargetApp: targetApp, Error: null);
    }

    // ── internal helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the friendly app name (or the bare executable filename without extension)
    /// for the given window. <paramref name="processFileName"/> is the raw <c>foo.exe</c>
    /// — caller uses it for the terminal-detection check.
    /// </summary>
    internal static string ResolveTargetApp(IntPtr hwnd, out string? processFileName)
    {
        processFileName = null;
        if (hwnd == IntPtr.Zero)
        {
            return "Unknown";
        }

        _ = User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0)
        {
            return "Unknown";
        }

        var handle = Kernel32.OpenProcess(NativeConstants.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return "Unknown";
        }

        try
        {
            var buf = new char[1024];
            uint len = Kernel32.K32GetModuleFileNameExW(handle, IntPtr.Zero, buf, (uint)buf.Length);
            if (len == 0)
            {
                return "Unknown";
            }

            var fullPath = new string(buf, 0, (int)len);
            processFileName = Path.GetFileName(fullPath);
            return ResolveFriendlyName(processFileName);
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    internal static string ResolveFriendlyName(string processFileName)
    {
        if (FriendlyAppNames.TryGetValue(processFileName, out var friendly))
        {
            return friendly;
        }
        return Path.GetFileNameWithoutExtension(processFileName);
    }

    internal static bool IsTerminal(string processFileName) =>
        TerminalProcessNames.Contains(processFileName);

    private static bool RestoreForeground(IntPtr targetHwnd)
    {
        if (User32.GetForegroundWindow() == targetHwnd)
        {
            return true;
        }

        if (TryAttachAndForeground(targetHwnd))
        {
            return true;
        }

        // Defensive retry — some shell ops lock SetForegroundWindow briefly.
        User32.AllowSetForegroundWindow(NativeConstants.ASFW_ANY);
        return TryAttachAndForeground(targetHwnd);
    }

    private static bool TryAttachAndForeground(IntPtr targetHwnd)
    {
        uint currentTid = Kernel32.GetCurrentThreadId();
        uint targetTid = User32.GetWindowThreadProcessId(targetHwnd, out _);
        if (targetTid == 0)
        {
            return false;
        }

        if (currentTid == targetTid)
        {
            return User32.SetForegroundWindow(targetHwnd);
        }

        User32.AttachThreadInput(currentTid, targetTid, true);
        try
        {
            return User32.SetForegroundWindow(targetHwnd);
        }
        finally
        {
            User32.AttachThreadInput(currentTid, targetTid, false);
        }
    }

    private static void SendPasteKeystroke(bool useShift)
    {
        // Single SendInput call so Windows doesn't interleave other input mid-paste.
        var inputs = useShift
            ? BuildKeySequence((ushort)CoreVK.Control, (ushort)CoreVK.LeftShift, (ushort)CoreVK.V)
            : BuildKeySequence((ushort)CoreVK.Control, (ushort)CoreVK.V);
        User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Build a down-down-down...-up-up-up SendInput payload.</summary>
    internal static INPUT[] BuildKeySequence(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i] = new INPUT
            {
                Type = NativeConstants.INPUT_KEYBOARD,
                Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = keys[i] } },
            };
        }
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[keys.Length + i] = new INPUT
            {
                Type = NativeConstants.INPUT_KEYBOARD,
                Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = keys[keys.Length - 1 - i], Flags = NativeConstants.KEYEVENTF_KEYUP } },
            };
        }
        return inputs;
    }
}
