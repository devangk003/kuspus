using KusPus.Core.State;
using KusPus.Persistence;
using WinFormsApp = System.Windows.Forms;

namespace KusPus.App;

/// <summary>
/// Tray icon per TECH_SPEC §8.9 + §25. Uses the bundled icon.ico (generated from
/// icons/icon.svg by tools/IconBuilder). Right-click opens a custom WPF
/// <see cref="TrayMenuWindow"/> matching the user-supplied design — not the
/// default WinForms ContextMenuStrip — so the menu can carry the design-system
/// tokens (rounded surface, drop shadow, themed text + icons, hotkey keycap).
/// </summary>
internal sealed class TrayManager : IDisposable
{
    private readonly WinFormsApp.NotifyIcon _icon;
    private readonly IPrefsStore _prefs;
    private readonly AppCoordinator _coordinator;
    private readonly Action _onToggleRecorder;
    private readonly Action<string> _onOpenTab;
    private readonly Action _onQuit;
    private TrayMenuWindow? _activeMenu;
    // State-aware tray icons. Loaded once at construction so the per-state
    // swap is allocation-free (System.Drawing.Icon is mutable + lives until
    // dispose). Recording / Error icons overlay a glyph on the base bars.
    private readonly System.Drawing.Icon _iconIdle;
    private readonly System.Drawing.Icon _iconRecording;
    private readonly System.Drawing.Icon _iconError;
    private IDisposable? _stateSub;
    // Track current to avoid swapping NotifyIcon.Icon on every snapshot —
    // setter is cheap but a no-op skip keeps Win32 IPC pressure off.
    private string _currentIconKey = "idle";

    public TrayManager(
        IPrefsStore prefs,
        AppCoordinator coordinator,
        Action onToggleRecorder,
        Action<string> onOpenTab,
        Action onQuit)
    {
        _prefs = prefs;
        _coordinator = coordinator;
        _onToggleRecorder = onToggleRecorder;
        _onOpenTab = onOpenTab;
        _onQuit = onQuit;

        _iconIdle = LoadIcon("icon-idle.ico");
        _iconRecording = LoadIcon("icon-recording.ico");
        _iconError = LoadIcon("icon-error.ico");

        _icon = new WinFormsApp.NotifyIcon
        {
            Icon = _iconIdle,
            Text = "KusPus",
            Visible = true,
        };
        // ContextMenuStrip = null — we handle right-click ourselves to render
        // the WPF design-system menu instead of the OS-themed strip.
        _icon.MouseClick += OnTrayMouseClick;

        // Live state binding — flip the tray icon as the FSM advances. Snapshot
        // also carries PostPaste; we treat a failed PostPaste as Error so the
        // user sees the warning glyph for the duration of the error hold.
        _stateSub = _coordinator.State.Subscribe(snap =>
        {
            string key = snap.PostPaste is { Pasted: false }
                ? "error"
                : snap.State switch
                {
                    AppState.Recording => "recording",
                    AppState.Transcribing => "recording",
                    _ => "idle",
                };
            if (key == _currentIconKey)
            {
                return;
            }
            _currentIconKey = key;
            _icon.Icon = key switch
            {
                "recording" => _iconRecording,
                "error" => _iconError,
                _ => _iconIdle,
            };
        });
    }

    private void OnTrayMouseClick(object? sender, WinFormsApp.MouseEventArgs e)
    {
        if (e.Button != WinFormsApp.MouseButtons.Right)
        {
            return;
        }
        // If a menu instance from a prior right-click is still up (rapid
        // re-click before Deactivated fired) close it first, then re-show at
        // the new cursor position.
        _activeMenu?.Close();
        _activeMenu = new TrayMenuWindow(
            _prefs,
            _coordinator,
            onToggleRecorder: _onToggleRecorder,
            onOpenPreferences: () => _onOpenTab("general"),
            onOpenHistory: () => _onOpenTab("history"),
            onOpenModels: () => _onOpenTab("models"),
            onQuit: _onQuit);
        _activeMenu.Closed += (_, _) => _activeMenu = null;
        _activeMenu.ShowAtCursor();
    }

    public void Dispose()
    {
        _stateSub?.Dispose();
        _icon.MouseClick -= OnTrayMouseClick;
        _icon.Visible = false;
        _icon.Icon = null;
        _iconIdle.Dispose();
        _iconRecording.Dispose();
        _iconError.Dispose();
        _icon.Dispose();
        _activeMenu?.Close();
    }

    private static System.Drawing.Icon LoadIcon(string resourceName)
    {
        // Resolve the WPF Resource via pack URI and hand the stream to
        // System.Drawing.Icon. resourceName is the per-state file name (e.g.
        // "icon-recording.ico"). Falls back to the system app icon if the
        // resource is missing — defensive, shouldn't fire in a normal build.
        var info = System.Windows.Application.GetResourceStream(
            new Uri($"pack://application:,,,/{resourceName}"));
        if (info is null)
        {
            return System.Drawing.SystemIcons.Application;
        }
        using var stream = info.Stream;
        return new System.Drawing.Icon(stream);
    }
}
