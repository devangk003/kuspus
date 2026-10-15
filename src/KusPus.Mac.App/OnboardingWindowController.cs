#pragma warning disable CA1848
#pragma warning disable CA1873

using AppKit;
using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using KusPus.Coordination;
using KusPus.Core.Platform;
using KusPus.Mac.Platform;
using KusPus.Persistence;
using KusPus.Whisper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.App;

/// <summary>
/// First-launch onboarding — AppKit port of <c>OnboardingWindow</c> (docs/APP_DESIGN.md §4)
/// with two macOS-only permission steps inserted after the microphone check:
/// <list type="number">
/// <item>Welcome</item><item>Hotkey</item><item>Microphone</item>
/// <item><b>Input Monitoring</b> (needed for the hotkey tap)</item>
/// <item><b>Accessibility</b> (needed to auto-paste; skippable → clipboard-only)</item>
/// <item>Login item</item><item>Crash reports</item><item>Try it</item><item>Done</item>
/// </list>
/// Runs as a modal session (<see cref="RunModal"/>). Finish and Skip both mark
/// onboarding complete (show-once-ever, re-runnable from About); Esc leaves it
/// incomplete so it reappears next launch — same contract as Windows.
/// </summary>
internal sealed class OnboardingWindowController : IDisposable
{
    private const int StepCount = 9;

    private readonly IPrefsStore _prefs;
    private readonly IHotkeyEngine _hotkey;
    private readonly IAudioRecorder _audio;
    private readonly ITranscriber _whisper;
    private readonly IModelManager _models;
    private readonly ILogger _logger;

    private readonly NSWindow _window;
    private readonly ThemedRootView _root = new();
    private readonly NSStackView _dots = UI.HStack(8);
    private readonly FlippedView _stepHost = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSButton _skip;
    private readonly NSButton _back;
    private readonly NSButton _next;
    private readonly NSView[] _dotViews = new NSView[StepCount];

    private int _currentStep;
    private bool _finished;
    private NSObject? _escMonitor;
    private HotkeyPickerView? _picker;

    public OnboardingWindowController(
        IPrefsStore prefs,
        IHotkeyEngine hotkey,
        IAudioRecorder audio,
        ITranscriber whisper,
        IModelManager models,
        ILogger<OnboardingWindowController>? logger = null)
    {
        _prefs = prefs;
        _hotkey = hotkey;
        _audio = audio;
        _whisper = whisper;
        _models = models;
        _logger = logger ?? NullLogger<OnboardingWindowController>.Instance;

        _window = new NSWindow(
            new CGRect(0, 0, 720, 520),
            NSWindowStyle.Titled | NSWindowStyle.FullSizeContentView,
            NSBackingStore.Buffered,
            false)
        {
            Title = "KusPus — Setup",
            TitlebarAppearsTransparent = true,
            TitleVisibility = NSWindowTitleVisibility.Hidden,
            MovableByWindowBackground = true,
            Level = NSWindowLevel.Floating,
        };
        _window.ReleaseWhenClosed(false);
        foreach (var b in new[] { NSWindowButton.CloseButton, NSWindowButton.MiniaturizeButton, NSWindowButton.ZoomButton })
        {
            var button = _window.StandardWindowButton(b);
            if (button is not null)
            {
                button.Hidden = true;
            }
        }
        _window.Center();
        _window.ContentView = _root;

        _skip = UI.Button("Skip onboarding", UI.ButtonKind.Ghost, () => _ = FinishAsync(completed: true));
        _skip.ContentTintColor = ThemeColors.Get("MutedText");
        _back = UI.Button("Back", UI.ButtonKind.Secondary, () => ShowStep(_currentStep - 1));
        _next = UI.Button("Next", UI.ButtonKind.Primary, OnNext);
        _next.KeyEquivalent = "\r";

        BuildShell();
        _root.AppearanceChanged += () => DispatchQueue.MainQueue.DispatchAsync(() => { BuildShell(); ShowStep(_currentStep); });
    }

