namespace KusPus.Core.State;

/// <summary>
/// Top-level FSM state. The recording sub-mode (hold vs tap) lives on
/// <see cref="CoordinatorSnapshot.IsHoldMode"/>, not as a separate state.
/// See TECH_SPEC §12.
/// </summary>
public enum AppState
{
    Idle,
    Armed,
    Recording,
    Transcribing,
    Cancelled,
}
