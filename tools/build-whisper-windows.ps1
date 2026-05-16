<#
.SYNOPSIS
Build whisper.cpp for KusPus (Windows, x64, MSVC, CPU only).

.DESCRIPTION
Produces installer/payload/whisper/{whisper.exe, *.dll} from the pinned
third_party/whisper.cpp submodule. See TECH_SPEC §29.

Requirements:
- Visual Studio 2022 (Community is fine) or VS Build Tools 14.40+ with the
  "Desktop development with C++" workload.
- CMake 3.28+ on PATH.
- third_party/whisper.cpp checked out (git submodule update --init --recursive).

GGML_NATIVE=OFF is mandatory: builds for portable x86-64-v2, not the build
machine's specific CPU. Skipping this would ship a binary that crashes on
older CPUs.
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repoRoot     = Split-Path $PSScriptRoot -Parent
$whisperSrc   = Join-Path $repoRoot 'third_party\whisper.cpp'
$whisperBuild = Join-Path $whisperSrc 'build'
$payload      = Join-Path $repoRoot 'installer\payload\whisper'

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

# ── 1. Sanity checks ─────────────────────────────────────────────────────────
if (-not (Test-Path $whisperSrc -PathType Container)) {
    throw "Submodule missing at $whisperSrc. Run: git submodule update --init --recursive"
}
if (-not (Test-Path (Join-Path $whisperSrc 'CMakeLists.txt'))) {
    throw "$whisperSrc exists but has no CMakeLists.txt — submodule isn't checked out."
}

# ── 2. Locate MSVC via vswhere ───────────────────────────────────────────────
Write-Step "Locating MSVC..."
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio 2022 (Community or Build Tools) with the Desktop C++ workload."
}
$vsInstall = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsInstall) {
    throw "No Visual Studio install with the MSVC x64 C++ toolset was found."
}
Write-Host "    VS install: $vsInstall"

$vsDevShellModule = Join-Path $vsInstall 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
if (-not (Test-Path $vsDevShellModule)) {
    throw "Microsoft.VisualStudio.DevShell.dll not found at $vsDevShellModule."
}

# ── 3. Verify CMake is reachable ─────────────────────────────────────────────
Write-Step "Verifying CMake..."
# NOTE: do NOT redirect stderr (`2>&1`) on native exes in PS 5.1 — it wraps each line
# in an ErrorRecord and flips $? to false even on exit 0, breaking ErrorActionPreference.
$cmakeVersion = (& cmake --version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) {
    throw "CMake not found on PATH. Install CMake 3.28+ from https://cmake.org/download/."
}
Write-Host "    $cmakeVersion"

# ── 4. Enter VS Developer Environment ────────────────────────────────────────
Write-Step "Entering VS Developer Environment (x64)..."
Import-Module $vsDevShellModule
Enter-VsDevShell -VsInstallPath $vsInstall -DevCmdArguments '-arch=x64 -host_arch=x64' -SkipAutomaticLocation | Out-Null

# ── 5. Optional clean ────────────────────────────────────────────────────────
if ($Clean -and (Test-Path $whisperBuild)) {
    Write-Step "Cleaning previous build dir..."
    Remove-Item -Recurse -Force $whisperBuild
}

# ── 6. Configure ─────────────────────────────────────────────────────────────
Write-Step "Configuring whisper.cpp via cmake..."
Push-Location $whisperSrc
try {
    & cmake -B build `
        -DGGML_NATIVE=OFF `
        -DWHISPER_BUILD_TESTS=OFF `
        -DWHISPER_BUILD_EXAMPLES=ON `
        -DCMAKE_BUILD_TYPE=$Configuration
    if ($LASTEXITCODE -ne 0) { throw "cmake configure failed with exit code $LASTEXITCODE." }

    # ── 7. Build ──────────────────────────────────────────────────────────────
    Write-Step "Building whisper-cli ($Configuration)..."
    & cmake --build build --config $Configuration --target whisper-cli
    if ($LASTEXITCODE -ne 0) { throw "cmake build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

# ── 8. Collect outputs ───────────────────────────────────────────────────────
Write-Step "Copying outputs to $payload..."
if (Test-Path $payload) {
    Remove-Item -Recurse -Force $payload
}
New-Item -ItemType Directory -Force -Path $payload | Out-Null

$cliExeSearch = Get-ChildItem -Path $whisperBuild -Recurse -Filter 'whisper-cli.exe' | Select-Object -First 1
if (-not $cliExeSearch) {
    throw "whisper-cli.exe not produced. Check cmake build output above."
}

# Rename to whisper.exe so the C# runner finds it under the documented name.
Copy-Item $cliExeSearch.FullName (Join-Path $payload 'whisper.exe')

# All DLLs the cli depends on at runtime (whisper, ggml, plus any GGML backends).
$dllSources = Get-ChildItem -Path $whisperBuild -Recurse -Filter '*.dll' |
    Where-Object { $_.FullName -notmatch '\\Test' }
foreach ($dll in $dllSources) {
    Copy-Item $dll.FullName $payload -Force
}

# ── 9. SHA-256 manifest ──────────────────────────────────────────────────────
Write-Step "Computing SHA-256 manifest..."
$shaPath = Join-Path $payload 'SHA256SUMS'
Get-ChildItem -Path $payload -File |
    Where-Object { $_.Name -ne 'SHA256SUMS' } |
    ForEach-Object {
        $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$h  $($_.Name)"
    } | Set-Content -Encoding utf8 $shaPath

Write-Host ""
Get-Content $shaPath | ForEach-Object { Write-Host "    $_" }

# ── 10. Smoke test ───────────────────────────────────────────────────────────
Write-Step "Smoke testing whisper.exe -h..."
$exe = Join-Path $payload 'whisper.exe'
& $exe -h | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Smoke test failed: '$exe -h' exited with code $LASTEXITCODE."
}

Write-Host ""
Write-Step "Done. whisper.exe + DLLs are in: $payload"
Write-Host "    Update src/KusPus.Whisper/Resources/models.json: replace TODO_PIN with the"
Write-Host "    huggingface.co/ggerganov/whisper.cpp commit SHA you're pinning to, and the"
Write-Host "    TODO_FILL_AFTER_DOWNLOADING_VERIFIED_MODEL sha256 fields with the real hashes."
