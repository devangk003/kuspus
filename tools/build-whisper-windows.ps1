<#
.SYNOPSIS
Populate installer/payload/whisper/ with whisper.exe + DLLs from a pinned
whisper.cpp GitHub release.

.DESCRIPTION
Phase 12 — see TECH_SPEC §29. Downloads `whisper-bin-x64.zip` from the
ggerganov/whisper.cpp release for $Tag, extracts to a tag-scoped cache,
copies whisper-cli.exe (renamed to whisper.exe) + all runtime DLLs into
installer/payload/whisper/, generates SHA256SUMS, and smoke-tests the
binary.

Build-from-source via the third_party/whisper.cpp submodule was the
original plan (and lives in git history at the prior version of this
file). The dogfood team picked download-prebuilt for v1.0 because:
  - No local MSVC + CMake toolchain required on dev machines or CI
  - Faster turnaround (a few seconds vs several minutes)
  - Upstream binaries are GGML_NATIVE=OFF (portable x86-64-v2) by default

Idempotent: re-running with the same -Tag is a no-op unless -Force is
passed (we write a .tag marker into the payload dir to detect prior runs).

.PARAMETER Tag
whisper.cpp release tag (e.g. "v1.8.4"). Defaults to the version pinned
for KusPus v1.0. Override to test newer releases.

.PARAMETER Force
Re-download + re-extract even if the payload is already at $Tag.
#>

[CmdletBinding()]
param(
    [string]$Tag = 'v1.8.4',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$payload  = Join-Path $repoRoot 'installer\payload\whisper'
$cacheDir = Join-Path $repoRoot '.local-temp\whisper-cache'

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

# ── 1. Idempotency — skip if already populated with the requested tag ─────
$tagMarker = Join-Path $payload '.tag'
if (-not $Force -and (Test-Path $tagMarker)) {
    $existing = (Get-Content -LiteralPath $tagMarker -Raw).Trim()
    if ($existing -eq $Tag) {
        Write-Host "Payload already at $Tag (use -Force to re-download)." -ForegroundColor Green
        Write-Host "  Path: $payload"
        exit 0
    }
}

# ── 2. Compute artifact URL ───────────────────────────────────────────────
# Naming convention: https://github.com/ggerganov/whisper.cpp/releases/download/<tag>/whisper-bin-x64.zip
# The release page also publishes BLAS / cuBLAS / Vulkan variants — we want
# the plain CPU build because PRD §1 promises CPU-only for v1.0.
$artifact = 'whisper-bin-x64.zip'
$url      = "https://github.com/ggerganov/whisper.cpp/releases/download/$Tag/$artifact"

# ── 3. Download (cached by tag) ───────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
$zipPath = Join-Path $cacheDir "$Tag-$artifact"
if (-not (Test-Path $zipPath) -or $Force) {
    Write-Step "Downloading $url"
    # Invoke-WebRequest follows redirects + works in PS 5.1.
    # ProgressPreference=SilentlyContinue makes the download ~10× faster on
    # Windows PowerShell (the progress bar is a known IWR perf hog).
    $previousProgressPreference = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgressPreference
    }
    Write-Host "    Wrote $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"
}
else {
    Write-Host "    Cached: $zipPath" -ForegroundColor Green
}

# ── 4. Extract to a tag-scoped subdirectory ───────────────────────────────
$extractDir = Join-Path $cacheDir "$Tag-extracted"
if (Test-Path $extractDir) {
    Remove-Item -Recurse -Force $extractDir
}
Write-Step "Extracting"
Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force

# ── 5. Reset payload directory ────────────────────────────────────────────
if (Test-Path $payload) {
    Remove-Item -Recurse -Force $payload
}
New-Item -ItemType Directory -Force -Path $payload | Out-Null

# ── 6. Locate the CLI binary ──────────────────────────────────────────────
# v1.7+ ships `whisper-cli.exe`; older releases used `main.exe`. Accept
# either so the script works across the v1.6..v1.8+ range.
$cli = Get-ChildItem -Path $extractDir -Recurse -File |
    Where-Object { $_.Name -in @('whisper-cli.exe', 'main.exe') } |
    Select-Object -First 1
if (-not $cli) {
    throw "Couldn't find whisper-cli.exe or main.exe in $extractDir. Did the artifact layout change for $Tag?"
}
Write-Host "    Found CLI: $($cli.Name) at $($cli.FullName)"

# ── 7. Copy CLI as whisper.exe + all runtime DLLs ─────────────────────────
# The C# WhisperRunner expects the binary at <payload>/whisper.exe regardless
# of upstream naming; rename happens here, not at install time.
Copy-Item -LiteralPath $cli.FullName -Destination (Join-Path $payload 'whisper.exe')

$dlls = Get-ChildItem -Path $extractDir -Recurse -File -Filter '*.dll'
foreach ($dll in $dlls) {
    Copy-Item -LiteralPath $dll.FullName -Destination $payload -Force
}
Write-Host "    Copied $($dlls.Count) DLL(s) + whisper.exe"

# ── 8. Tag marker for idempotency ─────────────────────────────────────────
Set-Content -LiteralPath $tagMarker -Value $Tag -Encoding utf8

# ── 9. SHA-256 manifest ───────────────────────────────────────────────────
Write-Step "Computing SHA-256 manifest"
$shaPath = Join-Path $payload 'SHA256SUMS'
$lines = Get-ChildItem -Path $payload -File |
    Where-Object { $_.Name -notin @('SHA256SUMS', '.tag') } |
    Sort-Object Name |
    ForEach-Object {
        $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$h  $($_.Name)"
    }
Set-Content -LiteralPath $shaPath -Value $lines -Encoding utf8
Write-Host ""
$lines | ForEach-Object { Write-Host "    $_" }

# ── 10. Smoke test ────────────────────────────────────────────────────────
Write-Step "Smoke testing whisper.exe -h"
$exe = Join-Path $payload 'whisper.exe'
# Accept exit codes 0 OR 1:
#   - 0: newer whisper-cli (v1.7+) returns success for -h
#   - 1: legacy main.exe (≤v1.6) returns failure for "unknown argument" with
#        usage to stderr — but the binary loaded its DLLs and ran its arg
#        parser, which is what we're verifying here.
# Hard DLL-load crashes show up as large negative exit codes (e.g.
# -1073741515 STATUS_DLL_NOT_FOUND), which we DO want to fail on.
& $exe -h 2>$null | Out-Null
$smokeExit = $LASTEXITCODE
if ($smokeExit -notin @(0, 1)) {
    throw "Smoke test failed: '$exe -h' exited with code $smokeExit (expected 0 or 1; large negatives mean missing DLL)."
}
# Reset so the script itself exits 0 even when the smoke check used a binary
# that returned 1 for -h. Without this, PowerShell propagates the last
# native exit code as the script's own exit code, breaking CI gating.
$global:LASTEXITCODE = 0

Write-Host ""
Write-Step "Done."
Write-Host "    whisper.exe + DLLs: $payload"
Write-Host "    Pinned to tag:      $Tag"
Write-Host "    SHA256 manifest:    $shaPath"
Write-Host ""
Write-Host "Next: tools/IconBuilder for icon.ico, then iscc.exe installer/KusPus.iss"
