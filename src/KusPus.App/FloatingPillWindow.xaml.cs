using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using KusPus.Core.State;

namespace KusPus.App;

/// <summary>
/// The floating "I'm recording / I'm transcribing" pill. TECH_SPEC §8.5 / §24.
///
/// Phase 6 is the bare minimum: topmost, no-activate, click-through, no-taskbar,
/// always-on-top window that shows one of three short strings derived from
/// <see cref="CoordinatorSnapshot.State"/>. Phase 8 adds the audio visualizer,
/// fade animations, Mica/Acrylic, paste-confirmation overlay, and per-monitor DPI.
/// </summary>
public partial class FloatingPillWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public FloatingPillWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED;
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public void Bind(IObservable<CoordinatorSnapshot> state)
    {
        state.Subscribe(snap => Dispatcher.BeginInvoke(() => Render(snap)));
    }

    private void Render(CoordinatorSnapshot snapshot)
    {
        switch (snapshot.State)
        {
            case AppState.Recording:
                StatusText.Text = "Recording…";
                ShowAtCursorMonitor();
                break;
            case AppState.Transcribing:
                StatusText.Text = "Transcribing…";
                break;
            case AppState.Idle:
            case AppState.Cancelled:
                Hide();
                break;
        }
    }

    private void ShowAtCursorMonitor()
    {
        // Phase 6 minimum: position bottom-center of the screen containing the cursor.
        // Phase 8 will do per-monitor DPI math via MonitorFromPoint + GetDpiForMonitor.
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        var work = screen.WorkingArea;
        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Bottom - Height - 40;
        Show();
    }
}
