namespace KusPus.Audio;

/// <summary>Token returned by <see cref="IAudioRecorder.StartAsync"/> identifying the active session.</summary>
public sealed record RecordingHandle(string WavPath, DateTimeOffset StartedAt);
