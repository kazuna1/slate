#!/bin/sh
# Installs (or updates) Slate for macOS from the latest GitHub release.
#   curl -fsSL https://raw.githubusercontent.com/kazuna1/slate/main/install.sh | sh
set -e

URL="https://github.com/kazuna1/slate/releases/latest/download/Slate-macOS.zip"
DEST="/Applications"
[ -w "$DEST" ] || DEST="$HOME/Applications"
mkdir -p "$DEST"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

printf '\033[35mDownloading Slate...\033[0m\n'
curl -fsSL "$URL" -o "$TMP/Slate-macOS.zip"

printf '\033[35mInstalling to %s...\033[0m\n' "$DEST"
pkill -x Slate 2>/dev/null || true
rm -rf "$DEST/Slate.app"
ditto -x -k "$TMP/Slate-macOS.zip" "$DEST"

open "$DEST/Slate.app"
printf '\033[32mSlate is installed and running. Press Option + Space.\033[0m\n'
