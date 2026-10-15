using System.Runtime.InteropServices;
using AudioToolbox;
using AudioUnit;
using AVFoundation;
using CoreFoundation;

namespace KusPus.Mac.Platform;

/// <summary>
/// Captures from one specific input device through a standalone AUHAL unit.
///
/// <para>Why not <c>AVAudioEngine</c>: its input node binds to the system default input
/// when it is first touched; re-pointing it with <c>kAudioOutputUnitProperty_CurrentDevice</c>
/// reports success but delivers no buffers (observed here: 0.0 s captured on the built-in
/// mic while Bluetooth buds were the default). A dedicated AUHAL bound to the device
/// before it initialises does deliver — the same approach Chromium's mac input uses.</para>
///
/// <para>AUHAL does no sample-rate conversion on input, so the client format is float32
/// non-interleaved at the device's own rate and channel count; callers convert to 16 kHz.
/// Buffers are rendered on the real-time IO thread, copied, and handed to
/// <c>onBuffer</c> on a private serial queue (in order) so file writes and conversion
/// never run on the IO thread.</para>
/// </summary>
internal sealed class AuhalInputCapture : IDisposable
{
    private const uint InputElement = 1;
    private const uint OutputElement = 0;
    private const uint MaxFrames = 8192;

    private readonly AudioUnit.AudioUnit _unit;
    private readonly AVAudioFormat _format;
    private readonly AVAudioPcmBuffer _render;
    private readonly AudioBuffers _buffers;
    private readonly Action<AVAudioPcmBuffer> _onBuffer;
    private readonly DispatchQueue _queue = new("KusPus.AuhalInput");
    private readonly Action<string>? _onDiagnostic;
    private bool _renderErrorReported;
    private bool _firstBufferReported;
    private bool _started;
    private bool _disposed;
    private IDisposable? _aliveWatch;

    public double SampleRate => _format.SampleRate;
    public uint ChannelCount => _format.ChannelCount;

    private AuhalInputCapture(AudioUnit.AudioUnit unit, AVAudioFormat format, Action<AVAudioPcmBuffer> onBuffer, Action<string>? onDiagnostic)
    {
        _unit = unit;
        _format = format;
        _onBuffer = onBuffer;
        _onDiagnostic = onDiagnostic;
        _render = new AVAudioPcmBuffer(format, MaxFrames);
        _buffers = new AudioBuffers((int)format.ChannelCount);
        // Non-interleaved: one channel per buffer. AudioBuffers(n) zero-initialises
        // NumberChannels and SetData never sets it.
        for (int c = 0; c < _buffers.Count; c++)
        {
            var b = _buffers[c];
            b.NumberChannels = 1;
            _buffers[c] = b;
        }
    }

