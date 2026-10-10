# macOS Metal recording

`RecordingVideoBackend.Automatic` selects this backend in macOS Editor Play mode. The current package is Editor-only and excludes Player builds. It requires Metal and a VideoToolbox H.264 hardware encoder. A failed hardware initialization is reported; the backend does not silently switch to software or image sequences.

## Data path

1. Unity captures the final Game View into an RGBA render texture. A GPU pass scales if necessary and converts linear-project colors to display RGB in a BGRA texture. Gamma projects bypass that conversion.
2. A Unity render-thread plugin event adds a Metal blit to Unity's current command buffer. Its destination is one of a bounded pool of IOSurface-backed `CVPixelBuffer` textures.
3. The command-buffer completion callback queues the finished surface for VideoToolbox. Neither the Unity main thread nor the render thread waits for hardware encoding. There is no `ReadPixels`, `AsyncGPUReadback`, CPU pixel array, PNG, or JPEG in this path.
4. VideoToolbox produces H.264 while the game runs. An AVAssetWriter writes those compressed samples. Surface leases remain alive through GPU completion and encoding, so Unity cannot overwrite a surface the encoder is using.
5. Existing Unity `AudioListener` capture writes bounded PCM audio. Stop drains video and writes an H.264/AAC MP4 by copying the compressed video and encoding the WAV to AAC. Video is not encoded again at stop.

macOS currently captures Unity Audio's listener mix. It does not provide Windows WASAPI's process-loopback scope for third-party audio engines. No microphone, desktop capture, or screen-recording permission is used.

## Timing and cancellation

Unity DSP time is the master clock for frame selection, captured audio and final duration. `NativeFrameOrdinal()` uses a bounded sub-frame admission window to tolerate block-quantized DSP updates and advances to the current ordinal when source rendering falls behind. It does not maintain an independent wall clock. Audio determines final duration. Stop drains the encoder queue asynchronously rather than blocking the Unity thread.

Capture slots and in-flight encodes are bounded by `GpuTexturePoolSize`. Exhausted slots drop source samples; source cadence gaps repeat the previous image in the CFR output. Gaps beyond `MaxEncodingLagMilliseconds` fault the session. Late render callbacks use request IDs and are ignored after cancellation. Buffer and RenderTexture release waits for pending GPU and encoder work.

`HardwareQuality` maps 0..51 to VideoToolbox quality 1..0. It is not a matched-quality mapping to NVENC CQ. Output is SDR H.264, not HDR or lossless.

## Build

On a Mac with Apple Command Line Tools and a Unity Editor installed:

```sh
export UNITY_PLUGIN_API="/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/Resources/PluginAPI"
./build.sh
```

The shipped plugin is `Editor/Plugins/macOS/MediaCaptureMetal.bundle`: an ad-hoc-signed universal arm64/x86_64 bundle targeting macOS 12+, using Apple's system frameworks. No bundled FFmpeg build or Swift compiler is required to use it.

The current `build.sh` still writes its rebuilt bundle to the legacy `Runtime/Plugins/macOS` location. With Unity closed, deploy the rebuilt bundle contents into the existing `Editor/Plugins/macOS/MediaCaptureMetal.bundle`, retain its Editor-only `.meta` import settings, and remove the generated legacy copy before reopening Unity. Script-only changes use the usual domain reload.

The intermediate executable and matching `.dSYM` stay in the ignored `build/` directory beside this script; debug symbols are not packaged inside the runtime bundle.

```sh
./check.sh /absolute/path/to/check-output
```

The standalone check encodes a moving color chart and test tone, verifies hardware selection, then exercises cancel plus a late render callback. It creates diagnostic media, not gameplay footage. See [VALIDATION.md](VALIDATION.md) for the actually tested host and evidence.

## API

```csharp
var capture = UnityAvRecorder.StartRecording(new RecordingOptions {
    OutputPath = "/absolute/path/capture.mp4",
    VideoBackend = RecordingVideoBackend.Automatic,
    FrameRateNumerator = 30,
    OutputWidth = 1080, OutputHeight = 1920,
    KeepIntermediateFiles = true
});
// After the desired gameplay interval:
RecordingResult result = await capture.StopRecordingAsync();
```

The native manifest reports hardware selection, CPU pixel readback bytes, source cadence misses, encoder/pool drops, pending frames, and capture submission time. Submission time excludes asynchronous GPU and encoding work. Game View must render continuously; an unfocused or hidden Editor can reduce its source cadence even with background execution enabled.

Apple references: [Core Video Metal texture cache](https://developer.apple.com/documentation/corevideo/cvmetaltexturecache-q3j), [required hardware encoder](https://developer.apple.com/documentation/videotoolbox/kvtvideoencoderspecification_requirehardwareacceleratedvideoencoder), [compression session](https://developer.apple.com/documentation/videotoolbox/vtcompressionsession-api-collection).
