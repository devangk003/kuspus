namespace KusPus.Core.Hotkeys;

/// <summary>
/// Platform-agnostic key codes. Values intentionally match Win32 VK_* codes so the
/// <c>KusPus.Native</c> layer can round-trip without a translation table — but this
/// enum has no Win32 dependency. See TECH_SPEC §28 for the consuming P/Invoke layer.
/// </summary>
public enum VirtualKey : ushort
{
    None = 0x00,

    Backspace = 0x08,
    Tab = 0x09,
    Return = 0x0D,

    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    CapsLock = 0x14,
    Escape = 0x1B,
    Space = 0x20,

    Delete = 0x2E,

    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,

    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    LeftWin = 0x5B,
    RightWin = 0x5C,

    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,

    LeftShift = 0xA0,
    RightShift = 0xA1,
    LeftCtrl = 0xA2,
    RightCtrl = 0xA3,
    LeftAlt = 0xA4,
    RightAlt = 0xA5,
}
