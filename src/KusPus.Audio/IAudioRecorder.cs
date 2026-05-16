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

    /// <summary>Open the mic and begin writing the WAV file.</summary>
    Task<Result<RecordingHandle>> StartAsync(CancellationToken ct = default);

    /// <summary>Stop capture, flush the WAV file, return its path and duration.</summary>
    Task<Result<RecordedFile>> StopAsync();
}
