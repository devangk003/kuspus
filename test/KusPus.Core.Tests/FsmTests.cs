using FluentAssertions;
using KusPus.Core.State;
using Xunit;

namespace KusPus.Core.Tests;

/// <summary>
/// Row-per-row coverage of the transition table in TECH_SPEC §12. Each [Fact]
/// is named after the transition it asserts so test failures map directly to
/// a row in the spec.
/// </summary>
public class FsmTests
{
    private static readonly FsmConfig Cfg = new(HoldThresholdMs: 250);

    private static readonly CoordinatorSnapshot Idle = new(AppState.Idle);
    private static readonly CoordinatorSnapshot Armed = new(AppState.Armed);
    private static readonly CoordinatorSnapshot Cancelled = new(AppState.Cancelled);
    private static readonly CoordinatorSnapshot RecordingHold = new(AppState.Recording, IsHoldMode: true);
    private static readonly CoordinatorSnapshot RecordingTap = new(AppState.Recording, IsHoldMode: false);
    private static readonly CoordinatorSnapshot Transcribing = new(AppState.Transcribing);

    // ── Idle ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_ChordEngaged_arms_and_starts_hold_timer()
    {
        var t = Fsm.Step(Idle, new ChordEngaged(), Cfg);

        t.Next.Should().Be(Armed);
        t.Effects.Should().Equal(new SideEffect[]
        {
            new CaptureForegroundHwnd(),
            new StartHoldTimer(250),
        });
    }

    [Fact]
    public void Idle_ToggleFromTray_enters_tap_recording()
    {
        var t = Fsm.Step(Idle, new ToggleFromTray(), Cfg);

        t.Next.Should().Be(RecordingTap);
        t.Effects.Should().Equal(new SideEffect[] { new StartAudioCapture() });
    }

    // ── Armed ────────────────────────────────────────────────────────────────

    [Fact]
    public void Armed_ChordReleased_enters_tap_recording()
    {
        var t = Fsm.Step(Armed, new ChordReleased(), Cfg);

        t.Next.Should().Be(RecordingTap);
        t.Effects.Should().Equal(new SideEffect[] { new StartAudioCapture() });
    }

    [Fact]
    public void Armed_HoldThresholdElapsed_enters_hold_recording()
    {
        var t = Fsm.Step(Armed, new HoldThresholdElapsed(), Cfg);

        t.Next.Should().Be(RecordingHold);
        t.Effects.Should().Equal(new SideEffect[] { new StartAudioCapture() });
    }

    [Fact]
    public void Armed_OtherKeyPressed_cancels()
    {
        var t = Fsm.Step(Armed, new OtherKeyPressedWhileArmed(), Cfg);

        t.Next.Should().Be(Cancelled);
        t.Effects.Should().Equal(new SideEffect[] { new CancelHoldTimer() });
    }

    // ── Recording (hold-mode) ────────────────────────────────────────────────

    [Fact]
    public void RecordingHold_ChordReleased_transcribes()
    {
        var t = Fsm.Step(RecordingHold, new ChordReleased(), Cfg);

        t.Next.Should().Be(Transcribing);
        t.Effects.Should().Equal(new SideEffect[]
        {
            new StopAudioCapture(),
            new BeginTranscribe(),
        });
    }

    [Fact]
    public void RecordingHold_OtherKeyPressed_is_noop()
    {
        var t = Fsm.Step(RecordingHold, new OtherKeyPressedWhileArmed(), Cfg);

        t.Next.Should().Be(RecordingHold);
        t.Effects.Should().BeEmpty();
    }

    // ── Recording (tap-mode) ─────────────────────────────────────────────────

    [Fact]
    public void RecordingTap_ChordEngaged_transcribes()
    {
        var t = Fsm.Step(RecordingTap, new ChordEngaged(), Cfg);

        t.Next.Should().Be(Transcribing);
        t.Effects.Should().Equal(new SideEffect[]
        {
            new StopAudioCapture(),
            new BeginTranscribe(),
        });
    }

