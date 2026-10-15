// ParakeetTranscriber logs: model load / release (rare), decode failures. Same
// "startup-only" log volume as WhisperRunner; LoggerMessage source-gen is overkill.
#pragma warning disable CA1848 // Use the LoggerMessage delegates
#pragma warning disable CA1873 // Avoid potentially expensive logging argument evaluation

using System.Diagnostics;
using KusPus.Core;
using KusPus.Whisper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SherpaOnnx;

namespace KusPus.Parakeet;

/// <summary>
/// In-process NVIDIA Parakeet-TDT engine via sherpa-onnx (<c>nemo_transducer</c>:
/// encoder + decoder + joiner ONNX + <c>tokens.txt</c>). Unlike whisper there is no
/// subprocess and no integrity hash: the native runtime ships inside the signed app and
/// the model files are SHA-verified by <see cref="ModelManager"/> at download time.
///
/// Lifetime: the 650 MB int8 encoder takes a second or more to load and ~1 GB of RAM
/// once resident, so one <see cref="OfflineRecognizer"/> is cached per model directory,
/// swapped when a different Parakeet model is used, and released after
/// <see cref="IdleUnloadDefault"/> without a dictation so a parked app doesn't pin a
/// gigabyte forever. First dictation after idle pays the load again (logged).
/// </summary>
public sealed class ParakeetTranscriber : ITranscriber, IDisposable
{
    public static readonly TimeSpan IdleUnloadDefault = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TimeoutDefault = TimeSpan.FromMinutes(5);

    private const string RuntimeMissingMessage = "Parakeet runtime missing — please reinstall KusPus.";

    private readonly ILogger<ParakeetTranscriber> _logger;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _idleUnload;
    private readonly int _numThreads;

    // Guards every touch of _recognizer / _loadedDirectory; sherpa-onnx recognizers are
    // not documented as thread-safe and we never want two decodes on one instance.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _idleTimer;
    private OfflineRecognizer? _recognizer;
    private string? _loadedDirectory;
    private bool _disposed;

