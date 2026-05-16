#pragma warning disable CA1848
#pragma warning disable CA1873

using System.IO;
using System.Reactive.Subjects;
using System.Windows.Threading;
using KusPus.Audio;
using KusPus.Core.Settings;
using KusPus.Core.State;
using KusPus.Native;
using KusPus.Persistence;
using KusPus.Whisper;
using HistoryPasteOutcome = KusPus.Persistence.PasteOutcome;
using NativePasteOutcome = KusPus.Native.PasteOutcome;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NativeHotkeyEvent = KusPus.Native.HotkeyEvent;
using NativeChordEngaged = KusPus.Native.ChordEngaged;
using NativeChordReleased = KusPus.Native.ChordReleased;
using NativeOtherKey = KusPus.Native.OtherKeyPressed;
using FsmChordEngaged = KusPus.Core.State.ChordEngaged;
using FsmChordReleased = KusPus.Core.State.ChordReleased;
using FsmOtherKey = KusPus.Core.State.OtherKeyPressedWhileArmed;

namespace KusPus.App;

/// <summary>
/// Wires the pure FSM in <see cref="Fsm"/> to the live services: hotkey events come
/// in, side effects fan out to <see cref="IAudioRecorder"/>, <see cref="IWhisperRunner"/>,
/// <see cref="IPasteEngine"/>, and <see cref="IHistoryStore"/>. See TECH_SPEC §12.
///
/// Threading: every FSM step + side-effect dispatch runs on the WPF dispatcher
/// (UI thread). Hook events arrive on the LL hook thread and are marshalled here.
/// Long-running side effects (audio capture, whisper transcribe) are kicked off
/// as <c>Task.Run</c> fire-and-forget; their completion posts a synthetic
/// <see cref="TranscribeComplete"/> / <see cref="TranscribeFailed"/> back through
/// the same channel.
/// </summary>
public sealed class AppCoordinator : IDisposable
{
    private readonly IHotkeyEngine _hotkey;
    private readonly IAudioRecorder _audio;
    private readonly IWhisperRunner _whisper;
    private readonly IPasteEngine _paste;
    private readonly IHistoryStore _history;
    private readonly IModelManager _models;
    private readonly IPrefsStore _prefs;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<AppCoordinator> _logger;

    private readonly BehaviorSubject<CoordinatorSnapshot> _state =
        new(new CoordinatorSnapshot(AppState.Idle));

    private CoordinatorSnapshot _snapshot = new(AppState.Idle);
    private FsmConfig _fsmConfig = new(HoldThresholdMs: 250);
    private IntPtr _capturedHwnd;
    private string? _pendingWavPath;
    private DateTimeOffset _recordingStartedAt;
    private System.Threading.Timer? _holdTimer;
    private IDisposable? _hotkeySub;
    private IDisposable? _prefsSub;

    public AppCoordinator(
        IHotkeyEngine hotkey,
        IAudioRecorder audio,
        IWhisperRunner whisper,
        IPasteEngine paste,
        IHistoryStore history,
        IModelManager models,
        IPrefsStore prefs,
        Dispatcher dispatcher,
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
        _logger = logger ?? NullLogger<AppCoordinator>.Instance;
    }

    /// <summary>The pill VM binds to this to derive its content from state.</summary>
    public IObservable<CoordinatorSnapshot> State => _state;

