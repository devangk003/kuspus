# Installing KusPus

This document is for **end-user testers** installing the unsigned `.exe` from GitHub Releases. It will be filled in during Phase 12, after the first installer build.

Until then, see [BUILD.md](BUILD.md) for developer setup.

## Why is this app unsigned?

KusPus is a personal-use app distributed to a small circle of friends. Code signing is a **permanent non-goal** — see PRD §9.9 / Decision D-26 / Non-goal N-11. The friction below is intentional and walked through manually.

## Expected install friction (Phase 12 — fill in)

- **SmartScreen** — "More info → Run anyway."
- **Windows Defender** — may quarantine the LL keyboard hook signature; whitelist `KusPus.exe` and `whisper.exe`.
- **Smart App Control (Win11)** — toggles off, no per-app override. As of April 2026 cumulative update, can be re-enabled without a Windows reset.
- **3rd-party AV** — varies; test machines have logged false positives from Avast and Bitdefender in pre-release builds.

## Privacy

See PRD §10. KusPus runs entirely on your machine. Microphone audio, transcribed text, and clipboard contents never leave your device.
