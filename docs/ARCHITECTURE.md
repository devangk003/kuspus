# KusPus architecture

One-page overview of the process model. Source code is the authoritative architectural contract.

## Solution layout

One repo, two native apps. The platform-neutral core is shared; each OS has its own UI and OS-services layer behind the `KusPus.Core.Platform` interfaces (`IHotkeyEngine`, `IAudioRecorder`, `IPasteEngine`, `IClipboardWriter`, `IProcessContainer`, `IUiDispatcher`).

| Project | TFM | Role |
|---|---|---|
| `KusPus.Core` | net10.0 | Pure, headless. FSM, `Result<T>`, `AppSettings`, egress allowlist, crash-scrubbing helpers, the platform interfaces, the shared `ChordTracker`. |
| `KusPus.Coordination` | net10.0 | `AppCoordinator` (FSM ↔ live services), `EgressAllowlistHandler`, `CrashReporter`, `TranscriptFilter`. |
| `KusPus.Persistence` | net10.0 | SQLite + FTS5 history, `settings.json` round-tripping. |
| `KusPus.Whisper` | net10.0 | `ITranscriber` contract, whisper subprocess driver (`whisper.exe` / `whisper-cli`) + model manager (multi-file manifest, HTTPS, pinned URLs, SHA-256, bundled read-only dir). |
| `KusPus.Parakeet` | net10.0 | In-process NVIDIA Parakeet-TDT engine on sherpa-onnx (`org.k2fsa.sherpa.onnx` NuGet + per-RID native runtime), `WavReader`, and `EngineRouter` that picks an engine from `ModelDescriptor.Engine`. |
| `KusPus.Audio` | net10.0-windows | WASAPI capture via NAudio, 15 Hz RMS level stream, device-change. |
| `KusPus.Native` | net10.0-windows | P/Invoke layer: low-level keyboard hook, `SendInput` paste, Job Object container. |
| `KusPus.App` | net10.0-windows | WPF composition root — DI, tray, floating pill, MainWindow, onboarding. |
| `KusPus.Mac.Platform` | net10.0-macos | `CGEventTap` hotkey (listen-only, self-healing), `AVAudioEngine` capture, `NSPasteboard` + `CGEvent` ⌘V paste, process containment, `SMAppService` login item, TCC permission helpers. |
| `KusPus.Mac.App` | net10.0-macos | AppKit composition root — status item, notch island `NSPanel` (Dynamic-Island style indicator in the notch), Preferences, onboarding. |

Solution filters: `KusPus.Windows.slnf`, `KusPus.Mac.slnf`, `KusPus.Shared.slnf`.

Solution file is `KusPus.slnx` (the .NET 10 default XML solution format). Build constraint: `TreatWarningsAsErrors=true`, `Nullable=enable`, analysis level latest, on every project including the Mac ones.

## Component map (single process, per OS)

```
KusPus.exe (WPF)  /  KusPus.app (AppKit)   — single instance

  Tray / StatusItem ─┐
  Preferences       ─┤
  FloatingPill      ─┴──> AppCoordinator (FSM: Idle ↔ Armed ↔ Recording ↔ Transcribing → Idle)
                            │
                            ├──> IHotkeyEngine     Win: WH_KEYBOARD_LL hook        Mac: CGEventTap (Input Monitoring)
                            ├──> IAudioRecorder    Win: WASAPI/NAudio               Mac: AVAudioEngine + AVAudioConverter
                            ├──> ITranscriber      EngineRouter → by ModelDescriptor.Engine
                            │      ├ WhisperRunner        subprocess, integrity-checked    Win: whisper.exe  Mac: whisper-cli (Metal)
                            │      └ ParakeetTranscriber  in-process sherpa-onnx (CPU int8), recognizer cached, idle-unloaded after 10 min
                            ├──> IPasteEngine      Win: SetForegroundWindow+Ctrl+V  Mac: NSRunningApplication.activate + CGEvent ⌘V (Accessibility)
                            ├──> IProcessContainer Win: Job Object                  Mac: kill-on-exit tracker
                            ├──> HistoryStore      SQLite + FTS5 (shared)
                            ├──> ModelManager      HuggingFace download, SHA-256 verify (shared)
                            ├──> PrefsStore        atomic settings.json + Rx Changes (shared)
                            └──> CrashReporter     Sentry, default OFF, path-scrubbing (shared)
```

Both FSM steps and side-effect dispatch run on the UI thread via `IUiDispatcher` (WPF `Dispatcher` / GCD main queue). Hook/tap events arrive on a dedicated thread and are marshalled; whisper and paste run as `Task.Run` work with their inputs captured first. A `RecordingFailed` event routes capture errors (mic blocked, device changed) back to Idle through the same failure path as transcription errors.

## On-disk layout

Windows:

