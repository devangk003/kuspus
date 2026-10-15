using FluentAssertions;
using KusPus.Core.Hotkeys;
using Xunit;

namespace KusPus.Mac.Platform.Tests;

public class MacKeyCodesTests
{
    [Theory]
    [InlineData(MacKeyCodes.Control, VirtualKey.LeftCtrl)]
    [InlineData(MacKeyCodes.RightControl, VirtualKey.RightCtrl)]
    [InlineData(MacKeyCodes.Command, VirtualKey.LeftWin)]
    [InlineData(MacKeyCodes.RightCommand, VirtualKey.RightWin)]
    [InlineData(MacKeyCodes.Option, VirtualKey.LeftAlt)]
    [InlineData(MacKeyCodes.Shift, VirtualKey.LeftShift)]
    [InlineData(MacKeyCodes.Space, VirtualKey.Space)]
    [InlineData(MacKeyCodes.V, VirtualKey.V)]
    [InlineData((ushort)0x7A, VirtualKey.F1)]
    public void Maps_mac_key_codes_to_virtual_keys(ushort mac, VirtualKey expected)
    {
        MacKeyCodes.ToVirtualKey(mac).Should().Be(expected);
    }

    [Fact]
    public void Unknown_key_code_maps_to_None()
    {
        MacKeyCodes.ToVirtualKey(0xFF).Should().Be(VirtualKey.None);
    }

    [Fact]
    public void Reverse_mapping_round_trips_every_forward_entry()
    {
        foreach (VirtualKey vk in Enum.GetValues<VirtualKey>())
        {
            if (!MacKeyCodes.TryToMacKeyCode(vk, out var mac))
            {
                continue;
            }
            if (vk is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Alt)
            {
                continue; // non-sided aliases map to the left key by design
            }
            MacKeyCodes.ToVirtualKey(mac).Should().Be(vk, $"{vk} → {mac:X2} must round-trip");
        }
    }

    [Theory]
    [InlineData(MacKeyCodes.Control, 0x1UL)]
    [InlineData(MacKeyCodes.RightControl, 0x2000UL)]
    [InlineData(MacKeyCodes.Command, 0x8UL)]
    [InlineData(MacKeyCodes.RightCommand, 0x10UL)]
    public void Modifier_flag_bits_distinguish_left_from_right(ushort mac, ulong expectedBit)
    {
        MacKeyCodes.TryGetModifierFlagBit(mac, out var bit).Should().BeTrue();
        bit.Should().Be(expectedBit);
    }

    [Fact]
    public void Letters_are_not_modifiers()
    {
        MacKeyCodes.IsModifierKeyCode(MacKeyCodes.V).Should().BeFalse();
    }
}
