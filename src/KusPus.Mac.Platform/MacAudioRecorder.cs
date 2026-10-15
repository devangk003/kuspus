// Logs on start/stop/device-change/cap only — not a hot path.
#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Diagnostics;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using AVFoundation;
using Foundation;
using KusPus.Core;
using KusPus.Core.Audio;
using KusPus.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.Platform;

/// <summary>
/// <c>AVAudioEngine</c>-backed <see cref="IAudioRecorder"/>. Same contract as the
/// Windows NAudio recorder: 16 kHz mono 16-bit PCM WAV in the temp dir, 20-band RMS
/// levels at ~15 Hz, 50-minute wall-clock cap, and a <see cref="DefaultDeviceChanged"/>
/// abort when the input configuration changes mid-take.
///
/// Pipeline: input-node tap (device native format, usually 48 kHz float32)
/// → <c>AVAudioConverter</c> (resample + downmix + int16) → <see cref="PcmWavWriter"/>.
/// The tap block runs on a real-time audio thread; we do the conversion there (it's
/// cheap) and only file I/O on a buffered stream.
///
/// Thread-safety: <see cref="_stateLock"/> guards start/stop; the tap never takes it.
/// Instead the tap increments <see cref="_tapInFlight"/> and <see cref="Cleanup"/> waits
/// (bounded) for in-flight taps to drain before disposing the writer/converter. Any
/// teardown triggered from the audio thread (disk full, cap) is deferred to the thread
/// pool — calling <c>AVAudioEngine.Stop</c> from inside its own tap can deadlock.
///
/// Bluetooth headsets switch to the hands-free profile when input opens, which posts a
/// configuration change right after start; that first change restarts the engine once
/// (same writer) instead of aborting the take.
/// </summary>
public sealed class MacAudioRecorder : IAudioRecorder, IDisposable
{
    private const int TargetSampleRate = 16_000;
    private const int MaxRecordingSeconds = 50 * 60;
    private const uint TapFrames = 4096;
    private static readonly TimeSpan EarlyReconfigureWindow = TimeSpan.FromSeconds(2);

    private readonly string _tempDir;
    private readonly ILogger<MacAudioRecorder> _logger;
    private readonly Subject<float[]> _levels = new();
    private readonly object _stateLock = new();

    private AVAudioEngine? _engine;
    // Used instead of _engine when a specific mic is chosen (see AuhalInputCapture).
    private AuhalInputCapture? _auhal;
    private string _deviceName = "default input";
    private AVAudioConverter? _converter;
    private AVAudioPcmBuffer? _outBuffer;
    private AVAudioFormat? _outFormat;
    private double _inSampleRate;
    private PcmWavWriter? _writer;
    private NSObject? _configObserver;
    private Stopwatch? _clock;
    private TimeSpan _lastLevelPost;
    private string? _wavPath;
    private DateTimeOffset _startedAt;
    private volatile bool _isRecording;
    private volatile bool _hitDurationCap;
    private volatile bool _teardownQueued;
    private int _restartCount;
    private volatile bool _writeFailed;
    // Bluetooth HFP switches can post several configuration changes in a row.
    private const int MaxEarlyRestarts = 3;
    private int _tapInFlight;
    private volatile string? _preferredDeviceUid;
    private byte[] _pcmScratch = new byte[TapFrames * 2];

    public MacAudioRecorder(string? tempDir = null, ILogger<MacAudioRecorder>? logger = null)
    {
        _tempDir = tempDir ?? Path.GetTempPath();
        _logger = logger ?? NullLogger<MacAudioRecorder>.Instance;
    }

    public IObservable<float[]> Levels => _levels;

    public event EventHandler? DefaultDeviceChanged;

    public void SetInputDeviceId(string? deviceId) => _preferredDeviceUid = deviceId;