    /// <summary>
    /// Creates and starts a capture on <paramref name="deviceId"/>. Returns the capture,
    /// or null with <paramref name="error"/> describing the failing step.
    /// </summary>
    /// <paramref name="onDiagnostic"/> receives one-off status lines (first buffer, render
    /// failure) on the delivery queue.
    /// <paramref name="onDisconnected"/> fires (on a CoreAudio thread) if the device goes away mid-capture.
    public static AuhalInputCapture? Start(uint deviceId, Action<AVAudioPcmBuffer> onBuffer, Action<string>? onDiagnostic, Action? onDisconnected, out string? error)
    {
        error = null;
        var desc = AudioComponentDescription.CreateOutput(AudioTypeOutput.HAL);
        var component = AudioComponent.FindComponent(ref desc);
        if (component is null)
        {
            error = "HAL output component not found";
            return null;
        }
        var unit = new AudioUnit.AudioUnit(component);
        AuhalInputCapture? capture = null;
        try
        {
            Check(unit.SetEnableIO(true, AudioUnitScopeType.Input, InputElement), "enable input");
            Check(unit.SetEnableIO(false, AudioUnitScopeType.Output, OutputElement), "disable output");
            // Always written explicitly: an AUHAL left on its default device can report the
            // right device and still deliver nothing.
            Check(unit.SetCurrentDevice(deviceId, AudioUnitScopeType.Global, 0), "set device");

            var hw = unit.GetAudioFormat(AudioUnitScopeType.Input, InputElement);
            if (hw.SampleRate <= 0 || hw.ChannelsPerFrame <= 0)
            {
                throw new InvalidOperationException($"device reports no input format ({hw.SampleRate} Hz, {hw.ChannelsPerFrame} ch)");
            }
            var format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, hw.SampleRate, (uint)hw.ChannelsPerFrame, interleaved: false);
            Check(unit.SetFormat(format.StreamDescription, AudioUnitScopeType.Output, InputElement), "set client format");

            capture = new AuhalInputCapture(unit, format, onBuffer, onDiagnostic);
            // The .NET binding stores the delegate keyed by the element passed here, but its
            // native trampoline looks it up by the bus number Core Audio calls back with —
            // and AUHAL delivers input on bus 1. Registering on element 0 (the usual C
            // idiom) makes every callback miss the lookup and silently return an error:
            // the mic opens and no audio ever arrives. So register on element 1; if AUHAL
            // rejects the property there, also set it on element 0 (the bus-1 entry is
            // already recorded, so dispatch still resolves).
            if (unit.SetInputCallback(capture.OnInput, AudioUnitScopeType.Global, InputElement) != AudioUnitStatus.OK)
            {
                Check(unit.SetInputCallback(capture.OnInput, AudioUnitScopeType.Global, 0), "set input callback");
            }
            Check(unit.Initialize(), "initialize");
            Check(unit.Start(), "start");
            capture._started = true;
            if (onDisconnected is not null)
            {
                // AUHAL has no AVAudioEngine-style configuration notification: without this
                // an unplugged mic would just go silent for the rest of the take.
                capture._aliveWatch = CoreAudioDevices.WatchDeviceAlive(deviceId, onDisconnected);
            }
            return capture;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (capture is not null)
            {
                capture.Dispose();
            }
            else
            {
                unit.Dispose();
            }
            return null;
        }
    }

    private static void Check(AudioUnitStatus status, string step)
    {
        if (status != AudioUnitStatus.OK)
        {
            throw new InvalidOperationException($"{step} failed ({status})");
        }
    }

    private static void Check(int status, string step)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{step} failed ({status})");
        }
    }

    // Real-time IO thread: render, copy, hand off. No locks, no file I/O.
    private AudioUnitStatus OnInput(AudioUnitRenderActionFlags flags, AudioTimeStamp timeStamp, uint busNumber, uint numberFrames, AudioUnit.AudioUnit unit)
    {
        if (numberFrames == 0 || numberFrames > MaxFrames || _disposed)
        {
            return AudioUnitStatus.OK;
        }
        int channels = (int)_format.ChannelCount;
        int bytes = (int)numberFrames * sizeof(float);
        var channelData = _render.FloatChannelData;
        for (int c = 0; c < channels; c++)
        {
            _buffers.SetData(c, Marshal.ReadIntPtr(channelData, c * IntPtr.Size), bytes);
        }
        var status = unit.Render(ref flags, timeStamp, busNumber, numberFrames, _buffers);
        if (status != AudioUnitStatus.OK)
        {
            if (!_renderErrorReported)
            {
                _renderErrorReported = true;
                var report = _onDiagnostic;
                _queue.DispatchAsync(() => report?.Invoke($"AudioUnitRender failed ({status})"));
            }
            return status;
        }

        if (!_firstBufferReported)
        {
            _firstBufferReported = true;
            var report = _onDiagnostic;
            var rate = _format.SampleRate;
            _queue.DispatchAsync(() => report?.Invoke($"first buffer: {numberFrames} frames at {rate} Hz"));
        }

        var copy = new AVAudioPcmBuffer(_format, numberFrames) { FrameLength = numberFrames };
        var dst = copy.FloatChannelData;
        for (int c = 0; c < channels; c++)
        {
            unsafe
            {
                // Copy from where Render actually wrote (it may substitute its own buffers).
                Buffer.MemoryCopy(
                    (void*)_buffers[c].Data,
                    (void*)Marshal.ReadIntPtr(dst, c * IntPtr.Size),
                    bytes, bytes);
            }
        }
        _queue.DispatchAsync(() =>
        {
            try
            {
                if (!_disposed)
                {
                    _onBuffer(copy);
                }
            }
            finally
            {
                copy.Dispose();
            }
        });
        return AudioUnitStatus.OK;
    }

    /// <summary>Stops the unit and waits for already-queued buffers to be delivered.</summary>
    public void Stop()
    {
        if (_started)
        {
            _started = false;
            _unit.Stop();
        }
        _queue.DispatchSync(() => { });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _aliveWatch?.Dispose();
        _aliveWatch = null;
        Stop();
        _disposed = true;
        _unit.Uninitialize();
        _unit.Dispose();
        _buffers.Dispose();
        _render.Dispose();
        _format.Dispose();
    }
}
