// IHotkeyEngine uses the spec §13 method names verbatim — Start/Stop. CA1716 flags
// Stop as a reserved keyword in other CLI languages (VB.NET) but this is an
// internal-only WPF app; following the spec letter wins over the analyzer's
// language-portability concern.
#pragma warning disable CA1716

using KusPus.Core.Hotkeys;

namespace KusPus.Native;

/// <summary>
/// Globally observes the keyboard via a WH_KEYBOARD_LL hook and reports chord-level
/// events. See TECH_SPEC §13. The Coordinator does the hold-vs-tap classification on
/// top of these raw engage/release events.
/// </summary>
public interface IHotkeyEngine
{
    IObservable<HotkeyEvent> Events { get; }
    void Start();
    void Stop();
    void SetChord(HotkeyChord chord);
}

public sealed record HotkeyChord(IReadOnlyList<KusPus.Core.Hotkeys.VirtualKey> Modifiers, KusPus.Core.Hotkeys.VirtualKey? Key)
{
    public static HotkeyChord Default { get; } =
        new([KusPus.Core.Hotkeys.VirtualKey.LeftCtrl, KusPus.Core.Hotkeys.VirtualKey.LeftWin], null);
}
