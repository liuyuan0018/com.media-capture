# Unity Media Capture

**English** · [简体中文](README.zh-CN.md)

Unity Media Capture is a **high-performance audio and video capture solution for use within Unity applications**. It records the **final Unity Game View rendering and game-process audio** to H.264 + AAC MP4. The default backend uses **Windows x64 / D3D11 / NVIDIA NVENC**: Unity supplies GPU textures, and a native plugin submits them to NVENC through FFmpeg hardware-frame interfaces.

Version **0.3.0**. Package code: [MIT](LICENSE). FFmpeg libraries: LGPL 2.1 or later; see [third-party notices](ThirdPartyNotices.md).

## Capture scope

The recorder captures the final Game View at the end of a rendered frame, including completed camera composition, post-processing and in-game UI. It captures that rendering result without separately rendering an individual Camera. **The desktop and Editor interfaces, including the Unity toolbar, Scene View and Inspector, are excluded.**

WASAPI process loopback captures audio playback from the Unity process and its descendants, including output from Unity Audio, Wwise and other audio engines. Unrelated applications and microphones are excluded. Audio previews played by Unity Editor itself belong to the same process and can be included.

## Supported configuration

| Item | Default native backend |
| --- | --- |
| Unity | 2022.3; tested on 2022.3.67f1 |
| OS | Windows x64; process audio requires build 20348 or newer, Windows 11 recommended |
| Graphics API | D3D11 |
| Hardware | NVIDIA GPU supporting H.264 NVENC and a compatible driver; tested on RTX 3070 |
| Video | H.264, 8-bit YUV 4:2:0, constant frame rate, even dimensions |
| Audio | WASAPI PCM16 / 48 kHz / stereo, converted to AAC 192 kb/s on stop |
| Not implemented | Metal, D3D12, Vulkan, AMD AMF, Intel QSV, HDR video |

Unsupported configurations return an error. The native backend does not automatically switch to CPU pixel readback or software encoding, preserving predictable performance behavior. The image-sequence backend remains an explicit option. Existing macOS AVFoundation code is retained; native Metal recording is outside the current implementation scope.

## Installation and API usage

Install through Unity Package Manager using this Git URL:

```text
https://github.com/liuyuan0018/com.media-capture.git
```

For local development, reference the package directory in `Packages/manifest.json`. Deploy the Windows native plugin and its four FFmpeg DLLs together in `Runtime/Plugins/x86_64`. See the [native build guide](Native~/README.md) for provenance, rebuilding and licensing.

Call from the Unity main thread in Play mode:

```csharp
using GameFramework.MediaCapture.Unity;

UnityAvRecorder recorder = UnityAvRecorder.StartRecording(new RecordingOptions
{
    OutputPath = @"D:\Recordings\game.mp4",
    FrameRateNumerator = 30,
    HardwareQuality = 20,
    KeepIntermediateFiles = false
});

// Call when the user requests stop, and await finalization.
RecordingResult result = await recorder.StopRecordingAsync();
if (!result.Success)
    UnityEngine.Debug.LogError(result.Message);
UnityEngine.Object.Destroy(recorder.gameObject);
```

Game View must continue rendering during recording, and finalization must complete before leaving Play mode. `Abort()` terminates recording without executing normal final-file generation. The stop task accepts a `CancellationToken`; failure or cancellation retains the session directory for diagnosis. The caller owns and destroys its recorder GameObject.

The package provides a recording API and a Player command-line entry point. Callers implement their own recording controls and configure frame rate, output dimensions and encoding quality through `RecordingOptions`.

## Command-line recording

The existing Player bootstrap accepts an absolute output path, duration in seconds and integer frame rate:

```text
Game.exe -force-d3d11 -gameFrameworkRecord "D:\Recordings\game.mp4" -gameFrameworkRecordSeconds 30 -gameFrameworkRecordFps 30
```

It uses the default native backend and retains intermediates. Run a rendered Player; a headless or `-nographics` session cannot provide Game View frames. The bootstrap logs completion or failure and does not quit the application automatically. This version has not been validated in a built Player.

## Architecture selection