    public async Task<Result<RecordingHandle>> StartAsync(CancellationToken ct = default)
    {
        // First use: show the system microphone prompt instead of failing on
        // "NotDetermined" (onboarding may have been skipped).
        if (Permissions.MicrophoneStatus() == AVAuthorizationStatus.NotDetermined)
        {
            await Permissions.RequestMicrophoneAsync().ConfigureAwait(false);
        }
        if (!Permissions.HasMicrophone())
        {
            return Result.Fail<RecordingHandle>(
                "Microphone blocked — allow KusPus in System Settings → Privacy & Security → Microphone.");
        }

        lock (_stateLock)
        {
            if (_isRecording)
            {
                return Result.Fail<RecordingHandle>("Already recording.");
            }

            try
            {
                Directory.CreateDirectory(_tempDir);
                _wavPath = Path.Combine(_tempDir, $"kuspus-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.wav");
                _writer = new PcmWavWriter(_wavPath, TargetSampleRate, 1);
                _startedAt = DateTimeOffset.UtcNow;
                _hitDurationCap = false;
                _teardownQueued = false;
                _restartCount = 0;
                _writeFailed = false;
                _clock = Stopwatch.StartNew();
                _lastLevelPost = TimeSpan.Zero;

                var error = StartEngineLocked();
                if (error is not null)
                {
                    Cleanup();
                    DeleteQuietly(_wavPath);
                    return Result.Fail<RecordingHandle>(error);
                }

                _isRecording = true;
                _logger.LogInformation(
                    "Started recording ({Rate} Hz → 16 kHz mono) to {Path}.", _inSampleRate, _wavPath);
                return Result.Ok(new RecordingHandle(_wavPath, _startedAt));
            }
            catch (Exception ex)
            {
                // Bindings throw plain System.Exception when a native init returns nil
                // (e.g. AVAudioConverter for an unsupported format), so catch broadly:
                // anything escaping here would leak the open writer.
                Cleanup();
                DeleteQuietly(_wavPath);
                return Result.Fail<RecordingHandle>($"Failed to open microphone: {ex.Message}", ex);
            }
        }
    }

    public Task<Result<RecordedFile>> StopAsync()
    {
        lock (_stateLock)
        {
            if (!_isRecording || _wavPath is null)
            {
                return Task.FromResult(Result.Fail<RecordedFile>("Not recording."));
            }

            var duration = DateTimeOffset.UtcNow - _startedAt;
            var path = _wavPath;
            var capped = _hitDurationCap;
            // Audio actually captured — a device binding that "succeeds" but delivers no
            // frames shows up here as ~0 s of audio.
            var capturedBytes = _writer?.BytesWritten ?? 0;
            var capturedSeconds = capturedBytes / (TargetSampleRate * 2.0);
            var deviceName = _deviceName;

            Cleanup();

            _logger.LogInformation("Stopped recording after {Seconds:F1}s ({Captured:F1}s of audio captured from {Device}){Capped}.",
                duration.TotalSeconds, capturedSeconds, deviceName, capped ? " (hit the 50-minute cap)" : string.Empty);
            if (capturedBytes == 0 && duration > TimeSpan.FromSeconds(0.5))
            {
                // Nothing reached the file: report the mic, not "No speech detected" (or a
                // whisper-cli failure on an empty WAV).
                DeleteQuietly(path);
                return Task.FromResult(Result.Fail<RecordedFile>(
                    $"No audio from {deviceName} — check the mic in Preferences → Audio."));
            }
            return Task.FromResult(Result.Ok(new RecordedFile(path, duration, capped)));
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            Cleanup();
        }
        _levels.Dispose();
    }

    // ── engine lifecycle (callers hold _stateLock) ───────────────────────────

    /// <summary>Builds engine + converter for the current device and starts it. Returns an error message or null.</summary>
    private string? StartEngineLocked()
    {
        var preferredUid = _preferredDeviceUid;
        if (!string.IsNullOrEmpty(preferredUid))
        {
            var device = CoreAudioDevices.FindByUid(preferredUid);
            if (device is not null)
            {
                return StartAuhalLocked(device);
            }
            _logger.LogWarning("Preferred input device {Uid} not present; falling back to system default.", preferredUid);
        }

        // System default: AVAudioEngine's input node follows the default device. (It can't
        // be re-pointed at another device — that delivers silence — hence the AUHAL path.)
        var engine = new AVAudioEngine();
        var input = engine.InputNode;
        var deviceName = CoreAudioDevices.DefaultInputDeviceName() ?? "default input";
        _deviceName = deviceName;

        // The hardware-side format, not GetBusOutputFormat(0): after switching the input
        // node to a preferred device, the output format can still describe the previous
        // default device (e.g. 16 kHz Bluetooth while the built-in mic runs at 48 kHz),
        // and a tap/converter built from it fails with -10868 (format not supported).
        var inFormat = CoreAudioDevices.HardwareInputFormat(input);
        if (inFormat.SampleRate <= 0 || inFormat.ChannelCount == 0)
        {
            engine.Dispose();
            return $"No input device available ({deviceName}).";
        }

        var outFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, TargetSampleRate, 1, interleaved: true);
        var converter = CreateConverter(inFormat, outFormat);
        if (converter is null)
        {
            outFormat.Dispose();
            engine.Dispose();
            return $"AVAudioConverter rejected the input format of {deviceName}.";
        }

