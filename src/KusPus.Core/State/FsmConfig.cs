namespace KusPus.Core.State;

/// <summary>
/// Parameters the FSM needs to compute transitions. Kept separate from
/// <see cref="CoordinatorSnapshot"/> because it's config, not state.
/// </summary>
public sealed record FsmConfig(int HoldThresholdMs = 250);
