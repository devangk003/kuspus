#pragma warning disable CA1848
#pragma warning disable CA1873

using AppKit;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using KusPus.Coordination;
using KusPus.Mac.Platform;
using Microsoft.Extensions.Logging;

namespace KusPus.Mac.App;

/// <summary>General + Audio tabs.</summary>
internal sealed partial class PreferencesWindowController
{
    private HotkeyPickerView? _hotkeyPicker;

    // ── General ─────────────────────────────────────────────────────────────

    private NSStackView BuildGeneralTab()
    {
        var s = _prefs.Current;

        // ── Dictation: the shortcut (and, when the event tap isn't running, why) ──
        _hotkeyPicker = new HotkeyPickerView(_prefs, _hotkey, _logger, string.Empty, hero: false, compact: true);
        _hotkeyWarningText = UI.Paragraph(" ", Fonts.Supporting, "SecondaryText", lineSpacing: 2);
        _hotkeyWarningRow = new SettingsRow(
            Settings.Status(StatusKind.Warning, "Shortcut inactive"),
            _hotkeyWarningText,
            UI.Button("Relaunch KusPus", UI.ButtonKind.Secondary, MacRelaunch.Relaunch),
            "Shortcut inactive")
        { Hidden = true };
        _shortcutRow = SettingsRow.Custom(_hotkeyPicker);
        var dictation = Settings.Group(_hotkeyWarningRow, _shortcutRow);

        // ── Permissions: the same three TCC grants onboarding asks for ──
        _permissionRows =
        [
            PermissionRowView.ForSettings("Microphone", Permissions.HasMicrophone,
                () => { _ = Permissions.RequestMicrophoneAsync(); Permissions.OpenSettings(Permissions.MicrophoneSettingsUrl); },
                "Records while you hold the shortcut."),
            PermissionRowView.ForSettings("Input Monitoring", Permissions.HasInputMonitoring,
                () => { Permissions.RequestInputMonitoring(); Permissions.OpenSettings(Permissions.InputMonitoringSettingsUrl); },
                "Lets the shortcut work in every app."),
            PermissionRowView.ForSettings("Accessibility", Permissions.HasAccessibility,
                () => { Permissions.RequestAccessibility(); Permissions.OpenSettings(Permissions.AccessibilitySettingsUrl); },
                "Pastes the text for you. Without it, KusPus copies to the clipboard instead."),
        ];
        var permissions = Settings.Group(_permissionRows.Select(SettingsRow.Custom).ToArray());
        // Summary under the heading so the overall state reads before the rows do.
        _permissionSummary = UI.Label(" ", Fonts.Supporting, "MutedText", wraps: true);
        var permissionHeader = UI.VStack(4, UI.Label("Permissions", Fonts.Title, "PrimaryText"), _permissionSummary);
        var permissionSection = UI.VStack(8, permissionHeader, permissions);
        permissionHeader.WidthAnchor.ConstraintEqualTo(permissionSection.WidthAnchor).Active = true;
        permissions.WidthAnchor.ConstraintEqualTo(permissionSection.WidthAnchor).Active = true;
        permissionSection.EdgeInsets = new NSEdgeInsets(0, 0, (nfloat)Settings.SectionGap, 0);
        RefreshPermissions();

        // ── General: login, appearance, motion ──
        var autostart = UI.Toggle(s.Autostart, on => _ = OnAutostartChangedAsync(on));
        var launchRow = new SettingsRow("Open at login", "The menu bar icon is always there while KusPus runs.", autostart);

        var segments = NSSegmentedControl.FromLabels(["Auto", "Light", "Dark"], NSSegmentSwitchTracking.SelectOne, () => { });
        segments.TranslatesAutoresizingMaskIntoConstraints = false;
        segments.SegmentStyle = NSSegmentStyle.Rounded;
        segments.SelectedSegment = s.Ui.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        segments.Activated += (_, _) => _ = OnThemeChangedAsync(segments.SelectedSegment switch { 1 => "light", 2 => "dark", _ => "auto" });
        var themeRow = new SettingsRow("Appearance", "Auto follows your Mac.", segments);

        var reduce = UI.Toggle(s.Privacy.ReducePillAnimations, on => _ = SavePrivacyAsync(p => p with { ReducePillAnimations = on }, "ReducePillAnimations"));
        var motionRow = new SettingsRow("Reduce motion", "Calmer animations in the notch indicator. Also follows the macOS Reduce Motion setting.", reduce);

        return Tab(
            Settings.Section("Dictation", dictation),
            permissionSection,
            Settings.Section("General", Settings.Group(launchRow, themeRow, motionRow)));
    }

