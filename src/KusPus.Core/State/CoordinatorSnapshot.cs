namespace KusPus.Core.State;

/// <summary>
/// Post-paste payload attached to a snapshot. Non-null for one snapshot per
/// dictation cycle — emitted by <c>AppCoordinator</c> after <c>PasteEngine</c>
/// finishes, so the pill can show "Pasted into &lt;App&gt;" (or the error reason)
/// for the hold defined in <c>docs/PILL_DESIGN.md</c> §2.5 / §2.6.
/// </summary>
/// <param name="Pasted">True if the paste keystroke reached the target window.</param>
/// <param name="TargetApp">Friendly app name (or "?" if not resolvable).</param>
/// <param name="ErrorReason">Non-null when <paramref name="Pasted"/> is false — short user-facing reason ("Microphone blocked", "Disk full", etc.).</param>
public sealed record PostPasteInfo(bool Pasted, string TargetApp, string? ErrorReason);

/// <summary>
/// The full coordinator state as a single immutable value. <see cref="IsHoldMode"/>
/// is only meaningful when <see cref="State"/> is <see cref="AppState.Recording"/>.
/// <see cref="PostPaste"/> is non-null only on the post-paste snapshot.
/// </summary>
public sealed record CoordinatorSnapshot(
    AppState State,
    bool IsHoldMode = false,
    PostPasteInfo? PostPaste = null);
