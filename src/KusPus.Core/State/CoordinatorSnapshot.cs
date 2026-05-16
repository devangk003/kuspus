namespace KusPus.Core.State;

/// <summary>
/// The full coordinator state as a single immutable value. <see cref="IsHoldMode"/>
/// is only meaningful when <see cref="State"/> is <see cref="AppState.Recording"/>.
/// </summary>
public sealed record CoordinatorSnapshot(AppState State, bool IsHoldMode = false);
