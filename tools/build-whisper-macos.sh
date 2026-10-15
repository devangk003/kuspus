#!/usr/bin/env bash
# Populate installer/payload/whisper-macos/ with whisper-cli (+ dylibs) built from
# the pinned whisper.cpp source, plus the bundled tiny.en model and a SHA256SUMS
# manifest. macOS counterpart of tools/build-whisper-windows.ps1.
#
# Why build from source here (the Windows script downloads a prebuilt zip):
# upstream publishes no macOS CLI binary, only an XCFramework for Swift. Building
# takes ~1–3 min on Apple Silicon with CMake + the Xcode command-line tools, and
# gives us a Metal-enabled binary we can code-sign ourselves.
#
# Idempotent: re-running with the same -t tag is a no-op unless -f is passed.
#
# Usage:
#   tools/build-whisper-macos.sh [-t v1.8.4] [-f]
#
# Outputs (installer/payload/whisper-macos/):
#   whisper-cli            Metal-enabled CLI (arm64)
#   *.dylib                libwhisper + ggml runtime libraries
#   models/ggml-tiny.en.bin
#   SHA256SUMS             sha256 of every file above (whisper-cli line feeds
#                          BuildConstants.ExpectedWhisperSha256 via
#                          build/WhisperSha.targets)
#   .tag                   idempotency marker

set -euo pipefail

TAG="v1.8.4"
FORCE=0
while getopts "t:f" opt; do
  case "$opt" in
    t) TAG="$OPTARG" ;;
    f) FORCE=1 ;;
    *) echo "usage: $0 [-t tag] [-f]" >&2; exit 2 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$REPO_ROOT/third_party/whisper.cpp"
PAYLOAD="$REPO_ROOT/installer/payload/whisper-macos"
BUILD="$REPO_ROOT/.local-temp/whisper-macos-build"
MODEL_CACHE="$REPO_ROOT/.local-temp/model-cache"
MANIFEST="$REPO_ROOT/src/KusPus.Whisper/Resources/models.json"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

# ── 1. idempotency ───────────────────────────────────────────────────────────
if [[ $FORCE -eq 0 && -f "$PAYLOAD/.tag" && "$(cat "$PAYLOAD/.tag")" == "$TAG" ]]; then
  echo "Payload already at $TAG (use -f to rebuild)."
  echo "  Path: $PAYLOAD"
  exit 0
fi

command -v cmake >/dev/null || { echo "cmake not found — brew install cmake" >&2; exit 1; }
xcode-select -p >/dev/null || { echo "Xcode command-line tools not found" >&2; exit 1; }

# ── 2. source at the pinned tag ──────────────────────────────────────────────
step "Checking out whisper.cpp $TAG"
if [[ ! -f "$SRC/CMakeLists.txt" ]]; then
  git -C "$REPO_ROOT" submodule update --init --depth 1 third_party/whisper.cpp
fi
git -C "$SRC" fetch --depth 1 origin "tag" "$TAG" 2>/dev/null || git -C "$SRC" fetch --tags origin
git -C "$SRC" checkout -q "$TAG"
echo "    $(git -C "$SRC" rev-parse HEAD)"

# ── 3. build (Metal on, portable arm64) ──────────────────────────────────────
# A stale tree from another tag leaves old dylib versions behind that map to the
# same install name; start clean whenever forced or the tag changed.
if [[ $FORCE -eq 1 || ! -f "$BUILD/.tag" || "$(cat "$BUILD/.tag" 2>/dev/null)" != "$TAG" ]]; then
  rm -rf "$BUILD"
fi
mkdir -p "$BUILD" && echo "$TAG" > "$BUILD/.tag"
step "Configuring CMake"
cmake -S "$SRC" -B "$BUILD" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
  -DGGML_METAL=ON \
  -DGGML_METAL_EMBED_LIBRARY=ON \
  -DGGML_NATIVE=OFF \
  -DWHISPER_BUILD_TESTS=OFF \
  -DWHISPER_BUILD_SERVER=OFF \
  -DWHISPER_BUILD_EXAMPLES=ON \
  >/dev/null
step "Building whisper-cli"
cmake --build "$BUILD" --config Release --target whisper-cli -j "$(sysctl -n hw.ncpu)" >/dev/null