        // Null tap format = the node's live format; each buffer carries its real format
        // and TapCore rebuilds the converter if it differs from the one guessed here.
        var tapError = CoreAudioDevices.InstallInputTap(input, TapFrames, OnTap);
        if (tapError is not null)
        {
            _logger.LogWarning("Installing the input tap on {Device} ({Rate} Hz, {Ch} ch) failed: {Error}",
                deviceName, inFormat.SampleRate, inFormat.ChannelCount, tapError);
            converter.Dispose();
            outFormat.Dispose();
            engine.Dispose();
            return $"Failed to open microphone ({deviceName}): {tapError}";
        }

        _configObserver = AVAudioEngine.Notifications.ObserveConfigurationChange(engine, (_, _) => QueueConfigurationChange());

        engine.Prepare();
        if (!engine.StartAndReturnError(out var error))
        {
            _logger.LogWarning("Starting the audio engine on {Device} ({Rate} Hz, {Ch} ch) failed: {Error}",
                deviceName, inFormat.SampleRate, inFormat.ChannelCount, error?.LocalizedDescription);
            try { input.RemoveTapOnBus(0); } catch (ObjCRuntime.ObjCException) { }
            _configObserver?.Dispose();
            _configObserver = null;
            converter.Dispose();
            outFormat.Dispose();
            engine.Dispose();
            return $"Failed to open microphone ({deviceName}): {error?.LocalizedDescription ?? "AVAudioEngine start failed"}";
        }

