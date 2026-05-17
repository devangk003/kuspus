using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KusPus.Audio;
using KusPus.Core.Hotkeys;
using KusPus.Core.Settings;
using KusPus.Native;
using KusPus.Persistence;
using KusPus.Whisper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoreVK = KusPus.Core.Hotkeys.VirtualKey;
using WpfBrush = System.Windows.Media.Brush;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace KusPus.App;

/// <summary>
/// First-launch onboarding modal per docs/APP_DESIGN.md §4. Seven steps walked
/// linearly with Back / Next / Skip / Finish navigation.
///
/// On Finish: sets <c>PrefsStore.Onboarding.Completed = true</c> so future
/// launches skip the modal. Re-runnable via the About tab's "Run again" row.
///
/// Hotkey-rebind logic in step 2 duplicates the listen-mode state machine from
/// MainWindow.xaml.cs — a future cluster should extract a HotkeyPickerControl
/// UserControl to deduplicate. Same applies to the mic meter (WasapiCapture +
/// peak compute + fill width).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Window owns its disposable fields (CTS, capture, enumerator) via OnClosed; making the Window IDisposable would conflict with WPF's lifecycle model (same pattern as App.xaml.cs and MainWindow.xaml.cs).")]
public partial class OnboardingWindow : Window
{
    private const int StepCount = 7;
    private const double MeterTrackWidthDip = 392.0;

    private readonly IPrefsStore _prefs;
    private readonly IHotkeyEngine _hotkey;
    private readonly IAudioRecorder _audio;
    private readonly IWhisperRunner _whisper;
    private readonly IModelManager _models;
    private readonly ILogger<OnboardingWindow> _logger;

    private int _currentStep;
    private bool _loaded;

    // Hotkey listen-mode (step 2) — mirrors MainWindow.
    private bool _isListening;
    private HotkeyChord? _savedChord;
    private readonly System.Collections.Generic.List<CoreVK> _pressedOrder = new();
    private readonly System.Collections.Generic.HashSet<CoreVK> _heldKeys = new();
    private System.Collections.Generic.IReadOnlyList<CoreVK> _bestModifiers = System.Array.Empty<CoreVK>();
    private CoreVK? _bestKey;

    // Mic check (step 3) — WasapiCapture session.
    private NAudio.CoreAudioApi.MMDeviceEnumerator? _mmEnumerator;
    private NAudio.CoreAudioApi.WasapiCapture? _micCapture;
    private DispatcherTimer? _micMeterTimer;
    private float _micPeak;
    private bool _micIsFloat;
    private int _micBytesPerSample;

    // Try-it (step 6).
    private DispatcherTimer? _tryItTimer;

    public OnboardingWindow(
        IPrefsStore prefs,
        IHotkeyEngine hotkey,
        IAudioRecorder audio,
        IWhisperRunner whisper,
        IModelManager models,
        ILogger<OnboardingWindow>? logger = null)
    {
        _prefs = prefs;
        _hotkey = hotkey;
        _audio = audio;
        _whisper = whisper;
        _models = models;
        _logger = logger ?? NullLogger<OnboardingWindow>.Instance;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    // Win11 DWM rounded-corner constants per the dwmapi.h header.
    // DWMWA_WINDOW_CORNER_PREFERENCE = 33; DWMWCP_ROUND = 2 (~8 px radius).
    // On Win10 the attribute is silently ignored — the Background={AppBg} fix in
    // OnboardingWindow.xaml ensures the corners blend on that fallback path.
    // Reference: https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwm_window_corner_preference
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ThemeApply.ApplyToWindow(hwnd, ThemeApply.Resolve(_prefs.Current.Ui.Theme));

        // Round the OS-level window edge so it matches the inner Border's
        // Radius.Lg (8 px). Without this the WindowStyle=None +
        // AllowsTransparency=False window is a hard rectangle and the rounded
        // <Border> renders against black/AppBg-coloured cutouts at each corner.
        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildProgressDots();
        ApplyHotkeyDisplay(_prefs.Current.Hotkey);
        OnbAutostartToggle.IsChecked = _prefs.Current.Autostart;
        OnbCrashToggle.IsChecked = _prefs.Current.Privacy.CrashReportsOptIn;
        _loaded = true;
        ShowStep(0);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopMicCheck();
        _tryItTimer?.Stop();
    }