The performance design focuses on capture within Unity: the default native backend passes video frames through GPU textures and uses hardware video encoding, avoiding full-frame pixel readback to CPU memory. This positioning does not imply a performance advantage over desktop, window or game capture software. No controlled benchmark against such software has been performed. Blit, GPU texture copies, synchronization and encoding still incur costs.

The implementation targets real-time recording during gameplay. Its priorities are reducing raw-pixel processing on the CPU, intermediate image-file I/O and full video encoding after stop, while bounding capture-queue memory. The output is constant-frame-rate H.264 + AAC MP4, with the platform scope restricted to Windows D3D11.

| Approach | Data transfer and encoding | Benefits and costs | Current role |
| --- | --- | --- | --- |
| Image sequence + system encoder | Read GPU pixels into CPU memory, write JPEG/PNG, then encode through Media Foundation or AVFoundation after stop | Retains individual source frames; adds CPU image processing, disk I/O and finalization time; JPEG introduces another lossy stage | Explicit compatibility backend |
| FFmpeg process + raw-frame standard input | Read GPU pixels into CPU memory and send them through a pipe to `ffmpeg.exe` | Provides process isolation and command-line configuration; retains raw-pixel readback and interprocess transfer, and hardware encoding may require a subsequent texture upload | Not selected as the default |
| Native FFmpeg + D3D11 hardware frames | Reuse Unity's D3D11 device and submit GPU textures to NVENC through FFmpeg | Avoids raw-pixel readback and intermediate image files; requires GPU synchronization, texture-reference management, native dependency distribution and driver compatibility | Default implementation |

FFmpeg supplies codec invocation, timestamps, encoded-packet handling and MP4 muxing, reducing the media-processing logic implemented by this package. NVENC performs H.264 hardware encoding. **Avoiding GPU readback requires compatible D3D11 textures and hardware encoding, same-device resource use and correct synchronization; native DLL integration alone does not establish those conditions.**

Only NVENC is implemented. AMD AMF and Intel QSV hardware-frame integration is not available. FFmpeg support for another encoder does not establish device management or runtime validation in this package. Software encoding is not an automatic fallback; callers must explicitly select another backend.

## Native FFmpeg integration

C# uses P/Invoke to call the C interface exported by `GameFrameworkMediaCapture.dll`. The DLL is loaded into the Unity process and dynamically links `avcodec`, `avformat`, `avutil` and `swresample`. **Recording does not launch `ffmpeg.exe` or pipe raw video frames through standard input.** A separate Windows helper process performs WASAPI audio capture.

```text
Final Game View image
  → Matching dimensions: Graphics.Blit(null, BGRA RenderTexture)
    Scaling required: capture into RGBA RenderTexture → Blit into BGRA RenderTexture
  → native D3D11 texture pool / GPU completion queries
  → FFmpeg D3D11 hardware frames → NVIDIA NVENC → video.mp4

Unity process audio → WASAPI helper → audio.wav
On stop: compressed video packets + WAV → AAC encoding / MP4 muxing
  → output.partial.mp4 → final destination
```

| Component | Responsibility |
| --- | --- |
| `UnityAvRecorder` / `NativeD3D11Capture` | Manage the session, capture the frame-end image, allocate capture/output textures and submit render-thread events |
| `GameFrameworkMediaCapture.dll` | Manage the D3D11 texture pool, GPU completion queries, encoding worker and native session resources |
| FFmpeg libraries / NVIDIA NVENC | FFmpeg manages hardware frames, timestamps and encoded packets; NVENC encodes video to H.264; FFmpeg performs AAC encoding and MP4 muxing |
| Windows audio helper | Capture audio for the Unity process scope, record QPC timing information and write WAV/audio statistics |

## Core design

### Submitting Unity GPU textures to FFmpeg

The following describes the package's internal D3D11 implementation. The public recording entry point remains `UnityAvRecorder.StartRecording()`; callers do not construct FFmpeg frames themselves.

