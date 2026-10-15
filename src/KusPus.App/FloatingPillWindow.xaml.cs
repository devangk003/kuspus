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
using WpfPoint = System.Windows.Point;

namespace KusPus.App;

/// <summary>
/// Floating Wispr-style bar. A fixed-size (352×84) fully transparent layered
/// window that never resizes; the dark capsule animates <em>inside</em> it via
/// three DependencyProperties (<see cref="CapsuleWidth"/>, <see cref="CapsuleHeight"/>,
/// <see cref="DockReveal"/>) — see <see cref="ApplyGeometry"/>, the single source
/// of truth for the capsule's target geometry.
///
/// Visual states (all derived from <see cref="CoordinatorSnapshot"/>):
/// <list type="bullet">
/// <item>Idle — a 56×8 dark handle at the bottom-centre of the work area, 10 DIP
///   above the taskbar. Nothing else visible.</item>
/// <item>Armed — 160×36 capsule, 20 mint bars at minimum height (dotted line).</item>
/// <item>Recording — bars follow the damped target/value motion model; real audio
///   levels (when available) override the simulated speak envelope.</item>
/// <item>Transcribing — bars collapse to dots and shimmer (travelling opacity wave).</item>
/// <item>Confirmed — instant collapse to the handle + a 600 ms mint flash. No text.</item>
/// <item>Error — capsule sized to the reason text, red dot + reason, 2 s hold.</item>
/// <item>Hover / Pinned — 320×36 capsule with the hotkey hint, Pin + Wand corner
///   buttons, and the 22 DIP dock (Record, Mic chooser, Settings, Quit) sliding
///   out below. Pin keeps it open after the cursor leaves.</item>
/// </list>
///
/// Surface is always dark regardless of the app theme (Pill* tokens in
/// <see cref="ThemeTokens"/>). Alpha-0 window pixels are OS click-through, so
/// clicks beside the handle reach the window underneath; <c>HoverPad</c> (alpha
/// 1/255) gives the 8 DIP handle a usable hover target.
///
/// Positioning — bottom-centre of the monitor containing the foreground window,
/// per-monitor DPI math. Remembered per-monitor positions and drag both work on
/// the <em>anchor</em> (bottom-centre of the handle) so the bar stays centred at
/// every capsule size. No focus theft (<c>WS_EX_NOACTIVATE</c>), hidden from
/// taskbar/Alt-Tab (<c>WS_EX_TOOLWINDOW</c>).
///
/// Motion — expand 180 ms cubic ease-out, collapse 120 ms cubic ease-in, 150 ms
/// content crossfades. Reduce-animations (user toggle OR Windows "Show
/// animations" off) snaps every resize and freezes the shimmer.
/// </summary>
public partial class FloatingPillWindow : Window
{
    // ── User32 extended styles (NO WS_EX_TRANSPARENT — the capsule is hit-testable) ──
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

    // ── Visualizer geometry ─────────────────────────────────────────────────
    private const int BarCount = 20;
    private const double BarWidth = 3.0;
    private const double BarGap = 3.0;
    private const double BarMinHeight = 3.0;
    private const double BarMaxHeight = 22.0;
    private const double TrackHeight = 24.0;

    // ── Capsule geometry (DIP). Window = HoverW + 2·16 pad wide (352),
    //    ChipH + DockH + 16 pad + PadBottom tall (84) — the 16 DIP side/top pad
    //    is the Root grid's Margin in XAML; the window never resizes. ──
    private const double HandleW = 56;
    private const double HandleH = 8;
    private const double ChipH = 36;
    private const double RecordW = 160;
    private const double HoverW = 320;
    private const double DockH = 22;
    // Default anchor sits this far above the work-area bottom.
    private const double EdgeGap = 10;
    // Bottom pad under the handle (Root grid's bottom Margin) — the anchor is
    // this far above the window's bottom edge.
    private const double PadBottom = 10;
    // HoverPad outgrows the capsule by this much (both axes) so the 8 DIP
    // handle has a forgiving hover target.
    private const double HoverPadSlackW = 24;
    private const double HoverPadSlackH = 12;

