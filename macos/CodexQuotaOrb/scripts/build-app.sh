#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_ROOT="$ROOT/.build-app"
DIST="$ROOT/dist"
APP="$DIST/Codex Quota Orb.app"
MODE="${1:---universal}"

rm -rf "$BUILD_ROOT" "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$DIST"

build_arch() {
  local arch="$1"
  local scratch="$BUILD_ROOT/$arch"
  swift build \
    --package-path "$ROOT" \
    --configuration release \
    --arch "$arch" \
    --scratch-path "$scratch"
  swift build \
    --package-path "$ROOT" \
    --configuration release \
    --arch "$arch" \
    --scratch-path "$scratch" \
    --show-bin-path
}

if [[ "$MODE" == "--native" ]]; then
  swift build \
    --package-path "$ROOT" \
    --configuration release \
    --scratch-path "$BUILD_ROOT/native"
  BIN_DIR="$(swift build \
    --package-path "$ROOT" \
    --configuration release \
    --scratch-path "$BUILD_ROOT/native" \
    --show-bin-path)"
  cp "$BIN_DIR/CodexQuotaOrb" "$APP/Contents/MacOS/CodexQuotaOrb"
else
  ARM_BIN_DIR="$(build_arch arm64 | tail -n 1)"
  X64_BIN_DIR="$(build_arch x86_64 | tail -n 1)"
  lipo -create \
    "$ARM_BIN_DIR/CodexQuotaOrb" \
    "$X64_BIN_DIR/CodexQuotaOrb" \
    -output "$APP/Contents/MacOS/CodexQuotaOrb"
fi

cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
chmod 755 "$APP/Contents/MacOS/CodexQuotaOrb"
xattr -cr "$APP"
codesign --force --deep --sign - "$APP"

"$APP/Contents/MacOS/CodexQuotaOrb" --self-test

rm -f "$DIST/Codex-Quota-Orb-macOS.dmg"
STAGING="$BUILD_ROOT/dmg"
mkdir -p "$STAGING"
ditto "$APP" "$STAGING/Codex Quota Orb.app"
ln -s /Applications "$STAGING/Applications"
hdiutil create \
  -volname "Codex Quota Orb" \
  -srcfolder "$STAGING" \
  -ov \
  -format UDZO \
  "$DIST/Codex-Quota-Orb-macOS.dmg"

echo "Built: $APP"
echo "Disk image: $DIST/Codex-Quota-Orb-macOS.dmg"
