# Unity Media Capture

**English** · [简体中文](README.zh-CN.md)

Unity Media Capture records the **final Unity Game View and audio to H.264 + AAC MP4** in **Unity Editor Play mode**. Version **0.4.0** provides two native hardware backends:

- **macOS: Metal + IOSurface + VideoToolbox** for video, with Unity Audio's `AudioListener` mix.
- **Windows: D3D11 + FFmpeg + NVIDIA NVENC** for video, with WASAPI process-loopback audio.

`RecordingVideoBackend.Automatic` selects the backend by platform. Both encode video while the game runs without CPU video-pixel readback or JPEG/PNG intermediates. The package also provides a one-shot PNG screenshot API.

**The current package is Editor-only.** Its native plugins and helper assets are excluded from Player builds: plugins are enabled only for the Editor platform, helper assets avoid build-shipped special folders, and the managed assemblies disable automatic references. The backend recordings linked below are historical Editor validation; the current Editor-only package does not provide standalone Player capture.

Package code: [MIT](LICENSE). The Windows FFmpeg libraries are LGPL 2.1 or later; macOS uses Apple's system frameworks. See [third-party notices](ThirdPartyNotices.md).

## Capture scope

The recorder captures the final Game View at the end of a rendered frame, including completed camera composition, post-processing and in-game UI. It captures that rendering result without separately rendering an individual Camera. **The desktop and Editor interfaces, including the Unity toolbar, Scene View and Inspector, are excluded.**

**macOS audio:** captures Unity Audio through the active `AudioListener`, at the Unity output sample rate. It does not capture third-party audio engines that bypass Unity Audio.

**Windows audio:** WASAPI process loopback captures playback from the Unity process and its descendants, including Unity Audio, Wwise and other audio engines. Unrelated applications and microphones are excluded. Editor audio previews belong to the same process and can be included.

## Supported configuration

The package declares Unity 2022.3 as its minimum version. The tested Editor versions differ by platform:

| Item | macOS native backend | Windows native backend |
| --- | --- | --- |
| Editor tested | Unity 6000.5.7f1 | Unity 2022.3.67f1 |
| OS / architecture | Bundle targets macOS 12+, universal arm64/x86_64; runtime tested on Apple M2 / macOS 27.0 | Windows x64; process audio requires build 20348+, Windows 11 recommended |
| Graphics API | Metal | D3D11 |
| Hardware encoder | VideoToolbox H.264 hardware encoder required; Apple M2 tested, Intel runtime unverified | NVIDIA H.264 NVENC and a compatible driver; RTX 3070 tested |
| Video | SDR H.264, constant frame rate, even dimensions | H.264, 8-bit YUV 4:2:0, constant frame rate, even dimensions |
| Audio source | Unity `AudioListener` mix; Unity output sample rate | WASAPI process audio; PCM16 / 48 kHz / stereo |
| Final file | H.264 + stereo AAC, 192 kb/s audio, MP4 | H.264 + stereo AAC, 192 kb/s audio, MP4 |
| Platform limits | No process-loopback capture for audio engines bypassing Unity Audio; Intel runtime unverified | No D3D12, Vulkan, AMD AMF, Intel QSV or HDR video |

Unsupported configurations return an error. Native hardware initialization does not silently fall back to software encoding or CPU pixel readback. Select `ImageSequence` explicitly for the previous image-sequence pipeline. Neither native path offers lossless or HDR video.

See the [Mac implementation guide](Native~/macOS/README.md), [Mac validation](Native~/macOS/VALIDATION.md), and [Windows validation](Native~/VALIDATION.md).

## Installation and API usage

Install through Unity Package Manager using this Git URL:

```text
https://github.com/liuyuan0018/com.media-capture.git
```

For local development, reference the package directory in `Packages/manifest.json`. The shared game workspace uses `file:../../../../framework/com.media-capture`.

- macOS imports `Runtime/Plugins/macOS/MediaCaptureMetal.bundle`; it uses system frameworks and does not require FFmpeg DLLs.
- Windows imports the recording plugin and its four FFmpeg DLLs together from `Runtime/Plugins/x86_64`.