# ── 4. copy binary + dylibs; make the binary find its libraries next to itself ──
step "Staging payload"
rm -rf "$PAYLOAD"
mkdir -p "$PAYLOAD/models"
cp "$BUILD/bin/whisper-cli" "$PAYLOAD/whisper-cli"
# Ship each dylib under the exact name its install name advertises (otool -D →
# @rpath/libggml.0.dylib etc.), as a REAL file. No symlinks: MSBuild's
# BundleResource glob follows them into duplicate items, and codesign/notarize
# reject dangling links inside Contents/Resources.
find "$BUILD" -name '*.dylib' -type f | while read -r lib; do
  install_name="$(otool -D "$lib" | sed -n 2p | tr -d '[:space:]')"
  cp "$lib" "$PAYLOAD/$(basename "$install_name")"
done
chmod +x "$PAYLOAD/whisper-cli"

# ── 4b. rpath hygiene ────────────────────────────────────────────────────────
# CMake leaves LC_RPATH entries pointing into this build tree on the binary AND on
# every dylib (they reference each other via @rpath). Shipping those leaks the
# developer's home path and, on the build machine, makes dyld prefer the unsigned
# build-tree copies over the bundled ones. Keep only @executable_path (binary) /
# @loader_path (dylibs), then re-sign ad-hoc — install_name_tool invalidates the
# linker's signature.
strip_rpaths() {
  local f="$1" keep="$2"
  otool -l "$f" | awk '/LC_RPATH/ {grab=1} grab && /path / {print $2; grab=0}' | while read -r rp; do
    [[ "$rp" == "$keep" ]] || install_name_tool -delete_rpath "$rp" "$f" 2>/dev/null || true
  done
  otool -l "$f" | grep -A2 LC_RPATH | grep -q "path $keep " || install_name_tool -add_rpath "$keep" "$f"
  codesign --force --sign - "$f" >/dev/null 2>&1
}
strip_rpaths "$PAYLOAD/whisper-cli" "@executable_path"
for lib in "$PAYLOAD"/*.dylib; do strip_rpaths "$lib" "@loader_path"; done

# ── 5. bundled tiny.en (pinned URL + SHA from models.json) ───────────────────
step "Fetching bundled tiny.en model"
TINY_URL="$(python3 -c "import json,sys; m=[x for x in json.load(open(sys.argv[1]))['models'] if x['id']=='ggml-tiny.en'][0]; print(m['files'][0]['url'])" "$MANIFEST")"
TINY_SHA="$(python3 -c "import json,sys; m=[x for x in json.load(open(sys.argv[1]))['models'] if x['id']=='ggml-tiny.en'][0]; print(m['files'][0]['sha256'])" "$MANIFEST")"
mkdir -p "$MODEL_CACHE"
CACHED="$MODEL_CACHE/$TINY_SHA-ggml-tiny.en.bin"
if [[ ! -f "$CACHED" ]]; then
  curl -fL --retry 3 -o "$CACHED.part" "$TINY_URL"
  mv "$CACHED.part" "$CACHED"
fi
ACTUAL="$(shasum -a 256 "$CACHED" | cut -d' ' -f1)"
if [[ "$ACTUAL" != "$TINY_SHA" ]]; then
  rm -f "$CACHED"
  echo "tiny.en SHA mismatch: expected $TINY_SHA got $ACTUAL (cache deleted; rerun)" >&2
  exit 1
fi
cp "$CACHED" "$PAYLOAD/models/ggml-tiny.en.bin"

# ── 6. smoke test + manifest ─────────────────────────────────────────────────
# Run from a copy OUTSIDE the build tree so a bundling mistake can't be masked by
# dyld finding libraries in the CMake output.
step "Smoke-testing whisper-cli (isolated copy)"
SMOKE="$(mktemp -d)"
cp -R "$PAYLOAD"/. "$SMOKE/"
"$SMOKE/whisper-cli" --help >/dev/null 2>&1 || { echo "whisper-cli failed to run outside the build tree (rpath/dylib problem)" >&2; rm -rf "$SMOKE"; exit 1; }
if otool -l "$PAYLOAD/whisper-cli" | grep -A2 LC_RPATH | grep -qE 'path /'; then
  echo "whisper-cli still has absolute rpaths" >&2; rm -rf "$SMOKE"; exit 1
fi
rm -rf "$SMOKE"

step "Writing SHA256SUMS"
( cd "$PAYLOAD" && find . -type f ! -name SHA256SUMS ! -name .tag | sed 's|^\./||' | sort | xargs shasum -a 256 ) > "$PAYLOAD/SHA256SUMS"
echo "$TAG" > "$PAYLOAD/.tag"

echo
echo "Payload ready: $PAYLOAD"
grep ' whisper-cli$' "$PAYLOAD/SHA256SUMS"
du -sh "$PAYLOAD" | cut -f1 | sed 's/^/  total: /'
