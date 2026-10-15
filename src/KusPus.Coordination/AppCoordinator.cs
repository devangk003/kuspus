#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Reactive.Subjects;
using KusPus.Core.Platform;
using KusPus.Core.Settings;
using KusPus.Core.State;
using KusPus.Persistence;
using KusPus.Whisper;
using HistoryPasteOutcome = KusPus.Persistence.PasteOutcome;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PlatformHotkeyEvent = KusPus.Core.Platform.HotkeyEvent;
using PlatformChordEngaged = KusPus.Core.Platform.ChordEngaged;
using PlatformChordReleased = KusPus.Core.Platform.ChordReleased;
using PlatformOtherKey = KusPus.Core.Platform.OtherKeyPressed;
using PlatformHookReinstalled = KusPus.Core.Platform.HookReinstalled;
using FsmChordEngaged = KusPus.Core.State.ChordEngaged;
using FsmChordReleased = KusPus.Core.State.ChordReleased;
using FsmOtherKey = KusPus.Core.State.OtherKeyPressedWhileArmed;

namespace KusPus.Coordination;

/// <summary>
/// Wires the pure FSM in <see cref="Fsm"/> to the live services: hotkey events come
/// in, side effects fan out to <see cref="IAudioRecorder"/>, <see cref="ITranscriber"/>,
/// <see cref="IPasteEngine"/>, and <see cref="IHistoryStore"/>. See TECH_SPEC §12.
///
/// Platform-neutral: every OS-specific service arrives through a <c>KusPus.Core.Platform</c>
/// interface and UI-thread marshalling goes through <see cref="IUiDispatcher"/>, so the
/// same coordinator drives both the WPF and the AppKit app.
///
/// Threading: every FSM step + side-effect dispatch runs on the UI thread via
/// <see cref="IUiDispatcher.Post"/>. Hook events arrive on the platform's hook thread
/// and are marshalled here. Long-running side effects (whisper transcribe, paste) are
/// kicked off as <c>Task.Run</c> fire-and-forget with every piece of mutable state
/// they need captured into locals first; their completion posts a synthetic
/// <see cref="TranscribeComplete"/> / <see cref="TranscribeFailed"/> back through
/// the same channel.
/// </summary>
public sealed class AppCoordinator : IDisposable
{
    private readonly IHotkeyEngine _hotkey;
    private readonly IAudioRecorder _audio;
    private readonly ITranscriber _whisper;
    private readonly IPasteEngine _paste;
    private readonly IHistoryStore _history;
    private readonly IModelManager _models;
    private readonly IPrefsStore _prefs;
    private readonly IUiDispatcher _dispatcher;
    private readonly string _failedDir;
    private readonly ILogger<AppCoordinator> _logger;

    private readonly BehaviorSubject<CoordinatorSnapshot> _state =
        new(new CoordinatorSnapshot(AppState.Idle));

    // All of the below are only touched on the UI thread (inside Dispatch).
    private CoordinatorSnapshot _snapshot = new(AppState.Idle);
    private FsmConfig _fsmConfig = new(HoldThresholdMs: 250);
    private IntPtr _capturedHwnd;
    private string? _pendingWavPath;
    // True while a capture this coordinator started is open (see AbortAudioCaptureAsync).
    private volatile bool _captureOwned;
    private DateTimeOffset _recordingStartedAt;
    private System.Threading.Timer? _holdTimer;
    private IDisposable? _hotkeySub;
    private IDisposable? _prefsSub;
    private bool _disposed;

    public AppCoordinator(
        IHotkeyEngine hotkey,
        IAudioRecorder audio,
        ITranscriber whisper,
        IPasteEngine paste,
        IHistoryStore history,
        IModelManager models,
        IPrefsStore prefs,
        IUiDispatcher dispatcher,
        string failedDir,
        ILogger<AppCoordinator>? logger = null)
    {
        _hotkey = hotkey;
        _audio = audio;
        _whisper = whisper;
        _paste = paste;
        _history = history;
        _models = models;
        _prefs = prefs;
        _dispatcher = dispatcher;
        _failedDir = failedDir;
        _logger = logger ?? NullLogger<AppCoordinator>.Instance;
    }

