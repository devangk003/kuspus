# KusPus — Roadmap (post-v1.0)

| | |
|---|---|
| **Status** | Draft v0.3 |
| **Scope** | Items deliberately deferred from `PRD.md` |
| **Updated** | 2026-05-17 (v0.3: added R1.2-10 long-mode chunk-on-VAD streaming on a second hotkey per dogfood UX request) |

This file is the **deferred-work log** for KusPus. Items here were considered during v1.0 design and intentionally pushed out. Each entry includes: what it is, why it was deferred, and what triggers it being scheduled.

Items are grouped by intended milestone. Versions are aspirational; reordering is expected as testers reveal what actually matters.

---

## v1.1 — "Speed + reach"

Themes: ship the things that the dogfood gate and the first 10 testers will most ask for.

### R1.1-01 — Vulkan GPU backend for Whisper.cpp
- **What:** Add a Vulkan build of whisper.cpp alongside the CPU build. Auto-detect at first launch (Vulkan loader present → use it; else CPU).
- **Why deferred:** v1.0 keeps the install/test path single. CPU is real-time for tiny.en; the speed win matters more once users pick larger models.
- **Trigger:** ≥ 3 testers say "transcription is too slow on small/medium models" OR author wants to dictate code reviews using `base.en` regularly.
- **Cost estimate:** ~+30 MB installer; ~1 week of work including runtime detection and a Preferences "Backend: Auto / CPU / Vulkan" override.
- **Risks:** Vulkan loader edge cases on old GPUs; per-driver bugs in ggml-vulkan.

### R1.1-02 — Hinglish / multi-language transcription
- **What:** Bundle (or download on demand) `tiny` multilingual model. Add a language picker in Preferences: "English (tiny.en)" / "English + Hindi (tiny)" / "Auto-detect". Possibly add `base.tdrz` or distilled variants for better Hinglish accuracy.
- **Why deferred:** v1.0's author-as-primary-user does not need Hinglish for daily English typing. Multi-language broadens test surface significantly.
- **Trigger:** Author starts dictating Hindi words frequently OR a tester asks for it.
- **Cost estimate:** ~1 week. Mostly UI + model-manager work; whisper.cpp already supports multilingual.

### ~~R1.1-03~~ — Code signing (OV cert) — **REMOVED 2026-05-16**

Previously this row tracked procurement of a Sectigo / SSL.com / Certera OV cert to sign `KusPus.exe`. With v0.2 of the PRD, code signing has been promoted to a **permanent non-goal** (see N-11 below and PRD §9.9). KusPus is a personal-use app distributed to a small circle of friends; the cost-benefit of paid certs never pencils out for this audience. The author owns the install-friction walk-through instead.

### R1.1-04 — Auto-update mechanism (lower priority)
- **What:** In-app "Check for updates" command + background check (once daily, opt-out). Use Velopack or a hand-rolled GitHub-Releases-API poller. Differential updates if possible.
- **Why deferred:** v1.0 audience is small and personal; manual download is acceptable. The friends-only distribution model means the author can simply message testers when a new build is up.
- **Why lower priority in v0.2:** With code signing dropped as a permanent non-goal, an auto-updater downloading unsigned binaries faces the same SmartScreen / Defender / SAC friction *every update*, which makes the UX gain smaller than for a signed app. A manual "Check for updates" button (no background polling) would be a reasonable middle ground if this ever ships.
- **Trigger:** Audience > 10 active users with appetite for frequent updates; OR pace of releases > 1 per week sustained.
- **Cost estimate:** ~3–5 days with Velopack. Longer if hand-rolled.

