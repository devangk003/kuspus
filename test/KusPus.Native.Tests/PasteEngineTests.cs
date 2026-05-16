using FluentAssertions;
using Xunit;

namespace KusPus.Native.Tests;

/// <summary>
/// Pure-helper tests for <see cref="PasteEngine"/>. End-to-end paste (clipboard,
/// foreground restore, SendInput) is exercised at the Phase 6 milestone smoke
/// (PRD §11.3 M-06..M-12).
/// </summary>
public class PasteEngineTests
{
    [Theory]
    [InlineData("WindowsTerminal.exe", true)]
    [InlineData("cmd.exe", true)]
    [InlineData("powershell.exe", true)]
    [InlineData("pwsh.exe", true)]
    [InlineData("alacritty.exe", true)]
    [InlineData("wezterm-gui.exe", true)]
    [InlineData("WINDOWSTERMINAL.exe", true)]   // case insensitive
    [InlineData("notepad.exe", false)]
    [InlineData("slack.exe", false)]
    [InlineData("chrome.exe", false)]
    [InlineData("mintty.exe", false)]            // deliberately excluded per §16
    [InlineData("wsl.exe", false)]               // deliberately excluded per §16
    public void IsTerminal_matches_section_16_list(string processFileName, bool expected)
    {
        PasteEngine.IsTerminal(processFileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("slack.exe", "Slack")]
    [InlineData("discord.exe", "Discord")]
    [InlineData("chrome.exe", "Chrome")]
    [InlineData("Code.exe", "VS Code")]
    [InlineData("CODE.EXE", "VS Code")]
    [InlineData("notepad.exe", "Notepad")]
    [InlineData("powershell.exe", "PowerShell")]
    [InlineData("pwsh.exe", "PowerShell")]
    public void ResolveFriendlyName_uses_static_map_for_known_apps(string processFileName, string expected)
    {
        PasteEngine.ResolveFriendlyName(processFileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("UnknownApp.exe", "UnknownApp")]
    [InlineData("MyTool.exe", "MyTool")]
    public void ResolveFriendlyName_falls_back_to_filename_without_extension(string processFileName, string expected)
    {
        PasteEngine.ResolveFriendlyName(processFileName).Should().Be(expected);
    }

    [Fact]
    public void BuildKeySequence_for_Ctrl_V_emits_down_down_up_up()
    {
        const ushort vkControl = 0x11;
        const ushort vkV = 0x56;

        var inputs = PasteEngine.BuildKeySequence(vkControl, vkV);

        inputs.Should().HaveCount(4);
        // First two are key-down (Flags == 0)
        inputs[0].Data.Keyboard.VirtualKey.Should().Be(vkControl);
        inputs[0].Data.Keyboard.Flags.Should().Be(0u);
        inputs[1].Data.Keyboard.VirtualKey.Should().Be(vkV);
        inputs[1].Data.Keyboard.Flags.Should().Be(0u);
        // Then key-ups in reverse order (so modifiers are released last)
        inputs[2].Data.Keyboard.VirtualKey.Should().Be(vkV);
        inputs[2].Data.Keyboard.Flags.Should().Be(KusPus.Native.PInvoke.NativeConstants.KEYEVENTF_KEYUP);
        inputs[3].Data.Keyboard.VirtualKey.Should().Be(vkControl);
        inputs[3].Data.Keyboard.Flags.Should().Be(KusPus.Native.PInvoke.NativeConstants.KEYEVENTF_KEYUP);
    }

    [Fact]
    public void BuildKeySequence_for_Ctrl_Shift_V_emits_six_events()
    {
        const ushort vkControl = 0x11;
        const ushort vkLeftShift = 0xA0;
        const ushort vkV = 0x56;

        var inputs = PasteEngine.BuildKeySequence(vkControl, vkLeftShift, vkV);

        inputs.Should().HaveCount(6);
        // 3 downs in given order, 3 ups in reverse order
        inputs[0].Data.Keyboard.VirtualKey.Should().Be(vkControl);
        inputs[1].Data.Keyboard.VirtualKey.Should().Be(vkLeftShift);
        inputs[2].Data.Keyboard.VirtualKey.Should().Be(vkV);
        inputs[3].Data.Keyboard.VirtualKey.Should().Be(vkV);
        inputs[4].Data.Keyboard.VirtualKey.Should().Be(vkLeftShift);
        inputs[5].Data.Keyboard.VirtualKey.Should().Be(vkControl);
    }
}
