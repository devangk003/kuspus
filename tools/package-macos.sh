#!/usr/bin/env bash
# Publish, sign, notarize and package KusPus for macOS as a DMG.
# macOS counterpart of installer/KusPus.iss + the release.yml publish step.
#
# Prerequisites:
#   tools/build-whisper-macos.sh      (payload: whisper-cli + dylibs + tiny.en)
#   A "Developer ID Application" certificate in the login keychain, and a
#   notarytool keychain profile:   xcrun notarytool store-credentials kuspus-notary
#
# Usage:
#   tools/package-macos.sh -v 1.5.0 [-b 202610151530] [-i "Developer ID Application: Name (TEAMID)"] [-p kuspus-notary] [--skip-notarize]
#
# Without -i the build is ad-hoc signed (local testing only — Gatekeeper will
# refuse it on other Macs, and TCC grants are lost on every rebuild).
#
# Signing order matters for notarization: every nested Mach-O (whisper-cli, the
# ggml/whisper dylibs, the .NET native libraries in MonoBundle) must carry a secure
# timestamp + the hardened runtime BEFORE the outer bundle is signed; then the
# app is notarized and stapled; then the DMG is built from the stapled app and
# itself notarized + stapled.

set -euo pipefail

VERSION=""
BUILD_NUMBER=""
IDENTITY="-"
PROFILE="kuspus-notary"
NOTARIZE=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    -v) VERSION="$2"; shift 2 ;;
    -b) BUILD_NUMBER="$2"; shift 2 ;;
    -i) IDENTITY="$2"; shift 2 ;;
    -p) PROFILE="$2"; shift 2 ;;
    --skip-notarize) NOTARIZE=0; shift ;;
    *) echo "unknown arg $1" >&2; exit 2 ;;
  esac
done
[[ -n "$VERSION" ]] || { echo "-v <version> is required (e.g. 1.5.0)" >&2; exit 2; }
# CFBundleVersion must increase for every build so LaunchServices treats a reinstall
# as an upgrade. A timestamp is monotonic; deriving it from the version is not
# (1.10.0 → "1100" would sort above 2.0.0 → "200").
[[ -n "$BUILD_NUMBER" ]] || BUILD_NUMBER="$(date +%Y%m%d%H%M)"
[[ "$IDENTITY" != "-" ]] || NOTARIZE=0

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_TMP_ERR="$(mktemp)"
trap 'rm -f "$OUT_TMP_ERR"' EXIT
PROJECT="$REPO_ROOT/src/KusPus.Mac.App/KusPus.Mac.App.csproj"
PAYLOAD="$REPO_ROOT/installer/payload/whisper-macos"
OUT="$REPO_ROOT/installer/Output"
PUBLISH="$REPO_ROOT/publish/osx-arm64"
ENTITLEMENTS="$REPO_ROOT/src/KusPus.Mac.App/Entitlements.plist"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

[[ -f "$PAYLOAD/whisper-cli" ]] || { echo "Run tools/build-whisper-macos.sh first (no payload at $PAYLOAD)" >&2; exit 1; }

# ── 1. publish the bundle ────────────────────────────────────────────────────
step "dotnet publish ($VERSION)"
rm -rf "$PUBLISH"
dotnet publish "$PROJECT" -c Release -r osx-arm64 -o "$PUBLISH" \
  -p:ApplicationDisplayVersion="$VERSION" \
  -p:ApplicationVersion="$BUILD_NUMBER" \
  -p:CodesignKey="$IDENTITY" \
  -p:CodesignEntitlements="$ENTITLEMENTS" \
  -p:EnableCodeSigning=true \
  -p:CreatePackage=false
# macios publishes the bundle next to the build output; copy it into our publish dir.
BUILT_APP="$REPO_ROOT/src/KusPus.Mac.App/bin/Release/net10.0-macos/osx-arm64/KusPus.app"
[[ -d "$BUILT_APP" ]] || { echo "KusPus.app not found at $BUILT_APP" >&2; exit 1; }
mkdir -p "$PUBLISH"
rm -rf "$PUBLISH/KusPus.app"
ditto "$BUILT_APP" "$PUBLISH/KusPus.app"
APP="$PUBLISH/KusPus.app"

# ── 2. deep-sign nested binaries, then the bundle ────────────────────────────
WHISPER_DIR="$APP/Contents/Resources/whisper"
WHISPER_ENTITLEMENTS="$REPO_ROOT/src/KusPus.Mac.App/WhisperEntitlements.plist"
if [[ "$IDENTITY" != "-" ]]; then
  step "Signing nested Mach-O binaries"
  find "$APP/Contents" -type f \( -name '*.dylib' -o -name 'whisper-cli' -o -perm -u+x \) \
    ! -path '*/MacOS/KusPus' -print0 \
    | while IFS= read -r -d '' f; do
        if file "$f" | grep -q 'Mach-O'; then
          if [[ "$(basename "$f")" == "whisper-cli" ]]; then
            # The subprocess needs library validation off (see WhisperEntitlements.plist);
            # dylibs carry no entitlements — only the executable's apply.
            codesign --force --timestamp --options runtime --entitlements "$WHISPER_ENTITLEMENTS" --sign "$IDENTITY" "$f"
          else
            codesign --force --timestamp --options runtime --sign "$IDENTITY" "$f"
          fi
        fi
      done
