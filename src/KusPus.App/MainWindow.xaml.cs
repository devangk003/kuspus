using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using KusPus.Core.Hotkeys;
using KusPus.Core.Settings;
using KusPus.Core.State;
using KusPus.Native;
using KusPus.Persistence;
using KusPus.Whisper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreVK = KusPus.Core.Hotkeys.VirtualKey;
// Aliases — System.Drawing.Brush / FontFamily leak in via UseWindowsForms and would
// conflict with System.Windows.Media.* here.
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushConverter = System.Windows.Media.BrushConverter;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace KusPus.App;

/// <summary>
/// Settings + History window per <c>docs/APP_DESIGN.md</c> §3. System chrome
/// (no custom title bar); the title bar tints dark via DWM immersive dark mode.
///
/// Cluster 9A scope: shell, sidebar nav, General tab (Hotkey display + Startup
/// toggle + Appearance segmented control). Toggle / segmented control write to
/// <see cref="IPrefsStore"/> but their side effects (HKCU\Run, live theme apply)
/// arrive in subsequent clusters.
///
/// Closing the window <see cref="Hide"/>s it (§3.1 + §8.5) — quit is only via the
/// tray menu.
/// </summary>
public partial class MainWindow : Window
{
    // ── DWM ─────────────────────────────────────────────────────────────────
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private readonly IPrefsStore _prefs;
    private readonly IHotkeyEngine _hotkey;
    private readonly IModelManager _models;
    private readonly IHistoryStore _history;
    private readonly AppCoordinator _coordinator;
    private readonly ILogger<MainWindow> _logger;
    private bool _loaded;
    private bool _allowClose;
    private IDisposable? _prefsSubscription;
    private IDisposable? _coordinatorSubscription;
    private bool _modelsRendered;
    private bool _historyRendered;

    // Listen-mode state — see 9B notes on suspending the global hook.
    private bool _isListening;
    private HotkeyChord? _savedChord;
    private readonly List<CoreVK> _pressedOrder = new();
    private readonly HashSet<CoreVK> _heldKeys = new();
    private IReadOnlyList<CoreVK> _bestModifiers = Array.Empty<CoreVK>();
    private CoreVK? _bestKey;

    // Audio-tab level meter — see 9E. MMDevice.AudioMeterInformation reports
    // zero unless an active recording session is open on the device (validated
    // against naudio/NAudio#160 + #347 + #507) so we open a lightweight
    // WasapiCapture for the duration the Audio tab is visible, compute peak
    // from samples, and decay between updates. UX: Discord-style horizontal
    // track + mint fill driven by `_currentPeak`, plus a peak-hold tick.
    private const double MeterTrackWidth = 200.0;
    private NAudio.CoreAudioApi.MMDeviceEnumerator? _mmEnumerator;
    private NAudio.CoreAudioApi.WasapiCapture? _audioMeterCapture;
    private DispatcherTimer? _meterTimer;
    private float _currentPeak;
    private float _peakHold;
    private bool _meterCaptureIsFloat;
    private int _meterBytesPerSample;

