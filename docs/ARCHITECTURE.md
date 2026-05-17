# KusPus architecture

One-page overview of the v1.0 process model. Source code is the authoritative architectural contract — the per-project READMEs in `src/KusPus.*/` describe each module's responsibilities and dependencies.

## Solution layout

Six projects under `src/`, one xunit project per source project under `test/`:

| Project | Role |
|---|---|
| `KusPus.Core` | Pure, headless. FSM, `Result<T>`, `AppSettings`, egress allowlist, crash-scrubbing helpers. |
| `KusPus.Persistence` | SQLite + FTS5 history, `settings.json` round-tripping. |
| `KusPus.Whisper` | `whisper.exe` subprocess driver + model manager (HTTPS, pinned URLs, SHA-256). |
| `KusPus.Audio` | WASAPI capture via NAudio, 15 Hz RMS level stream, device-change. |
| `KusPus.Native` | P/Invoke layer: low-level keyboard hook, `SendInput` paste, Job Object container. |
| `KusPus.App` | WPF composition root — DI, tray, floating pill, MainWindow, onboarding, Sentry wiring. |

Solution file is `KusPus.slnx` (the .NET 10 default XML solution format). Build constraint: `TreatWarningsAsErrors=true`, `Nullable=enable`, analysis level latest.

## Component map (single process)

```
KusPus.exe (WPF, single instance)

  TrayManager  ─┐
  MainWindow   ─┤
  FloatingPill ─┴──> AppCoordinator (FSM: Idle ↔ Armed ↔ Recording ↔ Transcribing ↔ Pasting ↔ Idle)
                       │
                       ├──> HotkeyEngine    (WH_KEYBOARD_LL, chord state machine, Rx subject)
                       ├──> AudioRecorder   (WASAPI → 16 kHz mono WAV, 50-min cap)
                       ├──> WhisperRunner   (subprocess in Job Object, integrity-checked exe)
                       ├──> PasteEngine     (clipboard write + foreground restore + Ctrl+V via SendInput)
                       ├──> HistoryStore    (single SqliteConnection guarded by SemaphoreSlim, FTS5)
                       ├──> ModelManager    (HuggingFace download, pinned commit URLs, SHA-256 verify)
                       ├──> PrefsStore      (atomic settings.json + Rx Changes stream)
                       └──> CrashReporter   (Sentry SDK, default OFF, path-scrubbing)
```

All cross-component coordination flows through Rx subjects on the `AppCoordinator`. Five thread contexts:

1. **UI dispatcher** — WPF.
2. **Hook thread** — dedicated message-loop STA thread owned by `HotkeyEngine`.
3. **Audio capture** — WASAPI callback + a worker thread that drains its ring buffer.
4. **Whisper subprocess** — owns its own threads; we just watch stdin/stdout.
5. **Persistence task queue** — `Channel<>`-fed worker for history writes.

## On-disk layout

```
{app}\                                       %LOCALAPPDATA%\Programs\KusPus\  (per-user install)
  KusPus.exe                                 self-contained single-file publish (~86 MB)
  whisper\
    whisper.exe + *.dll + SHA256SUMS         CPU build, pinned to whisper.cpp v1.8.4
    models\
      ggml-tiny.en.bin                       bundled with installer (~75 MB)
      ggml-base.en.bin                       downloaded on demand
      …

%APPDATA%\KusPus\
  settings.json                              user prefs, atomic write via temp+rename

%LOCALAPPDATA%\KusPus\
  history.db                                 SQLite + FTS5
  logs\                                      Serilog file sink, rolling
  failed\                                    .wav + .txt of failed transcribes (user-purgeable)
```

`{app}\whisper\models\` (under the install directory) is the runtime models path. Earlier builds used `%LOCALAPPDATA%\KusPus\models\`; that path is shadowed by Windows Defender Controlled Folder Access on unsigned binaries — moving models into `{app}` sidesteps it without per-machine whitelisting.

## Network egress

Two allowlisted hosts. Both gated by `EgressAllowlistHandler` regardless of UI state:

- `huggingface.co` — model downloads, user-initiated only.
- `*.sentry.io` (host ends in `.sentry.io` AND contains an `ingest` label) — opt-in crash reports. Default OFF. Covers regional ingest (`o<org>.ingest.de.sentry.io`, `…us.sentry.io`).

The **Offline Mode** toggle in Preferences → Privacy short-circuits both. Belt-and-suspenders: `CrashReporter.ShutdownSdk()` runs on Offline-Mode flips so any in-flight Sentry upload is also blocked. See `src/KusPus.Core/Networking/EgressPolicy.cs` + `src/KusPus.App/EgressAllowlistHandler.cs`.

## Bundled subprocess

`whisper.exe` + the runtime DLLs + `SHA256SUMS` are populated by `tools/build-whisper-windows.ps1` (downloads the pinned `whisper-bin-x64.zip` from the upstream GitHub release). Release builds embed the expected SHA-256 as a build-time constant (`BuildConstants.ExpectedWhisperSha256`, MSBuild target `EmitWhisperShaConstant`); `WhisperRunner` refuses to launch a binary whose hash doesn't match. Dev builds with an empty constant skip the check.

The subprocess runs inside a Win32 Job Object configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so a crashed or killed parent reliably tears the child down too. `KusPus.Native.JobObjectContainer` owns the handle.
