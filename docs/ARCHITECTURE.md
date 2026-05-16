# KusPus architecture

This is a one-page overview. The full architectural contract lives in [TECH_SPEC.md](TECH_SPEC.md); see in particular:

- §1 Solution structure (the 6-project + 3-test layout)
- §6 Threading model (5 thread contexts, marshalling rules)
- §12 AppCoordinator & state machine
- Appendix A Class diagrams

## Component map

```
KusPus.exe (single process, WPF)

  Tray  ─┐
  Main  ─┤
  Pill  ─┴──> AppCoordinator (FSM: idle ↔ recording ↔ transcribing ↔ idle)
                │
                ├──> HotkeyEngine    (WH_KEYBOARD_LL hook, watchdog self-heal)
                ├──> AudioRecorder   (WASAPI capture → 16 kHz mono WAV)
                ├──> WhisperRunner   (subprocess in Job Object)
                ├──> PasteEngine     (clipboard + SendInput + foreground restore)
                ├──> HistoryStore    (SQLite + FTS5)
                └──> ModelManager    (HTTPS, SHA-256, pinned-commit URLs)
```

External (allowlisted, one-way):

- `huggingface.co` — model downloads
- `ingest.sentry.io` — opt-in crash reports

Bundled subprocess: `whisper.exe + whisper.dll + ggml*.dll` (CPU build).
