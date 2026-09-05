# Unity Media Capture

[English](README.md) · **简体中文**

Unity Media Capture 是**面向 Unity 应用内部的高性能音视频采集方案**，将 **Unity Game View 最终渲染结果和游戏进程音频**录制为 H.264 + AAC MP4 文件。默认后端采用 **Windows x64 / D3D11 / NVIDIA NVENC**：Unity 提供 GPU 纹理，原生插件通过 FFmpeg 硬件帧接口将纹理提交给 NVENC 编码。

版本 **0.3.0**。本包代码使用 [MIT](LICENSE)；FFmpeg 库使用 LGPL 2.1 或更新许可，见[第三方许可](ThirdPartyNotices.md)。

## 采集范围

录制器在一帧渲染结束后取得 Game View 的最终画面，包含已完成的相机合成、后处理和游戏内 UI。采集对象为该渲染结果，不会单独调用某一台 Camera 重新渲染。**桌面、Unity 工具栏、Scene View、Inspector 等编辑器界面不属于采集范围。**

Windows 音频使用 WASAPI 进程回环采集，即按目标进程获取其播放的音频。目标为 Unity 进程及其子进程，可包含 Unity Audio、Wwise 等音频引擎的输出。其他无关应用和麦克风不属于采集范围；Unity 编辑器播放的音频预览属于同一进程，可能被录入。

## 支持范围

| 项目 | 默认原生实现 |
| --- | --- |
| Unity | 2022.3；已在 2022.3.67f1 上验证 |
| 系统 | Windows x64；进程音频要求 Windows build 20348 或更新版本，建议 Windows 11 |
| 图形 API | D3D11 |
| 编码硬件 | 支持 H.264 NVENC 的 NVIDIA GPU 与兼容驱动；实测 RTX 3070 |
| 视频 | H.264，8-bit YUV 4:2:0，固定帧率，偶数尺寸 |
| 音频 | WASAPI PCM16 / 48 kHz / 双声道，保存时编码为 AAC 192 kb/s |
| 暂不支持 | Metal、D3D12、Vulkan、AMD AMF、Intel QSV、HDR 视频 |

不满足要求的配置将返回错误，默认后端不自动切换至 CPU 像素回读或软件编码，以保持性能行为可预期。图片序列后端保留为显式选项；原有 macOS AVFoundation 实现继续保留，Metal 原生录制不在当前实现范围内。

## 安装与调用

通过 Unity Package Manager 的 Git URL 安装此包：

```text
https://github.com/liuyuan0018/com.media-capture.git
```

本地开发也可以在 `Packages/manifest.json` 中引用 package 目录。Windows 原生插件及它依赖的 FFmpeg DLL 必须一起部署到 `Runtime/Plugins/x86_64`。依赖来源、重建步骤和许可见 [原生构建说明](Native~/README.md)。

在 Play 模式的 Unity 主线程调用：

```csharp
using GameFramework.MediaCapture.Unity;

UnityAvRecorder recorder = UnityAvRecorder.StartRecording(new RecordingOptions
{
    OutputPath = @"D:\Recordings\game.mp4",
    FrameRateNumerator = 30,
    HardwareQuality = 20,
    KeepIntermediateFiles = false
});

// 在用户点击停止时调用并等待完成。
RecordingResult result = await recorder.StopRecordingAsync();
if (!result.Success)
    UnityEngine.Debug.LogError(result.Message);
UnityEngine.Object.Destroy(recorder.gameObject);
```

录制期间应保持 Game View 持续渲染，并在保存完成后退出 Play 模式。`Abort()` 终止录制，不执行正常的最终文件生成流程。停止任务支持 `CancellationToken`；失败或取消时保留会话目录用于诊断。调用者负责销毁其创建的录制器对象。

本包提供录制 API 和 Player 命令行入口。录制控制界面由调用方实现，并通过 `RecordingOptions` 配置帧率、输出尺寸和编码质量。

## 命令行录制

已有 Player 启动入口接受绝对输出路径、秒数和整数帧率：

