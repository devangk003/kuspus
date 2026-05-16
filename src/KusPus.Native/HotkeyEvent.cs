namespace KusPus.Native;

/// <summary>Raw chord-level events from <see cref="IHotkeyEngine"/>. See TECH_SPEC §13.</summary>
public abstract record HotkeyEvent;

public sealed record ChordEngaged : HotkeyEvent;
public sealed record ChordReleased : HotkeyEvent;
public sealed record OtherKeyPressed : HotkeyEvent;
public sealed record HookReinstalled : HotkeyEvent;