    public MainWindow(
        IPrefsStore prefs,
        IHotkeyEngine hotkey,
        IModelManager models,
        IHistoryStore history,
        AppCoordinator coordinator,
        ILogger<MainWindow>? logger = null)
    {
        _prefs = prefs;
        _hotkey = hotkey;
        _models = models;
        _history = history;
        _coordinator = coordinator;
        _logger = logger ?? NullLogger<MainWindow>.Instance;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    /// <summary>
    /// Opens the window on the named tab and brings it to the foreground.
    /// Called by <see cref="App"/> from the tray "Preferences…" menu item.
    /// </summary>
    public void ShowOn(string tabKey)
    {
        SelectTab(tabKey);
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Resolve theme preference ("auto" → OS, else literal) and tint title bar.
        ThemeApply.ApplyToWindow(hwnd, ThemeApply.Resolve(_prefs.Current.Ui.Theme));

        // 8 px Win 11 rounded outer corners per APP_DESIGN §3.1.
        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Reflect current PrefsStore state in the controls BEFORE wiring change
        // handlers — otherwise the initial Checked event would re-save the same
        // value and emit a Changes notification we don't want at startup.
        var s = _prefs.Current;
        ApplyHotkeyDisplay(s.Hotkey);
        AutostartToggle.IsChecked = s.Autostart;
        var themeButton = s.Ui.Theme switch
        {
            "light" => ThemeLight,
            "dark" => ThemeDark,
            _ => ThemeAuto,
        };
        themeButton.IsChecked = true;

        // Privacy tab initial state (9H + 13.1).
        OfflineToggle.IsChecked = s.Privacy.OfflineMode;
        CrashReportsToggle.IsChecked = s.Privacy.CrashReportsOptIn;
        UpdateOfflineSubtitle(s.Privacy.OfflineMode);
        ApplyCrashReportsGating(s.Privacy.OfflineMode);
        LogsPath.Text = AppPaths.LogsDir;

        // Sidebar footer live state (§13.3) — initial paint from current settings +
        // coordinator snapshot. Updates wired below via PrefsStore.Changes +
        // AppCoordinator.State.
        UpdateSidebarHotkey(s.Hotkey);
        UpdateSidebarStatus(AppState.Idle, s.Models.ActiveModelId);

        // About tab (9I) — assembly metadata + log path. Build-date/Git-SHA stamping
        // is set up by Phase 12's release pipeline; until then we fall back to the
        // assembly version + a "dev build" sentinel.
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
        var version = info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "—";
        AboutVersion.Text = $"Version {version}";
        var built = System.IO.File.GetLastWriteTime(asm.Location)
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        AboutBuildLine.Text = $"Built {built} · dev build";
        AboutLogsPath.Text = AppPaths.LogsDir;

        _loaded = true;

        // Re-apply theme whenever the user (or an external save) flips the
        // preference. Selecting Auto/Light/Dark in the segmented control triggers
        // PrefsStore.SaveAsync, which fires this through Changes.
        _prefsSubscription = _prefs.Changes.Subscribe(settings =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                var mode = ThemeApply.Resolve(settings.Ui.Theme);
                // Body brushes — DynamicResource-backed; replacement triggers re-resolve.
                ThemeTokens.Apply(System.Windows.Application.Current.Resources, mode);
                // Title-bar tint via DWM. SWP_FRAMECHANGED inside ApplyToWindow forces
                // the non-client area to repaint immediately.
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    ThemeApply.ApplyToWindow(hwnd, mode);
                }
                // Re-render code-built rows so they pull fresh brushes from Resources.
                ApplyHotkeyDisplay(settings.Hotkey);
                // §13.3 sidebar live binding — chord rebind + model swap re-render.
                UpdateSidebarHotkey(settings.Hotkey);
                UpdateSidebarStatus(_coordinator.Snapshot.State, settings.Models.ActiveModelId);
                // §13.1 Crash Reports gating must re-evaluate whenever Offline Mode flips.
                ApplyCrashReportsGating(settings.Privacy.OfflineMode);
                if (ContentModels.Visibility == Visibility.Visible)
                {
                    _modelsRendered = false;
                    RenderModelsTab();
                }
                if (ContentHistory.Visibility == Visibility.Visible)
                {
                    _historyRendered = false;
                    _ = RenderHistoryTabAsync();
                }
            });
        });

        // §13.3 sidebar status label — bound to AppCoordinator state snapshots.
        // Subscription marshalled to the dispatcher; coordinator emits on the
        // UI thread already (per AppCoordinator's threading model) but BeginInvoke
        // is cheap insurance and keeps the call site symmetric with PrefsStore.
        _coordinatorSubscription = _coordinator.State.Subscribe(snapshot =>
        {
            Dispatcher.BeginInvoke(() =>
                UpdateSidebarStatus(snapshot.State, _prefs.Current.Models.ActiveModelId));
        });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Cancel any in-flight rebind so the LL hook is restored before we hide;
        // otherwise the chord stays suspended after the window closes.
        if (_isListening)
        {
            CancelListenMode();
            ApplyHotkeyDisplay(_prefs.Current.Hotkey);
        }
        if (_allowClose)
        {
            _prefsSubscription?.Dispose();
            _prefsSubscription = null;
            _coordinatorSubscription?.Dispose();
            _coordinatorSubscription = null;
            return;
        }
        // §3.1 / §8.5: close hides; only the tray's Quit fully exits.
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Bypasses the hide-on-close contract for an actual app-exit close. Called
    /// from <see cref="App.OnExit"/>. Once flipped, the next <see cref="Window.Close"/>
    /// call is honoured.
    /// </summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    // ── Sidebar tab switching ───────────────────────────────────────────────

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        // XAML parser raises Checked for `IsChecked="True"` on TabGeneral BEFORE the
        // later-declared content panels (ContentGeneral, ContentAudio, …) have been
        // bound to their instance fields. Bail out until OnLoaded confirms the window
        // is fully wired — the initial visual state (General visible, rest Collapsed)
        // comes from the XAML's own `Visibility="Collapsed"` defaults.
        if (!_loaded)
        {
            return;
        }
        if (sender is not System.Windows.Controls.RadioButton rb)
        {
            return;
        }
        SelectTab(rb.Name switch
        {
            nameof(TabAudio) => "audio",
            nameof(TabModels) => "models",
            nameof(TabHistory) => "history",
            nameof(TabPrivacy) => "privacy",
            nameof(TabAbout) => "about",
            _ => "general",
        });
    }

    private void SelectTab(string key)
    {
        ContentGeneral.Visibility = key == "general" ? Visibility.Visible : Visibility.Collapsed;
        ContentAudio.Visibility = key == "audio" ? Visibility.Visible : Visibility.Collapsed;
        ContentModels.Visibility = key == "models" ? Visibility.Visible : Visibility.Collapsed;
        ContentHistory.Visibility = key == "history" ? Visibility.Visible : Visibility.Collapsed;
        ContentPrivacy.Visibility = key == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        ContentAbout.Visibility = key == "about" ? Visibility.Visible : Visibility.Collapsed;

        var tab = key switch
        {
            "audio" => TabAudio,
            "models" => TabModels,
            "history" => TabHistory,
            "privacy" => TabPrivacy,
            "about" => TabAbout,
            _ => TabGeneral,
        };
        if (tab.IsChecked != true)
        {
            tab.IsChecked = true;
        }

        // Cluster 9E: live-level polling only runs while Audio tab is showing.
        if (key == "audio")
        {
            StartAudioMeter();
        }
        else
        {
            StopAudioMeter();
        }

        if (key == "models" && !_modelsRendered)
        {
            RenderModelsTab();
        }
        if (key == "history" && !_historyRendered)
        {
            _ = RenderHistoryTabAsync();
        }
    }

    // ── Hotkey display + rebind (9B) ────────────────────────────────────────

    private void ApplyHotkeyDisplay(HotkeySettings hk)
    {
        var keys = new List<CoreVK>(hk.Modifiers);
        if (hk.KeyCode is { } k)
        {
            keys.Add(k);
        }
        RenderKeycaps(keys);
    }

    private void RenderKeycaps(IReadOnlyList<CoreVK> keys)
    {
        HotkeyKeycaps.Children.Clear();
        if (keys.Count == 0)
        {
            HotkeyKeycaps.Children.Add(BuildKeycap("…"));
            return;
        }
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0)
            {
                HotkeyKeycaps.Children.Add(new TextBlock
                {
                    Text = "+",
                    Foreground = Theme("MutedText"),
                    FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 10, 0),
                });
            }
            HotkeyKeycaps.Children.Add(BuildKeycap(FriendlyKey(keys[i])));
        }
    }

    private Border BuildKeycap(string label)
    {
        var border = new Border
        {
            Style = (Style)FindResource("Keycap"),
        };
        border.Child = new TextBlock
        {
            Text = label,
            FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = Theme("PrimaryText"),
        };
        return border;
    }

    private static string FriendlyKey(CoreVK k) => k switch
    {
        CoreVK.LeftCtrl => "LCtrl",
        CoreVK.RightCtrl => "RCtrl",
        CoreVK.LeftWin => "LWin",
        CoreVK.RightWin => "RWin",
        CoreVK.LeftAlt => "LAlt",
        CoreVK.RightAlt => "RAlt",
        CoreVK.LeftShift => "LShift",
        CoreVK.RightShift => "RShift",
        CoreVK.Space => "Space",
        CoreVK.Return => "Enter",
        CoreVK.Tab => "Tab",
        CoreVK.Escape => "Esc",
        CoreVK.Backspace => "Bksp",
        _ => k.ToString(),
    };

    /// <summary>
    /// Returns the live themed brush from <see cref="System.Windows.Application.Resources"/>.
    /// Code-built UI uses this so a runtime theme switch (which flips each brush's
    /// Color in place) propagates to dynamically-added elements too. Static so the
    /// static <c>BuildBundledBadge</c> can use it without an instance reference.
    /// </summary>
    private static WpfBrush Theme(string key) =>
        (WpfBrush)System.Windows.Application.Current.FindResource(key);

    // ── Listen mode ─────────────────────────────────────────────────────────

    private void OnHotkeyCardClick(object sender, MouseButtonEventArgs e)
    {
        if (_isListening)
        {
            return;
        }
        EnterListenMode();
        e.Handled = true;
    }

    private void EnterListenMode()
    {
        // Suspend the global LL hook for the duration of the rebind by handing it
        // an unreachable empty chord. PrefsStore is NOT touched so a cancel can
        // restore the original chord cleanly.
        var current = _prefs.Current.Hotkey;
        _savedChord = new HotkeyChord(current.Modifiers, current.KeyCode);
        _hotkey.SetChord(new HotkeyChord(Array.Empty<CoreVK>(), null));

        _isListening = true;
        _pressedOrder.Clear();
        _heldKeys.Clear();
        _bestModifiers = Array.Empty<CoreVK>();
        _bestKey = null;

        HotkeyListenBorder.Visibility = Visibility.Visible;
        HotkeyEyebrow.Foreground = Theme("Mint");
        HotkeyHint.Foreground = Theme("SecondaryText");
        RenderListeningHint();
        ConflictRow.Visibility = Visibility.Collapsed;
        RenderKeycaps(Array.Empty<CoreVK>());

        // Make the window receive PreviewKeyDown.
        Focus();

#pragma warning disable CA1848, CA1873
        _logger.LogDebug("Hotkey listen mode entered; LL hook suspended.");
#pragma warning restore CA1848, CA1873
    }

    private void CancelListenMode()
    {
        ExitListenModeVisuals();
        if (_savedChord is { } restore)
        {
            _hotkey.SetChord(restore);
        }
        _savedChord = null;
    }

    private void ExitListenModeVisuals()
    {
        _isListening = false;
        _heldKeys.Clear();
        _pressedOrder.Clear();
        HotkeyListenBorder.Visibility = Visibility.Collapsed;
        HotkeyEyebrow.Foreground = Theme("MutedText");
        // Restore the resting hint with literal text — no keycap needed here since
        // the rest message references no specific key.
        HotkeyHint.Inlines.Clear();
        HotkeyHint.Inlines.Add(new Run("Tap the picker, then press a new chord."));
        HotkeyHint.Foreground = Theme("DisabledText");
    }

    // §13.4 — inline keycap inside helper text. Builds:
    //   "Now press the keys you want to use… [ESC] to cancel"
    // where [ESC] is a mini Keycap (Cascadia Mono + KeycapBg). Implemented via
    // WPF Inline + InlineUIContainer so the keycap sits on the same baseline as
    // the surrounding muted text. Re-builds whenever listen mode is (re-)entered.
    private void RenderListeningHint()
    {
        HotkeyHint.Inlines.Clear();
        HotkeyHint.Inlines.Add(new Run("Now press the keys you want to use… "));
        var keycap = new Border
        {
            Background = Theme("KeycapBg"),
            BorderBrush = Theme("KeycapBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Child = new TextBlock
            {
                Text = "ESC",
                FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = Theme("PrimaryText"),
            },
        };
        HotkeyHint.Inlines.Add(new InlineUIContainer(keycap) { BaselineAlignment = BaselineAlignment.Center });
        HotkeyHint.Inlines.Add(new Run(" to cancel"));
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isListening)
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            CancelListenMode();
            ApplyHotkeyDisplay(_prefs.Current.Hotkey);
            e.Handled = true;
            return;
        }

        var vk = MapWpfKey(e);
        if (vk is null)
        {
            e.Handled = true;
            return;
        }

        if (_heldKeys.Add(vk.Value))
        {
            _pressedOrder.Add(vk.Value);
            SnapshotBest();
            RenderKeycaps(BuildPreview());
        }
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isListening)
        {
            return;
        }
        var vk = MapWpfKey(e);
        if (vk is null)
        {
            e.Handled = true;
            return;
        }
        _heldKeys.Remove(vk.Value);
        if (_heldKeys.Count == 0)
        {
            CommitListenMode();
        }
        e.Handled = true;
    }

    private async void CommitListenMode()
    {
        var mods = _bestModifiers;
        var key = _bestKey;
        ExitListenModeVisuals();
        _savedChord = null;

        if (mods.Count == 0 && key is null)
        {
            // No keys were captured (shouldn't normally happen — defensive guard).
            ApplyHotkeyDisplay(_prefs.Current.Hotkey);
            return;
        }

        var current = _prefs.Current;
        var next = current with
        {
            Hotkey = new HotkeySettings
            {
                Modifiers = mods.ToArray(),
                KeyCode = key,
                HoldThresholdMs = current.Hotkey.HoldThresholdMs,
            },
        };

#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Hotkey rebound → mods=[{Mods}] key={Key}.",
            string.Join(",", mods), key);