```text
Game.exe -force-d3d11 -gameFrameworkRecord "D:\Recordings\game.mp4" -gameFrameworkRecordSeconds 30 -gameFrameworkRecordFps 30
```

它使用默认原生后端并保留中间素材。必须运行有画面渲染的 Player；无图形或 `-nographics` 会话无法提供录制帧。启动入口将完成或失败写入日志，不会自动退出应用。当前版本尚未在构建出的 Player 中验证。

## 方案选型

性能设计针对 Unity 内部采集：默认原生后端通过 GPU 纹理传递视频帧并使用硬件视频编码，避免将完整视频帧像素回读到 CPU 内存。这一定位不表示本包比桌面、窗口或游戏录屏软件更快；当前尚未进行与这些软件的受控性能对比。Blit、GPU 纹理复制、同步和编码仍然存在开销。

当前实现以游戏运行期间的实时录制为目标，优先减少 CPU 原始像素处理、图片文件读写和停止后的整段视频编码，同时限制采集队列的内存占用。输出采用固定帧率 H.264 + AAC MP4；平台范围限定为 Windows D3D11。

| 方案 | 数据传递与编码方式 | 收益与成本 | 当前用途 |
| --- | --- | --- | --- |
| 图片序列 + 系统编码器 | GPU 回读到 CPU，写入 JPEG/PNG，停止后使用 Media Foundation 或 AVFoundation 编码 | 可保留逐帧素材；增加 CPU 图片处理、磁盘读写和结束耗时，JPEG 额外引入有损压缩 | 保留为显式选择的兼容后端 |
| FFmpeg 子进程 + 原始帧标准输入 | GPU 回读到 CPU，通过进程管道传入 `ffmpeg.exe` | 进程隔离且便于调用命令行参数；原始像素回读和跨进程传输仍然存在，使用硬件编码时还可能需要上传纹理 | 不作为默认实现 |
| 原生 FFmpeg + D3D11 硬件帧 | 插件复用 Unity D3D11 设备，通过 FFmpeg 向 NVENC 提交 GPU 纹理 | 避免原始像素回读和图片中间文件；需要维护 GPU 同步、纹理引用、原生依赖和驱动兼容性 | 默认实现 |

FFmpeg 提供编码器调用、时间戳、编码数据包和 MP4 封装能力，减少本包自行实现媒体处理逻辑的范围。NVENC 执行 H.264 硬件编码。**避免 GPU 回读依赖 D3D11 纹理与硬件编码器的兼容、同设备资源使用和正确的同步；仅将 FFmpeg 集成为原生 DLL 并不能自动满足这些条件。**

当前仅实现 NVENC，尚未实现 AMD AMF 和 Intel QSV 的硬件帧接入。FFmpeg 对其他编码器的支持不等于本包已完成相应设备管理和运行验证。默认不提供软件编码回退，调用者需显式选择其他后端。

## FFmpeg 原生集成

C# 通过 P/Invoke 调用 `GameFrameworkMediaCapture.dll` 导出的 C 接口。该 DLL 加载于 Unity 进程内，动态链接 `avcodec`、`avformat`、`avutil` 和 `swresample`。**录制过程不启动 `ffmpeg.exe`，也不通过标准输入传输原始视频帧。** WASAPI 音频采集由独立的 Windows 辅助进程执行。

```text
Game View 最终画面
  → 同尺寸：Graphics.Blit(null, BGRA RenderTexture)
    需缩放：采集到 RGBA RenderTexture → Blit 到 BGRA RenderTexture
  → 原生 D3D11 纹理池 / GPU 完成查询
  → FFmpeg D3D11 硬件帧 → NVIDIA NVENC → video.mp4

Unity 进程音频 → WASAPI 辅助进程 → audio.wav
停止后：video.mp4 的 H.264 数据包 + WAV → FFmpeg AAC 编码 / MP4 封装
  → output.partial.mp4 → 最终输出路径
```

