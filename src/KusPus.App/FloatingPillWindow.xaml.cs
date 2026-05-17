using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KusPus.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
// System.Drawing types leak in via UseWindowsForms — alias the WPF shapes/media we use.
using WpfRectangle = System.Windows.Shapes.Rectangle;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfPoint = System.Windows.Point;

namespace KusPus.App;

/// <summary>
/// Floating pill window per <c>docs/PILL_DESIGN.md</c> (the §10 hover-extend override
/// makes this hit-testable rather than click-through, and adds the close + settings
/// buttons that appear on hover).
///
/// Design coverage (full spec):
/// <list type="bullet">
/// <item>Surface (§1.1, §3.1, §3.3) — 200×56, 8px corners via DWM, dark gradient,
///   1px hairline border, drop shadow, inner top highlight.</item>
/// <item>Mica backdrop (§3.3) on Win11 22H2+ via <c>DWMWA_SYSTEMBACKDROP_TYPE</c>;
///   gracefully falls back to the gradient on older Windows.</item>
/// <item>Five-state machine (§2) — Hidden (resting), Recording, Transcribing,
///   Confirmed (1 s hold), Error (2 s hold).</item>
/// <item>20-bar visualizer (§4) with the damped target/value motion model from
///   §4.2 — center-weighted, per-bar damp rates, real audio levels (when available)
///   override the simulated speak envelope.</item>
/// <item>Accent line (§3.4) — mint gradient with glow, opacity per state.</item>
/// <item>Motion (§5) — 120 ms appear/disappear fade, 150 ms content crossfade,
///   confirmation choreography per §5.1, instant accent-color shift on error.</item>
/// <item>Positioning (§1.2, §6.5) — bottom-center on the monitor containing the
///   foreground window, 40 DIP above work-area bottom, per-monitor DPI math from 8A.</item>
/// <item>No focus theft (§6.3) — <c>WS_EX_NOACTIVATE</c>. Hidden from taskbar/Alt-Tab
///   (§1.2) — <c>WS_EX_TOOLWINDOW</c>.</item>
/// <item>Hover-extend (§10) — width animates 200→280 over 150 ms, button panel
///   fades in. <c>WS_EX_TRANSPARENT</c> is intentionally NOT applied.</item>
/// </list>
///
/// Out-of-spec / deferred until the Settings UI lands (Phase 9+):
/// - Accent colour picker (Mint hardcoded; spec §3.4 Amber/Azure/Violet alternates).
/// - Live light/dark theme switching (dark-only for v1; PILL_DESIGN §3.2 light theme).
/// - Reduced-motion preference gating (§5.3) — pill always animates.
/// - Confirmed-state radial mask on the visualizer (§4.3) — bars hide entirely
///   during Confirmed instead.
/// </summary>
public partial class FloatingPillWindow : Window
{
    // ── DWM ─────────────────────────────────────────────────────────────────
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3;

    // ── User32 extended styles (NO WS_EX_TRANSPARENT per §10.2) ─────────────
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // ── Monitor lookup ──────────────────────────────────────────────────────
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // EX-variant carries the device name (`\\.\DISPLAY1`) which we use as a stable
    // key for the per-monitor remembered-position dictionary.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoExW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    // ── Visualizer geometry (§4.1) ──────────────────────────────────────────
    private const int BarCount = 20;
    private const double BarWidth = 3.0;
    private const double BarGap = 4.0;
    private const double BarMinHeight = 4.0;
    private const double BarMaxHeight = 26.0;
    private const double TrackHeight = 28.0;

    // ── Motion durations (§5) ───────────────────────────────────────────────
    private static readonly Duration AppearDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration ContentCrossfade = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration HoverExtendDuration = new(TimeSpan.FromMilliseconds(150));

    private ILogger<FloatingPillWindow> _logger = NullLogger<FloatingPillWindow>.Instance;
    private Action? _onClose;
    private Action? _onSettings;

