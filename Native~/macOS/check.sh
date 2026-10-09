#!/bin/bash
set -euo pipefail
capture_dir="$(cd "$(dirname "$0")" && pwd)"
capture_unity="${UNITY_PLUGIN_API:-/Applications/Unity/Hub/Editor/6000.5.7f1/Unity.app/Contents/Resources/PluginAPI}"
capture_out="${1:?Usage: check.sh /absolute/output/directory}"
mkdir -p "$capture_out"
xcrun clang++ -std=c++17 -fobjc-arc -fblocks -O2 -Wno-deprecated-declarations \
  -I "$capture_unity" "$capture_dir/MetalCaptureCheck.mm" \
  -framework Foundation -framework Metal -framework CoreVideo -framework CoreMedia \
  -framework VideoToolbox -framework AVFoundation -framework AudioToolbox -o "$capture_out/MetalCaptureCheck"
"$capture_out/MetalCaptureCheck" "$capture_out/normal"
"$capture_out/MetalCaptureCheck" "$capture_out/cancel" abort
