using FluentAssertions;
using KusPus.Core.Hotkeys;
using KusPus.Core.Platform;
using Xunit;

namespace KusPus.Core.Tests.Hotkeys;

public class ChordTrackerTests
{
    [Fact]
    public void Default_chord_engages_on_LeftCtrl_plus_LeftWin()
    {
        var t = new ChordTracker();

        t.Step(VirtualKey.LeftCtrl, true).Should().BeEmpty();
        t.Step(VirtualKey.LeftWin, true).Should().ContainSingle().Which.Should().BeOfType<ChordEngaged>();
        t.IsEngaged.Should().BeTrue();
    }

    [Fact]
    public void Releasing_any_modifier_releases_the_chord_once()
    {
        var t = new ChordTracker();
        t.Step(VirtualKey.LeftCtrl, true);
        t.Step(VirtualKey.LeftWin, true);

        t.Step(VirtualKey.LeftWin, false).Should().ContainSingle().Which.Should().BeOfType<ChordReleased>();
        t.Step(VirtualKey.LeftCtrl, false).Should().BeEmpty();
        t.IsEngaged.Should().BeFalse();
    }

    [Fact]
    public void Other_key_while_engaged_emits_OtherKeyPressed_on_down_only()
    {
        var t = new ChordTracker();
        t.Step(VirtualKey.LeftCtrl, true);
        t.Step(VirtualKey.LeftWin, true);

        t.Step(VirtualKey.A, true).Should().ContainSingle().Which.Should().BeOfType<OtherKeyPressed>();
        t.Step(VirtualKey.A, false).Should().BeEmpty();
    }

    [Fact]
    public void Chord_with_a_key_needs_the_key_too()
    {
        var t = new ChordTracker(new HotkeyChord([VirtualKey.LeftAlt], VirtualKey.Space));

        t.Step(VirtualKey.LeftAlt, true).Should().BeEmpty();
        t.Step(VirtualKey.Space, true).Should().ContainSingle().Which.Should().BeOfType<ChordEngaged>();
        t.Step(VirtualKey.Space, false).Should().ContainSingle().Which.Should().BeOfType<ChordReleased>();
    }

    [Fact]
    public void Autorepeat_of_a_held_modifier_is_a_noop()
    {
        var t = new ChordTracker();
        t.Step(VirtualKey.LeftCtrl, true);
        t.Step(VirtualKey.LeftWin, true);

        t.Step(VirtualKey.LeftWin, true).Should().BeEmpty();
        t.Step(VirtualKey.LeftCtrl, true).Should().BeEmpty();
    }

    [Fact]
    public void SetChord_with_same_chord_preserves_state()
    {
        var t = new ChordTracker();
        t.Step(VirtualKey.LeftCtrl, true);
        t.Step(VirtualKey.LeftWin, true);

        t.SetChord(new HotkeyChord([VirtualKey.LeftWin, VirtualKey.LeftCtrl], null)).Should().BeFalse();
        t.IsEngaged.Should().BeTrue();
    }

    [Fact]
    public void SetChord_with_new_chord_resets_state()
    {
        var t = new ChordTracker();
        t.Step(VirtualKey.LeftCtrl, true);
        t.Step(VirtualKey.LeftWin, true);

        t.SetChord(new HotkeyChord([VirtualKey.LeftAlt], null)).Should().BeTrue();
        t.IsEngaged.Should().BeFalse();
        t.Step(VirtualKey.LeftAlt, true).Should().ContainSingle().Which.Should().BeOfType<ChordEngaged>();
    }

    [Fact]
    public void Empty_modifier_list_never_engages()
    {
        var t = new ChordTracker(new HotkeyChord([], VirtualKey.F5));
        t.Step(VirtualKey.F5, true).Should().BeEmpty();
    }
}
