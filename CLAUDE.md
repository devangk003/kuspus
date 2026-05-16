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

Append to this list (don't replace) when a new deviation lands.

## When in doubt

- Smaller change is better. One cluster at a time.
- The gate is the discipline. Don't skip it.
- Read the spec section again before guessing.
- Ask me. Confirmation is cheap; rework is not.
