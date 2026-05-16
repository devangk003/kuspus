# Build whisper.cpp for Windows (x64, MSVC, CPU only).
# Produces installer/payload/whisper/{whisper.exe, whisper.dll, ggml*.dll}.
#
# Phase 3 — see TECH_SPEC §29.
#
# Steps (when implemented):
#   1. Detect MSVC via vswhere.
#   2. Enter VS Developer Environment via Enter-VsDevShell.
#   3. cmake -B build -DGGML_NATIVE=OFF -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_EXAMPLES=ON -DCMAKE_BUILD_TYPE=Release
#   4. cmake --build build --config Release --target whisper-cli
#   5. Copy outputs to installer/payload/whisper/
#   6. Compute SHA-256, write SHA256SUMS
#   7. Smoke test: whisper.exe -h exits 0
#
# GGML_NATIVE=OFF is critical: builds for portable x86-64-v2, not the build machine's CPU.

throw "build-whisper-windows.ps1 — not implemented yet (Phase 3)."
