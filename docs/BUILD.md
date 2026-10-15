# Building KusPus

This page is for developers. End-user install lives in [INSTALL.md](INSTALL.md).

KusPus builds two native apps from one repo: the WPF app on Windows and the AppKit app on macOS. The platform-neutral projects (`KusPus.Core`, `KusPus.Coordination`, `KusPus.Persistence`, `KusPus.Whisper`) build and test on either OS. Jump to [macOS](#macos) for the Mac flow.

## Prerequisites (Windows)

- **Windows 10 22H2** or **Windows 11** (x64).
- **.NET 10 SDK** — install the latest patch from <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **Visual Studio 2022** 17.12+ (Community is fine) *or* VS Code with the C# Dev Kit extension. Rider works too.
- **Git** 2.40+.
- **PowerShell 5.1** (ships with Windows) or **PowerShell 7+** for `tools/*.ps1`.
- **Inno Setup 6.4+** — only needed if you're packaging an installer locally. CI installs it on the runner.

No MSVC, no CMake, no C++ toolchain. `whisper.exe` is downloaded prebuilt from the upstream release.

Version pins live in code: `Directory.Build.props` for .NET / analyzer / language; `tools/build-whisper-windows.ps1` for the whisper.cpp tag; `src/KusPus.App/KusPus.App.csproj` + `Properties/PublishProfiles/win-x64.pubxml` for publish.

## First-time setup

```powershell
git clone https://github.com/devangk003/kuspus.git
cd kuspus
dotnet restore
dotnet build
```

The solution file is `KusPus.slnx` (the .NET 10 XML solution format). It's checked in — no `dotnet new sln` needed.

`Directory.Build.props` enables `TreatWarningsAsErrors=true` and `Nullable=enable` across every project. The build is expected to be **0 warnings, 0 errors**. If you see warnings, that's a bug — please fix at the root rather than suppressing.

## Whisper payload (one-time, then idempotent)

Before the app can actually transcribe, populate `installer/payload/whisper/`:

```powershell
.\tools\build-whisper-windows.ps1                 # downloads pinned tag (v1.8.4)
.\tools\build-whisper-windows.ps1 -Tag v1.8.5     # try a newer release
.\tools\build-whisper-windows.ps1 -Force          # re-download anyway
```

The script downloads `whisper-bin-x64.zip` from the upstream `ggerganov/whisper.cpp` release, extracts `whisper-cli.exe` (renamed to `whisper.exe`) plus the runtime DLLs into `installer/payload/whisper/`, and writes `SHA256SUMS`. A `.tag` marker makes re-runs no-ops.

> **Why download not build?** The Phase 12 dogfood concluded build-from-source costs ~5 min, requires MSVC + CMake on every dev box and CI runner, and produces the same artifact as the upstream release. The download path takes ~3 s and the binaries carry Mark-of-the-Web reputation from GitHub's release pipeline. The from-source flow is preserved in git history if it's ever needed for audit.

## Running from source

```powershell
dotnet run --project src/KusPus.App
```

On first launch the app will look for `whisper.exe` at `{appDir}\whisper\whisper.exe` (next to the EXE that's executing) and for models under `{appDir}\whisper\models\`. Two env vars override:

- `KUSPUS_WHISPER_DIR` — point at `installer\payload\whisper\` so you don't have to copy files into `bin\Debug\…`.
- `KUSPUS_MODELS_DIR` — point at any folder containing `ggml-*.bin` files (Parakeet models live in `<id>/` sub-folders holding `encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt`).
- `KUSPUS_WHISPER_SHA256` — set to empty string to disable the integrity check (default for dev builds).

Quick local run:

```powershell
$env:KUSPUS_WHISPER_DIR = "$PWD\installer\payload\whisper"
$env:KUSPUS_MODELS_DIR  = "$PWD\installer\payload\whisper\models"
dotnet run --project src/KusPus.App
```

Drop a model in the models dir before the first dictation:

```powershell
$models = "$PWD\installer\payload\whisper\models"
New-Item -ItemType Directory -Force -Path $models | Out-Null
Invoke-WebRequest `
  -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin" `
  -OutFile (Join-Path $models "ggml-tiny.en.bin")
```

…or download from inside the app (Preferences → Models).

## Tests

```powershell
dotnet test KusPus.Windows.slnf                      # full Windows suite
dotnet test test/KusPus.Core.Tests/                  # targeted
dotnet test --filter "FullyQualifiedName~Hotkey"     # by name
```

One xunit project per source project (`KusPus.Coordination.Tests` drives the whole dictation pipeline against in-memory fakes). xunit + FluentAssertions; integration tests hit a real SQLite (no DB mocking).

Solution filters keep each OS to the projects it can build: `KusPus.Windows.slnf`, `KusPus.Mac.slnf`, and `KusPus.Shared.slnf` (platform-neutral only — what the `shared-macos` CI job runs).

## Publishing a single-file build

```powershell
dotnet publish src/KusPus.App `
  -p:PublishProfile=win-x64 `
  -o publish/win-x64
```

The `PublishProfile=win-x64` profile pins:

- `SelfContained=true`, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`
- `EnableCompressionInSingleFile=true`
- `PublishReadyToRun=true`
- `PublishTrimmed=false` (WPF + SharpVectors break under trim)
- `DebugType=embedded`

Output: a single `~86 MB KusPus.exe`. The profile lives in `src/KusPus.App/Properties/PublishProfiles/win-x64.pubxml` (not the csproj) so dev `dotnet build` calls stay lean — putting `SelfContained=true` in the csproj would materialise the 200 MB runtime in every Debug build.

`EnableSingleFileAnalyzer=true` is on in the csproj, so any regression that breaks single-file (`Assembly.Location`, etc.) fails the build.

## Packaging the installer

```powershell
.\tools\build-whisper-windows.ps1                 # populate payload
dotnet publish src/KusPus.App -p:PublishProfile=win-x64 -o publish\win-x64
& "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe" `
    installer\KusPus.iss /DAppVersion=v1.5.0
```

Output: `installer\Output\KusPus-Setup-v1.5.0.exe`. Per-user, no UAC, installs to `%LOCALAPPDATA%\Programs\KusPus\`. See `installer\KusPus.iss` for the full Inno Setup configuration (decisions are documented in-line at the top of the script).

## macOS

### Prerequisites

- **macOS 15+** on Apple Silicon (the app targets `osx-arm64`; an x64 build is possible but untested).
- **.NET 10 SDK** — `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0` installs to `~/.dotnet` without admin.
- **Xcode 27** (full Xcode from the App Store, not just Command Line Tools) — the macOS workload builds against Xcode's SDK. `sudo xcode-select -s /Applications/Xcode.app` after installing.
- **.NET macOS workload**: `dotnet workload install macos`. `global.json` pins the workload set (`workloadVersion`) so an automatic workload-set update can't start demanding a newer Xcode than you have ([dotnet/macios#26772](https://github.com/dotnet/macios/issues/26772)). The Mac csprojs set `TargetPlatformVersion=27.0`; change both when you move to a new Xcode.
- **CMake** (`brew install cmake`) — only for building `whisper-cli`.

### Build + test

```bash
dotnet build KusPus.Mac.slnf
dotnet test  KusPus.Mac.slnf
```

Library projects compile without Xcode; anything that produces an app bundle (`KusPus.Mac.App`, the Mac test host) needs it.

### Whisper payload

```bash
tools/build-whisper-macos.sh              # pinned v1.8.4, Metal on, ~2 min
tools/build-whisper-macos.sh -t v1.9.5    # try a newer tag
tools/build-whisper-macos.sh -f           # rebuild
```

Builds `whisper-cli` + the ggml/whisper dylibs from the `third_party/whisper.cpp` submodule at the pinned tag (upstream ships no macOS CLI binary), fetches and SHA-verifies the bundled `tiny.en` from `models.json`, and writes `installer/payload/whisper-macos/SHA256SUMS`. The `whisper-cli` line feeds `BuildConstants.ExpectedWhisperSha256` through `build/WhisperSha.targets`, the same mechanism as Windows. The payload is copied into `KusPus.app/Contents/Resources/whisper/` at build time.

### Run from source

```bash
dotnet build src/KusPus.Mac.App && open src/KusPus.Mac.App/bin/Debug/net10.0-macos/osx-arm64/KusPus.app
```

Logs: `~/Library/Logs/KusPus/`. Data: `~/Library/Application Support/KusPus/` (settings.json, history.db, downloaded models, failed recordings). The same `KUSPUS_WHISPER_DIR` / `KUSPUS_MODELS_DIR` / `KUSPUS_WHISPER_SHA256` / `KUSPUS_TEMP_DIR` overrides apply.

Dev builds are ad-hoc signed. macOS ties the Microphone / Input Monitoring / Accessibility grants to the code signature, so **every rebuild loses them** — expect to re-grant in System Settings while iterating. A Developer ID signature fixes that for release builds.

### Install on this Mac (and upgrade in place)

```bash
tools/install-macos-local.sh            # build → sign → DMG → /Applications/KusPus.app → launch
tools/install-macos-local.sh -v 1.2.0   # stamp a different version
```

This is the day-to-day upgrade path: re-run it after pulling changes and the new bundle replaces `/Applications/KusPus.app` (the running app is quit first). Three things make the upgrade seamless:

1. Same bundle id and install path → `settings.json`, `history.db`, downloaded models and the Login Item carry over.
2. **Stable signing identity.** macOS ties the Microphone / Input Monitoring / Accessibility grants to the app's code signature. The script creates a self-signed code-signing certificate once ("KusPus Local Signing", in `~/Library/Keychains/kuspus-local.keychain-db`) and signs every build with it, so the designated requirement doesn't change and the grants survive. Delete that keychain and you'll re-grant once.
3. `CFBundleVersion` is a build timestamp, so each build is newer to LaunchServices.

The DMG it writes to `installer/Output/` is signed but not notarized: it opens normally on this Mac; on another Mac it needs right-click → Open the first time (a Developer ID + notarization removes that).

### Package (DMG)

```bash
tools/package-macos.sh -v 1.5.0 -i "Developer ID Application: Name (TEAMID)" -p kuspus-notary
tools/package-macos.sh -v 1.5.0            # ad-hoc, local testing only
```

Publishes the bundle, signs every nested Mach-O with the hardened runtime (`com.apple.security.cs.allow-jit` + `device.audio-input`, no App Sandbox — the sandbox blocks the ⌘V paste), notarizes + staples the app, then builds, notarizes and staples the DMG into `installer/Output/`. Run `xcrun notarytool store-credentials kuspus-notary` once to set up the profile.

## CI

GitHub Actions runs:

- **`ci.yml`** on every push to `main` + every PR: three jobs — `build-test` (Windows: `KusPus.Windows.slnf`), `shared-macos` (the platform-neutral projects on a Mac runner), and `macos-app` (the AppKit app + its tests on `macos-26` with Xcode 27 and the pinned workload set). TRX results uploaded as artifacts.
- **`release.yml`** on every `v*` tag: the Windows pipeline → draft release with the installer attached + auto-generated notes + MotW unblock instructions in the body; then `release-macos` builds `whisper-cli`, packages the DMG (signed + notarized when the `MACOS_CERT_P12` / `MACOS_CERT_PASSWORD` / `MACOS_NOTARY_*` secrets exist, ad-hoc otherwise) and attaches it to the same draft.

The release workflow uses `Minionguyjpro/Inno-Setup-Action@v1.2.5` to install Inno Setup on the runner (the Sept 2025 windows-latest → Server 2025 migration removed Inno from the base image).

## Reference

- [ARCHITECTURE.md](ARCHITECTURE.md) — one-page architecture summary.
- [INSTALL.md](INSTALL.md) — end-user install guide.
- [../README.md](../README.md) — project overview, features, comparison table.
