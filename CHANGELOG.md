# Changelog

## Unreleased

- Capture directly into the fixed BGRA output texture when Game View dimensions match; allocate the RGBA intermediate only for scaling and release it when matching dimensions resume. Preserve frame-end capture, native texture ownership and complete-image scaling; report only allocated capture texture memory.
- Read the Game View render size directly when starting native recording from an Editor tool; prevent tool-window dimensions from causing a first-frame size mismatch or incorrect output aspect ratio.
- Keep native output dimensions fixed and recreate the GPU source texture when Game View changes size, allowing recording to continue with GPU scaling.
- Expand the English and Chinese guides with architecture selection, component responsibilities, GPU resource ownership, fixed output dimensions and encoding-quality limitations.

## 0.3.0 - 2026-09-04

- Make native Windows x64 / D3D11 / NVIDIA NVENC the default video backend. Game View textures stay on the GPU through capture, format conversion and hardware encoder submission.
- Migration: select `RecordingVideoBackend.ImageSequence` explicitly to retain the previous Windows/macOS path. The new default does not fall back automatically on unsupported hardware or platforms.
- Add bounded native texture storage, GPU completion queries, frame lifetime tracking and QPC-based constant-frame-rate output.
- Encode H.264 during recording; encode WASAPI WAV to AAC and copy video packets into MP4 on stop, publishing the destination only after success.
- Handle cancellation, pending render events, recorder destruction and editor shutdown; retain diagnostic manifests and explicit failure messages.
- Report audio discontinuities, timestamp errors and inserted silence. Use -1 for unavailable exact audio-loss counts.
- Add output dimensions, NVENC quality, texture-pool and encoding-lag settings. Preserve image-sequence and custom-backend recording as explicit alternatives.
- Bundle a minimal LGPL 2.1+ FFmpeg shared build, pinned corresponding sources, rebuild scripts and third-party notices.
- Rewrite English and Chinese guides with capture scope, native integration, design decisions, quality tradeoffs and validation limits.

## 0.2.0 - 2026-08-21

- Add Windows process-specific WASAPI loopback capture for Unity, Wwise, and other audio emitted by the Unity process.
- Use the Windows QPC clock for Windows audio duration and framebuffer timestamps.
- Read Windows frame and stop timestamps through `QueryPerformanceCounter` so Unity and the helper use the same counter origin.
- Add a Windows Media Foundation H.264/AAC MP4 encoder helper project.
- Select the Windows backend automatically in the Windows Editor and Windows Player.

## 0.1.0 - 2026-08-09

- Add timestamped Unity framebuffer and mixed-output audio capture.
- Add constant-frame-rate planning and audio-master A/V alignment rules.
- Add a macOS AVFoundation H.264/AAC MP4 backend.
- Preserve a diagnostic source bundle when encoding is unavailable or fails.
- Use asynchronous GPU readback, bounded preallocated capture slots, pooled audio blocks, and performance telemetry to keep capture work off the gameplay and audio threads.