| 组件 | 责任 |
| --- | --- |
| `UnityAvRecorder` / `NativeD3D11Capture` | 管理会话、取得帧末画面、分配采集与输出纹理、提交渲染线程事件 |
| `GameFrameworkMediaCapture.dll` | 管理 D3D11 纹理池、GPU 完成查询、编码工作线程和原生会话资源 |
| FFmpeg 库 / NVIDIA NVENC | FFmpeg 管理硬件帧、时间戳和编码数据包；NVENC 将视频帧编码为 H.264；FFmpeg 执行 AAC 编码和 MP4 封装 |
| Windows 音频辅助进程 | 按 Unity 进程范围采集音频，记录 QPC 时间信息并写入 WAV 及音频统计 |

## 核心设计

### Unity GPU 纹理提交至 FFmpeg 的调用过程

以下描述包内部的 D3D11 实现。公开录制入口仍为 `UnityAvRecorder.StartRecording()`，调用方无需自行构造 FFmpeg 帧。

```text
Unity 主线程：RenderTexture.GetNativeTexturePtr()
  → P/Invoke：mc_create(texturePointer, options)
  → Session：取得 Unity D3D11 设备，创建纹理池及 FFmpeg 硬件上下文

Unity 帧末：同尺寸直接 Graphics.Blit(null, BGRA)
           需缩放时 CaptureScreenshotIntoRenderTexture(RGBA) → Graphics.Blit(BGRA)
  → mc_queue_frame(handle, texturePointer, audioSample) → 请求 ID
  → CommandBuffer.IssuePluginEventAndData → Graphics.ExecuteCommandBuffer
Unity 渲染线程：RenderEvent → Session::Submit → CopyResource → End(query)
  → Session::PollGpu → GetData(query) 完成 → 编码待处理队列
原生编码线程：Session::Encode → AVFrame → avcodec_send_frame
  → h264_nvenc → avcodec_receive_packet → 写入 video.mp4
```

#### 1. 取得 Unity 纹理对象指针

`NativeD3D11Capture` 创建固定输出尺寸、单采样且无 mipmap 的 BGRA RenderTexture，并在创建后调用一次 `GetNativeTexturePtr()` 缓存原生指针。在 D3D11 后端，该指针对应 `ID3D11Texture2D*`；它是原生图形资源的接口指针，不是可在 CPU 上读取的像素数组地址。

```csharp
m_NativeTexture = m_OutputTexture.GetNativeTexturePtr();
m_Handle = mc_create(m_NativeTexture, ref nativeOptions, m_Error, m_Error.Length);
```

C# 通过 P/Invoke 将 `IntPtr` 传给原生 C 接口；`mc_create()` 将其转换为 `ID3D11Texture2D*` 后创建会话。此调用只传递纹理对象引用，不复制纹理像素到托管内存。

#### 2. 将 Unity 的 D3D11 设备关联至 FFmpeg

`Session` 调用 `source->GetDevice()` 取得纹理所属的 Unity D3D11 设备，通过 `GetImmediateContext()` 取得设备上下文，再用该设备创建编码纹理池。池内纹理使用 `D3D11_USAGE_DEFAULT` 和 `CPUAccessFlags = 0`，尺寸与格式匹配 Unity 的 BGRA 输出纹理。

`Session::OpenEncoder()` 创建 FFmpeg 硬件设备上下文和硬件帧上下文，并按下表关联资源：

| 对象或调用 | 赋值及作用 |
| --- | --- |
| `av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA)` | 分配 D3D11 硬件设备上下文 |
| `AVD3D11VADeviceContext.device` | 设置为从 Unity 纹理取得的设备，并 `AddRef()`；随后执行 `av_hwdevice_ctx_init()` |
| `av_hwframe_ctx_alloc(hardwareDevice)` | 创建属于该设备的硬件帧上下文 |
| `AVHWFramesContext.format / sw_format` | 分别设置为 `AV_PIX_FMT_D3D11` 和 `AV_PIX_FMT_BGRA`；后者描述纹理像素布局，不表示创建 CPU 帧 |
| `AVHWFramesContext.width / height / initial_pool_size` | 使用固定输出宽高，`initial_pool_size = 0`；实际输入纹理由插件自建纹理池提供，随后执行 `av_hwframe_ctx_init()` |
| `AVCodecContext.pix_fmt / hw_frames_ctx` | 设置为 `AV_PIX_FMT_D3D11` 并持有上述硬件帧上下文引用，再通过 `avcodec_open2()` 打开 `h264_nvenc` |

