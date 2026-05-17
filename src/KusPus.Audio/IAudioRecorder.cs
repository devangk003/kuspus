using KusPus.Core;

namespace KusPus.Audio;

/// <summary>
/// WASAPI shared-mode capture from the default input device, written to
/// <c>%TEMP%\kuspus-{unixMs}.wav</c> as 16 kHz mono 16-bit PCM. See TECH_SPEC §14.
/// </summary>
public interface IAudioRecorder
{
    /// <summary>20-element RMS over the most recent ~1/15s of audio, published at 15 Hz.</summary>
    IObservable<float[]> Levels { get; }

    /// <summary>Fired exactly once when the OS reports the default capture device changed mid-recording.</summary>
    event EventHandler? DefaultDeviceChanged;

    /// <summary>
    /// Sets the preferred input device id (Windows MMDevice endpoint id, e.g.
    /// <c>{0.0.1.00000000}.{guid}</c>). Subsequent <see cref="StartAsync"/> calls
    /// open that device. Pass <c>null</c> to follow the OS default. Composition
    /// root pushes this from <c>IPrefsStore.Audio.InputDeviceId</c> on every
    /// settings change — AudioRecorder doesn't take a Persistence dependency.
    /// </summary>
    void SetInputDeviceId(string? deviceId);

    /// <summary>Open the mic and begin writing the WAV file.</summary>
    Task<Result<RecordingHandle>> StartAsync(CancellationToken ct = default);

    /// <summary>Stop capture, flush the WAV file, return its path and duration.</summary>
    Task<Result<RecordedFile>> StopAsync();
}
