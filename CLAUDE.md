# CLAUDE.md

Working instructions for KusPus development. Read me at the start of every session.

## Project

KusPus — a floating, hotkey-driven, on-device speech-to-text utility for Windows. From-scratch C# + WPF + .NET 10 rewrite of the macOS app **WhisprFlow / FloatingRecorder**. Audience: the author + ~10 testers. Not a market product. MIT licensed.

Two pillars: **great local English transcription** (whisper.cpp CPU, no cloud) and **simplicity** (one hotkey, paste anywhere, no UI to manage).

## Source of truth

Read these IN ORDER before writing any code in a new phase:

1. **`docs/PRD.md`** — what we're building and why. Authoritative for scope, non-goals, acceptance criteria. The product contract.
2. **`docs/TECH_SPEC.md`** — how. Prescriptive — every architectural decision lives here (file layout, threading, P/Invoke, naming, dependencies).
3. **`docs/ROADMAP.md`** — what's deferred past v1.0. "Phase 2+", "deferred to ROADMAP", or any v1.1+ heading = **NOT in scope right now.**
4. **`docs/PROCESS.md`** — the gate-driven workflow. Follow it exactly.

**Conflict resolution:** PRD §scope decisions outrank TECH_SPEC §implementation decisions outrank anything else. If you find a contradiction between docs, surface it before picking a side. Never quietly resolve a conflict.

## How I want you to work

**Gate-driven. No exceptions.** Three layers:

| Layer | When | What you run |
|---|---|---|
| **Cluster gate** | After every 1–5 related files of a phase | Compile clean (zero warnings — `TreatWarningsAsErrors` is on), cluster tests green, spec citation, anti-bloat scan, dead-branch scan, comment audit (WHY not WHAT), silent-failure scan, deviation note. Reportable in <100 words. |
| **Phase gate** | Before marking a phase task complete | All cluster gates passed, full `dotnet test` green, **spawn a `general-purpose` subagent** for adversarial diff review against the relevant TECH_SPEC §, build a spec-coverage ledger (every § → file:line), list explicitly deferred items. |
| **Milestone gate** | Phases 6, 9, 10, 12, 13 (user-facing) | I walk a PRD §11.3 manual test (M-01..M-37) on this machine. AI can't self-verify keyboard/audio/paste behavior. |

The full template lives in [`docs/PROCESS.md`](docs/PROCESS.md). Refer to it when running a gate.

**Phase order, every phase:**
1. Read the relevant TECH_SPEC § end-to-end.
2. State the cluster plan up front (clusters defined by testability — group changes around testable behavior).
3. Per cluster: write the failing test first → implement → cluster gate → report.
4. Phase end: phase gate → only then mark the phase task complete.

## Hard rules

- **No code the spec didn't ask for.** Convenience statics, helper getters, "while I'm here" refactors. If TECH_SPEC § doesn't require it, don't add it. This is the #1 cause of AI drift and the #1 thing the gate exists to catch.
- **No defensive code for impossible cases.** No null checks on non-nullables you just constructed. No "what if the OS lies" branches the spec doesn't acknowledge. No try/catch around code that can't throw.
- **No silent failures.** Empty `catch`, swallowed exceptions, `catch (Exception) { /* log and continue */ }` are forbidden. Catch only at real I/O boundaries; return `Result<T>.Fail` with context.
- **No phase-boundary violations.** Phase 5 code does not appear in Phase 1, even "to save a step later." Each phase has explicit exit criteria. Meet them, then move.
- **No WHAT comments.** `// increment counter` for `counter++` is noise. Comments only when the WHY is non-obvious (a constraint, a workaround for a specific bug, a subtle invariant).
- **No commits without my explicit ask.** Staging (`git add`) is fine if it's part of a workflow I asked for; `git commit` requires me literally saying "commit it."
- **No edits to `docs/PRD.md`, `docs/TECH_SPEC.md`, or `docs/ROADMAP.md`** without my explicit ask. They are the contract. I revise them; you implement against them.
- **No destructive git operations** (`push`, `reset --hard`, `--force`, `--no-verify`, `clean -f`, `checkout --`, `branch -D`) without my explicit, in-context permission. Authorization for one such action does not generalize.

## Build & test

```powershell
dotnet build                              # full solution — must be 0 warnings, 0 errors
dotnet test test/KusPus.Core.Tests/       # targeted (Phase 1 — only Core.Tests has the SDK so far)
dotnet test                               # full suite — clean once Phase 2 & 3 add their test SDK refs
dotnet run --project src/KusPus.App       # runs the app (Phase 6+)
```

