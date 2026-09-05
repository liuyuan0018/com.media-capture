# Architecture

The package separates capture from encoding:

1. `UnityAvRecorder` schedules framebuffer samples at end-of-frame, captures into a preallocated render-target pool, and submits asynchronous GPU readbacks with audio-sample timestamps.
2. Completed readbacks are copied into fixed managed buffers. Thread-safe JPEG/PNG compression and file writes run on background tasks; the slot remains unavailable until that work finishes, making backpressure bounded and observable.
3. On Windows, `WindowsMediaCaptureHelper` captures the Unity process tree through WASAPI process loopback. It records Unity Audio, Wwise, and other native audio engines after they reach the Windows audio engine. On macOS, `UnityAudioCaptureTap` copies the active `AudioListener` mix into pooled blocks and `PcmWaveWriter` drains them on a background thread.
4. `ConstantFramePlan` uses integer sample timestamps and a rational frame rate to select one source image for every output frame. Missing source cadence becomes an explicit duplicate instead of shortening the movie.
5. `IRecordingEncoderBackend` receives a closed WAV and complete CFR frame plan. The Windows implementation uses WIC and Media Foundation to encode H.264/AAC. The macOS implementation invokes the packaged AVFoundation helper.

The output is first written to `<output>.partial`; it replaces the requested path only after the backend succeeds. The source session and diagnostic manifest remain authoritative on any failure.

## Platform boundary

Unity framebuffer capture is platform-independent. Audio capture and encoding are not. `WindowsMediaFoundationBackend` requires Windows 10 build 20348 or newer because older Windows builds do not provide process-specific WASAPI loopback. It starts the packaged helper with the Unity PID and includes child-process audio. `MacOsAvFoundationBackend` depends on AVFoundation and Apple Command Line Tools. Unsupported platforms report failure and retain the source bundle rather than silently producing video-only output.
