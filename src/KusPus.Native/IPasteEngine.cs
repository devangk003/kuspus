using KusPus.Core;

namespace KusPus.Native;

/// <summary>
/// Captures the foreground HWND at chord-engage, then delivers the transcript via
/// Clipboard + SendInput Ctrl+V to that same window. See TECH_SPEC §16.
/// </summary>
public interface IPasteEngine
{
    /// <summary>Snapshot the current foreground window. Call at chord-engage time (T0).</summary>
    Result<IntPtr> CaptureForegroundHwnd();

    /// <summary>
    /// Set the clipboard to <paramref name="text"/>, restore focus to <paramref name="targetHwnd"/>,
    /// and synthesise Ctrl+V (or Ctrl+Shift+V for known terminals).
    /// </summary>
    Task<PasteOutcome> DeliverAsync(string text, IntPtr targetHwnd, CancellationToken ct = default);
}

/// <summary>Result of <see cref="IPasteEngine.DeliverAsync"/>.</summary>
public sealed record PasteOutcome(bool Pasted, string TargetApp, string? Error);

/// <summary>
/// Indirection over Win32 / WPF / WinForms clipboard APIs so <see cref="PasteEngine"/>
/// can stay free of UI-framework dependencies. KusPus.App provides the WPF
/// implementation in Phase 6.
/// </summary>
public interface IClipboardWriter
{
    /// <summary>Set the clipboard to plain text. Retries on transient access-denied per §16.</summary>
    void SetText(string text);
}