    public ParakeetTranscriber(
        TimeSpan? idleUnload = null,
        TimeSpan? timeout = null,
        int? numThreads = null,
        ILogger<ParakeetTranscriber>? logger = null)
    {
        _logger = logger ?? NullLogger<ParakeetTranscriber>.Instance;
        _timeout = timeout ?? TimeoutDefault;
        _idleUnload = idleUnload ?? IdleUnloadDefault;
        _numThreads = numThreads ?? ChooseThreadCount(Environment.ProcessorCount);
        _idleTimer = new Timer(_ => ReleaseIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Half the cores, at least 2, at most 8 — the int8 encoder is memory-bound past that.</summary>
    internal static int ChooseThreadCount(int processorCount) => Math.Clamp(processorCount / 2, 2, 8);

    /// <summary>True while a recognizer is resident (test hook).</summary>
    internal bool IsLoaded => _recognizer is not null;

    public async Task<Result<string>> TranscribeAsync(string wavPath, ResolvedModel model, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (model.Descriptor.Engine != ModelEngine.Parakeet)
        {
            return Result.Fail<string>($"Model '{model.Descriptor.Id}' is not a Parakeet model.");
        }
        var files = LocateFiles(model);
        if (!files.Success)
        {
            return Result.Fail<string>(files.Error!);
        }
        if (!File.Exists(wavPath))
        {
            return Result.Fail<string>($"Audio file not found at {wavPath}");
        }

        var audio = WavReader.Read(wavPath);
        if (!audio.Success)
        {
            return Result.Fail<string>(audio.Error!, audio.Cause);
        }
        if (audio.Value!.Samples.Length == 0)
        {
            return Result.Ok(string.Empty); // coordinator reports "No speech detected."
        }

        // Long takes need proportionally longer: the base budget plus the audio's own length.
        var timeout = _timeout + TimeSpan.FromSeconds(audio.Value.Samples.Length / (double)audio.Value.SampleRate);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        bool acquired = false;
        Task<string>? decodeTask = null;
        try
        {
            await _lock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            acquired = true;
            _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var load = EnsureLoaded(model.Directory, files.Value!);
            if (!load.Success)
            {
                return Result.Fail<string>(load.Error!, load.Cause);
            }

            var recognizer = _recognizer!;
            var pcm = audio.Value;
            var sw = Stopwatch.StartNew();
            // Native decode can't be interrupted; WaitAsync lets the caller's timeout /
            // cancellation return early while the thread finishes in the background.
            decodeTask = Task.Run(() => Decode(recognizer, pcm), CancellationToken.None);
            var text = await decodeTask
                .WaitAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            _logger.LogDebug("Parakeet decoded {Seconds:F1}s of audio in {Ms}ms.", pcm.Samples.Length / (double)pcm.SampleRate, sw.ElapsedMilliseconds);
            return Result.Ok(text.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result.Fail<string>("Transcription cancelled by caller.");
        }
        catch (OperationCanceledException)
        {
            return Result.Fail<string>($"Parakeet timed out after {timeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _logger.LogError(ex, "sherpa-onnx native runtime failed to load.");
            return Result.Fail<string>(RuntimeMissingMessage, ex);
        }
        finally
        {
            if (acquired)
            {
                if (decodeTask is { IsCompleted: false })
                {
                    // Timed out / cancelled while the native decode is still running on the
                    // recognizer. Keep the lock until it actually finishes so the next call
                    // can neither decode concurrently on it nor dispose it mid-decode.
                    _ = decodeTask.ContinueWith(_ => ReleaseAfterDecode(), TaskScheduler.Default);
                }
                else
                {
                    ReleaseAfterDecode();
                }
            }
        }
    }

    private void ReleaseAfterDecode()
    {
        try
        {
            if (!_disposed)
            {
                _idleTimer.Change(_idleUnload, Timeout.InfiniteTimeSpan);
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the call — nothing to schedule.
        }
        _lock.Release();
    }

    private static string Decode(OfflineRecognizer recognizer, PcmAudio pcm)
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(pcm.SampleRate, pcm.Samples); // sherpa resamples if ≠ 16 kHz
        recognizer.Decode(stream);
        return stream.Result.Text;
    }

    /// <summary>Loads (or swaps) the recognizer for <paramref name="directory"/>. Caller holds the lock.</summary>
    private Result<bool> EnsureLoaded(string directory, ParakeetFiles files)
    {
        if (_recognizer is not null && string.Equals(_loadedDirectory, directory, StringComparison.Ordinal))
        {
            return Result.Ok(true);
        }
        ReleaseLocked("model switch");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner = files.Joiner;
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = _numThreads;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        var sw = Stopwatch.StartNew();
        _recognizer = new OfflineRecognizer(config);
        _loadedDirectory = directory;
        _logger.LogInformation("Parakeet model loaded from {Dir} in {Ms}ms ({Threads} threads).", directory, sw.ElapsedMilliseconds, _numThreads);
        return Result.Ok(true);
    }

    private static Result<ParakeetFiles> LocateFiles(ResolvedModel model)
    {
        string? encoder = null, decoder = null, joiner = null, tokens = null;
        foreach (var f in model.Descriptor.Files)
        {
            var path = model.PathFor(f.FileName);
            if (f.FileName.StartsWith("encoder", StringComparison.OrdinalIgnoreCase))
            {
                encoder = path;
            }
            else if (f.FileName.StartsWith("decoder", StringComparison.OrdinalIgnoreCase))
            {
                decoder = path;
            }
            else if (f.FileName.StartsWith("joiner", StringComparison.OrdinalIgnoreCase))
            {
                joiner = path;
            }
            else if (f.FileName.StartsWith("tokens", StringComparison.OrdinalIgnoreCase))
            {
                tokens = path;
            }
        }
        if (encoder is null || decoder is null || joiner is null || tokens is null)
        {
            return Result.Fail<ParakeetFiles>($"Model '{model.Descriptor.Id}' manifest entry lacks encoder/decoder/joiner/tokens files.");
        }
        foreach (var path in new[] { encoder, decoder, joiner, tokens })
        {
            if (!File.Exists(path))
            {
                return Result.Fail<ParakeetFiles>($"Model file not found at {path}");
            }
        }
        return Result.Ok(new ParakeetFiles(encoder, decoder, joiner, tokens));
    }

    private void ReleaseIdle()
    {
        if (!_lock.Wait(0))
        {
            return; // a decode is in flight; the timer is re-armed when it finishes
        }
        try
        {
            ReleaseLocked("idle");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ReleaseLocked(string reason)
    {
        if (_recognizer is null)
        {
            return;
        }
        _recognizer.Dispose();
        _recognizer = null;
        _logger.LogInformation("Parakeet recognizer released ({Reason}).", reason);
        _loadedDirectory = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _idleTimer.Dispose();
        // Bounded: a decode still in flight at quit must not hang shutdown. If it doesn't
        // finish in time, leak the recognizer (the process is exiting) rather than free it
        // under the native decode.
        if (!_lock.Wait(TimeSpan.FromSeconds(3)))
        {
            _logger.LogWarning("Parakeet decode still running at dispose; skipping recognizer release.");
            return;
        }
        try
        {
            ReleaseLocked("dispose");
        }
        finally
        {
            _lock.Release();
        }
    }

    private sealed record ParakeetFiles(string Encoder, string Decoder, string Joiner, string Tokens);
}