#pragma warning restore CA1848, CA1873

        try
        {
            // PrefsStore.Changes pipes through AppCoordinator.OnSettingsChanged →
            // IHotkeyEngine.SetChord(new chord). No need to call SetChord here.
            await _prefs.SaveAsync(next).ConfigureAwait(true);
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for Hotkey rebind.");
#pragma warning restore CA1848, CA1873
        }

        ApplyHotkeyDisplay(next.Hotkey);
        ShowConflictIfAny(mods, key);
    }

    /// <summary>
    /// Updates <see cref="_bestModifiers"/> / <see cref="_bestKey"/> to reflect the
    /// largest chord seen so far during this listen pass. Modifiers are the keys with
    /// VK codes in the modifier range; the chord key is the first non-modifier pressed.
    /// </summary>
    private void SnapshotBest()
    {
        var mods = new List<CoreVK>();
        CoreVK? chordKey = null;
        foreach (var k in _pressedOrder)
        {
            if (IsModifier(k))
            {
                if (!mods.Contains(k))
                {
                    mods.Add(k);
                }
            }
            else if (chordKey is null)
            {
                chordKey = k;
            }
        }
        if (mods.Count >= _bestModifiers.Count && (chordKey is not null || _bestKey is null))
        {
            _bestModifiers = mods;
            _bestKey = chordKey;
        }
    }

    private List<CoreVK> BuildPreview()
    {
        var keys = new List<CoreVK>(_bestModifiers);
        if (_bestKey is { } k)
        {
            keys.Add(k);
        }
        return keys;
    }

    private static bool IsModifier(CoreVK k) => k is
        CoreVK.Shift or CoreVK.Control or CoreVK.Alt
        or CoreVK.LeftShift or CoreVK.RightShift
        or CoreVK.LeftCtrl or CoreVK.RightCtrl
        or CoreVK.LeftAlt or CoreVK.RightAlt
        or CoreVK.LeftWin or CoreVK.RightWin;

    private static CoreVK? MapWpfKey(System.Windows.Input.KeyEventArgs e)
    {
        // For modifier keys WPF reports the generic Key (e.g. Key.LeftCtrl, Key.LWin)
        // which round-trips through KeyInterop to the matching VK_*. VirtualKey
        // values intentionally match Win32 VK_* codes.
        Key effective = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(effective);
        if (vk == 0)
        {
            return null;
        }
        // Filter out keys we can't represent. CoreVK is a ushort enum; check defined.
        if (Enum.IsDefined(typeof(CoreVK), (ushort)vk))
        {
            return (CoreVK)vk;
        }
        return null;
    }

    // ── Conflict detection (§3.3) ───────────────────────────────────────────

    private void ShowConflictIfAny(IReadOnlyList<CoreVK> mods, CoreVK? key)
    {
        var conflict = DetectConflict(mods, key);
        if (conflict is null)
        {
            ConflictRow.Visibility = Visibility.Collapsed;
            return;
        }
        ConflictText.Text = conflict;
        ConflictRow.Visibility = Visibility.Visible;
    }

    private static string? DetectConflict(IReadOnlyList<CoreVK> mods, CoreVK? key)
    {
        bool win = mods.Contains(CoreVK.LeftWin) || mods.Contains(CoreVK.RightWin);
        bool ctrl = mods.Contains(CoreVK.LeftCtrl) || mods.Contains(CoreVK.RightCtrl);
        bool alt = mods.Contains(CoreVK.LeftAlt) || mods.Contains(CoreVK.RightAlt);
        bool shift = mods.Contains(CoreVK.LeftShift) || mods.Contains(CoreVK.RightShift);

        // Single-modifier sentinels — Windows treats these as "tap to open Start".
        if (mods.Count == 1 && key is null && win)
        {
            return "Win on its own opens Start. Add a second modifier or a key.";
        }

        if (win && !ctrl && !alt && !shift && key is { } k1)
        {
            return k1 switch
            {
                CoreVK.L => "Win + L locks the screen.",
                CoreVK.D => "Win + D shows the desktop.",
                CoreVK.E => "Win + E opens File Explorer.",
                CoreVK.R => "Win + R opens Run.",
                CoreVK.I => "Win + I opens Settings.",
                CoreVK.A => "Win + A opens the Action Center / Quick Settings.",
                CoreVK.V => "Win + V opens Clipboard History.",
                CoreVK.X => "Win + X opens the power-user menu.",
                CoreVK.S => "Win + S opens Search.",
                CoreVK.P => "Win + P opens the Project picker.",
                CoreVK.B => "Win + B focuses the tray.",
                CoreVK.G => "Win + G opens the Xbox Game Bar.",
                CoreVK.Tab => "Win + Tab opens Task View.",
                CoreVK.Space => "Win + Space switches input language.",
                _ => null,
            };
        }
        if (ctrl && alt && !win && !shift && key == CoreVK.Delete)
        {
            return "Ctrl + Alt + Del is reserved by Windows.";
        }
        if (alt && !ctrl && !win && !shift && key == CoreVK.F4)
        {
            return "Alt + F4 closes the active window.";
        }
        if (alt && !ctrl && !win && !shift && key == CoreVK.Tab)
        {
            return "Alt + Tab switches windows.";
        }
        if (ctrl && shift && !win && !alt && key == CoreVK.Escape)
        {
            return "Ctrl + Shift + Esc opens Task Manager.";
        }
        if (ctrl && !shift && !win && !alt && key == CoreVK.Escape)
        {
            return "Ctrl + Esc opens the Start menu.";
        }
        return null;
    }

    // ── Audio-tab level meter (9E) ──────────────────────────────────────────
    //
    // Uses NAudio's MMDevice.AudioMeterInformation, which reports the device's
    // hardware peak without opening a capture session. Polling at ~15 Hz on a
    // DispatcherTimer mirrors the pill visualizer rate without the per-channel
    // RMS pipeline. Bars are seeded on first activation.

    private void StartAudioMeter()
    {
        if (_audioMeterCapture is null)
        {
            try
            {
                _mmEnumerator ??= new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var device = _mmEnumerator.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Capture,
                    NAudio.CoreAudioApi.Role.Communications);
                AudioDeviceTitle.Text = device.FriendlyName ?? "Microphone";

                var capture = new NAudio.CoreAudioApi.WasapiCapture(device);
                _meterCaptureIsFloat = capture.WaveFormat.Encoding ==
                    NAudio.Wave.WaveFormatEncoding.IeeeFloat;
                _meterBytesPerSample = capture.WaveFormat.BitsPerSample / 8;
                capture.DataAvailable += OnMeterDataAvailable;
                capture.StartRecording();
                _audioMeterCapture = capture;
            }
            catch (COMException ex)
            {
#pragma warning disable CA1848, CA1873
                _logger.LogWarning(ex, "Audio meter setup failed — no capture device?");
#pragma warning restore CA1848, CA1873
                AudioDeviceTitle.Text = "No microphone detected";
                return;
            }
            catch (NAudio.MmException ex)
            {
#pragma warning disable CA1848, CA1873
                _logger.LogWarning(ex, "WasapiCapture init failed — privacy block / busy device?");
#pragma warning restore CA1848, CA1873
                AudioDeviceTitle.Text = "Microphone busy or blocked";
                return;
            }
        }
        if (_meterTimer is null)
        {
            _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(66), // ~15 Hz
            };
            _meterTimer.Tick += OnMeterTick;
        }
        _meterTimer.Start();
    }

    private void StopAudioMeter()
    {
        _meterTimer?.Stop();
        if (_audioMeterCapture is not null)
        {
            try { _audioMeterCapture.StopRecording(); }
            catch (NAudio.MmException) { /* shutting down */ }
            _audioMeterCapture.DataAvailable -= OnMeterDataAvailable;
            _audioMeterCapture.Dispose();
            _audioMeterCapture = null;
        }
        _currentPeak = 0;
    }

    private void OnMeterDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }
        float peak = 0;
        var buf = e.Buffer;
        if (_meterCaptureIsFloat && _meterBytesPerSample == 4)
        {
            int sampleCount = e.BytesRecorded / 4;
            for (int i = 0; i < sampleCount; i++)
            {
                float s = BitConverter.ToSingle(buf, i * 4);
                float a = Math.Abs(s);
                if (a > peak)
                {
                    peak = a;
                }
            }
        }
        else if (_meterBytesPerSample == 2)
        {
            int sampleCount = e.BytesRecorded / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(buf, i * 2);
                float a = Math.Abs(s) / 32768f;
                if (a > peak)
                {
                    peak = a;
                }
            }
        }
        // Atomic float write — UI tick reads it next frame.
        _currentPeak = Math.Max(_currentPeak, peak);
    }

    private void OnMeterTick(object? sender, EventArgs e)
    {
        // Snapshot + decay so the fill recedes smoothly when audio stops.
        float peak = _currentPeak;
        _currentPeak *= 0.6f;

        // Peak-hold latches to the highest level seen recently then decays slower
        // than the fill so the tick lingers as a visual memory of the loudest moment.
        if (peak > _peakHold)
        {
            _peakHold = peak;
        }
        else
        {
            _peakHold *= 0.94f;
        }

        // Mic input rarely peaks past ~0.6 in normal speech — gain so the meter
        // reads "alive" without speaking into the mic at point-blank range.
        double normalized = Math.Clamp(peak * 2.5, 0.0, 1.0);
        double normalizedHold = Math.Clamp(_peakHold * 2.5, 0.0, 1.0);

        LevelMeterFill.Width = normalized * MeterTrackWidth;
        // Peak tick: fade out when hold is near zero; otherwise visible.
        LevelMeterPeak.Margin = new Thickness(
            Math.Max(0, (normalizedHold * MeterTrackWidth) - 1), 0, 0, 0);
        LevelMeterPeak.Opacity = normalizedHold > 0.04 ? 0.7 : 0;
    }

    // ── Models tab (9F) ─────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> SpeedLabels = new(StringComparer.Ordinal)
    {
        ["ggml-tiny.en"] = "Fastest",
        ["ggml-base.en"] = "Fast",
        ["ggml-small.en"] = "Balanced",
        ["ggml-medium.en"] = "Accurate",
        ["ggml-large-v3"] = "Most accurate · multilingual",
    };

    private void RenderModelsTab()
    {
        _modelsRendered = true;
        var activeId = _prefs.Current.Models.ActiveModelId;
        var manifest = _models.Manifest;

        var active = manifest.Models.FirstOrDefault(m => m.Id == activeId);
        ActiveModelText.Text = active is not null
            ? $"{active.DisplayName} · {FormatSize(active.SizeBytes)} · {SpeedLabel(active.Id)}"
            : "(none)";

        ModelsList.Children.Clear();
        for (int i = 0; i < manifest.Models.Count; i++)
        {
            var m = manifest.Models[i];
            bool installed = File.Exists(System.IO.Path.Combine(AppPaths.ModelsDir, m.FileName));
            bool isActive = m.Id == activeId;
            ModelsList.Children.Add(BuildModelRow(m, installed, isActive, isLast: i == manifest.Models.Count - 1));
        }
    }

    private Border BuildModelRow(ModelDescriptor m, bool installed, bool isActive, bool isLast)
    {
        var radio = new System.Windows.Controls.RadioButton
        {
            GroupName = "ActiveModel",
            IsChecked = isActive,
            IsEnabled = installed,
            Tag = m.Id,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        radio.Checked += OnModelRadioChecked;

        var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = m.DisplayName,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Theme("PrimaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (m.Bundled)
        {
            titleRow.Children.Add(BuildBundledBadge());
        }

        var subtitle = new TextBlock
        {
            Text = $"{FormatSize(m.SizeBytes)} · {SpeedLabel(m.Id)}",
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11.5,
            Foreground = Theme("MutedText"),
            Margin = new Thickness(0, 3, 0, 0),
        };

        var labelStack = new StackPanel();
        labelStack.Children.Add(titleRow);
        labelStack.Children.Add(subtitle);

        var statusText = installed
            ? (isActive ? "Active" : "Installed")
            : "Not installed";
        var statusBrush = installed
            ? (isActive
                ? Theme("Mint")
                : Theme("MutedText"))
            : Theme("DisabledText");
        var status = new TextBlock
        {
            Text = statusText,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = statusBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(radio, 0);
        Grid.SetColumn(labelStack, 1);
        Grid.SetColumn(status, 2);
        grid.Children.Add(radio);
        grid.Children.Add(labelStack);
        grid.Children.Add(status);

        var border = new Border
        {
            Style = (Style)FindResource("RowCard"),
            Margin = new Thickness(0, 0, 0, isLast ? 0 : 1),
        };
        border.Child = grid;
        return border;
    }

    private static Border BuildBundledBadge()
    {
        return new Border
        {
            Background = Theme("MintTint"),
            BorderBrush = Theme("MintBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Bundled",
                FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = Theme("Mint"),
            },
        };
    }

    private async void OnModelRadioChecked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string id)
        {
            return;
        }
        var current = _prefs.Current;
        if (current.Models.ActiveModelId == id)
        {
            return;
        }
        var next = current with { Models = current.Models with { ActiveModelId = id } };
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Active model changed → {Id}.", id);
#pragma warning restore CA1848, CA1873
        try
        {
            await _prefs.SaveAsync(next).ConfigureAwait(true);
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for ActiveModelId.");
#pragma warning restore CA1848, CA1873
            return;
        }
        // Re-render to update Active row + status pills.
        _modelsRendered = false;
        RenderModelsTab();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024 / 1024:F1} GB";
        }
        return $"{bytes / 1024 / 1024} MB";
    }

    private static string SpeedLabel(string id) =>
        SpeedLabels.TryGetValue(id, out var s) ? s : "—";

    // ── History tab (9G) ────────────────────────────────────────────────────

    private async Task RenderHistoryTabAsync()
    {
        _historyRendered = true;
        try
        {
            var rows = await _history.SearchAsync(null, 50, 0).ConfigureAwait(true);
            HistoryStats.Text = $"{rows.Count} transcripts loaded.";
            HistoryList.Children.Clear();
            foreach (var r in rows)
            {
                HistoryList.Children.Add(BuildHistoryRow(r));
            }
            if (rows.Count == 0)
            {
                HistoryList.Children.Add(new TextBlock
                {
                    Text = "No transcripts yet. Dictate something to populate history.",
                    FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 12,
                    Foreground = Theme("MutedText"),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 10, 0, 0),
                });
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Failed to query history.");
#pragma warning restore CA1848, CA1873
            HistoryStats.Text = "History unavailable.";
        }
    }

    private Border BuildHistoryRow(TranscriptRecord r)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        bool ok = r.Status == TranscriptStatus.Ok;
        var dot = new Ellipse
        {
            Width = 7, Height = 7,
            Fill = ok ? Theme("Mint") : Theme("ErrorRed"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(dot, 0);

        var stack = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = FormatRelative(r.Timestamp),
            FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            Foreground = Theme("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(r.TargetApp))
        {
            header.Children.Add(new TextBlock
            {
                Text = "·",
                FontSize = 11,
                Foreground = Theme("DisabledText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            });
            header.Children.Add(new TextBlock
            {
                Text = r.TargetApp,
                FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = Theme("SecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock
        {
            Text = r.Text,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            Foreground = ok ? Theme("PrimaryText") : Theme("ErrorRed"),
            FontStyle = ok ? FontStyles.Normal : FontStyles.Italic,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 540,
            Margin = new Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(stack, 1);

        var footer = new TextBlock
        {
            Text = $"{r.Model} · {r.Duration.TotalSeconds:F1}s",
            FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 10.5,
            Foreground = Theme("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(footer, 2);

        grid.Children.Add(dot);
        grid.Children.Add(stack);
        grid.Children.Add(footer);

        return new Border
        {
            Style = (Style)FindResource("RowCard"),
            Margin = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private static string FormatRelative(DateTimeOffset ts)
    {
        var delta = DateTimeOffset.UtcNow - ts.ToUniversalTime();
        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }
        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h ago";
        }
        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays}d ago";
        }
        return ts.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Privacy tab (9H) ────────────────────────────────────────────────────

    private async void OnOfflineChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        bool enabled = OfflineToggle.IsChecked == true;
        var current = _prefs.Current;
        if (current.Privacy.OfflineMode == enabled)
        {
            return;
        }
        var next = current with { Privacy = current.Privacy with { OfflineMode = enabled } };
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Offline mode → {Value}.", enabled);
#pragma warning restore CA1848, CA1873
        UpdateOfflineSubtitle(enabled);
        try
        {
            await _prefs.SaveAsync(next).ConfigureAwait(true);
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for OfflineMode.");
#pragma warning restore CA1848, CA1873
        }
    }

    private async void OnCrashReportsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        bool enabled = CrashReportsToggle.IsChecked == true;
        var current = _prefs.Current;
        if (current.Privacy.CrashReportsOptIn == enabled)
        {
            return;
        }
        var next = current with { Privacy = current.Privacy with { CrashReportsOptIn = enabled } };
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Crash reports → {Value}.", enabled);
#pragma warning restore CA1848, CA1873
        try
        {
            await _prefs.SaveAsync(next).ConfigureAwait(true);
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for CrashReportsOptIn.");
#pragma warning restore CA1848, CA1873
        }
    }

    private void UpdateOfflineSubtitle(bool enabled)
    {
        OfflineSubtitle.Text = enabled
            ? "Killswitch enabled — no network requests will be made."
            : "KusPus may reach model and crash-report servers when needed.";
    }

    // §13.1 — Crash Reports state-dependent enablement. When Offline Mode is ON,
    // CrashReporter.ApplySettings shuts Sentry down regardless of this toggle, so
    // the toggle has to render disabled and the subtitle must explain why.
    // CrashReportsToggle.IsChecked is preserved across the disable cycle: a user
    // who opts in but turns on Offline Mode sees their intent honored when they
    // later turn Offline back off.
    private void ApplyCrashReportsGating(bool offlineModeOn)
    {
        CrashReportsToggle.IsEnabled = !offlineModeOn;
        CrashReportsToggle.Opacity = offlineModeOn ? 0.5 : 1.0;
        CrashReportsSubtitle.Text = offlineModeOn
            ? "Disabled while Offline Mode is on."
            : "Anonymous, opt-in. Never includes transcripts, audio, or clipboard contents.";
    }

    // §13.3 — Sidebar status row binding. Updates the "Idle · tiny.en" label from
    // a coordinator state + an active model id. The active model id arrives from
    // PrefsStore as "ggml-tiny.en"; the sidebar shows the spec's short form
    // ("tiny.en") so the line stays compact at 200 px sidebar width.
    private void UpdateSidebarStatus(AppState state, string activeModelId)
    {
        string stateLabel = state switch
        {
            AppState.Idle => "Idle",
            AppState.Armed => "Armed",
            AppState.Recording => "Recording",
            AppState.Transcribing => "Transcribing",
            AppState.Cancelled => "Idle",
            _ => "Idle",
        };
        string modelShort = activeModelId.StartsWith("ggml-", StringComparison.Ordinal)
            ? activeModelId["ggml-".Length..]
            : activeModelId;
        StatusLabel.Text = $"{stateLabel} · {modelShort}";
    }

    // §13.3 — Sidebar hotkey glyph. Compact text form of the current chord
    // (e.g. "Ctrl+Win", "Ctrl+Alt+Space"). Survives a chord rebind because
    // PrefsStore.Changes calls this. Modifiers come first in the spec's order.
    private void UpdateSidebarHotkey(HotkeySettings hk)
    {
        if (hk.Modifiers.Count == 0 && hk.KeyCode is null)
        {
            SidebarHotkeyGlyph.Text = "—";
            return;
        }
        var parts = new List<string>(hk.Modifiers.Count + 1);
        foreach (var m in hk.Modifiers)
        {
            parts.Add(ShortKey(m));
        }
        if (hk.KeyCode is { } k)
        {
            parts.Add(ShortKey(k));
        }
        SidebarHotkeyGlyph.Text = string.Join("+", parts);
    }

    private static string ShortKey(CoreVK k) => k switch
    {
        CoreVK.LeftCtrl or CoreVK.RightCtrl or CoreVK.Control => "Ctrl",
        CoreVK.LeftWin or CoreVK.RightWin => "Win",
        CoreVK.LeftAlt or CoreVK.RightAlt or CoreVK.Alt => "Alt",
        CoreVK.LeftShift or CoreVK.RightShift or CoreVK.Shift => "Shift",
        CoreVK.Space => "Space",
        CoreVK.Return => "Enter",
        CoreVK.Tab => "Tab",
        CoreVK.Escape => "Esc",
        CoreVK.Backspace => "Bksp",
        _ => k.ToString(),
    };

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppPaths.LogsDir}\"",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Failed to open logs directory in Explorer.");
#pragma warning restore CA1848, CA1873
        }
    }

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/devangk003/kuspus",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Failed to open GitHub URL.");
#pragma warning restore CA1848, CA1873
        }
    }

    private void OnRerunOnboardingClick(object sender, RoutedEventArgs e)
    {
        var window = new OnboardingWindow(_prefs, _hotkey)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    // ── Setting writes ──────────────────────────────────────────────────────

    private async void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        var current = _prefs.Current;
        bool enabled = AutostartToggle.IsChecked == true;
        var next = current with { Autostart = enabled };
        if (next.Autostart == current.Autostart)
        {
            return;
        }
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Autostart toggled → {Value}.", enabled);
#pragma warning restore CA1848, CA1873
        try
        {
            // Write HKCU\Run first — if it fails (permissions, antivirus lock),
            // we'd rather NOT have a settings.json that lies about the registry.
            AutostartRegistry.Set(enabled);
            await _prefs.SaveAsync(next).ConfigureAwait(true);
        }
        catch (System.UnauthorizedAccessException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Autostart registry write blocked — UAC / antivirus?");
#pragma warning restore CA1848, CA1873
            AutostartToggle.IsChecked = current.Autostart;
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for Autostart.");
#pragma warning restore CA1848, CA1873
        }
    }

    private async void OnThemeChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        string theme =
            ThemeLight.IsChecked == true ? "light" :
            ThemeDark.IsChecked == true ? "dark" :
            "auto";
        var current = _prefs.Current;
        if (current.Ui.Theme == theme)
        {
            return;
        }
        var next = current with { Ui = current.Ui with { Theme = theme } };
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Theme set → {Theme}. (Live apply lands in 9C.)", theme);
#pragma warning restore CA1848, CA1873
        try
        {
            await _prefs.SaveAsync(next).ConfigureAwait(true);
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for Theme.");
#pragma warning restore CA1848, CA1873
        }
    }
}
