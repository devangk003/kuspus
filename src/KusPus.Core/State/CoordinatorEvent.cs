namespace KusPus.Core.State;

/// <summary>
/// Anything that can drive a state transition. Events are pushed into the coordinator's
/// channel from any thread; the FSM consumes them serially. See TECH_SPEC §12.
/// </summary>
public abstract record CoordinatorEvent;

public sealed record ChordEngaged : CoordinatorEvent;
public sealed record ChordReleased : CoordinatorEvent;
public sealed record HoldThresholdElapsed : CoordinatorEvent;
public sealed record OtherKeyPressedWhileArmed : CoordinatorEvent;
public sealed record TranscribeComplete(string Text, TimeSpan Duration, string Model) : CoordinatorEvent;
public sealed record TranscribeFailed(string Error, string? FailedWavPath) : CoordinatorEvent;
public sealed record ToggleFromTray : CoordinatorEvent;
