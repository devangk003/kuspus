<div align="center">
  <img src="icons/icon.svg" alt="KusPus logo" width="120" />

  # KusPus

  > Local-only voice-to-text for Windows and macOS. Privacy-First.
</div>

KusPus is a floating, hotkey-driven dictation app for Windows and macOS. Hold **Left Ctrl + Left Win** (Windows) or **⌃ ⌘** (Mac), speak, release. The transcript pastes into whatever app you're in — terminal, browser, Slack, IDE, anywhere with a text field. Everything runs on your machine. No cloud, no account, no telemetry.

Built in C# on .NET 10 with [whisper.cpp](https://github.com/ggerganov/whisper.cpp) and [NVIDIA Parakeet-TDT](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) (via [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)): a WPF app on Windows (CPU) and a native AppKit app on macOS (Metal whisper), sharing the dictation pipeline, settings and history code. Parakeet v2 (English) and v3 (25 European languages) are one download away in Preferences → Models — faster and more accurate than Whisper large-v3 on CPU, with punctuation and casing built in. A from-scratch rewrite of **WhisprFlow / FloatingRecorder**.

**Status:** v1.0.0 on Windows (installer on [Releases](https://github.com/devangk003/kuspus/releases)). macOS build in progress on `main` — see [docs/BUILD.md](docs/BUILD.md#macos) to build it yourself; a signed DMG lands with v1.1.

## Quick start

1. Download `KusPus-Setup-v1.0.0.exe` from [Releases](https://github.com/devangk003/kuspus/releases/latest).
2. Click Run Anyways (SmartScreen Alert — KusPus is unsigned by design.)
3. Run the installer. No admin prompt. ~155 MB on disk; bundles `whisper.exe` + `tiny.en` model so first launch is fully offline.
4. Hit **Left Ctrl + Left Win**, speak, release.

See [docs/INSTALL.md](docs/INSTALL.md) for SmartScreen / Defender / SAC walkthroughs.

## Privacy

Microphone audio, transcripts, and clipboard contents **never leave your device**. The only network traffic is:

- **HuggingFace model downloads** — user-initiated only, from Preferences → Models.
- **Opt-in Sentry crash reports** — default OFF, path-scrubbed before send.

Both are gated by an **Offline Mode** toggle. With it on, all `HttpClient` instances refuse non-allowlisted hosts via `EgressAllowlistHandler` — defense-in-depth even if the UI misfires. Verifiable in source: `src/KusPus.Coordination/EgressAllowlistHandler.cs`, `src/KusPus.Core/Telemetry/CrashScrubber.cs`.

On macOS the transcript is also marked `org.nspasteboard.ConcealedType`, so clipboard managers skip it.

## Contributing

**Contributors welcome.** KusPus is a one-person project that wants to be more. If you use it and want it to be better, the easiest ways to help:

- **Report bugs** — open an [issue](https://github.com/devangk003/kuspus/issues) with repro steps + a log slice from `%LOCALAPPDATA%\KusPus\logs\`.
- **Try a roadmap item** — pick anything from "What's next" below and open an issue to claim it before starting.
- **Improve install docs** — every tester hits SmartScreen/Defender/SAC differently; PRs to [docs/INSTALL.md](docs/INSTALL.md) with new screenshots or AV-specific notes are gold.

Dev setup is in [docs/BUILD.md](docs/BUILD.md). The architecture overview is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). PRs should keep the build at **0 warnings, 0 errors** (the project sets `TreatWarningsAsErrors=true`) and add tests for new behavior where the existing test projects cover similar code.

## What's next

Post-v1.0 work, roughly prioritized:

**v1.1 — Speed + reach**
- **macOS app** (native AppKit, Metal whisper) — code is on `main`; needs a Developer ID signature for distribution.
- Vulkan GPU backend for larger Whisper models (`base.en`, `small.en`).
- Hinglish / Indic transcription (Parakeet v3 covers European languages only; Whisper `large-v3` remains the multilingual fallback).
- Custom-model import UI.
- Custom dictionary via Whisper's `--prompt` flag (bias for proper nouns, jargon, names).
- Better install troubleshooting page with screenshots.

**v1.2 — Polish + trust**
- **Long-mode chunk-on-VAD streaming** on a second hotkey — keep talking, paste on natural pauses, no need to remember to release.
- Encrypted history (SQLCipher).
- Portable mode (no installer, run from a folder).
- Hotkey conflict detection.
- Diagnostic export ("attach this zip to a bug report").

**v1.3+**
- Native ARM64 build (Surface Pro X / Snapdragon X).
- CUDA / DirectML backends.

Suggestions welcome — open an issue if there's something missing.

## License

MIT — see [LICENSE](LICENSE).

---

<div align="center">
  Built solo by <a href="https://lnk.bio/devangk003">Devang Kumawat</a>. Designer-engineer.

  <br/>
  <strong>Local · Privacy · First</strong>
</div>
