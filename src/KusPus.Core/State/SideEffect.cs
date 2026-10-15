namespace KusPus.Core.State;

/// <summary>
/// What the FSM asks the outer world to do as part of a transition. Emitted as data so
/// the FSM itself stays a pure function — actual I/O happens in <c>KusPus.App</c>'s
/// AppCoordinator which interprets these.
/// </summary>
public abstract record SideEffect;

public sealed record CaptureForegroundHwnd : SideEffect;
public sealed record StartHoldTimer(int HoldThresholdMs) : SideEffect;
public sealed record CancelHoldTimer : SideEffect;
public sealed record StartAudioCapture : SideEffect;
public sealed record StopAudioCapture : SideEffect;
/// <summary>
/// Best-effort stop after a <see cref="RecordingFailed"/> event. Unlike
/// <see cref="StopAudioCapture"/> the Coordinator must not treat a stop failure
/// as a new error (the recorder may already have torn itself down).
/// </summary>
public sealed record AbortAudioCapture : SideEffect;
public sealed record BeginTranscribe : SideEffect;
public sealed record DeliverTranscript(string Text, TimeSpan Duration, string Model) : SideEffect;
public sealed record HandleTranscribeFailure(string Error, string? FailedWavPath) : SideEffect;