    // ── Step navigation ─────────────────────────────────────────────────────

    private void ShowStep(int index)
    {
        _currentStep = System.Math.Clamp(index, 0, StepCount - 1);

        Step1Welcome.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2Hotkey.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Mic.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step4Autostart.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step5Crash.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step6TryIt.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        Step7Done.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = index == StepCount - 1 ? "Finish" : "Next";
        SkipButton.Visibility = index == StepCount - 1 ? Visibility.Collapsed : Visibility.Visible;

        UpdateProgressDots();

        // Mic check only runs while step 3 is showing — same on/off pattern as
        // the MainWindow Audio tab. Populate the device combo FIRST so the
        // closed-state Text + selection match the persisted device id before
        // the capture session opens.
        if (index == 2)
        {
            PopulateOnbInputDeviceCombo();
            StartMicCheck();
        }
        else
        {
            StopMicCheck();
        }

        // Cancel any in-flight hotkey listen mode when navigating away.
        if (index != 1 && _isListening)
        {
            CancelListenMode();
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep >= StepCount - 1)
        {
            FinishOnboarding(completed: true);
            return;
        }
        ShowStep(_currentStep + 1);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        ShowStep(_currentStep - 1);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        FinishOnboarding(completed: false);
    }

    private async void FinishOnboarding(bool completed)
    {
        try
        {
            var current = _prefs.Current;
            var next = current with
            {
                Onboarding = current.Onboarding with { Completed = completed },
            };
            await _prefs.SaveAsync(next).ConfigureAwait(true);
#pragma warning disable CA1848, CA1873
            _logger.LogInformation("Onboarding closed — completed={Completed}.", completed);
#pragma warning restore CA1848, CA1873
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed at onboarding finish.");
#pragma warning restore CA1848, CA1873
        }
        Close();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Let the user drag the chromeless window from the progress-dots header.
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* not in a state to drag */ }
        }
    }

    // ── Progress dots ───────────────────────────────────────────────────────

    private readonly Border[] _dots = new Border[StepCount];

    private void BuildProgressDots()
    {
        ProgressDots.Children.Clear();
        for (int i = 0; i < StepCount; i++)
        {
            var dot = new Border
            {
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = i,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.MouseLeftButtonDown += OnDotClick;
            ProgressDots.Children.Add(dot);
            _dots[i] = dot;
        }
        UpdateProgressDots();
    }

    private void OnDotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is int i)
        {
            ShowStep(i);
            e.Handled = true;
        }
    }

    private void UpdateProgressDots()
    {
        for (int i = 0; i < StepCount; i++)
        {
            var dot = _dots[i];
            if (i == _currentStep)
            {
                dot.Width = 22; dot.Height = 6;
                dot.SetResourceReference(Border.BackgroundProperty, "Mint");
            }
            else if (i < _currentStep)
            {
                dot.Width = 6; dot.Height = 6;
                dot.SetResourceReference(Border.BackgroundProperty, "MintBorder");
            }
            else
            {
                dot.Width = 6; dot.Height = 6;
                dot.SetResourceReference(Border.BackgroundProperty, "BorderStrong");
            }
        }
    }

    // ── Step 2 · Hotkey picker (duplicate of MainWindow listen-mode) ───────

    private void OnHotkeyCardClick(object sender, MouseButtonEventArgs e)
    {
        if (_isListening)
        {
            return;
        }
        var current = _prefs.Current.Hotkey;
        _savedChord = new HotkeyChord(current.Modifiers, current.KeyCode);
        _hotkey.SetChord(new HotkeyChord(System.Array.Empty<CoreVK>(), null));

        _isListening = true;
        _pressedOrder.Clear();
        _heldKeys.Clear();
        _bestModifiers = System.Array.Empty<CoreVK>();
        _bestKey = null;

        OnbHotkeyListenBorder.Visibility = Visibility.Visible;
        OnbHotkeyEyebrow.SetResourceReference(TextBlock.ForegroundProperty, "Mint");
        OnbHotkeyHint.Text = "Now press the keys you want to use… (ESC to cancel)";
        OnbConflictRow.Visibility = Visibility.Collapsed;
        RenderKeycaps(System.Array.Empty<CoreVK>());
        Focus();
        e.Handled = true;
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
        OnbHotkeyListenBorder.Visibility = Visibility.Collapsed;
        OnbHotkeyEyebrow.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
        OnbHotkeyHint.Text = "Tap the picker, then press a new chord.";
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isListening)
        {
            if (e.Key == Key.Escape)
            {
                // ESC outside listen mode skips the onboarding.
                FinishOnboarding(completed: false);
                e.Handled = true;
            }
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
            ApplyHotkeyDisplay(_prefs.Current.Hotkey);
            return;
        }

        var current = _prefs.Current;
        var next = current with
        {
            Hotkey = new HotkeySettings
            {
                Modifiers = ((System.Collections.Generic.IEnumerable<CoreVK>)mods).ToArray(),
                KeyCode = key,
                HoldThresholdMs = current.Hotkey.HoldThresholdMs,
            },
        };
        try
        {
            await _prefs.SaveAsync(next).ConfigureAwait(true);
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed in onboarding hotkey commit.");
#pragma warning restore CA1848, CA1873
        }
        ApplyHotkeyDisplay(next.Hotkey);

        var conflict = DetectConflict(mods, key);
        OnbConflictRow.Visibility = conflict is null ? Visibility.Collapsed : Visibility.Visible;
        if (conflict is not null)
        {
            OnbConflictText.Text = conflict;
        }
    }

    private void SnapshotBest()
    {
        var mods = new System.Collections.Generic.List<CoreVK>();
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

    private System.Collections.Generic.List<CoreVK> BuildPreview()
    {
        var keys = new System.Collections.Generic.List<CoreVK>(_bestModifiers);
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
        Key effective = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(effective);
        if (vk == 0)
        {
            return null;
        }
        return System.Enum.IsDefined(typeof(CoreVK), (ushort)vk) ? (CoreVK)vk : null;
    }

    private void ApplyHotkeyDisplay(HotkeySettings hk)
    {
        var keys = new System.Collections.Generic.List<CoreVK>(hk.Modifiers);
        if (hk.KeyCode is { } k)
        {
            keys.Add(k);
        }
        RenderKeycaps(keys);
    }

    private void RenderKeycaps(System.Collections.Generic.IReadOnlyList<CoreVK> keys)
    {
        OnbHotkeyKeycaps.Children.Clear();
        if (keys.Count == 0)
        {
            OnbHotkeyKeycaps.Children.Add(BuildKeycap("…"));
            return;
        }
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0)
            {
                var plus = new TextBlock
                {
                    Text = "+",
                    FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 10, 0),
                };
                plus.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
                OnbHotkeyKeycaps.Children.Add(plus);
            }
            OnbHotkeyKeycaps.Children.Add(BuildKeycap(FriendlyKey(keys[i])));
        }
    }

    private Border BuildKeycap(string label)
    {
        var border = new Border
        {
            Style = (Style)FindResource("OnboardingKeycap"),
        };
        var text = new TextBlock
        {
            Text = label,
            FontFamily = new WpfFontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
        border.Child = text;
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

    private static string? DetectConflict(System.Collections.Generic.IReadOnlyList<CoreVK> mods, CoreVK? key)
    {
        bool win = mods.Contains(CoreVK.LeftWin) || mods.Contains(CoreVK.RightWin);
        bool ctrl = mods.Contains(CoreVK.LeftCtrl) || mods.Contains(CoreVK.RightCtrl);
        bool alt = mods.Contains(CoreVK.LeftAlt) || mods.Contains(CoreVK.RightAlt);
        if (mods.Count == 1 && key is null && win)
        {
            return "Win on its own opens Start. Add a second modifier or a key.";
        }
        if (win && !ctrl && !alt && key is { } k1)
        {
            return k1 switch
            {
                CoreVK.L => "Win + L locks the screen.",
                CoreVK.D => "Win + D shows the desktop.",
                CoreVK.E => "Win + E opens File Explorer.",
                CoreVK.V => "Win + V opens Clipboard History.",
                CoreVK.A => "Win + A opens Quick Settings.",
                CoreVK.Tab => "Win + Tab opens Task View.",
                _ => null,
            };
        }
        if (alt && !ctrl && !win && key == CoreVK.F4)
        {
            return "Alt + F4 closes the active window.";
        }
        return null;
    }

    // ── Step 3 · Microphone check ──────────────────────────────────────────

    private void StartMicCheck()
    {
        if (_micCapture is not null)
        {
            return;
        }
        try
        {
            _mmEnumerator ??= new NAudio.CoreAudioApi.MMDeviceEnumerator();
            // Use the user's saved device (mirrors MainWindow.ResolveLevelMeterDevice)
            // so the onboarding meter reflects whichever mic Preferences would.
            var device = ResolveOnbMicDevice(_mmEnumerator, _prefs.Current.Audio.InputDeviceId);
            MicDeviceLabel.Text = (device.FriendlyName ?? "MICROPHONE").ToUpperInvariant();

            var capture = new NAudio.CoreAudioApi.WasapiCapture(device);
            _micIsFloat = capture.WaveFormat.Encoding ==
                NAudio.Wave.WaveFormatEncoding.IeeeFloat;
            _micBytesPerSample = capture.WaveFormat.BitsPerSample / 8;
            capture.DataAvailable += OnMicDataAvailable;
            capture.StartRecording();
            _micCapture = capture;

            MicSuccessRow.Visibility = Visibility.Visible;
            MicErrorRow.Visibility = Visibility.Collapsed;
            MicOpenSettings.Visibility = Visibility.Collapsed;
        }
        catch (COMException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Mic check device-enumeration failed.");
#pragma warning restore CA1848, CA1873
            ShowMicError();
            return;
        }
        catch (NAudio.MmException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Mic check capture-init failed.");
#pragma warning restore CA1848, CA1873
            ShowMicError();
            return;
        }

        if (_micMeterTimer is null)
        {
            _micMeterTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = System.TimeSpan.FromMilliseconds(66),
            };
            _micMeterTimer.Tick += OnMicMeterTick;
        }
        _micMeterTimer.Start();
    }

    private void StopMicCheck()
    {
        _micMeterTimer?.Stop();
        if (_micCapture is not null)
        {
            try { _micCapture.StopRecording(); } catch (NAudio.MmException) { }
            _micCapture.DataAvailable -= OnMicDataAvailable;
            _micCapture.Dispose();
            _micCapture = null;
        }
        _micPeak = 0;
        MicMeterFill.Width = 0;
    }

    private void ShowMicError()
    {
        MicSuccessRow.Visibility = Visibility.Collapsed;
        MicErrorRow.Visibility = Visibility.Visible;
        MicOpenSettings.Visibility = Visibility.Visible;
        MicMeterFill.Width = 0;
    }

    private void OnMicDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }
        float peak = 0;
        var buf = e.Buffer;
        if (_micIsFloat && _micBytesPerSample == 4)
        {
            int count = e.BytesRecorded / 4;
            for (int i = 0; i < count; i++)
            {
                float a = System.Math.Abs(System.BitConverter.ToSingle(buf, i * 4));
                if (a > peak)
                {
                    peak = a;
                }
            }
        }
        else if (_micBytesPerSample == 2)
        {
            int count = e.BytesRecorded / 2;
            for (int i = 0; i < count; i++)
            {
                float a = System.Math.Abs(System.BitConverter.ToInt16(buf, i * 2)) / 32768f;
                if (a > peak)
                {
                    peak = a;
                }
            }
        }
        _micPeak = System.Math.Max(_micPeak, peak);
    }

    private void OnMicMeterTick(object? sender, EventArgs e)
    {
        float peak = _micPeak;
        _micPeak *= 0.6f;
        double normalized = System.Math.Clamp(peak * 2.5, 0.0, 1.0);
        MicMeterFill.Width = normalized * MeterTrackWidthDip;
    }

    private void OnOpenMicSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:privacy-microphone",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Failed to open mic Settings URI.");
