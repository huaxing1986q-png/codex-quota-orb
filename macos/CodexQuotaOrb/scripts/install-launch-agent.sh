#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SOURCE_APP="${1:-$ROOT/dist/Codex Quota Orb.app}"
TARGET_APP="$HOME/Applications/Codex Quota Orb.app"
PLIST="$HOME/Library/LaunchAgents/com.local.codex-quota-orb.plist"
LABEL="com.local.codex-quota-orb"
DOMAIN="gui/$(id -u)"

if [[ ! -d "$SOURCE_APP" ]]; then
  echo "App bundle not found: $SOURCE_APP" >&2
  echo "Run scripts/build-app.sh first." >&2
  exit 1
fi

mkdir -p "$HOME/Applications" "$HOME/Library/LaunchAgents"
rm -rf "$TARGET_APP"
ditto "$SOURCE_APP" "$TARGET_APP"

plutil -create xml1 "$PLIST"
plutil -insert Label -string "$LABEL" "$PLIST"
plutil -insert ProgramArguments -json \
  "[\"$TARGET_APP/Contents/MacOS/CodexQuotaOrb\"]" "$PLIST"
plutil -insert RunAtLoad -bool true "$PLIST"
plutil -insert KeepAlive -bool false "$PLIST"
plutil -insert ProcessType -string Interactive "$PLIST"
plutil -insert LimitLoadToSessionType -string Aqua "$PLIST"

launchctl bootout "$DOMAIN/$LABEL" 2>/dev/null || true
launchctl bootstrap "$DOMAIN" "$PLIST"
launchctl kickstart "$DOMAIN/$LABEL"

echo "Installed: $TARGET_APP"
echo "Login item: $PLIST"