FFmpeg 因而使用 Unity 纹理所属的同一 D3D11 设备。这里的 `D3D11VA` 是 FFmpeg 硬件设备类型名称，实际编码器由 `h264_nvenc` 选择。

#### 3. 在 Unity 渲染线程提交 GPU 复制

`CaptureNativeFrames()` 仍在 `WaitForEndOfFrame` 后采集，使用公开的引擎 API，不要求接入项目定制渲染管线。`NativeD3D11Capture.Capture()` 比较实际帧尺寸和固定输出尺寸：

- 尺寸相同：直接调用 `Graphics.Blit(null, m_OutputTexture)`，从当前帧缓冲写入 BGRA 输出纹理。不调用 `CaptureScreenshotIntoRenderTexture()`，也不分配 RGBA 中间纹理。
- 尺寸不同：按实际画面尺寸分配 RGBA 纹理，采集完整 Game View，再用 `Graphics.Blit()` 缩放至 BGRA 输出纹理。奇数画面尺寸向下取偶数的情况也使用该路径，避免裁剪边缘。

当前 Unity D3D11 实现对空源纹理的 `Graphics.Blit()` 通过 `GrabPixels()` 取得帧缓冲；兼容格式使用 GPU 复制，不兼容格式使用内部绘制完成转换。这里由内部绘制直接完成帧缓冲到 BGRA 的转换，省去 RGBA 中间纹理及其额外传输。写入 BGRA 期间关闭额外的 sRGB 写入转换，完成后恢复原有状态。不同尺寸保留截图加缩放路径，是因为帧缓冲采集中的矩形限制不能等同于完整画面缩放。

`mc_queue_frame(handle, texturePointer, audioSample)` 保存会话引用、纹理的 `ComPtr` 引用和采集时间，返回请求 ID；该函数本身不执行纹理复制。C# 将请求 ID 传入 `CommandBuffer.IssuePluginEventAndData()`，再通过 `Graphics.ExecuteCommandBuffer()` 提交。事件 ID `0` 对应帧提交，回调地址由 `mc_get_render_callback()` 提供。

Unity 渲染线程执行 `RenderEvent()`，按请求 ID 取得原生请求，再调用 `Session::Submit()`。该方法检查纹理设备、尺寸和格式，选择空闲池纹理，并依次执行 `CopyResource(slotTexture, unityOutputTexture)` 和 `End(completionQuery)`。GPU 复制使编码输入与 Unity 下一帧会覆盖的输出纹理分离。

#### 4. 确认 GPU 已完成复制

`CopyResource()` 提交的是 GPU 命令，CPU 函数返回不代表复制完成。`Session::PollGpu()` 在渲染回调中通过 `GetData(query, ..., D3D11_ASYNC_GETDATA_DONOTFLUSH)` 检查 `D3D11_QUERY_EVENT`：返回 `S_FALSE` 时继续保留待完成状态，确认完成后才将对应纹理加入编码待处理队列。事件 ID `1` 用于轮询；帧提交也会调用轮询。编码线程不直接读取仍在复制中的纹理。

#### 5. 将池纹理写入 FFmpeg 硬件帧

`Session::Encode()` 创建 `AVFrame`，设置硬件帧格式、输出尺寸、输出帧时间戳和硬件帧上下文。以下为该方法的关键赋值摘录，省略帧分配、错误处理和销毁流程：

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

在 `AV_PIX_FMT_D3D11` 约定下，`data[0]` 保存的是 `ID3D11Texture2D*`，而非 CPU 图像平面；`data[1]` 表示纹理数组索引，本实现每张纹理只有一个元素，因此使用索引 `0`，代码表示为 `nullptr`。`pts` 使用编码器时间基准中的输出帧序号，不直接写入 QPC 原始计数。

