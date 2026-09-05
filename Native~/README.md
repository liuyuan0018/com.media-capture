# Native Windows recording build

[Package guide (English)](../README.md) · [包说明（中文）](../README.zh-CN.md) · [Third-party notices](../ThirdPartyNotices.md)

## Components

- `FfmpegCapture.cpp` / `.h`: C ABI, Unity D3D11 texture ownership, GPU queries, bounded queues, NVENC H.264 worker and session shutdown.
- `FfmpegMux.cpp`: PCM16 WAV to AAC and final MP4; existing H.264 packets are copied without another video encode.
- `ProcessLoopbackCapture.cpp`: WASAPI process audio, QPC timestamps and audio diagnostics. It is compiled into the existing Windows helper executable.
- `D3D11CaptureCheck.cpp`: standalone hardware recording and cancellation check, independent of Unity.

The video plugin is loaded in the Unity process. The audio helper is a separate process started by C#. No FFmpeg executable is distributed or launched by the recording path.

## Prerequisites

- Windows x64; WASAPI process loopback needs Windows build 20348 or later.
- NVIDIA GPU with H.264 NVENC and a compatible driver. The pinned SDK 13.0 headers specify driver 570 or newer; runtime tests used RTX 3070 / 610.47.
- Visual Studio 2022 C++ desktop workload and Windows SDK 10.0.20348 or newer.
- Git for Windows with Bash. The default path is `C:\Program Files\Git\bin\bash.exe`.
- PowerShell and Windows `tar` with zstd support for the two local build-tool packages.

## Rebuild

Close Unity before replacing DLLs that it has loaded. From this directory:

```powershell
# Build the four FFmpeg libraries from the included, hash-checked sources.
.\build-ffmpeg.ps1

# Build the C++ recording DLL and deploy it plus FFmpeg DLLs into Runtime/Plugins/x86_64.
.\build-native.ps1

# Build the DLL and standalone check without changing Unity's plugin directory.
.\build-native.ps1 -TestOnly -BuildCheck
```

`build-native.ps1` calls `get-ffmpeg.ps1`, which reuses `.deps/ffmpeg-minimal` or builds it when absent. `get-ffmpeg.ps1 -Rebuild` forces a source rebuild. `build-native.ps1 -FfmpegRoot <path>` accepts a custom ABI-compatible FFmpeg installation with headers, import libraries and the four DLLs. The builder uses MSVC C++17, `/MD`, `/W4` and `/WX` for project code.

`build-ffmpeg.ps1 -BashPath <path> -Jobs 8` supports a different Git Bash location. The script finds Visual Studio with `vswhere`, checks archived source hashes, extracts into `.deps/source`, and downloads pinned GNU Make/pkgconf packages into `.deps/build-tools`. It does not modify the installed Git or Visual Studio directories. Its log is `.deps/build-ffmpeg.log`.

FFmpeg's MSVC installer places import libraries beside the DLLs; the script also copies those `.lib` files into `lib` for the native plugin build.

### Windows audio helper

Build `WindowsMediaCaptureHelper.vcxproj` as `Release|x64` using MSBuild. Its post-build step copies the executable to `Runtime/Unity/Resources/GameFrameworkMediaCapture/WindowsMediaCaptureHelper.bytes`. Unity loads that TextAsset and extracts it into each recording's session directory.

The helper still contains the previous Media Foundation image-sequence encoder. Default native recording uses only its WASAPI process-audio operation.

## Fixed sources and configuration

`ffmpeg-dependency.json` records exact commits, source archive hashes and build-tool downloads. Complete upstream FFmpeg and NVIDIA header sources are included under `ThirdPartySources`; no upstream source patches are applied.

`ffmpeg-configure.sh` enables only the two output encoders (`h264_nvenc`, `aac`), PCM16 decoding, MOV/WAV reading, MP4 writing, file access, D3D11 hardware frames and their internal dependencies. MOV's common bitstream code also needs AV1/APV parsers enabled for this MSVC build. Those parsers do not enable AV1 recording.

The build disables automatic external dependency detection, programs, networking, filters, devices, software scaling and external x86 assembly. It does not enable GPL, nonfree or version3. `avcodec_license()` reports **LGPL version 2.1 or later**. FFmpeg includes some internal code under permissive licenses; notices remain in the complete source and the upstream license summary.

The broad BtbN shared build was used during initial development but is not the final runtime dependency. The final source build keeps only the libraries needed by this recorder. See [third-party notices](../ThirdPartyNotices.md) before redistributing a Player; Unity does not copy `Native~` into Player builds automatically.

## Ownership and threading

At frame end, C# calls `Graphics.Blit(null, outputTexture)` when source and output dimensions match, writing the current framebuffer directly into the BGRA output texture. Only when scaling is required does it allocate an RGBA intermediate, capture the complete source image and blit into BGRA. It then submits a plugin event, which copies into a free native texture and inserts a D3D11 completion query. Only completed textures reach the encoder worker. A texture remains unavailable until all of its frame leases, including references retained for frame duplication, are released.

The plugin shares Unity's device with the worker and enables D3D11 multithread protection. It preserves the original setting and restores it after the last session releases the device. Control exports and render callbacks serialize session lifetime changes; encoding uses its own queue lock. Pending render requests use IDs so late events after cancellation do not dereference deleted requests.

Normal stop drains completed video work, closes intermediate video, encodes AAC and writes the final MP4. Cancellation stops new work; C# keeps textures alive until the native worker finishes and session destruction succeeds. Assembly reload and plugin unload request shutdown. The plugin neither owns nor destroys Unity's graphics device.

This avoids CPU readback of uncompressed video, not all GPU copies or CPU work. Audio and compressed packets use CPU memory. The managed capture code allocates or recreates its RGBA intermediate only when source and output dimensions differ, and releases it when they match again. The fixed-size BGRA output texture and native pointer remain unchanged. GPU scaling fills the output, so a changed aspect ratio stretches the image. The plugin reports device/encoder failures; it does not recreate an unsupported device or select another graphics API.

## Checks and evidence

With the DLL built, add `.deps/ffmpeg-minimal/bin` to the process PATH and run:

```powershell
.\bin\ffmpeg-native\D3D11CaptureCheck.exe "D:\CaptureChecks\normal"
.\bin\ffmpeg-native\D3D11CaptureCheck.exe "D:\CaptureChecks\cancel" abort
```

The check needs a real NVIDIA D3D11 device. It produces files under the chosen prefix. The normal case submits 90 frames at 1280×720 / 30 fps and muxes a stereo test tone. The cancellation case also invokes a deliberately late render event after session destruction.

[DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md) records actual Unity/native validation and its limits. Local samples and test requests live under ignored `artifacts/` and `.deps/`. They are not runtime package assets or evidence of validation on other GPUs.