    // ── Motion durations ────────────────────────────────────────────────────
    private const int ExpandMs = 180;
    private const int CollapseMs = 120;
    private const int FlashMs = 600;
    private static readonly Duration AppearDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration ContentCrossfade = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration PinFade = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration PinRotate = new(TimeSpan.FromMilliseconds(220));

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
    // Off — bars hidden (Idle / Confirmed / Error / Hidden).
    // Dots — Armed: bars pinned at minimum height, full opacity (dotted line).
    // Recording — voice envelope motion.
    // Shimmer — Transcribing: dots with a travelling opacity wave.
    private enum VisualizerMode { Off, Dots, Recording, Shimmer }
    private VisualizerMode _visualizerMode = VisualizerMode.Off;
    // Phase tracker for the shimmer wave. Advances each frame by 2π / 1.2 s.
    private double _shimmerPhase;

    // ── State machine ───────────────────────────────────────────────────────
    private enum PillVisual { Hidden, Idle, Armed, Recording, Transcribing, Confirmed, Error }
    private PillVisual _currentVisual = PillVisual.Hidden;
    private DispatcherTimer? _postPasteTimer;

    // Session-only — keyed by MONITORINFOEX.szDevice (e.g. "\\.\DISPLAY1"). Cleared
    // on every fresh process start per user spec ("forget on close"). Stores the
    // ANCHOR (bottom-centre of the handle), not the window origin, so the bar
    // stays centred at every capsule size. A dictation started on a monitor with
    // no entry uses that monitor's bottom-centre default.
    private readonly Dictionary<string, WpfPoint> _monitorPositions = new(StringComparer.OrdinalIgnoreCase);

    // Set during the drag operation so animation overlap doesn't undo the user.
    private bool _isDragging;

    // Tracks which content panels are currently faded in so repeated
    // UpdateContentVisibility calls (every hover) don't restart crossfades.
    private readonly Dictionary<FrameworkElement, bool> _shown = new();

    // ── Capsule geometry DPs — animated with DoubleAnimation via AnimateDp ──

    public static readonly DependencyProperty CapsuleWidthProperty = DependencyProperty.Register(
        nameof(CapsuleWidth), typeof(double), typeof(FloatingPillWindow),
        new PropertyMetadata(HandleW, OnCapsuleWidthChanged));

    public static readonly DependencyProperty CapsuleHeightProperty = DependencyProperty.Register(
        nameof(CapsuleHeight), typeof(double), typeof(FloatingPillWindow),
        new PropertyMetadata(HandleH, OnCapsuleHeightChanged));

    public static readonly DependencyProperty DockRevealProperty = DependencyProperty.Register(
        nameof(DockReveal), typeof(double), typeof(FloatingPillWindow),
        new PropertyMetadata(0.0, OnDockRevealChanged));

    /// <summary>Capsule width in DIP (56 handle … 320 hover).</summary>
    public double CapsuleWidth
    {
        get => (double)GetValue(CapsuleWidthProperty);
        set => SetValue(CapsuleWidthProperty, value);
    }

    /// <summary>Capsule height in DIP (8 handle … 36 chip).</summary>
    public double CapsuleHeight
    {
        get => (double)GetValue(CapsuleHeightProperty);
        set => SetValue(CapsuleHeightProperty, value);
    }

    /// <summary>Dock reveal in DIP (0 closed … 22 open). Drives the capsule's bottom margin and the drawer's opacity/slide.</summary>
    public double DockReveal
    {
        get => (double)GetValue(DockRevealProperty);
        set => SetValue(DockRevealProperty, value);
    }