    // ── Visualizer state ────────────────────────────────────────────────────
    private readonly WpfRectangle[] _bars = new WpfRectangle[BarCount];
    private readonly double[] _levels = new double[BarCount];
    private readonly double[] _targets = new double[BarCount];
    private readonly Random _rng = new();
    private DateTime _lastFrameTime = DateTime.MinValue;
    private DateTime _nextTargetAt = DateTime.MinValue;
    private float[]? _lastRealLevels;
    private bool _isRecording;
    private bool _renderingHooked;

    // ── State machine ───────────────────────────────────────────────────────
    // Idle is a dev-override (CLAUDE.md deviation): spec §6.1 wants the pill hidden
    // when not in an active state; user requested always-visible until the Settings
    // modal makes the close path discoverable elsewhere.
    private enum PillVisual { Hidden, Idle, Recording, Transcribing, Confirmed, Error }
    private PillVisual _currentVisual = PillVisual.Hidden;
    private DispatcherTimer? _postPasteTimer;

    // Session-only — keyed by MONITORINFOEX.szDevice (e.g. "\\.\DISPLAY1"). Cleared
    // on every fresh process start per user spec ("forget on close"). A dictation
    // started on a monitor with no entry uses that monitor's bottom-center default.
    private readonly Dictionary<string, WpfPoint> _monitorPositions = new(StringComparer.OrdinalIgnoreCase);

    // Set during the drag operation so animation overlap doesn't undo the user.
    private bool _isDragging;

    public FloatingPillWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public void SetLogger(ILogger<FloatingPillWindow> logger) => _logger = logger;

    /// <summary>
    /// Hook for the close button (§10.1). The App passes <c>Application.Shutdown</c> here.
    /// </summary>
    public void SetCloseAction(Action onClose) => _onClose = onClose;