`avcodec_send_frame()` 将该硬件帧交给 `h264_nvenc`。返回 `EAGAIN` 时，代码先取出已有编码数据包再重试；`avcodec_receive_packet()` 取得压缩后的 H.264 数据，随后写入中间 MP4。只有编码数据包进入 CPU 文件写入流程，原始视频像素不经过 GPU 回读。

#### 6. 在所有引用释放后复用纹理

`frame->buf[0]` 通过 `av_buffer_create()` 将纹理使用权关联到 FFmpeg 引用计数。释放回调销毁一个 `LeaseRef` 引用；当 FFmpeg、编码队列以及用于重复帧的上一帧记录均不再持有该纹理的 `FrameLease` 时，`FrameLease` 析构函数才将池槽标记为空闲。因此，`avcodec_send_frame()` 返回或临时 `AVFrame` 销毁后，都不能直接覆盖池纹理。

插件启用 D3D11 多线程保护，并在最后一个会话释放相关设备引用后恢复原设置；Unity 始终拥有图形设备。默认视频路径不调用 `ReadPixels`、`AsyncGPUReadback` 或 `av_hwframe_transfer_data()`，也不执行 JPEG/PNG 编码。该路径仍包含 GPU 复制、格式转换和同步，不属于完全无复制的实现。

对应源码：[帧末调度](Runtime/Unity/UnityAvRecorder.Native.cs)中的 `CaptureNativeFrames()`、[Unity 纹理与原生调用](Runtime/Unity/NativeD3D11Capture.cs)中的构造函数、`Capture()` 和 `IssueEvent()`，以及[原生 D3D11 与 FFmpeg 实现](Native~/FfmpegCapture.cpp)中的 `Session`、`OpenEncoder()`、`Submit()`、`PollGpu()`、`Encode()` 和 `FrameLease`。

### 固定输出尺寸与编辑器上下文

编辑器启动录制时使用 `Handles.GetMainGameViewSize()` 获取 Game View 渲染尺寸，避免工具按钮回调中的 `Screen.width/height` 返回工具窗口尺寸。视频输出尺寸在会话创建时确定，并保持不变。

BGRA 输出纹理、原生指针和编码器配置在整个会话期间保持不变。实际渲染尺寸与输出尺寸不同期间，录制器按需创建或重建 RGBA 中间纹理；恢复同尺寸后释放该纹理并恢复直接采集。缩放路径中的宽高比变化会导致拉伸，当前不自动裁剪或添加黑边；需要采用新输出尺寸时应结束当前会话并重新录制。

### 时间基准与队列上限

Windows 音频辅助进程和视频采集使用 QPC 高精度时钟作为公共时间基准。编码器按目标帧率生成输出时间戳，并选用对应时刻之前最近的可用画面。缺少新画面时重复上一帧，以保持视频时长与音频时间一致；重复帧无法恢复未采集的运动细节。

纹理池和待执行渲染请求均有数量上限。无可用纹理时丢弃本次采集并计数；编码落后时钟超过配置阈值时终止会话并返回错误，避免持续积压占用资源。

### 文件生成与停止流程

录制期间，编码线程将 H.264 写入会话目录中的 `video.mp4`，音频辅助进程写入 `audio.wav`。停止时，辅助进程按停止时间补齐音频时长，FFmpeg 将 WAV 编码为 AAC，并将已有 H.264 数据包复制到 `output.partial.mp4`，无需再次编码视频。封装成功后，录制器才移动该文件或替换最终目标文件。

该设计将视频编码分散至录制期间，并保留独立的诊断文件。停止后仍需处理完整音轨及文件封装，因此保存耗时随录制长度增长。RIFF WAV 接近 4 GiB 时会返回错误，48 kHz 双声道 PCM16 对应约 6.2 小时；磁盘空间需同时容纳中间文件和最终文件。

### 取消与资源释放