    private static void OnCapsuleWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FloatingPillWindow)d).ApplyCapsuleWidth((double)e.NewValue);

    private static void OnCapsuleHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FloatingPillWindow)d).ApplyCapsuleHeight((double)e.NewValue);

    private static void OnDockRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((FloatingPillWindow)d).ApplyDockReveal((double)e.NewValue);

    public FloatingPillWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public void SetLogger(ILogger<FloatingPillWindow> logger) => _logger = logger;

    /// <summary>
    /// Hook for the dock's Quit button. The App passes <c>Application.Shutdown</c> here.
    /// </summary>
    public void SetCloseAction(Action onClose) => _onClose = onClose;

    /// <summary>
    /// Wires the dock's Settings button to a host-supplied callback — in
    /// App.OnStartup, this is bound to <c>MainWindow.ShowOn("general")</c> so the
    /// pill's gear button opens the same Preferences modal as the tray menu's
    /// "Preferences…" item.
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

    /// <summary>
    /// Sets the hover hint line ("Hold ^ ⊞ to dictate"). The composition root
    /// feeds it from <c>HotkeyChordFormat.Hint</c> at startup and on every
    /// settings change so a hotkey edit updates the pill live.
    /// </summary>
    public void SetHotkeyHint(string hint) => HintLabel.Text = hint;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // No focus theft + hidden from taskbar/Alt-Tab. No WS_EX_TRANSPARENT —
        // the capsule itself must stay hit-testable; alpha-0 pixels of the
        // layered window are already click-through at the OS level.
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        _ = SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildBars();
        // The handle is visible from launch and stays visible between
        // dictations. Transition into Idle so the position math runs and the
        // surface appears (no-op if Bind() already rendered the Idle snapshot).
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
            // Solid mint in both themes — the capsule is always dark.
            bar.SetResourceReference(WpfRectangle.FillProperty, "Mint");
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

    /// <summary>
    /// Returns every bar to its resting pose (minimum height, full opacity) so
    /// the next Armed/Recording damps up from idle naturally instead of from
    /// whatever height a cancelled recording left behind.
    /// </summary>
    private void ResetBars()
    {
        _shimmerPhase = 0;
        for (int i = 0; i < BarCount; i++)
        {
            _levels[i] = 0.05;
            _targets[i] = 0.05;
            if (_bars[i] is { } bar)
            {
                bar.Opacity = 1.0;
                bar.Height = BarMinHeight;
                Canvas.SetTop(bar, (TrackHeight - BarMinHeight) / 2);
            }
        }
    }

    // ── Visualizer motion model ─────────────────────────────────────────────

    private void OnVisualizerTick(object? sender, EventArgs e)
    {
        if (_bars[0] is null
            || _currentVisual is PillVisual.Hidden or PillVisual.Idle or PillVisual.Confirmed)
        {
            // Stay quiescent — bars are hidden in these states (and were reset
            // on the way in), so there's nothing to animate.
            return;
        }

        var now = DateTime.UtcNow;
        double dt = _lastFrameTime == DateTime.MinValue
            ? 0.016
            : Math.Clamp((now - _lastFrameTime).TotalSeconds, 0, 0.064);
        _lastFrameTime = now;

        switch (_visualizerMode)
        {
            case VisualizerMode.Shimmer:
                // Transcribing: bars collapse to dots; a travelling opacity wave
                // (period 1.2 s, per-bar phase offset 0.35 rad) says "working".
                // Reduce-motion → constant 0.7 so nothing pulses.
                _shimmerPhase += dt * (2 * Math.PI / 1.2);
                for (int i = 0; i < BarCount; i++)
                {
                    _targets[i] = 0.05;
                    _bars[i].Opacity = _reduceAnimations
                        ? 0.7
                        : 0.35 + (0.65 * Math.Max(0, Math.Sin(_shimmerPhase - (i * 0.35))));
                }
                break;

            case VisualizerMode.Dots:
                // Armed: flat dotted line at full opacity, waiting for speech.
                for (int i = 0; i < BarCount; i++)
                {
                    _targets[i] = 0.05;
                    _bars[i].Opacity = 1.0;
                }
                break;

            case VisualizerMode.Recording:
                // Re-roll targets every 90–150 ms while recording.
                if (now >= _nextTargetAt)
                {
                    _nextTargetAt = now.AddMilliseconds(90 + (_rng.NextDouble() * 60));

                    if (_lastRealLevels is not null && _lastRealLevels.Length == BarCount)
                    {
                        // Real audio path: realAmplitude × center_weight × (1 + jitter).
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
                break;

            default:
                for (int i = 0; i < BarCount; i++)
                {
                    _targets[i] = 0.05;
                }
                break;
        }

        // Damped approach with per-bar rate variation so bars don't move in
        // lockstep.
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

        // Post-paste snapshot — flash Confirmed or hold Error, then back to Idle.
        if (snapshot.PostPaste is { } post)
        {
            if (post.Pasted)
            {
#pragma warning disable CA1848, CA1873
                _logger.LogDebug("Pasted into {App}.", post.TargetApp);
#pragma warning restore CA1848, CA1873
                ShowConfirmed();
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
            case AppState.Armed:
                TransitionTo(PillVisual.Armed);
                break;
            case AppState.Recording:
                TransitionTo(PillVisual.Recording);
                break;
            case AppState.Transcribing:
                TransitionTo(PillVisual.Transcribing);
                break;
            default:
                // Idle / Cancelled — back to the handle, unless a post-paste
                // hold is currently running.
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

        // Recording flag + visualizer mode drive the motion model.
        _isRecording = next == PillVisual.Recording;
        _visualizerMode = next switch
        {
            PillVisual.Armed => VisualizerMode.Dots,
            PillVisual.Recording => VisualizerMode.Recording,
            PillVisual.Transcribing => VisualizerMode.Shimmer,
            _ => VisualizerMode.Off,
        };
        if (previous == PillVisual.Transcribing)
        {
            // Leaving the shimmer — restore full opacity on every bar.
            for (int i = 0; i < BarCount; i++)
            {
                if (_bars[i] is { } bar)
                {
                    bar.Opacity = 1.0;
                }
            }
        }
        if (next is PillVisual.Idle or PillVisual.Hidden)
        {
            ResetBars();
        }
        if (previous == PillVisual.Confirmed)
        {
            // Drop the mint flash (covers the static reduce-motion hold too).
            FlashOverlay.BeginAnimation(OpacityProperty, null);
            FlashOverlay.Opacity = 0;
        }

        // Dock's record glyph reflects FSM state: dot when not recording,
        // filled red square when actively recording (so the toggle's affordance
        // matches "press to stop").
        UpdateRecordGlyph(next);
        // NOTE: do NOT auto-dismiss RecordNudgePopup on Recording transition.
        // ToggleFromTray dispatches the recording snapshot within a few ms of
        // the click, so a dismiss-on-Recording rule killed the nudge before
        // the user could read it. The 2 s timer in ShowRecordNudge is the
        // only dismissal path now.

        UpdateContentVisibility();
        ApplyGeometry(animated: true);

        // Pill-level appear/disappear.
        if (next == PillVisual.Hidden && previous != PillVisual.Hidden)
        {
            FadePillOut();
        }
        else if (next != PillVisual.Hidden && previous == PillVisual.Hidden)
        {
            FadePillIn();
        }
    }

    /// <summary>
    /// Crossfades the capsule's centre content to match the visual state and the
    /// dock: bars for Armed/Recording/Transcribing, the error row for Error, and
    /// the hotkey hint while the dock is open and nothing else needs the centre.
    /// </summary>
    private void UpdateContentVisibility()
    {
        bool dock = _dockOpen || _isPinned;
        bool bars = _currentVisual is PillVisual.Armed or PillVisual.Recording or PillVisual.Transcribing;
        bool error = _currentVisual == PillVisual.Error;
        SetShown(VisualizerCanvas, bars);
        SetShown(ErrorContent, error);
        SetShown(HintLabel, dock && !bars && !error);
    }

    private void SetShown(FrameworkElement target, bool shown)
    {
        if (_shown.TryGetValue(target, out var current) && current == shown)
        {
            return;
        }
        _shown[target] = shown;
        FadeElement(target, fadeIn: shown);
    }

    private static void FadeElement(FrameworkElement target, bool fadeIn)
    {
        target.IsHitTestVisible = fadeIn;
        // No From — animate from the current (possibly mid-animation) opacity
        // so a direction flip mid-crossfade doesn't jump.
        var anim = new DoubleAnimation
        {
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
                ResetBars();
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // ── Capsule geometry ────────────────────────────────────────────────────

    /// <summary>
    /// Single source of truth for the capsule's target size and the dock reveal,
    /// derived from the visual state and the dock/pin flags. Animates the three
    /// DPs (expand 180 ms ease-out, collapse 120 ms ease-in; instant under
    /// reduce-animations) and the corner buttons.
    /// </summary>
    private void ApplyGeometry(bool animated)
    {
        bool dock = _dockOpen || _isPinned;
        bool handleState = _currentVisual is PillVisual.Idle or PillVisual.Confirmed or PillVisual.Hidden;

        double h = handleState ? (dock ? ChipH : HandleH) : ChipH;
        double w = dock
            ? HoverW
            : _currentVisual switch
            {
                PillVisual.Armed or PillVisual.Recording or PillVisual.Transcribing => RecordW,
                PillVisual.Error => Math.Max(RecordW, MeasureErrorWidth() + 40),
                _ => HandleW,
            };
        double reveal = dock ? DockH : 0;

        bool expanding = w > CapsuleWidth || h > CapsuleHeight || reveal > DockReveal;
        int ms = !animated ? 0 : (expanding ? ExpandMs : CollapseMs);
        var mode = expanding ? EasingMode.EaseOut : EasingMode.EaseIn;

        AnimateDp(CapsuleWidthProperty, w, ms, mode);
        AnimateDp(CapsuleHeightProperty, h, ms, mode);
        AnimateDp(DockRevealProperty, reveal, ms, mode);
        UpdateCapsuleCorners();
        AnimateCornerButtons(dock);
    }

    private void AnimateDp(DependencyProperty dp, double to, int ms, EasingMode mode)
    {
        if (_reduceAnimations || ms <= 0)
        {
            // Clear any running clock first — a held animation would otherwise
            // mask the local value we're about to set.
            BeginAnimation(dp, null);
            SetValue(dp, to);
            return;
        }
        // No From — animates from the current (possibly mid-flight) value so
        // a hover-in/out flip never jumps.
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = mode },
        };
        BeginAnimation(dp, anim);
    }

    private void ApplyCapsuleWidth(double v)
    {
        if (PillSurface is null)
        {
            return;
        }
        PillSurface.Width = v;
        UpdateHoverPad();
    }

    private void ApplyCapsuleHeight(double v)
    {
        if (PillSurface is null)
        {
            return;
        }
        PillSurface.Height = v;
        UpdateCapsuleCorners();
        UpdateHoverPad();
    }

    private void ApplyDockReveal(double v)
    {
        if (PillSurface is null)
        {
            return;
        }
        PillSurface.Margin = new Thickness(0, 0, 0, v);
        DockDrawer.Opacity = v / DockH;
        DockTransform.Y = DockH - v;
        UpdateHoverPad();
    }

    /// <summary>
    /// r = min(18, h/2) so an 8-tall handle gets 4 px corners (never 18 on an
    /// 8-tall Border); bottom corners square off while the dock is open so chip
    /// and dock read as one continuous shape. Mirrored onto the flash overlay.
    /// </summary>
    private void UpdateCapsuleCorners()
    {
        double r = Math.Min(18, CapsuleHeight / 2);
        bool dock = _dockOpen || _isPinned;
        double rb = dock ? 0 : r;
        var corners = new CornerRadius(r, r, rb, rb);
        PillSurface.CornerRadius = corners;
        FlashOverlay.CornerRadius = corners;
    }

    private void UpdateHoverPad()
    {
        HoverPad.Width = Math.Min(HoverW, CapsuleWidth + HoverPadSlackW);
        HoverPad.Height = Math.Min(ChipH + DockH, CapsuleHeight + DockReveal + HoverPadSlackH);
    }

    private double MeasureErrorWidth()
    {
        ErrorContent.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return ErrorContent.DesiredSize.Width;
    }

    // ── Post-paste states ───────────────────────────────────────────────────

    private void ShowConfirmed()
    {
        // Collapse to the handle immediately; the mint flash is the only
        // confirmation cue (no "Pasted into …" text).
        TransitionTo(PillVisual.Confirmed);
        FlashOverlay.BeginAnimation(OpacityProperty, null);
        if (_reduceAnimations)
        {
            // Static hold — cleared when the post-paste timer returns to Idle.
            FlashOverlay.Opacity = 0.7;
        }
        else
        {
            FlashOverlay.Opacity = 0.9;
            FlashOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.9,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(FlashMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        StartPostPasteHold(TimeSpan.FromMilliseconds(FlashMs));
    }

    private void ShowError(string reason)
    {
        // Text first so MeasureErrorWidth (via ApplyGeometry) sees the reason.
        ErrorText.Text = reason;
        TransitionTo(PillVisual.Error);
        // Re-fit the capsule when a second error lands while already in Error
        // (TransitionTo is a no-op in that case).
        ApplyGeometry(animated: true);
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
            TransitionTo(PillVisual.Idle);
        };
        _postPasteTimer.Start();
    }

    // ── Positioning (multi-monitor sticky from CLAUDE.md) ───────────────────
    //
    // The window is a fixed 352×84; everything positional works on the ANCHOR
    // = bottom-centre of the idle handle, which sits PadBottom above the
    // window's bottom edge. Default anchor = (work-area centre X, work-area
    // bottom − EdgeGap), which puts the window's bottom edge exactly on the
    // work-area bottom (never onto the taskbar).

    private WpfPoint GetAnchor() => new(Left + (Width / 2), Top + Height - PadBottom);

    private void SetAnchor(double x, double y)
    {
        Left = x - (Width / 2);
        Top = y - Height + PadBottom;
    }

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
            SetAnchor(
                s.WorkingArea.Left + (s.WorkingArea.Width / 2.0),
                s.WorkingArea.Bottom - EdgeGap);
        }

        Show();
    }

    /// <summary>
    /// Positions the bar on the given monitor — at the user's remembered anchor
    /// for that monitor if one exists this session, otherwise at the default
    /// (bottom-centre of the work area, EdgeGap above the bottom edge). Does not
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
            _logger.LogDebug("Pill placing on {Device} at remembered anchor ({X:F0},{Y:F0}).",
                miEx.szDevice, remembered.X, remembered.Y);
#pragma warning restore CA1848, CA1873
            SetAnchor(remembered.X, remembered.Y);
            return;
        }

        double scale = 1.0;
        if (GetDpiForMonitor(hMon, MonitorDpiType.EffectiveDpi, out uint dpiX, out _) == 0
            && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        double workLeftDip = miEx.rcWork.Left / scale;
        double workWidthDip = (miEx.rcWork.Right - miEx.rcWork.Left) / scale;
        double workBottomDip = miEx.rcWork.Bottom / scale;

        SetAnchor(workLeftDip + (workWidthDip / 2), workBottomDip - EdgeGap);

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

    // ── Drag ────────────────────────────────────────────────────────────────

    private void OnPillMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        // Skip when the user is clicking on a Pin / Record button — let the
        // button's Click handler do its thing.
        if (IsClickOnButton(e.OriginalSource as DependencyObject))
        {
            return;
        }
        // Pin = positional lock: when pinned, the bar cannot be dragged. User
        // must unpin to move it again.
        if (_isPinned)
        {
            return;
        }

        _isDragging = true;
        try
        {
            // DragMove moves the fixed window; the anchor is re-derived below.
            DragMove();
        }
        finally
        {
            _isDragging = false;
        }

        // Capture the new anchor under whatever monitor we landed on.
        var device = GetCurrentMonitorDeviceName();
        if (device is not null)
        {
            var anchor = GetAnchor();
            _monitorPositions[device] = anchor;
#pragma warning disable CA1848, CA1873
            _logger.LogDebug("Pill drag end → remembered anchor ({X:F0},{Y:F0}) on {Device}.",
                anchor.X, anchor.Y, device);
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

    // ── Hover-expand + dock drawer ──────────────────────────────────────────
    //
    // On pointer enter (or while pinned): capsule grows to 320×36, the dock
    // slides out below it, Pin + Magic-wand corner buttons fade in (Pin
    // de-rotates -12°→0°) and the hotkey hint shows.
    //
    // On pointer leave: reverses — but stays open if pinned or while the mic
    // picker popup is open.
    //
    // Pin click latches "always open" — chip + dock + corner buttons stay
    // visible after the cursor leaves until the user clicks Pin again.

    private bool _isPinned;
    private bool _dockOpen;
    // True while the mic-chooser popup is open. Treated as "pinned for hover"
    // so the dock doesn't auto-close mid-pick (cursor enters the popup → pill
    // MouseLeave would otherwise fire and slam the dock shut).
    private bool _pickerOpen;
    private IPrefsStoreBridge? _prefsBridge;
    private IAudioRecorderBridge? _audioBridge;
    private bool _reduceAnimations;

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
    /// True → capsule resizes snap instead of tweening and the transcribing
    /// shimmer holds a static opacity. Content crossfades stay.
    /// </summary>
    public void SetReduceAnimations(bool reduce)
    {
        _reduceAnimations = reduce;
    }

    private void OnPillMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        OpenDock();
    }

    private void OnPillMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Don't close while the user has the mic chooser open — moving the
        // cursor over the popup fires the pill's MouseLeave (documented WPF
        // behaviour). Popup's Closed handler re-evaluates on close. Pinned
        // keeps the chip + dock open too.
        if (!_pickerOpen && !_isPinned)
        {
            CloseDock();
        }
    }

    private void OpenDock()
    {
        if (_dockOpen)
        {
            return;
        }
        _dockOpen = true;
        RefreshDock();
    }

    private void CloseDock()
    {
        if (!_dockOpen)
        {
            return;
        }
        _dockOpen = false;
        RefreshDock();
    }

    private void RefreshDock()
    {
        bool dock = _dockOpen || _isPinned;
        DockDrawer.IsHitTestVisible = dock;
        UpdateContentVisibility();
        ApplyGeometry(animated: true);
    }

    private void AnimateCornerButtons(bool visible)
    {
        // While pinned, the pin button stays visible always so the user can
        // click it again to unpin. The buttons share a StackPanel, so the
        // magic-wand rides along — harmless (it's dormant anyway).
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
            _isPinned ? "Mint" : "PillIcon");
        // Compact record button visibility follows pin state so a persistent
        // record affordance sits in the top-left corner opposite the pin.
        CompactRecordButton.Visibility = _isPinned
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Drag cursor follows pinned state — SizeAll telegraphs "drag-anywhere"
        // when unpinned, Arrow when pinned (drag is disabled per the pin lock).
        PillSurface.Cursor = _isPinned
            ? System.Windows.Input.Cursors.Arrow
            : System.Windows.Input.Cursors.SizeAll;

        // Pinned keeps the chip expanded with the dock open. Unpinning while
        // the cursor has already left collapses; unpinning while still hovered
        // leaves the normal hover-open state in place.
        if (_isPinned)
        {
            RefreshDock();
        }
        else if (!IsMouseOver)
        {
            CloseDock();
        }
        else
        {
            RefreshDock();
        }
    }

    private void UpdateRecordGlyph(PillVisual state)
    {
        // Both record glyphs (dock + compact corner) follow the same state
        // pattern: muted dot when idle (tap to start), red rounded-square when
        // recording (tap to stop).
        bool recording = state == PillVisual.Recording;
        var brush = recording
            ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xEF, 0x53, 0x50))
            : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("PillTextMuted");

        // Dock record glyph (8×8): full-circle dot ↔ rounded square.
        double rDock = recording ? 1.5 : 4;
        RecordGlyph.RadiusX = rDock;
        RecordGlyph.RadiusY = rDock;
        RecordGlyph.Fill = brush;

        // Compact corner record glyph (10×10).
        double rCompact = recording ? 2 : 5;
        CompactRecordGlyph.RadiusX = rCompact;
        CompactRecordGlyph.RadiusY = rCompact;
        CompactRecordGlyph.Fill = brush;
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
        // 2s display per user feedback — 10s was too long and lingered after
        // the user had already moved on. The dismiss-on-Recording rule that
        // killed the earlier 6s/3s nudges was removed; pure timer is the
        // sole dismissal path.
        _nudgeTimer?.Stop();
        RecordNudgePopup.IsOpen = true;
        _nudgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
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
