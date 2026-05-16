using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using KusPus.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.App;

/// <summary>
/// Phase 6 milestone pill — plain dark rectangle that displays the current
/// FSM state. TECH_SPEC §8.5 / §24's transparent acrylic version with the
/// visualizer + paste-confirmation overlay ships in Phase 8.
///
/// Window styles set in <see cref="OnSourceInitialized"/>:
/// <list type="bullet">
/// <item><c>WS_EX_TOOLWINDOW</c> — no Alt+Tab, no taskbar entry.</item>
/// <item><c>WS_EX_NOACTIVATE</c> — clicking the pill doesn't steal focus.</item>
/// <item><c>WS_EX_TRANSPARENT</c> — mouse events pass through to the underlying window.</item>
/// </list>
/// <c>AllowsTransparency=True</c> is deliberately OFF for Phase 6 — it caused the
/// pill to render invisibly on the author's Windows 11 dev box during the first
/// smoke test. Phase 8 brings it back along with the rounded-corner / acrylic look.
/// </summary>
public partial class FloatingPillWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    // WS_EX_TRANSPARENT and WS_EX_NOACTIVATE temporarily removed for Win 11 25H2
    // pill-visibility debugging. Restored in Phase 8.

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private ILogger<FloatingPillWindow> _logger = NullLogger<FloatingPillWindow>.Instance;

    public FloatingPillWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void SetLogger(ILogger<FloatingPillWindow> logger) => _logger = logger;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        // Only tool-window for now (no taskbar / no Alt-Tab). Skip click-through
        // and no-activate during the dev visibility debug — they sometimes
        // suppress the window on Win 11 25H2 entirely. Phase 8 reinstates them.
        ex |= WS_EX_TOOLWINDOW;
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public void Bind(IObservable<CoordinatorSnapshot> state)
    {
        state.Subscribe(snap => Dispatcher.BeginInvoke(() => Render(snap)));
    }

    private void Render(CoordinatorSnapshot snapshot)
    {
#pragma warning disable CA1848, CA1873
        // Information level so it definitely appears in the log file regardless of
        // MEL filter settings. Phase 8 will drop this back to Debug.
        _logger.LogInformation(
            "Pill render: state={State} holdMode={Hold} visible={Visible}.",
            snapshot.State, snapshot.IsHoldMode, IsVisible);
#pragma warning restore CA1848, CA1873

        StatusText.Text = snapshot.State switch
        {
            AppState.Idle => "KusPus (idle)",
            AppState.Armed => "Armed…",
            AppState.Recording => "Recording…",
            AppState.Transcribing => "Transcribing…",
            AppState.Cancelled => "Cancelled",
            _ => "KusPus",
        };

        if (!IsVisible)
        {
            ShowAtCursorMonitor();
            // Force topmost re-apply after Show — Win 11 sometimes drops the topmost
            // bit when a window with WS_EX_NOACTIVATE is first shown.
            Topmost = false;
            Topmost = true;
        }
    }

    private void ShowAtCursorMonitor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        // DEBUG: position at CENTER of screen so it can't be missed.
        // Phase 8 restores bottom-center placement.
        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Top + ((work.Height - Height) / 2);
        Show();
    }
}