    public void Start()
    {
        // Pick up settings (incl. hold threshold) and react to changes.
        _prefsSub = _prefs.Changes.Subscribe(OnSettingsChanged);
        OnSettingsChanged(_prefs.Current);

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

    private void OnHookEvent(NativeHotkeyEvent evt)
    {
        CoordinatorEvent? mapped = evt switch
        {
            NativeChordEngaged _ => new FsmChordEngaged(),
            NativeChordReleased _ => new FsmChordReleased(),
            NativeOtherKey _ => new FsmOtherKey(),
            _ => null,
        };
        if (mapped is not null)
        {
            Dispatch(mapped);
        }
    }

    private void OnAudioDeviceChanged(object? sender, EventArgs e)
    {
        // Treat mid-recording device change as a transcribe-failed event so the FSM
        // returns to Idle through HandleTranscribeFailure (which logs + records).
        Dispatch(new TranscribeFailed("Microphone changed mid-recording.", _pendingWavPath));
    }

    private void Dispatch(CoordinatorEvent evt)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var transition = Fsm.Step(_snapshot, evt, _fsmConfig);
            if (!ReferenceEquals(transition.Next, _snapshot))
            {
                _snapshot = transition.Next;
                _state.OnNext(_snapshot);
            }
            foreach (var effect in transition.Effects)
            {
                DispatchEffect(effect);
            }
        });
    }

    // ── FSM side effects → live services ─────────────────────────────────────

    private void DispatchEffect(SideEffect effect)
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
                _ = Task.Run(StartAudioCaptureAsync);
                break;

            case StopAudioCapture:
                _ = Task.Run(StopAudioCaptureAsync);
                break;

            case BeginTranscribe:
                _ = Task.Run(BeginTranscribeAsync);
                break;

            case DeliverTranscript dt:
                _ = Task.Run(() => DeliverAsync(dt.Text, dt.Duration, dt.Model));
                break;

            case HandleTranscribeFailure hf:
                _ = Task.Run(() => HandleFailureAsync(hf.Error, hf.FailedWavPath));
                break;
        }
    }

    private async Task StartAudioCaptureAsync()
    {
        var result = await _audio.StartAsync().ConfigureAwait(false);
        if (!result.Success)
        {
            _logger.LogWarning("Audio start failed: {Error}", result.Error);
            Dispatch(new TranscribeFailed(result.Error ?? "Audio start failed", null));
        }
    }

    private async Task StopAudioCaptureAsync()
    {
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

    private async Task BeginTranscribeAsync()
    {
        var wav = _pendingWavPath;
        if (string.IsNullOrEmpty(wav))
        {
            Dispatch(new TranscribeFailed("No wav file produced by recorder.", null));
            return;
        }

        var settings = _prefs.Current;
        var modelResult = _models.Resolve(settings.Models.ActiveModelId, settings.Models.CustomModelPath);
        if (!modelResult.Success)
        {
            Dispatch(new TranscribeFailed(modelResult.Error!, wav));
            return;
        }

        var transcribe = await _whisper.TranscribeAsync(wav, modelResult.Value!).ConfigureAwait(false);
        if (transcribe.Success)
        {
            var duration = DateTimeOffset.UtcNow - _recordingStartedAt;
            Dispatch(new TranscribeComplete(transcribe.Value!, duration, settings.Models.ActiveModelId));
        }
        else
        {
            Dispatch(new TranscribeFailed(transcribe.Error!, wav));
        }
    }

    private async Task DeliverAsync(string text, TimeSpan duration, string model)
    {
        string? wav = _pendingWavPath;
        _pendingWavPath = null;

        if (_capturedHwnd == IntPtr.Zero)
        {
            await AppendHistoryAsync(text, duration, model, "?", TranscriptStatus.Ok, null, HistoryPasteOutcome.ClipboardOnly).ConfigureAwait(false);
            return;
        }

        var outcome = await _paste.DeliverAsync(text, _capturedHwnd).ConfigureAwait(false);
        var settingsHistory = _prefs.Current.History;
        if (!settingsHistory.Enabled)
        {
            return;
        }

        var pasteOutcome = outcome.Pasted
            ? HistoryPasteOutcome.Pasted
            : (outcome.Error?.Contains("Window gone", StringComparison.Ordinal) == true
                ? HistoryPasteOutcome.WindowGone
                : HistoryPasteOutcome.ClipboardOnly);

        await AppendHistoryAsync(text, duration, model, outcome.TargetApp, TranscriptStatus.Ok, null, pasteOutcome).ConfigureAwait(false);

        // Successful transcription — clean up the wav.
        if (wav is not null && File.Exists(wav))
        {
            try { File.Delete(wav); } catch (IOException ex) { _logger.LogDebug(ex, "Failed to delete {Wav}.", wav); }
        }
    }

    private async Task HandleFailureAsync(string error, string? failedWavPath)
    {
        _logger.LogWarning("Transcription failed: {Error}", error);

        string? retainedPath = null;
        if (failedWavPath is not null && File.Exists(failedWavPath))
        {
            Directory.CreateDirectory(AppPaths.FailedDir);
            retainedPath = Path.Combine(AppPaths.FailedDir, Path.GetFileName(failedWavPath));
            try
            {
                File.Move(failedWavPath, retainedPath, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed to retain failed wav.");
                retainedPath = failedWavPath;
            }
        }

        if (_prefs.Current.History.Enabled)
        {
            await AppendHistoryAsync(
                text: $"[failed] {error}",
                duration: DateTimeOffset.UtcNow - _recordingStartedAt,
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
}