```text
Unity main thread: RenderTexture.GetNativeTexturePtr()
  → P/Invoke: mc_create(texturePointer, options)
  → Session: obtain Unity D3D11 device, create texture pool and FFmpeg hardware contexts

Unity frame end: matching dimensions → Graphics.Blit(null, BGRA)
                scaling required → CaptureScreenshotIntoRenderTexture(RGBA) → Graphics.Blit(BGRA)
  → mc_queue_frame(handle, texturePointer, audioSample) → request ID
  → CommandBuffer.IssuePluginEventAndData → Graphics.ExecuteCommandBuffer
Unity render thread: RenderEvent → Session::Submit → CopyResource → End(query)
  → Session::PollGpu → GetData(query) completed → encoding queue
Native encoding thread: Session::Encode → AVFrame → avcodec_send_frame
  → h264_nvenc → avcodec_receive_packet → write video.mp4
```

#### 1. Obtain the Unity texture object pointer

`NativeD3D11Capture` creates a fixed-size, single-sample BGRA RenderTexture without mipmaps, then calls `GetNativeTexturePtr()` once to cache its native pointer. With the D3D11 backend, that pointer represents an `ID3D11Texture2D*`: a native graphics-resource interface, not a CPU-readable pixel-array address.

```csharp
m_NativeTexture = m_OutputTexture.GetNativeTexturePtr();
m_Handle = mc_create(m_NativeTexture, ref nativeOptions, m_Error, m_Error.Length);
```

C# passes the `IntPtr` through P/Invoke to the native C interface. `mc_create()` casts it to `ID3D11Texture2D*` and creates the session. This passes a reference to the texture object without copying its pixels into managed memory.

#### 2. Associate Unity's D3D11 device with FFmpeg

`Session` calls `source->GetDevice()` to obtain the texture's Unity D3D11 device and `GetImmediateContext()` to obtain its device context. It creates the encoding texture pool on that device. Pool textures use `D3D11_USAGE_DEFAULT` and `CPUAccessFlags = 0`, with dimensions and format matching Unity's BGRA output texture.

`Session::OpenEncoder()` creates FFmpeg hardware-device and hardware-frame contexts with the following associations:

| Object or call | Assignment and purpose |
| --- | --- |
| `av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA)` | Allocate a D3D11 hardware-device context |
| `AVD3D11VADeviceContext.device` | Assign the device obtained from the Unity texture and call `AddRef()`, followed by `av_hwdevice_ctx_init()` |
| `av_hwframe_ctx_alloc(hardwareDevice)` | Create a hardware-frame context belonging to that device |
| `AVHWFramesContext.format / sw_format` | Set `AV_PIX_FMT_D3D11` and `AV_PIX_FMT_BGRA`, respectively; the latter describes the texture's pixel layout and does not allocate CPU frames |
| `AVHWFramesContext.width / height / initial_pool_size` | Set fixed output dimensions and `initial_pool_size = 0`; the plugin's own pool supplies input textures, followed by `av_hwframe_ctx_init()` |
| `AVCodecContext.pix_fmt / hw_frames_ctx` | Set `AV_PIX_FMT_D3D11` and retain the hardware-frame context, then open `h264_nvenc` through `avcodec_open2()` |

FFmpeg consequently uses the same D3D11 device that owns Unity's texture. `D3D11VA` is the FFmpeg hardware-device type name here; `h264_nvenc` selects the actual encoder.

#### 3. Submit the GPU copy on Unity's render thread

`CaptureNativeFrames()` continues to capture after `WaitForEndOfFrame` using public engine APIs, without requiring integration into a project-specific render pipeline. `NativeD3D11Capture.Capture()` compares the actual frame dimensions with the fixed output dimensions:

- Matching dimensions: `Graphics.Blit(null, m_OutputTexture)` writes the current framebuffer directly into the BGRA output texture. It calls neither `CaptureScreenshotIntoRenderTexture()` nor an intermediate RGBA texture.
- Different dimensions: allocate an RGBA texture at the actual rendering size, capture the complete Game View, then use `Graphics.Blit()` to scale it into the BGRA output texture. Rounding odd source dimensions down to even output dimensions also uses this path to avoid cropping image edges.