fi

# Signing rewrote whisper-cli + the dylibs, so the SHA256SUMS written at payload-build
# time no longer matches. Regenerate it from the signed files BEFORE sealing the
# bundle: the app reads the expected hash from this file (MacPaths.ExpectedWhisperSha256),
# and the bundle signature covers it.
if [[ -d "$WHISPER_DIR" ]]; then
  step "Regenerating whisper SHA256SUMS from the signed payload"
  ( cd "$WHISPER_DIR" && find . -type f ! -name SHA256SUMS | sed 's|^\./||' | sort | xargs shasum -a 256 ) > "$WHISPER_DIR/SHA256SUMS"
fi

if [[ "$IDENTITY" != "-" ]]; then
  step "Signing the app bundle"
  codesign --force --timestamp --options runtime --entitlements "$ENTITLEMENTS" --sign "$IDENTITY" "$APP"
fi
codesign --verify --deep --strict --verbose=2 "$APP"

# ── 2b. self-check: the bundled subprocess must run and match its sealed manifest ──
if [[ -x "$WHISPER_DIR/whisper-cli" ]]; then
  step "Smoke-testing the signed whisper-cli"
  if ! "$WHISPER_DIR/whisper-cli" --help >/dev/null 2>"$OUT_TMP_ERR"; then
    echo "whisper-cli failed to run from the signed bundle:" >&2; cat "$OUT_TMP_ERR" >&2; exit 1
  fi
  EXPECTED="$(awk '/ whisper-cli$/ {print $1}' "$WHISPER_DIR/SHA256SUMS")"
  ACTUAL="$(shasum -a 256 "$WHISPER_DIR/whisper-cli" | cut -d' ' -f1)"
  [[ "$EXPECTED" == "$ACTUAL" ]] || { echo "whisper-cli hash $ACTUAL != sealed $EXPECTED" >&2; exit 1; }
  if otool -l "$WHISPER_DIR/whisper-cli" | grep -A2 LC_RPATH | grep -qE 'path /'; then
    echo "whisper-cli still carries absolute rpaths — rebuild the payload (tools/build-whisper-macos.sh -f)" >&2; exit 1
  fi
  echo "    whisper-cli runs; hash matches sealed SHA256SUMS ($ACTUAL)"
fi

# ── 3. notarize + staple the app ─────────────────────────────────────────────
mkdir -p "$OUT"

# notarytool can exit 0 with status "Invalid"; check the status and print Apple's
# log on failure instead of finding out later from a confusing stapler error.
notarize() {
  local file="$1" result id status
  result="$(xcrun notarytool submit "$file" --keychain-profile "$PROFILE" --wait --output-format json)"
  id="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("id",""))' <<<"$result")"
  status="$(python3 -c 'import json,sys; print(json.load(sys.stdin).get("status",""))' <<<"$result")"
  echo "    notarization $id: $status"
  if [[ "$status" != "Accepted" ]]; then
    [[ -n "$id" ]] && xcrun notarytool log "$id" --keychain-profile "$PROFILE" || true
    echo "notarization failed for $file" >&2
    exit 1
  fi
}
if [[ $NOTARIZE -eq 1 ]]; then
  step "Notarizing the app"
  ZIP="$OUT/KusPus-$VERSION-app.zip"
  rm -f "$ZIP"
  ditto -c -k --keepParent "$APP" "$ZIP"
  notarize "$ZIP"
  xcrun stapler staple "$APP"
  rm -f "$ZIP"
fi

# ── 4. DMG ───────────────────────────────────────────────────────────────────
step "Building DMG"
DMG="$OUT/KusPus-$VERSION.dmg"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
# hdiutil prints a deprecation notice on macOS 26+ (diskutil image create is the
# successor) but still works everywhere we target; keep it for macOS 15 compatibility.
hdiutil create -volname "KusPus $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
if [[ "$IDENTITY" != "-" ]]; then
  # A signed DMG gets a cleaner Gatekeeper first-open and is required for a
  # notarization ticket that covers the container itself.
  codesign --force --sign "$IDENTITY" --timestamp "$DMG"
fi

if [[ $NOTARIZE -eq 1 ]]; then
  step "Notarizing the DMG"
  notarize "$DMG"
  xcrun stapler staple "$DMG"
fi

echo
echo "Artifact: $DMG ($(du -h "$DMG" | cut -f1))"
shasum -a 256 "$DMG"
if [[ $NOTARIZE -eq 1 ]]; then
  spctl -a -vv "$APP"
elif [[ "$IDENTITY" == "-" ]]; then
  echo "(ad-hoc signed: local testing only)"
else
  echo "(signed, not notarized: fine for this Mac; other Macs need right-click → Open)"
fi
