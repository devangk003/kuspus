using KusPus.Core.Hotkeys;

namespace KusPus.Mac.Platform;

/// <summary>
/// Maps between KusPus's Windows-shaped <see cref="VirtualKey"/> enum (what settings.json
/// stores) and macOS virtual key codes (<c>Carbon/Events.h</c> <c>kVK_*</c>). The macOS
/// codes are physical ANSI positions, so letters follow the US layout — same caveat as
/// the Windows LL hook, which also reports layout-independent VKs.
///
/// Side-specific modifiers map 1:1; the Windows "Win" key is Command on a Mac.
/// </summary>
public static class MacKeyCodes
{
    // kVK_* constants we care about.
    public const ushort Return = 0x24;
    public const ushort Tab = 0x30;
    public const ushort Space = 0x31;
    public const ushort Delete = 0x33;        // Backspace
    public const ushort Escape = 0x35;
    public const ushort RightCommand = 0x36;
    public const ushort Command = 0x37;
    public const ushort Shift = 0x38;
    public const ushort CapsLock = 0x39;
    public const ushort Option = 0x3A;
    public const ushort Control = 0x3B;
    public const ushort RightShift = 0x3C;
    public const ushort RightOption = 0x3D;
    public const ushort RightControl = 0x3E;
    public const ushort Function = 0x3F;
    public const ushort ForwardDelete = 0x75;
    public const ushort V = 0x09;

    private static readonly Dictionary<ushort, VirtualKey> MacToVk = new()
    {
        [0x00] = VirtualKey.A, [0x0B] = VirtualKey.B, [0x08] = VirtualKey.C, [0x02] = VirtualKey.D,
        [0x0E] = VirtualKey.E, [0x03] = VirtualKey.F, [0x05] = VirtualKey.G, [0x04] = VirtualKey.H,
        [0x22] = VirtualKey.I, [0x26] = VirtualKey.J, [0x28] = VirtualKey.K, [0x25] = VirtualKey.L,
        [0x2E] = VirtualKey.M, [0x2D] = VirtualKey.N, [0x1F] = VirtualKey.O, [0x23] = VirtualKey.P,
        [0x0C] = VirtualKey.Q, [0x0F] = VirtualKey.R, [0x01] = VirtualKey.S, [0x11] = VirtualKey.T,
        [0x20] = VirtualKey.U, [0x09] = VirtualKey.V, [0x0D] = VirtualKey.W, [0x07] = VirtualKey.X,
        [0x10] = VirtualKey.Y, [0x06] = VirtualKey.Z,

        [0x1D] = VirtualKey.D0, [0x12] = VirtualKey.D1, [0x13] = VirtualKey.D2, [0x14] = VirtualKey.D3,
        [0x15] = VirtualKey.D4, [0x17] = VirtualKey.D5, [0x16] = VirtualKey.D6, [0x1A] = VirtualKey.D7,
        [0x1C] = VirtualKey.D8, [0x19] = VirtualKey.D9,

        [Return] = VirtualKey.Return, [Tab] = VirtualKey.Tab, [Space] = VirtualKey.Space,
        [Delete] = VirtualKey.Backspace, [Escape] = VirtualKey.Escape, [ForwardDelete] = VirtualKey.Delete,
        [CapsLock] = VirtualKey.CapsLock,

        [Control] = VirtualKey.LeftCtrl, [RightControl] = VirtualKey.RightCtrl,
        [Command] = VirtualKey.LeftWin, [RightCommand] = VirtualKey.RightWin,
        [Option] = VirtualKey.LeftAlt, [RightOption] = VirtualKey.RightAlt,
        [Shift] = VirtualKey.LeftShift, [RightShift] = VirtualKey.RightShift,

        [0x7A] = VirtualKey.F1, [0x78] = VirtualKey.F2, [0x63] = VirtualKey.F3, [0x76] = VirtualKey.F4,
        [0x60] = VirtualKey.F5, [0x61] = VirtualKey.F6, [0x62] = VirtualKey.F7, [0x64] = VirtualKey.F8,
        [0x65] = VirtualKey.F9, [0x6D] = VirtualKey.F10, [0x67] = VirtualKey.F11, [0x6F] = VirtualKey.F12,
        [0x69] = VirtualKey.F13, [0x6B] = VirtualKey.F14, [0x71] = VirtualKey.F15, [0x6A] = VirtualKey.F16,
        [0x40] = VirtualKey.F17, [0x4F] = VirtualKey.F18, [0x50] = VirtualKey.F19, [0x5A] = VirtualKey.F20,
    };

    private static readonly Dictionary<VirtualKey, ushort> VkToMac = BuildReverse();

    private static Dictionary<VirtualKey, ushort> BuildReverse()
    {
        var d = new Dictionary<VirtualKey, ushort>();
        foreach (var (mac, vk) in MacToVk)
        {
            d[vk] = mac;
        }
        // Non-sided Windows VKs: pick the left-hand key.
        d[VirtualKey.Control] = Control;
        d[VirtualKey.Shift] = Shift;
        d[VirtualKey.Alt] = Option;
        return d;
    }

    public static VirtualKey ToVirtualKey(ushort macKeyCode) =>
        MacToVk.TryGetValue(macKeyCode, out var vk) ? vk : VirtualKey.None;

    public static bool TryToMacKeyCode(VirtualKey vk, out ushort macKeyCode) =>
        VkToMac.TryGetValue(vk, out macKeyCode);

    /// <summary>
    /// <c>NX_DEVICE*KEYMASK</c> bits in <c>CGEventFlags</c> that tell left from right
    /// for a <c>FlagsChanged</c> event. CoreGraphics' public enum only has the
    /// side-agnostic masks; these device-dependent bits are stable since 10.0.
    /// Caps Lock and Fn are deliberately absent: their flag bits reflect lock state,
    /// not whether the key is physically held, so they can't serve as chord modifiers.
    /// </summary>
    public static bool TryGetModifierFlagBit(ushort macKeyCode, out ulong bit)
    {
        bit = macKeyCode switch
        {
            Control => 0x0000_0001,
            Shift => 0x0000_0002,
            RightShift => 0x0000_0004,
            Command => 0x0000_0008,
            RightCommand => 0x0000_0010,
            Option => 0x0000_0020,
            RightOption => 0x0000_0040,
            RightControl => 0x0000_2000,
            _ => 0,
        };
        return bit != 0;
    }

    /// <summary>Key codes that only ever arrive as <c>FlagsChanged</c> (sided modifiers, Caps Lock, Fn).</summary>
    public static bool IsModifierKeyCode(ushort macKeyCode) =>
        TryGetModifierFlagBit(macKeyCode, out _) || macKeyCode is CapsLock or Function;
}