The current Unity D3D11 implementation handles `Graphics.Blit()` with a null source by obtaining the framebuffer through `GrabPixels()`. It uses a GPU copy for compatible formats and an internal draw for incompatible formats. The internal draw converts the framebuffer directly into BGRA, removing the intermediate RGBA texture and its additional transfer. An additional sRGB write conversion is disabled while writing BGRA, and the previous state is restored afterwards. The screenshot-and-scale path remains for different dimensions because rectangle limits in framebuffer capture are not equivalent to scaling the complete image.

`mc_queue_frame(handle, texturePointer, audioSample)` retains the session, a `ComPtr` reference to the texture and the capture time, then returns a request ID. It does not perform the texture copy. C# passes the ID to `CommandBuffer.IssuePluginEventAndData()` and submits the command buffer through `Graphics.ExecuteCommandBuffer()`. Event ID `0` submits a frame; `mc_get_render_callback()` provides the callback address.

Unity's render thread executes `RenderEvent()`, resolves the request ID and calls `Session::Submit()`. This method validates the texture device, dimensions and format, selects a free pool texture, and issues `CopyResource(slotTexture, unityOutputTexture)` followed by `End(completionQuery)`. The GPU copy separates the encoder input from the output texture that Unity will overwrite on its next frame.

#### 4. Confirm GPU copy completion

`CopyResource()` submits a GPU command; returning from the CPU call does not establish completion. In render callbacks, `Session::PollGpu()` checks a `D3D11_QUERY_EVENT` using `GetData(query, ..., D3D11_ASYNC_GETDATA_DONOTFLUSH)`. `S_FALSE` leaves the texture pending. Only a completed copy enters the encoding queue. Event ID `1` performs polling, and frame submission also polls. The encoding thread does not read a texture whose copy is still pending.

#### 5. Populate an FFmpeg hardware frame with the pool texture

`Session::Encode()` allocates an `AVFrame` and assigns its hardware format, output dimensions, output timestamp and hardware-frame context. The following excerpt shows the key assignments; frame allocation, error handling and destruction are omitted:

```cpp
frame->format = AV_PIX_FMT_D3D11;
frame->width = options.width;
frame->height = options.height;
frame->pts = encoded.load();
frame->duration = 1;
frame->hw_frames_ctx = av_buffer_ref(hardwareFrames);
frame->data[0] = reinterpret_cast<uint8_t*>(lease->slot->texture.Get());
frame->data[1] = nullptr;

auto* owner = new LeaseRef(lease);
frame->buf[0] = av_buffer_create(frame->data[0], 0,
    [](void* value, uint8_t*) { delete static_cast<LeaseRef*>(value); }, owner, 0);

int result = avcodec_send_frame(codec, frame);
```

Under the `AV_PIX_FMT_D3D11` convention, `data[0]` holds an `ID3D11Texture2D*`, not a CPU image plane. `data[1]` represents the texture-array index. Each texture in this implementation has one element, so the index is `0`, represented by `nullptr`. `pts` is the output frame ordinal in the encoder's time base, not a raw QPC counter value.

`avcodec_send_frame()` submits this hardware frame to `h264_nvenc`. On `EAGAIN`, the code receives existing encoded packets before retrying. `avcodec_receive_packet()` retrieves compressed H.264 data for the intermediate MP4. Encoded packets enter CPU file I/O; raw video pixels are not read back from the GPU.

#### 6. Reuse the texture only after all references are released

`frame->buf[0]` uses `av_buffer_create()` to associate texture ownership with FFmpeg reference counting. Its release callback destroys one `LeaseRef` reference. Only when FFmpeg, the encoding queue and the previous-frame record used for duplication no longer hold the texture's `FrameLease` does its destructor mark the pool slot free. Returning from `avcodec_send_frame()` or destroying the temporary `AVFrame` therefore does not permit immediate overwriting of the pool texture.

The plugin enables D3D11 multithread protection and restores the previous setting after the final session releases the associated device references. Unity retains ownership of its graphics device. The default video path calls neither `ReadPixels`, `AsyncGPUReadback` nor `av_hwframe_transfer_data()`, and performs no JPEG/PNG encoding. GPU copies, format conversion and synchronization remain; this is not a completely copy-free implementation.

