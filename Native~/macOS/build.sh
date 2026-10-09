#!/bin/bash
set -euo pipefail
capture_dir="$(cd "$(dirname "$0")" && pwd)"
capture_package="$(cd "$capture_dir/../.." && pwd)"
capture_unity="${UNITY_PLUGIN_API:-/Applications/Unity/Hub/Editor/6000.5.7f1/Unity.app/Contents/Resources/PluginAPI}"
capture_bundle="$capture_package/Runtime/Plugins/macOS/MediaCaptureMetal.bundle"
capture_build="$capture_dir/build"
mkdir -p "$capture_build" "$capture_bundle/Contents/MacOS"
xcrun clang++ -std=c++17 -fobjc-arc -fblocks -O2 -g -Wall -Wextra -Wno-deprecated-declarations \
  -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 -bundle -fvisibility=hidden \
  -I "$capture_unity" "$capture_dir/MetalCapture.mm" \
  -framework Foundation -framework Metal -framework CoreVideo -framework CoreMedia \
  -framework VideoToolbox -framework AVFoundation -framework AudioToolbox \
  -o "$capture_build/MediaCaptureMetal"
cp "$capture_build/MediaCaptureMetal" "$capture_bundle/Contents/MacOS/MediaCaptureMetal"
cat > "$capture_bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>CFBundleExecutable</key><string>MediaCaptureMetal</string><key>CFBundleIdentifier</key><string>com.media-capture.metal</string><key>CFBundlePackageType</key><string>BNDL</string><key>CFBundleVersion</key><string>0.4.0</string></dict></plist>
PLIST
codesign --force --sign - "$capture_bundle"
