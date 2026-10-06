#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
xcodegen generate
xcodebuild -project ClipHarbor.xcodeproj -scheme ClipHarbor -configuration Release \
  -derivedDataPath build -destination 'generic/platform=macOS' \
  ARCHS='arm64 x86_64' ONLY_ACTIVE_ARCH=NO CODE_SIGNING_ALLOWED=NO build
APP=build/Build/Products/Release/ClipHarbor.app
for architecture in arm64 x64; do
  dotnet publish shared/ClipHarbor.SyncHost/ClipHarbor.SyncHost.csproj -c Release \
    -r "osx-$architecture" --self-contained true -p:PublishSingleFile=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$APP/Contents/Helpers/Sync/$architecture"
done
identity=${SIGNING_IDENTITY:--}
signing_options=(--force --sign "$identity")
if [[ "$identity" != '-' ]]; then signing_options+=(--options runtime --timestamp); fi
while IFS= read -r -d '' binary; do
  if [[ "$(file -b "$binary")" == Mach-O* ]]; then
    if [[ "$(basename "$binary")" == ClipHarbor.SyncHost ]]; then
      codesign "${signing_options[@]}" --entitlements Resources/SyncHost.entitlements "$binary"
    else
      codesign "${signing_options[@]}" "$binary"
    fi
  fi
done < <(find "$APP/Contents/Helpers" -type f -print0)
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  codesign --force --options runtime --timestamp --sign "$SIGNING_IDENTITY" "$APP"
else
  codesign --force --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"
case "$(uname -m)" in arm64) native_architecture=arm64;; x86_64) native_architecture=x64;; *) exit 1;; esac
python3 integration/test-packaged-host.py "$APP/Contents/Helpers/Sync/$native_architecture/ClipHarbor.SyncHost"
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