Source references: `CaptureNativeFrames()` in [frame-end scheduling](Runtime/Unity/UnityAvRecorder.Native.cs); the constructor, `Capture()` and `IssueEvent()` in [Unity texture and native calls](Runtime/Unity/NativeD3D11Capture.cs); and `Session`, `OpenEncoder()`, `Submit()`, `PollGpu()`, `Encode()` and `FrameLease` in the [native D3D11 and FFmpeg implementation](Native~/FfmpegCapture.cpp).

### Fixed output dimensions and Editor context

In the Editor, recording starts with the Game View render size obtained through `Handles.GetMainGameViewSize()`. This avoids tool-window dimensions returned by `Screen.width/height` during Editor button callbacks. Video output dimensions are fixed when the session is created.

The BGRA output texture, native pointer and encoder configuration remain unchanged throughout the session. While actual rendering dimensions differ from the output, the recorder allocates or recreates an RGBA intermediate as needed. When the dimensions match again, it releases that texture and resumes direct capture. An aspect-ratio change in the scaling path stretches the image; automatic cropping or letterboxing is not implemented. A new session is required to adopt different output dimensions.

### Shared time base and bounded queues

Video capture and the Windows audio helper use the QPC high-resolution clock as a shared time base. The encoder generates output timestamps at the target frame rate and selects the latest available image at or before each timestamp. If no new image is available, it repeats the previous frame to preserve video duration relative to audio. Repetition cannot recover motion that was not captured.

Texture storage and pending render requests have fixed limits. When no texture is available, capture drops that image and increments its counter. Encoding lag beyond the configured clock threshold terminates the session with an error, preventing unbounded resource accumulation.

### File generation and finalization

During recording, the encoding worker writes H.264 to `video.mp4` in the session directory, while the audio helper writes `audio.wav`. On stop, the helper fills the required audio duration, FFmpeg encodes WAV to AAC, and existing H.264 packets are copied into `output.partial.mp4` without another video encode. The recorder moves that file to, or replaces, the requested destination only after muxing succeeds.

Video encoding is spread across the recording session and diagnostic files remain available. Stop still processes the full audio track and muxes the file, so its cost grows with duration. RIFF WAV fails explicitly near 4 GiB, approximately 6.2 hours at 48 kHz stereo PCM16. Disk space must cover intermediates and the final movie.

### Cancellation and resource release

Cancellation stops new frame submissions and requests native worker shutdown. C# retains the output texture until the native session can be destroyed, preventing access to released resources by the encoder. Pending render events resolve request IDs against valid requests; events arriving after cancellation do not directly access destroyed request objects. Assembly reload and plugin unload request native session shutdown.

### Encoding parameters and quality tradeoffs

The implementation uses NVENC `p4`, VBR rate control and a default CQ of 20. B-frames and lookahead are disabled to limit buffering associated with frame reordering and advance analysis. CQ is a quality-control parameter: lower values generally increase quality and file size without guaranteeing a fixed bitrate or output size. H.264 + AAC MP4 is intended for conventional playback and sharing. Video is limited to 8-bit YUV 4:2:0; lossless and HDR output are not provided.

FFmpeg library invocation and command-line invocation are integration choices, not separate quality levels. Quality depends primarily on input pixels, the actual encoder, rate control, presets and color conversion. This implementation reduces CPU pixel processing and intermediate image-file I/O. No matched-bitrate or matched-quality comparison establishes superior compression efficiency over x264 or the previous system encoders.

### Native dependencies and rebuildability

The runtime uses four purpose-built FFmpeg shared libraries with the NVENC, AAC, PCM, D3D11 hardware-frame and container functionality required by this package. Neither `ffmpeg.exe` nor x264 is included. Source archives, pinned revisions, hashes and build configuration are distributed with the package to support dependency verification and rebuilding. See the [native build guide](Native~/README.md) for build procedures and [third-party notices](ThirdPartyNotices.md) for distribution files and their licenses.

## Options and diagnostics