    private SettingsRow? _hotkeyWarningRow;
    private SettingsRow? _shortcutRow;
    private NSTextField? _hotkeyWarningText;
    private PermissionRowView[] _permissionRows = Array.Empty<PermissionRowView>();
    private NSTextField? _permissionSummary;
    private NSTimer? _permissionTimer;

    private void StartPermissionPolling()
    {
        _permissionTimer?.Invalidate();
        _permissionTimer = Timers.Repeating(TimeSpan.FromSeconds(2), _ => RefreshPermissions());
    }

    private void StopPermissionPolling()
    {
        _permissionTimer?.Invalidate();
        _permissionTimer = null;
    }

    private void RefreshPermissions()
    {
        // One primary action per screen: only the first missing grant gets the accent.
        bool primaryTaken = false;
        foreach (var row in _permissionRows)
        {
            row.Refresh();
            bool primary = !primaryTaken && !row.IsGranted;
            row.SetPrimary(primary);
            primaryTaken |= primary;
        }
        if (_permissionSummary is not null && _permissionRows.Length > 0)
        {
            int granted = _permissionRows.Count(r => r.IsGranted);
            int total = _permissionRows.Length;
            _permissionSummary.StringValue = granted == total
                ? "All granted. KusPus can record, hear the shortcut and paste for you."
                : $"{granted} of {total} granted. Grant the rest so dictation works in every app.";
            _permissionSummary.TextColor = ThemeColors.Get(granted == total ? "MutedText" : "WarningText");
        }
        if (_hotkeyWarningRow is null || _hotkeyWarningText is null || _shortcutRow is null)
        {
            return;
        }
        bool tapActive = _hotkey is not MacHotkeyEngine mac || mac.IsTapActive;
        _hotkeyWarningRow.Hidden = tapActive;
        _shortcutRow.ShowSeparator = !tapActive;
        if (!tapActive)
        {
            _hotkeyWarningText.StringValue = Permissions.HasInputMonitoring()
                ? "Input Monitoring is granted, but macOS applies it after a relaunch."
                : "Grant Input Monitoring below, then relaunch if the shortcut still doesn't respond.";
        }
    }

    private async Task OnAutostartChangedAsync(bool enabled)
    {
        var current = _prefs.Current;
        if (current.Autostart == enabled)
        {
            return;
        }
        _logger.LogInformation("Autostart toggled → {Value}.", enabled);
        // Register the login item first — if it fails we'd rather NOT have a
        // settings.json that lies about it (same ordering as the HKCU\Run write).
        var error = MacAutostart.Set(enabled);
        if (error is not null)
        {
            _logger.LogWarning("SMAppService register/unregister failed: {Error}", error);
            RebuildContent();
            return;
        }
        try
        {
            await _prefs.UpdateAsync(s => s with { Autostart = enabled }).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for Autostart.");
        }
    }

    private async Task OnThemeChangedAsync(string theme)
    {
        var current = _prefs.Current;
        if (current.Ui.Theme == theme)
        {
            return;
        }
        _logger.LogInformation("Theme set → {Theme}.", theme);
        try
        {
            // AppDelegate applies NSApp.appearance from PrefsStore.Changes; the
            // appearance change then rebuilds this window's content.
            await _prefs.UpdateAsync(s => s with { Ui = s.Ui with { Theme = theme } }).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for Theme.");
        }
    }

    // ── Audio ────────────────────────────────────────────────────────────────

    private NSPopUpButton? _deviceCombo;
    private NSTextField? _audioDeviceSubtitle;
    private NSStackView? _liveIndicator;
    private MeterView? _meterView;
    private MacLevelMeter? _meter;
    private NSTimer? _meterTimer;
    private NSTimer? _meterAutoStop;
    private NSButton? _meterButton;
    private const double MeterAutoStopSec = 15;
    private float _peakHold;
    private IReadOnlyList<CoreAudioDevices.InputDevice> _deviceItems = Array.Empty<CoreAudioDevices.InputDevice>();

    private NSTextField? _testSubtitle;
    private NSButton? _testButton;
    private CardView? _testResultContainer;
    private NSTextField? _testResultText;
    private CancellationTokenSource? _testCts;
    private NSTimer? _testCountdown;
    private int _testRemaining;
    private enum TestState { Idle, Recording, Transcribing, Result, Error }
    private TestState _testState = TestState.Idle;

    private const string TestIdleText = "Record 5 seconds and see what KusPus hears.";

