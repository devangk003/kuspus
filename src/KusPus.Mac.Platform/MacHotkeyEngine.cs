// Low-volume logging (start/stop, tap loss, permission state) — same CA1848/CA1873
// rationale as the Windows HotkeyEngine.
#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Reactive.Subjects;
using CoreFoundation;
using CoreGraphics;
using KusPus.Core.Hotkeys;
using KusPus.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObjCRuntime;

namespace KusPus.Mac.Platform;

/// <summary>
/// <c>CGEventTap</c> implementation of <see cref="IHotkeyEngine"/>.
///
/// <list type="bullet">
/// <item>A <b>listen-only</b> session tap, so only the <em>Input Monitoring</em> TCC
/// grant is required (not Accessibility). The Windows engine doesn't consume keys
/// either, so behaviour matches.</item>
/// <item>The tap runs on a dedicated <see cref="CFRunLoop"/> thread; the callback does
/// its work under <see cref="_stateLock"/> via the shared <see cref="ChordTracker"/>,
/// then emits events after releasing the lock.</item>
/// <item>Modifier keys arrive as <c>FlagsChanged</c>. On every such event the state of all
/// eight sided modifiers is re-derived from the device-dependent flag bits
/// (<see cref="MacKeyCodes.TryGetModifierFlagBit"/>) and only the ones that changed are
/// fed to the tracker — so a key-up swallowed by Secure Input (password fields) can't
/// leave a modifier stuck "down". Caps Lock/Fn are not chord modifiers: their flag
/// bits reflect lock state, not whether the key is held.</item>
/// <item>Self-heal: the OS disables a tap whose callback stalls
/// (<c>TapDisabledByTimeout</c>) and — after a re-sign or update — can leave a tap
/// installed but silently dead. We re-enable on the disable events AND run a
/// 5 s health check that re-enables or fully re-creates the tap. A re-creation
/// publishes <see cref="HookReinstalled"/> and resets chord state.</item>
/// <item>Permission: <c>CGPreflightListenEventAccess</c> is <em>not</em> trusted as the
/// gate — on current macOS it stays false for the running process after the user
/// grants Input Monitoring. Creating the tap is the real test, so the health check
/// simply retries <c>CGEventTapCreate</c> until it succeeds; the hotkey starts working
/// the moment the grant lands, without a relaunch where the OS allows it.</item>
/// </list>
/// Lifecycle (Start/Stop/health check/tap thread hand-off) is serialised by
/// <see cref="_lifecycleLock"/>; chord state by <see cref="_stateLock"/>.
/// </summary>
public sealed class MacHotkeyEngine : IHotkeyEngine, IDisposable
{
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(5);

    private static readonly ushort[] SidedModifierKeyCodes =
    [
        MacKeyCodes.Control, MacKeyCodes.RightControl,
        MacKeyCodes.Command, MacKeyCodes.RightCommand,
        MacKeyCodes.Option, MacKeyCodes.RightOption,
        MacKeyCodes.Shift, MacKeyCodes.RightShift,
    ];

    private readonly ILogger<MacHotkeyEngine> _logger;
    private readonly Subject<HotkeyEvent> _events = new();
    private readonly object _stateLock = new();
    private readonly object _lifecycleLock = new();
    // Serialises OnNext across the tap thread and the health-check timer (Subject is
    // not thread-safe) and orders it against Dispose.
    private readonly object _emitLock = new();
    private readonly ChordTracker _tracker = new();
    private readonly Dictionary<ushort, bool> _modifierDown = new();

    // Keep a strong reference: CoreGraphics holds only a function pointer.
    private readonly CGEvent.CGEventTapCallback _callback;

    private Thread? _tapThread;
    // Per-thread stop flag: the tap thread polls it, so a Stop() that lands before the
    // thread has published its port/run loop (or before Run started) can't be lost and
    // leave a zombie tap behind.
    private TapStop? _tapStop;
    private CFMachPort? _port;

    private sealed class TapStop
    {
        public volatile bool Requested;
    }
    private Timer? _healthTimer;
    private volatile bool _running;
    private volatile bool _disposed;
    private bool _tapWasAlive;
    private bool _loggedNoPermission;

    public MacHotkeyEngine(ILogger<MacHotkeyEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<MacHotkeyEngine>.Instance;
        _callback = OnTapEvent;
        foreach (var k in SidedModifierKeyCodes)
        {
            _modifierDown[k] = false;
        }
    }

    public IObservable<HotkeyEvent> Events => _events;

