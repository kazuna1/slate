#!/bin/bash
# Builds a universal (Apple Silicon + Intel) Slate.app and zips it for a release.
#   mac/build.sh            → mac/publish/Slate.app and mac/publish/Slate-macOS.zip
set -euo pipefail
cd "$(dirname "$0")"

# One version for both platforms: the VERSION file at the repo root.
VERSION=$(tr -d '[:space:]' < ../VERSION)
OUT=publish
APP=$OUT/Slate.app

swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/Slate"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Slate"
cp Resources/Slate.icns "$APP/Contents/Resources/"
sed "s/__VERSION__/$VERSION/g" Resources/Info.plist > "$APP/Contents/Info.plist"

# Signing. With SLATE_SIGN_IDENTITY (the release build sets it from the repo secrets), Slate is signed with a
# fixed self-signed certificate, so macOS sees every version as the same app and remembers permissions you
# granted. Without it, an ad-hoc signature (fine for local builds). Not notarized: that needs a paid Apple account.
if [ -n "${SLATE_SIGN_IDENTITY:-}" ]; then
  codesign --force --sign "$SLATE_SIGN_IDENTITY" ${SLATE_SIGN_KEYCHAIN:+--keychain "$SLATE_SIGN_KEYCHAIN"} "$APP"
else
  codesign --force --sign - "$APP"
fi
codesign -dr - "$APP" 2>&1 | tail -1

rm -f "$OUT/Slate-macOS.zip"
ditto -c -k --keepParent "$APP" "$OUT/Slate-macOS.zip"

echo "Slate $VERSION → $APP"
echo "Release zip  → $OUT/Slate-macOS.zip ($(du -h "$OUT/Slate-macOS.zip" | cut -f1))"