        _logger.LogInformation("Opened {Device} ({Rate} Hz, {Ch} ch).", deviceName, inFormat.SampleRate, inFormat.ChannelCount);
        _engine = engine;
        _outFormat = outFormat;
        _converter = converter;
        _inSampleRate = inFormat.SampleRate;
        EnsureOutputCapacity(TapFrames);
        return null;
    }

    private string? StartAuhalLocked(CoreAudioDevices.InputDevice device)
    {
        _deviceName = device.Name;
        // Converter is created lazily in TapCore from the first buffer's real format.
        _outFormat = new AVAudioFormat(AVAudioCommonFormat.PCMInt16, TargetSampleRate, 1, interleaved: true);
        var capture = AuhalInputCapture.Start(
            device.DeviceId,
            buffer => OnTap(buffer, null!),
            msg => _logger.LogInformation("{Device}: {Message}", device.Name, msg),
            () => ThreadPool.QueueUserWorkItem(_ => OnChosenDeviceDisconnected(device.Name)),
            out var error);
        if (capture is null)
        {
            _logger.LogWarning("Opening {Device} via AUHAL failed: {Error}", device.Name, error);
            _outFormat.Dispose();
            _outFormat = null;
            return $"Failed to open microphone ({device.Name}): {error}";
        }
        _auhal = capture;
        _inSampleRate = capture.SampleRate;
        EnsureOutputCapacity(TapFrames);
        _logger.LogInformation("Opened {Device} via AUHAL ({Rate} Hz, {Ch} ch).", device.Name, capture.SampleRate, capture.ChannelCount);
        return null;
    }

    private void OnChosenDeviceDisconnected(string deviceName)
    {
        lock (_stateLock)
        {
            if (!_isRecording || _auhal is null)
            {
                return;
            }
            _logger.LogWarning("{Device} disconnected mid-recording; aborting capture.", deviceName);
            _auhal.Stop();
        }
        // Same path as a device change on the engine: the coordinator aborts the take.
        DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>16 kHz mono int16 converter for <paramref name="inFormat"/>, or null if AVFoundation rejects it.</summary>
    private AVAudioConverter? CreateConverter(AVAudioFormat inFormat, AVAudioFormat outFormat)
    {
        try
        {
            // Bindings throw (rather than return null) when the native init returns nil.
            var converter = new AVAudioConverter(inFormat, outFormat)
            {
                // Without this, N→1 channel conversion keeps channel 0 and drops the rest —
                // a mic wired to input 2 of an interface would record silence.
                Downmix = true,
            };
            return converter;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AVAudioConverter rejected input format {Rate} Hz, {Ch} ch.", inFormat.SampleRate, inFormat.ChannelCount);
            return null;
        }
    }

    private void StopEngineLocked()
    {
        _configObserver?.Dispose();
        _configObserver = null;
        // Stops the unit and delivers already-queued buffers before we tear down below.
        _auhal?.Dispose();
        _auhal = null;
        if (_engine is not null)
        {
            try { _engine.InputNode.RemoveTapOnBus(0); } catch (ObjCRuntime.ObjCException) { }
            try { _engine.Stop(); } catch (ObjCRuntime.ObjCException) { }
            _engine.Dispose();
            _engine = null;
        }
        WaitForTapDrain();
        _outBuffer?.Dispose();
        _outBuffer = null;
        _converter?.Dispose();
        _converter = null;
        _outFormat?.Dispose();
        _outFormat = null;
    }

    private void WaitForTapDrain()
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _tapInFlight) > 0 && sw.ElapsedMilliseconds < 250)
        {
            Thread.Sleep(2);
        }
    }

    private void EnsureOutputCapacity(uint inputFrames)
    {
        if (_outFormat is null)
        {
            return;
        }
        var ratio = TargetSampleRate / _inSampleRate;
        var needed = (uint)Math.Ceiling(inputFrames * ratio) + 64;
        if (_outBuffer is null || _outBuffer.FrameCapacity < needed)
        {
            _outBuffer?.Dispose();
            _outBuffer = new AVAudioPcmBuffer(_outFormat, needed);
            if (_pcmScratch.Length < needed * 2)
            {
                _pcmScratch = new byte[needed * 2];
            }
        }
    }

    // ── real-time audio thread ───────────────────────────────────────────────

    private void OnTap(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        // Count ourselves in-flight *before* checking state so teardown's drain wait
        // can't slip between the check and the use of the converter/writer.
        Interlocked.Increment(ref _tapInFlight);
        try
        {
            if (!_isRecording || _teardownQueued)
            {
                return;
            }
            TapCore(buffer);
        }
        catch (ObjectDisposedException)
        {
            // Teardown won the race — nothing to do.
        }
        finally
        {
            Interlocked.Decrement(ref _tapInFlight);
        }
    }

    private void TapCore(AVAudioPcmBuffer buffer)
    {
        var converter = _converter;
        var writer = _writer;
        var clock = _clock;
        var outFormat = _outFormat;
        if (writer is null || clock is null || outFormat is null)
        {
            return;
        }

        // Buffers arrive in the input's live format, which can differ from the format read
        // at start (stale device format, Bluetooth profile switch) or not be known yet (the
        // AUHAL path creates the converter on the first buffer). Follow the real buffers.
        var bufFormat = buffer.Format;
        if (converter is null
            || bufFormat.SampleRate != converter.InputFormat.SampleRate
            || bufFormat.ChannelCount != converter.InputFormat.ChannelCount)
        {
            var rebuilt = CreateConverter(bufFormat, outFormat);
            if (rebuilt is null)
            {
                return;
            }
            if (converter is not null)
            {
                _logger.LogInformation("Input format is {Rate} Hz, {Ch} ch (expected {OldRate} Hz, {OldCh} ch); rebuilt the converter.",
                    bufFormat.SampleRate, bufFormat.ChannelCount, converter.InputFormat.SampleRate, converter.InputFormat.ChannelCount);
                converter.Dispose();
            }
            _converter = rebuilt;
            _inSampleRate = bufFormat.SampleRate;
            converter = rebuilt;
        }

        EnsureOutputCapacity(buffer.FrameLength);
        var outBuffer = _outBuffer;
        if (outBuffer is null)
        {
            return;
        }

        // Feed this one input buffer; keep pulling output until the converter has
        // drained it (a large tap buffer can need more than one output buffer).
        bool consumed = false;
        AVAudioConverterOutputStatus status;
        do
        {
            outBuffer.FrameLength = 0;
            status = converter.ConvertToBuffer(outBuffer, out var error, (uint _, out AVAudioConverterInputStatus inStatus) =>
            {
                if (consumed)
                {
                    inStatus = AVAudioConverterInputStatus.NoDataNow;
                    return null!;
                }
                consumed = true;
                inStatus = AVAudioConverterInputStatus.HaveData;
                return buffer;
            });
            if (status == AVAudioConverterOutputStatus.Error)
            {
                _logger.LogWarning("AVAudioConverter error: {Err}", error?.LocalizedDescription);
                return;
            }

            int frames = (int)outBuffer.FrameLength;
            if (frames > 0)
            {
                var channel0 = Marshal.ReadIntPtr(outBuffer.Int16ChannelData);
                int bytes = frames * 2;
                Marshal.Copy(channel0, _pcmScratch, 0, bytes);
                var pcm = _pcmScratch.AsSpan(0, bytes);
                try
                {
                    writer.Write(pcm);
                }
                catch (IOException ex)
                {
                    // Disk full / volume gone. Never tear down from the audio thread.
                    _logger.LogError(ex, "Audio write failed; aborting recording.");
                    _writeFailed = true;
                    QueueConfigurationChange();
                    return;
                }
                PostLevels(pcm, clock);
            }
        }
        while (status == AVAudioConverterOutputStatus.HaveData);

        if (clock.Elapsed.TotalSeconds >= MaxRecordingSeconds && !_hitDurationCap)
        {
            // Same contract as Windows: stop capturing, keep the take; StopAsync reports
            // CappedAtLimit=true and the coordinator transcribes what was recorded.
            _hitDurationCap = true;
            _logger.LogWarning("Recording exceeded the {Minutes}-minute cap; stopping capture.", MaxRecordingSeconds / 60);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_stateLock)
                {
                    try { _engine?.Stop(); } catch (ObjCRuntime.ObjCException) { }
                    _auhal?.Stop();
                }
            });
        }
    }

    private void PostLevels(ReadOnlySpan<byte> pcm, Stopwatch clock)
    {
        var elapsed = clock.Elapsed;
        if (elapsed - _lastLevelPost <= TimeSpan.FromMilliseconds(AudioLevels.LevelPostIntervalMs))
        {
            return;
        }
        var rms = new float[AudioLevels.LevelChannels];
        AudioLevels.ComputeRms(pcm, rms);
        _levels.OnNext(rms);
        _lastLevelPost = elapsed;
    }

    // ── configuration changes (device unplugged / default changed / HFP switch) ──

    private void QueueConfigurationChange()
    {
        if (_teardownQueued)
        {
            return;
        }
        _teardownQueued = true;
        ThreadPool.QueueUserWorkItem(_ => HandleConfigurationChange());
    }

    private void HandleConfigurationChange()
    {
        bool fire = false;
        lock (_stateLock)
        {
            if (!_isRecording)
            {
                return;
            }
            var sinceStart = DateTimeOffset.UtcNow - _startedAt;
            if (!_writeFailed && _restartCount < MaxEarlyRestarts && sinceStart < EarlyReconfigureWindow)
            {
                // Typical Bluetooth HFP profile switch right after start: rebuild the
                // engine and keep writing to the same file.
                _restartCount++;
                _logger.LogInformation("Audio configuration changed {Ms} ms after start; restarting the engine (#{N}).", (int)sinceStart.TotalMilliseconds, _restartCount);
                StopEngineLocked();
                // Re-arm before starting: the new engine's observer can fire during
                // StartEngineLocked, and that change must not be dropped.
                _teardownQueued = false;
                var error = StartEngineLocked();
                if (error is null)
                {
                    return;
                }
                _logger.LogWarning("Engine restart failed ({Error}); aborting the take.", error);
            }
            _logger.LogWarning("Audio configuration changed mid-recording; aborting capture.");
            try { _engine?.Stop(); } catch (ObjCRuntime.ObjCException) { }
            _auhal?.Stop();
            fire = true;
        }
        if (fire)
        {
            DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Cleanup()
    {
        _isRecording = false;
        try
        {
            StopEngineLocked();
        }
        catch (ObjCRuntime.ObjCException ex)
        {
            _logger.LogDebug(ex, "Best-effort engine teardown threw.");
        }
        try
        {
            _writer?.Dispose();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Finalising the WAV failed.");
        }
        _writer = null;
        _clock = null;
    }

    private void DeleteQuietly(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }
}