```
{app}\                                       %LOCALAPPDATA%\Programs\KusPus\  (per-user install)
  KusPus.exe                                 self-contained single-file publish (~86 MB)
  whisper\
    whisper.exe + *.dll + SHA256SUMS         CPU build, pinned to whisper.cpp v1.8.4
    models\
      ggml-tiny.en.bin                       bundled with installer (~75 MB)
      ggml-base.en.bin                       downloaded on demand
      parakeet-tdt-0.6b-v2\                  multi-file engines get a folder: encoder/decoder/joiner .onnx + tokens.txt
      …
  sherpa-onnx-c-api.dll · onnxruntime.dll    Parakeet runtime, shipped beside the exe (excluded from the single-file bundle)

%APPDATA%\KusPus\
  settings.json                              user prefs, atomic write via temp+rename

%LOCALAPPDATA%\KusPus\
  history.db                                 SQLite + FTS5
  logs\                                      Serilog file sink, rolling
  failed\                                    .wav + .txt of failed transcribes (user-purgeable)
```

`{app}\whisper\models\` (under the install directory) is the runtime models path. Earlier builds used `%LOCALAPPDATA%\KusPus\models\`; that path is shadowed by Windows Defender Controlled Folder Access on unsigned binaries — moving models into `{app}` sidesteps it without per-machine whitelisting.

macOS:

```
KusPus.app/Contents/
  MacOS/KusPus                               apphost + CoreCLR (self-contained, osx-arm64)
  Resources/whisper/
    whisper-cli + *.dylib + SHA256SUMS       Metal build of whisper.cpp v1.8.4 (built from source)
    models/ggml-tiny.en.bin                  bundled, read-only (a signed bundle must not be modified)

  MonoBundle/libsherpa-onnx-c-api.dylib + libonnxruntime.dylib   Parakeet runtime (signed with the bundle)

~/Library/Application Support/KusPus/
  settings.json · history.db · models/ (downloads; Parakeet models in models/<id>/) · failed/
~/Library/Logs/KusPus/                       Serilog file sink, rolling
```

`ModelManager` resolves a model in the writable `models/` first, then the bundled read-only dir. A manifest entry (`models.json`, schema 2) is a list of files plus an `engine`; every file is downloaded resumably and SHA-256-verified on its own, and a model counts as installed only when all of them are present.

## Engines

| Engine | Models | Runs as | Notes |
|---|---|---|---|
| whisper.cpp v1.8.4 | `ggml-*.bin` (tiny.en bundled) | subprocess (`whisper.exe` / `whisper-cli`), integrity-hashed | English hard-coded (`-l en`), 5-min timeout, Job Object / kill-on-exit containment |
| NVIDIA Parakeet-TDT 0.6B (CC-BY-4.0) via sherpa-onnx 1.13.8 | `parakeet-tdt-0.6b-v2` (English), `-v3` (25 European languages, auto-detected) — int8 ONNX from the sherpa-onnx HF mirrors | in-process (`libsherpa-onnx-c-api` + `onnxruntime`, CPU) | Punctuation + casing in the output; transducer decoding doesn't hallucinate on silence. ~1 GB resident once loaded; the recognizer is cached per model and released after 10 idle minutes. |

`EngineRouter` is the `ITranscriber` everything else talks to (coordinator, onboarding "try it", Preferences test transcription).

## Network egress

Two allowlisted hosts. Both gated by `EgressAllowlistHandler` regardless of UI state:

- `huggingface.co` — model downloads, user-initiated only.
- `*.sentry.io` (host ends in `.sentry.io` AND contains an `ingest` label) — opt-in crash reports. Default OFF. Covers regional ingest (`o<org>.ingest.de.sentry.io`, `…us.sentry.io`).

The **Offline Mode** toggle in Preferences → Privacy short-circuits both. Belt-and-suspenders: `CrashReporter.ShutdownSdk()` runs on Offline-Mode flips so any in-flight Sentry upload is also blocked. See `src/KusPus.Core/Networking/EgressPolicy.cs` + `src/KusPus.App/EgressAllowlistHandler.cs`.

## Bundled subprocess

Windows: `whisper.exe` + the runtime DLLs + `SHA256SUMS` are populated by `tools/build-whisper-windows.ps1` (downloads the pinned `whisper-bin-x64.zip` from the upstream GitHub release). macOS: `tools/build-whisper-macos.sh` builds `whisper-cli` from the `third_party/whisper.cpp` submodule at the same tag with Metal enabled (upstream ships no Mac CLI). Both write `SHA256SUMS`; the shared MSBuild target in `build/WhisperSha.targets` embeds the subprocess hash as `BuildConstants.ExpectedWhisperSha256`, and `WhisperRunner` refuses to launch a binary whose hash doesn't match. Dev builds with an empty constant skip the check.

On Windows the subprocess runs inside a Win32 Job Object configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so a crashed or killed parent reliably tears the child down too. macOS has no Job Objects; `MacProcessContainer` kills tracked children on exit/SIGTERM and the 5-minute whisper timeout bounds any orphan.

## macOS permissions

Three separate TCC grants, each requested from onboarding and deep-linked to its System Settings pane: **Microphone**, **Input Monitoring** (the listen-only event tap), **Accessibility** (posting ⌘V). Without Accessibility the paste engine degrades to clipboard-only and the pill says so. The app runs without the App Sandbox because the sandbox blocks `CGEventPost` to other processes; it ships with the hardened runtime and `com.apple.security.cs.allow-jit` + `device.audio-input`. Grants are tied to the code signature, so ad-hoc dev builds lose them on every rebuild.
