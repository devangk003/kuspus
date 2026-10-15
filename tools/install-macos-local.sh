#!/usr/bin/env bash
# Build, sign and install KusPus into /Applications on THIS Mac, and write a DMG.
# Re-running it is the upgrade path: the new bundle replaces the old one in place.
#
# What makes an upgrade seamless (and what this script guarantees):
#   1. Same bundle id (com.devangk003.kuspus) + same path (/Applications/KusPus.app)
#      → settings, history, downloaded models and the Login Item carry over.
#   2. Same code-signing identity every build. macOS ties the Microphone / Input
#      Monitoring / Accessibility grants to the app's signature; an ad-hoc signature
#      changes per build and would force you to re-grant after every update. Without
#      an Apple Developer ID we use a local self-signed code-signing certificate,
#      created once and kept in its own keychain (~/Library/Keychains/kuspus-local.*).
#   3. CFBundleVersion is a timestamp, so every build is "newer" to LaunchServices.
#
# Usage: tools/install-macos-local.sh [-v 1.5.0] [--no-install] [--no-launch]

set -euo pipefail

VERSION="1.5.0"
INSTALL=1
LAUNCH=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    -v) VERSION="$2"; shift 2 ;;
    --no-install) INSTALL=0; shift ;;
    --no-launch) LAUNCH=0; shift ;;
    *) echo "unknown arg $1" >&2; exit 2 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
IDENTITY_NAME="KusPus Local Signing"
KEYCHAIN="$HOME/Library/Keychains/kuspus-local.keychain-db"
KEYCHAIN_PW="kuspus-local"
APP_DEST="/Applications/KusPus.app"

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

# ── 0. toolchain on PATH (dotnet-install.sh default location) ────────────────
if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="$HOME/.dotnet"
fi

# ── 1. whisper payload ───────────────────────────────────────────────────────
if [[ ! -f "$REPO_ROOT/installer/payload/whisper-macos/whisper-cli" ]]; then
  step "Building whisper payload"
  "$REPO_ROOT/tools/build-whisper-macos.sh"
fi

# ── 2. stable self-signed code-signing identity ──────────────────────────────
# Note: `security find-identity -v` lists only *trusted* identities, which a
# self-signed certificate never is — so look the certificate up by name instead.
identity_hash() {
  security find-certificate -c "$IDENTITY_NAME" -Z "$KEYCHAIN" 2>/dev/null | awk '/SHA-1 hash:/ {print $3; exit}'
}

ensure_identity() {
  if [[ -f "$KEYCHAIN" && -n "$(identity_hash)" ]]; then
    security unlock-keychain -p "$KEYCHAIN_PW" "$KEYCHAIN"
    return
  fi
  step "Creating local code-signing identity \"$IDENTITY_NAME\""
  local tmp
  tmp="$(mktemp -d)"
  cat > "$tmp/ext.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = v3
prompt = no
[dn]
CN = $IDENTITY_NAME
O = KusPus
[v3]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = critical,codeSigning
subjectKeyIdentifier = hash
EOF
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -keyout "$tmp/key.pem" -out "$tmp/cert.pem" -config "$tmp/ext.cnf" >/dev/null 2>&1
  openssl pkcs12 -export -out "$tmp/id.p12" -inkey "$tmp/key.pem" -in "$tmp/cert.pem" \
    -name "$IDENTITY_NAME" -passout "pass:$KEYCHAIN_PW" >/dev/null 2>&1
  if [[ ! -f "$KEYCHAIN" ]]; then
    security create-keychain -p "$KEYCHAIN_PW" "$KEYCHAIN"
    security set-keychain-settings "$KEYCHAIN"   # no auto-lock
  fi
  security unlock-keychain -p "$KEYCHAIN_PW" "$KEYCHAIN"
  security import "$tmp/id.p12" -k "$KEYCHAIN" -P "$KEYCHAIN_PW" -T /usr/bin/codesign -T /usr/bin/security >/dev/null
  security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PW" "$KEYCHAIN" >/dev/null
  # Add our keychain to the user search list (keep the existing ones).
  # One quoted path per line; keep paths intact even if they contain spaces.
  local current=() line
  while IFS= read -r line; do
    line="${line#"${line%%[![:space:]]*}"}"   # trim leading whitespace
    line="${line#\"}"; line="${line%\"}"
    [[ -n "$line" && "$line" != *"/$(basename "$KEYCHAIN")"* ]] && current+=("$line")
  done < <(security list-keychains -d user)
  security list-keychains -d user -s "${current[@]}" "$KEYCHAIN"
  rm -rf "$tmp"
}
ensure_identity
IDENTITY_HASH="$(identity_hash)"
[[ -n "$IDENTITY_HASH" ]] || { echo "could not create/find the local signing identity" >&2; exit 1; }
echo "    identity: $IDENTITY_NAME ($IDENTITY_HASH)"

