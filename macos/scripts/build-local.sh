#!/bin/zsh
set -eu
cd "$(dirname "$0")/.."

if [[ -z "${DEVELOPER_DIR:-}" && -d /Applications/Xcode.app/Contents/Developer ]]; then
    export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi

xcodebuild -project QuotaMonitor.xcodeproj -scheme QuotaMonitor \
    -configuration Release -destination 'platform=macOS' -derivedDataPath .build-app/local \
    CODE_SIGNING_ALLOWED=NO QUOTA_APP_GROUP= build

app="$PWD/.build-app/local/Build/Products/Release/QuotaMonitor.app"
widget="$app/Contents/PlugIns/QuotaWidget.appex"
local_entitlements="$PWD/.build-app/local/widget-local.entitlements"
cp Widget/QuotaWidget.entitlements "$local_entitlements"
/usr/libexec/PlistBuddy -c 'Delete :com.apple.security.application-groups' "$local_entitlements"
codesign --force --sign - --entitlements "$local_entitlements" "$widget"
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"
print -r -- "$app"
