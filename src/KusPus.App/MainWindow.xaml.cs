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
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "MainWindow inherits from System.Windows.Window which owns its own lifecycle. All disposable fields are cleaned up in OnClosing (after _allowClose flips) — making MainWindow IDisposable would conflict with WPF's window-lifecycle model. Matches the same suppression on App.")]
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
    private readonly KusPus.Audio.IAudioRecorder _audio;
    private readonly IWhisperRunner _whisper;
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
        KusPus.Audio.IAudioRecorder audio,
        IWhisperRunner whisper,
        ILogger<MainWindow>? logger = null)
    {
        _prefs = prefs;
        _hotkey = hotkey;
        _models = models;
        _history = history;
        _coordinator = coordinator;
        _audio = audio;
        _whisper = whisper;
        _logger = logger ?? NullLogger<MainWindow>.Instance;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        // P0 mic-always-on bug fix: when the Preferences window is hidden via
        // its X button (hide-instead-of-close per §3.1), the Audio tab's
        // WasapiCapture must release the mic. Without this hook, the mic icon
        // stays in the system "in-use" tray indefinitely after the user closes
        // the window. IsVisibleChanged fires for both Hide() and Show().
        IsVisibleChanged += OnIsVisibleChanged;
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
        RefreshLogsSize();

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
            _testCts?.Cancel();
            _testCts?.Dispose();
            _testCts = null;
            StopAudioMeter();
            return;
        }
        // §3.1 / §8.5: close hides; only the tray's Quit fully exits.
        e.Cancel = true;
        Hide();
    }

    // P0 mic-bug fix: tie the level-meter WasapiCapture lifetime to the window's
    // visibility, not just to the tab-selection state. Without this, a user on
    // the Audio tab who clicks X keeps the mic in-use indefinitely.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            StopAudioMeter();
        }
        else if (ContentAudio.Visibility == Visibility.Visible)
        {
            // Reopened to the Audio tab — resume the meter.
            StartAudioMeter();
        }
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

    // Counterpart to Theme(): resolves a TextBlock Style from Application.Resources
    // so code-built TextBlocks can pick up the canonical Type.* roles instead of
    // re-declaring FontFamily / FontSize / FontWeight / Foreground inline.
    // §13.5 audit Wave C.
    private static Style TypeStyle(string key) =>
        (Style)System.Windows.Application.Current.FindResource(key);

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
        // LIVE indicator visible whenever the meter capture is open. Privacy
        // signal so the user knows the mic is actually in use.
        if (LiveIndicator is not null)
        {
            LiveIndicator.Visibility = Visibility.Visible;
        }
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
        if (LiveIndicator is not null)
        {
            LiveIndicator.Visibility = Visibility.Collapsed;
        }
        // Reset meter visuals so a paused meter doesn't show stale levels.
        if (LevelMeterFill is not null)
        {
            LevelMeterFill.Width = 0;
            LevelMeterPeak.Opacity = 0;
        }
    }

    // ── Test transcription (audit follow-up — restored from W1 placeholder) ────
    // State machine: Idle → Recording (5 s countdown) → Transcribing → Result/Error.
    // The level meter is paused during Recording to avoid double-opening the mic
    // (WasapiCapture shared mode usually allows it but a few drivers fail). When
    // the test finishes (success OR error), the meter resumes if the Audio tab
    // is still visible.

    private enum TestState { Idle, Recording, Transcribing, Result, Error }

    private TestState _testState = TestState.Idle;
    private DispatcherTimer? _testCountdownTimer;
    private int _testCountdownRemaining;
    private CancellationTokenSource? _testCts;

    private async void OnTestTranscriptionClick(object sender, RoutedEventArgs e)
    {
        if (_testState is TestState.Recording or TestState.Transcribing)
        {
            // In-flight test → button doubles as Cancel.
            _testCts?.Cancel();
            return;
        }
        await RunTestTranscriptionAsync().ConfigureAwait(true);
    }

    private async Task RunTestTranscriptionAsync()
    {
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;

        // Resolve the active model before we open the mic — fast-fail if missing.
        var modelId = _prefs.Current.Models.ActiveModelId;
        var customPath = _prefs.Current.Models.CustomModelPath;
        var resolved = _models.Resolve(modelId, customPath);
        if (!resolved.Success)
        {
            SetTestState(TestState.Error, resolved.Error ?? "Active model unavailable.");
            return;
        }

        // Pause meter to release the mic for the recorder.
        bool wasAudioTab = ContentAudio.Visibility == Visibility.Visible;
        StopAudioMeter();

        try
        {
            SetTestState(TestState.Recording, "Recording… 5 seconds remaining");
            var startResult = await _audio.StartAsync(ct).ConfigureAwait(true);
            if (!startResult.Success)
            {
                SetTestState(TestState.Error, startResult.Error ?? "Mic open failed.");
                return;
            }

            StartTestCountdown();
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
            StopTestCountdown();

            var stopResult = await _audio.StopAsync().ConfigureAwait(true);
            if (!stopResult.Success)
            {
                SetTestState(TestState.Error, stopResult.Error ?? "Mic stop failed.");
                return;
            }

            SetTestState(TestState.Transcribing, "Transcribing…");
            var transcript = await _whisper
                .TranscribeAsync(stopResult.Value!.WavPath, resolved.Value!, ct)
                .ConfigureAwait(true);

            try { File.Delete(stopResult.Value.WavPath); }
            catch (IOException) { /* best-effort cleanup of the temp wav */ }

            if (!transcript.Success)
            {
                SetTestState(TestState.Error, transcript.Error ?? "Transcribe failed.");
                return;
            }

            var text = string.IsNullOrWhiteSpace(transcript.Value)
                ? "(no speech detected)"
                : transcript.Value!.Trim();
            SetTestState(TestState.Result, text);
        }
        catch (OperationCanceledException)
        {
            StopTestCountdown();
            try { await _audio.StopAsync().ConfigureAwait(true); }
            catch { /* best-effort */ }
            SetTestState(TestState.Idle, "Click Test, speak for 5 seconds, see what KusPus heard.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Test transcription failed unexpectedly.");
#pragma warning restore CA1848, CA1873
            SetTestState(TestState.Error, "Unexpected error — see logs.");
        }
        finally
        {
            _testCts?.Dispose();
            _testCts = null;
            // Resume meter if user is still on the Audio tab and window is visible.
            if (wasAudioTab && IsVisible && ContentAudio.Visibility == Visibility.Visible)
            {
                StartAudioMeter();
            }
        }
    }

    private void StartTestCountdown()
    {
        _testCountdownRemaining = 5;
        _testCountdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _testCountdownTimer.Tick += (_, _) =>
        {
            _testCountdownRemaining--;
            if (_testCountdownRemaining <= 0)
            {
                _testCountdownTimer?.Stop();
                return;
            }
            if (_testState == TestState.Recording)
            {
                TestSubtitle.Text = $"Recording… {_testCountdownRemaining} second{(_testCountdownRemaining == 1 ? string.Empty : "s")} remaining";
            }
        };
        _testCountdownTimer.Start();
    }

    private void StopTestCountdown()
    {
        _testCountdownTimer?.Stop();
        _testCountdownTimer = null;
    }

    private void SetTestState(TestState state, string subtitleOrResult)
    {
        _testState = state;
        switch (state)
        {
            case TestState.Idle:
                TestSubtitle.Text = subtitleOrResult;
                TestButton.Content = "Test transcription";
                TestButton.Style = (Style)System.Windows.Application.Current.FindResource("Btn.Primary");
                TestButton.IsEnabled = true;
                TestResultContainer.Visibility = Visibility.Collapsed;
                break;
            case TestState.Recording:
            case TestState.Transcribing:
                TestSubtitle.Text = subtitleOrResult;
                TestButton.Content = "Cancel";
                TestButton.Style = (Style)System.Windows.Application.Current.FindResource("Btn.Ghost");
                TestButton.IsEnabled = true;
                TestResultContainer.Visibility = Visibility.Collapsed;
                break;
            case TestState.Result:
                TestSubtitle.Text = "Heard:";
                TestButton.Content = "Test again";
                TestButton.Style = (Style)System.Windows.Application.Current.FindResource("Btn.Secondary");
                TestButton.IsEnabled = true;
                TestResultText.Text = subtitleOrResult;
                TestResultText.Foreground = Theme("PrimaryText");
                TestResultContainer.Visibility = Visibility.Visible;
                break;
            case TestState.Error:
                TestSubtitle.Text = "Test failed.";
                TestButton.Content = "Retry";
                TestButton.Style = (Style)System.Windows.Application.Current.FindResource("Btn.Secondary");
                TestButton.IsEnabled = true;
                TestResultText.Text = subtitleOrResult;
                TestResultText.Foreground = Theme("ErrorRed");
                TestResultContainer.Visibility = Visibility.Visible;
                break;
        }
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

    // §13.5 P1-4 — per-model download state. Only entries for in-progress or
    // failed downloads exist; success/idle are derived from on-disk presence in
    // RenderModelsTab. CTS is held so Cancel can interrupt the HTTP stream.
    private sealed class ModelDownloadState
    {
        public CancellationTokenSource? Cts;
        public double Percent;
        public long BytesDone;
        public long BytesTotal;
        public string? Error;
    }

    private readonly Dictionary<string, ModelDownloadState> _modelDownloads =
        new(StringComparer.Ordinal);

    private void RenderModelsTab()
    {
        _modelsRendered = true;
        var activeId = _prefs.Current.Models.ActiveModelId;
        var manifest = _models.Manifest;

        // §13.5 P2-8: drop the unreachable "(none)" branch — ActiveModelId is
        // always a valid manifest id by data flow (DefaultSettings seeds it).
        var active = manifest.Models.First(m => m.Id == activeId);
        ActiveModelText.Text =
            $"{active.DisplayName} · {FormatSize(active.SizeBytes)} · {SpeedLabel(active.Id)}";

        ModelsList.Children.Clear();
        for (int i = 0; i < manifest.Models.Count; i++)
        {
            var m = manifest.Models[i];
            bool installed = File.Exists(System.IO.Path.Combine(AppPaths.ModelsDir, m.FileName));
            bool isActive = m.Id == activeId;
            ModelsList.Children.Add(BuildModelRow(m, installed, isActive, isLast: i == manifest.Models.Count - 1));
        }
    }

    // Model row — explicit action buttons per state, no radio button.
    // Per UI UX Pro Max convention for state-rich cards: visual state is carried
    // by (a) a 4 px left-edge accent strip + (b) the right-side CTA, NOT by a
    // radio toggle. Active card additionally gets a MintTint surface.
    //
    // State machine:
    //   Active        — MintTint card, mint accent, ACTIVE badge (no button — already in use)
    //   Installed     — neutral card, no accent, "Use this model" Btn.Primary
    //   Not installed — neutral card, no accent, "Download" Btn.Secondary
    //   Downloading   — neutral card, mint accent, progress + percent + Cancel ghost
    //   Failed        — neutral card, red accent, error text + Retry secondary
    private Border BuildModelRow(ModelDescriptor m, bool installed, bool isActive, bool isLast)
    {
        _modelDownloads.TryGetValue(m.Id, out var ds);
        bool isDownloading = ds?.Cts is not null;
        bool isFailed = ds?.Error is not null && ds.Cts is null;

        var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var titleText = new TextBlock
        {
            Text = m.DisplayName,
            Style = TypeStyle("Type.RowTitle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(titleText);
        if (m.Bundled)
        {
            titleRow.Children.Add(BuildBundledBadge());
        }
        if (isActive)
        {
            titleRow.Children.Add(BuildActiveBadge());
        }

        var subtitle = new TextBlock
        {
            Text = $"{FormatSize(m.SizeBytes)} · {SpeedLabel(m.Id)}",
            Style = TypeStyle("Type.RowSubtitle"),
        };

        var labelStack = new StackPanel();
        labelStack.Children.Add(titleRow);
        labelStack.Children.Add(subtitle);

        UIElement actionRegion = BuildModelActionRegion(m, installed, isActive, isDownloading, isFailed, ds);

        // 4 px left-edge accent strip — color depends on state.
        var accent = new Border
        {
            Width = 4,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(2, 0, 0, 2),
        };
        if (isFailed)
        {
            accent.Background = Theme("ErrorRed");
        }
        else if (isActive || isDownloading)
        {
            accent.Background = Theme("Mint");
        }
        else
        {
            accent.Background = System.Windows.Media.Brushes.Transparent;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Accent spans the full row height — sits in col 0.
        Grid.SetColumn(accent, 0);
        // 14 px gutter between accent and label so the strip reads as a separator,
        // not as part of the label.
        labelStack.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(labelStack, 1);
        Grid.SetColumn(actionRegion, 2);
        grid.Children.Add(accent);
        grid.Children.Add(labelStack);
        grid.Children.Add(actionRegion);

        var border = new Border
        {
            Style = (Style)FindResource("RowCard"),
            Margin = new Thickness(0, 0, 0, isLast ? 0 : 8),
        };
        if (isActive)
        {
            // Active state tints the whole card subtly so it reads as "in use"
            // at a glance — overrides the default RowCard surface brushes.
            border.Background = Theme("MintTint");
            border.BorderBrush = Theme("MintBorder");
        }
        border.Child = grid;
        return border;
    }

    private static StackPanel BuildActiveBadge()
    {
        // Small uppercase eyebrow chip — clearer at-a-glance than a status text.
        return new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Children =
            {
                new Border
                {
                    Background = Theme("Mint"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    Child = new TextBlock
                    {
                        Text = "ACTIVE",
                        FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x0F, 0x1F, 0x18)),
                    },
                },
            },
        };
    }

    // Right-side action region — state-driven CTA per the skill's primary-action
    // rule. Each state has exactly one button (Active has none — the action's
    // already been performed). Replaces the old `BuildModelStatusRegion`.
    private UIElement BuildModelActionRegion(
        ModelDescriptor m,
        bool installed,
        bool isActive,
        bool isDownloading,
        bool isFailed,
        ModelDownloadState? ds)
    {
        if (isActive)
        {
            // No button — card tint + ACTIVE badge already say "in use".
            return new TextBlock { Visibility = Visibility.Collapsed };
        }
        if (isFailed && ds is not null)
        {
            return BuildModelErrorRegion(m, ds.Error!);
        }
        if (isDownloading && ds is not null)
        {
            return BuildModelDownloadingRegion(m, ds);
        }
        if (installed)
        {
            // Installed-but-not-active → primary CTA.
            var useBtn = new System.Windows.Controls.Button
            {
                Content = "Use this model",
                Style = TypeStyle("Btn.Primary"),
                Tag = m.Id,
                VerticalAlignment = VerticalAlignment.Center,
            };
            useBtn.Click += OnUseModelClick;
            return useBtn;
        }
        // Not installed → secondary CTA (download is a heavier commitment than a
        // primary action — the user must know they're spending bandwidth + disk).
        var downloadBtn = new System.Windows.Controls.Button
        {
            Content = "Download",
            Style = TypeStyle("Btn.Secondary"),
            Tag = m.Id,
            VerticalAlignment = VerticalAlignment.Center,
        };
        downloadBtn.Click += OnModelDownloadClick;
        return downloadBtn;
    }

    private async void OnUseModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string id)
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
        _modelsRendered = false;
        RenderModelsTab();
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
                Style = TypeStyle("Type.BadgeMint"),
            },
        };
    }

    private StackPanel BuildModelDownloadingRegion(ModelDescriptor m, ModelDownloadState ds)
    {
        var stack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 180 × 4 px mint progress bar per APP_DESIGN §3.3 Tab 3.
        var trackOuter = new Grid { Width = 180, VerticalAlignment = VerticalAlignment.Center };
        trackOuter.Children.Add(new Border
        {
            Height = 4,
            Background = Theme("MeterTrack"),
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
        });
        trackOuter.Children.Add(new Border
        {
            Height = 4,
            Background = Theme("Mint"),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Width = Math.Max(0, Math.Min(180, ds.Percent / 100.0 * 180)),
        });
        stack.Children.Add(trackOuter);

        stack.Children.Add(new TextBlock
        {
            Text = $"  {ds.Percent:F0}%",
            Style = TypeStyle("Type.MonoSm"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 38,
        });

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Style = (Style)System.Windows.Application.Current.FindResource("Btn.Ghost"),
            Tag = m.Id,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cancelBtn.Click += OnModelCancelClick;
        stack.Children.Add(cancelBtn);
        return stack;
    }

    private StackPanel BuildModelErrorRegion(ModelDescriptor m, string error)
    {
        var stack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = error,
            Style = TypeStyle("Type.ErrorInline"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
        });
        var retryBtn = new System.Windows.Controls.Button
        {
            Content = "Retry",
            Style = (Style)System.Windows.Application.Current.FindResource("Btn.Secondary"),
            Tag = m.Id,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        retryBtn.Click += OnModelDownloadClick;
        stack.Children.Add(retryBtn);
        return stack;
    }

    private void OnModelDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string id)
        {
            return;
        }
        var descriptor = _models.Manifest.Models.FirstOrDefault(m => m.Id == id);
        if (descriptor is null)
        {
            return;
        }
        // If Offline Mode is on, EgressAllowlistHandler throws — but we should
        // pre-empt with a clearer message rather than launching a download that
        // immediately fails with "Network blocked". §13.1 keeps the contract honest.
        if (_prefs.Current.Privacy.OfflineMode)
        {
            _modelDownloads[id] = new ModelDownloadState
            {
                Error = "Disabled while Offline Mode is on.",
            };
            _modelsRendered = false;
            RenderModelsTab();
            return;
        }

        var cts = new CancellationTokenSource();
        var state = new ModelDownloadState
        {
            Cts = cts,
            Percent = 0,
            BytesDone = 0,
            BytesTotal = descriptor.SizeBytes,
        };
        _modelDownloads[id] = state;
        _modelsRendered = false;
        RenderModelsTab();

        var progress = new Progress<DownloadProgress>(p =>
        {
            // IProgress<T> already marshals to the captured SynchronizationContext
            // (UI thread for the constructor call). Throttle re-renders to ~3 Hz so
            // the StackPanel rebuild doesn't dominate the CPU on a fast download.
            state.BytesDone = p.BytesDownloaded;
            state.BytesTotal = p.TotalBytes > 0 ? p.TotalBytes : descriptor.SizeBytes;
            double newPct = state.BytesTotal > 0
                ? (double)state.BytesDone / state.BytesTotal * 100.0
                : 0;
            if (Math.Abs(newPct - state.Percent) < 0.5)
            {
                return;
            }
            state.Percent = newPct;
            _modelsRendered = false;
            RenderModelsTab();
        });

        _ = Task.Run(async () =>
        {
            var result = await _models.DownloadAsync(descriptor, progress, cts.Token).ConfigureAwait(false);
            await Dispatcher.BeginInvoke(() =>
            {
                if (result.Success)
                {
                    _modelDownloads.Remove(id);
#pragma warning disable CA1848, CA1873
                    _logger.LogInformation("Model {Id} downloaded.", id);
#pragma warning restore CA1848, CA1873
                }
                else
                {
                    // Cancellation comes back as a Fail with a "Download cancelled." message.
                    bool cancelled = cts.IsCancellationRequested;
                    if (cancelled)
                    {
                        _modelDownloads.Remove(id);
                    }
                    else
                    {
                        state.Cts = null;
                        state.Error = ShortenDownloadError(result.Error ?? "Download failed.");
                    }
                }
                cts.Dispose();
                _modelsRendered = false;
                RenderModelsTab();
            });
        });
    }

    private void OnModelCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string id)
        {
            return;
        }
        if (_modelDownloads.TryGetValue(id, out var state) && state.Cts is not null)
        {
            state.Cts.Cancel();
            // The completion continuation removes the entry and re-renders.
        }
    }

    private static string ShortenDownloadError(string raw)
    {
        // ModelManager wraps the underlying error with a prefix. Strip it for display.
        const string prefix = "HTTP error downloading ";
        if (raw.StartsWith(prefix, StringComparison.Ordinal))
        {
            int colon = raw.IndexOf(':', prefix.Length);
            if (colon > 0 && colon + 2 < raw.Length)
            {
                return raw[(colon + 2)..];
            }
        }
        // "Network blocked — Offline Mode is on." comes straight through.
        return raw.Length > 80 ? raw[..77] + "…" : raw;
    }

    // OnModelRadioChecked removed — the Models tab redesign dropped the radio
    // button in favor of an explicit "Use this model" Btn.Primary per row
    // (handled by OnUseModelClick in the BuildModelRow block above).

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

    // ── History tab (9G + §13.5 P1-2) ───────────────────────────────────────

    private DispatcherTimer? _historySearchDebounce;
    private string _historyQuery = string.Empty;

    private async Task RenderHistoryTabAsync()
    {
        _historyRendered = true;
        await ReloadHistoryAsync().ConfigureAwait(true);
    }

    private async Task ReloadHistoryAsync()
    {
        try
        {
            // SearchAsync(null) returns most recent first; query goes through FTS.
            string? query = string.IsNullOrWhiteSpace(_historyQuery) ? null : _historyQuery;
            var rows = await _history.SearchAsync(query, 50, 0).ConfigureAwait(true);

            HistoryStats.Text = query is null
                ? $"{rows.Count} transcripts shown · most recent first"
                : $"{rows.Count} match{(rows.Count == 1 ? string.Empty : "es")} for “{query}”";

            HistoryList.Children.Clear();
            foreach (var r in rows)
            {
                HistoryList.Children.Add(BuildHistoryRow(r));
            }
            if (rows.Count == 0)
            {
                HistoryList.Children.Add(new TextBlock
                {
                    Text = query is null
                        ? "No transcripts yet. Dictate something to populate history."
                        : "No transcripts match that search.",
                    Style = TypeStyle("Type.HintItalic"),
                    Margin = new Thickness(14, 14, 14, 14),
                });
            }
            // Disable Purge when there's literally nothing to purge.
            PurgeAllButton.IsEnabled = rows.Count > 0 || query is not null;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Failed to query history.");
#pragma warning restore CA1848, CA1873
            HistoryStats.Text = "History unavailable.";
        }
    }

    private void OnHistorySearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Placeholder visibility — toggled in code rather than a XAML trigger because
        // the placeholder is overlaid as a sibling on the same Grid column.
        HistorySearchPlaceholder.Visibility = string.IsNullOrEmpty(HistorySearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistorySearchClear.Visibility = string.IsNullOrEmpty(HistorySearchBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // 250 ms debounce so we don't fire a SQL query per keystroke.
        if (_historySearchDebounce is null)
        {
            _historySearchDebounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _historySearchDebounce.Tick += async (_, _) =>
            {
                _historySearchDebounce!.Stop();
                _historyQuery = HistorySearchBox.Text.Trim();
                await ReloadHistoryAsync().ConfigureAwait(true);
            };
        }
        _historySearchDebounce.Stop();
        _historySearchDebounce.Start();
    }

    private void OnHistorySearchClearClick(object sender, RoutedEventArgs e)
    {
        HistorySearchBox.Text = string.Empty;
        // TextChanged above debounces; force-flush so the empty query takes effect now.
        _historySearchDebounce?.Stop();
        _historyQuery = string.Empty;
        _ = ReloadHistoryAsync();
    }

    private async void OnPurgeAllClick(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "Delete every saved transcript? This cannot be undone.",
            "Purge all history",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            await _history.PurgeAllAsync().ConfigureAwait(true);
#pragma warning disable CA1848, CA1873
            _logger.LogInformation("History purged by user.");
#pragma warning restore CA1848, CA1873
            await ReloadHistoryAsync().ConfigureAwait(true);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PurgeAllAsync failed.");
#pragma warning restore CA1848, CA1873
        }
    }

    // History table row — Grid layout that mirrors the table header's column geometry
    // (14 / 78 / 110 / * / 72 / 52). Mono font for time / model / duration columns
    // per the skill's number-tabular rule. Transcript truncates with ellipsis and
    // exposes the full text via ToolTip per the skill's truncation-strategy rule.
    //
    // Row actions (Copy + Delete) per UI UX Pro Max convention for productivity
    // tables (Gmail / Notion / Linear pattern): the model + duration cells hide
    // on row hover and Copy / Delete IconGhost buttons take their place. Avoids
    // permanently-visible button clutter in a read-heavy table. Right-click
    // context menu remains as the keyboard / power-user path.
    private Border BuildHistoryRow(TranscriptRecord r)
    {
        bool ok = r.Status == TranscriptStatus.Ok;

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        // Col 0 — status dot (Dot.Mint for ok, Dot.Red for failed).
        var dot = new Ellipse
        {
            Style = (Style)System.Windows.Application.Current.FindResource(
                ok ? "Dot.Mint" : "Dot.Red"),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        // Col 1 — time. Mono via Type.MonoSm; full timestamp in tooltip.
        var time = new TextBlock
        {
            Text = FormatRelative(r.Timestamp),
            Style = TypeStyle("Type.MonoSm"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = r.Timestamp.LocalDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(time, 1);
        grid.Children.Add(time);

        // Col 2 — target app. 12.5 Medium SecondaryText — close to Type.Footnote
        // but Type.Footnote has no TextTrimming; inline kept here for the trim +
        // tooltip combo. (12.5 also doesn't match the 12 px in Type.Footnote.)
        var app = new TextBlock
        {
            Text = string.IsNullOrEmpty(r.TargetApp) ? "—" : r.TargetApp,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = Theme("SecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0),
        };
        if (!string.IsNullOrEmpty(r.TargetApp))
        {
            app.ToolTip = r.TargetApp;
        }
        Grid.SetColumn(app, 2);
        grid.Children.Add(app);

        // Col 3 — transcript. Sans 13 Regular — color flips between Primary/Error
        // and FontStyle flips italic on failure, so this stays inline (no single
        // Type.* role covers both color states + italic toggle).
        var transcript = new TextBlock
        {
            Text = r.Text,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            Foreground = ok ? Theme("PrimaryText") : Theme("ErrorRed"),
            FontStyle = ok ? FontStyles.Normal : FontStyles.Italic,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = r.Text,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(transcript, 3);
        grid.Children.Add(transcript);

        // Col 4 — model, mono via Type.MonoSm. Strip "ggml-" prefix.
        var model = new TextBlock
        {
            Text = ShortModelId(r.Model),
            Style = TypeStyle("Type.MonoSm"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(model, 4);
        grid.Children.Add(model);

        // Col 5 — duration, mono, right-aligned (tabular numeric).
        var duration = new TextBlock
        {
            Text = $"{r.Duration.TotalSeconds:F1}s",
            Style = TypeStyle("Type.MonoSm"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(duration, 5);
        grid.Children.Add(duration);

        // Hover-reveal actions: spans cols 4 + 5 so the right edge of the row
        // hides the model+duration data and exposes Copy + Delete instead.
        // Background = Surface ensures the row's HoverSubtle tint shows through
        // around the buttons (Surface is the parent card's bg, which is what's
        // underneath the row when not hovered).
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme("Surface"),
            Visibility = Visibility.Collapsed,
        };
        var copyBtn = new System.Windows.Controls.Button
        {
            Style = TypeStyle("Btn.IconGhost"),
            Content = "",                         // Fluent Icons "Copy"
            ToolTip = "Copy transcript",
            Margin = new Thickness(0, 0, 2, 0),
        };
        copyBtn.Click += (_, _) => CopyTranscriptToClipboard(r);
        var deleteBtn = new System.Windows.Controls.Button
        {
            Style = TypeStyle("Btn.IconGhost"),
            Content = "",                         // Fluent Icons "Delete"
            ToolTip = "Delete transcript",
            Foreground = Theme("ErrorRed"),
        };
        deleteBtn.Click += (_, _) => _ = DeleteTranscriptAsync(r);
        actions.Children.Add(copyBtn);
        actions.Children.Add(deleteBtn);
        Grid.SetColumn(actions, 4);
        Grid.SetColumnSpan(actions, 2);
        grid.Children.Add(actions);

        var border = new Border
        {
            Style = (Style)FindResource("HistoryRow"),
            Child = grid,
            ContextMenu = BuildHistoryContextMenu(r),
        };
        // Toggle the actions overlay on hover. The HistoryRow style's
        // IsMouseOver trigger handles the background tint; this handles the
        // child Visibility — keeps the show/hide logic alongside the data.
        border.MouseEnter += (_, _) => actions.Visibility = Visibility.Visible;
        border.MouseLeave += (_, _) => actions.Visibility = Visibility.Collapsed;
        return border;
    }

    private void CopyTranscriptToClipboard(TranscriptRecord r)
    {
        try
        {
            System.Windows.Clipboard.SetText(r.Text);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Clipboard write failed for history Copy.");
#pragma warning restore CA1848, CA1873
        }
    }

    private async Task DeleteTranscriptAsync(TranscriptRecord r)
    {
        try
        {
            await _history.DeleteAsync(r.Id).ConfigureAwait(true);
            await ReloadHistoryAsync().ConfigureAwait(true);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "DeleteAsync failed for transcript {Id}.", r.Id);
#pragma warning restore CA1848, CA1873
        }
    }

    private static string ShortModelId(string raw) =>
        raw.StartsWith("ggml-", StringComparison.Ordinal) ? raw["ggml-".Length..] : raw;

    private System.Windows.Controls.ContextMenu BuildHistoryContextMenu(TranscriptRecord r)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var copy = new System.Windows.Controls.MenuItem { Header = "Copy text" };
        copy.Click += (_, _) => CopyTranscriptToClipboard(r);
        menu.Items.Add(copy);

        var del = new System.Windows.Controls.MenuItem { Header = "Delete" };
        del.Click += async (_, _) => await DeleteTranscriptAsync(r).ConfigureAwait(true);
        menu.Items.Add(del);

        return menu;
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

    // §13.5 P1-3 — log size + Clear logs.
    // Total size is computed synchronously from the Logs directory's *.log files.
    // The Logs folder is small in v1 (cap ~25 MB across 5 daily rolls per App.OnStartup
    // Serilog config), so enumeration cost is negligible.
    private void RefreshLogsSize()
    {
        try
        {
            long total = 0;
            if (Directory.Exists(AppPaths.LogsDir))
            {
                foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log"))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (FileNotFoundException) { /* rolled over mid-enumeration */ }
                }
            }
            LogsSize.Text = FormatBytes(total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Failed to enumerate logs directory.");
#pragma warning restore CA1848, CA1873
            LogsSize.Text = "Unavailable";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} bytes";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    private void OnClearLogsClick(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "Delete all KusPus log files? The current log will be re-created as needed. " +
            "Anything you want to keep — copy out first.",
            "Clear logs",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }
        int deleted = 0;
        int skipped = 0;
        if (Directory.Exists(AppPaths.LogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDir, "*.log"))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException)
                {
                    // Today's log is held open by Serilog's FileSink — expected; we don't
                    // tear down the logger to clear logs. Skip and report.
                    skipped++;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                }
            }
        }
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Clear logs: deleted={Deleted} skipped={Skipped}.", deleted, skipped);
#pragma warning restore CA1848, CA1873
        RefreshLogsSize();
    }

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

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/devangk003/kuspus");

    // ── About-footer social links (audit follow-up). ──────────────────────────
    private void OnOpenLinkedInClick(object sender, RoutedEventArgs e) =>
        OpenUrl("https://www.linkedin.com/in/devangk003/");

    private void OnOpenXClick(object sender, RoutedEventArgs e) =>
        OpenUrl("https://x.com/devang_kumawat");

    private void OnOpenAuthorGitHubClick(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/devangk003");

    private void OnOpenPortfolioClick(object sender, RoutedEventArgs e) =>
        OpenUrl("https://lnk.bio/devangk003");

    // Single URL-launcher used by every social/footer link. UseShellExecute=true
    // hands off to the OS default browser; catches the typical Process.Start
    // failure modes without crashing the app.
    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Failed to open URL {Url}.", url);
#pragma warning restore CA1848, CA1873
        }
        catch (System.IO.FileNotFoundException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "No default handler for URL {Url}.", url);
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