Call the API from Editor tooling or an Editor-only assembly. Assemblies using `.asmdef` files must explicitly reference `MediaCapture.Unity`; the package disables automatic assembly references. Native build details are in the [Mac guide](Native~/macOS/README.md) and [Windows guide](Native~/README.md).

Call from the Unity main thread while the Editor is in Play mode:

```csharp
using GameFramework.MediaCapture.Unity;

UnityAvRecorder recorder = UnityAvRecorder.StartRecording(new RecordingOptions
{
    OutputPath = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "game.mp4"),
    VideoBackend = RecordingVideoBackend.Automatic,
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

The package provides recording and screenshot APIs for Editor tools and automation. Callers supply recording controls and configure frame rate, output dimensions and encoding quality through `RecordingOptions`. The earlier Player command-line example is not supported by the current Editor-only package.

## Architecture selection

The performance design focuses on capture within Unity: the default native backend passes video frames through GPU textures and uses hardware video encoding, avoiding full-frame pixel readback to CPU memory. This positioning does not imply a performance advantage over desktop, window or game capture software. No controlled benchmark against such software has been performed. Blit, GPU texture copies, synchronization and encoding still incur costs.

The implementation targets real-time recording during gameplay. Its priorities are reducing raw-pixel processing on the CPU, intermediate image-file I/O and full video encoding after stop, while bounding capture-queue memory. The output is constant-frame-rate H.264 + AAC MP4, with native implementations for macOS Metal and Windows D3D11.

| Approach | Data transfer and encoding | Benefits and costs | Current role |
| --- | --- | --- | --- |
| Image sequence + system encoder | Read GPU pixels into CPU memory, write JPEG/PNG, then encode through Media Foundation or AVFoundation after stop | Retains individual source frames; adds CPU image processing, disk I/O and finalization time; JPEG introduces another lossy stage | Explicit compatibility backend |
| FFmpeg process + raw-frame standard input | Read GPU pixels into CPU memory and send them through a pipe to `ffmpeg.exe` | Provides process isolation and command-line configuration; retains raw-pixel readback and interprocess transfer, and hardware encoding may require a subsequent texture upload | Not selected as the default |
| Native Metal + VideoToolbox | Copy Unity Metal textures into IOSurface-backed buffers, then submit them to VideoToolbox | Avoids raw-pixel readback and intermediate image files; requires GPU completion tracking and buffer lifetime management | Default on macOS |
| Native FFmpeg + D3D11 hardware frames | Reuse Unity's D3D11 device and submit GPU textures to NVENC through FFmpeg | Avoids raw-pixel readback and intermediate image files; requires GPU synchronization, texture-reference management, native dependency distribution and driver compatibility | Default on Windows |

On Windows, FFmpeg supplies codec invocation, timestamps, encoded-packet handling and MP4 muxing, reducing the media-processing logic implemented by this package. NVENC performs H.264 hardware encoding. **Avoiding GPU readback requires compatible D3D11 textures and hardware encoding, same-device resource use and correct synchronization; native DLL integration alone does not establish those conditions.**

The Windows native backend currently implements NVENC only. AMD AMF and Intel QSV hardware-frame integration is not available. FFmpeg support for another encoder does not establish device management or runtime validation in this package. Software encoding is not an automatic fallback; callers must explicitly select another backend.

## macOS Metal implementation

```text
Final Game View
  → RGBA RenderTexture → GPU scaling / Linear-to-display conversion → BGRA
  → Unity render-thread event → Metal blit into IOSurface-backed CVPixelBuffer
  → GPU completion → VideoToolbox hardware H.264 → AVAssetWriter → video.mp4

