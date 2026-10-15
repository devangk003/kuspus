#!/usr/bin/env bash
# Regenerate src/KusPus.Mac.App/Resources/KusPus.icns from icons/icon-mac.svg (the
# brand mark on a dark Apple-grid tile; icons/icon.svg stays the bare mark).
# Uses only tools that ship with macOS: QuickLook (SVG → PNG), sips (resize), iconutil.
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

qlmanage -t -s 1024 -o "$TMP" "$REPO_ROOT/icons/icon-mac.svg" >/dev/null 2>&1
SRC="$TMP/icon-mac.svg.png"
[[ -f "$SRC" ]] || { echo "QuickLook could not render icons/icon-mac.svg" >&2; exit 1; }

ICONSET="$TMP/KusPus.iconset"
mkdir -p "$ICONSET"
for sz in 16 32 128 256 512; do
  sips -z "$sz" "$sz" "$SRC" --out "$ICONSET/icon_${sz}x${sz}.png" >/dev/null
  d=$((sz * 2))
  sips -z "$d" "$d" "$SRC" --out "$ICONSET/icon_${sz}x${sz}@2x.png" >/dev/null
done
mkdir -p "$REPO_ROOT/src/KusPus.Mac.App/Resources"
iconutil -c icns "$ICONSET" -o "$REPO_ROOT/src/KusPus.Mac.App/Resources/KusPus.icns"
echo "Wrote src/KusPus.Mac.App/Resources/KusPus.icns"