### R1.1-05 — Custom-model import UI
- **What:** Preferences → Models → "Import model from file…" button. File picker, validation (read ggml header, check magic bytes), copy into managed `models\` folder.
- **Why deferred:** v1.0 supports custom models via `settings.json` edit (sufficient for the author's experimentation). UI is polish.
- **Trigger:** A tester needs a custom fine-tune OR Hinglish work needs a non-standard model.
- **Cost estimate:** ~2 days.

### R1.1-06 — UIA-based "safety net" for password fields (opt-in)
- **What:** Add an opt-in Preferences toggle: "Don't paste into password fields". When enabled, run a fast UIA check on the focused element before paste; if `IsPassword=true`, fall back to clipboard-only and surface "Did not paste — password field detected".
- **Why deferred:** Product principle #1 (simplicity over safety nets). But this is a reasonable opt-in for users who specifically want it.
- **Trigger:** A "dictated password into Slack" incident, or a tester asks.
- **Cost estimate:** ~2 days.

### R1.1-07 — Better install troubleshooting / Defender + SAC survivor guide
- **What:** A standalone web page (under `website/`) documenting every common Win10/Win11 install hiccup with screenshots: SmartScreen, Defender quarantine, browser block, AV interaction, and **Smart App Control** (Win11). Linked from README and the installer "Read me" page.
- **Why deferred:** v1.0 has README install notes; richer docs come once we see what testers actually trip on.
- **Why higher value in v0.2:** With code signing permanently dropped, the install guide is the *only* line of defence against tester frustration. Probably the single highest-leverage v1.1 item.
- **Trigger:** First 3 testers report a non-trivial install hiccup. Or first SAC-blocked tester.
- **Cost estimate:** ~1–2 days; lives alongside the existing Next.js `website/`.

### R1.1-08 — Custom dictionary via Whisper `--prompt` parameter
- **What:** Preferences → Audio gets a "Custom dictionary" multi-line text field. Contents are passed to `whisper.exe` via the `--prompt "…"` flag on every invocation. Whisper uses the prompt to bias decoding toward those terms — useful for proper nouns, technical jargon, your own name as you pronounce it, team-mate names, codebase identifiers.
- **Why deferred:** v1.0 keeps Preferences narrow. Cheap feature; no reason to ship at v1.0 specifically.
- **Why it's a fit:** Borrowed from OpenWhispr's design. Doesn't expand brand surface (no cloud, no UI surface beyond one text field), pure power-user win.
- **Trigger:** First request, or first time author dictates a proper noun and whisper.cpp consistently gets it wrong.
- **Cost estimate:** ~1 day. One Preferences field, one settings.json field, one CLI flag added in `WhisperRunner`.

---

## v1.2 — "Polish + trust"

Themes: things that don't fix bugs but build trust and reduce friction for a wider audience.

### R1.2-01 — Per-monitor DPI edge-case audit
- **What:** Systematic test on 4K / 1440p / 1080p combinations at 100/125/150/175/200 % scaling. Fix any pill-position or font-rendering issues.
- **Cost estimate:** ~2 days + bug-fix tail.

### R1.2-02 — Encrypted history database (SQLCipher)
- **What:** Replace `Microsoft.Data.Sqlite` with `Microsoft.Data.Sqlite + SQLCipher`. Key derived from a Windows-DPAPI-protected secret tied to the user account.
- **Why deferred:** v1 audience trusts disk encryption at the OS layer (BitLocker, device encryption). SQLCipher adds dependency complexity.
- **Trigger:** A user explicitly asks; OR healthcare/legal/finance audience emerges (which would also push other enterprise asks).
- **Cost estimate:** ~3 days.

### R1.2-03 — Hotkey conflict detection
- **What:** On hotkey assignment, scan known-conflicting OS shortcuts (Win+L, Win+E, Win+Ctrl+M, etc.) and warn the user. Optionally test the chord in a sandbox before committing.
- **Cost estimate:** ~1–2 days.

### R1.2-04 — Win+V parity helper
- **What:** Detect at first launch whether Win+V clipboard history is enabled in Settings. If not, show a one-time prompt: "KusPus relies on Win+V history for recovering your prior clipboard. Enable Win+V?" with a direct link.
- **Cost estimate:** ~1 day.

### R1.2-05 — Diagnostic export
- **What:** Settings → About → "Export diagnostics…" produces a `.zip` with: `settings.json`, latest 3 log files, `history.db` schema (no rows), environment summary (OS version, GPU, mic device, .NET runtime). User can attach to bug reports.
- **Cost estimate:** ~2 days.

### R1.2-06 — Portable mode
- **What:** A "portable" build that runs from a folder, stores all data alongside the EXE, no installer, no registry, no autostart hook. Useful for testers, power users, locked-down environments.
- **Cost estimate:** ~2 days (mostly path resolution refactor).

### R1.2-07 — Self-recovery for stuck recordings
- **What:** Watchdog: if `recording` state has been entered for > 5 minutes without explicit user release, prompt "Still recording — stop?" in the pill.
- **Cost estimate:** ~0.5 day.

### R1.2-08 — Tray-icon recording badge
- **What:** Tray icon changes color (or shows a small red dot) while recording. Provides off-screen feedback for users on multi-monitor setups where they may not see the pill.
- **Cost estimate:** ~0.5 day.

### R1.2-09 — More languages (Spanish, French, German)
- **What:** Build on R1.1-02. Add common European languages once Hinglish is shipped.
- **Trigger:** Tester demand. Mostly a model-manager UI change.

### R1.2-10 — Long-mode chunk-on-VAD continuous transcription (second hotkey)
- **What:** Add a second hotkey (default `Ctrl+Shift+LWin`) that enters a "long-mode" recording state. Mic stays open; Silero VAD on the live stream detects natural pauses (>=600 ms silence after speech); each detected utterance is handed to `whisper.exe` and pasted into whatever's currently focused. Loop continues until the user presses the long-mode hotkey again. Existing `Ctrl+Win` push-to-talk behaviour is unchanged.
- **Why deferred from v1:** ~2 weeks of focused build + iterate. v1's dogfood pass should validate the single-utterance UX first; layering streaming on top before the base is solid would blur which behaviour is the source of any bug.
- **Why valuable:** Dogfood feedback from author — long-form dictation (writing prose, code comments, Slack threads) feels constrained by the one-utterance-at-a-time push-to-talk model. Streaming with paste-on-pause removes the "remember-to-release" friction without changing the trust model (still local-first, still subprocess whisper).
- **Architecture (recommended after research, 2026-05-17):** chunk-on-VAD, NOT sliding-window. VAD runs in-process; whisper.exe stays subprocess; pastes are serialized. See https://github.com/Sharrnah/whispering for the closest reference impl.
- **Cluster plan (each ~0.5–2 days):**
  1. Settings — add `LongModeHotkey` to `HotkeySettings`, default `Ctrl+Shift+LWin`, MainWindow listen+rebind UI mirrors existing hotkey card.
  2. HotkeyEngine — support a second chord, new `LongModeChordEngaged` event into Coordinator.
  3. Silero VAD plumbing — `ManySpeech.SileroVad` NuGet (MIT, .NET-friendly ONNX wrapper). `VadGate` processes 30 ms frames from the live audio stream, emits `SpeechStart`/`SpeechEnd` after a configurable hangover.
  4. AppCoordinator streaming branch — new FSM transitions: `Idle + LongModeToggle → Streaming → chunk-on-VAD loop`. Serialized paste queue so chunk N+1 can't beat chunk N to the foreground.
  5. Whisper warm-pool — keep one `whisper.exe` per session alive, pipe wav-per-chunk over stdin (or via a temp-file watch). Claws back ~150 ms spawn lag per chunk.
  6. Pill UI variant — "STREAMING · CTRL+SHIFT+WIN TO STOP" label, brighter accent + slower breath so the user sees state.
  7. Hallucination filter — min chunk 1.0 s, VAD-confidence integral floor, phrase blacklist for known whisper artifacts ("Thanks for watching", "Subtitles by …"), `--no-context --temperature 0` flags, foreground-HWND sanity check before paste (queue if it changed mid-utterance).
  8. Manual milestone test — author walks long-form dictation scenarios (article writing, Slack thread, code comment block); iterate on hallucination filter + boundary handling based on what surfaces.
- **Top 3 risks:**
  1. Whisper hallucinations on near-silent chunks land in the user's doc — high-visibility ("Thanks for watching" pasted into Slack). Mitigated in cluster 7 but never fully eliminated.
  2. Mid-word VAD cuts on breath pauses ("trans-action"). Mitigated with 200 ms padding + `--prompt` previous-tail; expect one visible glitch per long session anyway.
  3. Paste-into-wrong-app when user alt-tabs mid-utterance. Mitigated by foreground-HWND check at chunk-emit time.
- **Realistic latency:** ~0.7–1.0 s perceived after each pause with tiny.en on a modern CPU. Not instant; comparable to Wispr Flow push-to-talk responsiveness.
- **Trigger for promotion:** Author finishes dogfood pass on v1.0 push-to-talk AND still wants this OR ≥ 2 testers ask for "let me keep talking" UX.
- **Alternative considered + rejected (for now):** "Option A" soft-cap auto-flush (paste at 30 s OR first long pause after 10 s while keeping toggle-recording UX). Simpler, lower risk; rejected because dogfood feedback specifically asked for the speak-pause-paste loop, not a long-recording survival aid. If R1.2-10 proves too costly during implementation, fall back to this.
- **Cost estimate:** ~2 weeks build + 1 week dogfood + bug-tail.

---

## v1.3 — "ARM64 + GPU diversity"

### R1.3-01 — ARM64 build
- **What:** Native ARM64 KusPus build + native ARM64 whisper.cpp. Test on Surface Pro X / Snapdragon X.
- **Why deferred:** ARM64 Windows is < 5 % of installs; no v1 testers on the platform.
- **Trigger:** Snapdragon X NPU support in whisper.cpp matures, OR an ARM64 tester appears.
- **Cost estimate:** Mostly CI + test. Code already pure managed except for whisper subprocess.

### R1.3-02 — CUDA backend (additive to Vulkan)
- **What:** Optional CUDA install for users with NVidia GPUs who want maximum throughput on large models.
- **Why deferred:** Vulkan covers NVidia adequately for v1 needs.
- **Trigger:** Heavy power user running `medium.en` or `large-v3` regularly and wanting more speed.

### R1.3-03 — DirectML backend (optional)
- **What:** Windows-native GPU compute path; broad compatibility including integrated GPUs.
- **Trigger:** If Vulkan turns out to have unsolved compatibility issues on a real fraction of test machines.

---

## Longer-term candidates (no version assigned)

### LT-01 — Cross-platform Rust core
- **What:** Extract `AudioRecorder`, `WhisperRunner`, `ModelManager`, `HotkeyEngine` state machine into a shared Rust crate. Mac (Swift) and Windows (C#) call into it via FFI.
- **Why long-term:** Only valuable if both Mac and Windows are actively evolving in parallel. v1 ships ahead of that scenario.
- **Trigger:** ≥ 3 features requested on both platforms simultaneously.

### LT-02 — Optional cloud-premium tier
- **What:** Opt-in cloud transcription via a Whisper-API-compatible endpoint for users who want `large-v3-turbo` speed without local GPU. Explicit "this leaves your machine" UI; never default.
- **Why long-term:** Adds revenue / sustainability lever. Must not undermine local-first brand.
- **Trigger:** Sustained user request OR a willingness to put a paid layer on the app.

### LT-03 — Settings sync across devices
- **What:** Sync `settings.json` and (optionally) history across the user's Mac and Windows installs. Possibly via end-to-end-encrypted blob storage.
- **Why long-term:** Cross-platform parity with Mac becomes real here. Big privacy / infra surface.

### LT-04 — Mobile companion (clipboard relay)
- **What:** Receive transcribed text from KusPus on a phone (push-to-phone clipboard, useful for dictating notes into mobile apps).
- **Why long-term:** Speculative; would have to validate need.

### LT-05 — Per-app preference profiles
- **What:** "When pasting into Slack, replace 'period' with '.', etc." Per-app post-processing rules.
- **Why long-term:** Power-user catnip; non-trivial UI; defer until single users actually ask.

### LT-06 — Voice commands (not just dictation)
- **What:** Reserved keywords that trigger app actions ("new line", "clear", "undo"). Local pattern match before whisper.
- **Why long-term:** Crosses from "dictation tool" into "voice assistant" — different product.

### LT-07 — Streaming partial-results UI (separate from R1.2-10)
- **What:** Show transcript text appearing in real-time *while user holds the chord*, using whisper.cpp's sliding-window `examples/stream/stream.cpp` mode (`--step/--length/--keep`). Text mutates as the model revises — useful as a live caption overlay, NOT for paste-into-app.
- **Why long-term:** Different architecture from R1.2-10 (sliding-window vs chunk-on-VAD). Distinct UX value (visible live feedback in the pill) but emits unstable text — you can't un-paste a token whisper later revises. So this is a viewer feature, not a paste pipeline.
- **Why deferred indefinitely:** R1.2-10 covers the "continuous paste" use case via chunk-on-VAD. Sliding-window is only valuable if you want the *visible-text-mutating-in-pill* affordance, which is a separate UX hypothesis worth testing only after R1.2-10 ships and we see whether testers want more.

### LT-08 — LLM post-processing (opt-in)
- **What:** After transcription, optionally pipe text through a local LLM (Ollama / llama.cpp) for cleanup: filler-word removal, punctuation fix, capitalization.
- **Why long-term:** Adds heavy local-LLM dependency; brand-fit concern (KusPus claims to be lightweight); should be evaluated as a separate product.

### LT-09 — In-app history export
- **What:** Export history as Markdown / JSON / CSV.
- **Why long-term:** Minor UX. Most users will not need it.

### LT-10 — Selectable audio device per-session
- **What:** Beyond a Preferences default, allow a hotkey or tray submenu to pick a different mic per session (e.g. switching between laptop mic and headset).
- **Why long-term:** Niche but useful for power users with multiple inputs.

---

## Explicit non-goals (never)

These have been considered and ruled out — listing them prevents re-litigation.

| # | Non-goal | Reason |
|---|---|---|
| N-01 | Default-on telemetry of any kind | Brand violation. Crash reports are opt-in only; usage telemetry is never. |
| N-02 | Cloud-by-default transcription | Would break the local-first promise. Opt-in only (see LT-02). |
| N-03 | Selling, sharing, or monetizing user data | Disqualifying. |
| N-04 | In-app ads | Disqualifying. |
| N-05 | Mandatory account / sign-in | App is local; no account required. |
| N-06 | Microsoft Store / MSIX as primary distribution | MSIX containerization interferes with the low-level keyboard hook and cross-process SendInput. |
| N-07 | Web-based UI as primary surface | Wrong shape for a hotkey-driven floating utility. |
| N-08 | Replacing the Windows IME / claiming a system-level service | Out of scope. |
| N-09 | Multi-user per-machine install with shared models | Per-user install is the only supported model. |
| N-10 | Background-process daemon (no UI) | App is a single visible process; no service install. |
| **N-11** | **Code signing (Authenticode OV/EV) of any kind** | **Personal-use app distributed to a small circle of friends; the cost-benefit of paid certs (Sectigo / SSL.com / Certera / Certum OSS / DigiCert) never pencils out for this audience. The author owns the install-friction walk-through manually. Decision made permanent 2026-05-16.** |
| **N-12** | **Azure Artifact Signing (formerly Trusted Signing)** | **Genuinely cheap ($9.99/month) and good UX, but unavailable to individual developers outside US/Canada. Even if it became available, see N-11 — this is a personal-use app, not a product.** |

---

## Process notes

- **One change per row.** Don't bundle related items — promote bundles to their own milestone.
- **Promotion criteria** for any row: a Trigger fires, the cost estimate still looks honest, and the next milestone has capacity.
- **Deletions are OK.** If a row's Trigger never fires across two milestones, delete it. The roadmap is a working document, not a contract.
- **Authoritative scope** for v1.0 is `PRD.md`. This file is strictly v1.1+.

*End of ROADMAP.md v0.2.*
