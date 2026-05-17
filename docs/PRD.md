# KusPus — Windows Port Product Requirements Document

| | |
|---|---|
| **Project codename** | KusPus |
| **Status** | Draft v0.2 — pre-implementation |
| **Author** | Devang Kumawat |
| **Created** | 2026-05-16 |
| **Revised** | 2026-05-16 (v0.2: .NET 10, signing permanently dropped, SAC + LL-hook robustness added) |
| **Source app** | WhisprFlow / FloatingRecorder (macOS) |
| **Target platform** | Windows 10 22H2 + Windows 11, x64 |
| **Tech stack** | C# + WPF + .NET 10 (LTS) |
| **License** | MIT |
| **Repository** | New repo, codename `KusPus` (docs temporarily under `WhisprFlow/windows-port/`) |

---

## Table of contents

1. [Executive summary](#1-executive-summary)
2. [Goals & non-goals](#2-goals--non-goals)
3. [Target user & jobs-to-be-done](#3-target-user--jobs-to-be-done)
4. [Product principles](#4-product-principles)
5. [Scope of v1.0](#5-scope-of-v10)
6. [System architecture](#6-system-architecture)
7. [Data flow & lifecycle](#7-data-flow--lifecycle)
8. [Behavior contracts](#8-behavior-contracts)
9. [Platform & technology decisions](#9-platform--technology-decisions)
10. [Privacy, security & trust](#10-privacy-security--trust)
11. [Acceptance criteria for v1.0](#11-acceptance-criteria-for-v10)
12. [Risks & open questions](#12-risks--open-questions)
13. [Glossary](#13-glossary)
14. [Appendix A — Decision register](#14-appendix-a--decision-register)

---

## 1. Executive summary

KusPus is a from-scratch Windows rewrite of the macOS app **WhisprFlow / FloatingRecorder**: a floating, hotkey-driven, fully on-device speech-to-text utility that lets a user dictate into any text field on their machine. The Mac version is validated; the Windows version exists to put a production-quality, daily-driver dictation tool on the platform where the author personally spends most of their typing day.

The product bet is **not "reach the Windows market"**. It is **"build a portfolio-grade, real-production Windows app, with ~10 trusted testers as the first audience, and ship something the author will use every day."** Every scope decision in this PRD ladders up to that bet.

KusPus must reach feature parity with the macOS app in a single v1.0 release, with one explicit tradeoff: it is a **fresh rewrite, not a port** — no Swift, no cross-platform UI framework, no shared core. The rewrite is the project.

Two product pillars:

1. **Great transcription** — local Whisper.cpp, English (incl. Indian-accented English), good defaults, no cloud.
2. **Simplicity** — one hotkey, paste anywhere, no UI to manage, visible feedback inside the floating pill.

Everything else in this document either supports those two pillars or is explicitly deferred to `ROADMAP.md`.

---

## 2. Goals & non-goals

### 2.1 Goals (v1.0)

| # | Goal | How measured |
|---|---|---|
| G1 | A user can dictate into any text-input surface they use daily — terminal, browser, Slack, Discord, IDE, Office, native apps — by pressing one hotkey. | Author can complete a full typing day without falling back to keyboard for ≥ 80% of typed text. |
| G2 | Transcription runs fully on-device. No audio, no transcripts, no clipboard contents leave the machine. | Wireshark capture during a 2-hour session shows no outbound traffic except (a) HuggingFace model downloads when actively initiated, (b) opt-in Sentry crash uploads. |
| G3 | One-step install from a single `.exe` on GitHub Releases. | Fresh Win10 22H2 and fresh Win11 install successfully in under 3 minutes (incl. dismissing SmartScreen / Defender warnings) using a written guide. |
| G4 | Floating pill UI feels native, lightweight, and unobtrusive — bottom-center of active monitor, visible only when in use. | No tester reports "the pill is in my way" or "I forgot the app was running" during a 2-week trial. |
| G5 | Onboarding is < 60 seconds, includes hotkey customization. | Mean time from first launch to first successful dictation < 60 s across 10 testers. |

### 2.2 Non-goals (v1.0)

| # | Non-goal | Reason |
|---|---|---|
| NG1 | Hinglish / Hindi / multi-language transcription. | Scope discipline. Author's daily typing is English (incl. Indian-accented English). Multilingual model added in v1.1. |
| NG2 | GPU-accelerated transcription (CUDA, Vulkan, DirectML). | CPU is sufficient for tiny.en at near-real-time. GPU backend is the first item in `ROADMAP.md`. |
| NG3 | Code signing (Authenticode OV/EV) — **permanent, not deferred**. | KusPus is a personal-use app shared with a small circle of friends/testers. The author walks each tester through SmartScreen + Defender + Smart App Control friction manually. The cost-benefit of paid certs never pencils out for this distribution model. See §9.9 and the Decision register. |
| NG4 | Auto-update mechanism. | Manual download from GitHub Releases until the user base justifies an updater. |
| NG5 | Telemetry of any kind (anonymous usage analytics, feature flags, A/B). | Brand pillar. Only opt-in crash reports. |
| NG6 | UIA-based focus detection, password-field detection, paste denylist / allowlist. | Simplicity over preemption. We show *where* we pasted in the pill; user awareness is the safety net. |
| NG7 | ARM64 build, Win10 LTSC support, Windows 7/8.x support, Windows Server. | Test-matrix discipline; x64 desktop only. |
| NG8 | Cross-device settings sync, cloud history, mobile companion. | Privacy stance plus scope discipline. |
| NG9 | In-app fine-tuning, custom prompt engineering, post-processing LLM cleanup. | Out of scope for v1.0; ROADMAP candidate. |
| NG10 | Sandbox-friendly distribution (MSIX, Microsoft Store). | MSIX containerization subtly conflicts with low-level keyboard hook + cross-process `SendInput`. Wrong shape for this app. |

---

## 3. Target user & jobs-to-be-done

### 3.1 Primary user — "Power-typing prosumer"

A working software engineer or knowledge worker who types tens of thousands of words a week across heterogeneous surfaces: terminals, IDEs, browsers, chat apps, document editors. They are comfortable installing unsigned software they trust, dismissing SmartScreen warnings, and editing a JSON file if it unlocks a niche feature. They value privacy enough to choose local-first tools, but they do not need cryptographic guarantees — they need the app to be **observably** local-first.

**The author is this user.** This is deliberate: KusPus is dogfood-led. Every product decision survives the test "would the author actually use this daily?"

### 3.2 Jobs-to-be-done (JTBD)

| JTBD | When | I want to | So that |
|---|---|---|---|
| JTBD-1 | I'm composing a long Slack/Discord/email reply | dictate the body in a single take | I don't have to two-finger type a paragraph |
| JTBD-2 | I'm pairing in a terminal | dictate a complex command or commit message | I don't break the conversational flow with my colleague |
| JTBD-3 | I'm filling in a web form (Jira, Notion, GitHub PR description) | dictate freely and have it pasted into the focused field | the form fields don't dictate my pace |
| JTBD-4 | I'm writing code comments / docstrings | dictate the prose | I write more thorough comments than I would if I had to type them |
| JTBD-5 | I'm researching | dictate notes into Obsidian / a text editor mid-thought | my thinking and capture are at the same speed |

### 3.3 Anti-personas (deliberately not v1 users)

- **Enterprise IT admins** — no MDM/GPO/sysprep support in v1.0.
- **Users with strict regulatory requirements (healthcare, legal, finance)** — no audit logs, no SSO, no formal certifications.
- **Multilingual transcribers** — only English in v1.0.
- **Heavy GPU users wanting maximum model size at maximum speed** — CPU-only in v1.0.
- **Users who want a polished, signed installer with zero install friction** — explicit v1.0 trade-off.

---

## 4. Product principles

Used as tiebreakers when designing any new behavior:

1. **Simplicity beats safety nets.** If we have to choose between an extra guard and a cleaner core experience, default to the cleaner core. Visibility (showing what happened) beats prevention (refusing to act).
2. **Local-first is non-negotiable.** Network egress is an allowlist, not a denylist. New outbound endpoints require an explicit PRD revision.
3. **The hotkey is the product.** Everything is one chord away. The main window is a configuration surface, not a daily-use surface.
4. **Parity with the Mac comes from semantics, not pixels.** We mirror what the Mac app *does*, not how it looks. WPF should look native to Windows, not native to macOS.
5. **No silent surprises.** If we modify clipboard, paste into an app, or hit the network, the user sees that it happened.
6. **The author is the QA bar.** A feature ships when it survives daily use, not when a test suite passes.

---

## 5. Scope of v1.0

### 5.1 In scope

**Behavioral parity with FloatingRecorder (Mac) on Windows:**

- Global hotkey with tap-vs-hold semantics (tap to toggle, hold for push-to-talk).
- Floating pill UI showing recording state and audio visualizer.
- Local Whisper.cpp transcription (CPU only, English only).
- Smart-paste: transcript replaces clipboard, then is pasted into the foreground window via simulated Ctrl+V.
- In-pill post-paste confirmation ("Pasted into Slack") with the visualizer fading and returning.
- System tray icon with toggle, model selector, preferences, history, quit.
- Main window: Preferences + History.
- In-app model manager: download / verify / activate Whisper ggml models.
- Onboarding: welcome → hotkey picker → microphone check → autostart opt-in → crash reports opt-in → done.
- Autostart on Windows login (opt-in).
- History (transcript log with search, on by default, opt-out in Settings).
- Offline Mode (kill switch for all outbound traffic).
- Crash reports (opt-in).

### 5.2 Out of scope — deferred to `ROADMAP.md`

See `ROADMAP.md` for detail. Headlines:

- GPU backend (Vulkan).
- Hinglish / multi-language transcription.
- Code signing (OV / EV cert).
- Auto-update.
- Custom-model import UI.
- ARM64.
- UIA-based paste safety net.
- Sound effects, custom themes.
- Settings sync.

### 5.3 Explicit feature inventory

| Feature | In v1.0 | Source |
|---|---|---|
| Tap-toggle + push-to-talk hotkey | ✅ | Mac parity |
| Customizable hotkey | ✅ | Mac parity |
| Floating pill UI with visualizer | ✅ | Mac parity |
| In-pill post-paste confirmation | ✅ | New (Windows only) |
| Smart-paste (Ctrl+V to foreground) | ✅ | Mac parity (simplified) |
| Clipboard replace-and-leave | ✅ | Mac parity |
| Win+V tip in onboarding & Settings help | ✅ | New (Windows only) |
| In-app model manager | ✅ | Mac parity |
| Bundled tiny.en | ✅ | Mac parity |
| History with FTS search | ✅ | Mac parity |
| Onboarding | ✅ | Mac parity (Windows-shaped) |
| Autostart on login | ✅ | Mac parity |
| Tray menu | ✅ | Mac parity |
| Preferences window | ✅ | Mac parity |
| Diagnostics panel | ✅ | Mac parity |
| Offline Mode | ✅ | New (Windows only) |
| Opt-in crash reports | ✅ | New (Windows only) |
| Failed-audio retention (24 h) | ✅ | New (Windows only) |
| Custom model via `settings.json` | ✅ | New (Windows only, hidden) |
| Multi-language | ❌ | Roadmap |
| GPU backend | ❌ | Roadmap |
| Signed installer | ❌ | **Permanent non-goal** |
| Auto-update | ❌ | Roadmap (lower priority — friends-only audience) |
| UIA password-field detection | ❌ | Explicit non-goal |
| ARM64 build | ❌ | Roadmap |

---

## 6. System architecture

### 6.1 High-level component diagram

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                          KusPus.exe (single process, WPF)                     │
│                                                                                │
│  ┌────────────┐   ┌────────────────────┐   ┌────────────────────────────┐   │
│  │ Tray icon  │   │  Main Window       │   │ Floating Pill              │   │
│  │ NotifyIcon │   │  (Prefs + History) │   │ topmost / NOACTIVATE       │   │
│  │ + menu     │   │  WPF, MVVM         │   │ TRANSPARENT / TOOLWINDOW   │   │
│  └─────┬──────┘   └─────────┬──────────┘   └─────────────┬──────────────┘   │
│        │ commands           │ binding              show/hide                  │
│        └────────────┬───────┴──────────────────────────┬─┘                    │
│                     │                                   │                      │
│              ┌──────▼───────────────────────────────────▼────────┐           │
│              │            AppCoordinator (singleton)              │           │
│              │  FSM:  idle ↔ recording ↔ transcribing ↔ idle      │           │
│              └─┬──────────┬───────────┬──────────┬──────────┬────┘           │
│                │          │           │          │          │                  │
│         ┌──────▼───┐ ┌────▼─────┐ ┌──▼─────┐ ┌─▼─────┐ ┌─▼──────┐           │
│         │ Hotkey   │ │ Audio    │ │Whisper │ │Paste  │ │History │           │
│         │ Engine   │ │ Recorder │ │Runner  │ │Engine │ │ Store  │           │
│         │(WH_KBD_LL│ │(WASAPI)  │ │(spawn) │ │(Send- │ │(SQLite │           │
│         │  hook)   │ │          │ │        │ │ Input)│ │ + FTS5)│           │
│         └──────────┘ └──────────┘ └────────┘ └───────┘ └────────┘           │
│                                       │                                       │
│                                ┌──────▼────────┐                              │
│                                │ ModelManager  │                              │
│                                │ (HTTPS + SHA) │                              │
│                                └────────┬──────┘                              │
│                                         │                                     │
│              ┌──────────────────────────▼────────────────────────────┐       │
│              │  PrefsStore  │  LogSink  │  CrashReporter (opt-in)   │       │
│              └──────────────┴───────────┴────────────────────────────┘       │
│                                                                                │
└────────────────────────────────────────────────────────────────────────────────┘

External (allowlisted, one-way):
  ▶ huggingface.co       (model downloads, SHA-256 verified from bundled manifest)
  ▶ sentry.io            (opt-in crash minidumps, scrubbed)

Bundled subprocess:
  whisper.exe + whisper.dll + ggml*.dll  (CPU build, MSVC)
```

### 6.2 Components

| Component | Responsibility | Key Windows APIs |
|---|---|---|
| **AppCoordinator** | Owns app state machine. Mediates between hotkey, recorder, runner, paste engine, history. | — |
| **HotkeyEngine** | Low-level keyboard hook; tap/hold state machine; LWin keyup suppression. | `SetWindowsHookExW(WH_KEYBOARD_LL)` |
| **AudioRecorder** | Open default mic, capture at 16 kHz mono, write `.wav` to `%TEMP%`, expose RMS levels for visualizer. | WASAPI (`IMMDeviceEnumerator`, `IAudioClient`, `IAudioCaptureClient`) |
| **WhisperRunner** | Spawn `whisper.exe`, pipe stdout/stderr, read side-file `.wav.txt`, return text. | `System.Diagnostics.Process` |
| **PasteEngine** | Capture foreground HWND at hotkey-engage; set clipboard; restore foreground; SendInput Ctrl+V; resolve target app name. | `GetForegroundWindow`, `AttachThreadInput`, `SetForegroundWindow`, `SendInput`, `OpenProcess` + `GetModuleFileNameEx` |
| **HistoryStore** | SQLite + FTS5 for transcript log. | `Microsoft.Data.Sqlite` |
| **ModelManager** | Download / verify / activate Whisper ggml models. | `HttpClient`, SHA-256 |
| **PrefsStore** | Read/write `settings.json` with file watcher for external edits (custom model path). | `System.Text.Json`, `FileSystemWatcher` |
| **LogSink** | Rotating file logger, metadata-only. | `Microsoft.Extensions.Logging` |
| **CrashReporter** | Opt-in Sentry minidumps; scrubbing pipeline. | `Sentry.NET` SDK |
| **Tray** | `NotifyIcon` with context menu. | `H.NotifyIcon` (community WPF wrapper) or `System.Windows.Forms.NotifyIcon` |
| **FloatingPillWindow** | Topmost, click-through, no-activate WPF window with visualizer + confirmation overlay. | WPF + extended window styles (`WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`) |
| **MainWindow** | Tabbed WPF window (General / Audio / Models / History / Privacy / About). | WPF MVVM |

### 6.3 Process & threading model

- **Single process.** No background service, no helper process. Simplifies install, lifecycle, and crash recovery.
- **UI thread.** WPF dispatcher; all UI mutation here.
- **Hotkey thread.** The LL keyboard hook runs on a dedicated message-pump thread (Win32 requires the hook to be installed on a thread with a running message loop). Hook callbacks must return within milliseconds — they post events to the AppCoordinator on the UI thread via `Dispatcher.BeginInvoke`.
- **Audio thread.** WASAPI callback thread; writes to a lock-free ring buffer + the WAV file. Computes RMS levels and posts to UI thread at 15 Hz.
- **Whisper thread.** Async `Process.WaitForExitAsync()`; output is read on a thread-pool task; result marshalled back to UI thread.
- **Background tasks.** Model downloads on `HttpClient` async; SQLite writes on a dedicated serial task queue.

### 6.4 What we are NOT building (architecturally)

- No plugin system.
- No IPC, no named pipes (single-process app — second-launch handoff is a single named-mutex check).
- No background service / Windows Service registration.
- No driver, no kernel-mode anything.
- No DLL injection.
- No COM server registration.

---

## 7. Data flow & lifecycle

### 7.1 Data inventory

| Data type | Origin | Routes through | Sinks | Network? |
|---|---|---|---|---|
| Keyboard events | OS via LL hook | HotkeyEngine FSM | AppCoordinator events | **No** |
| Raw audio frames | WASAPI capture | AudioRecorder ring buffer | `.wav` file + visualizer levels | **No** |
| Audio file (`.wav`) | AudioRecorder | Filesystem | `whisper.exe` stdin (via `-f`) → deleted on success / 24 h retention on failure | **No** |
| Whisper stdout/stderr | Subprocess | LogSink (stderr metadata only — not transcripts) | Disk logs | **No** |
| Whisper side-file (`.wav.txt`) | Subprocess | Read once, deleted | Transcribed text | **No** |
| Transcribed text | WhisperRunner | PasteEngine, HistoryStore, FloatingPillWindow | Clipboard, foreground HWND (via SendInput), SQLite | **No** |
| Foreground HWND + app name | `GetForegroundWindow` etc. | PasteEngine | In-pill confirmation, history.target_app field | **No** |
| Clipboard contents | PasteEngine | OS clipboard | OS clipboard (replaces prior) | **No** |
| History records | HistoryStore | SQLite | `%LOCALAPPDATA%\KusPus\history.db` | **No** |
| Settings | PrefsStore | `settings.json` | `%APPDATA%\KusPus\settings.json` | **No** |
| Model files | HTTPS download | ModelManager | `{app}\whisper\models\*.bin` (CFA-friendly install-dir location since 2026-05-17; was `%LOCALAPPDATA%\KusPus\models\`) | **OUT** to huggingface.co (allowlisted) |
| Crash minidumps | Sentry SDK | Scrubber → Sentry | sentry.io (opt-in) | **OUT** to sentry.io (allowlisted, opt-in only) |
| Logs | LogSink | Rotating file appender | `%LOCALAPPDATA%\KusPus\logs\*.log` | **No** |
| Failed audio | AudioRecorder on transcribe failure | Filesystem | `%LOCALAPPDATA%\KusPus\failed\` (24 h auto-prune) | **No** |

### 7.2 Disk layout

```
%APPDATA%\KusPus\
└── settings.json                  ← small, human-readable, roaming-OK

%LOCALAPPDATA%\KusPus\
├── models\                        ← large; per-machine, not roaming
│   ├── ggml-tiny.en.bin           ← bundled with installer
│   ├── ggml-base.en.bin           ← downloaded on demand
│   └── ...
├── history.db                     ← SQLite (FTS5)
├── failed\                        ← .wav files from failed transcriptions (≤ 24 h)
│   └── kuspus-failed-<ts>.wav
└── logs\                          ← rotated, ≤ 5 MB each, ≤ 5 files
    └── kuspus-<yyyy-mm-dd>.log

%TEMP%\
└── kuspus-<n>.wav                 ← ephemeral; deleted on successful transcribe

Program Files\KusPus\              ← install location
├── KusPus.exe                     ← unsigned single-file .NET 10 self-contained
├── whisper\                       ← MSVC CPU build of whisper.cpp
│   ├── whisper.exe
│   ├── whisper.dll
│   └── ggml*.dll
└── (no user data here)
```

### 7.3 Trust boundaries

| Boundary | Direction | What crosses | Validation |
|---|---|---|---|
| Mic → KusPus | In | Raw audio frames | None — trust OS device |
| KusPus → Clipboard | Out (local) | Transcribed text | None |
| KusPus → Foreground app | Out (local) | Synthesized Ctrl+V keystroke | None — pure trust (see [4. Principles](#4-product-principles)) |
| KusPus ↔ Filesystem | In/Out | Settings, history, models, audio, logs | JSON schema validation on settings; SHA-256 on models |
| KusPus → HuggingFace | Out (network) | HTTP GET for `.bin` files | SHA-256 verified against bundled manifest; URL allowlist |
| KusPus → Sentry | Out (network, opt-in) | Scrubbed crash minidumps | Scrubbing pipeline strips paths, environment vars, clipboard, transcripts |
| External → KusPus | (forbidden) | — | No inbound network listeners; no IPC; no IPC servers |

### 7.4 Lifecycle of the critical data types

**Audio file lifecycle:**

```
[hotkey engaged] → create %TEMP%\kuspus-N.wav
[recording]      → WASAPI frames written
[hotkey released or push-to-talk end]
                 → close .wav
                 → spawn whisper.exe
                 ↓
   ┌─────────────┴──────────────┐
[success]                    [failure]
   ↓                              ↓
delete .wav                  move .wav → %LOCALAPPDATA%\KusPus\failed\
delete .wav.txt              record failure in History with "Retry" + "Send to debug"
                              auto-prune anything in failed\ older than 24 h on next launch
```

**Transcript lifecycle:**

```
whisper.exe writes .wav.txt
  ↓
read + trim
  ↓
┌─────────────────┬──────────────────┬────────────────────────┐
↓                 ↓                  ↓                        ↓
Clipboard.Set    SendInput Ctrl+V   HistoryStore.Insert     FloatingPill: show
(replace prior)  (to captured        (text, ts, duration,    "Pasted into <App>"
                  foreground HWND)    model, target_app)      for 1 s, then fade
```

**Clipboard lifecycle:**

```
Before hotkey:   [user's previous clipboard contents]
After paste:     [transcribed text]   ← user can Ctrl+V to replay
                 (prior contents recoverable via Win+V history)
```

We never read the prior clipboard, never save it, never restore it.

**Foreground-window capture timing — critical:**

```
T0  hotkey engaged                           ← capture GetForegroundWindow()  → "targetHwnd"
T1  show floating pill (WS_EX_NOACTIVATE)
T2  record audio while held / until tap-release
T3  hide visualizer animation in pill
T4  spawn whisper.exe
T5  transcript ready
T6  Clipboard.SetText(transcript)
T7  AttachThreadInput / SetForegroundWindow(targetHwnd)   ← restore focus to T0 target
T8  SendInput Ctrl+V
T9  show "Pasted into <name>" overlay in pill for 1 s
T10 hide pill
```

`targetHwnd` is captured at T0 because by the time we paste at T8, the pill itself or any window the user clicked on during the wait would otherwise have stolen focus. This is one of the key correctness invariants of the paste pipeline.

---

## 8. Behavior contracts

This section reads like a partial test specification. Every contract here is testable by hand.

### 8.1 Hotkey state machine

**States:** `idle`, `armed`, `recording`, `transcribing`, `cancelled`.

**Inputs:** `chord_engaged` (LCtrl+LWin both held), `chord_released`, `hold_threshold_elapsed`, `other_key_pressed`, `transcribe_complete`, `transcribe_failed`.

**Transitions:**

| From | Input | To | Side effect |
|---|---|---|---|
| `idle` | `chord_engaged` | `armed` | start 250 ms hold timer; capture foreground HWND |
| `armed` | `chord_released` (before timer) | `transcribing` | show pill, stop recorder (no audio collected → noop), this becomes a TAP toggle of the recorder state instead. See "Tap" below. |
| `armed` | `hold_threshold_elapsed` | `recording` | start audio capture, show pill |
| `armed` | `other_key_pressed` | `cancelled` | cancel hold timer, do nothing |
| `recording` | `chord_released` | `transcribing` | stop audio capture, kick whisper |
| `transcribing` | `transcribe_complete` | `idle` | clipboard + paste + history + show in-pill confirmation, hide pill |
| `transcribing` | `transcribe_failed` | `idle` | retain `.wav` in `failed/`, log error, show error toast in pill |
| `cancelled` | `chord_released` | `idle` | (no-op) |

**Tap vs hold semantics:**

- **Tap** = press + release before 250 ms. Toggles a persistent "recording mode" — pill stays visible; record until next tap.
- **Hold** = press + held past 250 ms. Push-to-talk — record while held, transcribe on release.

This is mac parity. The persistent-recording-mode (tap branch) is implemented as a second internal sub-state.

**LWin keyup suppression:**

The LL hook MUST consume (return `1`) the keyup of `VK_LWIN` whenever it released the chord. Otherwise, releasing LWin without another key open the Windows Start menu. We must inject a synthetic `VK_CONTROL` keydown/keyup pair just before the LWin keyup is consumed to break the "lone Win key" gesture. (Standard Windows trick used by many hotkey utilities.)

### 8.2 Recording

- **API:** WASAPI shared-mode capture from default input device.
- **Format:** 16-bit PCM, 16 kHz mono. Source format auto-resampled (Windows mixer does this for free in shared mode).
- **Write target:** `%TEMP%\kuspus-<unix_ms>.wav`, RIFF/WAVE with standard header.
- **Visualizer levels:** RMS over 20 chunks per frame, posted to UI thread at 15 Hz.
- **Device-change handling:** if the default device changes mid-recording (`IMMNotificationClient.OnDefaultDeviceChanged`), stop recording immediately and surface an error in the pill ("Mic changed — try again"). Do not auto-switch and continue; the user expects to start over.
- **Maximum recording length:** no hard cap in v1.0. Practical limit is disk space in `%TEMP%`. Monitor `.wav` size; if it exceeds 100 MB (~50 minutes at 16 kHz mono PCM), stop and warn.

### 8.3 Transcription

- **Invocation:**
  ```
  whisper.exe -m <model_path> -f <wav_path> -nt --output-txt -l en
  ```
  Flags:
  - `-nt` — no timestamps
  - `--output-txt` — write side-file `<wav>.txt`
  - `-l en` — force English (we are explicit even though tiny.en is English-only)
- **Working directory:** the directory containing `whisper.exe` (so its DLLs resolve via implicit DLL search order).
- **Timeout:** 5 minutes hard cap. If exceeded, kill the process tree and treat as `transcribe_failed`.
- **Output:** read `<wav>.txt`, UTF-8, trim leading/trailing whitespace.
- **Stderr handling:** capture, log first 4 KB and last 4 KB at `Warn` level on non-zero exit. Do NOT log transcript content.
- **Process model decision:** subprocess, not in-process DLL P/Invoke. (See §9.4.)

### 8.4 Smart-paste

- **Foreground capture timing:** at the moment the chord is engaged (T0 in §7.4), not at paste time.
- **No UIA, no denylist, no allowlist.** Trust the captured HWND.
- **Procedure:**
  1. `Clipboard.SetText(transcript)` with retry-once on `ERROR_ACCESS_DENIED`.
  2. Resolve target app name from `targetHwnd` → `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `GetModuleFileNameEx` → strip path → friendly name (e.g. `slack.exe` → "Slack" via a small static map; fallback to filename).
  3. Restore foreground: if `GetForegroundWindow() != targetHwnd`, perform `AttachThreadInput(currentThread, targetThread, TRUE)` + `SetForegroundWindow(targetHwnd)` + `AttachThreadInput(..., FALSE)`.
  4. Send `Ctrl+V` via `SendInput`:
     - keydown `VK_CONTROL`
     - keydown `V`
     - keyup `V`
     - keyup `VK_CONTROL`
  5. Show "Pasted into &lt;App&gt;" in the floating pill for 1 second. Fade visualizer to ~20% opacity behind text. Fade text out, restore visualizer, hide pill.
- **What we explicitly do NOT do:**
  - Press Enter / Return.
  - Send any character keys directly (only Ctrl+V via clipboard).
  - Check the focused element's role.
  - Block paste based on app identity.

### 8.5 Floating pill window

- **Extended window styles:**
  - `WS_EX_TOPMOST` — always on top
  - `WS_EX_NOACTIVATE` — does not steal focus when shown
  - `WS_EX_TRANSPARENT` — click-through (mouse events pass to underlying window)
  - `WS_EX_TOOLWINDOW` — does not appear in Alt+Tab or taskbar
  - `WS_EX_LAYERED` — alpha blending for fade animations
- **Position:** bottom-center of the monitor containing the cursor at the moment of showing. Re-evaluate on each show. Approx. 40 px above the taskbar.
- **DPI awareness:** declared `PerMonitorV2` in app manifest. Position math uses `MonitorFromPoint` and DPI-aware coordinates.
- **Size:** ~360 × 64 logical pixels; scaled with DPI.
- **States:**
  - `hidden`
  - `recording` — visualizer animating, accent-colored
  - `transcribing` — spinner + "Transcribing…" text
  - `paste_confirmed` — visualizer faded to ~20%, "Pasted into &lt;App&gt;" text on top
  - `error` — red accent, brief error text
- **Theme:** follow Windows system theme (`SystemParameters.UxThemeName` + registry watcher).
- **Shadow & corners:** acrylic / Mica background where available (Win11), rounded corners; Win10 fallback is solid dark/light translucent fill.

### 8.6 History

**Schema:**

```sql
CREATE TABLE transcripts (
  id            INTEGER PRIMARY KEY,
  ts            INTEGER NOT NULL,         -- unix ms
  text          TEXT    NOT NULL,
  duration_ms   INTEGER NOT NULL,
  model         TEXT    NOT NULL,         -- e.g. "ggml-tiny.en"
  target_app    TEXT,                     -- e.g. "Slack", nullable for "no paste"
  status        TEXT    NOT NULL,         -- "ok" | "failed"
  failed_wav    TEXT                      -- nullable, path to retained .wav
);

CREATE VIRTUAL TABLE transcripts_fts USING fts5(text, content='transcripts', content_rowid='id');
CREATE TRIGGER transcripts_ai AFTER INSERT ON transcripts BEGIN
  INSERT INTO transcripts_fts(rowid, text) VALUES (new.id, new.text);
END;
CREATE TRIGGER transcripts_ad AFTER DELETE ON transcripts BEGIN
  INSERT INTO transcripts_fts(transcripts_fts, rowid, text) VALUES('delete', old.id, old.text);
END;
```

**UI:**
- Search bar (FTS query against `transcripts_fts`).
- List of cards: timestamp, model, duration, target app, first line of text.
- Card actions: Copy, Delete, Retry (if `failed_wav` present).
- Bulk: "Purge history" button (confirmation modal).

**Retention:** none. User-only deletion.

**Default:** ON. Onboarding explains it and shows the opt-out toggle.

**Disable behavior:** when disabled in Settings, stop writing; existing rows remain until user purges.

### 8.7 Onboarding

Sequence (each is its own page; user can `Back` / `Next`):

1. **Welcome.** What KusPus does in three lines. Single screenshot of the pill.
2. **Hotkey picker.** Default chord LCtrl+LWin highlighted. "Press a new chord to change." Live preview. Test bar: "Press your chord now." Show "Detected!" feedback. Optional warning if chord conflicts with a known well-used Windows chord (e.g. `Win+Ctrl+M` for Magnifier).
3. **Microphone check.** Attempt to open default mic; show level meter for 3 seconds. If access denied, link to `ms-settings:privacy-microphone` with "Open Settings" button. After enabling, "Test again" button.
4. **Autostart.** Toggle: "Launch KusPus when I sign in" (default OFF). Explanation: "You can change this later in Preferences."
5. **Crash reports.** Toggle: "Help me improve KusPus with opt-in crash reports" (default OFF). One-line privacy promise.
6. **Try it.** "Press your chord and dictate one sentence. We'll show you what was transcribed." Live demo into a tutorial-only text field.
7. **Done.** "You're set up. KusPus is in your system tray." Pin-to-tray instructions.

**Skippable:** the entire flow can be skipped from any step via a "Skip onboarding" link. Skipping leaves all toggles at their default values.

**Re-runnable:** Preferences → About → "Re-run onboarding".

### 8.8 Preferences (tab structure)

| Tab | Controls |
|---|---|
| **General** | Hotkey picker; Autostart toggle; Theme (Auto / Light / Dark) |
| **Audio** | Input device dropdown; level meter; "Test transcription" button |
| **Models** | Active model selector; download manager (per-model status: not installed / downloading / installed); link to model help (where to put custom models) |
| **History** | Search; entry list; "Purge history" button; "Enable history" toggle |
| **Privacy** | Offline Mode toggle; crash reports toggle; "Clear logs" button; link to log folder |
| **About** | Version; build date; GitHub link; "Open log folder" button; "Re-run onboarding" button |

### 8.9 Tray menu

- `KusPus` (header — non-clickable, shows version)
- separator
- `Toggle Recorder` (Ctrl+LWin shown as accelerator)
- `Active Model: tiny.en ▸` (submenu of available models)
- separator
- `Preferences…`
- `History…`
- separator
- `Quit`

### 8.10 Single-instance behavior

- Named mutex `Local\KusPus` checked at launch.
- If already held by another process: send a custom Win32 message (`RegisterWindowMessage("KusPus.BringMainToFront")`) to the existing instance's main window via `PostMessage` to `HWND_BROADCAST`. Existing instance brings its main window forward. New instance exits with code 0.

### 8.11 Error & edge-case behaviors

| Scenario | Behavior |
|---|---|
| Mic disabled in Privacy settings | Pill shows "Microphone access blocked" with a tap-to-open-settings affordance |
| Active model missing on disk (manually deleted) | Tray menu shows "No active model"; first tap opens Preferences → Models |
| Whisper subprocess crashes | Treat as `transcribe_failed`; pill shows "Transcription failed — see History to retry" |
| Disk full during recording | Stop recording; pill shows "Disk full"; do not write history row |
| `targetHwnd` window closed before paste | Paste skipped; clipboard still set; pill shows "Window gone — text in clipboard" |
| `targetHwnd` is an elevated process (UIPI) and KusPus isn't | Paste silently fails; pill shows "Couldn't paste (elevated window) — text in clipboard" |
| Hotkey collision with a real Windows shortcut | Document in Preferences; user must rebind |
| Two displays with different DPI | Pill positions on cursor's monitor with correct DPI scaling |
| Recording while in a fullscreen game | Pill may not render over fullscreen-exclusive games; clipboard still works; this is acceptable for v1 |
| Network down during model download | Download fails; user is shown error with retry; previously installed models remain usable |

---

## 9. Platform & technology decisions

For each decision: **what we picked**, **why**, **what we rejected**.

### 9.1 OS floor: Win10 22H2 + Win11, x64 only

- **Picked:** Both Win10 22H2 and Win11; x64 only.
- **Why:** Covers ~99% of consumer Windows desktops in 2026. .NET 10 fully supports both. Single test matrix (x64 only).
- **Rejected:** Win11-only (loses ~half the testable user pool); x64+ARM64 (doubles whisper.cpp build matrix; no testers on ARM64); Win10 LTSC (niche).

### 9.2 Tech stack: C# + WPF + .NET 10 (LTS)

- **Picked:** C# + WPF + .NET 10 LTS (self-contained single-file publish).
- **Why:**
  - **.NET 10 is the current LTS** (released Nov 2025, supported through Nov 2028). .NET 8 LTS reaches end-of-support on Nov 10, 2026 — within KusPus's likely v1 ship window. Starting a new desktop app on an EOL-imminent runtime is a deliberate footgun we decline.
  - **WPF is mature for our exact needs.** Layered topmost click-through windows, NotifyIcon, P/Invoke for low-level hooks, `SendInput`, `GetForegroundWindow`, WASAPI — all well-trodden in .NET. WPF on .NET 10 ships meaningful rendering perf improvements over .NET 8.
  - **One-developer ROI.** Larger ecosystem, more examples, faster debugging than WinUI 3 or Rust+windows-rs.
  - **MVVM** matches how the Mac version is structured.
  - **Single-file publish** gives us one `.exe` for the installer to drop.
- **Rejected:**
  - **.NET 8 LTS** — Six months from EOS at the time of this PRD; would force a runtime migration during or immediately after v1.
  - **.NET 9 STS** — Already past EOS for new projects (May 2026).
  - **WinUI 3 / Windows App SDK** — modern Win11 look (Mica, Fluent), but as of 2026 the WinUI team's own discussion threads confirm Direct3D-backed composition makes input pass-through impossible without `WS_EX_NOREDIRECTIONBITMAP` workarounds; smaller community; harder to ship outside MSIX; we need topmost click-through more than we need Mica.
  - **Rust + Tauri + windows-rs** — tiny binary, modern stack, but every native call needs FFI; ~2–3× more code; Rust learning curve cost not justified by v1 audience.
  - **Electron + native helper binary** — 150 MB install, RAM-hungry, wrong shape for a lightweight floating utility.
  - **C++ / Qt** — heavy, mature, but a one-dev productivity tax we don't need.
  - **Recompile Swift for Windows (SwiftCrossUI / SwiftWin32)** — immature; no AX equivalent; we'd still rewrite ~60% of the code.

### 9.3 Whisper backend: CPU only

- **Picked:** Single CPU build of whisper.cpp via MSVC, bundled in installer.
- **Why:**
  - tiny.en is real-time on a modern laptop CPU.
  - Single installer, single test path, single distribution artifact.
  - Defers all GPU-detection complexity to v1.1.
- **Rejected:**
  - **CPU + CUDA dual installers** — doubles the release pipeline; forces users to pick the right one; debugging CUDA driver mismatches on remote machines is a tax we cannot afford for 10 users.
  - **CPU + Vulkan single installer with runtime detection** — better long-term answer than CUDA; deferred to v1.1 to keep v1 scope tight.
  - **CPU forever** — caps future capability needlessly.

### 9.4 Whisper process model: subprocess

- **Picked:** Spawn `whisper.exe` as a subprocess; capture output via stdout/stderr; read side-file `.wav.txt`.
- **Why:**
  - **Parity with Mac.** Mac version uses the same shape.
  - **Crash isolation.** A C++ crash in whisper terminates the subprocess, not KusPus. We surface the error and recover.
  - **Trivial upgrade story.** We swap the `whisper\` folder contents to update whisper.cpp.
  - **~50 ms spawn cost** is dwarfed by transcription time (seconds).
- **Rejected:**
  - **In-process via DLL P/Invoke.** Faster startup; risk of unrecoverable native crashes taking down the entire app; harder upgrade. Deferred consideration for v1.1.

### 9.5 Hotkey: Left Ctrl + Left Win, modifier-only chord

- **Picked:** LCtrl + LWin, with onboarding-time rebinding.
- **Why:** Rare combination on Windows; doesn't conflict with major IDE / IME / Office shortcuts; preserves the macOS "modifier-only chord" feel.
- **Risk:** Releasing LWin alone opens Start menu — we mitigate with the standard "inject a stray VK_CONTROL keystroke before consuming the LWin keyup" trick.
- **Rejected:**
  - **Ctrl+Space** — conflicts with IntelliSense, IMEs, IDE autocomplete. Wrong for a developer audience.
  - **Right Ctrl single key** — fine on desktop keyboards but missing on many laptops.
  - **Caps Lock** — surprising to remap; breaks Caps for those who use it.
  - **LCtrl+LShift modifier chord** — collides with normal typing patterns (Ctrl+Shift+T = reopen tab).

### 9.6 Smart-paste: no UIA, no denylist (with one terminal-aware exception)

- **Picked:** Capture foreground HWND at engage time; clipboard + SendInput Ctrl+V; show post-paste confirmation in pill.
- **Picked exception — terminals:** When the target foreground process is a known terminal (Windows Terminal, `cmd.exe`, `powershell.exe`, `pwsh.exe`, `wsl.exe`, ConEmu, mintty, Alacritty, WezTerm), substitute **Ctrl+Shift+V** for Ctrl+V. Most modern Windows terminals bind paste to Ctrl+Shift+V; Ctrl+V either does nothing or sends `^V` as a literal character. This is a process-name match only — no UIA, no role check.
- **Why:** Product principle #1 (simplicity beats safety nets); product principle #5 (no silent surprises — confirmation IS the safety net). UIA is unreliable in terminals and Chromium; a denylist would inevitably be wrong. Win+V history is the user's escape hatch for a wrong paste. The terminal exception is a narrow, deterministic process-name lookup that turns a guaranteed failure into a guaranteed success without dragging in UIA.
- **Rejected:**
  - **UIA-based focused-element check** — fragile in browsers/Electron/terminals (exactly the apps the primary user lives in).
  - **App denylist** — incomplete by definition; high maintenance.
  - **App allowlist** — too restrictive.
  - **Per-app prompt on first use** — onboarding friction not justified by the safety win.

### 9.7 Clipboard hygiene: replace and leave

- **Picked:** Transcript replaces clipboard; we never read or restore prior contents. Onboarding mentions Win+V as the recovery path.
- **Why:** Win+V exists; restoring prior clipboard correctly across all formats (CF_BITMAP, CF_HDROP, etc.) is ~150 LOC of fragile P/Invoke; the user gains a trivial "Ctrl+V replay" capability of the transcript.
- **Rejected:** Save+restore (complex, breaks replay); tiered timed restore (confusing); per-Preferences toggle (defer).

### 9.8 Bundled model: tiny.en only

- **Picked:** tiny.en bundled with the installer (~75 MB); base / small / medium / large-v3 downloadable in-app.
- **Why:** Installer stays under 150 MB; first-launch time-to-first-transcription is < 5 seconds; English-only matches v1 language scope.
- **Rejected:** tiny multilingual (slightly less accurate on English; Hinglish not in v1); bundle nothing (forces a download on first use); bundle multiple (installer bloat).

### 9.9 Distribution: unsigned Inno Setup `.exe` on GitHub Releases (permanent)

- **Picked:** Single `.exe` installer built with Inno Setup, **unsigned in perpetuity**, uploaded to GitHub Releases. Audience is the author and a small circle of friends/testers who can be walked through install friction manually.
- **Why:**
  - This is a personal-use app, not a product. Paid certs (OV ~$200–300/yr including the HSM token required by CABF since June 2023; Azure Artifact Signing ~$120/yr but unavailable to individual developers outside US/Canada) never pencil out for this audience.
  - Trust is established person-to-person, not via Microsoft's reputation graph.
  - The author owns the friction of the install walk-through.
- **Accepted friction (documented in INSTALL.md):**
  - **SmartScreen warning** — testers click "More info → Run anyway." Standard Windows behavior for unsigned consumer downloads.
  - **Defender heuristic quarantine** — LL keyboard hook + clipboard write + SendInput is the canonical keylogger signature; expect occasional false-positive quarantines. Testers add `KusPus.exe` and `whisper.exe` to Defender's exclusions, or the author submits the SHA via submit.microsoft.com per release.
  - **Smart App Control (SAC)** — for testers running Win11 with SAC enforced, KusPus will be blocked outright with no override. As of the April 2026 cumulative update, SAC can be toggled off without a Windows reset. Testers either toggle SAC off temporarily during install, or KusPus simply cannot be run on their machine. Documented as a hard requirement in INSTALL.md.
- **Rejected:**
  - **OV cert from any vendor (Sectigo, SSL.com, DigiCert, Certera, Certum OSS)** — cost-benefit fails for friends-only distribution; permanently dropped, not deferred.
  - **EV cert** — even more cost-benefit failure.
  - **Azure Artifact Signing** — not available to individual developers outside US/Canada.
  - **MSIX** — sandbox interferes with global hook and cross-process SendInput.
  - **Unsigned `.zip`** — even worse SmartScreen experience than `.exe`.

### 9.10 Telemetry: none. Crash reports: opt-in only.

- **Picked:** Zero usage telemetry. Sentry-based crash reports, default OFF, opt-in during onboarding.
- **Why:** Brand pillar. Local-first audience disqualifies anything else.
- **Rejected:** Default-on usage analytics (brand violation); no crash reports at all (loses too much debugging signal).

### 9.11 History: default ON, opt-out in Settings

- **Picked:** Default ON; opt-out in Settings; explained in onboarding step #6.
- **Why:** Primary feature parity with Mac; useful for the power user; opt-out path preserves user agency.
- **Rejected:** Default OFF (defeats the parity feature); time-boxed auto-prune (surprises long-tail use).

### 9.12 Custom models: hidden, settings.json only

- **Picked:** Power users can point `settings.json` to a custom model path. No UI in v1.
- **Why:** Author's own future-experimentation lifeline (fine-tunes, distilled variants) without ANY user-facing surface area cost.
- **Rejected:** No support (forecloses experimentation); first-class Import UI (defer to v1.1).

### 9.13 Failed-audio retention: 24 h

- **Picked:** Keep failed `.wav` in `%LOCALAPPDATA%\KusPus\failed\` for 24 hours; surface "Retry" + "Send to debug" in History.
- **Why:** Bug-report fidelity from 10 testers. Slight privacy bump (audio on disk for a day) but local-only and visible in disk layout.
- **Rejected:** Always delete (Mac parity, but loses tester fidelity); keep forever (privacy/disk creep); user-configurable (defer).

### 9.14 Single-instance: named mutex, second launch focuses first

- **Picked:** Named mutex `Local\KusPus`; second launch broadcasts a "bring main to front" Win32 message and exits.
- **Why:** Standard, low-LOC, works without IPC.

### 9.15 Hotkey conflict warning list

The onboarding hotkey picker (§8.7 step 2) warns the user when the chord they're trying to bind overlaps a well-known Windows shortcut. v1.0 ships a **hardcoded conflict list** rather than a runtime conflict detector (the latter is on the roadmap).

Hardcoded conflict list (any chord whose key + modifier set fully matches a row warns the user):

| Chord | What it does |
|---|---|
| `Win+L` | Lock workstation |
| `Win+E` | File Explorer |
| `Win+S` | Search |
| `Win+I` | Settings |
| `Win+A` | Action Center / Quick Settings |
| `Win+V` | Clipboard history (KusPus depends on this — see §9.7) |
| `Win+Tab` | Task View |
| `Win+.` / `Win+;` | Emoji picker |
| `Win+H` | Voice typing |
| `Win+Ctrl+M` | Magnifier |
| `Win+Ctrl+Q` | Quick Assist |
| `Alt+Tab` | App switcher |
| `Alt+F4` | Close window |
| `Ctrl+Alt+Del` | Security screen |
| `Ctrl+Shift+Esc` | Task Manager |

The warning is non-blocking ("This chord is also used by Windows for X. Are you sure?") — the user can proceed.

### 9.16 Position relative to built-in Windows dictation (Voice Access)

Voice Access is Microsoft's first-party voice typing tool (`Win+H`). It is cloud-leaning and command-oriented (it understands "select previous word", etc.). KusPus does not compete with it on commands or accessibility — KusPus is a focused dictation tool that is:

- **Local-only.** Voice Access uses Microsoft's cloud speech service for higher-quality models.
- **Hotkey-driven, paste-anywhere.** Voice Access requires opening a tray panel and pointing it at a specific app or text field.
- **No commands, just text.** A KusPus dictation is always a literal transcription.
- **Indian-accented English accuracy via whisper.cpp.** Voice Access historically underperforms on non-US-native English.

This is a positioning note, not a competitive one — KusPus and Voice Access can coexist on the same machine.

---

## 10. Privacy, security & trust

### 10.1 Privacy promise (verbatim, will appear in README and onboarding)

> KusPus runs entirely on your machine. Your microphone audio, transcribed text, and clipboard contents never leave your device. The only network traffic KusPus generates is downloading Whisper model files (from huggingface.co, verified by SHA-256) and, if you opt in, anonymous crash reports.

### 10.2 Egress allowlist (enforced in code)

```csharp
// pseudo-code
static readonly Uri[] AllowedHosts = new[] {
    new Uri("https://huggingface.co/"),
    new Uri("https://ingest.sentry.io/"),  // only if CrashReportsOptedIn
};
```

All `HttpClient` instances in the codebase route through a single factory that asserts the URI's host is in the allowlist. Crash reports are gated by the user's opt-in flag at construction. Offline Mode disables both.

### 10.3 Crash report scrubbing

Sentry payload scrubbing strips:
- All file paths under `%USERPROFILE%`, `%APPDATA%`, `%LOCALAPPDATA%`, `%TEMP%`.
- All environment variables.
- The user's username (from `Environment.UserName`).
- Any field whose key is in {`clipboard`, `transcript`, `text`, `target_app`, `hwnd`}.
- Stack traces are kept; argument values are not.

### 10.4 Threat model (informal, scoped)

We are **not** defending against:
- Local malware with code-execution as the user (it can read clipboard, see history, etc. — KusPus is not a sandbox).
- A user with disk access reading `history.db` (local-only, no encryption).
- Privacy from Microsoft's own clipboard sync (user-controlled; documented).

We **are** defending against:
- Accidental network exfiltration via a future code change (egress allowlist enforces explicit allow).
- Transcript leakage via logs (logs are metadata-only).
- Transcript leakage via crash reports (scrubbing pipeline).
- A confused user thinking their dictation is "cloud" (UI and README make local-first explicit).

### 10.5 Audit affordance

A power user can:
- Run Wireshark and observe traffic.
- Open `%LOCALAPPDATA%\KusPus\logs\` and see what was logged.
- Inspect `settings.json`.
- Disable everything with one Offline Mode toggle.

---

## 11. Acceptance criteria for v1.0

### 11.1 Author dogfood gate (gating, must pass before any tester sees v1.0)

KusPus has a tiered dictation gate. Real-world dictation ceilings for software-engineering work tend to sit around 60–70 % because code editing and terminal usage are back-and-forth activities, not monologue. We separate "ship-worthy" from "delight":

- **Ship-worthy (must pass):** KusPus is the author's primary input method for ≥ 14 consecutive days, with **≥ 60 %** of all words typed in that window dictated.
- **Delight (target, not gating):** ≥ 80 % over the same window.

Plus, regardless of percentage:

- No more than 3 unintentional reverts to keyboard per day on average (i.e., "I gave up and typed it").
- Zero crashes that required restarting the app.
- Zero "I dictated my password into a chat" incidents.

### 11.2 Install gate

- Clean install on a fresh Win10 22H2 VM: success in < 3 minutes including SmartScreen + Defender dismissal.
- Clean install on a fresh Win11 VM: same.
- Uninstall removes all of `Program Files\KusPus\`, the Start menu shortcut, and the autostart entry. Leaves `%APPDATA%\KusPus\` and `%LOCALAPPDATA%\KusPus\` (user data) intact unless user checks "Also remove my data".

### 11.3 Behavioral correctness gate (hand tests)

Tester confirms each works on their machine:

- [ ] Hotkey LCtrl+LWin engages within 100 ms of physical press.
- [ ] Tap (release < 250 ms) toggles persistent recording mode.
- [ ] Hold (≥ 250 ms) push-to-talks; release transcribes and pastes.
- [ ] Pill appears bottom-center of the monitor with the cursor.
- [ ] Pill does NOT steal focus when shown.
- [ ] Transcribed text appears in the previously-focused field via Ctrl+V (no Enter key sent).
- [ ] Paste into Windows Terminal (default settings) succeeds — Ctrl+Shift+V is used automatically.
- [ ] Paste into `cmd.exe` and `powershell.exe` succeeds — Ctrl+Shift+V is used automatically.
- [ ] In-pill "Pasted into &lt;App&gt;" confirmation appears for 1 s after paste.
- [ ] Win+V history contains both the prior clipboard content and the new transcript.
- [ ] History tab shows the new entry with correct target-app name.
- [ ] Releasing LWin after the chord does NOT open the Start menu.
- [ ] Disabling mic in Privacy settings produces a clear error in the pill, not a silent failure.
- [ ] Offline Mode toggle blocks model download with a clear message.
- [ ] Quitting KusPus from the tray cleanly exits; no orphan whisper.exe processes.

### 11.4 Privacy gate

- Wireshark capture during a 30-minute session with Offline Mode ON shows zero outbound packets from KusPus.exe.
- With Offline Mode OFF and crash reports OFF: zero outbound packets unless the user initiates a model download.
- Inspection of `%LOCALAPPDATA%\KusPus\logs\*.log` confirms no transcript text is present.

---

## 12. Risks & open questions

### 12.1 Risks (severity × likelihood)

| Risk | Severity | Likelihood | Mitigation |
|---|---|---|---|
| **Smart App Control (SAC) blocks KusPus.exe outright on Win11** | **High** | **Medium** | SAC has no override; documented in INSTALL.md that testers running SAC must toggle it off (April 2026+ cumulative makes this reversible without a Windows reset). Pre-flight check during onboarding step 0 detects SAC state and shows a guided disable flow. |
| Windows Defender quarantines unsigned KusPus.exe | High | High | Written install guide with screenshots; tester walk-through; per-release SHA submission to submit.microsoft.com as false positive |
| SmartScreen blocks download | Medium | High | Document the "More info → Run anyway" click in README; same as Mac's "Open anyway" parallel |
| LL keyboard hook flagged by 3rd-party AV | High | Medium | Test against common AVs (Defender, Avast, Bitdefender) before v1.0 release; document known false positives |
| **LL hook silently removed by Windows after exceeding `LowLevelHooksTimeout` (5000 ms default)** | **High** | **Low** | Hook callback in `< 1 ms` by design (push-to-channel); heartbeat thread re-installs the hook if a synthetic key-event probe shows it died (see TECH_SPEC §13). |
| LCtrl+LWin Start-menu suppression bug | High | Medium | Use known stray-VK_CONTROL trick; manual test across 5+ scenarios; add to acceptance criteria |
| Focus restore fails (Windows blocks SetForegroundWindow) | High | Medium | Use AttachThreadInput; capture HWND at chord-engage time, not paste time; pill is WS_EX_NOACTIVATE; **retry with `AllowSetForegroundWindow(ASFW_ANY)` on first FALSE return** (see TECH_SPEC §16). |
| Paste into UIPI-elevated app fails silently | Medium | Low for primary user | Show "Couldn't paste (elevated window) — text in clipboard" message |
| WASAPI device-change mid-recording | Medium | Low | Subscribe to default-device-changed; abort recording cleanly; surface error in pill |
| SQLite corruption on power loss | Low | Low | WAL mode; daily backup of `history.db` to `history.db.bak` (1-day rolling) |
| HuggingFace endpoint changes / 404 | Medium | Low | **Pin model URLs by HF commit SHA** (not `main` branch) in bundled manifest; show clear error on 404; user can drop file manually |
| Per-monitor DPI scaling makes pill blurry | Medium | Medium | Declare PerMonitorV2 in manifest; explicit DPI math; test on 100/125/150/200 % scaling |
| Whisper.cpp on Windows produces worse English transcripts than on Mac | Medium | Low | Benchmark vs the Mac build during dev; document any deltas |
| Author dogfood reveals UX bug we didn't anticipate | High | Medium | This is the entire point of §11.1; budget rework time |
| Time-to-first-paste perceived as slow | Medium | Medium | Profile end-to-end latency; target < 1.5 s for a 5-second dictation on a modern CPU |
| **Anti-cheat (Vanguard, EAC, BattlEye) refuses to launch the game with KusPus running** | **Low** | **Low for non-gaming testers** | Documented limitation: "Quit KusPus before launching protected games." Tray menu retains a quick-quit affordance. |
| **Remote Desktop / Citrix sessions fire spurious 'mic changed' events on attach/detach** | **Low** | **Low** | Documented limitation. Recording aborts cleanly; user retries. v1 makes no attempt to suppress spurious device-change events. |
| .NET 10 runtime missing on tester machine | Low | Low | Self-contained single-file publish (~70 MB more in installer, no runtime dependency) |

### 12.2 Open questions

| # | Question | Owner | When |
|---|---|---|---|
| OQ-1 | Which Inno Setup template (modern vs classic)? | Implementation | Before first installer build |
| OQ-2 | NotifyIcon: `H.NotifyIcon` community library or roll our own via `Shell_NotifyIconW`? | Implementation | Before tray work starts |
| OQ-3 | Self-contained publish (~70 MB) vs framework-dependent (~5 MB + user installs .NET 10)? | Implementation | Before installer work |
| OQ-4 | Pill animation toolkit — pure WPF animations vs Lottie? | Design | Before pill UI work |
| OQ-5 | Acrylic/Mica feature detection — fail back to solid gracefully on Win10? | Implementation | During pill UI work |
| OQ-6 | App-name resolution: hardcode friendly names for top 30 apps, or use `FileVersionInfo.ProductName`? | Implementation | During paste-engine work |
| ~~OQ-7~~ | **RESOLVED:** "Test transcription" in Preferences runs a sample WAV through the full whisper pipeline (not just a mic-level test). This is the only test that catches a broken whisper bundle at install time, and it mirrors the onboarding "Try it" step. | Resolved 2026-05-16 |
| OQ-8 | History "Send to debug" — where does the .wav go? Manual user-action ("Copy file path to clipboard") in v1, or shared upload later? | Product | Pre-v1.0 |
| OQ-9 | Per-app friendly-name mapping (`slack.exe` → "Slack") — embed as JSON resource? | Implementation | During paste-engine work |
| OQ-10 | Should we ship a portable mode (no installer, run from a folder)? | Product | Post-v1.0 candidate |

---

## 13. Glossary

| Term | Meaning |
|---|---|
| **Chord** | A simultaneous combination of keys held down. KusPus's default is LCtrl + LWin held together. |
| **CPU build** | A whisper.cpp binary compiled without GPU acceleration. |
| **Engaged / engage** | The moment the hotkey chord becomes active (all required keys held). |
| **FSM** | Finite state machine. The hotkey logic is one. |
| **HWND** | Windows window handle — opaque ID for a window. |
| **LL hook** | Low-level keyboard hook (`WH_KEYBOARD_LL`) — Windows mechanism for seeing all keyboard events. |
| **Offline Mode** | A toggle in Preferences that disables all outbound network traffic from KusPus. |
| **Paste confirmation** | The in-pill overlay shown briefly after a successful paste. |
| **PerMonitorV2** | A Windows DPI-awareness mode that handles per-monitor DPI changes. |
| **Pill** | The small floating UI element that shows recording state. |
| **SAC** | Smart App Control. A Windows 11 security layer (separate from SmartScreen and Defender) that blocks unsigned or low-reputation binaries with no per-app override. |
| **Smart-paste** | KusPus's "paste anywhere" feature: clipboard + simulated Ctrl+V into the foreground window. |
| **Target HWND** | The window that was in the foreground at hotkey-engage time. This is where the transcript will be pasted. |
| **Tap** | Press-and-release of the chord within the hold threshold (250 ms). Toggles persistent recording mode. |
| **Hold** | Press-and-hold of the chord past the hold threshold. Push-to-talk; releasing triggers transcribe. |
| **tiny.en** | The smallest English-only Whisper.cpp ggml model. ~75 MB. |
| **UIA** | UI Automation — the Windows accessibility framework. **Not used in v1.0.** |
| **WASAPI** | Windows Audio Session API. The audio capture API we use. |
| **Win+V** | Windows built-in clipboard history. KusPus relies on it as the user's prior-clipboard recovery path. |

---

## 14. Appendix A — Decision register

| ID | Decision | Picked | Rejected | Source |
|---|---|---|---|---|
| D-01 | Project mission | Portfolio-grade, real production app for the author and ~10 testers | Reach / enterprise / personal-only | §2 |
| D-02 | Primary user | Power-typing prosumer (author included) | Enterprise, accessibility-first, niche privacy | §3 |
| D-03 | Scope of v1 | Strict mac parity (clean rewrite) | Parity-minus, parity-plus, different shape | §5 |
| D-04 | Repo | New repo `KusPus`, docs in `WhisprFlow/windows-port/` for now | Same repo, monorepo, mac-legacy | §1 |
| D-05 | Tech stack | C# + WPF + .NET 10 LTS (self-contained) | WinUI 3, Rust+Tauri, Electron, .NET 8 (EOS Nov 2026), .NET 9 STS | §9.2 |
| D-06 | OS floor | Win10 22H2 + Win11, x64 | Win11-only, ARM64, LTSC | §9.1 |
| D-07 | Whisper backend | CPU only | CPU+CUDA dual, CPU+Vulkan auto-detect, CPU forever | §9.3 |
| D-08 | Whisper process model | Subprocess | In-process DLL P/Invoke | §9.4 |
| D-09 | Default hotkey | Left Ctrl + Left Win (rebindable) | Ctrl+Space, Right Ctrl, Caps Lock | §9.5 |
| D-10 | Hotkey hold threshold | 250 ms | (no alternatives considered) | §8.1 |
| D-11 | Smart-paste mode | Aggressive (paste to foreground), no UIA, no denylist | UIA focus check, denylist, allowlist, per-app prompt | §9.6 |
| D-12 | Paste confirmation | In-pill overlay; visualizer fade + text + return | Separate toast; no feedback | §8.4 |
| D-13 | Clipboard hygiene | Replace and leave; rely on Win+V | Save+restore, tiered restore | §9.7 |
| D-14 | Bundled model | tiny.en (English-only, ~75 MB) | tiny multilingual, base, none | §9.8 |
| D-15 | Languages (v1) | English only (incl. Indian-accented English) | Multi-language, Hinglish, English-only forever | §2 / §5 |
| D-16 | Custom models | Hidden via `settings.json` path | No support, Import UI | §9.12 |
| D-17 | History | Default ON, opt-out in Settings; full metadata; user-purgeable; no auto-prune | Default OFF, minimum metadata, auto-prune | §9.11 |
| D-18 | Failed-audio retention | 24 h in `failed/`, auto-prune | Always delete, keep forever, configurable | §9.13 |
| D-19 | Logs | Metadata only, no transcripts | Transcripts in logs, hashes, verbose toggle | §8.2 / §10.3 |
| D-20 | Floating pill position | Bottom-center, fixed, on cursor's monitor | Caret-following, last-position, top-center | §8.5 |
| D-21 | Autostart | OFF default, opt-in during onboarding | ON default, prompt-every-launch, no support | §8.7 |
| D-22 | Theme | Follow Windows system theme | Dark-only, light-only, custom | §8.5 |
| D-23 | Telemetry | None | Default-on, opt-in usage | §9.10 |
| D-24 | Crash reports | Sentry, opt-in (default OFF) | Default-on, never | §9.10 |
| D-25 | Network egress | Hardcoded allowlist + Offline Mode toggle | Allowlist no toggle, no enforcement, full transparency log | §10.2 |
| D-26 | Distribution | **Unsigned Inno Setup `.exe` on GitHub Releases (permanent)** | OV/EV cert ever, MSIX, .zip, Azure Artifact Signing | §9.9 |
| D-27 | Auto-update | None in v1; lower priority on roadmap (friends-only audience) | Velopack, in-app check | §5.2 |
| D-28 | Settings storage | `%APPDATA%\KusPus\settings.json` | Registry, LOCALAPPDATA | §7.2 |
| D-29 | History storage | SQLite + FTS5 in `%LOCALAPPDATA%\KusPus\history.db` | JSON files, registry | §8.6 |
| D-30 | Single-instance | Named mutex `Local\KusPus`; broadcast bring-to-front | Multi-instance, file-lock | §8.10 |
| D-31 | Acceptance gate | Author dogfood ≥ 14 days @ ≥ 60 % ship-worthy / ≥ 80 % delight | Test users first, feature checklist only, install matrix only | §11.1 |
| D-32 | Hotkey conflict UX | Hardcoded warning list of well-known Windows chords; non-blocking | Full runtime detection, no warning at all | §9.15 |
| D-33 | Voice Access coexistence | Position as complementary, not competitive; no integration | Try to integrate, try to replace | §9.16 |
| D-34 | LL hook robustness | Heartbeat probe + auto re-install on silent removal | Trust the hook stays alive forever | §9.15 / TECH_SPEC §13 |

---

*End of PRD v0.2. Next revision: after a first implementation spike resolves the open questions in §12.2.*
