namespace KusPus.Audio;

/// <summary>The output of a completed recording. Returned by <see cref="IAudioRecorder.StopAsync"/>.</summary>
/// <param name="CappedAtLimit">
/// True when the recorder auto-stopped because it hit the 50-minute hard cap (§14).
/// Phase 6 surfaces this as the "Recording capped at 50 min" pill message.
/// </param>
public sealed record RecordedFile(string WavPath, TimeSpan Duration, bool CappedAtLimit = false);