    /// <summary>
    /// Wires the hover-extended Settings button to a host-supplied callback —
    /// in App.OnStartup, this is bound to <c>MainWindow.ShowOn("general")</c> so
    /// the pill's gear button opens the same Preferences modal as the tray menu's
    /// "Preferences…" item. Per docs/APP_DESIGN.md §13 audit follow-up.
    /// </summary>
    public void SetSettingsAction(Action onSettings) => _onSettings = onSettings;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Rounded 8 px corners (Win11). Older Windows returns non-success HRESULT — we ignore.
        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Dark-mode hint so DWM tints Mica with the dark palette.
        int darkMode = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        // Mica (transient). Win11 22H2+. Older Windows returns non-success — gradient fallback.
        int backdrop = DWMSBT_TRANSIENTWINDOW;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        // No focus theft + hidden from taskbar/Alt-Tab. No WS_EX_TRANSPARENT per §10.2.
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildBars();
        StartSpinner();
        // PRD G4 dev override: pill is visible from launch and stays visible
        // between dictations. Transition into Idle so the position math runs
        // and the surface appears.
        TransitionTo(PillVisual.Idle);
    }

    // ── Visualizer build ────────────────────────────────────────────────────

    private void BuildBars()
    {
        if (_bars[0] is not null)
        {
            return;
        }

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new WpfRectangle
            {
                Width = BarWidth,
                Height = BarMinHeight,
                RadiusX = 1.5,
                RadiusY = 1.5,
            };
            // SetResourceReference binds Fill to the theme token, so when
            // ThemeTokens.Apply replaces the brush in Application.Resources the
            // bars re-fill in the new theme automatically.
            bar.SetResourceReference(WpfRectangle.FillProperty, "VisualizerBarActive");
            Canvas.SetLeft(bar, i * (BarWidth + BarGap));
            Canvas.SetTop(bar, (TrackHeight - BarMinHeight) / 2);
            VisualizerCanvas.Children.Add(bar);
            _bars[i] = bar;
            _levels[i] = 0.05;
            _targets[i] = 0.05;
        }

        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnVisualizerTick;
            _renderingHooked = true;
        }
    }

    private void StartSpinner()
    {
        // 0.9 s full rotation per §2.4. Direct BeginAnimation on the RotateTransform —
        // Storyboard.SetTarget on a Freezable inside a deeply named scope silently
        // no-ops; this is the reliable form.
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, anim);
    }

    // ── Visualizer motion model (§4.2) ──────────────────────────────────────

    private void OnVisualizerTick(object? sender, EventArgs e)
    {
        if (_bars[0] is null || _currentVisual is PillVisual.Hidden)
        {
            // Stay quiescent — bars retain whatever level they had. The next show
            // will damp from idle back up via the model.
            return;
        }

        var now = DateTime.UtcNow;
        double dt = _lastFrameTime == DateTime.MinValue
            ? 0.016
            : Math.Clamp((now - _lastFrameTime).TotalSeconds, 0, 0.064);
        _lastFrameTime = now;

        // Re-roll targets every 90–150 ms while recording.
        if (_isRecording && now >= _nextTargetAt)
        {
            _nextTargetAt = now.AddMilliseconds(90 + (_rng.NextDouble() * 60));

            if (_lastRealLevels is not null && _lastRealLevels.Length == BarCount)
            {
                // Real audio path — §4.2 trailer: realAmplitude × center_weight × (1 + jitter).
                for (int i = 0; i < BarCount; i++)
                {
                    double centerWeight = 1.0 - (Math.Abs(i - 9.5) / 9.5);
                    double jitter = ((_rng.NextDouble() * 2) - 1) * 0.05;
                    double scaled = _lastRealLevels[i] * (0.6 + (centerWeight * 0.6)) * 4.0;
                    _targets[i] = Math.Clamp(scaled + jitter, 0.05, 1.0);
                }
            }
            else
            {
                // Simulated speak envelope.
                double speak = 0.55 + (Math.Sin(now.Ticks / 1e7 / 0.38) * 0.25) + ((_rng.NextDouble() - 0.5) * 0.25);
                for (int i = 0; i < BarCount; i++)
                {
                    double centerWeight = 1.0 - (Math.Abs(i - 9.5) / 9.5);
                    double baseVal = 0.18 + (centerWeight * 0.45);
                    double jitter = ((_rng.NextDouble() * 1.0) - 0.30) * 0.55;
                    _targets[i] = Math.Clamp((baseVal * speak) + jitter, 0.05, 1.0);
                }
            }
        }

        if (!_isRecording)
        {
            for (int i = 0; i < BarCount; i++)
            {
                _targets[i] = 0.05;
            }
        }

        // Damped approach with per-bar rate variation so bars don't move in lockstep.
        for (int i = 0; i < BarCount; i++)
        {
            double rate = _isRecording ? (14 + ((i % 3) * 3)) : 6;
            double k = 1 - Math.Exp(-rate * dt);
            _levels[i] += (_targets[i] - _levels[i]) * k;

            double h = BarMinHeight + (_levels[i] * (BarMaxHeight - BarMinHeight));
            _bars[i].Height = h;
            Canvas.SetTop(_bars[i], (TrackHeight - h) / 2);
        }
    }

    // ── Public binding API ──────────────────────────────────────────────────

    public void Bind(IObservable<CoordinatorSnapshot> state)
    {
        state.Subscribe(snap => Dispatcher.BeginInvoke(() => Render(snap)));
    }

    public void BindLevels(IObservable<float[]> levels)
    {
        // Background priority so visualizer updates can't starve state-change rendering.
        levels.Subscribe(arr => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _lastRealLevels = arr;
        })));
    }

    // ── State rendering ─────────────────────────────────────────────────────

    private void Render(CoordinatorSnapshot snapshot)
    {
#pragma warning disable CA1848, CA1873
        _logger.LogDebug(
            "Pill render: state={State} hold={Hold} postPaste={Post} visible={Visible}.",
            snapshot.State, snapshot.IsHoldMode, snapshot.PostPaste, IsVisible);
#pragma warning restore CA1848, CA1873

        // Post-paste snapshot — show Confirmed or Error for its hold, then fade out.
        if (snapshot.PostPaste is { } post)
        {
            if (post.Pasted)
            {
                ShowConfirmed(post.TargetApp);
            }
            else
            {
                ShowError(post.ErrorReason ?? "Something went wrong");
            }
            return;
        }

        // Fresh dictation starting — clear any lingering post-paste timer.
        if (snapshot.State is AppState.Armed or AppState.Recording)
        {
            _postPasteTimer?.Stop();
            _postPasteTimer = null;
        }

        // Hybrid sticky multi-monitor (option C): when a dictation kicks off, move
        // to the foreground window's monitor if the pill isn't already there.
        if (snapshot.State is AppState.Armed or AppState.Recording)
        {
            EnsurePillOnForegroundMonitor();
        }

        switch (snapshot.State)
        {
            case AppState.Recording:
                TransitionTo(PillVisual.Recording);
                break;
            case AppState.Transcribing:
                TransitionTo(PillVisual.Transcribing);
                break;
            default:
                // Idle / Armed / Cancelled — fall back to the Idle visual (dev override
                // of spec §6.1), unless a post-paste hold is currently running.
                if (_postPasteTimer is null)
                {
                    TransitionTo(PillVisual.Idle);
                }
                break;
        }
    }

    private void TransitionTo(PillVisual next)
    {
        if (next == _currentVisual)
        {
            return;
        }

#pragma warning disable CA1848, CA1873
        _logger.LogDebug("Pill visual {From} → {To}.", _currentVisual, next);
#pragma warning restore CA1848, CA1873

        var previous = _currentVisual;
        _currentVisual = next;

        // Update recording flag — drives the visualizer motion model.
        _isRecording = next == PillVisual.Recording;

        // Accent line + glow per §3.4 opacity table. Idle and Hidden both render
        // the accent line invisible.
        AccentLine.Opacity = next switch
        {
            PillVisual.Recording => 1.0,
            PillVisual.Transcribing => 0.55,
            PillVisual.Confirmed => 0.40,
            PillVisual.Error => 1.0,
            _ => 0.0,
        };
        AccentGlow.Opacity = next is PillVisual.Recording or PillVisual.Error ? 0.6 : 0.0;

        // §2.6 error accent shift is instant — swap brush colors.
        if (next == PillVisual.Error)
        {
            SetAccent(0xFF, 0x4D, 0x4F);
        }
        else if (previous == PillVisual.Error)
        {
            SetAccent(0x4D, 0xDB, 0xA6);
        }

        // Content crossfade — fade out the old, fade in the new.
        FadeContent(previous, fadeIn: false);
        FadeContent(next, fadeIn: true);

        // Pill-level appear/disappear (§5).
        if (next == PillVisual.Hidden && previous != PillVisual.Hidden)
        {
            FadePillOut();
        }
        else if (next != PillVisual.Hidden && previous == PillVisual.Hidden)
        {
            FadePillIn();
        }
    }

    private void SetAccent(byte r, byte g, byte b)
    {
        // Three stops in AccentBrush — fade ends to transparent, center to 50% accent.
        var transparent = WpfColor.FromArgb(0x00, r, g, b);
        var middle = WpfColor.FromArgb(0x80, r, g, b);
        AccentBrush.GradientStops[0].Color = transparent;
        AccentBrush.GradientStops[1].Color = middle;
        AccentBrush.GradientStops[2].Color = transparent;
        AccentGlow.Color = WpfColor.FromRgb(r, g, b);
    }

    private void FadeContent(PillVisual which, bool fadeIn)
    {
        FrameworkElement? target = which switch
        {
            PillVisual.Idle => IdleContent,
            PillVisual.Recording => RecordingContent,
            PillVisual.Transcribing => TranscribingContent,
            PillVisual.Confirmed => ConfirmedContent,
            PillVisual.Error => ErrorContent,
            _ => null,
        };
        if (target is null)
        {
            return;
        }

        target.IsHitTestVisible = fadeIn;
        var anim = new DoubleAnimation
        {
            From = fadeIn ? 0 : target.Opacity,
            To = fadeIn ? 1 : 0,
            Duration = ContentCrossfade,
            EasingFunction = new CubicEase { EasingMode = fadeIn ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        target.BeginAnimation(OpacityProperty, anim);
    }

    private void FadePillIn()
    {
        Opacity = 0;
        ShowAtForegroundMonitor();
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = AppearDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void FadePillOut()
    {
        var anim = new DoubleAnimation
        {
            From = Opacity,
            To = 0,
            Duration = AppearDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            if (_currentVisual == PillVisual.Hidden)
            {
                Hide();
                // Reset levels so the next show damps up from idle naturally.
                for (int i = 0; i < BarCount; i++)
                {
                    _levels[i] = 0.05;
                    _targets[i] = 0.05;
                }
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // ── Post-paste states (§2.5, §2.6, §5.1) ────────────────────────────────

    private void ShowConfirmed(string appName)
    {
        ConfirmedAppRun.Text = appName;
        TransitionTo(PillVisual.Confirmed);
        StartPostPasteHold(TimeSpan.FromMilliseconds(1000));
    }

    private void ShowError(string reason)
    {
        ErrorText.Text = reason;
        TransitionTo(PillVisual.Error);
        StartPostPasteHold(TimeSpan.FromMilliseconds(2000));
    }

    private void StartPostPasteHold(TimeSpan hold)
    {
        _postPasteTimer?.Stop();
        _postPasteTimer = new DispatcherTimer { Interval = hold };
        _postPasteTimer.Tick += (_, _) =>
        {
            _postPasteTimer?.Stop();
            _postPasteTimer = null;
            // PRD G4 dev override — return to Idle (visible), not Hidden.
            TransitionTo(PillVisual.Idle);
        };
        _postPasteTimer.Start();
    }

    // ── Positioning (§1.2, §6.5 + multi-monitor sticky from CLAUDE.md) ──────

    private void ShowAtForegroundMonitor()
    {
        var fg = GetForegroundWindow();
        var hMon = fg != IntPtr.Zero
            ? MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);

        if (hMon != IntPtr.Zero)
        {
            PlaceOnMonitor(hMon);
        }
        else
        {
            // Final fallback — no monitor handle. Use cursor's screen via WinForms.
            var cur = System.Windows.Forms.Cursor.Position;
            var s = Screen.FromPoint(cur);
            Left = s.WorkingArea.Left + ((s.WorkingArea.Width - 200.0) / 2);
            Top = s.WorkingArea.Bottom - Height - 40;
        }

        Show();
    }

    /// <summary>
    /// Positions the pill on the given monitor — at the user's remembered position
    /// for that monitor if one exists this session, otherwise at the §1.2 default
    /// (bottom-center of the work area, 40 DIP above the bottom edge). Does not
    /// call Show — the caller decides visibility lifecycle.
    /// </summary>
    private void PlaceOnMonitor(IntPtr hMon)
    {
        var miEx = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoExW(hMon, ref miEx))
        {
            return;
        }

        if (_monitorPositions.TryGetValue(miEx.szDevice, out var remembered))
        {
#pragma warning disable CA1848, CA1873
            _logger.LogDebug("Pill placing on {Device} at remembered ({L:F0},{T:F0}).",
                miEx.szDevice, remembered.X, remembered.Y);
#pragma warning restore CA1848, CA1873
            Left = remembered.X;
            Top = remembered.Y;
            return;
        }

        // Default per design spec §1.2.
        double scale = 1.0;
        if (GetDpiForMonitor(hMon, MonitorDpiType.EffectiveDpi, out uint dpiX, out _) == 0
            && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        double workLeftDip = miEx.rcWork.Left / scale;
        double workWidthDip = (miEx.rcWork.Right - miEx.rcWork.Left) / scale;
        double workBottomDip = miEx.rcWork.Bottom / scale;

        // Anchor on base width (200) not animated width, so hover-extend doesn't drift center.
        Left = workLeftDip + ((workWidthDip - 200.0) / 2);
        Top = workBottomDip - Height - 40;

#pragma warning disable CA1848, CA1873
        _logger.LogDebug("Pill placing on {Device} at default ({L:F0},{T:F0}) (scale {Scale}).",
            miEx.szDevice, Left, Top, scale);
#pragma warning restore CA1848, CA1873
    }

    /// <summary>
    /// Multi-monitor option C (CLAUDE.md): if a dictation is starting on a monitor
    /// the pill isn't on, jump to that monitor's remembered (or default) position.
    /// Called on transition into Armed/Recording. No-op when pill is already on the
    /// foreground monitor, or while the user is actively dragging.
    /// </summary>
    private void EnsurePillOnForegroundMonitor()
    {
        if (_isDragging)
        {
            return;
        }

        var fg = GetForegroundWindow();
        var ourHwnd = new WindowInteropHelper(this).Handle;
        if (fg == IntPtr.Zero || fg == ourHwnd || ourHwnd == IntPtr.Zero)
        {
            return;
        }

        var fgMon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        if (fgMon == IntPtr.Zero)
        {
            return;
        }

        var pillMon = MonitorFromWindow(ourHwnd, MONITOR_DEFAULTTONEAREST);
        if (pillMon == fgMon)
        {
            return;
        }

        PlaceOnMonitor(fgMon);
    }

    // ── Drag (custom, beyond design spec §1.2 click-through override) ───────

    private void OnPillMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        // Skip when the user is clicking on a Settings / Close button — let the
        // button's Click handler do its thing.
        if (IsClickOnButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _isDragging = true;
        try
        {
            DragMove();
        }
        finally
        {
            _isDragging = false;
        }

        // Capture the new position under whatever monitor we landed on.
        var device = GetCurrentMonitorDeviceName();
        if (device is not null)
        {
            _monitorPositions[device] = new WpfPoint(Left, Top);
#pragma warning disable CA1848, CA1873
            _logger.LogDebug("Pill drag end → remembered ({L:F0},{T:F0}) on {Device}.",
                Left, Top, device);
#pragma warning restore CA1848, CA1873
        }
    }

    private static bool IsClickOnButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button)
            {
                return true;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private string? GetCurrentMonitorDeviceName()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero)
        {
            return null;
        }
        var miEx = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoExW(hMon, ref miEx) ? miEx.szDevice : null;
    }

    // ── Hover-extend (§10) ──────────────────────────────────────────────────

    private void OnPillMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateWidth(280);
        AnimateButtonPanel(visible: true);
    }

    private void OnPillMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        AnimateWidth(200);
        AnimateButtonPanel(visible: false);
    }

    private void AnimateWidth(double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = HoverExtendDuration,
            EasingFunction = new CubicEase { EasingMode = to > Width ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        BeginAnimation(WidthProperty, anim);
    }

    private void AnimateButtonPanel(bool visible)
    {
        ButtonPanel.IsHitTestVisible = visible;
        var anim = new DoubleAnimation
        {
            To = visible ? 1.0 : 0.0,
            Duration = HoverExtendDuration,
            EasingFunction = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        ButtonPanel.BeginAnimation(OpacityProperty, anim);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Close button clicked — invoking shutdown action.");
#pragma warning restore CA1848, CA1873
        _onClose?.Invoke();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
#pragma warning disable CA1848, CA1873
        _logger.LogDebug("Settings button clicked — opening Preferences.");
#pragma warning restore CA1848, CA1873
        _onSettings?.Invoke();
    }
}