    /// <summary>
    /// When true, <see cref="Start"/> asks macOS for Input Monitoring if it isn't granted
    /// (shows the system prompt once per app identity). The composition root sets this
    /// when onboarding has already completed; during onboarding the permission step
    /// drives the request instead.
    /// </summary>
    public bool RequestPermissionOnStart { get; set; }

    /// <summary>True while a tap exists and CoreGraphics reports it enabled.</summary>
    public bool IsTapActive
    {
        get
        {
            var port = _port;
            try
            {
                return port is not null && CGEvent.IsTapEnabled(port);
            }
            catch (ObjectDisposedException)
            {
                return false; // health check rebuilt the tap under us
            }
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_running || _disposed)
            {
                return;
            }
            _running = true;
            if (RequestPermissionOnStart && !Permissions.HasInputMonitoring())
            {
                Permissions.RequestInputMonitoring();
            }
            StartTapThreadLocked();
            _healthTimer = new Timer(_ => HealthCheck(), null, HealthCheckInterval, HealthCheckInterval);
        }
        _logger.LogInformation("MacHotkeyEngine started.");
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!_running)
            {
                return;
            }
            _running = false;
            _healthTimer?.Dispose();
            _healthTimer = null;
            StopTapThreadLocked();
        }
        _logger.LogInformation("MacHotkeyEngine stopped.");
    }

    public void SetChord(HotkeyChord chord)
    {
        lock (_stateLock)
        {
            _tracker.SetChord(chord);
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_emitLock)
        {
            _disposed = true;
        }
        _events.Dispose();
    }

    // ── tap thread (callers hold _lifecycleLock) ─────────────────────────────

    private void StartTapThreadLocked()
    {
        if (_tapThread?.IsAlive == true)
        {
            return;
        }
        var stop = new TapStop();
        var thread = new Thread(() => TapThreadProc(stop))
        {
            IsBackground = true,
            Name = "KusPus.EventTapThread",
        };
        _tapThread = thread;
        _tapStop = stop;
        thread.Start();
    }

    private void StopTapThreadLocked()
    {
        var thread = _tapThread;
        if (_tapStop is { } stop)
        {
            stop.Requested = true;
        }
        // The thread polls the flag at least every RunSliceSeconds.
        bool exited = thread?.Join(TimeSpan.FromSeconds(2)) ?? true;
        _tapThread = null;
        _tapStop = null;
        var port = _port;
        _port = null;
        if (exited)
        {
            port?.Dispose();
        }
        else
        {
            _logger.LogWarning("Event tap thread did not exit in time; leaving its port alive.");
        }
    }

    private const double RunSliceSeconds = 0.25;

    private void TapThreadProc(TapStop stop)
    {
        var mask = CGEventMask.KeyDown | CGEventMask.KeyUp | CGEventMask.FlagsChanged;
        var port = CGEvent.CreateTap(
            CGEventTapLocation.Session,
            CGEventTapPlacement.HeadInsert,
            CGEventTapOptions.ListenOnly,
            mask,
            _callback,
            IntPtr.Zero);
        if (port is null)
        {
            // Input Monitoring not granted (the usual cause). Logged once; the health
            // check keeps retrying so the hotkey comes alive as soon as the grant lands.
            if (!_loggedNoPermission)
            {
                _loggedNoPermission = true;
                _logger.LogWarning(
                    "CGEventTapCreate failed — Input Monitoring is probably not granted. Retrying every {Seconds}s.",
                    HealthCheckInterval.TotalSeconds);
            }
            return;
        }

        if (stop.Requested)
        {
            port.Dispose(); // stopped before we got going
            return;
        }

        var source = port.CreateRunLoopSource();
        var loop = CFRunLoop.Current;
        loop.AddSource(source, CFRunLoop.ModeDefault);
        CGEvent.TapEnable(port);

        _port = port;
        _tapWasAlive = true;
        _loggedNoPermission = false;
        _logger.LogInformation("CGEventTap installed on thread {Tid}.", Environment.CurrentManagedThreadId);

        // Run in short slices and re-check the stop flag: CFRunLoopStop from another thread
        // is lost if it lands before the loop is actually running.
        while (!stop.Requested)
        {
            loop.RunInMode(CFRunLoop.ModeDefault, RunSliceSeconds, false);
        }

        CGEvent.TapDisable(port);
        loop.RemoveSource(source, CFRunLoop.ModeDefault);
        source.Dispose();
    }

    // ── callback — must return fast ──────────────────────────────────────────

    private IntPtr OnTapEvent(IntPtr proxy, CGEventType type, IntPtr eventRef, IntPtr userInfo)
    {
        if (type is CGEventType.TapDisabledByTimeout or CGEventType.TapDisabledByUserInput)
        {
            var port = _port;
            if (port is not null)
            {
                CGEvent.TapEnable(port);
                _logger.LogWarning("Event tap was disabled ({Type}); re-enabled.", type);
            }
            // Key events were lost while the tap was off — most importantly modifier
            // releases. Resync from the live modifier state so a chord released during
            // the outage doesn't stay engaged (and keep a hold-mode recording running).
            ProcessModifierFlags((ulong)CGEventSource.GetFlagsState(CGEventSourceStateID.CombinedSession));
            return eventRef;
        }

        using var evt = Runtime.GetINativeObject<CGEvent>(eventRef, owns: false);
        if (evt is null)
        {
            return eventRef;
        }

        switch (type)
        {
            case CGEventType.KeyDown:
                if (evt.GetLongValueField(CGEventField.KeyboardEventAutorepeat) != 0)
                {
                    return eventRef; // autorepeat — the key is already held
                }
                ProcessNonModifier((ushort)evt.GetLongValueField(CGEventField.KeyboardEventKeycode), isKeyDown: true);
                break;
            case CGEventType.KeyUp:
                ProcessNonModifier((ushort)evt.GetLongValueField(CGEventField.KeyboardEventKeycode), isKeyDown: false);
                break;
            case CGEventType.FlagsChanged:
                ProcessModifierFlags((ulong)evt.Flags);
                break;
        }
        return eventRef;
    }

    private void ProcessNonModifier(ushort keyCode, bool isKeyDown)
    {
        if (MacKeyCodes.IsModifierKeyCode(keyCode))
        {
            return; // modifiers are handled from FlagsChanged
        }
        var vk = MacKeyCodes.ToVirtualKey(keyCode);
        if (vk != VirtualKey.None)
        {
            ProcessKey(vk, isKeyDown);
        }
    }

    /// <summary>
    /// Re-derives every sided modifier from the flags and feeds the tracker only the
    /// transitions. Internal for tests.
    /// </summary>
    internal void ProcessModifierFlags(ulong flags)
    {
        List<(VirtualKey Key, bool Down)>? changes = null;
        lock (_stateLock)
        {
            foreach (var keyCode in SidedModifierKeyCodes)
            {
                if (!MacKeyCodes.TryGetModifierFlagBit(keyCode, out var bit))
                {
                    continue;
                }
                bool down = (flags & bit) != 0;
                if (_modifierDown[keyCode] != down)
                {
                    _modifierDown[keyCode] = down;
                    (changes ??= []).Add((MacKeyCodes.ToVirtualKey(keyCode), down));
                }
            }
        }
        if (changes is null)
        {
            return;
        }
        // Releases first so a swapped-in modifier can't momentarily complete a chord.
        foreach (var (key, down) in changes.OrderBy(c => c.Down))
        {
            ProcessKey(key, down);
        }
    }

    /// <summary>Feeds one key transition through the shared chord tracker. Internal for tests.</summary>
    internal void ProcessKey(VirtualKey key, bool isKeyDown)
    {
        IReadOnlyList<HotkeyEvent> toEmit;
        lock (_stateLock)
        {
            toEmit = _tracker.Step(key, isKeyDown);
        }
        foreach (var e in toEmit)
        {
            Emit(e);
        }
    }

    private void Emit(HotkeyEvent e)
    {
        lock (_emitLock)
        {
            if (!_disposed)
            {
                _events.OnNext(e);
            }
        }
    }

    // ── watchdog ─────────────────────────────────────────────────────────────

    private void HealthCheck()
    {
        bool reinstalled = false;
        lock (_lifecycleLock)
        {
            if (!_running || _disposed)
            {
                return;
            }

            var port = _port;
            if (port is null)
            {
                // Never created (no permission yet) or torn down: try again. The tap
                // thread exits immediately when CGEventTapCreate fails, so IsAlive is
                // false and we can just spin a new attempt.
                StartTapThreadLocked();
                return;
            }

            if (CGEvent.IsTapEnabled(port))
            {
                _tapWasAlive = true;
                return;
            }

            CGEvent.TapEnable(port);
            if (CGEvent.IsTapEnabled(port))
            {
                _logger.LogWarning("Event tap found disabled by health check; re-enabled.");
                return;
            }

            // Still dead: rebuild from scratch.
            _logger.LogWarning("Event tap unrecoverable; re-creating.");
            StopTapThreadLocked();
            lock (_stateLock)
            {
                _tracker.Reset();
                foreach (var k in SidedModifierKeyCodes)
                {
                    _modifierDown[k] = false;
                }
            }
            StartTapThreadLocked();
            reinstalled = _tapWasAlive;
        }
        if (reinstalled)
        {
            Emit(new HookReinstalled());
        }
    }
}
