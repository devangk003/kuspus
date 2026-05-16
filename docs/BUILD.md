# Building KusPus

## Prerequisites

- **Windows 10 22H2** or **Windows 11** (x64).
- **.NET 10 SDK** — install the latest patch from <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **Visual Studio 2022** 17.12+ (Community is fine) *or* VS Code with the C# Dev Kit extension.
- **Git** 2.40+ with submodule support.
- For whisper.cpp builds (Phase 3+): **MSVC Build Tools 14.40+** and **CMake** 3.28+.
- For installer builds (Phase 12+): **Inno Setup** 6.4.0+.

See TECH_SPEC.md §3 for the canonical build-tooling matrix and version pins.

## First-time setup

```powershell
git clone --recurse-submodules <repo-url>
cd KusPus

# Generate the solution file (intentionally not committed during scaffolding):
dotnet new sln -n KusPus
Get-ChildItem -Recurse -Filter *.csproj | ForEach-Object { dotnet sln add $_.FullName }

# (Phase 3+) Build whisper.cpp:
# tools/build-whisper-windows.ps1

dotnet restore
dotnet build
```

## Running

(Phase 6+) F5 in Visual Studio, or `dotnet run --project src/KusPus.App`.

Until Phase 6, `KusPus.App` has no entry point — it's a structural shell. Other projects (Core, Native, Audio, Whisper, Persistence) and the test projects compile clean once their phase ships.

## Tests

```powershell
dotnet test
```

## Phase status

See the [TECH_SPEC.md](TECH_SPEC.md) table of contents for the full module map; the phased build plan lives in the in-session task list.

| Phase | What lands | Build exits cleanly? |
|---|---|---|
| 0 | Repo scaffolded | Solution file not generated yet |
| 1 | `KusPus.Core` + tests | ✅ |
| 2 | `KusPus.Persistence` + tests | ✅ |
| 3 | `KusPus.Whisper` + whisper.exe payload | ✅ |
| 4 | `KusPus.Audio` | ✅ |
| 5 | `KusPus.Native` | ✅ |
| 6 | `KusPus.App` runs end-to-end | ✅ — F5 dictates into Notepad |
| 7+ | Polish / onboarding / installer / dogfood | ✅ |
