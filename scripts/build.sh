#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
xcodegen generate
xcodebuild -project ClipHarbor.xcodeproj -scheme ClipHarbor -configuration Release \
  -derivedDataPath build -destination 'generic/platform=macOS' \
  ARCHS='arm64 x86_64' ONLY_ACTIVE_ARCH=NO CODE_SIGNING_ALLOWED=NO build
APP=build/Build/Products/Release/ClipHarbor.app
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  codesign --force --deep --options runtime --timestamp --sign "$SIGNING_IDENTITY" "$APP"
else
  codesign --force --deep --sign - "$APP"
fi
mkdir -p build/dist
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$APP" build/dist/ClipHarbor.zip
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  : "${APPLE_ID:?Missing APPLE_ID}" "${APPLE_APP_PASSWORD:?Missing APPLE_APP_PASSWORD}" "${APPLE_TEAM_ID:?Missing APPLE_TEAM_ID}"
  xcrun notarytool submit build/dist/ClipHarbor.zip --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait
  xcrun stapler staple "$APP"
  /usr/bin/ditto -c -k --sequesterRsrc --keepParent "$APP" build/dist/ClipHarbor.zip
fi
mkdir -p build/dmg
/usr/bin/ditto "$APP" build/dmg/ClipHarbor.app
ln -s /Applications build/dmg/Applications
hdiutil create -volname ClipHarbor -srcfolder build/dmg -ov -format UDZO build/dist/ClipHarbor.dmg
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  codesign --timestamp --sign "$SIGNING_IDENTITY" build/dist/ClipHarbor.dmg
  xcrun notarytool submit build/dist/ClipHarbor.dmg --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait
  xcrun stapler staple build/dist/ClipHarbor.dmg
fi
(cd build/dist && shasum -a 256 ClipHarbor.zip ClipHarbor.dmg > SHA256SUMS.txt)
