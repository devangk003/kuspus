#pragma warning disable CA1848
#pragma warning disable CA1873

using System.Runtime.InteropServices;
using AudioUnit;
using AVFoundation;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KusPus.Mac.Platform;

/// <summary>
/// Lightweight microphone peak meter for Preferences → Audio and the onboarding
/// mic step. Opens an <c>AVAudioEngine</c> input tap, tracks the absolute peak
/// since the last <see cref="TakePeak"/>, writes nothing to disk. The Windows app
/// opens a <c>WasapiCapture</c> for the same purpose; same lifecycle contract —
/// start when the tab/step is visible, stop when it isn't.
/// </summary>
public sealed class MacLevelMeter : IDisposable
{
    private readonly ILogger<MacLevelMeter> _logger;
    private readonly object _lock = new();
    private AVAudioEngine? _engine;
    private NSObject? _configObserver;
    private AuhalInputCapture? _auhal;
    private float _peak;

    public MacLevelMeter(ILogger<MacLevelMeter>? logger = null)
    {
        _logger = logger ?? NullLogger<MacLevelMeter>.Instance;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Friendly name of the device the meter is listening to (after <see cref="Start"/>).</summary>
    public string DeviceName { get; private set; } = "Default device";

    /// <summary>Fires on a background thread when the engine's configuration changes (device unplugged, etc.).</summary>
    public event EventHandler? ConfigurationChanged;

    /// <summary>
    /// Starts listening. Shows the system microphone prompt first if the user has never
    /// been asked. Returns an error message on failure (permission denied, no device,
    /// engine start failure), or <c>null</c> on success.
    /// </summary>
    public async Task<string?> StartAsync(string? preferredDeviceUid)
    {
        if (Permissions.MicrophoneStatus() == AVAuthorizationStatus.NotDetermined)
        {
            await Permissions.RequestMicrophoneAsync().ConfigureAwait(true);
        }
        return Start(preferredDeviceUid);
    }

    private string? Start(string? preferredDeviceUid)
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                return null;
            }
            if (!Permissions.HasMicrophone())
            {
                return "Microphone blocked — allow KusPus in System Settings → Privacy & Security → Microphone.";
            }
            // A chosen mic goes through AUHAL: AVAudioEngine's input node only ever
            // delivers audio from the system default device (see AuhalInputCapture).
            var device = string.IsNullOrEmpty(preferredDeviceUid) ? null : CoreAudioDevices.FindByUid(preferredDeviceUid);
            if (device is not null)
            {
                DeviceName = device.Name;
                var capture = AuhalInputCapture.Start(device.DeviceId, b => OnTap(b, null!),
                    msg => _logger.LogInformation("Level meter {Device}: {Message}", device.Name, msg),
                    () => ConfigurationChanged?.Invoke(this, EventArgs.Empty), out var auhalError);
                if (capture is null)
                {
                    _logger.LogWarning("Level meter: opening {Device} via AUHAL failed: {Error}", device.Name, auhalError);
                    return $"Microphone unavailable — {auhalError}";
                }
                _auhal = capture;
                IsRunning = true;
                return null;
            }
            try
            {
                var engine = new AVAudioEngine();
                var input = engine.InputNode;
                DeviceName = CoreAudioDevices.DefaultInputDeviceName() ?? "Default device";
                var format = CoreAudioDevices.HardwareInputFormat(input);
                if (format.SampleRate <= 0 || format.ChannelCount == 0)
                {
                    engine.Dispose();
                    return "No microphone detected — plug one in or pick another in the dropdown.";
                }
                // OnTap reads each buffer's own format, so tap in the node's live format.
                var tapError = CoreAudioDevices.InstallInputTap(input, 2048, OnTap);
                if (tapError is not null)
                {
                    _logger.LogWarning("Level meter tap on {Device} ({Rate} Hz, {Ch} ch) failed: {Error}",
                        DeviceName, format.SampleRate, format.ChannelCount, tapError);
                    engine.Dispose();
                    return $"Microphone unavailable — {tapError}";
                }
                _configObserver = AVAudioEngine.Notifications.ObserveConfigurationChange(engine,
                    (_, _) => ConfigurationChanged?.Invoke(this, EventArgs.Empty));
                engine.Prepare();
                if (!engine.StartAndReturnError(out var error))
                {
                    _configObserver?.Dispose();
                    _configObserver = null;
                    engine.InputNode.RemoveTapOnBus(0);
                    engine.Dispose();
                    return $"Microphone busy or blocked — {error?.LocalizedDescription ?? "engine start failed"}.";
                }
                _engine = engine;
                IsRunning = true;
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjCRuntime.ObjCException)
            {
                _logger.LogWarning(ex, "Level meter start failed.");
                return "Microphone unavailable — see logs.";
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning)
            {
                return;
            }
            IsRunning = false;
            _auhal?.Dispose();
            _auhal = null;
            _configObserver?.Dispose();
            _configObserver = null;
            if (_engine is not null)
            {
                try { _engine.InputNode.RemoveTapOnBus(0); } catch (ObjCRuntime.ObjCException) { }
                _engine.Stop();
                _engine.Dispose();
                _engine = null;
            }
            _peak = 0;
        }
    }

    /// <summary>Returns the peak (0..1) seen since the previous call and resets it.</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0f);

    public void Dispose() => Stop();

    private void OnTap(AVAudioPcmBuffer buffer, AVAudioTime when)
    {
        var channels = (int)buffer.Format.ChannelCount;
        var frames = (int)buffer.FrameLength;
        if (frames == 0 || channels == 0)
        {
            return;
        }
        float peak = 0;
        var floatData = buffer.FloatChannelData;
        if (floatData != IntPtr.Zero)
        {
            for (int c = 0; c < channels; c++)
            {
                var ch = Marshal.ReadIntPtr(floatData, c * IntPtr.Size);
                unsafe
                {
                    var p = (float*)ch;
                    for (int i = 0; i < frames; i++)
                    {
                        var a = Math.Abs(p[i]);
                        if (a > peak)
                        {
                            peak = a;
                        }
                    }
                }
            }
        }
        else
        {
            var int16Data = buffer.Int16ChannelData;
            if (int16Data == IntPtr.Zero)
            {
                return;
            }
            for (int c = 0; c < channels; c++)
            {
                var ch = Marshal.ReadIntPtr(int16Data, c * IntPtr.Size);
                unsafe
                {
                    var p = (short*)ch;
                    for (int i = 0; i < frames; i++)
                    {
                        var a = Math.Abs(p[i]) / 32768f;
                        if (a > peak)
                        {
                            peak = a;
                        }
                    }
                }
            }
        }

        // Keep the max until the UI timer drains it.
        float current;
        do
        {
            current = _peak;
            if (peak <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peak, peak, current) != current);
    }
}
