# CLAUDE.md

Working instructions for KusPus development. Read me at the start of every session.

## Project

KusPus — a floating, hotkey-driven, on-device speech-to-text utility for Windows. From-scratch C# + WPF + .NET 10 rewrite of the macOS app **WhisprFlow / FloatingRecorder**. Audience: the author + ~10 testers. Not a market product. MIT licensed.

Two pillars: **great local English transcription** (whisper.cpp CPU, no cloud) and **simplicity** (one hotkey, paste anywhere, no UI to manage).

## Source of truth

Read these IN ORDER before writing any code in a new phase:

1. **`docs/PRD.md`** — what we're building and why. Authoritative for scope, non-goals, acceptance criteria. The product contract.
2. **`docs/TECH_SPEC.md`** — how. Prescriptive — every architectural decision lives here (file layout, threading, P/Invoke, naming, dependencies).
3. **`docs/APP_DESIGN.md`** — full visual + interaction spec for every user-facing surface (pill, MainWindow, onboarding, tray). Tokens, components, layouts. Use it for Phase 9+ UI.
4. **`docs/PILL_DESIGN.md`** — pill-specific spec, including the **§10 hover-extend override** (close + settings buttons on hover). Where it conflicts with APP_DESIGN §2 (click-through), **PILL_DESIGN §10 wins** — user-confirmed override.
5. **`docs/ROADMAP.md`** — what's deferred past v1.0. "Phase 2+", "deferred to ROADMAP", or any v1.1+ heading = **NOT in scope right now.**
6. **`docs/PROCESS.md`** — the gate-driven workflow. Follow it exactly.

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
- **`src/KusPus.Audio/AudioRecorder.cs` (REVERTED 2026-05-16)** — Phase 4 originally used `MediaFoundationResampler` instead of the spec §14 `WdlResamplingSampleProvider` chain. Phase 6 manual smoke uncovered why MF was the wrong choice: it pads the output buffer with zeros when the source has no fresh data, so the worker writes target-format bytes at SSD speed (~500× realtime) until the disk fills. Reverted to the spec-prescribed `ToSampleProvider → ToMono → WdlResamplingSampleProvider(16k) → SampleToWaveProvider16` pull-based chain — WDL returns 0 when source is empty, so the worker correctly stalls.
- **`src/KusPus.App/FloatingPillWindow.xaml.cs`** — **PRD G4 deviation (user-requested, ongoing):** pill stays visible between dictations, showing an "Idle" content state (faint SVG icon + "KusPus" label). PRD G4 + APP_DESIGN §2.4 say no idle pill — user explicitly wants always-visible until the Settings modal exposes the close path through the tray. Will revert to the spec'd hidden-when-not-in-use behaviour once Phase 9's tray "Preferences…" / "Quit" items make discoverability acceptable.
- **`src/KusPus.App/FloatingPillWindow.xaml{,.cs}` — APP_DESIGN §2 / PILL_DESIGN.md fully implemented in Phase 8.** 200×56 Mica surface, 8 px DWM rounded corners, §3.1 dark gradient, §3.3 hairline border + drop shadow + inner highlight, five-state machine (Hidden/Recording/Transcribing/Confirmed/Error), §4.2 damped-target visualizer (20 bars × 3 px × 4 px gap, real audio via `IAudioRecorder.Levels`), 14 px ¾-arc spinner, 136 × 1.5 mint accent line with per-state opacity, 120 / 150 ms motion. `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` for focus-proof + hidden-from-shell. Per-monitor DPI math via `MonitorFromWindow` + `GetDpiForMonitor`.
- **`src/KusPus.App/FloatingPillWindow.xaml{,.cs}` — `PILL_DESIGN.md §10` hover-extend override (user-requested):** pill width animates 200 → 280 on hover, revealing Settings (placeholder) + Close buttons. `WS_EX_TRANSPARENT` therefore NOT applied (overrides APP_DESIGN §2.1 / §8.3 click-through). Drag-anywhere-except-buttons via `Window.DragMove` with `VisualTreeHelper`-based button-source detection.
- **Draggable pill + hybrid-sticky multi-monitor (beyond spec, user-requested):** session-only `Dictionary<deviceName, Point>` keyed by `MONITORINFOEX.szDevice` remembers per-monitor positions. On `Armed`/`Recording` transitions, `EnsurePillOnForegroundMonitor` jumps the pill to the focused window's monitor (at remembered or default position). Dictionary cleared every fresh process start per user spec.
- **`src/KusPus.App/AppCoordinator.cs` + `src/KusPus.Core/State/CoordinatorSnapshot.cs`** — added `PostPasteInfo (Pasted, TargetApp, ErrorReason)` on the snapshot. `AppCoordinator.EmitPostPasteSnapshot` fires one extra snapshot from `DeliverAsync` / `HandleFailureAsync` so the pill can render Confirmed (1 s) or Error (2 s) per APP_DESIGN §2.4.
- **`src/KusPus.App/KusPus.App.csproj`** — adds `SharpVectors.Wpf 1.8.5` so the pill Idle state can load `icons/icon.svg` via `<svgc:SvgViewbox>` rather than hand-converted XAML. Same SVG file is the single source for the tray, taskbar, Task Manager, and .exe icon (via `tools/IconBuilder` → `icons/icon.ico`). SVG `viewBox` tightened to `272 246 480 480` so the 5-bar content fills ~94 % of every rendered frame instead of ~44 %.
- **`src/KusPus.Audio/AudioRecorder.cs`** — RMS is computed on the worker thread over each 1/15s output frame rather than "in the WASAPI callback over 20 chunks per buffer" per §14. User-visible behaviour is identical (20-channel array at 15 Hz). The worker location avoids doing math on the capture thread (NAudio holds a lock that delays the next callback).
- **`src/KusPus.Audio/RecordedFile.cs`** — adds `bool CappedAtLimit = false` so Phase 6 can surface the spec §14 "Recording capped at 50 min" pill message. Optional ctor parameter; pre-existing callers continue to compile.
- **`test/KusPus.Native.Tests/`** — 5th test project (spec §1 lists 3). Pure-helper coverage for `PasteEngine` (terminal-detect, friendly-name, SendInput payload) and `HotkeyEngine.ProcessKey` chord state machine. LL-hook itself and end-to-end paste are Phase 6 manual smoke per §33.
- **`src/KusPus.Native/{HotkeyEngine,PasteEngine,JobObjectContainer}.cs`** — same CA1848/CA1873 file-local pragma as the other layers.
- **`src/KusPus.Native/IHotkeyEngine.cs`** — CA1716 file-local suppression. `Stop()` is a reserved keyword in VB.NET; spec §13 uses it verbatim and KusPus is a single-language WPF app, so following the spec wins.
- **`src/KusPus.Native/HotkeyEngine.cs`** — **hook self-heal watchdog NOT implemented** (spec §13 "Hook self-heal"). The callback runs in &lt; 1 ms by design so `LowLevelHooksTimeout` shouldn't fire; the 30s heartbeat + auto-reinstall is deferred to a follow-up phase. If the hook silently dies in the field we'll see it and add it.
- **`src/KusPus.Native/PasteEngine.cs`** — `IClipboardWriter` injection point. Spec §16 calls `Clipboard.SetText` directly, which requires WPF in the Native project. Decoupling via an interface keeps `KusPus.Native` free of WPF deps; the WPF impl ships in Phase 6's `KusPus.App` composition root.
- **`src/KusPus.Native/JobObjectContainer.cs`** — owns the Job Object handle. Spec §15 says "created in AppCoordinator and injected into WhisperRunner via DI"; in practice the handle lives in this class which implements `IProcessContainer` and is injected as the `WhisperRunner._onProcessStarted` callback. Functionally equivalent.
- **`src/KusPus.Native/HotkeyEngine.cs`** — Spec §13 step 6 prescribes a `Channel<HookEvent>` between the LL callback and the subject. Real impl emits directly to `Subject<HotkeyEvent>` AFTER releasing the state lock, which avoids the deadlock-under-callback risk without the channel allocation/dispatch overhead. At v1's input rate (a handful of events per chord activation) this is well within the `LowLevelHooksTimeout` budget. Revisit if the watchdog observes timeouts.
- **`src/KusPus.Native/HotkeyEngine.cs` — LWin keyup is NOT consumed** (spec §13 says it should be; that turned out to be a bug). The Ctrl-tap injection still runs (AHK `#MenuMaskKey` idiom — masks the Start menu) but the real LWin keyup is allowed to reach the OS so its internal "Win is held" state clears. Consuming the keyup left Win stuck-down — every later `SendInput(Ctrl+V)` from PasteEngine read as `Win+Ctrl+V` (opens Action Center / Quick Settings) and every later keystroke became a `Win+key` system shortcut. Validated by AutoHotkey community docs + PowerToys issue #35345 / #18175. **Spec §13 needs an update** when the user is reviewing — flagged in conversation, not edited here per CLAUDE.md rule.
- **`src/KusPus.Whisper/WhisperRunner.cs`** — empty `expectedWhisperSha256` now skips the integrity check (dev mode). Phase 12 release builds populate the SHA from `installer/payload/whisper/SHA256SUMS`.
- **`src/KusPus.App/`** — Phase 6 milestone code. Several v1-only simplifications:
  - Pill: built out in Phase 8 per `docs/PILL_DESIGN.md` + APP_DESIGN §2. The Phase 6 plain-Topmost/text-only pill is gone; see the Phase 8 entries above for the current state.
  - MainWindow under construction (Phase 9 — starting now). Tray menu has Toggle + Quit until 9A adds Preferences / History items.
  - Single-instance "bring main to front" broadcast still deferred until the WndProc message handler lands with MainWindow. Second launch exits silently for now.
  - `UseWindowsForms=true` alongside `UseWPF=true` to get `NotifyIcon`-style tray and `Screen.FromPoint`. Triggers ambiguous-`Application` / `Button` / `Rectangle` / `Color` / `Point` references — App uses local aliases (e.g. `WpfRectangle`, `WpfPoint`) and fully qualifies where needed.
  - `KUSPUS_WHISPER_DIR` and `KUSPUS_WHISPER_SHA256` env vars override the default `{app}\whisper` path + skip-integrity-check dev mode. Phase 12 installer sets these from real values.
  - Sentry, autostart registry write, onboarding modal — deferred to Phases 10/11; autostart toggle UI lands in Phase 9 General tab.
  - `CA1001` suppressed on `App` (it owns disposable fields but inherits from `System.Windows.Application` which isn't `IDisposable`; cleanup happens in `OnExit`).
- **`src/KusPus.Core/Networking/EgressPolicy.cs` + `src/KusPus.App/EgressAllowlistHandler.cs` (Phase 11)** — Sentry's regional ingest hosts (`o<org>.ingest.de.sentry.io`, `o<org>.ingest.us.sentry.io`) are accepted in addition to the spec-literal `ingest.sentry.io`. Recognised by "host ends in `.sentry.io` AND contains an `ingest` label" — covers every documented region without enumerating them. **PRD §10.2 needs an update** to mention regional ingest; flagged here per CLAUDE.md rule. Driver: the user-supplied EU DSN.
- **`src/KusPus.App/CrashReporter.cs` (Phase 11)** — embeds the project's EU Sentry DSN as `internal const string DefaultDsn` with `KUSPUS_SENTRY_DSN` env-var override. Spec §19 says "`<dsn from build constant>`"; const-in-source is functionally equivalent for the friends-only audience and avoids a MSBuild property indirection. DSNs are not secrets per Sentry's own docs.
- **`src/KusPus.App/CrashReporter.cs` (Phase 11)** — Sentry's own HTTP transport is routed through `EgressAllowlistHandler` via `SentryOptions.CreateHttpMessageHandler`. Without this, PRD §10.2's "All HttpClient instances in the codebase route through a single factory" promise would be unmet for the Sentry SDK. Belt-and-suspenders with `ShutdownSdk()` on Offline-Mode flips so an in-flight Sentry upload also gets blocked.
- **`src/KusPus.App/CrashReporter.cs` (Phase 11)** — breadcrumb scrubbing runs in `SetBeforeBreadcrumb`, not inside `BeforeSend`. Sentry 5.0 exposes `SentryEvent.Breadcrumbs` as `IReadOnlyCollection<Breadcrumb>`, so mutation from inside `BeforeSend` is impossible. Scrubbing at add-time is the supported hook and matches the spec intent.
- **`src/KusPus.Core/Telemetry/CrashScrubber.cs` (Phase 11)** — env-var ordering is `TEMP → LOCALAPPDATA → APPDATA → USERPROFILE`. On Windows, `%TEMP%` is `%LOCALAPPDATA%\Temp` so the more-specific prefix must win, otherwise `%TEMP%`-rooted paths get prefixed as `%LOCALAPPDATA%\Temp\…` and lose the most-specific signal. Same logic for LOCALAPPDATA/APPDATA before USERPROFILE.
- **`src/KusPus.Core/Telemetry/CrashScrubber.cs` (Phase 11)** — `ScrubString` does mid-string replacement (free-form text where the path is embedded in a sentence), `ScrubPath` is start-anchored (fields whose value IS a path — stack-frame `AbsolutePath`, `FileName`). Two functions because mid-string substring replacement would corrupt a stack frame's relative-path field that legitimately contains `\Temp\` as a directory component.
- **`tools/verify-egress-allowlist.ps1` — NOT implemented in Phase 11.** Phase 11 enforces the allowlist at runtime via `EgressAllowlistHandler`; the static-analysis pre-commit script is a belt-and-suspenders dev-loop tool the spec calls out. Deferred to a follow-up; the existing stub still throws on invocation.
- **`KusPus.App.Tests` project — NOT added in Phase 11.** `BeforeSend` (internal static in `CrashReporter`) has no direct unit tests; coverage comes via `CrashScrubber` tests + manual smoke. Adding a 6th test project for one internal-static is more scaffolding than value at v1's audience size.

### Dogfood-driven UX deviations (2026-05-17 session)

- **`src/KusPus.App/FloatingPillWindow.xaml{,.cs}` — Pin is now a "compact-mode + position lock" toggle, not just "latch open"** (user-requested). Pin click: contract pill to 200×56 + slide dock back + keep pin button always visible at angle=0 (mint-tinted). While pinned, hover only swaps SVG↔visualizer content (no resize, no dock); position drag is disabled. `CompactRecordButton` (top-LEFT corner, 18×18 + Radius=4 matching the right cluster) is the persistent record affordance in pinned mode. Cursor flips `SizeAll`↔`Arrow` on pin to telegraph the lock. APP_DESIGN §2.4 / PILL_DESIGN §10 originally framed pin as "latch open" — flagged for spec update.
- **`src/KusPus.App/FloatingPillWindow.xaml{,.cs}` — Record-toggle nudge popup** (10s timer, sole dismissal path). Earlier 6s timer was a red herring: a `TransitionTo`'s "dismiss-on-Recording" rule fired within ~ms of click since the FSM moves to Recording immediately after `ToggleFromTray`. That rule is now removed — comment in `Render()` explains. Nudge says "Click into your text field" so the user knows the paste goes wherever focus is at transcribe-end.
- **`src/KusPus.App/FloatingPillWindow.xaml.cs` — `SetRecordToggleAction(Action)`** added. App.OnStartup wires `_coordinator.ToggleFromTray`. Per user spec the toggle does NOT auto-capture a foreground HWND — paste lands wherever focus is at transcribe-end. Tap-mode label is **"Toggle Recording [BETA]"** on both the dock button and tray menu.
- **`src/KusPus.App/TrayManager.cs` + `src/KusPus.App/TrayMenuWindow.xaml{,.cs}` — Custom WPF tray right-click menu** replaces the WinForms `ContextMenuStrip`. Matches user-supplied `Tray_light.png`/`Tray_dark.png`: rounded `Surface` card + drop shadow + design-system tokens. KusPus header with state-aware "Version 1.0.0 · {Idle|Recording|Transcribing}" subtitle, "Toggle Recording [BETA]" with hotkey keycap (live-bound to PrefsStore.Hotkey), "Active model: {name}" with chevron (opens models tab), Preferences, History, red Quit. Shows at cursor on right-click; closes on `Deactivated`. `WS_EX_TOOLWINDOW` hides from Alt-Tab/taskbar.
- **`icons/icon-{idle,recording,error}.{svg,ico}` + `src/KusPus.App/TrayManager.cs` — State-aware tray icons.** Recording overlays a red dot + glow on the bars; Error overlays a red warning triangle. `tools/IconBuilder` generates the multi-resolution .ico files from each SVG. `TrayManager` subscribes to `AppCoordinator.State` and swaps `NotifyIcon.Icon` based on FSM state (treating a failed `PostPaste` snapshot as Error for its hold duration).
- **`src/KusPus.Core/Settings/AppSettings.cs` — Default `UiSettings.Theme = "dark"`** (was `"auto"`). User dogfood feedback: light theme is still in beta polish, so new installs land on the polished dark surface. `MainWindow.xaml` Light radio shows `"Light [BETA]"` with tooltip explaining beta state. `DefaultSettingsTests` assertion updated. **APP_DESIGN.md theme defaults section needs an update** — flagged for spec revision.
- **`src/KusPus.App/OnboardingWindow.xaml{,.cs}` — Step 6 (Try it) replaced fake `SimulatedSentences` with real `IAudioRecorder` + `IWhisperRunner` pipeline.** 5s countdown record → transcribe with active model → render actual transcript (or error). Threaded audio/whisper/models services through the OnboardingWindow constructor + both call sites (`App.OnStartup` + `MainWindow.OnRerunOnboarding`). Surfaces broken-mic / missing-model failures during setup instead of after onboarding finishes.
- **`src/KusPus.App/OnboardingWindow.xaml{,.cs}` — Step 3 adds `OnbInputDeviceCombo`** (the mic chooser). Writes to the same `PrefsStore.Audio.InputDeviceId` field that Preferences → Audio uses, so the selection persists until the user changes it from either surface. `ResolveOnbMicDevice` mirrors `MainWindow.ResolveLevelMeterDevice` — falls back to OS default if the saved id doesn't resolve. Live meter card re-binds when the user changes selection.
- **`src/KusPus.App/Styles/Tokens.xaml` — added `Icon.Glyph=11` + `Icon.Chevron=9` double tokens** for unified icon sizing across pill / tray / dock chrome. Replaces literal `FontSize="10/11/8"` scattered across button glyphs. Bound via `{StaticResource Icon.Glyph}` / `{StaticResource Icon.Chevron}`.
- **`src/KusPus.App/FloatingPillWindow.xaml` — Pill drop shadow softened** to `ShadowDepth=0 BlurRadius=14 Opacity=0.25` (was `Depth=2 Blur=32 Opacity=0.45`) per UX audit Fix #9. Omnidirectional soft halo so the shadow doesn't bleed onto the dock when the drawer is open. Inner `PillInnerHighlight` Rectangle removed per Fix #7 — only existed on the pill (not the dock), created a seam at the drawer junction. All chrome gutters standardised at 6 px per Fix #10.
- **`src/KusPus.App/FloatingPillWindow.xaml` — `MicChooserButton` hover** swapped from `Opacity=1.4` (silent no-op — WPF clamps at 1) to `Background={DynamicResource SurfaceElevated}` via Setter. Real, theme-aware lift.
- **`docs/ROADMAP.md` — added R1.2-10 long-mode chunk-on-VAD streaming entry** (2026-05-17). User dogfood feedback asked for continuous "speak-pause-paste" loop; researched 3 architectures (sliding-window vs chunk-on-VAD vs library-binding) and recommended chunk-on-VAD on a second hotkey ("Option B"). Deferred to v1.2 — ~2 weeks build + 1 week dogfood, too large for the pre-v1 polish window. Entry includes 8-cluster plan + risk register + latency expectations.

### Late-afternoon dogfood pass (2026-05-17, commits 03853e7…0ce194c)

- **`src/KusPus.App/OnboardingWindow.xaml.cs` — Skip now sets `Onboarding.Completed = true`** (was: false). Onboarding modal opens **once-ever** per install; closing via either Skip or Finish is honoured the same way. Re-runnable via About → "Run again". Replaces the prior "skip-on-skip keeps re-prompting" semantics which were hostile to dogfood users.
- **`src/KusPus.App/App.xaml.cs` — pill `Bind()` / `BindLevels()` deferred** to after the onboarding modal closes (or runs immediately if no onboarding). Pill is now invisible while the modal is up, then appears with FadePillIn after Finish/Skip. New `BindPillAndShow()` helper. Per user spec: "When the onboarding setup is opened the pill UI should not be visible."
- **`src/KusPus.App/OnboardingWindow.xaml.cs` — Step 3 mic init runs async** via `OpenMicStepAsync` mirroring `MainWindow.OpenAudioTabAsync`. Page paints immediately with "Loading microphones…" + "LOADING…" placeholders; MMDevice enum + WasapiCapture init run on `Task.Run`; UI populates when ready. Step 3 used to block the dispatcher for ~250 ms on first entry.
- **`src/KusPus.App/OnboardingWindow.xaml.cs` — apartment-marshalling bug fix.** First async pass returned the `MMDevice` across the `Task.Run`→dispatcher boundary, then accessed `.FriendlyName` on STA — NAudio's `IMMDevice` doesn't support standard COM cross-apartment marshalling, so the property getter threw `InvalidCastException` → `E_NOINTERFACE`. Surfaced (after broadened catch) as "Microphone blocked" even when nothing held the mic. Fix: read `FriendlyName` **inside** the `Task.Run` lambda (on the MTA thread that created the device), return only the string + `WasapiCapture` across the await. `MMDevice` never crosses thread boundaries. `WasapiCapture` is fine cross-thread because it caches `WaveFormat` internally before its ctor returns — that's why `MainWindow.OpenAudioTabAsync` (which only returns the capture) never had this bug. Also broadened the `Task.Run` catch from `COMException`+`MmException` → general `Exception`, and wrapped `OpenMicStepAsync` in an outer try/catch so any unhandled error surfaces as `ShowMicError` instead of silent stuck-Loading.
- **`src/KusPus.App/MainWindow.xaml` — Globe + GitHub social icons wrapped in fixed 24×24 `Canvas`.** All four social icons (LinkedIn, X, GitHub, Globe) now wrap their `Path` in a `Canvas Width=24 Height=24` so the `Viewbox` measures against guaranteed identical bounds. Path bboxes vary subtly (GitHub's `M12 .297` y-offset, X's `0.258` left edge, Bezier control points extending beyond visible curves) which caused uneven rendered sizes when `Viewbox` uniformly stretched each to 14×14. Globe geometry was also expanded earlier from `(2,2) W=20 H=20` → `(1,1) W=22 H=22` so its visible ink fills the box; the canvas wrapper is belt-and-suspenders on top of that.
- **`src/KusPus.App/FloatingPillWindow.xaml{,.cs}` — Both record glyphs share the same state pattern.** `CompactRecordGlyph` (corner, 10×10) and `RecordGlyph` (dock, 8×8) are now both **grey** (`MutedText`) when idle, **red** (`#EF5350`) when actively recording. Single brush shared in `UpdateRecordGlyph` so the two glyphs stay in sync. Earlier the dock glyph was always red (reading as "always recording at rest"); the compact corner glyph was the first to flip per Option α; this commit aligned the dock with the corner.
- **`src/KusPus.App/FloatingPillWindow.xaml.cs` — Nudge timer 10 s → 2 s.** "Click into your text field" hint is now a brief flash. The earlier 10 s landed after the dispatch-race fix (Aug 2026-05-17) but turned out to linger far longer than the hint needed.
- **`src/KusPus.App/FloatingPillWindow.xaml{,.cs}` — Pill bottom corners squared while dock is open.** `PillSurface.CornerRadius` snaps `8` → `(8,8,0,0)` at the start of `OpenDock()` and back to `8` at the start of `CloseDock()`. Pinned mode never calls those methods (gated by `!_isPinned` in `OnPillMouseEnter`/`Leave` + `OnPinClick`), so compact-mode pill keeps its full rounded corners untouched. Snap (not animate) because `CornerRadius` isn't a natively animatable `DependencyProperty` and a brief asymmetry during a 240 ms slide is acceptable. Per user: pill+dock should read as one continuous shape while drawer is visible.
- **`src/KusPus.App/MainWindow.xaml` — About tagline + History search bar accent.** Tagline `"Press a hotkey. Speak. Get pasted."` → `"Local · Privacy First"` with middle-dot rhythm matching the legal line. History search bar gets a 2-layer mint accent per UX Pro Max: magnifier glyph `Foreground=Mint` (persistent brand cue, low-key) + bottom border `1px BorderSubtle` → `2px Mint` (element doubles as accent + divider above table header). No bg fill — that would compete with row data.
- **`src/KusPus.App/Styles/Tokens.xaml` — `Icon.Glyph=11` + `Icon.Chevron=9` double tokens** were added earlier in this session; restated here so a future grep for "icon size" finds the deviation log alongside the token definition.

Append to this list (don't replace) when a new deviation lands.

## When in doubt

- Smaller change is better. One cluster at a time.
- The gate is the discipline. Don't skip it.
- Read the spec section again before guessing.
- Ask me. Confirmation is cheap; rework is not.