取消操作停止提交新帧，并请求原生工作线程退出。C# 在原生会话确认可以销毁前保留输出纹理，避免编码线程访问已释放资源。待执行渲染事件通过请求 ID 查找有效请求，取消后到达的事件不会直接访问已经销毁的请求对象。程序集重载和插件卸载会请求关闭原生会话。

### 编码参数与画质取舍

当前使用 NVENC `p4`、VBR 码率控制和默认 CQ 20，并关闭 B 帧及前向分析，以限制帧重排和预分析带来的缓冲需求。CQ 为质量控制参数，数值降低通常提高画质并增大文件，但不保证固定码率或文件大小。H.264 + AAC MP4 用于常规播放与分享，当前视频格式限定为 8-bit YUV 4:2:0，不提供无损或 HDR 输出。

FFmpeg 的库调用和命令行调用属于集成方式，不构成独立的画质等级。画质主要取决于输入像素、实际编码器、码率控制、预设和色彩转换。当前选型的收益在于减少 CPU 像素处理和中间图片写盘；尚无相同码率或相同画质条件下的对比结果，不能据此认定 NVENC 的压缩效率优于 x264 或旧系统编码器。

### 原生依赖与可重建性

运行时使用按需构建的四个 FFmpeg 共享库，启用本包所需的 NVENC、AAC、PCM、D3D11 硬件帧及文件封装能力，不包含 `ffmpeg.exe` 或 x264。源码归档、固定提交、哈希和构建配置随包提供，以支持依赖核对和重新构建。构建步骤见[原生构建说明](Native~/README.md)，分发文件及对应许可见[第三方许可](ThirdPartyNotices.md)。

## 设置与诊断

| 设置 | 默认值 | 说明 |
| --- | --- | --- |
| `OutputPath` | 必填 | 绝对 `.mp4` 路径 |
| `VideoBackend` | `NativeD3D11` | 可显式选择 `ImageSequence` |
| `FrameRateNumerator / Denominator` | `24 / 1` | 例如 `30 / 1`、`30000 / 1001` |
| `OutputWidth / OutputHeight` | `0 / 0` | 使用开始时 Game View 尺寸并取偶数；自定义时两个值都必须是正偶数 |
| `HardwareQuality` | `20` | NVENC CQ，范围 0–51 |
| `GpuTexturePoolSize` | `8` | 原生视频纹理数量，范围 4–32 |
| `MaxEncodingLagMilliseconds` | `2000` | 编码落后时钟的失败阈值 |
| `EncoderTimeoutSeconds` | `300` | 停止及保存等待上限 |
| `KeepIntermediateFiles` | `false` | 成功后是否保留原始 WAV / 中间视频 |
| `OverwriteExisting` | `false` | 是否允许完成后替换已有输出 |

调用方设置自定义输出尺寸时，应同时提供正偶数宽度与高度，并保持目标画面的宽高比。录制期间的尺寸变化处理见“固定输出尺寸与编辑器上下文”。

`RecordingResult` 提供实际采集、输出、重复和丢弃视频帧数。`Performance` 提供采集 API 耗时、队列深度和估算纹理显存；**采集 API 耗时不是整个录制系统的 CPU/GPU 耗时**，显存估算也不包含驱动和编码器内部资源。

音频的精确丢失帧数无法从 WASAPI 完整推导时，`DroppedAudioFrames` 为 `-1`，`DroppedAudioFramesKnown` 为 `false`。音频统计另行记录不连续事件、时间戳错误和补静音帧数；补静音也可能由音频空闲或结束时长补齐产生，不等同于音频丢失量。

原生会话将这些数据写入输出目录下 `.media-capture-*` 中的 `manifest.json`；音频辅助进程写入 `audio.wav.stats.json`。成功且不保留中间素材时，仍保留诊断 JSON。

## 验证结果与适用边界

在直接 BGRA 优化之前，一次同场景、每段 20 秒的对比中，原生录制约 173.56 FPS，旧图片序列流程约 162.42 FPS；停止保存耗时分别为 1.10 秒和 8.90 秒。这是简单场景实测，两种编码器的参数不同，不是同画质压缩效率评测，也不代表此次优化后的性能数据。此次优化的实际耗时收益尚未测量。CPU 数据、声画时间与限制见[验证报告](Native~/VALIDATION.md)。

