// AudioRecorder logs on start, stop, device-change, and 50-min cap — none on hot path.
#pragma warning disable CA1848 // Use the LoggerMessage delegates
#pragma warning disable CA1873 // Avoid potentially expensive logging argument evaluation

using System.Diagnostics;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using KusPus.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KusPus.Audio;

/// <summary>
/// NAudio-backed implementation of <see cref="IAudioRecorder"/>. See TECH_SPEC §14.
///
/// The capture's source format (typically 48 kHz stereo float32, device-dependent) is
/// resampled to the §14 target (16 kHz mono 16-bit PCM) via
/// <see cref="MediaFoundationResampler"/>. A dedicated worker thread pulls resampled
/// samples and writes to <see cref="WaveFileWriter"/>; the capture's DataAvailable
/// callback only pushes raw bytes into a <see cref="BufferedWaveProvider"/>.
///
/// IMMNotificationClient surfaces default-device changes; the recorder stops itself
/// and raises <see cref="DefaultDeviceChanged"/> so the Coordinator can show the
/// "Mic changed — try again" pill per §8.2.
/// </summary>
public sealed class AudioRecorder : IAudioRecorder, IDisposable
{
    private const int TargetSampleRate = 16_000;
    private const int TargetBitsPerSample = 16;
    private const int TargetChannels = 1;
    private const int BytesPerSample = TargetBitsPerSample / 8;
    private const int MaxRecordingSeconds = 50 * 60;
    private const int LevelChannels = 20;
    private const int LevelPostIntervalMs = 66; // ~15 Hz

    private static readonly WaveFormat TargetFormat =
        new(TargetSampleRate, TargetBitsPerSample, TargetChannels);

    private readonly string _tempDir;
    private readonly ILogger<AudioRecorder> _logger;
    private readonly Subject<float[]> _levels = new();
    private readonly object _stateLock = new();

    private MMDeviceEnumerator? _enumerator;
    private DeviceChangeNotifier? _notifier;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private SampleToWaveProvider16? _resampler;
    private WaveFileWriter? _writer;
    private Thread? _workerThread;
    private CancellationTokenSource? _stopCts;

    private string? _wavPath;
    private DateTimeOffset _startedAt;
    private long _samplesWritten;
    private volatile bool _isRecording;
    private volatile bool _hitDurationCap;

    public AudioRecorder(string? tempDir = null, ILogger<AudioRecorder>? logger = null)
    {
        _tempDir = tempDir ?? Path.GetTempPath();
        _logger = logger ?? NullLogger<AudioRecorder>.Instance;
    }

    public IObservable<float[]> Levels => _levels;

    public event EventHandler? DefaultDeviceChanged;

