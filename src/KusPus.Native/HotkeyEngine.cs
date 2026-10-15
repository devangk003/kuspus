// HotkeyEngine logs at start/stop, on LL-hook install failure, and on rare
// LWin-suppression injections. Not a hot path — same suppression rationale as
// the other layers.
#pragma warning disable CA1848
#pragma warning disable CA1873

using System.ComponentModel;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KusPus.Core.Hotkeys;
using KusPus.Native.PInvoke;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreVK = KusPus.Core.Hotkeys.VirtualKey;

using KusPus.Core.Platform;

namespace KusPus.Native;

/// <summary>
/// WH_KEYBOARD_LL implementation of <see cref="IHotkeyEngine"/>. See TECH_SPEC §13.
///
/// Threading: a dedicated hook thread runs <c>GetMessageW</c>. The LL callback runs
/// on that thread, does its work under <see cref="_stateLock"/>, BUILDS a list of
/// events to emit, releases the lock, then emits them to <see cref="_events"/>.
/// This avoids running subscriber callbacks under the hook-thread lock and
/// matches §13's "callback must return in &lt; 1 ms" requirement.
///
/// Spec §13 step 6 prescribes a <c>Channel&lt;HookEvent&gt;</c> indirection between
/// the callback and the subject. Deferred — at v1's input rate the direct subject
/// emission (after lock release) is well within the LowLevelHooksTimeout budget.
/// See CLAUDE.md deviation log.
///
/// LWin-suppression: when the user releases LWin while we own the chord, Windows
/// would normally open the Start menu. We inject a stray Ctrl tap right before the
/// LWin keyup so the OS sees "other input between LWin↓ and LWin↑" and skips the
/// Start menu — this is AutoHotkey's <c>#MenuMaskKey</c> idiom. We do NOT consume
/// the LWin keyup itself: the OS must see it so its internal "Win is held" state
/// clears. Consuming it (the original §13 phrasing) left Win stuck-down, which made
/// subsequent <c>SendInput(Ctrl+V)</c> read as <c>Win+Ctrl+V</c> (opens Action
/// Center / Clipboard History) and turned every later keystroke into a Win+key
/// system shortcut.
///
/// Hook self-heal watchdog (TECH_SPEC §13) is NOT implemented in Phase 5. Deferred.
/// </summary>
public sealed class HotkeyEngine : IHotkeyEngine, IDisposable
{
    // Static so GC can never collect the delegate while the OS holds a function
    // pointer to it. Captures `this` via the instance HookCallback method, which
    // also keeps the engine instance reachable. One HotkeyEngine per process —
    // documented in the class summary.
    private static User32.LowLevelKeyboardProc? s_pinnedCallback;

    private readonly ILogger<HotkeyEngine> _logger;
    private readonly Subject<HotkeyEvent> _events = new();
    private readonly object _stateLock = new();

    private IntPtr _hookHandle;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private volatile bool _shutdownRequested;

    // Chord detection itself is platform-neutral and shared with the macOS engine.
    private readonly ChordTracker _tracker = new();

    public HotkeyEngine(ILogger<HotkeyEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<HotkeyEngine>.Instance;
    }

