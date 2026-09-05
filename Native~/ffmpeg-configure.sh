#!/usr/bin/env bash
set -eu

SCRIPT_ROOT="$(cd -- "$(dirname -- "$0")" && pwd)"
ROOT="$SCRIPT_ROOT/.deps"
VCBIN="$(cygpath -u "$VCToolsInstallDir")/bin/Hostx64/x64"
export PATH="$VCBIN:$ROOT/build-tools/usr/bin:/usr/bin:$PATH"
PREFIX="$ROOT/ffmpeg-minimal"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"

make -C "$ROOT/source/nv-codec-headers-e844e5b26f46bb77479f063029595293aa8f812d" PREFIX="$PREFIX" install
mkdir -p "$ROOT/ffmpeg-minimal-build"
cd "$ROOT/ffmpeg-minimal-build"

# MOV shares AV1/APV parsing code even in an H.264-only recording build.
# Enable those parsers so its coded-bitstream table is valid with MSVC.
"$ROOT/source/FFmpeg-1a748fe2cd43e3ead22fafb1b5b7d77f153898a8/configure" \
    --toolchain=msvc --arch=x86_64 --target-os=win32 --prefix="$PREFIX" --pkg-config=pkgconf \
    --disable-autodetect --disable-everything --disable-programs --disable-doc \
    --disable-network --disable-x86asm --disable-static --enable-shared \
    --disable-avdevice --disable-avfilter --disable-swscale \
    --enable-avcodec --enable-avformat --enable-avutil --enable-swresample \
    --enable-d3d11va --enable-ffnvcodec --enable-nvenc \
    --enable-encoder=h264_nvenc,aac --enable-decoder=pcm_s16le \
    --enable-parser=h264,av1,apv --enable-muxer=mp4 --enable-demuxer=mov,wav --enable-protocol=file
make -r -j"${MC_BUILD_JOBS:-8}"
make -r install