    public Task<Result<RecordingHandle>> StartAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_isRecording)
            {
                return Task.FromResult(Result.Fail<RecordingHandle>("Already recording."));
            }

            try
            {
                _enumerator = new MMDeviceEnumerator();
                using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

                _capture = new WasapiCapture(device, useEventSync: false);
                _buffer = new BufferedWaveProvider(_capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(5),
                    DiscardOnBufferOverflow = true,
                    // CRITICAL: default is true, which causes BufferedWaveProvider.Read to
                    // pad the requested byte count with ZEROS when no source data is buffered.
                    // That makes the worker write silence at full disk speed (capture is async
                    // and lags first samples by ~10 ms; without this, every gap becomes silence
                    // in the WAV). With ReadFully=false the worker correctly sees bytesRead=0,
                    // sleeps, and lets WASAPI fill the buffer. See NAudio Issue #980.
                    ReadFully = false,
                };

                // TECH_SPEC §14 pipeline:
                //   capture (any source format) → BufferedWaveProvider → ToSampleProvider
                //   → ToMono (downmix if stereo) → WdlResamplingSampleProvider(16 kHz)
                //   → SampleToWaveProvider16 (float → int16) → writer
                //
                // The WDL pull-based resampler returns 0 samples when the source has
                // none — unlike MediaFoundationResampler which pads with silence at
                // full output-buffer rate, blowing out the WAV file by 500× as
                // observed in Phase 6 smoke. Spec was right; my Phase 4 deviation
                // to MF was wrong. Reverting.
                var sampleSource = _buffer.ToSampleProvider();
                if (_capture.WaveFormat.Channels > 1)
                {
                    sampleSource = sampleSource.ToMono();
                }
                var resampled = new WdlResamplingSampleProvider(sampleSource, TargetSampleRate);
                _resampler = new SampleToWaveProvider16(resampled);

                Directory.CreateDirectory(_tempDir);
                _wavPath = Path.Combine(
                    _tempDir,
                    $"kuspus-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.wav");
                _writer = new WaveFileWriter(_wavPath, TargetFormat);
                _startedAt = DateTimeOffset.UtcNow;
                _samplesWritten = 0;
                _hitDurationCap = false;

                _capture.DataAvailable += OnDataAvailable;

                _notifier = new DeviceChangeNotifier(this);
                _enumerator.RegisterEndpointNotificationCallback(_notifier);

                _stopCts = new CancellationTokenSource();

                // ORDER MATTERS: start the capture FIRST so the worker's CaptureState
                // check (`_capture.CaptureState != Capturing → break`) doesn't fire
                // immediately. WasapiCapture.StartRecording transitions the state
                // asynchronously, but the worker's startup wait below absorbs that.
                _capture.StartRecording();
                _isRecording = true;

                _workerThread = new Thread(() => WorkerLoop(_stopCts.Token))
                {
                    IsBackground = true,
                    Name = "KusPus.AudioRecorder.Worker",
                };
                _workerThread.Start();

                _logger.LogInformation(
                    "Started recording from {Device} ({Format}) to {Path}.",
                    device.FriendlyName, _capture.WaveFormat, _wavPath);

                return Task.FromResult(Result.Ok(new RecordingHandle(_wavPath, _startedAt)));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or System.UnauthorizedAccessException)
            {
                Cleanup();
                return Task.FromResult(Result.Fail<RecordingHandle>(
                    $"Failed to open microphone: {ex.Message}", ex));
            }
        }
    }

    public Task<Result<RecordedFile>> StopAsync()
    {
        lock (_stateLock)
        {
            if (!_isRecording || _capture is null || _writer is null || _wavPath is null)
            {
                return Task.FromResult(Result.Fail<RecordedFile>("Not recording."));
            }

            try
            {
                _capture.StopRecording();

                // Don't cancel the worker's CT yet — the BufferedWaveProvider still has
                // up to ~5s of source samples that haven't been resampled and written.
                // The worker has a natural-exit condition (bytesRead <= 0 && CaptureState
                // != Capturing) that drains the buffer first. Join with a generous
                // timeout; only cancel if drain doesn't complete in time.
                bool joined = _workerThread?.Join(TimeSpan.FromSeconds(3)) ?? true;
                if (!joined)
                {
                    _logger.LogWarning(
                        "Audio worker did not drain within 3s; cancelling and proceeding.");
                    _stopCts?.Cancel();
                    _workerThread?.Join(TimeSpan.FromSeconds(1));
                }

                var duration = DateTimeOffset.UtcNow - _startedAt;
                var path = _wavPath;
                var capped = _hitDurationCap;

                Cleanup();

                _logger.LogInformation("Stopped recording after {Seconds:F1}s.", duration.TotalSeconds);
                return Task.FromResult(Result.Ok(new RecordedFile(path, duration, capped)));
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                Cleanup();
                return Task.FromResult(Result.Fail<RecordedFile>($"Stop failed: {ex.Message}", ex));
            }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isRecording)
            {
                try { _capture?.StopRecording(); } catch (COMException) { }
            }
            Cleanup();
        }
        _levels.Dispose();
    }

    // ── private ──────────────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void WorkerLoop(CancellationToken ct)
    {
        // One "level frame" = ~1/15s of 16 kHz mono = 1066 samples = 2132 bytes.
        var buf = new byte[(TargetSampleRate / 15) * BytesPerSample];
        var stopwatch = Stopwatch.StartNew();
        var lastLevelPost = TimeSpan.Zero;

        // Startup wait: WasapiCapture.StartRecording transitions state asynchronously.
        // Without this, our first natural-exit check (bytesRead==0 && CaptureState!=Capturing)
        // would fire instantly on a brief Stopped state and the worker would exit before
        // any audio arrives. Wait up to 1s for the transition; after that, if capture
        // never started, the natural exit will fire as designed.
        var startupWait = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested
            && _capture?.CaptureState != CaptureState.Capturing
            && startupWait.Elapsed < TimeSpan.FromSeconds(1))
        {
            Thread.Sleep(5);
        }

        while (!ct.IsCancellationRequested)
        {
            int bytesRead;
            try
            {
                bytesRead = _resampler!.Read(buf, 0, buf.Length);
            }
            catch (Exception ex) when (ex is COMException or ObjectDisposedException)
            {
                break;
            }

            if (bytesRead <= 0)
            {
                if (_capture?.CaptureState != CaptureState.Capturing)
                {
                    break;
                }
                Thread.Sleep(5);
                continue;
            }

            try
            {
                _writer!.Write(buf, 0, bytesRead);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (IOException ex)
            {
                // Disk full mid-recording (TECH_SPEC §8.11 "Disk full"). Don't crash
                // the app — abort the recording, surface it via DefaultDeviceChanged-
                // style fallback (coordinator treats this as a transcribe failure).
                _logger.LogError(ex, "Audio write failed; aborting recording.");
                try { _capture?.StopRecording(); } catch (COMException) { }
                break;
            }

            _samplesWritten += bytesRead / BytesPerSample;

            if (stopwatch.Elapsed - lastLevelPost > TimeSpan.FromMilliseconds(LevelPostIntervalMs))
            {
                var rms = new float[LevelChannels];
                ComputeRms(buf.AsSpan(0, bytesRead), rms);
                _levels.OnNext(rms);
                lastLevelPost = stopwatch.Elapsed;
            }

            // Wall-clock cap (not sample-count). "50 minutes" in the spec means real
            // time elapsed since recording started, not 50 minutes' worth of perfectly
            // metered samples. Stopwatch is monotonic and immune to sample-rate drift.
            if (stopwatch.Elapsed.TotalSeconds >= MaxRecordingSeconds)
            {
                _logger.LogWarning("Recording exceeded the {Minutes}-minute cap; stopping.", MaxRecordingSeconds / 60);
                _hitDurationCap = true;
                try
                {
                    _capture?.StopRecording();
                }
                catch (COMException ex)
                {
                    _logger.LogDebug(ex, "StopRecording threw during 50-minute cap auto-stop.");
                }
                break;
            }
        }
    }

    /// <summary>
    /// Computes <paramref name="rms"/>.Length-channel RMS over the given 16-bit mono PCM buffer.
    /// Each output channel covers an equal slice of the buffer. Internal for unit testing.
    /// </summary>
    internal static void ComputeRms(ReadOnlySpan<byte> pcm16Mono, Span<float> rms)
    {
        if (rms.Length == 0)
        {
            return;
        }

        int sampleCount = pcm16Mono.Length / BytesPerSample;
        int samplesPerWindow = Math.Max(1, sampleCount / rms.Length);

        for (int w = 0; w < rms.Length; w++)
        {
            int start = w * samplesPerWindow;
            int end = Math.Min(start + samplesPerWindow, sampleCount);
            if (end <= start)
            {
                rms[w] = 0f;
                continue;
            }

            double sumSq = 0;
            for (int i = start; i < end; i++)
            {
                short s = (short)(pcm16Mono[i * 2] | (pcm16Mono[(i * 2) + 1] << 8));
                double normalised = s / (double)short.MaxValue;
                sumSq += normalised * normalised;
            }
            rms[w] = (float)Math.Sqrt(sumSq / (end - start));
        }
    }

    internal void HandleDefaultDeviceChanged()
    {
        // Notifier callbacks arrive on a COM RPC thread — take the lock so we don't race
        // a user-initiated StopAsync. Phase 6 trap: the DefaultDeviceChanged event also
        // fires on this thread; the Coordinator must marshal to its own dispatcher.
        lock (_stateLock)
        {
            if (!_isRecording)
            {
                return;
            }
            _logger.LogWarning("Default capture device changed mid-recording; aborting.");
            try
            {
                _capture?.StopRecording();
            }
            catch (COMException ex)
            {
                _logger.LogDebug(ex, "StopRecording threw during device-change handling.");
            }
        }
        DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Cleanup()
    {
        TryDispose(() => _writer?.Dispose());
        // _resampler is an IWaveProvider chain (SampleToWaveProvider16 →
        // WdlResamplingSampleProvider → SampleProvider wrapper → BufferedWaveProvider).
        // None of those are IDisposable; releasing the reference is sufficient.
        TryDispose(() => _capture?.Dispose());
        if (_enumerator is not null && _notifier is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_notifier); } catch (COMException) { }
        }
        TryDispose(() => _enumerator?.Dispose());
        TryDispose(() => _stopCts?.Dispose());

        _writer = null;
        _resampler = null;
        _capture = null;
        _enumerator = null;
        _notifier = null;
        _buffer = null;
        _workerThread = null;
        _stopCts = null;
        _isRecording = false;
    }

    private void TryDispose(Action dispose)
    {
        try
        {
            dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Best-effort dispose threw {Type}.", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Bridge from NAudio's <see cref="IMMNotificationClient"/> COM interface to a single
    /// <see cref="DefaultDeviceChanged"/> event raise per session.
    /// </summary>
    private sealed class DeviceChangeNotifier : IMMNotificationClient
    {
        private readonly AudioRecorder _owner;
        public DeviceChangeNotifier(AudioRecorder owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Capture)
            {
                _owner.HandleDefaultDeviceChanged();
            }
        }
    }
}
