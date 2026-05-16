# KusPus — Phase 6 manual smoke prep

Phase 6 ships the milestone scope: **F5 → press LCtrl+LWin in Notepad → see a transcript appear.** This document walks through the one-time setup the developer machine needs before that round-trip works end-to-end.

## Prerequisites (one-time)

You should already have:

- .NET 10 SDK 10.0.300+ (`dotnet --version`)
- Git with submodules (`third_party/whisper.cpp` is pinned at 968eebe7)
- An on-Windows-desktop session (not RDP, not headless CI) — the LL keyboard hook needs the interactive session

Still missing on a fresh machine:

- **Visual Studio 2022 Build Tools 14.40+** with the "Desktop development with C++" workload. Free download. Required by `cmake --build` to produce whisper.exe.
- **CMake 3.28+** on PATH. Free download from https://cmake.org/download/.

## Build whisper.exe

```powershell
git submodule update --init --recursive
.\tools\build-whisper-windows.ps1
```

Output lands in `installer\payload\whisper\` — that's `whisper.exe`, several `*.dll`, and a `SHA256SUMS` manifest.

## Place the binaries where KusPus expects them

KusPus.App looks for whisper at `{appDir}\whisper\whisper.exe`. For an F5 run from Visual Studio:

```powershell
# Adjust the source path to wherever Phase 4 puts the App's bin dir.
$src = ".\installer\payload\whisper"
$dst = ".\src\KusPus.App\bin\Debug\net10.0-windows\whisper"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item "$src\*" $dst -Recurse -Force
```

OR set the env var to point at the payload dir directly:

```powershell
$env:KUSPUS_WHISPER_DIR = "$PWD\installer\payload\whisper"
```

## Drop a model in the models folder

```powershell
$models = Join-Path $env:LOCALAPPDATA "KusPus\models"
New-Item -ItemType Directory -Force -Path $models | Out-Null

# Download tiny.en (~75 MB) from HuggingFace.
$url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin"
Invoke-WebRequest -Uri $url -OutFile (Join-Path $models "ggml-tiny.en.bin")
```

## Run the app

```powershell
dotnet run --project src/KusPus.App
```

You should see a tray icon appear. Right-click → **Quit** to close.

## Manual smoke checklist (subset of PRD §11.3)

| # | Test | Expected |
|---|---|---|
| M-06 | Open Notepad. Hold LCtrl+LWin, say "hello world", release. | "hello world" appears in Notepad at the caret. |
| M-13 | Tap LCtrl+LWin (release < 250 ms). | Start menu does NOT open. Pill stays visible; tap chord again to stop+transcribe. |
| M-15 | Disable mic in Privacy settings → press chord. | Pill shows microphone error (currently logs only; UI string is Phase 8). |
| M-17 | Right-click tray → Quit. | App exits cleanly; no `KusPus.exe` left in Task Manager. |

## Things that won't work yet (planned)

- **MainWindow / Preferences / History UI** → Phase 9.
- **Pill animations + visualizer + "Pasted into X" overlay** → Phase 8.
- **Onboarding flow** → Phase 10.
- **Offline Mode toggle + opt-in crash reports + autostart** → Phase 11.
- **Installer + signed releases** → Phase 12 (and §9.9 — signing is a permanent non-goal).

## When things go wrong

- Hook installs but pill never appears → check `%LOCALAPPDATA%\KusPus\logs\` for the LL-hook install error.
- Whisper subprocess fails → check that `KUSPUS_WHISPER_DIR` points at a folder containing `whisper.exe` AND its DLL siblings.
- Paste lands on the WRONG window → that's a foreground-restore failure; the pill (when wired in Phase 8) will say "Window gone — text in clipboard."

Drop notes in this file or open a GitHub issue when something breaks.
