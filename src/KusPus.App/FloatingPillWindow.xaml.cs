using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private Action? _onRecordToggle;
    private DispatcherTimer? _nudgeTimer;

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
    // Phase 2: visualizer modes drive both the motion model and which content
    // panel is shown. Off — bars silent, IdleContent (SVG+wordmark) visible.
    // Recording — voice envelope motion, "RECORDING" label.
    // HoverIdle — low-amplitude traveling sine, "IDLE · HOLD TO DICTATE" label.
    private enum VisualizerMode { Off, Recording, HoverIdle }
    private VisualizerMode _visualizerMode = VisualizerMode.Off;
    // Phase tracker for the hover-idle traveling sine wave. Advances each frame
    // by 2π / (2.4 s) so the wave completes a full traversal every ~2.4 s.
    private double _hoverIdleWavePhase;

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

    /// <summary>
    /// Wires the dock's record toggle button to a host-supplied action —
    /// composition root binds to <c>AppCoordinator.ToggleFromTray</c>. Per user
    /// spec the toggle does NOT auto-capture a foreground target window; the
    /// transcript pastes wherever focus is when transcribe finishes. The nudge
    /// popup hints the user to click into their text field while we record.
    /// </summary>
    public void SetRecordToggleAction(Action onToggle) => _onRecordToggle = onToggle;

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
        // Personality animations default ON; SetReduceAnimations from the
        // composition root will pause them if user toggle or OS reduced-motion
        // says so.
        StartPersonalityAnimations();
    }

    // ── Personality animations (PILL_DESIGN Phase 3) ─────────────────────
    //
    // Breath: subtle ±0.6% scale pulse on a 4 s sine. ScaleTransform on
    // PillSurface (RenderTransformOrigin=0.5,0.5).
    // Hue drift: AccentBrush's middle gradient stop cycles mint → seafoam →
    // cyan → back over 14 s with constant perceived lightness (manual color
    // picks approximate OKLCH L=0.84, C=0.14 constraint — WPF has no native
    // OKLCH interpolation).
    //
    // Both stored as long-lived Storyboards so SetReduceAnimations can pause
    // them on toggle without rebuilding.

    private void StartPersonalityAnimations()
    {
        if (_personalityRunning || _reduceAnimations || _bars[0] is null)
        {
            return;
        }
        BuildBreathStoryboard();
        BuildHueDriftStoryboard();
        _breathStoryboard?.Begin(this, isControllable: true);
        _hueStoryboard?.Begin(this, isControllable: true);
        _personalityRunning = true;
    }

    private void StopPersonalityAnimations()
    {
        if (!_personalityRunning)
        {
            return;
        }
        // Storyboard.Stop returns BreathScale/AccentBrush to their initial values
        // (ScaleX/Y=1.0, mint #4DDBA6) so the pill stops at a clean rest pose.
        _breathStoryboard?.Stop(this);
        _hueStoryboard?.Stop(this);
        _personalityRunning = false;
    }

    private void BuildBreathStoryboard()
    {
        if (_breathStoryboard is not null)
        {
            return;
        }
        var sb = new Storyboard();
        // 2 s in + 2 s out via AutoReverse = 4 s cycle. SineEase for the
        // "alive breath" feel.
        var scaleAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 1.006,
            Duration = new Duration(TimeSpan.FromSeconds(2)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(scaleAnim, BreathScale);
        // Two clones — one per axis — so storyboard owns both.
        var xAnim = scaleAnim.Clone();
        var yAnim = scaleAnim.Clone();
        Storyboard.SetTargetProperty(xAnim, new PropertyPath(ScaleTransform.ScaleXProperty));
        Storyboard.SetTargetProperty(yAnim, new PropertyPath(ScaleTransform.ScaleYProperty));
        Storyboard.SetTarget(xAnim, BreathScale);
        Storyboard.SetTarget(yAnim, BreathScale);
        sb.Children.Add(xAnim);
        sb.Children.Add(yAnim);
        _breathStoryboard = sb;
    }

    private void BuildHueDriftStoryboard()
    {
        if (_hueStoryboard is not null)
        {
            return;
        }
        var hueAnim = new ColorAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromSeconds(14)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        // Constant-R=0x4D so the hue shift stays in the same brightness band
        // (manual approximation of OKLCH constant-L/C constraint). Alpha=0x80
        // matches the middle gradient stop's resting opacity.
        hueAnim.KeyFrames.Add(new LinearColorKeyFrame(
            WpfColor.FromArgb(0x80, 0x4D, 0xDB, 0xA6), KeyTime.FromPercent(0.0)));
        hueAnim.KeyFrames.Add(new LinearColorKeyFrame(
            WpfColor.FromArgb(0x80, 0x4D, 0xCD, 0xC2), KeyTime.FromPercent(0.33)));
        hueAnim.KeyFrames.Add(new LinearColorKeyFrame(
            WpfColor.FromArgb(0x80, 0x4D, 0xB8, 0xDB), KeyTime.FromPercent(0.66)));
        hueAnim.KeyFrames.Add(new LinearColorKeyFrame(
            WpfColor.FromArgb(0x80, 0x4D, 0xDB, 0xA6), KeyTime.FromPercent(1.0)));

        var sb = new Storyboard();
        sb.Children.Add(hueAnim);
        Storyboard.SetTarget(hueAnim, AccentBrush.GradientStops[1]);
        Storyboard.SetTargetProperty(hueAnim, new PropertyPath(GradientStop.ColorProperty));
        _hueStoryboard = sb;
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

        // Hover-idle: traveling low-amplitude sine wave per Organic Pill §3 idle-
        // visualizer cue. Per-bar phase offset 0.18 rad, amplitude 0.06-0.14, damp
        // k≈3.5/s. Doesn't share the voice-envelope target-rolling loop.
        if (_visualizerMode == VisualizerMode.HoverIdle)
        {
            _hoverIdleWavePhase += dt * (2 * Math.PI / 2.4);
            for (int i = 0; i < BarCount; i++)
            {
                double s = Math.Sin(_hoverIdleWavePhase - (i * 0.18));
                _targets[i] = 0.10 + (s * 0.04);   // ~0.06 - 0.14
            }
        }

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

        if (!_isRecording && _visualizerMode != VisualizerMode.HoverIdle)
        {
            for (int i = 0; i < BarCount; i++)
            {
                _targets[i] = 0.05;
            }
        }

        // Damped approach with per-bar rate variation so bars don't move in
        // lockstep. HoverIdle uses k≈3.5 for the spec's slow approach.
        for (int i = 0; i < BarCount; i++)
        {
            double rate = _isRecording
                ? (14 + ((i % 3) * 3))
                : (_visualizerMode == VisualizerMode.HoverIdle ? 3.5 : 6);
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

        // Dock's record glyph reflects FSM state: dot when not recording,
        // filled red square when actively recording (so the toggle's affordance
        // matches "press to stop"). Hide the nudge once recording truly begins
        // since it served its purpose.
        UpdateRecordGlyph(next);
        if (next == PillVisual.Recording)
        {
            RecordNudgePopup.IsOpen = false;
            _nudgeTimer?.Stop();
        }

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
            // Idle: which child shows depends on hover state (Phase 2). The
            // ApplyIdleContent helper handles the per-hover swap; here we just
            // delegate when this is an Idle-entry/exit event.
            PillVisual.Idle => null,
            PillVisual.Recording => VisualizerContent,
            PillVisual.Transcribing => TranscribingContent,
            PillVisual.Confirmed => ConfirmedContent,
            PillVisual.Error => ErrorContent,
            _ => null,
        };
        if (which == PillVisual.Idle)
        {
            if (fadeIn)
            {
                // Re-evaluate idle content based on current hover state.
                ApplyIdleContent();
            }
            else
            {
                // Fade out whichever idle child happens to be visible.
                FadeElement(IdleContent, fadeIn: false);
                FadeElement(VisualizerContent, fadeIn: false);
            }
            return;
        }
        if (target is null)
        {
            return;
        }

        // Entering a non-Idle, non-hover-idle state — make sure the visualizer
        // mode reflects the new state.
        if (fadeIn)
        {
            _visualizerMode = which == PillVisual.Recording ? VisualizerMode.Recording : VisualizerMode.Off;
            if (which == PillVisual.Recording)
            {
                VisualizerLabel.Text = "RECORDING";
            }
        }
        FadeElement(target, fadeIn);
    }

    // Phase 2: shows IdleContent (SVG + KusPus) when not hovered, swaps to the
    // VisualizerContent (bars + IDLE · HOLD TO DICTATE label) on hover. Called
    // from TransitionTo when entering Idle, and from OnPillMouseEnter/Leave
    // while in Idle state.
    private void ApplyIdleContent()
    {
        if (_currentVisual != PillVisual.Idle)
        {
            return;
        }
        if (IsMouseOver)
        {
            VisualizerLabel.Text = "IDLE · HOLD TO DICTATE";
            _visualizerMode = VisualizerMode.HoverIdle;
            FadeElement(IdleContent, fadeIn: false);
            FadeElement(VisualizerContent, fadeIn: true);
        }
        else
        {
            _visualizerMode = VisualizerMode.Off;
            FadeElement(VisualizerContent, fadeIn: false);
            FadeElement(IdleContent, fadeIn: true);
        }
    }

    private static void FadeElement(FrameworkElement target, bool fadeIn)
    {
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

    // ── Hover-expand + dock drawer (PILL_DESIGN Phase 1 redesign) ─────────
    //
    // On pointer enter (or while pinned): pill widens 200→320, window grows
    // 56→78 tall to accommodate the 22 px dock peek, dock slides down + fades
    // in, Pin + Magic-wand corner buttons fade in (Pin de-rotates -12°→0°).
    //
    // On pointer leave: reverses — but stays open if pinned.
    //
    // Pin click latches "always open" — dock + corner buttons stay visible
    // after the cursor leaves until the user clicks Pin again.

    private static readonly Duration DockSlideIn = new(TimeSpan.FromMilliseconds(240));
    private static readonly Duration DockSlideOut = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration PinFade = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration PinRotate = new(TimeSpan.FromMilliseconds(220));

    private bool _isPinned;
    // True while the mic-chooser popup is open. Treated as "pinned for hover"
    // so the dock doesn't auto-close mid-pick (cursor enters the popup → pill
    // MouseLeave would otherwise fire and slam the dock shut).
    private bool _pickerOpen;
    private IPrefsStoreBridge? _prefsBridge;
    private IAudioRecorderBridge? _audioBridge;
    // Phase 3 — personality animations.
    private Storyboard? _breathStoryboard;
    private Storyboard? _hueStoryboard;
    private bool _reduceAnimations;
    private bool _personalityRunning;

    // Composition root injects these tiny bridges so the pill can read the
    // current device list + active selection without taking a hard dependency
    // on KusPus.Persistence / KusPus.Audio types. Keeps the assembly graph
    // unchanged (App is the only consumer that knows both layers).
    public interface IPrefsStoreBridge
    {
        string? CurrentInputDeviceId { get; }
        Task SetInputDeviceIdAsync(string? id);
    }
    public interface IAudioRecorderBridge
    {
        IReadOnlyList<(string Id, string Name)> EnumerateInputDevices();
    }
    public void SetBridges(IPrefsStoreBridge prefs, IAudioRecorderBridge audio)
    {
        _prefsBridge = prefs;
        _audioBridge = audio;
        // Warm the device cache on a background thread so the first picker
        // open is instant. Without this, MMDeviceEnumerator.EnumerateAudioEndPoints
        // runs synchronously on the click — same root cause as the audio tab
        // combo lag fixed in f4d2413 (~150 ms COM round-trip per open).
        _ = Task.Run(() =>
        {
            try
            {
                var devices = audio.EnumerateInputDevices();
                Dispatcher.BeginInvoke(() =>
                {
                    _cachedMicDevices = devices;
                    UpdateMicChooserLabel();
                });
            }
            catch (COMException)
            {
                // Enumeration fails on shutdown / fresh-install — picker
                // will fall back to live-enum on first click.
            }
        });
        UpdateMicChooserLabel();
    }

    // Cached at SetBridges time on a background thread. Reused on every picker
    // open so the popup appears instantly. Refreshed in the background after
    // each open so hot-plugged devices appear on the next open.
    private IReadOnlyList<(string Id, string Name)>? _cachedMicDevices;

    /// <summary>
    /// Composition root calls this with (userToggle OR Windows reduced-motion).
    /// True → personality animations (breath, hue drift) pause. State-transition
    /// animations and the dock slide remain active.
    /// </summary>
    public void SetReduceAnimations(bool reduce)
    {
        _reduceAnimations = reduce;
        if (reduce)
        {
            StopPersonalityAnimations();
        }
        else
        {
            StartPersonalityAnimations();
        }
    }

    private void OnPillMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Pin = compact-mode toggle (user spec): while pinned, hover MUST NOT
        // re-expand the window or re-show the dock — only the idle content
        // swaps SVG+wordmark → visualizer+IDLE label.
        if (!_isPinned)
        {
            OpenDock();
        }
        ApplyIdleContent();
    }

    private void OnPillMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Don't close while the user has the mic chooser open — moving the
        // cursor over the popup fires the pill's MouseLeave (documented WPF
        // behaviour). Popup's Closed handler re-evaluates on close.
        if (_pickerOpen)
        {
            return;
        }
        // Pinned mode: don't contract (we're already contracted) — only flip
        // content back to SVG+wordmark.
        if (!_isPinned)
        {
            CloseDock();
        }
        ApplyIdleContent();
    }

    private const double PillCollapsedWidth = 200;
    private const double PillExpandedWidth = 320;
    private const double WindowCollapsedHeight = 56;
    private const double WindowExpandedHeight = 78;

    private void OpenDock()
    {
        AnimateWindowSize(width: PillExpandedWidth, height: WindowExpandedHeight, dockShowing: true);
        AnimateDockDrawer(visible: true);
        AnimateCornerButtons(visible: true);
    }

    private void CloseDock()
    {
        AnimateWindowSize(width: PillCollapsedWidth, height: WindowCollapsedHeight, dockShowing: false);
        AnimateDockDrawer(visible: false);
        AnimateCornerButtons(visible: false);
    }

    private void AnimateWindowSize(double width, double height, bool dockShowing)
    {
        // Fixes per the WPF window-animation research:
        // 1. Center-expand: when Width grows by ΔW, Left must shrink by ΔW/2 so
        //    the pill expands symmetrically (default WPF animates Width only —
        //    right edge moves while left stays put). User audit feedback.
        // 2. Height-stuck bug: animating Window.Height with WindowStyle="None"
        //    is documented as "part WPF / part native" (see Microsoft Learn
        //    forum thread + Pixel-in-Gene blog). The animated value can fail
        //    to sync back to the OS window. Fix: FillBehavior=Stop on the
        //    Width/Height animations + a Completed handler that explicitly
        //    clears the animation clock and assigns the final value.
        var ease = new CubicEase { EasingMode = dockShowing ? EasingMode.EaseOut : EasingMode.EaseIn };
        var dur = dockShowing ? DockSlideIn : DockSlideOut;

        double deltaW = width - Width;
        double targetLeft = Left - (deltaW / 2.0);

        AnimateWindowProperty(WidthProperty, width, dur, ease);
        AnimateWindowProperty(HeightProperty, height, dur, ease);
        AnimateWindowProperty(LeftProperty, targetLeft, dur, ease);
    }

    private void AnimateWindowProperty(DependencyProperty prop, double to, Duration dur, IEasingFunction ease)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = dur,
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) =>
        {
            // Clear the animation clock so the property's local value takes
            // effect, then assign the target value. Without this the OS-side
            // window stays at the animated value (WPF/native split bug).
            BeginAnimation(prop, null);
            SetValue(prop, to);
        };
        BeginAnimation(prop, anim);
    }

    private void AnimateDockDrawer(bool visible)
    {
        DockDrawer.IsHitTestVisible = visible;
        var ease = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn };
        var dur = visible ? DockSlideIn : DockSlideOut;
        DockDrawer.BeginAnimation(OpacityProperty,
            new DoubleAnimation { To = visible ? 1.0 : 0.0, Duration = dur, EasingFunction = ease });
        DockTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation { To = visible ? 0.0 : -22.0, Duration = dur, EasingFunction = ease });
    }

    private void AnimateCornerButtons(bool visible)
    {
        // While pinned (compact mode), the pin button stays visible always so
        // the user can click it again to unpin. The buttons share a StackPanel,
        // so the magic-wand rides along — harmless (it's dormant anyway).
        bool effectiveVisible = visible || _isPinned;
        CornerButtons.IsHitTestVisible = effectiveVisible;
        var ease = new CubicEase { EasingMode = effectiveVisible ? EasingMode.EaseOut : EasingMode.EaseIn };
        CornerButtons.BeginAnimation(OpacityProperty,
            new DoubleAnimation { To = effectiveVisible ? 1.0 : 0.0, Duration = PinFade, EasingFunction = ease });
        // Pin un-rotates on hover-in, rotates back to -12° on hover-out (unless pinned).
        double targetAngle = effectiveVisible ? 0.0 : -12.0;
        PinRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation { To = targetAngle, Duration = PinRotate,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ── Corner-button + dock-button handlers ──────────────────────────────

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
#pragma warning disable CA1848, CA1873
        _logger.LogDebug("Pin toggled → {Pinned}.", _isPinned);
#pragma warning restore CA1848, CA1873
        // Mint tint glyph when pinned.
        PinGlyph.SetResourceReference(TextBlock.ForegroundProperty,
            _isPinned ? "Mint" : "SecondaryText");
        if (_isPinned)
        {
            // Compact mode (user spec): contract pill + slide dock back, even
            // though the cursor is still over the pill from clicking pin. The
            // pin button stays visible (AnimateCornerButtons → effectiveVisible
            // includes _isPinned). Content stays as visualizer for as long as
            // the cursor remains; OnPillMouseLeave will swap it to SVG.
            CloseDock();
        }
        else if (IsMouseOver)
        {
            // Just unpinned and still hovered → resume the normal hover-expand.
            OpenDock();
        }
        // else (unpinned + not hovered) → leave the pill in its contracted
        // resting state; next hover will OpenDock normally.
    }

    private void UpdateRecordGlyph(PillVisual state)
    {
        // RadiusX=4 + W=8 renders as a circle (radius = half side). Drop the
        // radius to 1.5 and it reads as a filled rounded square — the canonical
        // "press to stop" affordance.
        bool recording = state == PillVisual.Recording;
        RecordGlyph.RadiusX = recording ? 1.5 : 4;
        RecordGlyph.RadiusY = recording ? 1.5 : 4;
    }

    private void OnRecordToggleClick(object sender, RoutedEventArgs e)
    {
#pragma warning disable CA1848, CA1873
        _logger.LogDebug("Record toggle clicked → ToggleFromTray.");
#pragma warning restore CA1848, CA1873
        _onRecordToggle?.Invoke();
        // Nudge appears only when starting a fresh recording (Idle → Recording).
        // The post-toggle snapshot will arrive on the next Render(); we look at
        // the CURRENT visual instead since render() lags one dispatch. Showing
        // the nudge when stopping (Recording → Transcribing) would be noise.
        if (_currentVisual is PillVisual.Idle)
        {
            ShowRecordNudge();
        }
    }

    private void ShowRecordNudge()
    {
        // 6s display per user feedback — the previous 3 s window dismissed
        // before the user could read it. Auto-dismisses when state moves to
        // Recording (Render() / TransitionTo) so the nudge doesn't linger
        // after dictation actually started.
        _nudgeTimer?.Stop();
        RecordNudgePopup.IsOpen = true;
        _nudgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _nudgeTimer.Tick += (_, _) =>
        {
            _nudgeTimer?.Stop();
            _nudgeTimer = null;
            RecordNudgePopup.IsOpen = false;
        };
        _nudgeTimer.Start();
    }

    private void OnMicChooserClick(object sender, RoutedEventArgs e)
    {
        if (_audioBridge is null || _prefsBridge is null)
        {
            return;
        }
        BuildMicChooserList();
        _pickerOpen = true;
        MicChooserPopup.IsOpen = true;
        // Refresh the cache in the background so a hot-plugged device shows
        // up next time the picker opens. Doesn't block this open.
        RefreshMicCacheAsync();
    }

    private void RefreshMicCacheAsync()
    {
        if (_audioBridge is null)
        {
            return;
        }
        var audio = _audioBridge;
        _ = Task.Run(() =>
        {
            try
            {
                var devices = audio.EnumerateInputDevices();
                Dispatcher.BeginInvoke(() => _cachedMicDevices = devices);
            }
            catch (COMException)
            {
                // Treat as "stale cache is better than no cache" — leave existing.
            }
        });
    }

    private void OnMicChooserPopupClosed(object? sender, EventArgs e)
    {
        _pickerOpen = false;
        UpdateMicChooserLabel();
        // If the cursor isn't over the pill any more (user picked a device and
        // moved away), close the dock now that the popup-pin lock is released.
        if (!IsMouseOver && !_isPinned)
        {
            CloseDock();
            ApplyIdleContent();
        }
    }

    private void BuildMicChooserList()
    {
        if (_audioBridge is null || _prefsBridge is null)
        {
            return;
        }
        MicChooserList.Children.Clear();
        // Use the cached list if we have one; fall back to live enum on the
        // very first open before the background warm completes. Subsequent
        // opens always hit the cache → no perceptible lag.
        var devices = _cachedMicDevices ?? _audioBridge.EnumerateInputDevices();
        var currentId = _prefsBridge.CurrentInputDeviceId;

        MicChooserList.Children.Add(BuildMicChooserItem(
            id: null,
            label: "Default device (follows Windows)",
            isSelected: string.IsNullOrEmpty(currentId)));

        foreach (var d in devices)
        {
            MicChooserList.Children.Add(BuildMicChooserItem(
                id: d.Id,
                label: d.Name,
                isSelected: string.Equals(d.Id, currentId, StringComparison.Ordinal)));
        }
    }

    private System.Windows.Controls.Button BuildMicChooserItem(string? id, string label, bool isSelected)
    {
        var btn = new System.Windows.Controls.Button
        {
            Background = isSelected
                ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("MintTint")
                : System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = id,
            Content = new TextBlock
            {
                Text = label,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("PrimaryText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        btn.Template = BuildMicChooserItemTemplate();
        btn.Click += async (_, _) =>
        {
            if (_prefsBridge is null)
            {
                return;
            }
            await _prefsBridge.SetInputDeviceIdAsync(id).ConfigureAwait(true);
            MicChooserPopup.IsOpen = false;
        };
        return btn;
    }

    private static ControlTemplate BuildMicChooserItemTemplate()
    {
        // Rounded surface with explicit hover tint trigger so non-selected
        // items get a HoverSubtle background as the cursor passes over —
        // selected items keep their MintTint (set inline by BuildMicChooserItem).
        var template = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "ItemBg";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
            });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Stretch);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        // Hover trigger — only takes effect when the parent's Background is
        // Transparent (non-selected items). Selected items already have an
        // opaque MintTint so the hover layer is masked by it.
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(
            Border.BackgroundProperty,
            (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("HoverSubtle"),
            "ItemBg"));
        template.Triggers.Add(hoverTrigger);
        return template;
    }

    private void UpdateMicChooserLabel()
    {
        if (_prefsBridge is null || _audioBridge is null)
        {
            return;
        }
        var currentId = _prefsBridge.CurrentInputDeviceId;
        if (string.IsNullOrEmpty(currentId))
        {
            MicChooserLabel.Text = "Default device";
            return;
        }
        // Same caching strategy as BuildMicChooserList — use the cache if
        // populated (the warm task may not have completed yet on very early
        // calls; fall back to live enum once).
        var devices = _cachedMicDevices ?? _audioBridge.EnumerateInputDevices();
        var match = devices.FirstOrDefault(d => string.Equals(d.Id, currentId, StringComparison.Ordinal));
        MicChooserLabel.Text = match.Name ?? "Default device";
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
