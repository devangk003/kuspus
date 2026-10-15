using FluentAssertions;
using KusPus.Core.Hotkeys;
using KusPus.Core.Platform;
using Xunit;

namespace KusPus.Mac.Platform.Tests;

/// <summary>
/// Exercises the engine's key-processing path without installing a CGEventTap
/// (that needs Input Monitoring and a real session). Mirrors HotkeyEngineTests on Windows.
/// </summary>
public class MacHotkeyEngineTests
{
    private static (MacHotkeyEngine engine, List<HotkeyEvent> received) NewEngine()
    {
        var engine = new MacHotkeyEngine();
        var received = new List<HotkeyEvent>();
        engine.Events.Subscribe(received.Add);
        return (engine, received);
    }

    [Fact]
    public void Ctrl_plus_Cmd_engages_and_releases()
    {
        var (engine, received) = NewEngine();

        engine.ProcessKey(VirtualKey.LeftCtrl, true);
        engine.ProcessKey(VirtualKey.LeftWin, true);
        engine.ProcessKey(VirtualKey.LeftWin, false);

        received.Should().HaveCount(2);
        received[0].Should().BeOfType<ChordEngaged>();
        received[1].Should().BeOfType<ChordReleased>();
    }

    [Fact]
    public void SetChord_switches_to_new_chord()
    {
        var (engine, received) = NewEngine();
        engine.SetChord(new HotkeyChord([VirtualKey.LeftAlt], VirtualKey.Space));

        engine.ProcessKey(VirtualKey.LeftCtrl, true);
        engine.ProcessKey(VirtualKey.LeftWin, true);
        received.Should().BeEmpty();

        engine.ProcessKey(VirtualKey.LeftAlt, true);
        engine.ProcessKey(VirtualKey.Space, true);
        received.Should().ContainSingle().Which.Should().BeOfType<ChordEngaged>();
    }

    [Fact]
    public void Dispose_without_Start_is_safe()
    {
        var (engine, _) = NewEngine();
        engine.Dispose();
    }
}
