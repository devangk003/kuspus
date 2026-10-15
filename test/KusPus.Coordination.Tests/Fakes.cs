using System.Reactive.Subjects;
using KusPus.Core;
using KusPus.Core.Platform;
using KusPus.Core.Settings;
using KusPus.Persistence;
using KusPus.Whisper;
using PasteOutcome = KusPus.Core.Platform.PasteOutcome;

namespace KusPus.Coordination.Tests;

/// <summary>Runs every posted action immediately on the calling thread.</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeHotkeyEngine : IHotkeyEngine
{
    public Subject<HotkeyEvent> Subject { get; } = new();
    public IObservable<HotkeyEvent> Events => Subject;
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public HotkeyChord? LastChord { get; private set; }

    public void Start() => StartCalls++;
    public void Stop() => StopCalls++;
    public void SetChord(HotkeyChord chord) => LastChord = chord;
}

internal sealed class FakeAudioRecorder : IAudioRecorder
{
    private readonly string _tempDir;
    public FakeAudioRecorder(string tempDir) => _tempDir = tempDir;

    public Subject<float[]> LevelsSubject { get; } = new();
    public IObservable<float[]> Levels => LevelsSubject;
    public event EventHandler? DefaultDeviceChanged;

    public string? StartError { get; set; }
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public string? LastWavPath { get; private set; }
    public bool IsRecording { get; private set; }
    public string? InputDeviceId { get; private set; }

    public void SetInputDeviceId(string? deviceId) => InputDeviceId = deviceId;

    public Task<Result<RecordingHandle>> StartAsync(CancellationToken ct = default)
    {
        StartCalls++;
        if (StartError is not null)
        {
            return Task.FromResult(Result.Fail<RecordingHandle>(StartError));
        }
        IsRecording = true;
        LastWavPath = Path.Combine(_tempDir, $"kuspus-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(LastWavPath, new byte[44]);
        return Task.FromResult(Result.Ok(new RecordingHandle(LastWavPath, DateTimeOffset.UtcNow)));
    }

    public Task<Result<RecordedFile>> StopAsync()
    {
        StopCalls++;
        if (!IsRecording)
        {
            return Task.FromResult(Result.Fail<RecordedFile>("Not recording."));
        }
        IsRecording = false;
        return Task.FromResult(Result.Ok(new RecordedFile(LastWavPath!, TimeSpan.FromSeconds(2))));
    }

    public void RaiseDeviceChanged()
    {
        // Mirror the real recorder: it stops itself before raising.
        IsRecording = false;
        DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeTranscriber : ITranscriber
{
    public Result<string> Result { get; set; } = KusPus.Core.Result.Ok("hello world");
    public string? LastWavPath { get; private set; }
    public TaskCompletionSource Gate { get; } = new();
    public bool WaitForGate { get; set; }
    public Exception? Throw { get; set; }

    public async Task<Result<string>> TranscribeAsync(string wavPath, ResolvedModel model, CancellationToken ct = default)
    {
        LastWavPath = wavPath;
        if (Throw is not null)
        {
            throw Throw;
        }
        if (WaitForGate)
        {
            await Gate.Task.ConfigureAwait(false);
        }
        return Result;
    }
}

internal sealed class FakePasteEngine : IPasteEngine
{
    public IntPtr ForegroundToReturn { get; set; } = new(0x1234);
    public PasteOutcome Outcome { get; set; } = new(Pasted: true, TargetApp: "TextEdit", Error: null);
    public List<(string Text, IntPtr Target)> Deliveries { get; } = new();
    public int CaptureCalls { get; private set; }

    public Result<IntPtr> CaptureForegroundHwnd()
    {
        CaptureCalls++;
        return ForegroundToReturn == IntPtr.Zero
            ? Result.Fail<IntPtr>("no foreground")
            : Result.Ok(ForegroundToReturn);
    }

    public Exception? ThrowOnDeliver { get; set; }

    public async Task<PasteOutcome> DeliverAsync(string text, IntPtr targetHwnd, CancellationToken ct = default)
    {
        Deliveries.Add((text, targetHwnd));
        if (ThrowOnDeliver is not null)
        {
            // Throw after a real await so the exception surfaces the way the Mac engine's
            // AppKit thread-affinity failure did (from a continuation, not synchronously).
            await Task.Yield();
            throw ThrowOnDeliver;
        }
        return Outcome;
    }
}

internal sealed class FakeHistoryStore : IHistoryStore
{
    public List<TranscriptRecord> Records { get; } = new();

    public Task AppendAsync(TranscriptRecord record)
    {
        lock (Records)
        {
            Records.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TranscriptRecord>> SearchAsync(string? query, int limit = 200, int offset = 0) =>
        Task.FromResult<IReadOnlyList<TranscriptRecord>>(Records.ToList());

    public Task DeleteAsync(long id) => Task.CompletedTask;
    public Task PurgeAllAsync() => Task.CompletedTask;
}

internal sealed class FakeModelManager : IModelManager
{
    public static readonly ModelDescriptor Tiny = ModelDescriptor.SingleFile(
        "ggml-tiny.en", "Tiny", "ggml-tiny.en.bin", 1, "00", "https://huggingface.co/x", bundled: true);

    public ModelManifest Manifest { get; } = new(1, "abc", [Tiny]);
    public string ModelsDirectory => "/models";
    public string? ResolveError { get; set; }

    public long DownloadedSizeBytes(ModelDescriptor model) => 0;

    public Result<long> Remove(ModelDescriptor model) => Result.Ok(0L);

    public Result<ResolvedModel> Resolve(string activeModelId, string? customModelPath) =>
        ResolveError is null
            ? Result.Ok(new ResolvedModel(Tiny, "/models/ggml-tiny.en.bin"))
            : Result.Fail<ResolvedModel>(ResolveError);

    public bool IsInstalled(ModelDescriptor model) => true;

    public Task<bool> IsInstalledAsync(ModelDescriptor model, CancellationToken ct = default) => Task.FromResult(true);

    public Task<Result<string>> DownloadAsync(ModelDescriptor model, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(Result.Ok("/models/x"));
}

internal sealed class FakePrefsStore : IPrefsStore, IDisposable
{
    private readonly BehaviorSubject<AppSettings> _changes;
    public FakePrefsStore(AppSettings? initial = null) => _changes = new(initial ?? new AppSettings());

    public AppSettings Current => _changes.Value;
    public IObservable<AppSettings> Changes => _changes;

    public Task SaveAsync(AppSettings updated, CancellationToken ct = default)
    {
        _changes.OnNext(updated);
        return Task.CompletedTask;
    }

    public Task ReloadFromDiskAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose() => _changes.Dispose();
}