    public IObservable<HotkeyEvent> Events => _events;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_hookThread is not null)
            {
                return;
            }
            _shutdownRequested = false;
            _hookThread = new Thread(HookThreadProc)
            {
                IsBackground = true,
                Name = "KusPus.HookThread",
            };
            _hookThread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint tid;
        lock (_stateLock)
        {
            thread = _hookThread;
            tid = _hookThreadId;
            _shutdownRequested = true;
            _hookThread = null;
        }

        if (thread is not null)
        {
            // Break GetMessageW out of its blocking wait so the hook thread can exit
            // promptly — otherwise Stop() blocks for the 2s Join timeout every time
            // there's no incoming keyboard activity.
            if (tid != 0)
            {
                User32.PostThreadMessageW(tid, NativeConstants.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            thread.Join(TimeSpan.FromSeconds(2));
        }

        if (_hookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        s_pinnedCallback = null;
    }

    public void SetChord(HotkeyChord chord)
    {
        lock (_stateLock)
        {
            // ChordTracker.SetChord is idempotent: an unchanged chord preserves
            // held-modifier state so a runtime settings save doesn't wipe mid-chord
            // state and leave hold-mode stranded in 'engaged' forever.
            _tracker.SetChord(chord);
        }
    }

    public void Dispose()
    {
        Stop();
        _events.Dispose();
    }

    // ── hook thread ──────────────────────────────────────────────────────────

    private void HookThreadProc()
    {
        _hookThreadId = Kernel32.GetCurrentThreadId();
        s_pinnedCallback = HookCallback;

        try
        {
            _hookHandle = User32.SetWindowsHookExW(
                NativeConstants.WH_KEYBOARD_LL, s_pinnedCallback, IntPtr.Zero, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetWindowsHookExW(WH_KEYBOARD_LL) failed.");
            }
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Failed to install LL keyboard hook.");
            return;
        }

        _logger.LogInformation("LL keyboard hook installed on thread {Tid}.", _hookThreadId);

        // Hook-thread message pump. GetMessageW returns false on WM_QUIT.
        while (!_shutdownRequested)
        {
            if (!User32.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                break;
            }
            User32.TranslateMessage(in msg);
            User32.DispatchMessageW(in msg);
        }
    }

    // ── LL hook callback — MUST return in milliseconds ───────────────────────

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return User32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // Unsafe.Read is zero-alloc for blittable structs — matches §13's
        // "must not allocate in the hot path" requirement.
        KBDLLHOOKSTRUCT data;
        unsafe
        {
            data = Unsafe.Read<KBDLLHOOKSTRUCT>((void*)lParam);
        }

        bool isInjected = (data.Flags & NativeConstants.LLKHF_INJECTED) != 0;
        if (isInjected)
        {
            return User32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var msg = (uint)wParam.ToInt64();
        bool isKeyDown = msg == NativeConstants.WM_KEYDOWN || msg == NativeConstants.WM_SYSKEYDOWN;
        bool isKeyUp = msg == NativeConstants.WM_KEYUP || msg == NativeConstants.WM_SYSKEYUP;
        if (!isKeyDown && !isKeyUp)
        {
            return User32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var key = (CoreVK)data.VirtualKeyCode;
        bool consume = ProcessKey(key, isKeyDown);

        return consume
            ? new IntPtr(1)
            : User32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// Updates the modifier bitmap, detects chord engage/release, queues events,
    /// and signals whether the hook should consume the event (for LWin-suppression).
    /// Internal so unit tests exercise the state machine without an OS hook.
    /// </summary>
    internal bool ProcessKey(CoreVK key, bool isKeyDown)
    {
        bool isKeyUp = !isKeyDown;
        IReadOnlyList<HotkeyEvent> toEmit;
        bool consume = false;

        lock (_stateLock)
        {
            toEmit = _tracker.Step(key, isKeyDown);

            // LWin-suppression (menu-mask-key idiom, AHK #MenuMaskKey): if the
            // chord just disengaged because the user released a Windows key,
            // inject a stray Ctrl tap so Windows sees "other input between
            // LWin↓ and LWin↑" and skips the Start menu. The LWin keyup itself
            // is NOT consumed — the OS needs to see it so its internal "Win is
            // held" state clears. Otherwise Win stays stuck-down in OS state
            // and the next SendInput(Ctrl+V) reads as Win+Ctrl+V (opens Action
            // Center / Clipboard History) and every subsequent keystroke
            // becomes a Win+key system shortcut.
            if (isKeyUp
                && (key == CoreVK.LeftWin || key == CoreVK.RightWin)
                && toEmit.Any(e => e is ChordReleased))
            {
                InjectControlTap();
            }
        }

        // Emit OUTSIDE the lock so subscriber callbacks can't deadlock against
        // a re-entrant SetChord / Stop / etc.
        foreach (var evt in toEmit)
        {
            _events.OnNext(evt);
        }

        return consume;
    }

    private static void InjectControlTap()
    {
        var inputs = new INPUT[]
        {
            new() { Type = NativeConstants.INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = (ushort)CoreVK.Control } } },
            new() { Type = NativeConstants.INPUT_KEYBOARD, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = (ushort)CoreVK.Control, Flags = NativeConstants.KEYEVENTF_KEYUP } } },
        };
        User32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