    /// <summary>The pill VM binds to this to derive its content from state.</summary>
    public IObservable<CoordinatorSnapshot> State => _state;

    /// <summary>
    /// Last emitted snapshot — handy when a caller already received an unrelated
    /// notification (e.g. PrefsStore change) and wants the current FSM state
    /// without re-subscribing. Mirrors <see cref="IPrefsStore.Current"/>'s shape.
    /// </summary>
    public CoordinatorSnapshot Snapshot => _state.Value;

    public void Start()
    {
        // PrefsStore.Changes is a BehaviorSubject — it pushes the current value
        // synchronously on Subscribe. No need to also call OnSettingsChanged manually
        // (that would fire it twice in immediate succession, and any later runtime save
        // would re-fire and reset HotkeyEngine state mid-chord).
        _prefsSub = _prefs.Changes.Subscribe(OnSettingsChanged);

        _hotkeySub = _hotkey.Events.Subscribe(OnHookEvent);
        _audio.DefaultDeviceChanged += OnAudioDeviceChanged;
        _hotkey.Start();

        _logger.LogInformation("AppCoordinator started.");
    }

    public void Stop()
    {
        _hotkey.Stop();
        _hotkeySub?.Dispose();
        _prefsSub?.Dispose();
        _audio.DefaultDeviceChanged -= OnAudioDeviceChanged;
        _holdTimer?.Dispose();
        _holdTimer = null;
        _logger.LogInformation("AppCoordinator stopped.");
    }

    public void Dispose()
    {
        // Idempotent: AppCoordinator gets disposed both explicitly from the app's
        // exit path AND by the DI ServiceProvider when it tears down singletons.
        // Without this guard, Stop() runs twice and the second call double-disposes
        // subjects.
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
        _state.Dispose();
    }

    public void ToggleFromTray()
    {
        Dispatch(new ToggleFromTray());
    }

    // ── settings ─────────────────────────────────────────────────────────────

    private void OnSettingsChanged(AppSettings settings)
    {
        _fsmConfig = new FsmConfig(HoldThresholdMs: settings.Hotkey.HoldThresholdMs);
        _hotkey.SetChord(new HotkeyChord(settings.Hotkey.Modifiers, settings.Hotkey.KeyCode));
    }

    // ── hook → FSM ───────────────────────────────────────────────────────────

    private void OnHookEvent(PlatformHotkeyEvent evt)
    {
        CoordinatorEvent? mapped = evt switch
        {
            PlatformChordEngaged _ => new FsmChordEngaged(),
            PlatformChordReleased _ => new FsmChordReleased(),
            PlatformOtherKey _ => new FsmOtherKey(),
            // The hook was rebuilt and chord state reset, so any key-up for a held chord
            // is lost. Treat it as a release: ends a hold-mode recording, clears Cancelled;
            // a no-op in Idle and tap-mode Recording.
            PlatformHookReinstalled _ => new FsmChordReleased(),
            _ => null,
        };
        if (mapped is not null)
        {
            Dispatch(mapped);
        }
    }

    private void OnAudioDeviceChanged(object? sender, EventArgs e)
    {
        // The FSM only honours this while Recording; in any other state it's a no-op.
        Dispatch(new RecordingFailed("Microphone changed mid-recording."));
    }