The solution file is `KusPus.slnx` (.NET 10's default XML solution format from `dotnet new sln`).

## Repo layout

```
KusPus/
├── CLAUDE.md                       this file
├── KusPus.slnx                     solution
├── Directory.Build.props           nullable, warnings-as-errors, analysis-level
├── .editorconfig
├── .gitignore
├── docs/                           PRD + TECH_SPEC + ROADMAP + PROCESS + BUILD + INSTALL + ARCHITECTURE
├── src/
│   ├── KusPus.Core/                pure, headless. FSM + Result + AppSettings.    ✅ Phase 1
│   ├── KusPus.Persistence/         SQLite + FTS5 + settings.json.                 Phase 2
│   ├── KusPus.Whisper/             subprocess + model manager.                    Phase 3
│   ├── KusPus.Audio/               WASAPI capture via NAudio.                     Phase 4
│   ├── KusPus.Native/              P/Invoke + LL hook + paste engine.             Phase 5
│   └── KusPus.App/                 WPF, DI, tray, pill, MainWindow.               Phase 6+
├── test/                           one .Tests project per source project (xunit + FluentAssertions)
├── installer/                                                                     Phase 12
├── tools/                          build-whisper-windows, compute-sha256, verify-egress
├── third_party/                    whisper.cpp submodule will live here           Phase 3
└── .github/workflows/              ci.yml + release.yml                           Phase 12
```

Current phase status lives in the in-session task list (not here, so it doesn't go stale).

## Deviations from spec — running list

When a gate forces a deliberate deviation from TECH_SPEC's literal text, log it here so future sessions know. Each deviation has: where, what, why.

- **`src/KusPus.Core/Result.cs`** — TECH_SPEC §10 shows `Result<T>` with static `Ok`/`Fail` factories. Real impl uses a non-generic `Result` helper class with generic methods (`Result.Ok(42)`, `Result.Fail<T>("msg")`). Reason: satisfies analyzer CA1000 + enables type inference at call sites. Spec snippet is intent-level; this is the idiomatic .NET placement.
- **`Directory.Build.props`** — adds `<NoWarn>CA1707</NoWarn>` for `*.Tests` projects only. Reason: xunit's snake_case test-name convention conflicts with the production-code naming rule. Production code remains under CA1707.
- **`src/KusPus.Persistence/PrefsStore.cs`** — file-level `#pragma warning disable CA1848, CA1873` (LoggerMessage source-gen delegate analyzers). Reason: PrefsStore logs at startup and on rare validation errors only — never on a hot path. LoggerMessage source-gen would add ~30 lines of boilerplate for ~5 log calls with no measurable benefit at this volume. The rules stay active globally so hot-path classes (HotkeyEngine, Coordinator) still get the signal.
- **`src/KusPus.Persistence/HistoryStore.cs`** — same CA1848/CA1873 suppression as PrefsStore, same rationale (logs only on startup backup-rotation and on backup failures).
- **`src/KusPus.Persistence/HistoryStore.cs`** — uses a **single SqliteConnection guarded by SemaphoreSlim** for both reads and writes. TECH_SPEC §17 envisions "writes serialised through the persistence task queue; reads use a separate read-only connection." For v1's expected load (a few writes/minute, a handful of reads/day) the single-connection design is simpler and adequate. WAL is still enabled so external tools can read the DB concurrently. Revisit if Phase 6+ profiling shows lock contention.
- **`src/KusPus.Whisper/IWhisperRunner.cs`** — `TranscribeAsync` takes `ResolvedModel` (descriptor + path) instead of the spec §15's bare `ModelDescriptor`. The §15 signature is missing the on-disk path that the runner needs to invoke whisper.exe; `ResolvedModel` already exists from ModelManager.Resolve and packages both concerns. ModelDescriptor stays the abstract "what this model is" type.
- **`src/KusPus.Whisper/WhisperRunner.cs`** + **`src/KusPus.Whisper/ModelManager.cs`** — same CA1848/CA1873 file-local `#pragma` suppression as PrefsStore/HistoryStore, same rationale (startup + boundary-failure logging only, not hot path).
- **`test/KusPus.Audio.Tests/`** — TECH_SPEC §1 lists only 3 test projects (Core, Persistence, Whisper). Added a 4th for `KusPus.Audio` to cover pure-helper coverage (`ComputeRms`, state-guard early-fail). WASAPI capture itself is manual-tested at Phase 6 per §33; this project hosts only what's testable without a microphone.
- **`src/KusPus.Audio/AudioRecorder.cs`** — same CA1848/CA1873 suppression as the other layers (start/stop/cap logging only).
- **`src/KusPus.Audio/IAudioRecorder.cs`** — exposes `event EventHandler? DefaultDeviceChanged` rather than the spec §14 prose "surface error in pill" (the spec describes a behaviour, not a mechanism). Event lets the Coordinator subscribe in Phase 6 and translate to pill state.
- **`src/KusPus.Audio/AudioRecorder.cs`** — uses `MediaFoundationResampler` rather than the spec §14 example's `WdlResamplingSampleProvider` + `WaveFloatTo16Provider` chain. Output format (16 kHz mono 16-bit PCM) is identical; MF is a single-call wrapper that avoids hand-pushing samples through an `ISampleProvider` pull pipeline. Quality: MF default + ResamplerQuality=60 ≈ WDL high-quality SRC.
- **`src/KusPus.Audio/AudioRecorder.cs`** — RMS is computed on the worker thread over each 1/15s output frame rather than "in the WASAPI callback over 20 chunks per buffer" per §14. User-visible behaviour is identical (20-channel array at 15 Hz). The worker location avoids doing math on the capture thread (NAudio holds a lock that delays the next callback).
- **`src/KusPus.Audio/RecordedFile.cs`** — adds `bool CappedAtLimit = false` so Phase 6 can surface the spec §14 "Recording capped at 50 min" pill message. Optional ctor parameter; pre-existing callers continue to compile.
- **`test/KusPus.Native.Tests/`** — 5th test project (spec §1 lists 3). Pure-helper coverage for `PasteEngine` (terminal-detect, friendly-name, SendInput payload) and `HotkeyEngine.ProcessKey` chord state machine. LL-hook itself and end-to-end paste are Phase 6 manual smoke per §33.
- **`src/KusPus.Native/{HotkeyEngine,PasteEngine,JobObjectContainer}.cs`** — same CA1848/CA1873 file-local pragma as the other layers.
- **`src/KusPus.Native/IHotkeyEngine.cs`** — CA1716 file-local suppression. `Stop()` is a reserved keyword in VB.NET; spec §13 uses it verbatim and KusPus is a single-language WPF app, so following the spec wins.
- **`src/KusPus.Native/HotkeyEngine.cs`** — **hook self-heal watchdog NOT implemented** (spec §13 "Hook self-heal"). The callback runs in &lt; 1 ms by design so `LowLevelHooksTimeout` shouldn't fire; the 30s heartbeat + auto-reinstall is deferred to a follow-up phase. If the hook silently dies in the field we'll see it and add it.
- **`src/KusPus.Native/PasteEngine.cs`** — `IClipboardWriter` injection point. Spec §16 calls `Clipboard.SetText` directly, which requires WPF in the Native project. Decoupling via an interface keeps `KusPus.Native` free of WPF deps; the WPF impl ships in Phase 6's `KusPus.App` composition root.
- **`src/KusPus.Native/JobObjectContainer.cs`** — owns the Job Object handle. Spec §15 says "created in AppCoordinator and injected into WhisperRunner via DI"; in practice the handle lives in this class which implements `IProcessContainer` and is injected as the `WhisperRunner._onProcessStarted` callback. Functionally equivalent.
- **`src/KusPus.Native/HotkeyEngine.cs`** — Spec §13 step 6 prescribes a `Channel<HookEvent>` between the LL callback and the subject. Real impl emits directly to `Subject<HotkeyEvent>` AFTER releasing the state lock, which avoids the deadlock-under-callback risk without the channel allocation/dispatch overhead. At v1's input rate (a handful of events per chord activation) this is well within the `LowLevelHooksTimeout` budget. Revisit if the watchdog observes timeouts.
- **`src/KusPus.Whisper/WhisperRunner.cs`** — empty `expectedWhisperSha256` now skips the integrity check (dev mode). Phase 12 release builds populate the SHA from `installer/payload/whisper/SHA256SUMS`.
- **`src/KusPus.App/`** — Phase 6 milestone code. Several v1-only simplifications, all to be revisited in their planned phases:
  - Pill: plain `Topmost`/click-through/`WS_EX_NOACTIVATE` window with a single status text; no acrylic, no animations, no visualizer (Phase 8). Position math uses `System.Windows.Forms.Screen.FromPoint` (good enough for one-monitor dev; Phase 8 switches to `MonitorFromPoint` + `GetDpiForMonitor` for per-monitor DPI).
  - No MainWindow yet (Phase 9). Tray menu has Toggle + Quit only.
  - Single-instance "bring main to front" broadcast deferred until MainWindow exists (Phase 9). Second launch just exits silently for now.
  - `UseWindowsForms=true` alongside `UseWPF=true` to get `NotifyIcon`-style tray and `Screen.FromPoint`. Triggers ambiguous-`Application` references — App uses `using WpfApplication = System.Windows.Application` and qualifies the partial-class base type.
  - `KUSPUS_WHISPER_DIR` and `KUSPUS_WHISPER_SHA256` env vars override the default `{app}\whisper` path + skip-integrity-check dev mode. Phase 12 installer sets these from real values.
  - Sentry, autostart registry write, onboarding modal — all deferred to Phases 10/11.
  - `CA1001` suppressed on `App` (it owns disposable fields but inherits from `System.Windows.Application` which isn't `IDisposable`; cleanup happens in `OnExit`).

Append to this list (don't replace) when a new deviation lands.

## When in doubt

- Smaller change is better. One cluster at a time.
- The gate is the discipline. Don't skip it.
- Read the spec section again before guessing.
- Ask me. Confirmation is cheap; rework is not.