# ── 3. publish + sign + DMG (reuses the release packaging script) ────────────
BUILD_NUMBER="$(date +%Y%m%d%H%M)"
step "Packaging $VERSION ($BUILD_NUMBER)"
# Sign by certificate hash — unambiguous even if a same-named cert exists elsewhere.
"$REPO_ROOT/tools/package-macos.sh" -v "$VERSION" -b "$BUILD_NUMBER" -i "$IDENTITY_HASH" --skip-notarize
APP_SRC="$(find "$REPO_ROOT/publish/osx-arm64" -maxdepth 1 -name 'KusPus.app' | head -1)"
DMG="$REPO_ROOT/installer/Output/KusPus-$VERSION.dmg"

# ── 4. install into /Applications (upgrade in place) ─────────────────────────
if [[ $INSTALL -eq 1 ]]; then
  step "Installing to $APP_DEST"
  if pgrep -x KusPus >/dev/null; then
    osascript -e 'tell application "KusPus" to quit' >/dev/null 2>&1 || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do pgrep -x KusPus >/dev/null || break; sleep 0.5; done
    pkill -x KusPus 2>/dev/null || true
  fi
  if [[ -d "$APP_DEST" ]]; then
    OLD_REQ="$(codesign -dr - "$APP_DEST" 2>/dev/null | grep designated || true)"
  fi
  # Copy first, then swap: a failed copy must not leave the user with no app at all.
  rm -rf "$APP_DEST.new"
  ditto "$APP_SRC" "$APP_DEST.new"
  rm -rf "$APP_DEST"
  mv "$APP_DEST.new" "$APP_DEST"
  NEW_REQ="$(codesign -dr - "$APP_DEST" 2>/dev/null | grep designated || true)"
  if [[ -z "${OLD_REQ:-}" ]]; then
    echo "    fresh install — onboarding will ask for Microphone / Input Monitoring / Accessibility."
  elif [[ "$OLD_REQ" != "$NEW_REQ" ]]; then
    echo "    note: signing requirement changed — macOS will ask for permissions again."
  else
    echo "    upgrade — same signing requirement, existing permission grants carry over."
  fi
  step "Verifying the installed bundle"
  W="$APP_DEST/Contents/Resources/whisper"
  if "$W/whisper-cli" --help >/dev/null 2>&1; then echo "    PASS whisper-cli runs"; else echo "    FAIL whisper-cli does not run (library validation / rpath?)"; exit 1; fi
  EXP="$(awk '/ whisper-cli$/ {print $1}' "$W/SHA256SUMS")"; ACT="$(shasum -a 256 "$W/whisper-cli" | cut -d' ' -f1)"
  if [[ "$EXP" == "$ACT" ]]; then echo "    PASS whisper-cli hash matches sealed SHA256SUMS"; else echo "    FAIL hash $ACT != sealed $EXP"; exit 1; fi
  if codesign --verify --deep --strict "$APP_DEST"; then echo "    PASS bundle signature valid"; else echo "    FAIL bundle signature invalid"; exit 1; fi
  if [[ $LAUNCH -eq 1 ]]; then
    step "Launching"
    open "$APP_DEST"
  fi
fi

echo
echo "Installed: $APP_DEST"
echo "DMG:       $DMG"