Unity AudioListener mix → PCM WAV
On stop: drain video → copy H.264 + encode PCM to AAC → final MP4
```

The native plugin submits its Metal blit to Unity's command buffer. A completion callback makes the surface available to the encoder only after the GPU copy finishes. Bounded surface leases keep buffers alive through GPU completion, encoding and writing; the Unity threads do not wait for each hardware encode.

The current frame scheduler uses Unity DSP time for audio and video. It admits the next frame within a bounded sub-frame window to tolerate block-based DSP updates. Audio duration determines the final constant-frame-rate video length; missed source frames may repeat in the output. Stop drains video, encodes the audio and muxes the tracks without encoding the video again.

`HardwareQuality` maps 0–51 to VideoToolbox quality 1–0. This is not a quality-equivalence mapping to NVENC CQ. Both paths retain GPU copy, color-conversion and encoding costs.

Sources: [Unity texture submission](Runtime/Unity/NativeMetalCapture.cs), [frame scheduling](Runtime/Unity/UnityAvRecorder.Native.cs), and [native Metal / VideoToolbox implementation](Native~/macOS/MetalCapture.mm). Build and lifecycle details are in the [Mac guide](Native~/macOS/README.md).

## Windows native FFmpeg integration

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

## Windows backend design

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
| `VideoBackend` | `Automatic` | Native Metal on macOS; native D3D11 on Windows; select `ImageSequence` explicitly for the previous backend |
| `FrameRateNumerator / Denominator` | `24 / 1` | For example `30 / 1` or `30000 / 1001` |
| `OutputWidth / OutputHeight` | `0 / 0` | Initial Game View size rounded down to even values; custom dimensions must both be positive and even |
| `HardwareQuality` | `20` | Range 0–51: NVENC CQ on Windows; mapped to VideoToolbox quality 1–0 on macOS. Lower means higher requested quality; values are not equivalent across encoders |
| `GpuTexturePoolSize` | `8` | Native video textures, range 4–32 |
| `MaxEncodingLagMilliseconds` | `2000` | Encoding lag failure threshold |
| `EncoderTimeoutSeconds` | `300` | Stop/finalization timeout |
| `KeepIntermediateFiles` | `false` | Retain WAV and intermediate video on success |
| `OverwriteExisting` | `false` | Replace an existing destination after completion |

Callers specifying custom output dimensions must provide both width and height as positive even values and should preserve the intended image aspect ratio. Dimension changes during recording are described under "Fixed output dimensions and Editor context."

`RecordingResult` reports captured, output, duplicate and dropped video frames. `Performance` reports capture API timing, queue depth and estimated texture memory. **Capture API timing is not the full CPU/GPU cost of recording.** Texture estimates exclude encoder and driver allocations.

When WASAPI cannot provide an exact loss count, `DroppedAudioFrames` is `-1` and `DroppedAudioFramesKnown` is `false`. Separate counters report discontinuities, timestamp errors and inserted silence. Silence also occurs for idle audio or end padding and is not an exact loss count.

Native sessions write `manifest.json` under `.media-capture-*` beside the destination. The Windows audio helper also writes `audio.wav.stats.json`. Diagnostic JSON remains after successful cleanup even when intermediates are not retained.

## Validation results and applicability

### macOS Metal

The [2026-09-24 validation record](Native~/macOS/VALIDATION.md) covers Apple M2 / macOS 27.0 / Unity 6000.5.7f1 / Metal. A 1080×1920, 30 fps Editor recording produced 20.1 seconds of H.264/AAC video; stop through completion took 0.759 seconds. It reported 0 encoder-pool drops, 0 dropped audio frames and 11 CFR repeated frames. Twenty diagnostic audio/visual pulse onsets differed by at most one 30 fps frame (33.33 ms).

These are measurements of that documented sample, not a new test of the current checkout or a comparison with Windows. Only arm64 runtime was tested; a universal binary does not establish Intel runtime compatibility. The current package excludes Player builds.

### Windows D3D11 / NVENC

Before the direct-BGRA optimization, one 20-second same-scene comparison measured 173.56 game FPS for native recording versus 162.42 for the previous image pipeline, with finalization taking 1.10 seconds versus 8.90 seconds. These simple-scene measurements use different encoder settings; they are neither a matched-quality compression benchmark nor measurements of this optimization. Its timing benefit has not been measured. See the [validation report](Native~/VALIDATION.md) for CPU measurements, audio/video timing and limitations.

At 1920×1080, one RGBA/BGRA texture contains `1920 × 1080 × 4 = 8,294,400` bytes of pixels, approximately 7.91 MiB (`1 MiB = 1,048,576 bytes`). The recording texture estimates break down as follows:

| Configuration | Recording GPU textures | Estimated pixel storage |
| --- | --- | ---: |
| Native backend before direct Blit | 1 RGBA intermediate + 1 BGRA output + 8 native pool textures | 79.1 MiB |
| Windows native backend, matching source/output dimensions | 1 BGRA output + 8 native pool textures | 71.2 MiB |
| Previous image-sequence backend in the comparison | 3 capture textures for asynchronous GPU readback | 23.7 MiB |

The native plugin preallocates `GpuTexturePoolSize` textures, 8 by default. It copies each captured frame into an available pool texture so Unity can write the next frame while GPU copying and encoding of earlier frames complete. A pool texture becomes reusable only after all frame references are released. The 8 allocated textures do not imply that 8 frames are always waiting for encoding. The previous pipeline moves pixels into CPU memory for image processing and also preallocated 23.7 MiB of CPU pixel buffers in this comparison.

The current 71.2 MiB figure is calculated from texture dimensions and allocation counts, not a new GPU memory measurement. Scaling adds an RGBA intermediate at the actual source dimensions. These estimates cover recording texture pixel storage only; they exclude driver and encoder internal allocations, allocation alignment and temporary resources pending destruction.

Windows 11 / Unity 2022.3.67f1 / D3D11 / RTX 3070 tests cover native texture encoding, 1080p Game View capture, process audio, text orientation and red/green/blue transitions. Samples exclude editor UI. See the [development and validation record](Native~/DEVELOPMENT_PLAN.md) for evidence, remaining checks and devices not covered.

H.264 is lossy. YUV 4:2:0 reduces color detail at fine lines and text edges. The tested GPU RGBA-to-BGRA conversion preserved pixel values, while decoded video still showed color differences. These results do not establish lossless pixels, universal compatibility or zero drops in complex scenes. Repeated resolution changes during recording have not received dedicated validation; the startup-dimension diagnosis and feedback are documented in the [validation report](Native~/VALIDATION.md).

## Previous backend and extensions

Select `VideoBackend = RecordingVideoBackend.ImageSequence` to use the previous pipeline. JPEG/PNG, GPU readback queue and frame-write options remain available. Passing an explicit `IRecordingEncoderBackend` also selects that pipeline and hands a `RecordingEncodeRequest` to it. These settings do not change the default native path.

The previous Windows implementation uses Media Foundation and macOS uses AVFoundation. Their sources remain available; platform runtime validation is recorded separately for Windows D3D11 and macOS Metal.

## Troubleshooting

- **Metal bundle unavailable:** check `Runtime/Plugins/macOS/MediaCaptureMetal.bundle`, macOS Editor import settings and native loading/signature errors. Restart Unity after replacing a loaded bundle.
- **VideoToolbox initialization fails:** confirm Unity is using Metal and a hardware H.264 encoder is available. There is no automatic software fallback.
- **Native DLL unavailable:** check Windows x64 import settings, all four FFmpeg DLLs and the Visual C++ runtime. Restart Unity after replacing a loaded native DLL.
- **NVENC initialization fails:** check D3D11, GPU, driver and available hardware encoder sessions. There is no automatic software fallback.
- **No images or many duplicates:** keep Game View rendering. Pausing, changing to a view that stops rendering or main-thread stalls affect capture.
- **Saving fails:** inspect the result message and `manifest.json`; check free space, directory permissions and file locks.
- **No sound on macOS:** check Unity Audio and an active `AudioListener`; audio engines bypassing that mix are not captured.
- **No sound on Windows:** verify output from the Unity process or its descendants, then inspect helper errors and audio statistics. Disabling Unity Audio does not necessarily disable Wwise output.

## Standalone screenshot

In Editor Play mode, call `UnityScreenshot.CaptureAsync("/absolute/path/page.png")` on the Unity main thread. The task completes only after a new PNG has been written atomically. Capture occurs at end of frame and includes overlay UI at the current Game View resolution. Existing output paths are rejected. Keep the Game View visible while waiting; exiting Play mode cancels pending capture. This one-shot API does not start or modify an AV recording session.
