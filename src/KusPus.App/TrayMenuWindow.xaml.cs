using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using KusPus.Core.Settings;
using KusPus.Core.State;
using KusPus.Persistence;

namespace KusPus.App;

/// <summary>
/// Custom WPF tray right-click menu — replaces the default WinForms
/// ContextMenuStrip with a designed surface (rounded card, drop shadow,
/// themed tokens) per Tray_light.png / Tray_dark.png. Re-shown on every
/// tray right-click; closes on Deactivated (focus lost) or any item click.
/// </summary>
public partial class TrayMenuWindow : Window
{
    // ── User32 — extended styles + cursor query ─────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly Action _onToggleRecorder;
    private readonly Action _onOpenPreferences;
    private readonly Action _onOpenHistory;
    private readonly Action _onOpenModels;
    private readonly Action _onQuit;
    private readonly IPrefsStore _prefs;
    private readonly AppCoordinator _coordinator;
    private IDisposable? _stateSub;
    private IDisposable? _prefsSub;

    public TrayMenuWindow(
        IPrefsStore prefs,
        AppCoordinator coordinator,
        Action onToggleRecorder,
        Action onOpenPreferences,
        Action onOpenHistory,
        Action onOpenModels,
        Action onQuit)
    {
        _prefs = prefs;
        _coordinator = coordinator;
        _onToggleRecorder = onToggleRecorder;
        _onOpenPreferences = onOpenPreferences;
        _onOpenHistory = onOpenHistory;
        _onOpenModels = onOpenModels;
        _onQuit = onQuit;

        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Hidden from Alt-Tab and the taskbar (tool-window). Not NOACTIVATE —
        // we DO want focus on show so the Deactivated handler fires on focus
        // loss, which is how the menu auto-dismisses.
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW;
        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyHotkeyKeycap(_prefs.Current.Hotkey);
        ApplyActiveModel(_prefs.Current.Models.ActiveModelId);
        ApplyStateSubtitle(_coordinator.Snapshot.State);

        _prefsSub = _prefs.Changes.Subscribe(s => Dispatcher.BeginInvoke(() =>
        {
            ApplyHotkeyKeycap(s.Hotkey);
            ApplyActiveModel(s.Models.ActiveModelId);
        }));
        _stateSub = _coordinator.State.Subscribe(snap => Dispatcher.BeginInvoke(() =>
        {
            ApplyStateSubtitle(snap.State);
        }));
    }

    /// <summary>
    /// Places the window above-left of the OS cursor. Called by TrayManager
    /// every right-click so the menu always appears under the user's pointer
    /// regardless of where the tray icon physically sits (Win11 hides the
    /// overflow tray so the icon's coordinates aren't usable as an anchor).
    /// </summary>
    public void ShowAtCursor()
    {
        if (!GetCursorPos(out var pt))
        {
            return;
        }
        // SizeToContent means the window's measured size isn't valid until
        // first render. Show with WindowStartupLocation=Manual at an offscreen
        // location, then re-position on Loaded. Simpler: position so the
        // bottom-right corner sits at the cursor (menu floats up-and-left like
        // a typical popup). Re-position after Loaded if the measured size
        // exceeds the screen edge.
        Left = pt.X - 260;
        Top = pt.Y - 320;
        Show();
        Activate();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Focus lost → user clicked elsewhere → dismiss.
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _stateSub?.Dispose();
        _prefsSub?.Dispose();
        base.OnClosing(e);
    }

    // ── Item click handlers ─────────────────────────────────────────────────
    // Each one fires the host-supplied action, then closes the menu. Order
    // matters: closing first would tear down the action's chain (e.g. invoking
    // ShowOn on MainWindow while we're disposing subscriptions could race).

    private void OnToggleRecorderClick(object sender, RoutedEventArgs e)
    {
        _onToggleRecorder();
        Close();
    }

    private void OnActiveModelClick(object sender, RoutedEventArgs e)
    {
        _onOpenModels();
        Close();
    }

    private void OnPreferencesClick(object sender, RoutedEventArgs e)
    {
        _onOpenPreferences();
        Close();
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        _onOpenHistory();
        Close();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        _onQuit();
    }

    // ── State binding helpers ───────────────────────────────────────────────

    private void ApplyHotkeyKeycap(HotkeySettings hk)
    {
        // Show only the last key in the chord (the trigger key) — full chord
        // wouldn't fit. Modifier symbol composed inline.
        var parts = new List<string>();
        foreach (var m in hk.Modifiers)
        {
            parts.Add(ModifierGlyph(m));
        }
        if (hk.KeyCode is { } k)
        {
            parts.Add(KeyDisplay(k));
        }
        HotkeyKeycapText.Text = parts.Count == 0 ? "—" : string.Join(string.Empty, parts);
    }

    private void ApplyActiveModel(string modelId)
    {
        // Strip the "ggml-" prefix used in file names so the menu shows the
        // bare model display name ("tiny.en" instead of "ggml-tiny.en").
        var name = modelId.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase)
            ? modelId[5..]
            : modelId;
        ActiveModelText.Text = name;
    }

    private void ApplyStateSubtitle(AppState state)
    {
        VersionSubtitle.Text = state switch
        {
            AppState.Recording => "Version 1.0.0 · Recording",
            AppState.Transcribing => "Version 1.0.0 · Transcribing",
            _ => "Version 1.0.0 · Idle",
        };
    }

    private static string ModifierGlyph(KusPus.Core.Hotkeys.VirtualKey vk) => vk switch
    {
        KusPus.Core.Hotkeys.VirtualKey.LeftCtrl => "^",
        KusPus.Core.Hotkeys.VirtualKey.RightCtrl => "^",
        KusPus.Core.Hotkeys.VirtualKey.LeftAlt => "⌥",
        KusPus.Core.Hotkeys.VirtualKey.RightAlt => "⌥",
        KusPus.Core.Hotkeys.VirtualKey.LeftShift => "⇧",
        KusPus.Core.Hotkeys.VirtualKey.RightShift => "⇧",
        KusPus.Core.Hotkeys.VirtualKey.LeftWin => "⊞",
        KusPus.Core.Hotkeys.VirtualKey.RightWin => "⊞",
        _ => vk.ToString(),
    };

    private static string KeyDisplay(KusPus.Core.Hotkeys.VirtualKey vk) => vk switch
    {
        KusPus.Core.Hotkeys.VirtualKey.Space => "Spc",
        KusPus.Core.Hotkeys.VirtualKey.Return => "↵",
        KusPus.Core.Hotkeys.VirtualKey.Tab => "⇥",
        KusPus.Core.Hotkeys.VirtualKey.Escape => "Esc",
        _ => vk.ToString().Length == 1 ? vk.ToString() : vk.ToString(),
    };
}