#pragma warning restore CA1848, CA1873
        }
    }

    // ── Step 4 · Autostart ──────────────────────────────────────────────────

    private void OnAutostartCardClick(object sender, MouseButtonEventArgs e)
    {
        // Click anywhere on the card flips the toggle for a Fitts'-law-friendly target.
        if (!ReferenceEquals(e.OriginalSource, OnbAutostartToggle))
        {
            OnbAutostartToggle.IsChecked = OnbAutostartToggle.IsChecked != true;
        }
    }

    private async void OnAutostartToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        bool enabled = OnbAutostartToggle.IsChecked == true;
        var current = _prefs.Current;
        if (current.Autostart == enabled)
        {
            return;
        }
        try
        {
            AutostartRegistry.Set(enabled);
            await _prefs.SaveAsync(current with { Autostart = enabled }).ConfigureAwait(true);
        }
        catch (System.UnauthorizedAccessException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Autostart registry write blocked.");
#pragma warning restore CA1848, CA1873
            OnbAutostartToggle.IsChecked = current.Autostart;
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for Autostart.");
#pragma warning restore CA1848, CA1873
        }
    }

    // ── Step 5 · Crash reports ──────────────────────────────────────────────

    private void OnCrashCardClick(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, OnbCrashToggle))
        {
            OnbCrashToggle.IsChecked = OnbCrashToggle.IsChecked != true;
        }
    }

    private async void OnCrashToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }
        bool enabled = OnbCrashToggle.IsChecked == true;
        var current = _prefs.Current;
        if (current.Privacy.CrashReportsOptIn == enabled)
        {
            return;
        }
        try
        {
            await _prefs.SaveAsync(current with
            {
                Privacy = current.Privacy with { CrashReportsOptIn = enabled },
            }).ConfigureAwait(true);
        }
        catch (System.IO.IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for crash reports.");
#pragma warning restore CA1848, CA1873
        }
    }

    // ── Step 6 · Try it (real dictation) ────────────────────────────────────
    //
    // Mirrors MainWindow.RunTestTranscriptionAsync: 5 s countdown record →
    // whisper transcribe → render text. Per user dogfood feedback (2026-05-17)
    // the prior simulation was misleading — onboarding needs to actually
    // exercise the audio+whisper path so a broken mic / missing model surfaces
    // before the user finishes setup, not after.

    private enum TryItState { Idle, Recording, Transcribing }
    private TryItState _tryItState = TryItState.Idle;
    private int _tryItCountdownRemaining;
    private System.Threading.CancellationTokenSource? _tryItCts;

    private async void OnTryItSimulateClick(object sender, RoutedEventArgs e)
    {
        if (_tryItState is TryItState.Recording or TryItState.Transcribing)
        {
            // In-flight → cancel.
            _tryItCts?.Cancel();
            return;
        }
        await RunTryItDictationAsync().ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task RunTryItDictationAsync()
    {
        _tryItCts = new System.Threading.CancellationTokenSource();
        var ct = _tryItCts.Token;

        // Resolve the active model before opening the mic — fast-fail if missing
        // (covers the "user skipped model download" case without a confusing
        // generic error after the 5 s wait).
        var modelId = _prefs.Current.Models.ActiveModelId;
        var customPath = _prefs.Current.Models.CustomModelPath;
        var resolved = _models.Resolve(modelId, customPath);
        if (!resolved.Success)
        {
            SetTryItError(resolved.Error ?? "Active model unavailable.");
            return;
        }

        try
        {
            SetTryItState(TryItState.Recording, "Recording… 5 seconds");
            var startResult = await _audio.StartAsync(ct).ConfigureAwait(true);
            if (!startResult.Success)
            {
                SetTryItError(startResult.Error ?? "Mic open failed.");
                return;
            }

            StartTryItCountdown();
            await System.Threading.Tasks.Task.Delay(System.TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
            StopTryItCountdown();

            var stopResult = await _audio.StopAsync().ConfigureAwait(true);
            if (!stopResult.Success)
            {
                SetTryItError(stopResult.Error ?? "Mic stop failed.");
                return;
            }

            SetTryItState(TryItState.Transcribing, "Transcribing…");
            var transcript = await _whisper
                .TranscribeAsync(stopResult.Value!.WavPath, resolved.Value!, ct)
                .ConfigureAwait(true);

            try { File.Delete(stopResult.Value.WavPath); }
            catch (IOException) { /* best-effort cleanup of the temp wav */ }

            if (!transcript.Success)
            {
                SetTryItError(transcript.Error ?? "Transcribe failed.");
                return;
            }

            var text = string.IsNullOrWhiteSpace(transcript.Value)
                ? "(no speech detected)"
                : transcript.Value!.Trim();
            SetTryItResult(text);
        }
        catch (System.OperationCanceledException)
        {
            StopTryItCountdown();
            try { await _audio.StopAsync().ConfigureAwait(true); }
            catch (System.InvalidOperationException) { /* already stopped */ }
            SetTryItState(TryItState.Idle, "Your transcript will appear here.");
        }
        catch (System.Exception ex) when (ex is IOException or System.InvalidOperationException)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Onboarding try-it dictation failed unexpectedly.");
#pragma warning restore CA1848, CA1873
            SetTryItError("Unexpected error — see logs.");
        }
        finally
        {
            _tryItCts?.Dispose();
            _tryItCts = null;
        }
    }

    private void SetTryItState(TryItState next, string emptyText)
    {
        _tryItState = next;
        TryItEmpty.Text = emptyText;
        TryItEmpty.Visibility = Visibility.Visible;
        TryItContent.Visibility = Visibility.Collapsed;
        TryItSimulate.Content = next switch
        {
            TryItState.Recording => "Stop",
            TryItState.Transcribing => "Working…",
            _ => "Record dictation",
        };
        TryItSimulate.IsEnabled = next != TryItState.Transcribing;
        TryItClear.Visibility = Visibility.Collapsed;
        TryItBorderBrush.Color = next == TryItState.Recording
            ? System.Windows.Media.Color.FromArgb(0xCC, 0x4D, 0xDB, 0xA6)
            : ((SolidColorBrush)FindResource("BorderStrong")).Color;
    }

    private void SetTryItResult(string text)
    {
        _tryItState = TryItState.Idle;
        TryItContent.Text = text;
        TryItEmpty.Visibility = Visibility.Collapsed;
        TryItContent.Visibility = Visibility.Visible;
        TryItSimulate.Content = "Try another";
        TryItSimulate.IsEnabled = true;
        TryItClear.Visibility = Visibility.Visible;
        TryItBorderBrush.Color = ((SolidColorBrush)FindResource("Mint")).Color;
    }

    private void SetTryItError(string error)
    {
        _tryItState = TryItState.Idle;
        TryItContent.Text = error;
        TryItEmpty.Visibility = Visibility.Collapsed;
        TryItContent.Visibility = Visibility.Visible;
        TryItContent.Foreground = (SolidColorBrush)FindResource("ErrorRed");
        TryItSimulate.Content = "Record dictation";
        TryItSimulate.IsEnabled = true;
        TryItClear.Visibility = Visibility.Visible;
        TryItBorderBrush.Color = ((SolidColorBrush)FindResource("ErrorRed")).Color;
    }

    private void StartTryItCountdown()
    {
        _tryItCountdownRemaining = 5;
        _tryItTimer?.Stop();
        _tryItTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
        _tryItTimer.Tick += (_, _) =>
        {
            _tryItCountdownRemaining--;
            if (_tryItCountdownRemaining <= 0)
            {
                _tryItTimer?.Stop();
                return;
            }
            TryItEmpty.Text = $"Recording… {_tryItCountdownRemaining} second{(_tryItCountdownRemaining == 1 ? "" : "s")}";
        };
        _tryItTimer.Start();
    }

    private void StopTryItCountdown()
    {
        _tryItTimer?.Stop();
        _tryItTimer = null;
    }

    private void OnTryItClearClick(object sender, RoutedEventArgs e)
    {
        TryItEmpty.Text = "Your transcript will appear here.";
        TryItEmpty.Visibility = Visibility.Visible;
        TryItContent.Visibility = Visibility.Collapsed;
        TryItContent.Foreground = (SolidColorBrush)FindResource("PrimaryText");
        TryItSimulate.Content = "Record dictation";
        TryItSimulate.IsEnabled = true;
        TryItClear.Visibility = Visibility.Collapsed;
        TryItBorderBrush.Color = ((SolidColorBrush)FindResource("BorderStrong")).Color;
    }

    // ── Step 3 · Input device chooser ──────────────────────────────────────
    // Wires the same PrefsStore.Audio.InputDeviceId field that Preferences →
    // Audio uses. Selection persists until the user changes it from either
    // surface (or unplugs the device, in which case Resolve falls back to
    // the OS default). Logic mirrors MainWindow's combo with no shared base
    // class — onboarding is short-lived and a single-window helper would
    // pull in more ceremony than it removes.

    private sealed class OnbInputDeviceItem
    {
        public string? Id { get; init; }
        public string Display { get; init; } = string.Empty;
        public override string ToString() => Display;
    }

    private bool _suppressOnbDeviceChange;

    private void PopulateOnbInputDeviceCombo()
    {
        var items = new System.Collections.Generic.List<OnbInputDeviceItem>
        {
            new() { Id = null, Display = "Default device (follows Windows)" },
        };
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.DeviceState.Active);
            foreach (var d in devices)
            {
                items.Add(new OnbInputDeviceItem
                {
                    Id = d.ID,
                    Display = d.FriendlyName ?? "(unknown device)",
                });
                d.Dispose();
            }
        }
        catch (COMException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Onboarding mic enumeration failed.");
#pragma warning restore CA1848, CA1873
        }

        _suppressOnbDeviceChange = true;
        try
        {
            OnbInputDeviceCombo.ItemsSource = items;
            string? savedId = _prefs.Current.Audio.InputDeviceId;
            int selectedIndex = 0;
            for (int i = 1; i < items.Count; i++)
            {
                if (string.Equals(items[i].Id, savedId, System.StringComparison.Ordinal))
                {
                    selectedIndex = i;
                    break;
                }
            }
            OnbInputDeviceCombo.SelectedIndex = selectedIndex;
            OnbInputDeviceCombo.Text = items[selectedIndex].Display;
        }
        finally
        {
            _suppressOnbDeviceChange = false;
        }
    }

    private async void OnOnbInputDeviceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressOnbDeviceChange || !_loaded)
        {
            return;
        }
        if (OnbInputDeviceCombo.SelectedItem is not OnbInputDeviceItem item)
        {
            return;
        }
        OnbInputDeviceCombo.Text = item.Display;

        var current = _prefs.Current;
        if (current.Audio.InputDeviceId == item.Id)
        {
            return;
        }
        var next = current with { Audio = current.Audio with { InputDeviceId = item.Id } };
