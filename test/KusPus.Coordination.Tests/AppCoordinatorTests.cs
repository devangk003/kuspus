using FluentAssertions;
using KusPus.Core.Platform;
using KusPus.Core.Settings;
using KusPus.Core.State;
using KusPus.Persistence;
using Xunit;
using HistoryPasteOutcome = KusPus.Persistence.PasteOutcome;
using PlatformPasteOutcome = KusPus.Core.Platform.PasteOutcome;
using PlatformChordEngaged = KusPus.Core.Platform.ChordEngaged;
using PlatformChordReleased = KusPus.Core.Platform.ChordReleased;
using PlatformOtherKey = KusPus.Core.Platform.OtherKeyPressed;

namespace KusPus.Coordination.Tests;

/// <summary>
/// End-to-end coordinator cycles against in-memory fakes. Every FSM step runs
/// inline on the test thread via <see cref="InlineDispatcher"/>; the background
/// <c>Task.Run</c> segments (transcribe, deliver, failure handling) are awaited
/// with <see cref="WaitUntil"/>.
/// </summary>
public sealed class AppCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _failedDir;
    private readonly FakeHotkeyEngine _hotkey = new();
    private readonly FakeAudioRecorder _audio;
    private readonly FakeTranscriber _whisper = new();
    private readonly FakePasteEngine _paste = new();
    private readonly FakeHistoryStore _history = new();
    private readonly FakeModelManager _models = new();
    private readonly FakePrefsStore _prefs;
    private readonly List<CoordinatorSnapshot> _snapshots = new();
    private AppCoordinator? _coordinator;

    public AppCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KusPus_CoordTests_" + Guid.NewGuid().ToString("N"));
        _failedDir = Path.Combine(_tempDir, "failed");
        Directory.CreateDirectory(_tempDir);
        _audio = new FakeAudioRecorder(_tempDir);
        _prefs = new FakePrefsStore();
    }

    public void Dispose()
    {
        _coordinator?.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private AppCoordinator Build(AppSettings? settings = null)
    {
        if (settings is not null)
        {
            _prefs.SaveAsync(settings).GetAwaiter().GetResult();
        }
        _coordinator = new AppCoordinator(
            _hotkey, _audio, _whisper, _paste, _history, _models, _prefs,
            new InlineDispatcher(), _failedDir);
        _coordinator.State.Subscribe(s => { lock (_snapshots) { _snapshots.Add(s); } });
        _coordinator.Start();
        return _coordinator;
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }
            Thread.Sleep(10);
        }
    }

    private PostPasteInfo? LastPostPaste()
    {
        lock (_snapshots)
        {
            return _snapshots.LastOrDefault(s => s.PostPaste is not null)?.PostPaste;
        }
    }

    /// <summary>Tap-mode cycle: engage+release starts recording; engage again stops + transcribes.</summary>
    private void RunTapCycle()
    {
        _hotkey.Subject.OnNext(new PlatformChordEngaged());
        _hotkey.Subject.OnNext(new PlatformChordReleased());
        _coordinator!.Snapshot.State.Should().Be(AppState.Recording);
        _hotkey.Subject.OnNext(new PlatformChordEngaged());
        _coordinator.Snapshot.State.Should().Be(AppState.Transcribing);
    }

    // ── happy path ───────────────────────────────────────────────────────────

    [Fact]
    public void Tap_cycle_pastes_to_captured_target_records_history_and_deletes_wav()
    {
        Build();
        _paste.ForegroundToReturn = new IntPtr(0xBEEF);

        RunTapCycle();
        var wav = _audio.LastWavPath!;

        WaitUntil(() => _paste.Deliveries.Count == 1, "paste");
        WaitUntil(() => _history.Records.Count == 1, "history");

        _paste.Deliveries[0].Text.Should().Be("hello world");
        _paste.Deliveries[0].Target.Should().Be(new IntPtr(0xBEEF));
        _history.Records[0].Status.Should().Be(TranscriptStatus.Ok);
        _history.Records[0].Outcome.Should().Be(HistoryPasteOutcome.Pasted);
        _history.Records[0].TargetApp.Should().Be("TextEdit");
        LastPostPaste().Should().Be(new PostPasteInfo(true, "TextEdit", null));
        WaitUntil(() => !File.Exists(wav), "wav deleted");
        _coordinator!.Snapshot.State.Should().Be(AppState.Idle);
    }

    [Fact]
    public void History_disabled_still_pastes_and_deletes_wav_without_recording()
    {
        Build(new AppSettings { History = new HistorySettings { Enabled = false } });

        RunTapCycle();
        var wav = _audio.LastWavPath!;

        WaitUntil(() => _paste.Deliveries.Count == 1, "paste");
        WaitUntil(() => !File.Exists(wav), "wav deleted even with history off");
        Thread.Sleep(50);
        _history.Records.Should().BeEmpty();
    }

    [Fact]
    public void Tray_toggle_captures_foreground_at_toggle_time()
    {
        var c = Build();
        _paste.ForegroundToReturn = new IntPtr(0x42);

        c.ToggleFromTray();
        c.Snapshot.State.Should().Be(AppState.Recording);
        _paste.CaptureCalls.Should().Be(1);

        // Focus moves elsewhere before the user stops — paste must still go to 0x42.
        _paste.ForegroundToReturn = new IntPtr(0x99);
        c.ToggleFromTray();

        WaitUntil(() => _paste.Deliveries.Count == 1, "paste");
        _paste.Deliveries[0].Target.Should().Be(new IntPtr(0x42));
    }

    [Fact]
    public void No_foreground_window_still_delivers_to_clipboard()
    {
        Build();
        _paste.ForegroundToReturn = IntPtr.Zero;
        _paste.Outcome = new PlatformPasteOutcome(false, "Unknown", "Window gone — text in clipboard");

        RunTapCycle();

        WaitUntil(() => _paste.Deliveries.Count == 1, "paste attempted with zero target");
        WaitUntil(() => _history.Records.Count == 1, "history");
        _paste.Deliveries[0].Target.Should().Be(IntPtr.Zero);
        _history.Records[0].Outcome.Should().Be(HistoryPasteOutcome.WindowGone);
        LastPostPaste()!.Pasted.Should().BeFalse();
    }

    [Fact]
    public void Paste_engine_exception_is_reported_as_clipboard_only()
    {
        Build();
        _paste.ThrowOnDeliver = new InvalidOperationException("AppKit thread-affinity");

        RunTapCycle();

        WaitUntil(() => _history.Records.Count == 1, "history row despite paste-engine exception");
        _history.Records[0].Status.Should().Be(TranscriptStatus.Ok);
        _history.Records[0].Outcome.Should().Be(HistoryPasteOutcome.ClipboardOnly);
        var post = LastPostPaste();
        post.Should().NotBeNull();
        post!.Pasted.Should().BeFalse();
        post.ErrorReason.Should().Contain("text in clipboard");
    }

    // ── failure paths ────────────────────────────────────────────────────────

    [Fact]
    public void Mic_blocked_at_start_returns_to_idle_and_reports()
    {
        Build();
        _audio.StartError = "Failed to open microphone: access denied";

        _hotkey.Subject.OnNext(new PlatformChordEngaged());
        _hotkey.Subject.OnNext(new PlatformChordReleased());

        WaitUntil(() => _history.Records.Count == 1, "failure recorded");
        _coordinator!.Snapshot.State.Should().Be(AppState.Idle);
        _history.Records[0].Status.Should().Be(TranscriptStatus.Failed);
        _history.Records[0].Text.Should().Contain("access denied");
        LastPostPaste()!.ErrorReason.Should().Contain("access denied");
        _paste.Deliveries.Should().BeEmpty();
    }

    [Fact]
    public void Device_change_mid_recording_aborts_and_reports()
    {
        Build();
        _hotkey.Subject.OnNext(new PlatformChordEngaged());
        _hotkey.Subject.OnNext(new PlatformChordReleased());
        _coordinator!.Snapshot.State.Should().Be(AppState.Recording);

        _audio.RaiseDeviceChanged();

        WaitUntil(() => _history.Records.Count == 1, "failure recorded");
        _coordinator.Snapshot.State.Should().Be(AppState.Idle);
        LastPostPaste()!.ErrorReason.Should().Contain("Microphone changed");
        _whisper.LastWavPath.Should().BeNull("no transcription should be attempted");
    }

    [Fact]
    public void Device_change_while_idle_is_ignored()
    {
        Build();
        _audio.RaiseDeviceChanged();
        Thread.Sleep(50);
        _coordinator!.Snapshot.State.Should().Be(AppState.Idle);
        _history.Records.Should().BeEmpty();
        LastPostPaste().Should().BeNull();
    }

    [Fact]
    public void Blank_audio_is_reported_as_no_speech_and_wav_is_retained()
    {
        Build();
        _whisper.Result = Core.Result.Ok("[BLANK_AUDIO]");

        RunTapCycle();
        var wav = _audio.LastWavPath!;

        WaitUntil(() => _history.Records.Count == 1, "failure recorded");
        _paste.Deliveries.Should().BeEmpty();
        _history.Records[0].Status.Should().Be(TranscriptStatus.Failed);
        _history.Records[0].Text.Should().Contain("No speech detected");
        _history.Records[0].FailedWavPath.Should().Be(Path.Combine(_failedDir, Path.GetFileName(wav)));
        File.Exists(_history.Records[0].FailedWavPath!).Should().BeTrue();
        File.Exists(wav).Should().BeFalse();
    }

    [Fact]
    public void Whisper_failure_moves_wav_to_failed_dir()
    {
        Build();
        _whisper.Result = Core.Result.Fail<string>("whisper-cli exited with code 3.");

        RunTapCycle();
        var wav = _audio.LastWavPath!;

        WaitUntil(() => _history.Records.Count == 1, "failure recorded");
        _history.Records[0].FailedWavPath.Should().Be(Path.Combine(_failedDir, Path.GetFileName(wav)));
        LastPostPaste()!.ErrorReason.Should().Contain("exited with code 3");
    }

    [Fact]
    public void New_chord_press_during_transcription_does_not_redirect_the_paste()
    {
        Build();
        _whisper.WaitForGate = true;
        _paste.ForegroundToReturn = new IntPtr(0x1);

        RunTapCycle();
        WaitUntil(() => _whisper.LastWavPath is not null, "whisper started");

        // User immediately starts a new dictation in another window while whisper runs.
        _paste.ForegroundToReturn = new IntPtr(0x2);
        _hotkey.Subject.OnNext(new PlatformChordEngaged()); // Transcribing: no-op per FSM

        _whisper.Gate.SetResult();
        WaitUntil(() => _paste.Deliveries.Count == 1, "paste");
        _paste.Deliveries[0].Target.Should().Be(new IntPtr(0x1));
    }

    // ── settings plumbing ────────────────────────────────────────────────────

    [Fact]
    public void Start_pushes_chord_from_settings_and_starts_hotkey()
    {
        Build(new AppSettings
        {
            Hotkey = new HotkeySettings { Modifiers = [Core.Hotkeys.VirtualKey.LeftAlt], KeyCode = Core.Hotkeys.VirtualKey.Space },
        });

        _hotkey.StartCalls.Should().Be(1);
        _hotkey.LastChord!.Modifiers.Should().Equal(Core.Hotkeys.VirtualKey.LeftAlt);
        _hotkey.LastChord.Key.Should().Be(Core.Hotkeys.VirtualKey.Space);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var c = Build();
        c.Dispose();
        c.Dispose();
        _hotkey.StopCalls.Should().Be(1);
    }

    // ── regressions ──────────────────────────────────────────────────────────

    [Fact]
    public void Transcriber_exception_returns_to_idle_instead_of_sticking_in_transcribing()
    {
        Build();
        _whisper.Throw = new IOException("disk gone");

        RunTapCycle();

        WaitUntil(() => _coordinator!.Snapshot.State == AppState.Idle, "back to idle");
        WaitUntil(() => LastPostPaste() is { Pasted: false }, "error surfaced");
    }

    [Fact]
    public void Failed_start_does_not_stop_a_recording_owned_by_someone_else()
    {
        Build();
        // e.g. the Preferences test is recording: our start fails with "Already recording".
        _audio.StartError = "Already recording.";

        _hotkey.Subject.OnNext(new PlatformChordEngaged());
        _hotkey.Subject.OnNext(new PlatformChordReleased());

        WaitUntil(() => _coordinator!.Snapshot.State == AppState.Idle, "back to idle");
        _audio.StopCalls.Should().Be(0);
    }

    [Fact]
    public void Hook_reinstall_clears_a_cancelled_chord()
    {
        Build();
        _hotkey.Subject.OnNext(new PlatformChordEngaged());
        _hotkey.Subject.OnNext(new PlatformOtherKey());
        _coordinator!.Snapshot.State.Should().Be(AppState.Cancelled);

        // The key-up was lost while the tap was being rebuilt.
        _hotkey.Subject.OnNext(new HookReinstalled());

        _coordinator.Snapshot.State.Should().Be(AppState.Idle);
    }
}