    private NSStackView BuildAudioTab()
    {
        _deviceCombo = new NSPopUpButton(new CGRect(0, 0, 240, 24), false) { TranslatesAutoresizingMaskIntoConstraints = false };
        _deviceCombo.WidthAnchor.ConstraintEqualTo(240).Active = true;
        _deviceCombo.AddItem("Default device (follows macOS)");
        _deviceCombo.Activated += (_, _) => _ = OnInputDeviceChangedAsync();
        var deviceRow = new SettingsRow("Input device", "Used whenever you dictate.", _deviceCombo);
        _audioDeviceSubtitle = deviceRow.Secondary;

        // Privacy: the microphone stays closed until the user asks. Opening this tab never
        // starts it; the check stops itself after 15 s, on leaving the tab, or on Stop.
        _liveIndicator = Settings.Status(StatusKind.Ok, "Listening");
        _liveIndicator.Hidden = true;
        _meterView = new MeterView(160, 24);
        _meterButton = UI.Button("Check level", UI.ButtonKind.Secondary, ToggleAudioMeter);
        var levelRow = new SettingsRow("Input level",
            "The microphone stays off until you check. It stops by itself after 15 seconds and nothing is recorded.",
            UI.HStack(12, _meterView, _meterButton), titleAccessory: _liveIndicator);

        _testButton = UI.Button("Record 5 seconds", UI.ButtonKind.Primary, () => _ = OnTestTranscriptionClickAsync());
        var testRow = new SettingsRow("Test dictation", TestIdleText, _testButton);
        _testSubtitle = testRow.Secondary;
        _testResultText = UI.Label(string.Empty, Fonts.Body, "PrimaryText", wraps: true);
        _testResultContainer = new CardView(_testResultText, "SurfaceInput", "BorderSubtle", radius: 6, padX: 12, padY: 8) { Hidden = true };
        testRow.AddBelow(_testResultContainer);

        return Tab(Settings.Section("Microphone", Settings.Group(deviceRow, levelRow, testRow)));
    }

    private async Task OpenAudioTabAsync()
    {
        // Device enumeration is a CoreAudio round-trip — off the main thread so the
        // panel paints first (mirrors MainWindow.OpenAudioTabAsync).
        var items = await Task.Run(CoreAudioDevices.EnumerateInputDevices).ConfigureAwait(true);
        if (_currentTab != "audio" || _deviceCombo is null)
        {
            return;
        }
        _deviceItems = items;
        _suppressEvents = true;
        try
        {
            _deviceCombo.RemoveAllItems();
            _deviceCombo.AddItem("Default device (follows macOS)");
            foreach (var d in items)
            {
                _deviceCombo.AddItem(d.Name);
            }
            var saved = _prefs.Current.Audio.InputDeviceId;
            int idx = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Uid == saved)
                {
                    idx = i + 1;
                    break;
                }
            }
            _deviceCombo.SelectItem(idx);
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void ToggleAudioMeter()
    {
        if (_meter?.IsRunning == true)
        {
            StopAudioMeter();
        }
        else
        {
            StartAudioMeter();
        }
    }

