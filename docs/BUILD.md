# Building KusPus

This page is for developers. End-user install lives in [INSTALL.md](INSTALL.md).

## Prerequisites

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
- `KUSPUS_MODELS_DIR` — point at any folder containing `ggml-*.bin` files.
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
dotnet test                                          # full suite
dotnet test test/KusPus.Core.Tests/                  # targeted
dotnet test --filter "FullyQualifiedName~Hotkey"     # by name
```

Five test projects, one per source project (the spec calls for three; Audio + Native were added during their phases for pure-helper coverage). xunit + FluentAssertions; integration tests hit a real SQLite (no DB mocking — see CLAUDE.md feedback log for why).

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
    installer\KusPus.iss /DAppVersion=v1.0.0
```

Output: `installer\Output\KusPus-Setup-v1.0.0.exe`. Per-user, no UAC, installs to `%LOCALAPPDATA%\Programs\KusPus\`. See `installer\KusPus.iss` for the full Inno Setup configuration (decisions are documented in-line at the top of the script).

## CI

GitHub Actions runs:

- **`ci.yml`** on every push to `main` + every PR: `dotnet build` + `dotnet test`. Windows runner, TRX results uploaded as artifact.
- **`release.yml`** on every `v*` tag: full pipeline above → draft release with the installer attached + auto-generated notes + MotW unblock instructions in the body.

The release workflow uses `Minionguyjpro/Inno-Setup-Action@v1.2.5` to install Inno Setup on the runner (the Sept 2025 windows-latest → Server 2025 migration removed Inno from the base image).

## Reference

- [ARCHITECTURE.md](ARCHITECTURE.md) — one-page architecture summary.
- [INSTALL.md](INSTALL.md) — end-user install guide.
- [../README.md](../README.md) — project overview, features, comparison table.
