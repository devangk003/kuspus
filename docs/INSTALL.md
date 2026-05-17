# Installing KusPus

This page is for **end-user testers** installing the v1.0 release. For developer setup, see [BUILD.md](BUILD.md).

## What you're installing

A single per-user installer, ~155 MB on disk after install:

- `KusPus.exe` — self-contained single-file (.NET 10 runtime bundled, ~86 MB).
- `whisper.exe` + DLLs — CPU-only build of [whisper.cpp](https://github.com/ggerganov/whisper.cpp) v1.8.4, integrity-checked.
- `ggml-tiny.en.bin` — ~75 MB Whisper model, so first launch works fully offline.

The installer is **per-user, no admin/UAC prompt**. It writes to `%LOCALAPPDATA%\Programs\KusPus\`. Nothing global. Nothing in HKLM.

## Why is this app unsigned?

KusPus is a personal-use app distributed to a small circle of friends and dogfood testers. Code signing is a **permanent non-goal** — the small audience doesn't justify the annual certificate cost + EV-signing overhead. The friction below is intentional and walked through manually.

If you ever see a KusPus build claiming to be signed, it isn't from this repo.

## Install steps

1. **Download** the latest installer from [Releases](https://github.com/devangk003/kuspus/releases/latest). Look for `KusPus-Setup-v<version>.exe`.

2. **Unblock the file before running it.** Right-click the downloaded `.exe` → **Properties** → tick the **Unblock** checkbox at the bottom → **Apply** → **OK**. This removes the Mark-of-the-Web that triggers SmartScreen.

3. **Run the installer.** No admin prompt. Choose whether to add a desktop shortcut (off by default — the tray icon is the canonical surface). Hit **Install**.

4. **First launch** opens the 7-step onboarding modal. Step 6 runs an actual dictation test against your real mic + the bundled model — if it returns a transcript, the install is good.

5. **Use it.** Hold **Left Ctrl + Left Win**, speak, release. The transcript pastes wherever your cursor is.

## Install friction you may hit

### SmartScreen ("Windows protected your PC")

If you skip step 2 above, you'll see a blue SmartScreen panel. Click **More info** → **Run anyway**. Unblocking in Properties is the cleaner path because it also prevents SmartScreen from re-flagging the installer on later runs.

### Windows Defender

Defender occasionally quarantines the low-level keyboard hook signature on first install. If `KusPus.exe` disappears after install, open Defender's **Protection history**, find the quarantine entry, and pick **Allow**. Add `%LOCALAPPDATA%\Programs\KusPus\KusPus.exe` and `%LOCALAPPDATA%\Programs\KusPus\whisper\whisper.exe` as allowed items if it keeps recurring.

### Controlled Folder Access (CFA)

If you have CFA on, it silently blocks unsigned apps from writing to common user folders. KusPus stores models under `{app}\whisper\models\` (inside the install dir) precisely to sidestep this — apps reading their own install folder are almost never blocked. If model downloads fail with "access denied", whitelist `KusPus.exe` in **Windows Security → Virus & threat protection → Ransomware protection → Allow an app through Controlled folder access**.

### Smart App Control (Win11)

SAC blocks unsigned binaries with no per-app override. If you have it on, you have two options:

- Turn SAC off (Windows Security → App & browser control → Smart App Control). As of the April 2026 cumulative update, this no longer requires a Windows reset.
- Don't install KusPus.

### Third-party AV

Varies. Pre-release builds have hit false positives from Avast and Bitdefender. If your AV quarantines KusPus, submit it as a false positive and restore from quarantine.

## Where your data lives

| What | Where |
|---|---|
| App binaries + bundled Whisper + bundled tiny.en model | `%LOCALAPPDATA%\Programs\KusPus\` |
| Settings (`settings.json`) | `%APPDATA%\KusPus\` |
| History database (`history.db`, SQLite + FTS5) | `%LOCALAPPDATA%\KusPus\` |
| Rolling logs | `%LOCALAPPDATA%\KusPus\logs\` |
| Failed-transcribe audio + text (user-purgeable) | `%LOCALAPPDATA%\KusPus\failed\` |
| Additional downloaded models | `%LOCALAPPDATA%\Programs\KusPus\whisper\models\` |

## Uninstall

Settings → Apps → KusPus → Uninstall.

**The uninstaller deliberately preserves your data.** Settings, history, logs, and downloaded models stay on disk through an uninstall or reinstall — friends-only audiences often reinstall to test a new build, and the "lost my history" surprise isn't worth it. If you want a clean removal, delete `%APPDATA%\KusPus\` and `%LOCALAPPDATA%\KusPus\` by hand after the uninstaller finishes.

A future v1.1+ may add an opt-in "Also remove my data" checkbox to the uninstaller — deliberately not in v1.0 to prevent accidental-tick data loss.

## Upgrading

Just run the new installer. The installer ID (`AppId`) is fixed across versions; Inno Setup detects the prior install, closes any running KusPus, and overwrites in place. Settings and history are preserved.

## Privacy

KusPus runs entirely on your machine. Microphone audio, transcribed text, and clipboard contents never leave your device. The only network traffic is:

- **HuggingFace model downloads** — only when you actively initiate one from Preferences → Models.
- **Opt-in Sentry crash reports** — default OFF; toggle in Preferences → Privacy; path-scrubbed before send (`%USERNAME%`, `%TEMP%`, etc. replaced).

Both are gated by an **Offline Mode** toggle in Preferences → Privacy. When ON, all `HttpClient` instances refuse non-allowlisted hosts via `EgressAllowlistHandler` — defense-in-depth even if the UI gating misfires. The source code makes the posture verifiable: `src/KusPus.App/EgressAllowlistHandler.cs`, `src/KusPus.Core/Telemetry/CrashScrubber.cs`.