#pragma warning disable CA1848, CA1873
        _logger.LogInformation("Onboarding input device changed → {Id} ({Display}).",
            item.Id ?? "(default)", item.Display);
#pragma warning restore CA1848, CA1873
        try
        {
            await _prefs.SaveAsync(next).ConfigureAwait(true);
            // Re-open the meter on the new device so the user sees the level
            // jump for the device they actually chose.
            StopMicCheck();
            StartMicCheck();
        }
        catch (IOException ex)
        {
#pragma warning disable CA1848, CA1873
            _logger.LogWarning(ex, "Onboarding PrefsStore save failed for InputDeviceId.");
#pragma warning restore CA1848, CA1873
        }
    }

    private NAudio.CoreAudioApi.MMDevice ResolveOnbMicDevice(
        NAudio.CoreAudioApi.MMDeviceEnumerator enumerator,
        string? preferredId)
    {
        if (!string.IsNullOrEmpty(preferredId))
        {
            try
            {
                var device = enumerator.GetDevice(preferredId);
                if (device is not null
                    && device.State == NAudio.CoreAudioApi.DeviceState.Active
                    && device.DataFlow == NAudio.CoreAudioApi.DataFlow.Capture)
                {
                    return device;
                }
                device?.Dispose();
            }
            catch (COMException ex)
            {
#pragma warning disable CA1848, CA1873
                _logger.LogWarning(ex,
                    "Onboarding mic: preferred device {Id} not resolvable; using OS default.",
                    preferredId);
#pragma warning restore CA1848, CA1873
            }
        }
        return enumerator.GetDefaultAudioEndpoint(
            NAudio.CoreAudioApi.DataFlow.Capture,
            NAudio.CoreAudioApi.Role.Communications);
    }
}
