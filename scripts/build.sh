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
if [[ "${CI:-}" == true ]]; then
  # The signed application is self-contained; compiler caches are no longer needed.
  rm -rf build/Build/Intermediates.noindex build/ModuleCache.noindex build/SDKStatCaches.noindex
  rm -rf shared/ClipHarbor.Sync/bin shared/ClipHarbor.Sync/obj shared/ClipHarbor.SyncHost/bin shared/ClipHarbor.SyncHost/obj
fi
mkdir -p build/dist
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$APP" build/dist/ClipHarbor.zip
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  : "${APPLE_ID:?Missing APPLE_ID}" "${APPLE_APP_PASSWORD:?Missing APPLE_APP_PASSWORD}" "${APPLE_TEAM_ID:?Missing APPLE_TEAM_ID}"
  xcrun notarytool submit build/dist/ClipHarbor.zip --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait
  xcrun stapler staple "$APP"
  /usr/bin/ditto -c -k --sequesterRsrc --keepParent "$APP" build/dist/ClipHarbor.zip
fi
rm -rf build/dmg
mkdir -p build/dmg
trap 'rm -rf build/dmg' EXIT
/usr/bin/ditto "$APP" build/dmg/ClipHarbor.app
ln -s /Applications build/dmg/Applications
# APFS compression/cloning can make allocated blocks smaller than the bytes
# copied into the DMG. Size from logical lengths, allowing for filesystem overhead.
dmg_megabytes=$(python3 - build/dmg <<'PY'
import os
import stat
import sys

logical_bytes = 0
for root, directories, files in os.walk(sys.argv[1], followlinks=False):
    for name in directories + files:
        info = os.lstat(os.path.join(root, name))
        if stat.S_ISREG(info.st_mode):
            logical_bytes += ((info.st_size + 4095) // 4096) * 4096
        else:
            logical_bytes += 4096
megabyte = 1024 * 1024
print(max(512, (logical_bytes * 3 // 2 + megabyte - 1) // megabyte + 128))
PY
)
printf 'Creating DMG with %s MiB capacity (logical content plus 50%% and 128 MiB overhead).\n' "$dmg_megabytes"
df -h build/dist
du -sh build/dmg
hdiutil create -volname ClipHarbor -srcfolder build/dmg -size "${dmg_megabytes}m" -fs HFS+ -ov -format UDZO build/dist/ClipHarbor.dmg
rm -rf build/dmg
trap - EXIT
if [[ -n "${SIGNING_IDENTITY:-}" ]]; then
  codesign --timestamp --sign "$SIGNING_IDENTITY" build/dist/ClipHarbor.dmg
  xcrun notarytool submit build/dist/ClipHarbor.dmg --apple-id "$APPLE_ID" --password "$APPLE_APP_PASSWORD" --team-id "$APPLE_TEAM_ID" --wait
  xcrun stapler staple build/dist/ClipHarbor.dmg
fi
(cd build/dist && shasum -a 256 ClipHarbor.zip ClipHarbor.dmg > SHA256SUMS.txt)