    private async Task OnInputDeviceChangedAsync()
    {
        if (_suppressEvents || _deviceCombo is null)
        {
            return;
        }
        var idx = (int)_deviceCombo.IndexOfSelectedItem;
        string? id = idx <= 0 || idx > _deviceItems.Count ? null : _deviceItems[idx - 1].Uid;
        var current = _prefs.Current;
        if (current.Audio.InputDeviceId == id)
        {
            return;
        }
        _logger.LogInformation("Input device changed → {Id}.", id ?? "(default)");
        try
        {
            await _prefs.UpdateAsync(s => s with { Audio = s.Audio with { InputDeviceId = id } }).ConfigureAwait(true);
            if (_meter?.IsRunning == true)
            {
                StopAudioMeter();
                StartAudioMeter();
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for InputDeviceId.");
        }
    }

    private void StartAudioMeter() => _ = StartAudioMeterAsync();

    private async Task StartAudioMeterAsync()
    {
        if (_meterView is null || _liveIndicator is null)
        {
            return;
        }
        if (_meter is null)
        {
            _meter = new MacLevelMeter();
            // Device unplugged / default changed while the tab is open → restart on the new device.
            _meter.ConfigurationChanged += (_, _) => DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                // Only follow the device if the user's level check is still running.
                if (_meterTimer is not null && _currentTab == "audio" && _window.IsVisible)
                {
                    StopAudioMeter();
                    StartAudioMeter();
                }
            });
        }
        var error = await _meter.StartAsync(_prefs.Current.Audio.InputDeviceId).ConfigureAwait(true);
        if (_currentTab != "audio" || !_window.IsVisible || _meterView is null || _liveIndicator is null)
        {
            // Window closed / tab left while the mic was opening: don't keep it running hidden.
            _meter.Stop();
            return;
        }
        if (error is not null)
        {
            _logger.LogWarning("Audio meter setup failed: {Reason}", error);
            if (_audioDeviceSubtitle is not null)
            {
                _audioDeviceSubtitle.StringValue = error;
            }
            return;
        }
        _meterTimer?.Invalidate();
        _meterTimer = Timers.Repeating(TimeSpan.FromMilliseconds(66), _ => OnMeterTick());
        _liveIndicator.Hidden = false;
        if (_meterButton is not null)
        {
            _meterButton.Title = "Stop";
        }
        _meterAutoStop?.Invalidate();
        _meterAutoStop = Timers.Once(TimeSpan.FromSeconds(MeterAutoStopSec), _ =>
        {
            _meterAutoStop = null;
            StopAudioMeter();
        });
    }

    private void StopAudioMeter()
    {
        _meterAutoStop?.Invalidate();
        _meterAutoStop = null;
        if (_meterButton is not null)
        {
            _meterButton.Title = "Check level";
        }
        _meterTimer?.Invalidate();
        _meterTimer = null;
        _meter?.Stop();
        _peakHold = 0;
        if (_liveIndicator is not null)
        {
            _liveIndicator.Hidden = true;
        }
        _meterView?.Set(0, 0);
    }

    private void OnMeterTick()
    {
        if (_meter is null || _meterView is null)
        {
            return;
        }
        float peak = _meter.TakePeak();
        _peakHold = peak > _peakHold ? peak : _peakHold * 0.94f;
        // Mic input rarely peaks past ~0.6 in normal speech — gain so the meter reads "alive".
        _meterView.Set(Math.Clamp(peak * 2.5, 0, 1), Math.Clamp(_peakHold * 2.5, 0, 1));
    }

    private async Task OnTestTranscriptionClickAsync()
    {
        if (_testState is TestState.Recording or TestState.Transcribing)
        {
            _testCts?.Cancel();
            return;
        }
        await RunTestTranscriptionAsync().ConfigureAwait(true);
    }

    private async Task RunTestTranscriptionAsync()
    {
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;

        var resolved = _models.Resolve(_prefs.Current.Models.ActiveModelId, _prefs.Current.Models.CustomModelPath);
        if (!resolved.Success)
        {
            SetTestState(TestState.Error, resolved.Error ?? "Active model unavailable.");
            return;
        }

        // Resume the level check afterwards only if the user had it running.
        bool meterWasRunning = _meter?.IsRunning == true;
        StopAudioMeter();
        string? wav = null;
        try
        {
            SetTestState(TestState.Recording, "Recording… 5 seconds remaining");
            var start = await _audio.StartAsync(ct).ConfigureAwait(true);
            if (!start.Success)
            {
                SetTestState(TestState.Error, start.Error ?? "Mic open failed.");
                return;
            }
            StartTestCountdown();
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(true);
            StopTestCountdown();

            var stop = await _audio.StopAsync().ConfigureAwait(true);
            if (!stop.Success)
            {
                SetTestState(TestState.Error, stop.Error ?? "Mic stop failed.");
                return;
            }
            wav = stop.Value!.WavPath;
            SetTestState(TestState.Transcribing, "Transcribing…");
            var transcript = await _whisper.TranscribeAsync(wav, resolved.Value!, ct).ConfigureAwait(true);
            if (!transcript.Success)
            {
                SetTestState(TestState.Error, transcript.Error ?? "Transcribe failed.");
                return;
            }
            var text = TranscriptFilter.Clean(transcript.Value!);
            SetTestState(TestState.Result, text.Length == 0 ? "(no speech detected)" : text);
        }
        catch (OperationCanceledException)
        {
            StopTestCountdown();
            try { await _audio.StopAsync().ConfigureAwait(true); } catch (InvalidOperationException) { }
            SetTestState(TestState.Idle, TestIdleText);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Test transcription failed unexpectedly.");
            SetTestState(TestState.Error, "Unexpected error — see logs.");
        }
        finally
        {
            // Also on cancel/failure, so a test recording never lingers in the temp folder.
            if (wav is not null)
            {
                try { File.Delete(wav); } catch (IOException) { }
            }
            _testCts?.Dispose();
            _testCts = null;
            if (meterWasRunning && _window.IsVisible && _currentTab == "audio")
            {
                StartAudioMeter();
            }
        }
    }

    private void StartTestCountdown()
    {
        _testRemaining = 5;
        _testCountdown?.Invalidate();
        _testCountdown = Timers.Repeating(TimeSpan.FromSeconds(1), _ =>
        {
            _testRemaining--;
            if (_testRemaining <= 0)
            {
                StopTestCountdown();
                return;
            }
            if (_testState == TestState.Recording && _testSubtitle is not null)
            {
                _testSubtitle.StringValue = $"Recording… {_testRemaining} second{(_testRemaining == 1 ? string.Empty : "s")} remaining";
            }
        });
    }

    private void StopTestCountdown()
    {
        _testCountdown?.Invalidate();
        _testCountdown = null;
    }

    private void SetTestState(TestState state, string subtitleOrResult)
    {
        _testState = state;
        if (_testSubtitle is null || _testButton is null || _testResultContainer is null || _testResultText is null)
        {
            return;
        }
        switch (state)
        {
            case TestState.Idle:
                _testSubtitle.StringValue = subtitleOrResult;
                _testButton.Title = "Record 5 seconds";
                UI.ApplyKind(_testButton, UI.ButtonKind.Primary);
                _testResultContainer.Hidden = true;
                break;
            case TestState.Recording:
            case TestState.Transcribing:
                _testSubtitle.StringValue = subtitleOrResult;
                _testButton.Title = "Cancel";
                UI.ApplyKind(_testButton, UI.ButtonKind.Secondary);
                _testResultContainer.Hidden = true;
                break;
            case TestState.Result:
                _testSubtitle.StringValue = "KusPus heard:";
                _testButton.Title = "Record again";
                UI.ApplyKind(_testButton, UI.ButtonKind.Secondary);
                _testResultText.StringValue = subtitleOrResult;
                _testResultText.TextColor = ThemeColors.Get("PrimaryText");
                _testResultContainer.Hidden = false;
                break;
            case TestState.Error:
                _testSubtitle.StringValue = "The test didn't work.";
                _testButton.Title = "Try again";
                UI.ApplyKind(_testButton, UI.ButtonKind.Secondary);
                _testResultText.StringValue = subtitleOrResult;
                _testResultText.TextColor = ThemeColors.Get("ErrorText");
                _testResultContainer.Hidden = false;
                break;
        }
    }

    /// <summary>Discord-style meter: 6 pt track, mint fill from the left, 2×12 peak-hold tick.</summary>
    internal sealed class MeterView : FlippedView
    {
        private readonly NSView _track;
        private readonly NSView _fill;
        private readonly NSView _peak;
        private readonly double _width;

        public MeterView(double width, double height)
        {
            _width = width;
            TranslatesAutoresizingMaskIntoConstraints = false;
            WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
            HeightAnchor.ConstraintEqualTo((nfloat)height).Active = true;
            _track = Bar("MeterTrack");
            _fill = Bar("MintText");
            _peak = new NSView { WantsLayer = true, AlphaValue = 0 };
            _peak.Layer!.BackgroundColor = ThemeColors.Cg("MintText");
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.LevelIndicatorRole;
            AccessibilityLabel = "Input level";
            AccessibilityValue = NSNumber.FromInt32(0);
            AddSubview(_track);
            AddSubview(_fill);
            AddSubview(_peak);
            double midY = height / 2;
            _track.Frame = new CGRect(0, midY - 3, width, 6);
            _fill.Frame = new CGRect(0, midY - 3, 0, 6);
            _peak.Frame = new CGRect(0, midY - 6, 2, 12);
        }

        public void Set(double level, double hold)
        {
            var f = _fill.Frame;
            _fill.Frame = new CGRect(f.X, f.Y, level * _width, f.Height);
            var p = _peak.Frame;
            _peak.Frame = new CGRect(Math.Max(0, (hold * _width) - 1), p.Y, 2, 12);
            _peak.AlphaValue = hold > 0.04 ? (nfloat)0.7 : 0;
            AccessibilityValue = NSNumber.FromInt32((int)Math.Round(level * 100));
        }

        private static NSView Bar(string colorKey)
        {
            var v = new NSView { WantsLayer = true };
            v.Layer!.CornerRadius = 3;
            v.Layer.BackgroundColor = ThemeColors.Cg(colorKey);
            return v;
        }
    }
}