    [Fact]
    public void RecordingTap_ChordReleased_is_noop()
    {
        var t = Fsm.Step(RecordingTap, new ChordReleased(), Cfg);

        t.Next.Should().Be(RecordingTap);
        t.Effects.Should().BeEmpty();
    }

    [Fact]
    public void RecordingTap_ToggleFromTray_transcribes()
    {
        var t = Fsm.Step(RecordingTap, new ToggleFromTray(), Cfg);

        t.Next.Should().Be(Transcribing);
        t.Effects.Should().Equal(new SideEffect[]
        {
            new StopAudioCapture(),
            new BeginTranscribe(),
        });
    }

    [Fact]
    public void RecordingTap_OtherKeyPressed_is_noop()
    {
        var t = Fsm.Step(RecordingTap, new OtherKeyPressedWhileArmed(), Cfg);

        t.Next.Should().Be(RecordingTap);
        t.Effects.Should().BeEmpty();
    }

    // ── Transcribing ─────────────────────────────────────────────────────────

    [Fact]
    public void Transcribing_TranscribeComplete_delivers_and_returns_to_idle()
    {
        var evt = new TranscribeComplete("hello world", TimeSpan.FromSeconds(3), "ggml-tiny.en");

        var t = Fsm.Step(Transcribing, evt, Cfg);

        t.Next.Should().Be(Idle);
        t.Effects.Should().Equal(new SideEffect[]
        {
            new DeliverTranscript("hello world", TimeSpan.FromSeconds(3), "ggml-tiny.en"),
        });
    }

    [Fact]
    public void Transcribing_TranscribeFailed_handles_failure_and_returns_to_idle()
    {
        var evt = new TranscribeFailed("exit 1", @"C:\tmp\bad.wav");

        var t = Fsm.Step(Transcribing, evt, Cfg);

        t.Next.Should().Be(Idle);
        t.Effects.Should().Equal(new SideEffect[]
        {
            new HandleTranscribeFailure("exit 1", @"C:\tmp\bad.wav"),
        });
    }

    // ── Cancelled ────────────────────────────────────────────────────────────

    [Fact]
    public void Cancelled_ChordReleased_returns_to_idle()
    {
        var t = Fsm.Step(Cancelled, new ChordReleased(), Cfg);

        t.Next.Should().Be(Idle);
        t.Effects.Should().BeEmpty();
    }

    // ── Representative no-op cases for events the spec doesn't enumerate ─────

    [Fact]
    public void Idle_ChordReleased_is_noop()
    {
        var t = Fsm.Step(Idle, new ChordReleased(), Cfg);

        t.Next.Should().Be(Idle);
        t.Effects.Should().BeEmpty();
    }

    [Fact]
    public void Transcribing_ChordEngaged_is_noop()
    {
        var t = Fsm.Step(Transcribing, new ChordEngaged(), Cfg);

        t.Next.Should().Be(Transcribing);
        t.Effects.Should().BeEmpty();
    }

    [Fact]
    public void Armed_ChordEngaged_is_noop()
    {
        var t = Fsm.Step(Armed, new ChordEngaged(), Cfg);

        t.Next.Should().Be(Armed);
        t.Effects.Should().BeEmpty();
    }

    [Fact]
    public void Cancelled_anything_else_stays_cancelled()
    {
        var t = Fsm.Step(Cancelled, new ChordEngaged(), Cfg);

        t.Next.Should().Be(Cancelled);
        t.Effects.Should().BeEmpty();
    }

    [Fact]
    public void Hold_threshold_value_comes_from_config()
    {
        var customCfg = new FsmConfig(HoldThresholdMs: 500);
        var t = Fsm.Step(Idle, new ChordEngaged(), customCfg);

        t.Effects.Should().Contain(new StartHoldTimer(500));
        t.Effects.Should().NotContain(new StartHoldTimer(250));
    }
}
