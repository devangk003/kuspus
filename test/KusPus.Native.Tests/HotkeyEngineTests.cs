using FluentAssertions;
using KusPus.Core.Hotkeys;
using Xunit;

namespace KusPus.Native.Tests;

/// <summary>
/// Tests <see cref="HotkeyEngine.ProcessKey"/> in isolation — the chord state machine
/// that decides when to emit ChordEngaged / ChordReleased / OtherKeyPressed and when
/// to consume the LWin keyup. Running the actual WH_KEYBOARD_LL hook is a Phase 6
/// manual smoke (PRD §11.3 M-04, M-13).
/// </summary>
public class HotkeyEngineTests
{
    private static (HotkeyEngine engine, List<HotkeyEvent> received) NewEngine()
    {
        var engine = new HotkeyEngine();
        var received = new List<HotkeyEvent>();
        engine.Events.Subscribe(received.Add);
        return (engine, received);
    }

    [Fact]
    public void LeftCtrl_then_LeftWin_emits_ChordEngaged()
    {
        var (engine, received) = NewEngine();

        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: true);

        received.Should().ContainSingle().Which.Should().BeOfType<ChordEngaged>();
    }

    [Fact]
    public void Releasing_a_modifier_after_engage_emits_ChordReleased()
    {
        var (engine, received) = NewEngine();
        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: true);

        engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: false);

        received.Should().HaveCount(2);
        received[0].Should().BeOfType<ChordEngaged>();
        received[1].Should().BeOfType<ChordReleased>();
    }

    [Fact]
    public void Releasing_LWin_after_engage_consumes_the_keyup()
    {
        var engine = new HotkeyEngine();
        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: true);

        bool consumed = engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: false);

        consumed.Should().BeTrue(because: "LWin-suppression must consume the keyup");
    }

    [Fact]
    public void Releasing_LCtrl_after_engage_does_not_consume()
    {
        var engine = new HotkeyEngine();
        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: true);

        bool consumed = engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: false);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void Pressing_another_key_while_engaged_emits_OtherKeyPressed()
    {
        var (engine, received) = NewEngine();
        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: true);

        engine.ProcessKey(VirtualKey.A, isKeyDown: true);

        received.Last().Should().BeOfType<OtherKeyPressed>();
    }

    [Fact]
    public void Half_a_chord_does_not_emit_engaged()
    {
        var (_, received) = NewEngine();
        var engine = new HotkeyEngine();
        engine.Events.Subscribe(received.Add);

        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);

        received.Should().BeEmpty();
    }

    [Fact]
    public void Modifier_plus_key_chord_engages_when_key_pressed_with_all_modifiers_held()
    {
        var engine = new HotkeyEngine();
        engine.SetChord(new HotkeyChord(
            Modifiers: [VirtualKey.LeftCtrl, VirtualKey.LeftShift],
            Key: VirtualKey.Space));
        var received = new List<HotkeyEvent>();
        engine.Events.Subscribe(received.Add);

        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftShift, isKeyDown: true);
        engine.ProcessKey(VirtualKey.Space, isKeyDown: true);

        received.Should().ContainSingle().Which.Should().BeOfType<ChordEngaged>();
    }

    [Fact]
    public void Modifier_plus_key_chord_disengages_on_key_up()
    {
        var engine = new HotkeyEngine();
        engine.SetChord(new HotkeyChord(
            Modifiers: [VirtualKey.LeftCtrl],
            Key: VirtualKey.Space));
        var received = new List<HotkeyEvent>();
        engine.Events.Subscribe(received.Add);

        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.Space, isKeyDown: true);
        engine.ProcessKey(VirtualKey.Space, isKeyDown: false);

        received.Should().HaveCount(2);
        received[1].Should().BeOfType<ChordReleased>();
    }

    [Fact]
    public void Modifier_plus_key_chord_does_not_engage_with_only_modifiers_held()
    {
        var engine = new HotkeyEngine();
        engine.SetChord(new HotkeyChord(
            Modifiers: [VirtualKey.LeftCtrl, VirtualKey.LeftShift],
            Key: VirtualKey.Space));
        var received = new List<HotkeyEvent>();
        engine.Events.Subscribe(received.Add);

        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftShift, isKeyDown: true);
        // Space NOT pressed.

        received.Should().BeEmpty();
    }

    [Fact]
    public void SetChord_resets_held_modifier_state()
    {
        var (engine, received) = NewEngine();
        engine.ProcessKey(VirtualKey.LeftCtrl, isKeyDown: true);
        engine.ProcessKey(VirtualKey.LeftWin, isKeyDown: true);
        received.Clear();

        // Switch to a different chord — the old modifier state should not produce events.
        engine.SetChord(new HotkeyChord([VirtualKey.LeftAlt], Key: null));

        // Now pressing only LeftAlt should engage the new chord.
        engine.ProcessKey(VirtualKey.LeftAlt, isKeyDown: true);

        received.Should().ContainSingle().Which.Should().BeOfType<ChordEngaged>();
    }
}