    /// <summary>Runs the modal session on the main thread; returns when the user finishes, skips or escapes.</summary>
    public bool RunModal()
    {
        ShowStep(0);
        _escMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, e =>
        {
            if (e.KeyCode == MacKeyCodes.Escape && _picker?.IsListening != true)
            {
                _ = FinishAsync(completed: false);
                return null!;
            }
            return e;
        });
        NSApplication.SharedApplication.Activate();
        _window.MakeKeyAndOrderFront(null);
        NSApplication.SharedApplication.RunModalForWindow(_window);
        _window.OrderOut(null);
        return _finished;
    }

    public void Dispose()
    {
        if (_escMonitor is not null)
        {
            NSEvent.RemoveMonitor(_escMonitor);
            _escMonitor = null;
        }
        StopMicCheck();
        StopPermissionPolling();
        _tryItTimer?.Invalidate();
        _window.Close();
    }

    // ── shell ────────────────────────────────────────────────────────────────

    private void BuildShell()
    {
        foreach (var v in _root.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }
        _root.Layer!.BackgroundColor = ThemeColors.Cg("AppBg");

        foreach (var d in _dots.ArrangedSubviews.ToArray())
        {
            _dots.RemoveArrangedSubview(d);
            d.RemoveFromSuperview();
        }
        for (int i = 0; i < StepCount; i++)
        {
            var dot = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
            dot.Layer!.CornerRadius = 3;
            int step = i;
            dot.AddGestureRecognizer(new NSClickGestureRecognizer(() => ShowStep(step)));
            _dotViews[i] = dot;
            _dots.AddArrangedSubview(dot);
        }

        var footerDivider = UI.Divider();
        var footerRow = UI.HStack(10, _skip, UI.Spacer(), _back, _next);
        var footer = UI.VStack(0, footerDivider, footerRow);
        footerDivider.WidthAnchor.ConstraintEqualTo(footer.WidthAnchor).Active = true;
        footerRow.WidthAnchor.ConstraintEqualTo(footer.WidthAnchor, 1, -56).Active = true;
        footer.Alignment = NSLayoutAttribute.CenterX;
        footer.SetCustomSpacing(16, footerDivider);
        footer.EdgeInsets = new NSEdgeInsets(0, 0, 16, 0);

        _root.AddSubview(_dots);
        _root.AddSubview(_stepHost);
        _root.AddSubview(footer);
        NSLayoutConstraint.ActivateConstraints(
        [
            _dots.CenterXAnchor.ConstraintEqualTo(_root.CenterXAnchor),
            _dots.TopAnchor.ConstraintEqualTo(_root.TopAnchor, 24),
            _stepHost.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor, 40),
            _stepHost.TrailingAnchor.ConstraintEqualTo(_root.TrailingAnchor, -40),
            _stepHost.TopAnchor.ConstraintEqualTo(_dots.BottomAnchor, 28),
            _stepHost.BottomAnchor.ConstraintEqualTo(footer.TopAnchor),
            footer.LeadingAnchor.ConstraintEqualTo(_root.LeadingAnchor),
            footer.TrailingAnchor.ConstraintEqualTo(_root.TrailingAnchor),
            footer.BottomAnchor.ConstraintEqualTo(_root.BottomAnchor),
        ]);
    }

    private void UpdateDots()
    {
        for (int i = 0; i < StepCount; i++)
        {
            var dot = _dotViews[i];
            foreach (var c in dot.Constraints)
            {
                c.Active = false;
            }
            bool current = i == _currentStep;
            dot.WidthAnchor.ConstraintEqualTo(current ? 22 : 6).Active = true;
            dot.HeightAnchor.ConstraintEqualTo(6).Active = true;
            dot.Layer!.BackgroundColor = ThemeColors.Cg(current ? "Mint" : i < _currentStep ? "MintBorder" : "BorderStrong");
        }
    }

    // ── navigation ───────────────────────────────────────────────────────────

    private void ShowStep(int index)
    {
        // Every ShowStep rebuilds the step view (also on appearance change), discarding the
        // current picker — so always cancel an in-flight rebind, or the old picker keeps its
        // key monitor and the global hotkey stays cleared.
        _picker?.CancelListenMode();
        // Leaving the step mid "Try it" must not keep the mic open / whisper running.
        _tryItCts?.Cancel();
        _currentStep = Math.Clamp(index, 0, StepCount - 1);
        StopMicCheck();
        StopPermissionPolling();

        foreach (var v in _stepHost.Subviews.ToArray())
        {
            v.RemoveFromSuperview();
        }
        var step = _currentStep switch
        {
            0 => BuildWelcome(),
            1 => BuildHotkey(),
            2 => BuildMicrophone(),
            3 => BuildInputMonitoring(),
            4 => BuildAccessibility(),
            5 => BuildLoginItem(),
            6 => BuildCrashReports(),
            7 => BuildTryIt(),
            _ => BuildDone(),
        };
        _stepHost.AddSubview(step);
        NSLayoutConstraint.ActivateConstraints(
        [
            step.LeadingAnchor.ConstraintEqualTo(_stepHost.LeadingAnchor),
            step.TrailingAnchor.ConstraintEqualTo(_stepHost.TrailingAnchor),
            step.TopAnchor.ConstraintEqualTo(_stepHost.TopAnchor),
        ]);

        _back.Hidden = _currentStep == 0;
        _next.Title = _currentStep == StepCount - 1 ? "Finish" : "Next";
        _skip.Hidden = _currentStep == StepCount - 1;
        UpdateDots();
    }

    private void OnNext()
    {
        if (_currentStep >= StepCount - 1)
        {
            _ = FinishAsync(completed: true);
            return;
        }
        ShowStep(_currentStep + 1);
    }

    // Must not block the main thread on the save: PrefsStore.Changes fires on the
    // thread that completed the write, and subscribers hop back to the main queue —
    // a synchronous wait here would deadlock against them.
    private async Task FinishAsync(bool completed)
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        _picker?.CancelListenMode();
        StopMicCheck();
        StopPermissionPolling();
        _tryItCts?.Cancel();
        try
        {
            var current = _prefs.Current;
            await _prefs.UpdateAsync(s => s with
            {
                // Esc on a re-run ("Run onboarding again") must not un-complete it, or
                // onboarding would reappear on every launch.
                Onboarding = s.Onboarding with { Completed = completed || s.Onboarding.Completed },
            }).ConfigureAwait(true);
            _logger.LogInformation("Onboarding closed — completed={Completed}.", completed);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed at onboarding finish.");
        }
        // We're past an await, i.e. not inside an event handler: stopModal wouldn't end
        // runModalForWindow until the next input event arrives. abortModal does.
        NSApplication.SharedApplication.AbortModal();
    }

    // ── step scaffolding ────────────────────────────────────────────────────

    private NSStackView Step(string title, string description, params NSView[] body)
    {
        var eyebrow = UI.Label($"STEP {_currentStep + 1} OF {StepCount}", Fonts.Eyebrow, "Mint");
        var heading = UI.Label(title, Fonts.Headline22, "PrimaryText");
        var desc = UI.Label(description, Fonts.Body, "SecondaryText", wraps: true);
        desc.PreferredMaxLayoutWidth = 540;
        var stack = UI.VStack(0, eyebrow, heading, desc);
        stack.SetCustomSpacing(6, eyebrow);
        stack.SetCustomSpacing(8, heading);
        stack.SetCustomSpacing(24, desc);
        foreach (var v in body)
        {
            stack.AddArrangedSubview(v);
        }
        return stack;
    }

    private static FlippedView Centered(NSView content, double width)
    {
        var box = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        box.AddSubview(content);
        content.WidthAnchor.ConstraintEqualTo((nfloat)width).Active = true;
        NSLayoutConstraint.ActivateConstraints(
        [
            content.CenterXAnchor.ConstraintEqualTo(box.CenterXAnchor),
            content.TopAnchor.ConstraintEqualTo(box.TopAnchor),
            content.BottomAnchor.ConstraintEqualTo(box.BottomAnchor),
        ]);
        return box;
    }

    private static CardView ToggleCard(string title, string subtitle, bool on, Action<bool> changed)
    {
        var left = UI.VStack(4, UI.Label(title, Fonts.Semibold(14), "PrimaryText"), UI.Label(subtitle, Fonts.RowSubtitle, "MutedText", wraps: true));
        var row = UI.Row(left, UI.Toggle(on, changed));
        return new CardView(row, radius: 10, padX: 22, padY: 20, borderWidth: 1.5);
    }

    // ── 1 · Welcome ──────────────────────────────────────────────────────────

    private NSStackView BuildWelcome()
    {
        var icon = NSImageView.FromImage(NSImage.ImageNamed(NSImageName.ApplicationIcon) ?? new NSImage());
        icon.TranslatesAutoresizingMaskIntoConstraints = false;
        icon.WidthAnchor.ConstraintEqualTo(56).Active = true;
        icon.HeightAnchor.ConstraintEqualTo(56).Active = true;
        var previewInner = UI.VStack(8, icon, UI.Label("KusPus", Fonts.Semibold(14), "PrimaryText"));
        previewInner.Alignment = NSLayoutAttribute.CenterX;
        var preview = new CardView(previewInner, radius: 10, padX: 28, padY: 32);

        var values = UI.HStack(10,
            ValueCard("Hotkey-driven", "Hold a chord, dictate, release. Paste lands wherever you were typing."),
            ValueCard("Local-first", "Transcription runs on this Mac. No cloud, no accounts, no telemetry."),
            ValueCard("Invisible", "A small island at the top of the screen shows recording state. That's it."));
        values.Distribution = NSStackViewDistribution.FillEqually;
        values.Alignment = NSLayoutAttribute.Top;

        var step = Step("Welcome to KusPus", "A quiet, local-first way to turn your voice into text — anywhere on your Mac.", preview, values);
        preview.WidthAnchor.ConstraintEqualTo(step.WidthAnchor).Active = true;
        values.WidthAnchor.ConstraintEqualTo(step.WidthAnchor).Active = true;
        step.SetCustomSpacing(18, preview);
        return step;
    }

    private static CardView ValueCard(string label, string detail) =>
        new(UI.VStack(4, UI.Label(label, Fonts.Semibold(12), "PrimaryText"), UI.Label(detail, Fonts.Hint, "MutedText", wraps: true)), padX: 14, padY: 12);

    // ── 2 · Hotkey ───────────────────────────────────────────────────────────

    private NSStackView BuildHotkey()
    {
        _picker = new HotkeyPickerView(_prefs, _hotkey, _logger,
            "Hold this chord anywhere on your Mac to start dictation. Release to stop and paste.", hero: true);
        return Step("Pick your hotkey", "Hold this chord anywhere on your Mac to start dictation. Release to stop and paste.",
            Centered(_picker, 440));
    }

    // ── 3 · Microphone ───────────────────────────────────────────────────────

    private NSPopUpButton? _micCombo;
    private IReadOnlyList<CoreAudioDevices.InputDevice> _micItems = Array.Empty<CoreAudioDevices.InputDevice>();
    private NSTextField? _micDeviceLabel;
    private PreferencesWindowController.MeterView? _micMeter;
    private NSStackView? _micSuccess;
    private NSStackView? _micError;
    private NSButton? _micOpenSettings;
    private MacLevelMeter? _meter;
    private NSTimer? _meterTimer;
    private bool _suppressMicChange;

    private NSStackView BuildMicrophone()
    {
        _micCombo = new NSPopUpButton(new CGRect(0, 0, 440, 24), false) { TranslatesAutoresizingMaskIntoConstraints = false };
        _micCombo.AddItem("Loading microphones…");
        _micCombo.Activated += (_, _) => _ = OnMicDeviceChangedAsync();
        var chooser = UI.VStack(8, UI.Eyebrow("INPUT DEVICE"), _micCombo);
        _micCombo.WidthAnchor.ConstraintEqualTo(chooser.WidthAnchor).Active = true;

        _micDeviceLabel = UI.Eyebrow("LOADING…");
        _micMeter = new PreferencesWindowController.MeterView(392, 14);
        _micSuccess = UI.HStack(10, new DotView("Mint"), UI.Label("Receiving audio", Fonts.Medium(12), "Mint"));
        _micError = UI.HStack(10, new DotView("ErrorRed"), UI.Label("Microphone blocked", Fonts.Medium(12), "ErrorRed"));
        _micError.Hidden = true;
        var cardInner = UI.VStack(0, _micDeviceLabel, _micMeter, _micSuccess, _micError);
        cardInner.SetCustomSpacing(12, _micDeviceLabel);
        cardInner.SetCustomSpacing(16, _micMeter);
        var card = new CardView(cardInner, radius: 10, padX: 24, padY: 22);

        _micOpenSettings = UI.Button("Open System Settings", UI.ButtonKind.Primary, () => Permissions.OpenSettings(Permissions.MicrophoneSettingsUrl));
        _micOpenSettings.Hidden = true;

        var step = Step("Check your mic",
            "We need permission to listen while you hold the hotkey. Nothing is recorded otherwise. Pick which mic to use — you can change it later in Preferences → Audio.",
            Centered(chooser, 440), Centered(card, 440), Centered(_micOpenSettings, 440));
        step.SetCustomSpacing(16, step.ArrangedSubviews[3]);
        step.SetCustomSpacing(14, step.ArrangedSubviews[4]);
        step.SetCustomSpacing(20, step.ArrangedSubviews[2]);
        _ = OpenMicStepAsync();
        return step;
    }

    private async Task OpenMicStepAsync()
    {
        var items = await Task.Run(CoreAudioDevices.EnumerateInputDevices).ConfigureAwait(true);
        if (_currentStep != 2 || _micCombo is null)
        {
            return;
        }
        _micItems = items;
        _suppressMicChange = true;
        try
        {
            _micCombo.RemoveAllItems();
            _micCombo.AddItem("Default device (follows macOS)");
            foreach (var d in items)
            {
                _micCombo.AddItem(d.Name);
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
            _micCombo.SelectItem(idx);
        }
        finally
        {
            _suppressMicChange = false;
        }
        await StartMicCheckAsync().ConfigureAwait(true); // prompts for Microphone if never asked
    }

    private async Task OnMicDeviceChangedAsync()
    {
        if (_suppressMicChange || _micCombo is null)
        {
            return;
        }
        var idx = (int)_micCombo.IndexOfSelectedItem;
        string? id = idx <= 0 || idx > _micItems.Count ? null : _micItems[idx - 1].Uid;
        var current = _prefs.Current;
        if (current.Audio.InputDeviceId == id)
        {
            return;
        }
        try
        {
            await _prefs.UpdateAsync(s => s with { Audio = s.Audio with { InputDeviceId = id } }).ConfigureAwait(true);
            StopMicCheck();
            StartMicCheck();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Onboarding PrefsStore save failed for InputDeviceId.");
        }
    }

    private void StartMicCheck() => _ = StartMicCheckAsync();

    private async Task StartMicCheckAsync()
    {
        if (_micMeter is null || _micDeviceLabel is null)
        {
            return;
        }
        if (_meter is null)
        {
            _meter = new MacLevelMeter();
            _meter.ConfigurationChanged += (_, _) => DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                if (_currentStep == 2 && !_finished)
                {
                    StopMicCheck();
                    StartMicCheck();
                }
            });
        }
        var error = await _meter.StartAsync(_prefs.Current.Audio.InputDeviceId).ConfigureAwait(true);
        if (_currentStep != 2 || _micMeter is null || _micDeviceLabel is null)
        {
            _meter.Stop();
            return;
        }
        if (error is not null)
        {
            _logger.LogWarning("Onboarding mic check failed: {Error}", error);
            ShowMicError();
            return;
        }
        _micDeviceLabel.StringValue = _meter.DeviceName.ToUpperInvariant();
        _micSuccess!.Hidden = false;
        _micError!.Hidden = true;
        _micOpenSettings!.Hidden = true;
        _meterTimer?.Invalidate();
        _meterTimer = Timers.Repeating(TimeSpan.FromMilliseconds(66), _ =>
        {
            var peak = _meter.TakePeak();
            _micMeter.Set(Math.Clamp(peak * 2.5, 0, 1), 0);
        });
    }

    private void StopMicCheck()
    {
        _meterTimer?.Invalidate();
        _meterTimer = null;
        _meter?.Stop();
        _micMeter?.Set(0, 0);
    }

    private void ShowMicError()
    {
        if (_micSuccess is null)
        {
            return;
        }
        _micSuccess.Hidden = true;
        _micError!.Hidden = false;
        _micOpenSettings!.Hidden = false;
        _micDeviceLabel!.StringValue = "MICROPHONE";
        _micMeter?.Set(0, 0);
    }

    // ── 4 · Input Monitoring / 5 · Accessibility ─────────────────────────────

    private NSTimer? _permissionTimer;

    private NSStackView BuildInputMonitoring() => BuildPermissionStep(
        "Allow Input Monitoring",
        "KusPus listens for your hotkey system-wide. macOS calls that Input Monitoring. Nothing is logged — only the chord you chose is acted on.",
        "Input Monitoring",
        Permissions.HasInputMonitoring,
        () => { Permissions.RequestInputMonitoring(); Permissions.OpenSettings(Permissions.InputMonitoringSettingsUrl); },
        "Without it the hotkey can't work. If the hotkey doesn't respond after granting, quit and reopen KusPus.");

    private NSStackView BuildAccessibility() => BuildPermissionStep(
        "Allow Accessibility",
        "To paste the transcript into the app you were using, KusPus sends a ⌘V keystroke. macOS gates that behind Accessibility.",
        "Accessibility",
        Permissions.HasAccessibility,
        () => { Permissions.RequestAccessibility(); Permissions.OpenSettings(Permissions.AccessibilitySettingsUrl); },
        "You can skip this: KusPus will copy each transcript to the clipboard instead, and you paste it yourself.");

    private NSStackView BuildPermissionStep(string title, string description, string name, Func<bool> check, Action request, string footnote)
    {
        var row = new PermissionRowView(name, check, request);
        // Some grants (Input Monitoring in particular) only take effect for a freshly
        // launched process. Offer a relaunch once the grant is seen, or after 10 s of
        // waiting — the user may already have ticked the box in System Settings.
        var relaunch = UI.Button("Relaunch KusPus", UI.ButtonKind.Secondary, MacRelaunch.Relaunch);
        relaunch.Hidden = true;
        var relaunchNote = UI.Label("Granted. If the hotkey doesn't respond, a relaunch applies the new permission.", Fonts.RowSubtitle, "MutedText", wraps: true);
        relaunchNote.Hidden = true;
        var cardInner = UI.VStack(0, UI.Eyebrow("PERMISSION"), row, relaunchNote, relaunch);
        cardInner.SetCustomSpacing(12, cardInner.ArrangedSubviews[0]);
        cardInner.SetCustomSpacing(14, row);
        cardInner.SetCustomSpacing(10, relaunchNote);
        row.WidthAnchor.ConstraintEqualTo(cardInner.WidthAnchor).Active = true;
        var card = new CardView(cardInner, radius: 10, padX: 24, padY: 22);
        var note = UI.Label(footnote, Fonts.RowSubtitle, "MutedText", wraps: true);

        var shownAt = DateTime.UtcNow;
        void Poll()
        {
            row.Refresh();
            bool show = row.IsGranted || DateTime.UtcNow - shownAt > TimeSpan.FromSeconds(10);
            relaunch.Hidden = !show;
            relaunchNote.Hidden = !show;
            if (show && !row.IsGranted)
            {
                relaunchNote.StringValue = "Already ticked KusPus in System Settings? Relaunch to apply it.";
            }
        }
        Poll();
        _permissionTimer = Timers.Repeating(TimeSpan.FromSeconds(1), _ => Poll());

        var step = Step(title, description, Centered(card, 440), Centered(note, 440));
        step.SetCustomSpacing(12, step.ArrangedSubviews[3]);
        return step;
    }

    private void StopPermissionPolling()
    {
        _permissionTimer?.Invalidate();
        _permissionTimer = null;
    }

    // ── 6 · Login item ───────────────────────────────────────────────────────

    private NSStackView BuildLoginItem()
    {
        var card = ToggleCard("Launch KusPus when I sign in",
            "Adds KusPus to Login Items. Remove it any time from System Settings → General → Login Items.",
            _prefs.Current.Autostart,
            on => _ = SetAutostartAsync(on));
        return Step("Start with your Mac?", "KusPus is most useful when it's already running. You can change this later in Preferences.", card);
    }

    private async Task SetAutostartAsync(bool enabled)
    {
        var current = _prefs.Current;
        if (current.Autostart == enabled)
        {
            return;
        }
        var error = MacAutostart.Set(enabled);
        if (error is not null)
        {
            _logger.LogWarning("SMAppService failed: {Error}", error);
            ShowStep(_currentStep);
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

    // ── 7 · Crash reports ────────────────────────────────────────────────────

    private NSStackView BuildCrashReports()
    {
        var card = ToggleCard("Send opt-in crash reports",
            "No transcript text, audio, or clipboard contents are ever sent.",
            _prefs.Current.Privacy.CrashReportsOptIn,
            on => _ = SetCrashReportsAsync(on));
        var mint = new CardView(UI.VStack(4,
                UI.Label("Local-first.", Fonts.SectionHeader, "Mint"),
                UI.Label("Audio capture, transcription, and history all stay on this Mac.", Fonts.BodySmall, "PrimaryText", wraps: true)),
            "MintTint", "MintBorder", radius: 8, padX: 20, padY: 16);
        var step = Step("Help improve KusPus?", "Crash reports are anonymous and opt-in. Everything else stays local. Always.", card, mint);
        card.WidthAnchor.ConstraintEqualTo(step.WidthAnchor).Active = true;
        mint.WidthAnchor.ConstraintEqualTo(step.WidthAnchor).Active = true;
        step.SetCustomSpacing(18, card);
        return step;
    }

    private async Task SetCrashReportsAsync(bool enabled)
    {
        var current = _prefs.Current;
        if (current.Privacy.CrashReportsOptIn == enabled)
        {
            return;
        }
        try
        {
            await _prefs.UpdateAsync(s => s with { Privacy = s.Privacy with { CrashReportsOptIn = enabled } }).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "PrefsStore.SaveAsync failed for crash reports.");
        }
    }

    // ── 8 · Try it ───────────────────────────────────────────────────────────

    private enum TryItState { Idle, Recording, Transcribing }
    private TryItState _tryItState = TryItState.Idle;
    private CardView? _tryItSurface;
    private NSTextField? _tryItEmpty;
    private NSTextField? _tryItContent;
    private NSButton? _tryItRecord;
    private NSButton? _tryItClear;
    private NSTimer? _tryItTimer;
    private int _tryItRemaining;
    private CancellationTokenSource? _tryItCts;
    private CancellationTokenSource? _tryItStopEarlyCts;

    private NSStackView BuildTryIt()
    {
        _tryItEmpty = UI.Label("Your transcript will appear here.", Fonts.Body, "MutedText", wraps: true);
        _tryItEmpty.Font = NSFontManager.SharedFontManager.ConvertFont(Fonts.Body, NSFontTraitMask.Italic);
        _tryItEmpty.Alignment = NSTextAlignment.Center;
        _tryItContent = UI.Label(string.Empty, Fonts.Medium(13), "PrimaryText", wraps: true);
        _tryItContent.Alignment = NSTextAlignment.Center;
        _tryItContent.Hidden = true;
        var inner = UI.VStack(0, _tryItEmpty, _tryItContent);
        inner.Alignment = NSLayoutAttribute.CenterX;
        _tryItSurface = new CardView(inner, "Surface", "BorderStrong", radius: 8, padX: 20, padY: 16);
        _tryItSurface.HeightAnchor.ConstraintEqualTo(120).Active = true;

        _tryItRecord = UI.Button("Record dictation", UI.ButtonKind.Primary, () => _ = OnTryItClickAsync());
        _tryItClear = UI.Button("Clear", UI.ButtonKind.Secondary, OnTryItClear);
        _tryItClear.Hidden = true;
        var buttons = UI.HStack(10, _tryItRecord, _tryItClear);

        var step = Step("Try your first dictation",
            "Click Record below, say one sentence, and KusPus will transcribe it using your active model. Take as long as you like — Record again to stop early, or wait the 5 second timer.",
            _tryItSurface, Centered(buttons, 300));
        _tryItSurface.WidthAnchor.ConstraintEqualTo(step.WidthAnchor).Active = true;
        step.SetCustomSpacing(18, _tryItSurface);
        return step;
    }

    private async Task OnTryItClickAsync()
    {
        if (_tryItState is TryItState.Recording)
        {
            // "Record again to stop early": end the recording now and transcribe it.
            _tryItStopEarlyCts?.Cancel();
            return;
        }
        if (_tryItState is TryItState.Transcribing)
        {
            _tryItCts?.Cancel();
            return;
        }
        await RunTryItAsync().ConfigureAwait(true);
    }

    private async Task RunTryItAsync()
    {
        _tryItCts = new CancellationTokenSource();
        var ct = _tryItCts.Token;
        var resolved = _models.Resolve(_prefs.Current.Models.ActiveModelId, _prefs.Current.Models.CustomModelPath);
        if (!resolved.Success)
        {
            SetTryItError(resolved.Error ?? "Active model unavailable.");
            return;
        }
        StopMicCheck();
        string? wav = null;
        try
        {
            SetTryItState(TryItState.Recording, "Recording… 5 seconds");
            var start = await _audio.StartAsync(ct).ConfigureAwait(true);
            if (!start.Success)
            {
                SetTryItError(start.Error ?? "Mic open failed.");
                return;
            }
            StartTryItCountdown();
            using (var stopEarly = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                _tryItStopEarlyCts = stopEarly;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stopEarly.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Stopped early by the user — transcribe what was recorded.
                }
                finally
                {
                    _tryItStopEarlyCts = null;
                }
            }
            StopTryItCountdown();
            var stop = await _audio.StopAsync().ConfigureAwait(true);
            if (!stop.Success)
            {
                SetTryItError(stop.Error ?? "Mic stop failed.");
                return;
            }
            wav = stop.Value!.WavPath;
            SetTryItState(TryItState.Transcribing, "Transcribing…");
            var transcript = await _whisper.TranscribeAsync(wav, resolved.Value!, ct).ConfigureAwait(true);
            if (!transcript.Success)
            {
                SetTryItError(transcript.Error ?? "Transcribe failed.");
                return;
            }
            var text = TranscriptFilter.Clean(transcript.Value!);
            SetTryItResult(text.Length == 0 ? "(no speech detected)" : text);
        }
        catch (OperationCanceledException)
        {
            StopTryItCountdown();
            try { await _audio.StopAsync().ConfigureAwait(true); } catch (InvalidOperationException) { }
            SetTryItState(TryItState.Idle, "Your transcript will appear here.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Onboarding try-it dictation failed unexpectedly.");
            SetTryItError("Unexpected error — see logs.");
        }
        finally
        {
            if (wav is not null)
            {
                try { File.Delete(wav); } catch (IOException) { }
            }
            _tryItCts?.Dispose();
            _tryItCts = null;
        }
    }

    private void SetTryItState(TryItState next, string emptyText)
    {
        _tryItState = next;
        if (_tryItEmpty is null)
        {
            return;
        }
        _tryItEmpty.StringValue = emptyText;
        _tryItEmpty.Hidden = false;
        _tryItContent!.Hidden = true;
        _tryItRecord!.Title = next switch { TryItState.Recording => "Stop", TryItState.Transcribing => "Working…", _ => "Record dictation" };
        _tryItRecord.Enabled = next != TryItState.Transcribing;
        _tryItClear!.Hidden = true;
        _tryItSurface!.BorderOverride = next == TryItState.Recording ? ThemeColors.FromArgb(0xCC4DDBA6) : null;
        _tryItSurface.Retheme();
    }

    private void SetTryItResult(string text)
    {
        _tryItState = TryItState.Idle;
        _tryItContent!.StringValue = text;
        _tryItContent.TextColor = ThemeColors.Get("PrimaryText");
        _tryItEmpty!.Hidden = true;
        _tryItContent.Hidden = false;
        _tryItRecord!.Title = "Try another";
        _tryItRecord.Enabled = true;
        _tryItClear!.Hidden = false;
        _tryItSurface!.BorderOverride = ThemeColors.Get("Mint");
        _tryItSurface.Retheme();
    }

    private void SetTryItError(string error)
    {
        _tryItState = TryItState.Idle;
        _tryItContent!.StringValue = error;
        _tryItContent.TextColor = ThemeColors.Get("ErrorRed");
        _tryItEmpty!.Hidden = true;
        _tryItContent.Hidden = false;
        _tryItRecord!.Title = "Record dictation";
        _tryItRecord.Enabled = true;
        _tryItClear!.Hidden = false;
        _tryItSurface!.BorderOverride = ThemeColors.Get("ErrorRed");
        _tryItSurface.Retheme();
    }

    private void OnTryItClear()
    {
        _tryItEmpty!.StringValue = "Your transcript will appear here.";
        _tryItEmpty.Hidden = false;
        _tryItContent!.Hidden = true;
        _tryItRecord!.Title = "Record dictation";
        _tryItRecord.Enabled = true;
        _tryItClear!.Hidden = true;
        _tryItSurface!.BorderOverride = null;
        _tryItSurface.Retheme();
    }

    private void StartTryItCountdown()
    {
        _tryItRemaining = 5;
        _tryItTimer?.Invalidate();
        _tryItTimer = Timers.Repeating(TimeSpan.FromSeconds(1), _ =>
        {
            _tryItRemaining--;
            if (_tryItRemaining <= 0)
            {
                StopTryItCountdown();
                return;
            }
            if (_tryItEmpty is not null)
            {
                _tryItEmpty.StringValue = $"Recording… {_tryItRemaining} second{(_tryItRemaining == 1 ? string.Empty : "s")}";
            }
        });
    }

    private void StopTryItCountdown()
    {
        _tryItTimer?.Invalidate();
        _tryItTimer = null;
    }

    // ── 9 · Done ─────────────────────────────────────────────────────────────

    private NSStackView BuildDone()
    {
        // Menu-bar diagram: fake menu bar along the top with the KusPus slot highlighted.
        var diagram = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        diagram.Layer!.CornerRadius = 10;
        diagram.Layer.MasksToBounds = true;
        diagram.Layer.BackgroundColor = ThemeColors.Cg("Surface");
        diagram.Layer.BorderWidth = 1;
        diagram.Layer.BorderColor = ThemeColors.Cg("BorderSubtle");
        diagram.HeightAnchor.ConstraintEqualTo(220).Active = true;

        var bar = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
        bar.Layer!.BackgroundColor = ThemeColors.Cg("Sidebar");
        var dots = UI.HStack(8);
        for (int i = 0; i < 2; i++)
        {
            var d = new NSView { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false, AlphaValue = (nfloat)0.4 };
            d.Layer!.CornerRadius = 9;
            d.Layer.BackgroundColor = ThemeColors.Cg("MutedText");
            d.WidthAnchor.ConstraintEqualTo(18).Active = true;
            d.HeightAnchor.ConstraintEqualTo(18).Active = true;
            dots.AddArrangedSubview(d);
        }
        var slotIcon = UI.SymbolView("mic", 13, "Mint");
        var slot = new CardView(slotIcon, "MintTint", "MintTint", radius: 8, padX: 8, padY: 6, borderWidth: 0);
        dots.AddArrangedSubview(slot);
        var callout = UI.VStack(3,
            UI.Label("Your KusPus icon lives here.", Fonts.Semibold(13), "PrimaryText"),
            UI.Label("Click it for the menu.", Fonts.RowSubtitle, "MutedText"));
        diagram.AddSubview(bar);
        diagram.AddSubview(dots);
        diagram.AddSubview(callout);
        NSLayoutConstraint.ActivateConstraints(
        [
            bar.LeadingAnchor.ConstraintEqualTo(diagram.LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(diagram.TrailingAnchor),
            bar.TopAnchor.ConstraintEqualTo(diagram.TopAnchor),
            bar.HeightAnchor.ConstraintEqualTo(36),
            dots.TrailingAnchor.ConstraintEqualTo(diagram.TrailingAnchor, -18),
            dots.CenterYAnchor.ConstraintEqualTo(bar.CenterYAnchor),
            callout.LeadingAnchor.ConstraintEqualTo(diagram.LeadingAnchor, 22),
            callout.BottomAnchor.ConstraintEqualTo(diagram.BottomAnchor, -18),
        ]);

        var step = Step("You're set up", "KusPus lives in your menu bar. Click the icon to change anything.", diagram);
        diagram.WidthAnchor.ConstraintEqualTo(step.WidthAnchor).Active = true;
        return step;
    }
}
