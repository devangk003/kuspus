namespace KusPus.Core.State;

/// <summary>
/// The pure, headless transition function for KusPus. Implements the table in
/// TECH_SPEC §12. All I/O (audio, whisper, paste, history) is requested via
/// <see cref="SideEffect"/> records that the Coordinator interprets at runtime.
///
/// Pill visibility and content are NOT emitted as side effects — the pill VM binds
/// to <see cref="CoordinatorSnapshot"/> via the Coordinator's observable and derives
/// its own state. See TECH_SPEC §12 "Implementation" ("emits IObservable&lt;AppState&gt;
/// for the pill view-model to bind to").
/// </summary>
public static class Fsm
{
    public static Transition Step(CoordinatorSnapshot snapshot, CoordinatorEvent evt, FsmConfig config)
    {
        return (snapshot.State, snapshot.IsHoldMode, evt) switch
        {
            // ── Idle ─────────────────────────────────────────────────────────
            (AppState.Idle, _, ChordEngaged _) =>
                Move(snapshot with { State = AppState.Armed },
                    new CaptureForegroundHwnd(),
                    new StartHoldTimer(config.HoldThresholdMs)),

            (AppState.Idle, _, ToggleFromTray _) =>
                Move(snapshot with { State = AppState.Recording, IsHoldMode = false },
                    new StartAudioCapture()),

            // ── Armed ────────────────────────────────────────────────────────
            (AppState.Armed, _, ChordReleased _) =>
                Move(snapshot with { State = AppState.Recording, IsHoldMode = false },
                    new StartAudioCapture()),

            (AppState.Armed, _, HoldThresholdElapsed _) =>
                Move(snapshot with { State = AppState.Recording, IsHoldMode = true },
                    new StartAudioCapture()),

            (AppState.Armed, _, OtherKeyPressedWhileArmed _) =>
                Move(snapshot with { State = AppState.Cancelled },
                    new CancelHoldTimer()),

            // ── Recording (hold-mode) ────────────────────────────────────────
            (AppState.Recording, true, ChordReleased _) =>
                Move(snapshot with { State = AppState.Transcribing, IsHoldMode = false },
                    new StopAudioCapture(),
                    new BeginTranscribe()),

            // ── Recording (tap-mode) ─────────────────────────────────────────
            (AppState.Recording, false, ChordEngaged _) =>
                Move(snapshot with { State = AppState.Transcribing, IsHoldMode = false },
                    new StopAudioCapture(),
                    new BeginTranscribe()),

            (AppState.Recording, false, ToggleFromTray _) =>
                Move(snapshot with { State = AppState.Transcribing, IsHoldMode = false },
                    new StopAudioCapture(),
                    new BeginTranscribe()),

            // ── Transcribing ─────────────────────────────────────────────────
            // DeliverTranscript / HandleTranscribeFailure are deliberately coarse-grained:
            // the spec §12 rows list "set clipboard; SetForegroundWindow + SendInput;
            // record history; show in-pill paste confirmation; hide pill" as the success
            // path. The FSM can't represent any of those as deterministic side effects
            // because they depend on the captured HWND, runtime paste outcome, and target
            // app name resolved during delivery. The Coordinator unpacks one of these
            // records into the full choreography in Phase 6.
            (AppState.Transcribing, _, TranscribeComplete tc) =>
                Move(snapshot with { State = AppState.Idle, IsHoldMode = false },
                    new DeliverTranscript(tc.Text, tc.Duration, tc.Model)),

            (AppState.Transcribing, _, TranscribeFailed tf) =>
                Move(snapshot with { State = AppState.Idle, IsHoldMode = false },
                    new HandleTranscribeFailure(tf.Error, tf.FailedWavPath)),

            // ── Cancelled ────────────────────────────────────────────────────
            (AppState.Cancelled, _, ChordReleased _) =>
                Move(snapshot with { State = AppState.Idle }),

            // Any (state, event) pair not in the table is a no-op. Covers sticky-key
            // re-fires and late events arriving after a state change.
            _ => NoOp(snapshot),
        };
    }

    private static Transition Move(CoordinatorSnapshot next, params SideEffect[] effects)
        => new(next, effects);

    private static Transition NoOp(CoordinatorSnapshot snapshot)
        => new(snapshot, []);
}

public sealed record Transition(CoordinatorSnapshot Next, IReadOnlyList<SideEffect> Effects);