1920×1080 的 RGBA/BGRA 纹理每像素占 4 字节，一张纹理的像素数据为 `1920 × 1080 × 4 = 8,294,400` 字节，约 7.91 MiB（`1 MiB = 1,048,576 字节`）。录制纹理显存估算的组成如下：

| 配置 | 录制使用的 GPU 纹理 | 像素存储估算 |
| --- | --- | ---: |
| 直接 Blit 优化前的原生后端 | 1 张 RGBA 中间纹理 + 1 张 BGRA 输出纹理 + 8 张原生纹理池纹理 | 79.1 MiB |
| 当前原生后端，采集与输出尺寸相同 | 1 张 BGRA 输出纹理 + 8 张原生纹理池纹理 | 71.2 MiB |
| 对比中的旧图片序列后端 | 3 张用于异步 GPU 回读的采集纹理 | 23.7 MiB |

原生插件按 `GpuTexturePoolSize` 预分配纹理，默认数量为 8。插件将采集画面复制到空闲池纹理，使 Unity 写入下一帧时，前面帧的 GPU 复制和编码可以继续执行；只有全部帧引用释放后，池纹理才可再次使用。分配 8 张纹理不表示始终有 8 帧等待编码。旧流程将像素回读到 CPU 内存后继续进行图片处理，在这次对比中还预分配了 23.7 MiB 的 CPU 像素缓冲。

当前的 71.2 MiB 是根据纹理尺寸和分配数量计算的结果，不是新一轮 GPU 显存实测。需要缩放时，还会按实际采集尺寸增加一张 RGBA 中间纹理。这些估算只统计录制纹理的像素存储，不包含驱动和编码器内部资源、分配对齐开销及等待销毁的临时资源。

目前已验证 Windows 11 / Unity 2022.3.67f1 / D3D11 / RTX 3070 的原生纹理编码、Game View 1080p 录制、进程声音、文字方向和红绿蓝画面顺序。样片没有编辑器界面。详细证据、未覆盖设备与剩余验证见 [开发与验证记录](Native~/DEVELOPMENT_PLAN.md)。

H.264 是有损格式，YUV 4:2:0 会降低彩色细线和文字边缘的色彩分辨率。测试中的 GPU RGBA → BGRA 转换保留了像素值，视频解码结果仍存在色差。上述结果不代表像素无损、全设备兼容或复杂场景零丢帧。录制期间连续调整分辨率尚未进行专项验证；启动尺寸修复的诊断和反馈记录见[验证报告](Native~/VALIDATION.md)。

## 旧实现与扩展

设置 `VideoBackend = RecordingVideoBackend.ImageSequence` 可使用原有图片序列流程，其 JPEG/PNG、GPU 回读队列和图片写盘参数仍有效。显式传入 `IRecordingEncoderBackend` 也会使用该流程，由自定义后端处理 `RecordingEncodeRequest`。这些参数不改变默认原生路径。

Windows 旧流程使用 Media Foundation；macOS 旧流程使用 AVFoundation。两者的源码继续保留，当前版本的新验证集中于 Windows D3D11 原生路径。

## 故障诊断

- **原生 DLL 加载失败**：检查 Windows x64 导入设置、四个 FFmpeg DLL 和 Visual C++ 运行库；替换已加载的 DLL 后重启 Unity。
- **NVENC 初始化失败**：检查 D3D11、GPU 型号、驱动和可用硬件编码会话。不会自动改用软件编码。
- **无视频帧或重复帧过多**：检查 Game View 是否持续渲染；暂停、切换至停止渲染的视图或主线程阻塞均会影响采集。
- **停止失败**：查看结果消息与会话 `manifest.json`；确认磁盘空间、输出目录权限以及文件是否被其他程序占用。
- **无音频输出**：确认音频由目标 Unity 进程或其子进程播放，并检查辅助进程退出信息及音频统计。Unity Audio 与 Wwise 的启用状态需分别检查。