| Option | Default | Meaning |
| --- | --- | --- |
| `OutputPath` | Required | Absolute `.mp4` path |
| `VideoBackend` | `NativeD3D11` | Select `ImageSequence` for the previous backend |
| `FrameRateNumerator / Denominator` | `24 / 1` | For example `30 / 1` or `30000 / 1001` |
| `OutputWidth / OutputHeight` | `0 / 0` | Initial Game View size rounded down to even values; custom dimensions must both be positive and even |
| `HardwareQuality` | `20` | NVENC CQ, range 0–51 |
| `GpuTexturePoolSize` | `8` | Native video textures, range 4–32 |
| `MaxEncodingLagMilliseconds` | `2000` | Encoding lag failure threshold |
| `EncoderTimeoutSeconds` | `300` | Stop/finalization timeout |
| `KeepIntermediateFiles` | `false` | Retain WAV and intermediate video on success |
| `OverwriteExisting` | `false` | Replace an existing destination after completion |

Callers specifying custom output dimensions must provide both width and height as positive even values and should preserve the intended image aspect ratio. Dimension changes during recording are described under "Fixed output dimensions and Editor context."

`RecordingResult` reports captured, output, duplicate and dropped video frames. `Performance` reports capture API timing, queue depth and estimated texture memory. **Capture API timing is not the full CPU/GPU cost of recording.** Texture estimates exclude encoder and driver allocations.

When WASAPI cannot provide an exact loss count, `DroppedAudioFrames` is `-1` and `DroppedAudioFramesKnown` is `false`. Separate counters report discontinuities, timestamp errors and inserted silence. Silence also occurs for idle audio or end padding and is not an exact loss count.

Native sessions write `manifest.json` under `.media-capture-*` beside the destination. The helper writes `audio.wav.stats.json`. Diagnostic JSON remains after successful cleanup even when intermediates are not retained.

## Validation results and applicability

Before the direct-BGRA optimization, one 20-second same-scene comparison measured 173.56 game FPS for native recording versus 162.42 for the previous image pipeline, with finalization taking 1.10 seconds versus 8.90 seconds. Estimated recording texture memory was 79.1 MiB versus 23.7 MiB. These simple-scene measurements use different encoder settings; they are neither a matched-quality compression benchmark nor measurements of this optimization. Matching-size direct capture removes one full-size RGBA texture, approximately 7.9 MiB at 1920×1080; its timing benefit has not been measured. See the [validation report](Native~/VALIDATION.md) for CPU measurements, audio/video timing and limitations.

Windows 11 / Unity 2022.3.67f1 / D3D11 / RTX 3070 tests cover native texture encoding, 1080p Game View capture, process audio, text orientation and red/green/blue transitions. Samples exclude editor UI. See the [development and validation record](Native~/DEVELOPMENT_PLAN.md) for evidence, remaining checks and devices not covered.

H.264 is lossy. YUV 4:2:0 reduces color detail at fine lines and text edges. The tested GPU RGBA-to-BGRA conversion preserved pixel values, while decoded video still showed color differences. These results do not establish lossless pixels, universal compatibility or zero drops in complex scenes. Repeated resolution changes during recording have not received dedicated validation; the startup-dimension diagnosis and feedback are documented in the [validation report](Native~/VALIDATION.md).

## Previous backend and extensions

Select `VideoBackend = RecordingVideoBackend.ImageSequence` to use the previous pipeline. JPEG/PNG, GPU readback queue and frame-write options remain available. Passing an explicit `IRecordingEncoderBackend` also selects that pipeline and hands a `RecordingEncodeRequest` to it. These settings do not change the default native path.

The previous Windows implementation uses Media Foundation and macOS uses AVFoundation. Their sources remain available; new runtime validation focuses on Windows D3D11.

## Troubleshooting

- **Native DLL unavailable:** check Windows x64 import settings, all four FFmpeg DLLs and the Visual C++ runtime. Restart Unity after replacing a loaded native DLL.
- **NVENC initialization fails:** check D3D11, GPU, driver and available hardware encoder sessions. There is no automatic software fallback.
- **No images or many duplicates:** keep Game View rendering. Pausing, changing to a view that stops rendering or main-thread stalls affect capture.
- **Saving fails:** inspect the result message and `manifest.json`; check free space, directory permissions and file locks.
- **No sound:** verify output from the Unity process or its descendants, then inspect helper errors and audio statistics. Disabling Unity Audio does not necessarily disable Wwise output.
