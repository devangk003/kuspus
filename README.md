<div align="center">
  <img src="icons/icon.svg" alt="KusPus logo" width="120" />

  # KusPus

  > Local-only voice-to-text for Windows. Press a hotkey, speak, paste anywhere.
</div>

KusPus is a floating, hotkey-driven dictation app for Windows. Hold **Left Ctrl + Left Win**, speak, release. The transcript pastes into whatever app you're in — terminal, browser, Slack, IDE, anywhere with a text field. Everything runs on your machine. No cloud, no account, no telemetry.

A from-scratch Windows rewrite of **WhisprFlow / FloatingRecorder** for macOS, built in C# + WPF on .NET 10, using [whisper.cpp](https://github.com/ggerganov/whisper.cpp) for transcription. CPU-only — no GPU required.

**Status:** v1.0.0 release-candidate. Installer downloadable from [Releases](https://github.com/devangk003/kuspus/releases). See [docs/ROADMAP.md](docs/ROADMAP.md) for what's deferred to v1.1+.

---

## Features

- **Push-to-talk** — hold the chord, speak, release; transcript pastes wherever your cursor is.
- **Toggle Recording [BETA]** — tray menu / pill button alternative for hands-free start/stop.
- **Paste-anywhere** — works in any text field via clipboard + simulated `Ctrl+V`. Tested in terminals, browsers, IDEs, Slack, Word, Notepad.
- **Floating pill UI** — sits at the bottom-center of your active monitor. Shows recording state, a damped audio visualizer, post-paste confirmation. Draggable. Pin to lock position + go into compact mode.
- **Custom WPF tray menu** — matches the app's design system, not the default Windows context strip. State-aware tray icon (idle / recording / error).
- **Local transcription** — `tiny.en` whisper model bundled (~75 MB) so first launch works offline. Bigger models (`base.en`, `small.en`, `medium.en`, `large-v3`) downloadable from the Models tab.
- **Searchable history** — SQLite + FTS5 store of past transcripts. User-purgeable from the History tab. Toggleable off in Preferences.
- **Themes** — Dark (default) + Light `[BETA]`. Mica backdrop on Win11.
- **Custom hotkey** — rebindable from Preferences with visual feedback + conflict warnings (Win+L, Win+E, etc.)
- **Offline Mode** — single toggle in Privacy that kills all network egress.
- **Opt-in crash reports** — Sentry SDK with privacy-preserving path scrubbing. Default OFF.
- **Onboarding modal** — 7-step first-run flow with real dictation test in step 6 (not a simulation).
- **Reduce-motion** — Privacy toggle for users who don't want the pill's breath + hue-drift animations.

## Quick start

```powershell
# 1. Download the latest installer from Releases:
#    https://github.com/devangk003/kuspus/releases/latest
#
# 2. Right-click the downloaded .exe → Properties → tick "Unblock" → Apply
#    (defangs SmartScreen — KusPus is unsigned by design, see PRD §9.9)
#
# 3. Run the installer. No admin prompt — installs to %LOCALAPPDATA%\Programs\KusPus.
#    7-step onboarding walks hotkey + mic + a working dictation test.
#
# 4. Hit Left Ctrl + Left Win, speak, release. Transcript pastes wherever your cursor is.
```

The installer is ~155 MB (bundles whisper.cpp + `tiny.en` model). First launch is fully offline.

For developer setup, see [docs/BUILD.md](docs/BUILD.md).

## How it works

KusPus is a single WPF process with five coordinated subsystems running through an `AppCoordinator` finite state machine:

1. **`KusPus.Native`** — a low-level keyboard hook detects the chord and consumes its key-events so they don't leak into the focused app. A paste engine restores the original foreground window and simulates `Ctrl+V` via `SendInput`.
2. **`KusPus.Audio`** — WASAPI capture (via NAudio) records 16 kHz mono PCM into a temp `.wav`, with a 15 Hz RMS level stream for the visualizer.
3. **`KusPus.Whisper`** — bundled `whisper.exe` is launched as a subprocess inside a Job Object (so it dies with the app), reads the `.wav`, outputs a `.txt` sidecar that we read.
4. **`KusPus.Persistence`** — SQLite + FTS5 for history; JSON for settings.
5. **`KusPus.App`** — WPF composition root, pill + MainWindow + tray, DI container.

Five threads (UI dispatcher, hook thread, audio capture, whisper subprocess, persistence task queue) coordinate through Rx subjects.

Full architectural contract: [docs/TECH_SPEC.md](docs/TECH_SPEC.md). One-page summary: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Comparison

| | KusPus (tiny.en, CPU) | Windows Voice Typing (Win+H) | Wispr Flow (cloud) |
|---|---|---|---|
| Local-first | ✅ | ✅ | ❌ |
| Offline | ✅ | ❌ (needs network) | ❌ |
| Account required | ❌ | ❌ | ✅ |
| Paste-anywhere (any text field) | ✅ | ✅ | ✅ |
| Hotkey-customizable | ✅ | Limited | ✅ |
| Searchable history | ✅ | ❌ | ✅ |
| Cost | Free / MIT | Free | $12/mo |
| Latency (p50, 5 s dictation) | TBD | TBD | TBD |

Benchmark methodology + reproducible scripts coming in v1.1.

## Documentation

| Document | What's in it |
|---|---|
| [docs/PRD.md](docs/PRD.md) | Product requirements, scope, non-goals, decision register |
| [docs/TECH_SPEC.md](docs/TECH_SPEC.md) | Architecture, threading model, FSM, native interop contracts |
| [docs/APP_DESIGN.md](docs/APP_DESIGN.md) | Visual + interaction spec for every user-facing surface |
| [docs/PILL_DESIGN.md](docs/PILL_DESIGN.md) | Pill-specific spec, motion model, hover-extend behaviour |
| [docs/ROADMAP.md](docs/ROADMAP.md) | What's deferred past v1.0, with promotion triggers |
| [docs/PROCESS.md](docs/PROCESS.md) | Gate-driven development workflow used to build this |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | One-page architecture summary |
| [docs/BUILD.md](docs/BUILD.md) | Developer setup |
| [docs/INSTALL.md](docs/INSTALL.md) | End-user install guide + SmartScreen/SAC notes |
| [docs/MANUAL_SMOKE.md](docs/MANUAL_SMOKE.md) | Manual milestone-gate test checklist |

## Privacy

KusPus runs entirely on your machine. Microphone audio, transcribed text, and clipboard contents **never** leave your device. The only network egress is:

- **HuggingFace model downloads** — only when you actively initiate one from the Models tab.
- **Opt-in Sentry crash reports** — default OFF, path-scrubbed (`%USERNAME%`, `%TEMP%`, etc. replaced before send).

Both are gated by an **Offline Mode** toggle in Preferences → Privacy. When ON, all `HttpClient` instances refuse non-allowlisted hosts via `EgressAllowlistHandler` — defense-in-depth even if the UI gating misfires.

See [docs/PRD.md §10](docs/PRD.md) for the full privacy posture.

## Built with

- **C# + WPF + .NET 10 LTS** — self-contained single-file publish, ~86 MB launcher
- **[whisper.cpp](https://github.com/ggerganov/whisper.cpp)** — CPU build, pinned to release `v1.8.4`
- **NAudio** — WASAPI audio capture
- **SQLite + FTS5** — history store via `Microsoft.Data.Sqlite`
- **Serilog** — file-based structured logging
- **Sentry** (optional, opt-in) — crash reporting with scrubbing
- **System.Reactive** — coordinator state stream
- **Inno Setup 6** — installer (per-user, no UAC)
- **SharpVectors** — SVG rendering inside WPF

## Acknowledgments

- **WhisprFlow / FloatingRecorder** — the macOS original this rewrites
- **[whisper.cpp](https://github.com/ggerganov/whisper.cpp)** by Georgi Gerganov
- **[ggerganov/whisper.cpp on HuggingFace](https://huggingface.co/ggerganov/whisper.cpp)** — pre-quantized models the bundled `tiny.en` ships from
- **Simple Icons / Lucide** — social icons in the About tab (CC0 / ISC)

## License

MIT — see [LICENSE](LICENSE).

---

<div align="center">
  Built solo by <a href="https://lnk.bio/devangk003">Devang Kumawat</a>. Designer-engineer at <a href="https://crewm8.ai">CrewM8</a>.

  <br/>
  <strong>Local · Privacy · First</strong>
</div>
