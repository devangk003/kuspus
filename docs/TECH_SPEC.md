# KusPus — Technical Implementation Specification

| | |
|---|---|
| **Status** | Draft v0.2 — pre-implementation |
| **Author** | Devang Kumawat |
| **Created** | 2026-05-16 |
| **Revised** | 2026-05-16 (v0.2: .NET 10, signing permanently dropped, hook self-heal, paste retry, R2R no-compression publish) |
| **Relationship to PRD** | This document is prescriptive. The PRD answers WHAT and WHY; this answers HOW. Where the PRD specified behavior, this specifies the code that produces it. |
| **Target build** | KusPus v1.0 on Windows 10 22H2 + 11 (x64), .NET 10 LTS |

This document is intentionally opinionated. Every decision needed to start coding is made here. Anything genuinely uncertain is listed in [Part XIV — Open engineering questions](#part-xiv--open-engineering-questions); everything else is a contract for the implementation phase.

---

## Table of contents

- **Part I — Foundations**
  - [1. Solution structure](#1-solution-structure)
  - [2. Repo layout](#2-repo-layout)
  - [3. Build tooling](#3-build-tooling)
  - [4. Dependencies](#4-dependencies)
  - [5. Local dev setup](#5-local-dev-setup)
- **Part II — Cross-cutting concerns**
  - [6. Threading model](#6-threading-model)
  - [7. Dependency injection & composition root](#7-dependency-injection--composition-root)
  - [8. Logging](#8-logging)
  - [9. Settings storage & schema](#9-settings-storage--schema)
  - [10. Error handling & failure modes](#10-error-handling--failure-modes)
  - [11. Versioning](#11-versioning)
- **Part III — Core components**
  - [12. AppCoordinator & state machine](#12-appcoordinator--state-machine)
  - [13. HotkeyEngine](#13-hotkeyengine)
  - [14. AudioRecorder](#14-audiorecorder)
  - [15. WhisperRunner](#15-whisperrunner)
  - [16. PasteEngine](#16-pasteengine)
  - [17. HistoryStore](#17-historystore)
  - [18. ModelManager](#18-modelmanager)
  - [19. CrashReporter](#19-crashreporter)
  - [20. PrefsStore](#20-prefsstore)
  - [21. SingleInstanceGuard](#21-singleinstanceguard)
- **Part IV — UI implementation**
  - [22. MVVM pattern & WPF layout](#22-mvvm-pattern--wpf-layout)
  - [23. MainWindow](#23-mainwindow)
  - [24. FloatingPillWindow](#24-floatingpillwindow)
  - [25. Tray icon](#25-tray-icon)
  - [26. Onboarding flow](#26-onboarding-flow)
  - [27. Theming & DPI](#27-theming--dpi)
- **Part V — Native interop**
  - [28. P/Invoke catalog](#28-pinvoke-catalog)
- **Part VI — Build, install, ship**
  - [29. Whisper.cpp build pipeline](#29-whispercpp-build-pipeline)
  - [30. Inno Setup installer script](#30-inno-setup-installer-script)
  - [31. CI/CD](#31-cicd)
  - [32. Release procedure](#32-release-procedure)
- **Part VII — Testing**
  - [33. Test strategy](#33-test-strategy)
  - [34. Manual test matrix](#34-manual-test-matrix)
  - [35. Performance budget](#35-performance-budget)
- **Part VIII — Appendices**
  - [Part XIV — Open engineering questions](#part-xiv--open-engineering-questions)

---

# Part I — Foundations

## 1. Solution structure

KusPus is a single Visual Studio solution containing six projects. The split is deliberate: it lets us unit-test Core without dragging in UI or native dependencies.

```
KusPus.sln
├── src/
│   ├── KusPus.App/             [WPF app, entry point, DI composition root]
│   ├── KusPus.Core/            [domain models, state machine, interfaces — no Win32, no UI]
│   ├── KusPus.Native/          [P/Invoke wrappers, LL hook, SendInput, foreground capture]
│   ├── KusPus.Audio/           [WASAPI capture via NAudio]
│   ├── KusPus.Whisper/         [subprocess launcher, model manager, SHA verify]
│   └── KusPus.Persistence/     [SQLite history, JSON settings, file watcher]
└── test/
    ├── KusPus.Core.Tests/
    ├── KusPus.Persistence.Tests/
    └── KusPus.Whisper.Tests/
```

### Project responsibilities

| Project | Target framework | References | Touches Win32? | Touches UI? |
|---|---|---|---|---|
| `KusPus.Core` | `net10.0` | (none — pure C#) | No | No |
| `KusPus.Native` | `net10.0-windows` | `KusPus.Core` | **Yes** | No |
| `KusPus.Audio` | `net10.0-windows` | `KusPus.Core`, NAudio | Indirectly (NAudio) | No |
| `KusPus.Whisper` | `net10.0` | `KusPus.Core` | No | No |
| `KusPus.Persistence` | `net10.0` | `KusPus.Core`, Microsoft.Data.Sqlite | No | No |
| `KusPus.App` | `net10.0-windows` (WPF, single-file publish) | all of the above | Yes (via Native) | **Yes** |

### Namespaces

Every project uses a namespace matching its project name (`KusPus.Core`, `KusPus.Native`, etc.). Nested types use folder-matched sub-namespaces.

### Why not fewer projects

A flat single-project solution would compile faster but make unit testing the state machine require referencing WPF. The 6-project split costs ~3 seconds at solution restore and pays back every time a Core test runs without spinning up a UI thread.

### Why not more projects

We deliberately do not split UI into a separate library project. WPF UI + view-models live in `KusPus.App`. A theoretical `KusPus.UI` would invite premature abstraction; nothing in v1 needs UI reuse.

---

## 2. Repo layout

```
KusPus/
├── src/                          (see §1)
├── test/                         (see §1)
├── installer/
│   ├── KusPus.iss                (Inno Setup script)
│   ├── assets/
│   │   ├── kuspus.ico
│   │   ├── installer-banner.bmp
│   │   └── installer-readme.rtf
│   └── build-installer.ps1
├── third_party/
│   └── whisper.cpp/              (git submodule, locked to a known SHA)
├── tools/
│   ├── build-whisper-windows.ps1 (builds whisper.exe + dlls)
│   ├── compute-sha256.ps1
│   └── verify-egress-allowlist.ps1  (greps for outbound URLs not in allowlist)
├── docs/
│   ├── PRD.md
│   ├── ROADMAP.md
│   ├── TECH_SPEC.md              (this file)
│   ├── BUILD.md                  (developer build instructions)
│   ├── INSTALL.md                (end-user install troubleshooting)
│   └── ARCHITECTURE.md           (one-page diagram, generated from this doc)
├── .github/
│   └── workflows/
│       ├── ci.yml                (build + test on every PR)
│       └── release.yml           (tag-triggered installer build)
├── .gitignore
├── .editorconfig
├── Directory.Build.props         (centralised compiler flags)
├── KusPus.sln
├── README.md
└── LICENSE
```

**Submodule pinning:** `third_party/whisper.cpp` is a submodule pinned to a specific commit. Updates require a deliberate `git submodule update --remote` + manual verification on a known WAV fixture.

**`.editorconfig` highlights:**
- 4-space indentation, LF endings
- `file_header_template = unset` (no banners)
- `dotnet_style_qualification_for_*` = false (omit `this.`)
- nullable enabled, treat warnings as errors

**`Directory.Build.props`:**
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningLevel>9999</WarningLevel>
    <LangVersion>latest</LangVersion>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>
</Project>
```

---

## 3. Build tooling

| Tool | Version | Purpose |
|---|---|---|
| **.NET SDK** | 10.0.x (latest LTS patch) | Build all C# projects |
| **Visual Studio 2022** | 17.12+ (or VS 2026 when GA) | Primary IDE (Community is fine) |
| **Inno Setup** | 6.4.0+ | Installer compilation |
| **PowerShell** | 5.1 (Windows built-in) or 7.x | Build scripts |
| **Git** | 2.40+ | VCS + submodule |
| **MSVC (Build Tools for Visual Studio 2022)** | 14.40+ | Whisper.cpp compilation |
| **CMake** | 3.28+ | Whisper.cpp build orchestration |
| **GitHub Actions** | (cloud) | CI/CD |

### Publish profile

`KusPus.App` publishes as:
- `PublishSingleFile=true`
- `SelfContained=true`
- `RuntimeIdentifier=win-x64`
- `IncludeNativeLibrariesForSelfExtract=true`
- `EnableCompressionInSingleFile=false` *(intentional — see rationale below)*
- `PublishTrimmed=false` (WPF + reflection don't trim cleanly; opt-out is safer for v1)
- `PublishReadyToRun=true`

Resulting size: ~95–110 MB single `.exe` (larger than a compressed build by ~20–30 MB). The installer adds whisper.exe (~5 MB) + DLLs (~30 MB) + tiny.en (~75 MB), totalling ~215–230 MB.

**Rationale for R2R-without-compression.** `PublishReadyToRun=true` ships pre-jitted native code so the app starts faster. `EnableCompressionInSingleFile=true` shrinks the on-disk image but decompresses everything on launch, costing back most of the R2R gain. For a desktop utility where the user opens KusPus once and leaves it running, cold-start time matters more than installer size. The ~25 MB we'd save isn't worth a slower first launch. (See open question EQ-03 — resolved 2026-05-16.)

**Rationale for self-contained:** Eliminates the "user needs to install .NET 10 runtime" hurdle for a friends-only audience. Worth the +70 MB over a framework-dependent publish.

---

## 4. Dependencies

### NuGet packages (pinned with exact versions)

| Package | Version | Used by | Why |
|---|---|---|---|
| `Microsoft.Extensions.DependencyInjection` | 10.0.0 | App | DI container |
| `Microsoft.Extensions.Hosting` | 10.0.0 | App | Host lifecycle |
| `Microsoft.Extensions.Logging` | 10.0.0 | All | Logging abstraction |
| `Serilog.Extensions.Logging` | 9.0.0 | App | Bridge MEL → Serilog |
| `Serilog` | 4.2.0 | App | Structured logging |
| `Serilog.Sinks.File` | 6.0.0 | App | Rotating file appender |
| `Microsoft.Data.Sqlite` | 10.0.x | Persistence | SQLite |
| `H.NotifyIcon.Wpf` | 2.0.131 | App | Tray icon (well-maintained WPF wrapper) |
| `CommunityToolkit.Mvvm` | 8.4.2 | App | Source-generated `INotifyPropertyChanged`, `RelayCommand`; partial-property syntax |
| `NAudio` | 2.2.1 | Audio | WASAPI capture |
| `Sentry` | 5.0.0 | App | Opt-in crash reporter |
| `System.Reactive` | 6.0.1 | Core | `IObservable<T>` for state machine events (only) |

### CommunityToolkit.Mvvm 8.4 partial-property syntax

8.4 supports `partial` properties on `[ObservableProperty]`, removing the underscore-field idiom. Prefer:

```csharp
public partial class ExampleViewModel : ObservableObject {
    [ObservableProperty] public partial string Name { get; set; }
}
```

over the older:

```csharp
public partial class ExampleViewModel : ObservableObject {
    [ObservableProperty] private string _name = "";
}
```

throughout `KusPus.App`.

### Native libraries (shipped, not from NuGet)

| Library | Version pin | Source | Output |
|---|---|---|---|
| `whisper.cpp` | Submodule commit SHA pinned in `third_party/` | Built locally via `tools/build-whisper-windows.ps1` | `whisper.exe`, `whisper.dll`, `ggml*.dll` |

### Forbidden dependencies

These are explicitly rejected to keep the binary lean and the trust surface small:
- **Entity Framework Core.** Overkill for our 1-table schema. Direct `Microsoft.Data.Sqlite` is sufficient.
- **WPF UI libraries (MaterialDesignThemes, MahApps.Metro).** Bloat. We use stock WPF + a small custom ResourceDictionary.
- **Reactive Extensions UI (ReactiveUI).** CommunityToolkit.Mvvm is lighter and source-generates the boilerplate.
- **Newtonsoft.Json.** `System.Text.Json` is sufficient.
- **AutoMapper, MediatR.** No.

---

## 5. Local dev setup

`BUILD.md` will contain the user-facing version; the contract is:

1. Clone with submodules: `git clone --recurse-submodules`
2. `tools/build-whisper-windows.ps1` once (10–15 min cold)
3. `dotnet restore`
4. F5 in Visual Studio → launches `KusPus.App` debug build
5. `dotnet test` runs all unit tests

Pre-commit hook (optional, documented): runs `dotnet format` + `verify-egress-allowlist.ps1`.

---

# Part II — Cross-cutting concerns

## 6. Threading model

The app has exactly **five** logical thread contexts. Crossing between them is explicit and goes through a single mechanism per direction.

| # | Thread | Created by | Owns |
|---|---|---|---|
| 1 | **UI thread** | WPF runtime | All UI mutation, view-models, `Dispatcher` |
| 2 | **Hook thread** | Manually started by `HotkeyEngine` | `WH_KEYBOARD_LL` hook; runs `GetMessage` loop |
| 3 | **Audio thread** | NAudio (via WASAPI client) | WASAPI buffer events, RMS computation, WAV writing |
| 4 | **Whisper task** | `Task.Run` per transcription | Reads stdout/stderr/side-file from `whisper.exe` |
| 5 | **Persistence task queue** | A single `Channel<Func<Task>>` consumer | All SQLite writes (serialised) |

### Marshalling rules

| From | To | Mechanism |
|---|---|---|
| Hook → UI | `Dispatcher.BeginInvoke(DispatcherPriority.Send, ...)` |
| Audio → UI | `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` for visualizer (15 Hz throttle); `Send` for state transitions |
| Whisper → UI | `await` from a UI-thread context via `ConfigureAwait(true)` |
| UI → Persistence | Push to `Channel<Func<Task>>`; fire-and-forget |
| Persistence → UI | `Dispatcher.BeginInvoke` only for History pane refresh |

### Forbidden patterns

- **No `Thread.Sleep` on the UI thread.** Use `Task.Delay`.
- **No blocking on async from UI.** No `.Result`, no `.Wait()`, no `.GetAwaiter().GetResult()` on UI thread.
- **No `lock` held across an `await`.** Use `SemaphoreSlim` if mutual exclusion across awaits is needed (rare; only PrefsStore).
- **No long work on the hook thread.** Hook callbacks must return in < 1 ms. They only push events to a `Channel<HookEvent>` consumed by the UI thread.

### Cancellation

Every long-running task takes a `CancellationToken`. App shutdown raises a `CancellationTokenSource` from the composition root; every task observes it.

---

## 7. Dependency injection & composition root

DI is wired in `KusPus.App/Program.cs` via `Microsoft.Extensions.DependencyInjection`. Lifetime defaults:

| Type | Lifetime | Reason |
|---|---|---|
| `IHotkeyEngine`, `IAudioRecorder`, `IWhisperRunner`, `IPasteEngine`, `IHistoryStore`, `IModelManager`, `IPrefsStore`, `ICrashReporter` | **Singleton** | One per app process; hold OS resources |
| `AppCoordinator` | **Singleton** | The state machine |
| View-models | **Singleton** for shell-level (MainWindowVM, FloatingPillVM); **Transient** for per-screen (PreferencesVM, HistoryVM, OnboardingStepVM) | View-models holding mutable state must be singletons |
| `ILogger<T>` | Provided by MEL | — |

```csharp
// KusPus.App/Program.cs (sketch)
public static class Program {
    [STAThread]
    public static int Main(string[] args) {
        using var mutex = SingleInstanceGuard.AcquireOrSignal();
        if (!mutex.IsOwner) return 0;

        var host = Host.CreateApplicationBuilder(args)
            .ConfigureServices()
            .Build();

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
```

Composition is in `KusPus.App/Composition/ServiceRegistration.cs`; one method per concern (`AddCore`, `AddNative`, `AddAudio`, `AddWhisper`, `AddPersistence`, `AddUI`).

---

## 8. Logging

### Stack

`Microsoft.Extensions.Logging` (consumed) → `Serilog` (provider) → `Serilog.Sinks.File` (rotating file).

### Sinks

- **File:** `%LOCALAPPDATA%\KusPus\logs\kuspus-.log` with daily roll, retained 5 days, max 5 MB per file, max 5 files total. Format: `[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}`.
- **Debug (DEBUG builds only):** Visual Studio Output window via `Serilog.Sinks.Debug`.

### What is logged at each level

| Level | Examples | Includes transcript text? |
|---|---|---|
| `Verbose` | Hook callback enter/exit | No |
| `Debug` | State transitions, NAudio buffer sizes | No |
| `Information` | App start/stop, model activated, recording started/stopped | No |
| `Warning` | Mic device changed mid-recording, retry on clipboard contention, missing custom model path | No |
| `Error` | Whisper non-zero exit, model SHA mismatch, paste skipped (HWND closed) | No (only error context — exit code, file path WITHOUT user-data segments) |
| `Fatal` | Unhandled exception about to crash app | Sanitised stack trace only |

### Scrubbing in logs

A custom `ILogEventEnricher` strips:
- `%USERPROFILE%`, `%APPDATA%`, `%LOCALAPPDATA%`, `%TEMP%` prefixes — replaced with the env-var name placeholder.
- Any property whose key contains `text`, `transcript`, `clipboard`, `password` is dropped before write.

### Source contexts

Each component uses `ILogger<T>` so `SourceContext` = `KusPus.X.Y`. Filter by component when debugging.

---

## 9. Settings storage & schema

### Location

`%APPDATA%\KusPus\settings.json` (read-write).

### Schema (v1)

```json
{
  "schemaVersion": 1,
  "hotkey": {
    "modifiers": ["LeftCtrl", "LeftWin"],
    "keyCode": null,
    "holdThresholdMs": 250
  },
  "audio": {
    "inputDeviceId": null,
    "captureSampleRate": 16000
  },
  "models": {
    "activeModelId": "ggml-tiny.en",
    "customModelPath": null
  },
  "ui": {
    "theme": "auto",
    "pillPosition": "bottom-center"
  },
  "history": {
    "enabled": true
  },
  "privacy": {
    "offlineMode": false,
    "crashReportsOptIn": false
  },
  "autostart": false,
  "onboarding": {
    "completed": false,
    "version": 1
  }
}
```

### Validation

- Loaded via `System.Text.Json` with a strict POCO type tree.
- `schemaVersion` mismatch → run a migration chain (`Migrate1To2`, `Migrate2To3`, …). v1 has none.
- Unknown fields are warned + ignored. Missing fields are filled with defaults.
- After load, settings are validated (e.g., `holdThresholdMs > 0`); invalid → reset to default + log warning.

### Persistence

- All writes go through `PrefsStore.SaveAsync` which:
  1. Serializes to a temp file `settings.json.tmp` in the same folder.
  2. `File.Replace(settings.json.tmp, settings.json, null)` — atomic on NTFS.
  3. Notifies subscribers (`IObservable<AppSettings>` exposed for VMs).
- A `FileSystemWatcher` watches `settings.json` for external edits (so a user editing `customModelPath` by hand is honored without restart).
- Concurrent writes serialized by a `SemaphoreSlim(1,1)` inside PrefsStore.

### Defaults

Defaults live in `KusPus.Core.Defaults.DefaultSettings`. Source of truth for both first-run write and migration filling.

---

## 10. Error handling & failure modes

### Philosophy

- Internal code throws exceptions; **boundary code returns Results.** Boundaries: anywhere we call out of process (whisper subprocess, network, SQLite, Win32 calls that can fail), and the top of every async chain reachable from a UI command.
- The `Result<T>` type lives in `KusPus.Core`:
  ```csharp
  public readonly record struct Result<T>(bool Success, T? Value, string? Error, Exception? Cause) {
      public static Result<T> Ok(T value) => new(true, value, null, null);
      public static Result<T> Fail(string error, Exception? cause = null) => new(false, default, error, cause);
  }
  ```
- UI displays the `.Error` string from a Result; never raw exception messages.

### Global unhandled exception handlers

In `App.OnStartup`:
- `AppDomain.CurrentDomain.UnhandledException`
- `Application.Current.DispatcherUnhandledException`
- `TaskScheduler.UnobservedTaskException`

All three log `Fatal`, attempt a graceful shutdown (release mutex, flush logs, send crash report if opted-in), and exit with code 1.

### User-visible failure modes

| Scenario | UX |
|---|---|
| Whisper subprocess fails | Pill shows "Transcription failed — see History" |
| Audio device unavailable | Pill shows "Microphone access blocked" |
| Disk full | Pill shows "Disk full" |
| Paste foreground window gone | Pill shows "Window gone — text in clipboard" |
| Model file corrupt (SHA mismatch) | Models pane shows red badge, "Re-download" button |
| Settings.json corrupt | App boots with defaults, banner: "Your settings were reset — see logs" |

---

## 11. Versioning

- **Assembly version:** `Major.Minor.Patch.0` (e.g., `1.0.0.0`). Auto-bumped from the highest git tag matching `v*` at CI time.
- **File version:** same as assembly version.
- **Informational version:** `1.0.0+{git-short-sha}` for diagnostics.
- **Schema versions:** independent integers per persistence layer (`settings.json schemaVersion`, SQLite `PRAGMA user_version`).

`KusPus.Core.Versioning` exposes a static `AppVersion` record with `Major`, `Minor`, `Patch`, `GitSha`, `BuildTimestamp`. Set at build time via MSBuild properties from a `Version.props` file.

---

# Part III — Core components

## 12. AppCoordinator & state machine

### Responsibility

Owns the global state of "what is KusPus doing right now". Receives events from `HotkeyEngine`, drives `AudioRecorder` / `WhisperRunner` / `PasteEngine` / `HistoryStore` / `FloatingPillWindow` as side effects.

### States

```csharp
public enum AppState {
    Idle,
    Armed,          // chord engaged, hold-threshold timer running
    Recording,      // audio capturing (either hold-mode or persistent-tap-mode)
    Transcribing,   // whisper running
    Cancelled,      // chord released after another key was pressed
}
```

### Events

```csharp
public abstract record CoordinatorEvent;
public record ChordEngaged() : CoordinatorEvent;
public record ChordReleased() : CoordinatorEvent;
public record HoldThresholdElapsed() : CoordinatorEvent;
public record OtherKeyPressedWhileArmed() : CoordinatorEvent;
public record TranscribeComplete(string Text, TimeSpan Duration, string Model) : CoordinatorEvent;
public record TranscribeFailed(string Error, string? FailedWavPath) : CoordinatorEvent;
public record ToggleFromTray() : CoordinatorEvent;
```

### Transition table (authoritative — implement as `switch (state, event)`)

| From | Event | To | Side effects |
|---|---|---|---|
| `Idle` | `ChordEngaged` | `Armed` | Capture foreground HWND; start 250 ms hold timer |
| `Idle` | `ToggleFromTray` | `Recording` | Start audio capture; show pill (persistent-tap-mode) |
| `Armed` | `ChordReleased` | `Recording` (persistent-tap-mode) | Show pill; start audio capture |
| `Armed` | `HoldThresholdElapsed` | `Recording` (hold-mode) | Show pill; start audio capture |
| `Armed` | `OtherKeyPressedWhileArmed` | `Cancelled` | Cancel timer |
| `Recording` (hold-mode) | `ChordReleased` | `Transcribing` | Stop audio; spawn whisper |
| `Recording` (hold-mode) | `OtherKeyPressedWhileArmed` | `Recording` (hold-mode) | No-op (typing while recording is normal) |
| `Recording` (tap-mode) | `ChordEngaged` | `Transcribing` | (Tap to stop) Stop audio; spawn whisper |
| `Recording` (tap-mode) | `ChordReleased` | `Recording` (tap-mode) | **No-op** (chord was released long ago when tap-mode started; transient re-fires from sticky-keys or hardware ignored) |
| `Recording` (tap-mode) | `ToggleFromTray` | `Transcribing` | Stop audio; spawn whisper |
| `Recording` (tap-mode) | `OtherKeyPressedWhileArmed` | `Recording` (tap-mode) | No-op |
| `Transcribing` | `TranscribeComplete` | `Idle` | Set clipboard; SetForegroundWindow + SendInput Ctrl+V; record history; show in-pill paste confirmation; hide pill |
| `Transcribing` | `TranscribeFailed` | `Idle` | Move `.wav` to `failed\`; record failed history; show pill error 2 s; hide pill |
| `Cancelled` | `ChordReleased` | `Idle` | (no-op) |

### Implementation

- Single-threaded: events are pushed to a `Channel<CoordinatorEvent>` from any thread; a single consumer task runs on the UI thread.
- Recording sub-mode (hold vs tap) is a private boolean on the coordinator, not a separate state.
- State transitions emit `IObservable<AppState>` for the pill view-model to bind to.

### Why a hand-rolled FSM and not Stateless / Appccelerate.StateMachine

Five states. Eight events. A 50-line `switch` is simpler than a dependency.

---

## 13. HotkeyEngine

### Responsibility

Globally observe keyboard events; classify into tap-vs-hold of the configured chord; suppress LWin's Start-menu side-effect.

### Public interface

```csharp
public interface IHotkeyEngine {
    void Start();
    void Stop();
    void SetChord(HotkeyChord chord);
    IObservable<HotkeyEvent> Events { get; }
}

public record HotkeyChord(IReadOnlyList<VirtualKey> Modifiers, VirtualKey? Key);

public abstract record HotkeyEvent;
public record ChordEngaged() : HotkeyEvent;
public record ChordReleased() : HotkeyEvent;
public record OtherKeyPressed() : HotkeyEvent;
```

### Implementation

- Installs `WH_KEYBOARD_LL` on a dedicated thread that runs `GetMessageW` indefinitely. Thread name: `KusPus.HookThread`.
- Callback delegate kept in a static field to prevent GC collection.
- Callback body:
  1. Cast `lParam` to `KBDLLHOOKSTRUCT`.
  2. Determine: is this `KEYDOWN` or `KEYUP`? Is this a modifier we care about? Is this another key?
  3. Update internal bitmap of "which of our modifiers are held".
  4. Detect chord-engaged: all configured modifiers held AND no configured `Key` (modifier-only chord) OR configured `Key` just pressed while all modifiers held.
  5. Detect chord-released: any modifier of the chord goes up.
  6. Push event into a `Channel<HookEvent>` (lock-free, single producer, single consumer).
  7. **LWin suppression:** if we're consuming the chord and the user is releasing `VK_LWIN`, post a synthetic `VK_CONTROL` keydown+keyup via `SendInput` *before* returning, and return `1` to suppress the LWin keyup. This is the textbook trick to prevent the Start menu opening when LWin is released alone.
  8. Return `CallNextHookEx(IntPtr.Zero, code, wParam, lParam)` for all events we don't consume.
- Callback **must not allocate** in the hot path beyond pushing one record to the channel.

### Hold-vs-tap decision

Lives in `AppCoordinator`, not here. `HotkeyEngine` only reports raw chord engage/release. The 250 ms timer is started by the coordinator when transitioning to `Armed`.

### Settings

Initial chord loaded from `IPrefsStore`. Re-binding from the UI calls `SetChord` which restarts the hook with the new config (cheap).

### Hook self-heal (mitigation for `LowLevelHooksTimeout`)

Windows enforces a per-hook timeout via the registry value `HKCU\Control Panel\Desktop\LowLevelHooksTimeout` (default 5000 ms). If a callback ever exceeds it, **Windows silently uninstalls the hook** — no exception, no log line, no notification. The app then stops responding to the hotkey with no visible failure.

Our design (callback returns in `< 1 ms` because it only pushes one record to a `Channel<HookEvent>`) is well within the budget, but we add a defensive heartbeat against pathological cases (GC pause, machine under heavy load, antivirus inspecting the process):

1. A `HookWatchdog` task wakes every 30 seconds.
2. It posts a synthetic, harmless probe event via `SendInput` — specifically a `KEYUP` of an unused virtual key (e.g., `VK_NONAME = 0xFC`).
3. The hook callback marks "alive = true" on receipt.
4. If alive flag is not set within 250 ms of the probe, the watchdog logs `Warning`, unhooks (defensive even though Windows already did), and re-installs via `SetWindowsHookExW`. The HotkeyEngine emits a `HookReinstalled` event for the AppCoordinator to surface a one-time pill notice ("Hotkey reconnected") if it fires.
5. The watchdog also runs once immediately on `Start()` to verify install succeeded.

In DEBUG builds, the callback additionally measures its own duration via a thread-local stopwatch and logs `Warning` if any single callback exceeds 500 µs.

### LL hook lifecycle on unhandled exceptions

If the AppCoordinator hits an unhandled exception, the hook is uninstalled in `App.OnExit` to avoid leaving a dangling LL hook on the system (which would otherwise survive until process termination — usually fine, but explicit cleanup is good citizen behavior).

---

## 14. AudioRecorder

### Responsibility

Open the default WASAPI input device, capture at 16 kHz mono 16-bit PCM, write to a temp `.wav`, expose RMS levels for the visualizer.

### Public interface

```csharp
public interface IAudioRecorder {
    Task<Result<RecordingHandle>> StartAsync(CancellationToken ct);
    Task<Result<RecordedFile>> StopAsync();
    IObservable<float[]> Levels { get; }  // 20-channel RMS, 15 Hz
}

public record RecordedFile(string WavPath, TimeSpan Duration);
```

### Implementation

- Uses `NAudio.Wave.WasapiCapture` on `MMDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)`.
- Source format is whatever the device exposes (commonly 48 kHz stereo float).
- Resampled to 16 kHz mono via `NAudio.Wave.SampleProviders.WdlResamplingSampleProvider` (high-quality SRC; not the cheap linear one).
- Bit depth converted to 16-bit PCM via `WaveFloatTo16Provider`.
- Written via `NAudio.Wave.WaveFileWriter` to `%TEMP%\kuspus-{unixMs}.wav`.
- RMS computed in the WASAPI callback over 20 equal chunks per buffer; published at 15 Hz via `IObservable<float[]>`.

### Device-change handling

- Subscribe to `MMDeviceEnumerator` notifications. On default-device-change while recording: `StopAsync` immediately; the coordinator surfaces a "Mic changed — try again" pill error.

### Buffer sizes

- WASAPI shared-mode: ~10 ms callback interval at 48 kHz.
- Internal ring buffer: 5 seconds of 16 kHz PCM (~160 KB). Detects underruns; logs warning if any.

### Maximum recording

- Hard cap at 50 minutes (~96 MB of 16 kHz PCM). Beyond that, `StopAsync` is called automatically and the user sees "Recording capped at 50 min".

---

## 15. WhisperRunner

### Responsibility

Spawn `whisper.exe`, feed it a wav file and a model path, capture output, return transcript.

### Public interface

```csharp
public interface IWhisperRunner {
    Task<Result<string>> TranscribeAsync(string wavPath, ModelDescriptor model, CancellationToken ct);
}
```

### Process invocation

```
{installDir}\whisper\whisper.exe -m {modelPath} -f {wavPath} -nt --output-txt -l en -t {threads}
```

- `-nt` — no timestamps
- `--output-txt` — write side-file `<wav>.txt`
- `-l en` — force English (explicit even though tiny.en is English-only; defensive)
- `-t N` — threads is **model-aware**:
  - `ggml-tiny.en` and `ggml-tiny`: `min(4, max(2, ProcessorCount - 1))` — tiny scales poorly past 4 threads; capping saves CPU/battery.
  - `ggml-base.en` and larger: `min(8, max(2, ProcessorCount - 1))` — larger models benefit from more parallelism.

### whisper.exe integrity check

On the first transcription per app launch, compute the SHA-256 of `{installDir}\whisper\whisper.exe` and compare against the bundled manifest in `KusPus.App.Resources.WhisperSha256.txt`.

- **Match:** proceed; cache the result for the rest of the process lifetime.
- **Mismatch:** the file has been tampered with, corrupted, or partially overwritten. **Refuse to start whisper**: return `Result.Fail("Whisper binary integrity check failed — please reinstall KusPus")` and surface a clear error in the Models pane. Do **not** silently fall back to a different binary or try to repair. This is a hard stop — `ITranscribeAsync` calls will return the same failure until the user reinstalls.

### Lifecycle

1. Validate paths and existence; verify `whisper.exe` SHA against bundled manifest on first call after launch (catches tampering).
2. `var psi = new ProcessStartInfo { … }` with `UseShellExecute = false`, `CreateNoWindow = true`, `RedirectStandardOutput = true`, `RedirectStandardError = true`, `WorkingDirectory = whisperDir`.
3. **Assign to a process-wide Job Object** (see "Job Object containment" below) so the subprocess and any children are guaranteed to terminate when KusPus exits — even on a hard crash.
4. Start with a 5-minute hard timeout (`CancellationTokenSource.CancelAfter(5_min)`).
5. Read stdout/stderr concurrently (`Process.OutputDataReceived` / `ErrorDataReceived`).
6. Await `WaitForExitAsync(ct)`. On timeout, `Kill(entireProcessTree: true)` AND close the Job Object handle (which kills the tree as a backstop).
7. On exit code 0: read `<wav>.txt`, trim, delete side-file. Return `Result.Ok(text)`.
8. On non-zero exit: log first 4 KB + last 4 KB of stderr at `Warn`. Return `Result.Fail("Whisper exit code {n}", null)`.

### Job Object containment

To guarantee that a hung or crashed `whisper.exe` (or any subprocess it spawns) cannot outlive KusPus, all whisper invocations are assigned to a single per-process Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` set.

```csharp
// Once per AppCoordinator lifetime (created at app start, closed at app exit)
IntPtr job = Kernel32.CreateJobObjectW(IntPtr.Zero, null);
var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION {
        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    }
};
Kernel32.SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, ...);

// Per whisper invocation, immediately after Process.Start():
Kernel32.AssignProcessToJobObject(job, process.Handle);
```

Properties:
- If `KusPus.exe` dies for any reason (kill, crash, power loss handled by OS shutdown), Windows closes the Job handle and kills every process inside.
- Subprocesses spawned by `whisper.exe` (e.g. future GPU helper processes) are inherited into the Job automatically — no per-child bookkeeping required.
- Cheap: ~50 bytes of kernel state per Job, plus one P/Invoke per spawn.

The Job Object handle is created in `AppCoordinator` and injected into `WhisperRunner` via DI.

### No transcript logging

Stdout/stderr are aggregated; stderr is partially logged on failure (metadata only — exit codes, GGML init messages, NOT transcript content). Stdout (which whisper.cpp uses for progress messages, not transcript) is logged at `Debug`.

---

## 16. PasteEngine

### Responsibility

Set the clipboard. Restore the captured foreground window. Synthesize Ctrl+V. Resolve the target app's friendly name for the in-pill confirmation.

### Public interface

```csharp
public interface IPasteEngine {
    /// <summary>Captures the current foreground window — call this at chord-engage time.</summary>
    Result<IntPtr> CaptureForegroundHwnd();

    /// <summary>Sets clipboard, restores focus to targetHwnd, sends Ctrl+V.</summary>
    Task<PasteOutcome> DeliverAsync(string text, IntPtr targetHwnd);
}

public record PasteOutcome(bool Pasted, string TargetApp, string? Error);
```

### Procedure (`DeliverAsync`)

1. **Clipboard write.**
   ```csharp
   for (int attempt = 0; attempt < 3; attempt++) {
       try { Clipboard.SetText(text); break; }
       catch (ExternalException) when (attempt < 2) { await Task.Delay(50); }
   }
   ```
2. **Resolve target app name.** Via `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `K32GetModuleFileNameExW`. Strip path. Map known executables to friendly names via a static `Dictionary<string, string>` in `KusPus.App.Resources.AppNameMap.json` (e.g., `slack.exe` → "Slack"). Fallback: file name without extension, title-cased.
3. **Restore foreground.** Windows degraded the reliability of `SetForegroundWindow` after Win10 1607 — it silently returns FALSE if another process called `LockSetForegroundWindow(LSFW_LOCK)`, which the shell itself does briefly during common operations. We retry once with `AllowSetForegroundWindow(ASFW_ANY)` before giving up.

   ```csharp
   bool RestoreForeground(IntPtr targetHwnd) {
       if (User32.GetForegroundWindow() == targetHwnd) return true;
       uint currentTid = Kernel32.GetCurrentThreadId();
       uint targetTid  = User32.GetWindowThreadProcessId(targetHwnd, out _);

       bool TryAttach() {
           if (currentTid == targetTid) return User32.SetForegroundWindow(targetHwnd);
           User32.AttachThreadInput(currentTid, targetTid, true);
           try { return User32.SetForegroundWindow(targetHwnd); }
           finally { User32.AttachThreadInput(currentTid, targetTid, false); }
       }

       if (TryAttach()) return true;

       // Defensive retry — some shell operations lock SetForegroundWindow briefly.
       User32.AllowSetForegroundWindow(User32.ASFW_ANY);
       return TryAttach();
   }
   ```

   If both attempts return FALSE, treat the paste as "window gone" — clipboard is still set, pill shows the appropriate confirmation message, and we do **not** blast `SendInput` into whatever has focus now. This is the single most common production bug in apps that do exactly what KusPus does.
4. **Send paste.** Choose chord based on the resolved process name:
   - If the target process is in `TerminalProcessNames`, send **Ctrl+Shift+V** (6 events: Ctrl down, Shift down, V down, V up, Shift up, Ctrl up).
   - Otherwise, send **Ctrl+V** (4 events: Ctrl down, V down, V up, Ctrl up).
   - Single `SendInput` call with all events in one batch (atomic — Windows will not interleave other input).
   ```csharp
   private static readonly HashSet<string> TerminalProcessNames =
       new(StringComparer.OrdinalIgnoreCase) {
           "WindowsTerminal.exe", "OpenConsole.exe",
           "cmd.exe", "powershell.exe", "pwsh.exe",
           "ConEmu64.exe", "ConEmuC64.exe",
           "Cmder.exe",
           "alacritty.exe", "wezterm-gui.exe",
       };
   ```

   Match is by file name only (path stripped, case-insensitive). The set lives in `KusPus.Native.PasteEngine` as a `static readonly` field.

   **Deliberately excluded:**
   - `mintty.exe` (Git Bash, Cygwin) defaults to `Shift+Insert` for paste, not `Ctrl+Shift+V`. A separate clipboard-mode wouldn't help here. If a user dictates into Git Bash, they should rebind the KusPus chord or accept that `Ctrl+V` will send `^V` as a literal.
   - `wsl.exe`, `wslhost.exe` — these almost never appear as the foreground process; the foreground when WSL is open is `WindowsTerminal.exe` hosting WSL, which already triggers the terminal path. Listing them would be dead code.
5. **Verify** (best-effort): after a 50 ms delay, check if `GetForegroundWindow() == targetHwnd`. If not, log Warning and report `Error = "Foreground lost"`.

### What we do NOT do

- Press Enter / Return.
- Send any non-Ctrl-V keystroke.
- Call into UIA.
- Maintain a deny- or allowlist (per PRD §9.6).

---

## 17. HistoryStore

### Responsibility

Append-only log of transcripts. FTS5 search. Soft delete on user request.

### Schema

```sql
PRAGMA journal_mode = WAL;
PRAGMA user_version = 1;

CREATE TABLE IF NOT EXISTS transcripts (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  ts           INTEGER NOT NULL,            -- unix ms
  text         TEXT    NOT NULL,
  duration_ms  INTEGER NOT NULL,
  model        TEXT    NOT NULL,
  target_app   TEXT,
  status       TEXT    NOT NULL CHECK (status IN ('ok','failed')),
  failed_wav   TEXT,
  paste_outcome TEXT  CHECK (paste_outcome IN ('pasted','clipboard_only','window_gone'))
);

CREATE INDEX IF NOT EXISTS ix_transcripts_ts ON transcripts(ts DESC);

CREATE VIRTUAL TABLE IF NOT EXISTS transcripts_fts USING fts5(
  text, content='transcripts', content_rowid='id'
);

CREATE TRIGGER IF NOT EXISTS transcripts_ai AFTER INSERT ON transcripts
  BEGIN INSERT INTO transcripts_fts(rowid, text) VALUES (new.id, new.text); END;

CREATE TRIGGER IF NOT EXISTS transcripts_ad AFTER DELETE ON transcripts
  BEGIN INSERT INTO transcripts_fts(transcripts_fts, rowid, text) VALUES('delete', old.id, old.text); END;

-- NOTE: No AFTER UPDATE trigger by design — `text` is immutable in v1 (transcripts
-- are append-only; the only mutation is row deletion via "Purge" or "Delete").
-- If a future version allows editing a transcript, add:
--   CREATE TRIGGER transcripts_au AFTER UPDATE ON transcripts BEGIN
--     INSERT INTO transcripts_fts(transcripts_fts, rowid, text) VALUES('delete', old.id, old.text);
--     INSERT INTO transcripts_fts(rowid, text) VALUES (new.id, new.text);
--   END;
```

### Public interface

```csharp
public interface IHistoryStore {
    Task AppendAsync(TranscriptRecord record);
    Task<IReadOnlyList<TranscriptRecord>> SearchAsync(string? query, int limit = 200, int offset = 0);
    Task DeleteAsync(long id);
    Task PurgeAllAsync();
}
```

### Migrations

Driven off `PRAGMA user_version`. Migration code lives in `KusPus.Persistence.Migrations` and runs on first connection per app launch. v1 has no migrations.

### Backup

On app start, copy `history.db` to `history.db.bak` if the existing backup is > 24 h old. Rolling 1-day backup. Easy data recovery if the file gets corrupted.

### Concurrency

All writes serialized through the persistence task queue (§6). Reads use a separate read-only connection; WAL allows concurrent reads with the writer.

---

## 18. ModelManager

### Responsibility

List available models. Download from HuggingFace. Verify SHA-256. Activate. Detect custom models from `settings.json`.

### Bundled manifest

`KusPus.Whisper.Resources.models.json` (embedded resource):

```json
{
  "schemaVersion": 1,
  "hfRepoCommit": "<pinned commit SHA of huggingface.co/ggerganov/whisper.cpp at manifest creation time>",
  "models": [
    {
      "id": "ggml-tiny.en",
      "displayName": "Tiny (English)",
      "fileName": "ggml-tiny.en.bin",
      "sizeBytes": 78643200,
      "sha256": "<hex>",
      "url": "https://huggingface.co/ggerganov/whisper.cpp/resolve/<commitSha>/ggml-tiny.en.bin",
      "bundled": true
    },
    { "id": "ggml-base.en", ... },
    { "id": "ggml-small.en", ... },
    { "id": "ggml-medium.en", ... },
    { "id": "ggml-large-v3", ... }
  ]
}
```

**URL pinning by commit SHA, not `main`.** HuggingFace's `/resolve/main/{filename}` path is a branch reference that has changed quietly in the past. Pinning to `/resolve/<commitSha>/{filename}` makes the download URL immutable; a manifest update is a deliberate maintenance task that recomputes SHA-256 against the new pinned commit.

### Download flow

1. `HttpClient` GET to manifest URL. Header `Accept-Encoding: identity` (no compression — whisper models are pre-compressed binaries).
2. Stream into `%LOCALAPPDATA%\KusPus\models\{fileName}.tmp`, computing SHA-256 as we go.
3. On completion, compare SHA to manifest entry.
4. SHA match: `File.Replace` `.tmp` → final name. SHA mismatch: delete `.tmp`, surface error, do not activate.
5. Progress events at 1 Hz to the UI.

### Network

- Uses a single `IHttpClientFactory`-backed `HttpClient` named `"kuspus-egress"` configured with the egress allowlist enforcing handler.
- Cancellable; download resumable on next attempt (HTTP `Range` request if `.tmp` exists and partial).

### Custom models

- `settings.json → models.customModelPath`: if non-null and file exists, appears in the picker as "Custom (\<filename\>)".
- No SHA verification (user-managed); a warning shown in Models pane: "Custom models are not verified."

### Active model resolution

```
activeModelId in settings → if "custom" → customModelPath → else → ggml-{activeModelId}.bin in models folder
```

`Result.Fail` if the active model file is missing.

---

## 19. CrashReporter

### Stack

`Sentry.NET` SDK initialized only if `settings.json → privacy.crashReportsOptIn == true` AND `offlineMode == false`.

### Init

```csharp
SentrySdk.Init(o => {
    o.Dsn = "<dsn from build constant>";
    o.AutoSessionTracking = false;
    o.SendDefaultPii = false;
    o.AttachStacktrace = true;
    o.MaxBreadcrumbs = 20;
    o.BeforeSend = ScrubBeforeSend;
});
```

### `ScrubBeforeSend`

Drops every event whose payload contains keys from a denylist (`text`, `transcript`, `clipboard`, `password`, `target_app`, `hwnd`). Replaces all string values containing `%USERPROFILE%`-like prefixes with placeholders.

### Forced disable

If Offline Mode is toggled ON at runtime, the SDK is removed from the global tap-list (`SentrySdk.Close()`) and re-initialised on toggle OFF.

---

## 20. PrefsStore

Described in §9. Public interface:

```csharp
public interface IPrefsStore {
    AppSettings Current { get; }
    IObservable<AppSettings> Changes { get; }
    Task SaveAsync(AppSettings updated);
    Task ReloadFromDiskAsync();
}
```

`AppSettings` is an immutable record; updates create a new instance. The observable emits the new snapshot.

---

## 21. SingleInstanceGuard

### Implementation

```csharp
public sealed class SingleInstanceGuard : IDisposable {
    private readonly Mutex _mutex;
    public bool IsOwner { get; }

    public static SingleInstanceGuard AcquireOrSignal() {
        var mutex = new Mutex(initiallyOwned: true, name: @"Local\KusPus", out bool created);
        if (!created) {
            // Another instance is running — broadcast a wake message.
            uint msg = User32.RegisterWindowMessageW("KusPus.BringMainToFront");
            User32.PostMessageW(HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
        }
        return new SingleInstanceGuard(mutex, created);
    }

    public void Dispose() {
        if (IsOwner) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
```

The running instance's MainWindow listens for the registered message in `WndProc` and calls `Show + Activate + Focus`.

---

# Part IV — UI implementation

## 22. MVVM pattern & WPF layout

- **Pattern:** Model-View-ViewModel via `CommunityToolkit.Mvvm` source generators.
- **No code-behind logic.** Code-behind files contain only `InitializeComponent()` and trivial event-to-command bridges where data binding is impractical (rare; tray icon ContextMenu being one).
- **DataTemplates** map view-model types to views, registered in `App.xaml`:
  ```xml
  <DataTemplate DataType="{x:Type vm:PreferencesViewModel}">
      <views:PreferencesView />
  </DataTemplate>
  ```
- **Navigation** inside MainWindow is view-model-driven: a `ContentControl` bound to `CurrentTabVM` switches automatically.
- **Validation** uses `INotifyDataErrorInfo` for forms (hotkey picker, mic level test).
- **Localization** is not wired in v1, but every user-visible string is sourced from `KusPus.App.Resources.Strings.resx` for future-proofing.
- **Hardcoded-string ban (enforced at compile time).** A Roslyn analyzer fails the build if a string literal appears as the value of a XAML `Content`, `Text`, `ToolTip`, `Header`, or `Title` property, or as an argument to MessageBox/`Dialog.Show*`. The accepted pattern is `{x:Static res:Strings.MyKey}` in XAML or `Strings.MyKey` in code. Implemented via `BannedApiAnalyzers` for the API surface + a small custom analyzer for the XAML attributes. Rationale: makes i18n cheap when it ships, and prevents the "string drift" that bit OpenWhispr-style projects.

---

## 23. MainWindow

### Window

- Resizable, min size 820 × 600.
- Tab bar on the left, content on the right.
- Title bar reuses the system chrome (no custom chrome in v1).

### Tabs

| Tab | View | View-model |
|---|---|---|
| General | `GeneralView` | `GeneralViewModel` |
| Audio | `AudioView` | `AudioViewModel` |
| Models | `ModelsView` | `ModelsViewModel` |
| History | `HistoryView` | `HistoryViewModel` |
| Privacy | `PrivacyView` | `PrivacyViewModel` |
| About | `AboutView` | `AboutViewModel` |

### Special behaviors

- Listening for the `KusPus.BringMainToFront` registered message (single-instance signal).
- Closing the window hides it (does not exit the app). Quit is via tray.
- On first launch, MainWindow auto-shows onboarding instead of tabs.

---

## 24. FloatingPillWindow

### Window construction

```xml
<Window WindowStyle="None"
        AllowsTransparency="True"
        ShowInTaskbar="False"
        Topmost="True"
        Background="Transparent"
        Width="360" Height="64"
        ResizeMode="NoResize">
```

Extended styles set via P/Invoke in code-behind on `SourceInitialized`:

```csharp
int ex = User32.GetWindowLong(hwnd, GWL_EXSTYLE);
ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED;
User32.SetWindowLong(hwnd, GWL_EXSTYLE, ex);
```

### Positioning

```csharp
var cursor = Cursor.Position;       // physical pixels
var hMon   = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
var info   = GetMonitorInfo(hMon);
GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY);
double scale = dpiX / 96.0;
double x = info.WorkAreaCenterX_DIPs - WidthDIP/2;
double y = info.WorkArea.Bottom_DIPs - HeightDIP - 40;
this.Left = x; this.Top = y;
```

### Animations

- **Visualizer** — a `Canvas` of 20 `Rectangle`s, height bound to `Levels[i]` via a converter, animated with WPF `RenderTransform` + `ScaleTransform.Y`.
- **Paste confirmation** — `Opacity` animation: visualizer fades from 1 → 0.2 over 150 ms; "Pasted into X" `TextBlock` fades from 0 → 1 over 150 ms, holds 1000 ms, fades 0 over 150 ms; visualizer restores 0.2 → 1 over 150 ms.
- **Show/hide** — fade `Opacity` 0 ↔ 1 over 120 ms.

### Theming

Resources are sourced from `App.Resources` (light or dark dictionary), swapped on system-theme change (§27).

---

## 25. Tray icon

- Implemented via `H.NotifyIcon.Wpf` (`TaskbarIcon` control in MainWindow.xaml).
- Icon swapped at runtime based on state: idle (gray), recording (red), error (red dot).
- Context menu items declared in XAML and bound to view-model commands.

---

## 26. Onboarding flow

### Architecture

- A single `OnboardingWindow` (modal, not a tab in MainWindow).
- A `Stack<OnboardingStepVM>` for back navigation.
- Each step is a self-contained view-model with `CanGoNext`, `OnEnterAsync`, `OnLeaveAsync`.

### Steps

| # | View-model | Notes |
|---|---|---|
| 1 | `WelcomeStepVM` | One screenshot, three lines of copy, "Next" |
| 2 | `HotkeyPickerStepVM` | Live capture from `IHotkeyEngine`'s raw events; debounce; "Press your chord". Default LCtrl+LWin highlighted. |
| 3 | `MicCheckStepVM` | Try opening default mic; live level meter for 3 s; on permission denied, link to `ms-settings:privacy-microphone` |
| 4 | `AutostartStepVM` | Single toggle (default OFF); writes registry on commit |
| 5 | `CrashReportsStepVM` | Single toggle (default OFF); writes to settings |
| 6 | `TryItStepVM` | Inline text field; user dictates one sentence; shows transcript inline |
| 7 | `DoneStepVM` | Pin-to-tray instructions, "Finish" |

Skipping at any step closes the window, sets `onboarding.completed = false`, and shows a "You can rerun onboarding from About" toast.

---

## 27. Theming & DPI

### Theme detection

- Initial theme: read `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`.
- Watch `WM_SETTINGCHANGE` via `HwndSource.AddHook` on MainWindow; on change, re-read and swap resource dictionary.
- User can override in Preferences (`auto / light / dark`).

### Resource dictionaries

`Themes/Light.xaml` and `Themes/Dark.xaml` define color brushes referenced by all controls. No hardcoded colors anywhere in views.

### DPI

- Application manifest declares `<dpiAwareness>PerMonitorV2</dpiAwareness>`.
- All sizes in XAML are in DIPs (default unit).
- Positioning math (§24) converts between DIPs and physical pixels using the monitor-specific DPI.
- Test matrix includes 100/125/150/175/200 % scaling.

---

# Part V — Native interop

## 28. P/Invoke catalog

Every native function used in the codebase, with its containing class. Lives under `KusPus.Native.PInvoke.*`.

### user32.dll

| Function | Used by | Purpose |
|---|---|---|
| `SetWindowsHookExW` | HotkeyEngine | Install `WH_KEYBOARD_LL` |
| `UnhookWindowsHookEx` | HotkeyEngine | Uninstall hook on stop |
| `CallNextHookEx` | HotkeyEngine | Pass through events we don't consume |
| `GetMessageW` / `TranslateMessage` / `DispatchMessageW` | HotkeyEngine | Hook-thread message loop |
| `SendInput` | HotkeyEngine, PasteEngine | Synthetic Ctrl+V; LWin suppression injection |
| `GetForegroundWindow` | PasteEngine | Capture target HWND |
| `SetForegroundWindow` | PasteEngine | Restore focus pre-paste |
| `AllowSetForegroundWindow` | PasteEngine | Defensive retry when shell-level foreground lock is in effect |
| `AttachThreadInput` | PasteEngine | Foreground-restore trick |
| `GetWindowThreadProcessId` | PasteEngine | Resolve target process |
| `GetWindowLong` / `SetWindowLong` (`GetWindowLongPtrW` / `SetWindowLongPtrW` for 64-bit) | FloatingPillWindow | Set extended window styles |
| `SetWindowPos` | FloatingPillWindow | Topmost positioning |
| `RegisterWindowMessageW` | SingleInstanceGuard, MainWindow | Custom IPC message |
| `PostMessageW` | SingleInstanceGuard | Broadcast wake to existing instance |
| `MonitorFromPoint` | FloatingPillWindow | Resolve target monitor |
| `GetMonitorInfoW` | FloatingPillWindow | Read work area |
| `EnumDisplayMonitors` | (future, multi-monitor tests) | — |

### kernel32.dll

| Function | Used by | Purpose |
|---|---|---|
| `GetCurrentThreadId` | PasteEngine | AttachThreadInput first arg |
| `OpenProcess` | PasteEngine | Open target for `GetModuleFileNameEx` |
| `CloseHandle` | PasteEngine, WhisperRunner | Clean up handles; closing Job handle kills its processes |
| `CreateJobObjectW` | WhisperRunner | Create a per-app Job Object for whisper subprocess containment |
| `SetInformationJobObject` | WhisperRunner | Set `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` |
| `AssignProcessToJobObject` | WhisperRunner | Attach a started whisper.exe to the Job |

### psapi.dll (or kernel32 since Win7)

| Function | Used by | Purpose |
|---|---|---|
| `K32GetModuleFileNameExW` | PasteEngine | Resolve target app exe path |

### shcore.dll

| Function | Used by | Purpose |
|---|---|---|
| `GetDpiForMonitor` | FloatingPillWindow | Per-monitor DPI |

### dwmapi.dll

| Function | Used by | Purpose |
|---|---|---|
| `DwmSetWindowAttribute` (with `DWMWA_SYSTEMBACKDROP_TYPE`, Win11) | FloatingPillWindow | Optional Mica/Acrylic |
| `DwmExtendFrameIntoClientArea` | FloatingPillWindow | Optional blur on Win10 |

### Wrapper style

All declarations are in `KusPus.Native.PInvoke` as `static partial class` per DLL using **LibraryImport** (source-generated, available since .NET 7; we target .NET 10) rather than `DllImport`. Example:

```csharp
internal static partial class User32 {
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessageW(string lpString);
}
```

LibraryImport gives us AOT-compatible source-generated marshalling, avoiding the slower DllImport stubs.

---

# Part VI — Build, install, ship

## 29. Whisper.cpp build pipeline

### Source

Submodule at `third_party/whisper.cpp`, pinned to a specific SHA. Update is a deliberate maintenance task documented in `BUILD.md`.

### Build script

`tools/build-whisper-windows.ps1` (PowerShell):

1. Detect MSVC via `vswhere`.
2. Enter VS Developer Environment via `Enter-VsDevShell`.
3. `cmake -B build -DGGML_NATIVE=OFF -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=ON -DCMAKE_BUILD_TYPE=Release`
   - `GGML_NATIVE=OFF` is critical: builds for a portable x86-64-v2 baseline, not the build machine's specific CPU. Without this we'd ship a binary that crashes on older CPUs.
4. `cmake --build build --config Release --target whisper-cli`
5. Copy outputs:
   - `build/Release/whisper-cli.exe` → `installer/payload/whisper/whisper.exe`
   - `build/bin/Release/*.dll` (whisper, ggml*) → `installer/payload/whisper/`
6. Compute SHA-256 of each binary; write to `installer/payload/whisper/SHA256SUMS`.
7. Smoke test: run `whisper.exe -h` and assert exit code 0.

### CI integration

`tools/build-whisper-windows.ps1` runs on every push that modifies `third_party/whisper.cpp` or the script itself. Output binaries are cached in GitHub Actions cache keyed by submodule SHA so daily builds are fast.

---

## 30. Inno Setup installer script

### File: `installer/KusPus.iss`

Key behaviors:

```ini
[Setup]
AppId={{ {STABLE_GUID_HERE} }}
AppName=KusPus
AppVersion={#AppVersion}
AppPublisher=KusPus
DefaultDirName={autopf}\KusPus
DefaultGroupName=KusPus
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
OutputBaseFilename=KusPus-Setup-{#AppVersion}
SetupIconFile=assets\kuspus.ico
WizardStyle=modern
UninstallDisplayIcon={app}\KusPus.exe

[Files]
Source: "..\src\KusPus.App\bin\Release\net10.0-windows\win-x64\publish\KusPus.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\whisper\*"; DestDir: "{app}\whisper"; Flags: recursesubdirs ignoreversion
Source: "payload\models\ggml-tiny.en.bin"; DestDir: "{localappdata}\KusPus\models"; Flags: external onlyifdoesntexist

[Icons]
Name: "{group}\KusPus"; Filename: "{app}\KusPus.exe"
Name: "{commondesktop}\KusPus"; Filename: "{app}\KusPus.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\KusPus.exe"; Description: "Launch KusPus"; Flags: nowait postinstall skipifsilent
```

### Decisions

- **PrivilegesRequired=lowest** — install per-user by default. UAC elevation is offered as an option, not required. Per-machine install lives in Program Files; per-user in `%LOCALAPPDATA%\Programs\KusPus`.
- **No registry HKLM writes** — everything lives under HKCU or in user folders.
- **Autostart is NOT written by the installer.** It's written by the app itself when the user opts in during onboarding, into `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- **Tiny.en bundled as `external onlyifdoesntexist`** — doesn't overwrite a model the user already has.

### Uninstall behavior

- Removes everything in `{app}`.
- Leaves `%LOCALAPPDATA%\KusPus\` and `%APPDATA%\KusPus\` intact.
- Offers a "Also remove my settings, history, and models?" checkbox in the uninstaller; if checked, deletes user folders too.
- Removes the autostart Run key.

---

## 31. CI/CD

### `.github/workflows/ci.yml`

Triggers: push to `main`, every PR.

Jobs:
- **build-managed**: Windows runner, .NET 10 SDK, `dotnet restore` + `dotnet build -c Release` + `dotnet test`.
- **lint**: `dotnet format --verify-no-changes`, `tools/verify-egress-allowlist.ps1`.
- **build-whisper** (conditional on submodule change): runs `build-whisper-windows.ps1`; caches output.

### `.github/workflows/release.yml`

Trigger: push of a tag matching `v*`.

Jobs:
1. `build-managed` (publish, single-file).
2. `build-whisper` (or restore from cache).
3. `package`: copy publish output + whisper payload + tiny.en (downloaded from HuggingFace + SHA-verified) into `installer/payload/`.
4. `iscc installer/KusPus.iss` (Inno Setup compiler).
5. Compute SHA-256 of `KusPus-Setup-{tag}.exe`.
6. Create GitHub Release with the `.exe` and `.exe.sha256` as assets, body sourced from `CHANGELOG.md`.

---

## 32. Release procedure

Documented in `RELEASING.md`:

1. Update `CHANGELOG.md`.
2. Bump version in `Directory.Build.props`.
3. Run local smoke test: `dotnet test`, `tools/build-whisper-windows.ps1`, F5 KusPus, dictate one sentence into Notepad.
4. `git tag v1.0.0 && git push --tags`.
5. Wait for `release.yml` to complete.
6. Manually verify the GitHub Release: download the installer on a clean Windows 10 VM, install, run, dictate.
7. Update `INSTALL.md` with any new SmartScreen / Defender screenshots.

---

# Part VII — Testing

## 33. Test strategy

### Unit tests (automated)

Targets:
- `KusPus.Core.Tests` — state machine transitions; settings (de)serialization; result types.
- `KusPus.Persistence.Tests` — SQLite migrations; FTS search; settings file round-trip; settings migration chain.
- `KusPus.Whisper.Tests` — model manifest parsing; SHA verification; process timeout; argument formatting.

Conventions:
- xUnit 2.x.
- `FluentAssertions` for readable asserts.
- No mocking framework — hand-written fakes for interfaces.
- One file per system-under-test (`AppCoordinatorTests.cs`, etc.).

### Integration tests (semi-automated)

`tests/integration/`:
- Run actual `whisper.exe` against a checked-in 5-second WAV fixture; assert transcript contains expected substring.
- Spawn KusPus.exe in a child process and drive it via a test-only IPC pipe; assert state transitions over fake input events.

### What we don't test automatically (yet)

- UI rendering. WPF testing harness is heavy. Manual matrix below.
- LL keyboard hook behavior. Cannot be tested headlessly. Manual matrix.
- WASAPI capture. Requires a physical mic. Manual matrix.

### Coverage target

No numeric target. Critical-path code (state machine, paste sequence, model SHA) must have explicit tests; everything else is opportunistic.

---

## 34. Manual test matrix

Run before each release. Tester walks through every row.

| # | Scenario | Expected | Pass criterion |
|---|---|---|---|
| M-01 | Fresh install on Win10 22H2 VM | Installs in < 3 min including SmartScreen | Installer exits 0; tray icon appears |
| M-02 | Fresh install on Win11 23H2 VM | Same as M-01 | Same |
| M-03 | First launch shows onboarding | Onboarding window appears | All 7 steps complete |
| M-04 | Hotkey picker registers new chord | Can rebind to e.g. Right Alt | Subsequent dictation uses new chord |
| M-05 | Mic check shows live levels | Levels move when speaking | — |
| M-06 | Tap dictation into Notepad | Press+release < 250 ms; pill stays; speak; tap again; transcript pasted | Transcript text in Notepad caret position |
| M-07 | Hold dictation into Notepad | Press+hold; speak; release; transcript pasted | Transcript text in Notepad caret position |
| M-08 | Paste into Chrome address bar | Hold-dictate "anthropic dot com"; release | Caret-position paste works |
| M-09 | Paste into Windows Terminal | Hold-dictate "ls dash la"; release | Paste lands in active terminal |
| M-10 | Paste into VS Code | Hold-dictate; release | Caret-position paste (NOT IntelliSense triggered) |
| M-11 | Paste into Slack | Hold-dictate; release | Text appears, NO Enter pressed |
| M-12 | Paste into Discord | Same | Same |
| M-13 | LWin release after chord does NOT open Start menu | Engage chord; release LWin | Start menu does not appear |
| M-14 | Toggle Offline Mode then attempt model download | Model download blocked | Clear error message |
| M-15 | Disable mic in Privacy settings; try hotkey | Pill shows "Microphone access blocked" | — |
| M-16 | Pull network cable mid-download | Download fails gracefully | Retry button works after reconnect |
| M-17 | Quit from tray menu | Process exits | No `KusPus.exe` in Task Manager |
| M-18 | Launch second instance | First instance's MainWindow surfaces | Only one `KusPus.exe` running |
| M-19 | 4K monitor at 200% scaling | Pill renders crisp, correctly positioned | — |
| M-20 | Two monitors at different DPIs | Pill appears on the monitor with cursor; correct DPI | — |
| M-21 | Theme switch from light to dark mid-session | App theme updates | No restart needed |
| M-22 | Disconnect default mic mid-recording | Recording aborts cleanly | "Mic changed — try again" in pill |
| M-23 | Disk near-full | Disk-full message; no crash | — |
| M-24 | Recording > 50 minutes | Auto-stops at cap | "Recording capped at 50 min" |
| M-25 | Whisper subprocess killed externally | Treated as failure; pill shows error | Failed `.wav` retained in `failed\` |
| M-26 | Uninstall + reinstall | Settings/history preserved | Reinstalled instance loads previous state |
| M-27 | Uninstall with "Remove my data" checked | `%LOCALAPPDATA%\KusPus` and `%APPDATA%\KusPus` removed | — |
| M-28 | Drop custom model into `models\` and edit `settings.json` | Custom model appears as "Custom" option | Activation works |
| M-29 | Press Caps Lock during onboarding hotkey picker | Picker rejects Caps Lock (or accepts gracefully) | Per chosen behavior |
| M-30 | Foreground window closes between chord-release and paste | "Window gone — text in clipboard" | Clipboard contains transcript |
| M-31 | Install on Win11 with Smart App Control **enabled** | SAC blocks the installer outright | Tester sees the SAC block dialog; INSTALL.md walks them through toggling SAC off |
| M-32 | Install on Win11 with Smart App Control **disabled** (April 2026+ build) | Installs normally; no SAC interference | — |
| M-33 | Force hook removal (manually exceed `LowLevelHooksTimeout` via debugger pause > 5 s) | Watchdog detects within 30 s, re-installs hook, surfaces "Hotkey reconnected" pill notice | Dictation works again after watchdog re-arms |
| M-34 | `SetForegroundWindow` returns FALSE on first attempt (simulate via debugger or shell lock) | PasteEngine retries with `AllowSetForegroundWindow(ASFW_ANY)`; succeeds on second attempt | Paste lands in target |
| M-35 | Launch a Vanguard / EAC / BattlEye-protected game with KusPus running | Game refuses to launch OR launches but disables KusPus's hook | Documented limitation; tray menu offers quick-quit |
| M-36 | Run KusPus inside an RDP session | Recording aborts cleanly on RDP attach/detach with "Mic changed — try again" | Documented limitation; no crash |
| M-37 | Tamper with `whisper\whisper.exe` (overwrite with a different byte) | First transcription attempt returns "Whisper binary integrity check failed — please reinstall" | Hard stop; subsequent attempts return the same error |

---

## 35. Performance budget

| Metric | Budget | Measured how |
|---|---|---|
| Cold start to tray-icon visible | < 1.5 s on mid-range laptop | Stopwatch in App startup |
| Warm start (single-instance signal) | < 200 ms | Same |
| Chord-press to recording-started (pill visible) | < 100 ms | Logged timestamps |
| Chord-release to whisper.exe spawned | < 100 ms | Logged timestamps |
| Whisper transcribe time (5 s of audio, tiny.en, mid CPU) | ≤ 2 s | Whisper stderr timing |
| Transcribe-complete to paste-fired | < 200 ms | Logged timestamps |
| Idle RSS | < 100 MB | Task Manager |
| Recording RSS | < 200 MB | Task Manager |
| Visualizer refresh | 15 Hz (target), no UI thread blocking | Profiler |
| Pill show/hide animation | 60 fps | DevTools / WPF rendering profile |
| Installer size | ≤ 240 MB | File size |
| Self-contained EXE size | ≤ 120 MB | File size |

Anything beyond budget on a real tester machine is a P1 bug.

---

# Part VIII — Appendices

## A. Class diagrams (textual)

```
AppCoordinator
  ├─ uses → IHotkeyEngine        (subscribes to Events; calls Start/Stop)
  ├─ uses → IAudioRecorder       (calls StartAsync/StopAsync)
  ├─ uses → IWhisperRunner       (calls TranscribeAsync)
  ├─ uses → IPasteEngine         (calls CaptureForegroundHwnd, DeliverAsync)
  ├─ uses → IHistoryStore        (calls AppendAsync)
  ├─ exposes → IObservable<AppState>  (bound by FloatingPillVM)
  └─ exposes → IObservable<PillContent> (visualizer + paste confirmation)

PrefsStore
  ├─ reads/writes %APPDATA%\KusPus\settings.json
  ├─ owns FileSystemWatcher
  └─ exposes IObservable<AppSettings>

ModelManager
  ├─ uses IPrefsStore (active model id, custom model path)
  ├─ uses HttpClient (allowlisted)
  └─ exposes IObservable<DownloadProgress>
```

## B. State machine sequence (chord-hold dictation into Slack)

```
t=0      User presses LCtrl
t=2ms    HotkeyEngine: modifiers={LCtrl}
t=12ms   User presses LWin (chord complete)
t=13ms   HotkeyEngine: ChordEngaged → channel
t=14ms   Coordinator: state Idle → Armed; capture HWND = SlackHwnd; start 250ms timer
t=264ms  Timer fires → HoldThresholdElapsed → channel
t=265ms  Coordinator: state Armed → Recording (hold-mode); start audio; show pill (NOACTIVATE)
t=265ms..3000ms  Audio captured to %TEMP%\kuspus-N.wav; visualizer at 15Hz
t=3000ms User releases LWin
t=3001ms HotkeyEngine: ChordReleased → channel; LWin keyup SUPPRESSED + stray Ctrl injected
t=3002ms Coordinator: state Recording → Transcribing; stop audio
t=3050ms whisper.exe spawned
t=3850ms whisper.exe exits 0; reads .wav.txt
t=3860ms Coordinator: TranscribeComplete event
t=3862ms PasteEngine: Clipboard.SetText(transcript)
t=3868ms PasteEngine: AttachThreadInput + SetForegroundWindow(SlackHwnd)
t=3870ms PasteEngine: SendInput Ctrl+V
t=3880ms FloatingPillVM: show "Pasted into Slack" overlay
t=4880ms FloatingPillVM: fade out
t=5000ms Coordinator: state Transcribing → Idle; pill hidden; HistoryStore.AppendAsync fired
```

End-to-end latency: ~860 ms after release (for 3 s of audio on tiny.en).

## C. Folder & file inventory (post-install, x64 per-user)

```
%LOCALAPPDATA%\Programs\KusPus\
├── KusPus.exe
├── whisper\
│   ├── whisper.exe
│   ├── whisper.dll
│   └── ggml*.dll
└── unins000.exe   (Inno Setup uninstaller)

%LOCALAPPDATA%\KusPus\
├── models\
│   └── ggml-tiny.en.bin
├── history.db
├── history.db.bak       (auto-rolling)
├── logs\
│   └── kuspus-2026-05-16.log
└── failed\              (empty until something fails)

%APPDATA%\KusPus\
└── settings.json

HKCU\Software\Microsoft\Windows\CurrentVersion\Run
└── KusPus = "C:\Users\...\KusPus.exe"     (only if user opted in)

HKCU\Software\KusPus\
└── (reserved for future per-machine state; nothing in v1)
```

---

# Part XIV — Open engineering questions

These are decisions deferred until first-implementation evidence forces the hand.

| # | Question | When we'll decide |
|---|---|---|
| EQ-01 | Should the LWin-suppression injection use `SendInput` or `keybd_event`? Both work; SendInput is the modern call but `keybd_event` is sometimes more reliable inside an LL hook callback. | First failure of the Start-menu-doesn't-open test (M-13) |
| EQ-02 | Do we ship a `KusPus.exe` console subcommand for diagnostics (`KusPus.exe --diag`)? | If support burden from testers exceeds 30 min/week |
| ~~EQ-03~~ | **RESOLVED:** `PublishReadyToRun=true`, `EnableCompressionInSingleFile=false`. Compression and R2R partially defeat each other (decompression on launch erases the pre-jit gain). For a desktop utility opened once and left running, cold-start time beats installer size by a comfortable margin. See §3 Publish profile. | Resolved 2026-05-16 |
| EQ-04 | Use `H.NotifyIcon.Wpf` or roll our own `Shell_NotifyIconW` wrapper? Currently picking `H.NotifyIcon.Wpf`. | If `H.NotifyIcon` has a blocking bug for our use case |
| EQ-05 | `Microsoft.Data.Sqlite` raw vs `Dapper` thin wrapper? Currently raw. | If query-writing burden grows |
| EQ-06 | `NAudio` vs `CSCore` for WASAPI? Currently NAudio. | If NAudio resampling is audibly worse than the Mac-side equivalent |
| EQ-07 | Whisper output: parse stdout in real time for progress, or just side-file at the end? Currently end-only. | If we want a "transcribing... 40%" indicator in v1 |
| EQ-08 | Friendly app-name map: hardcoded JSON vs `FileVersionInfo.ProductName` extracted at runtime? Currently hardcoded with fallback. | If maintenance burden of the hardcoded map exceeds 10 entries |
| ~~EQ-09~~ | **RESOLVED:** Whisper subprocess is wrapped in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. See §15 "Job Object containment". `Process.Kill(entireProcessTree: true)` is now the second line of defence, not the primary. | — |
| EQ-10 | Acrylic/Mica on Win10: do we ship a polyfill or accept a solid-color fallback? Currently solid fallback. | After visual review on Win10 vs Win11 |
| EQ-11 | Should the `failed\` folder be a SQLite blob column instead of files on disk? Currently files. | If `failed\` becomes a hassle for users to clean up |
| EQ-12 | Use `IObservable` + `System.Reactive` for state observation or hand-rolled events? Currently observables. | If `System.Reactive` size hurts trim/AOT path later |

---

*End of TECH_SPEC.md v0.2. Next revision: after the first implementation spike resolves EQ-01, EQ-02, EQ-04.*
