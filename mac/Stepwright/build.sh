#!/usr/bin/env bash
# Builds Stepwright.app from the Swift sources. No Xcode project involved.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$ROOT/build}"
APP="$OUT/Stepwright.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

echo "compiling"
xcrun swiftc \
  -O \
  -target arm64-apple-macos13.0 \
  -sdk "$(xcrun --show-sdk-path)" \
  -framework AppKit \
  -framework ApplicationServices \
  -framework CoreGraphics \
  -framework ImageIO \
  -framework Security \
  -o "$APP/Contents/MacOS/Stepwright" \
  "$ROOT/Sources"/*.swift

# An unsigned bundle cannot ask for the permissions it needs, so it is signed with an
# ad hoc signature. That is enough for macOS to give it a stable identity on this machine.
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "note: could not sign, permissions may be asked for again after each build"

echo "built $APP"
