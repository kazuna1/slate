#!/bin/bash
# Builds a universal (Apple Silicon + Intel) Slate.app and zips it for a release.
#   mac/build.sh            → publish/mac/Slate.app and publish/Slate-macOS.zip
set -euo pipefail
cd "$(dirname "$0")"

# One version for both platforms: the Windows project file is the source of truth.
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ../src/Slate/Slate.csproj)
OUT=../publish
APP=$OUT/mac/Slate.app

swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/Slate"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Slate"
cp Resources/Slate.icns "$APP/Contents/Resources/"
sed "s/__VERSION__/$VERSION/g" Resources/Info.plist > "$APP/Contents/Info.plist"

# Ad-hoc signature: required on Apple Silicon. Not notarized (needs a paid Apple Developer account).
codesign --force --sign - "$APP"

rm -f "$OUT/Slate-macOS.zip"
ditto -c -k --keepParent "$APP" "$OUT/Slate-macOS.zip"

echo "Slate $VERSION → $APP"
echo "Release zip  → $OUT/Slate-macOS.zip ($(du -h "$OUT/Slate-macOS.zip" | cut -f1))"