    private void Dispatch(CoordinatorEvent evt)
    {
        // Async lambda on the UI thread so we can `await` between effects.
        // The FSM emits Stop+BeginTranscribe together for Recording → Transcribing;
        // these MUST be serialised so the wav is fully on disk before whisper reads it.
        _dispatcher.Post(async () =>
        {
            if (_disposed)
            {
                return; // late hook/timer event during shutdown
            }
            try
            {
                var transition = Fsm.Step(_snapshot, evt, _fsmConfig);
                if (!ReferenceEquals(transition.Next, _snapshot))
                {
                    _snapshot = transition.Next;
                    _state.OnNext(_snapshot);
                }
                foreach (var effect in transition.Effects)
                {
                    await DispatchEffectAsync(effect).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                // This is an async-void continuation on the UI thread: anything that
                // escapes here terminates the process. Log and carry on — the FSM is
                // already in its next state and the next event will re-sync.
                _logger.LogError(ex, "Error dispatching FSM event {Evt}.", evt.GetType().Name);
            }
        });
    }

    // ── FSM side effects → live services ─────────────────────────────────────

    private async Task DispatchEffectAsync(SideEffect effect)
    {
        switch (effect)
        {
            case CaptureForegroundHwnd:
                var fg = _paste.CaptureForegroundHwnd();
                _capturedHwnd = fg.Success ? fg.Value : IntPtr.Zero;
                break;

            case StartHoldTimer t:
                _holdTimer?.Dispose();
                _holdTimer = new System.Threading.Timer(
                    _ => Dispatch(new HoldThresholdElapsed()),
                    state: null,
                    dueTime: t.HoldThresholdMs,
                    period: Timeout.Infinite);
                break;

            case CancelHoldTimer:
                _holdTimer?.Dispose();
                _holdTimer = null;
                break;

            case StartAudioCapture:
                _recordingStartedAt = DateTimeOffset.UtcNow;
                await StartAudioCaptureAsync().ConfigureAwait(true);
                break;

            case StopAudioCapture:
                // MUST complete before BeginTranscribe runs — the wav has to be flushed.
                await StopAudioCaptureAsync().ConfigureAwait(true);
                break;

            case AbortAudioCapture:
                await AbortAudioCaptureAsync().ConfigureAwait(true);
                break;

            case BeginTranscribe:
            {
                // Long-running (seconds). Fire-and-forget so subsequent FSM events
                // (user pressing chord again) aren't blocked by the whisper run.
                // Capture the wav + start time now: the next chord press can overwrite
                // the fields while whisper is still running.
                var wav = _pendingWavPath;
                var startedAt = _recordingStartedAt;
                _ = Task.Run(() => GuardAsync(
                    () => BeginTranscribeAsync(wav, startedAt),
                    ex => Dispatch(new TranscribeFailed($"Transcription error ({ex.GetType().Name})", wav))));
                break;
            }

            case DeliverTranscript dt:
            {
                var hwnd = _capturedHwnd;
                var wav = _pendingWavPath;
                _pendingWavPath = null;
                _ = Task.Run(() => GuardAsync(() => DeliverAsync(dt.Text, dt.Duration, dt.Model, hwnd, wav), null));
                break;
            }

            case HandleTranscribeFailure hf:
            {
                // RecordingFailed carries no path; AbortAudioCapture may have stashed one.
                var wav = hf.FailedWavPath ?? _pendingWavPath;
                _pendingWavPath = null;
                var startedAt = _recordingStartedAt;
                _ = Task.Run(() => GuardAsync(() => HandleFailureAsync(hf.Error, wav, startedAt), null));
                break;
            }
        }
    }

    /// <summary>
    /// Runs a fire-and-forget effect so an exception can't vanish as an unobserved task
    /// (which would leave the FSM parked in Transcribing with no way out).
    /// </summary>
    private async Task GuardAsync(Func<Task> work, Action<Exception>? onError)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background coordinator effect failed.");
            onError?.Invoke(ex);
        }
    }

    private async Task StartAudioCaptureAsync()
    {
        var result = await _audio.StartAsync().ConfigureAwait(false);
        if (!result.Success)
        {
            _logger.LogWarning("Audio start failed: {Error}", result.Error);
            Dispatch(new RecordingFailed(result.Error ?? "Audio start failed"));
            return;
        }
        _captureOwned = true;

        // StartAsync can take a while on first use (the microphone permission prompt), and
        // other FSM events run in the meantime. If the take was already stopped/aborted
        // while we waited, nobody will stop this capture — close it so the mic doesn't
        // stay on with the FSM idle.
        _dispatcher.Post(() =>
        {
            if (_snapshot.State != AppState.Recording && _captureOwned)
            {
                _logger.LogWarning("Capture started after the take ended ({State}); discarding it.", _snapshot.State);
                _ = Task.Run(() => GuardAsync(DiscardOrphanedCaptureAsync, null));
            }
        });
    }

    private async Task DiscardOrphanedCaptureAsync()
    {
        _captureOwned = false;
        var result = await _audio.StopAsync().ConfigureAwait(false);
        if (result.Success && result.Value is not null)
        {
            DeleteQuietly(result.Value.WavPath);
        }
    }

    private async Task StopAudioCaptureAsync()
    {
        _captureOwned = false;
        var result = await _audio.StopAsync().ConfigureAwait(false);
        if (result.Success && result.Value is not null)
        {
            _pendingWavPath = result.Value.WavPath;
        }
        else
        {
            _logger.LogWarning("Audio stop failed: {Error}", result.Error);
            Dispatch(new TranscribeFailed(result.Error ?? "Audio stop failed", null));
        }
    }

    private async Task AbortAudioCaptureAsync()
    {
        // Best-effort. The recorder may have already stopped itself (device change,
        // disk full) in which case StopAsync returns "Not recording." — that's fine.
        // Only stop a capture we started: when StartAsync failed with "Already recording"
        // the recorder belongs to someone else (Preferences test / onboarding Try-it),
        // and stopping it would steal their take.
        if (!_captureOwned)
        {
            return;
        }
        _captureOwned = false;
        var result = await _audio.StopAsync().ConfigureAwait(false);
        if (result.Success && result.Value is not null)
        {
            _pendingWavPath = result.Value.WavPath;
        }
    }

    private async Task BeginTranscribeAsync(string? wav, DateTimeOffset startedAt)
    {
        if (string.IsNullOrEmpty(wav))
        {
            Dispatch(new TranscribeFailed("No wav file produced by recorder.", null));
            return;
        }

        _logger.LogInformation("Beginning transcription of {Wav}.", wav);

        var settings = _prefs.Current;
        var modelResult = _models.Resolve(settings.Models.ActiveModelId, settings.Models.CustomModelPath);
        if (!modelResult.Success)
        {
            _logger.LogWarning("Model resolution failed: {Error}", modelResult.Error);
            Dispatch(new TranscribeFailed(modelResult.Error!, wav));
            return;
        }

        _logger.LogInformation(
            "Resolved model {Id} at {Path}; spawning whisper.",
            modelResult.Value!.Descriptor.Id, modelResult.Value!.Path);

        var computeStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var transcribe = await _whisper.TranscribeAsync(wav, modelResult.Value!).ConfigureAwait(false);
        computeStopwatch.Stop();

        if (!transcribe.Success)
        {
            _logger.LogWarning("Whisper failed: {Error}", transcribe.Error);
            Dispatch(new TranscribeFailed(transcribe.Error!, wav));
            return;
        }

        var text = TranscriptFilter.Clean(transcribe.Value!);
        if (text.Length == 0)
        {
            _logger.LogInformation("Whisper returned no speech (compute {ComputeMs}ms).", computeStopwatch.ElapsedMilliseconds);
            Dispatch(new TranscribeFailed("No speech detected.", wav));
            return;
        }

        var totalDuration = DateTimeOffset.UtcNow - startedAt;
        _logger.LogInformation(
            "Whisper returned {Chars} chars (compute {ComputeMs}ms, total {TotalSeconds:F1}s since start).",
            text.Length,
            computeStopwatch.ElapsedMilliseconds,
            totalDuration.TotalSeconds);
        Dispatch(new TranscribeComplete(text, totalDuration, settings.Models.ActiveModelId));
    }

    private async Task DeliverAsync(string text, TimeSpan duration, string model, IntPtr targetHwnd, string? wav)
    {
        _logger.LogInformation(
            "Delivering transcript ({Chars} chars) to target {Hwnd}.",
            text.Length, targetHwnd);

        // Always go through the paste engine, even with no captured target: it still
        // puts the text on the clipboard and reports "Window gone — text in clipboard",
        // so the user never loses a transcript.
        Core.Platform.PasteOutcome outcome;
        try
        {
            outcome = await _paste.DeliverAsync(text, targetHwnd).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A paste-engine bug must not swallow the transcript: the text is already on
            // the clipboard by now, so report it as clipboard-only (error pill + history
            // row) instead of dying as an unobserved task.
            _logger.LogError(ex, "Paste engine threw; reporting clipboard-only outcome.");
            outcome = new Core.Platform.PasteOutcome(false, "?", $"Paste failed — text in clipboard ({ex.GetType().Name})");
        }
        _logger.LogInformation(
            "Paste outcome: pasted={Pasted} app={App} error={Error}.",
            outcome.Pasted, outcome.TargetApp, outcome.Error ?? "<none>");

        EmitPostPasteSnapshot(outcome.Pasted, outcome.TargetApp, outcome.Error);

        if (_prefs.Current.History.Enabled)
        {
            var pasteOutcome = outcome.Pasted
                ? HistoryPasteOutcome.Pasted
                : (outcome.Error?.Contains("Window gone", StringComparison.Ordinal) == true
                    ? HistoryPasteOutcome.WindowGone
                    : HistoryPasteOutcome.ClipboardOnly);

            await AppendHistoryAsync(text, duration, model, outcome.TargetApp, TranscriptStatus.Ok, null, pasteOutcome).ConfigureAwait(false);
        }

        // Successful transcription — the wav is never kept, regardless of the
        // history setting. (Previously the history-disabled path returned early
        // and left every recording in %TEMP%.)
        DeleteQuietly(wav);
    }

    /// <summary>
    /// Marshals a one-shot post-paste snapshot onto the UI thread so the pill can
    /// render Confirmed (1 s) or Error (2 s) per design spec §2.5 / §2.6. The FSM
    /// itself already transitioned to Idle before this fires; the snapshot rides
    /// on top of Idle with <see cref="PostPasteInfo"/> attached.
    /// </summary>
    private void EmitPostPasteSnapshot(bool pasted, string targetApp, string? error)
    {
        var info = new PostPasteInfo(pasted, targetApp, error);
        _dispatcher.Post(() =>
        {
            if (_disposed)
            {
                return;
            }
            // Only decorate Idle. If the user already started the next dictation while
            // this paste was in flight, showing "Pasted into X" and then snapping the
            // pill back to Idle would misreport an active recording.
            if (_snapshot.State != AppState.Idle)
            {
                _logger.LogDebug("Skipping post-paste snapshot: FSM is {State}.", _snapshot.State);
                return;
            }
            _state.OnNext(new CoordinatorSnapshot(AppState.Idle, PostPaste: info));
        });
    }

    private async Task HandleFailureAsync(string error, string? failedWavPath, DateTimeOffset startedAt)
    {
        _logger.LogWarning("Transcription failed: {Error}", error);

        EmitPostPasteSnapshot(pasted: false, targetApp: "?", error: error);

        string? retainedPath = null;
        if (failedWavPath is not null && File.Exists(failedWavPath))
        {
            try
            {
                Directory.CreateDirectory(_failedDir);
                retainedPath = Path.Combine(_failedDir, Path.GetFileName(failedWavPath));
                File.Move(failedWavPath, retainedPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Failed to retain failed wav.");
                retainedPath = failedWavPath;
            }
        }

        if (_prefs.Current.History.Enabled)
        {
            await AppendHistoryAsync(
                text: $"[failed] {error}",
                duration: DateTimeOffset.UtcNow - startedAt,
                model: _prefs.Current.Models.ActiveModelId,
                targetApp: null,
                status: TranscriptStatus.Failed,
                failedWavPath: retainedPath,
                outcome: null).ConfigureAwait(false);
        }
    }

    private async Task AppendHistoryAsync(
        string text, TimeSpan duration, string model, string? targetApp,
        TranscriptStatus status, string? failedWavPath, HistoryPasteOutcome? outcome)
    {
        try
        {
            await _history.AppendAsync(new TranscriptRecord(
                Id: 0,
                Timestamp: DateTimeOffset.UtcNow,
                Text: text,
                Duration: duration,
                Model: model,
                TargetApp: targetApp,
                Status: status,
                FailedWavPath: failedWavPath,
                Outcome: outcome)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _logger.LogWarning(ex, "Failed to append history record.");
        }
    }

    private void DeleteQuietly(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to delete {Wav}.", path);
        }
    }
}
