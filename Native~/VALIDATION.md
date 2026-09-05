# Validation / 验证记录

Date: 2026-09-04. Windows 11 build 26100, Unity 2022.3.67f1, D3D11, NVIDIA RTX 3070 / driver 610.47. Final runtime libraries were built from the sources recorded in `ffmpeg-dependency.json`.

## Final dependency test / 最终依赖验证

- MSVC compiled the C++ plugin with `/W4 /WX`; Unity compiled package C# and the host project's Designer Toolbox integration.
- A standalone D3D11 texture test encoded 90 frames at 1280×720 / 30 fps with 0 duplicates and 0 drops. H.264 and AAC were both written successfully.
- Cancellation and a deliberately late render callback after destruction completed safely.
- Unity generated and fully decoded a 1920×1080 / 30 fps MP4 with the final minimal FFmpeg libraries. A decoded frame showed only Game View, with moving text in the expected orientation; no editor UI was present.
- The build-source comparison found no changes to the 10138 archived FFmpeg files or the 9 archived NVIDIA header files. The libraries report LGPL 2.1 or later, with NVENC and native AAC available and libx264 absent.

## Same-scene comparison / 同场景对比

One sequential run, 20 seconds per phase, using a moving TextMesh, changing camera background and a looping process-audio tone. The editor's Game View remained visible. These are measurements of a simple test scene, not guarantees for a production game or a statistically repeated benchmark.

| Measurement | No recording | Native D3D11 | Previous image sequence |
| --- | ---: | ---: | ---: |
| Rendered game FPS | 191.15 | 173.56 | 162.42 |
| Unity CPU seconds / elapsed second | 1.59 | 1.49 | 2.15 |
| Finalization time | — | 1.10 s | 8.90 s |
| Captured / output frames | — | 601 / 601 | 601 / 601 |
| Duplicate / dropped captures | — | 4 / 0 | 3 / 0 |
| Estimated recording GPU textures | — | 79.1 MiB | 23.7 MiB |
| Preallocated CPU pixel buffers | — | 0 | 23.7 MiB |
| Capture API mean time | — | 0.0316 ms | 0.0233 ms |
| Final MP4 size | — | 5,637,917 bytes | 15,652,519 bytes |

CPU sampling used the Unity process counter at 500 ms intervals, excluding separate audio/Media Foundation helper CPU. A value of 1.49 means 1.49 CPU seconds per elapsed second across all Unity threads, not 149% of the whole machine. The editor was uncapped, so its frame rate also changes CPU consumption. Capture API timing alone would incorrectly suggest that the previous pipeline costs less: it excludes readback completion, image encoding and later video encoding.

The native texture pool uses more GPU memory to overlap capture and hardware encoding. Process private-memory peaks were about 7544 MiB (native) and 7485 MiB (previous pipeline), but those values include the entire editor. Background asset unloading reduced memory between the initial baseline and subsequent phases; these process totals are **not attributable recorder allocations**. No per-process GPU timing or driver-internal encoder memory measurement was collected.

两种录制使用不同编码器和默认质量设置，文件大小不能直接作为同画质压缩效率的结论。这里验证的是同一场景下的运行结果：原生流程减少了图片写盘和结束时的视频编码，但显存纹理占用更高。复杂场景仍需要按项目实际帧率、运动和画质要求评估。

## Audio/video timing / 声画时间

A final-library test requested a white camera flash and a 120 ms process-audio tone near 1, 5 and 9 seconds. Decoded video flashes began at 1.0667, 5.0667 and 9.0667 seconds. Decoded audio onsets began at 1.084, 5.084 and 9.092 seconds: audio followed the image by about **17, 17 and 25 ms**.

Video timing has one-frame resolution at 30 fps; audio onset detection used 2 ms RMS windows. SoundPlayer and the audio device introduce their own playback latency. This validates the tested process-audio path at these three points, not every Wwise buffering configuration or long-duration synchronization scenario.

## Ten-minute recording / 十分钟录制

The earlier broad development FFmpeg build produced a fully decodable 602-second Unity sample: 18035 captured frames, 18061 output frames, 105 duplicates and 26 dropped captures. Video duration was 602.033333 seconds; AAC duration was 602.030000 seconds. Audio diagnostics recorded zero discontinuity events, zero timestamp-error packets and 559 inserted-silence frames. The exact audio-loss count remained unknown (-1).

This longer sample used the same FFmpeg source revision and recording implementation but the **development library build**, before switching to the minimal distribution. The final minimal libraries passed the standalone and shorter Unity comparisons above; the ten-minute result must not be presented as a ten-minute run of those final DLLs.

## Failure and lifetime checks / 失败与生命周期

- Three consecutive recordings at 1280×720 succeeded using a Chinese filename and overwrite enabled.
- Calling stop twice returned the same task.
- Cancellation while stopping returned Aborted; destroying the recorder released pending texture cleanup and the audio helper.
- Locking the partial MP4 produced a write failure while preserving the existing destination bytes. Toolbox displayed the specific failure reason.
- Leaving Play during an active recording marked the session Aborted. Afterwards, Active was empty, the retirement list was empty, no Unity capture textures remained and no audio helper process remained.
- Null options and edit-mode starts were rejected before allocating a recorder.

## Editor startup dimensions / 编辑器启动尺寸

After a Toolbox start failed with a first-frame size mismatch, read-only inspection of the failed recorder found a 507×571 source and 506×570 output, while both Game View's target size and a later Screen query reported 1920×1080. The managed fix reads the Editor Game View target size at start and uses that size for Toolbox aspect-ratio calculation. It also replaces only the source texture on subsequent size changes. Following the fix, the user reported no further apparent issue. This is user-reported operational feedback; no independent post-fix compilation record or dedicated dynamic resolution-change test was collected.

启动失败会话的只读检查表明，录制器保存了 507×571 的采集尺寸及 506×570 的输出尺寸，而 Game View 目标尺寸与随后查询的 Screen 尺寸均为 1920×1080。修复后，启动尺寸及 Toolbox 宽高比计算使用 Game View 专用接口，后续尺寸变化仅重建采集纹理。用户反馈未再观察到异常；本次未另行采集修复后的编译记录，也未执行录制期间调整分辨率的专项验证。

## Direct BGRA capture / 直接 BGRA 采集（2026-09-05）

In the existing Play session, a frame-end probe captured the same 1920×1080 Game View through the previous RGBA-capture/BGRA-blit path and directly into BGRA. All 2,073,600 pixels had identical RGB values: zero differing pixels, zero mean channel error and zero maximum RGB-sum error. The reference channel range was 0–255. Both disabled and enabled `GL.sRGBWrite` settings produced the same comparison result for this frame; the implementation disables it during BGRA output. Temporary textures and the probe object were scheduled for destruction after the comparison. CPU pixel readback was used only by this diagnostic probe.

The updated managed assembly compiled successfully with Unity's existing response-file defines, references and source generators, with outputs redirected into ignored `artifacts/direct-bgra-build/`. The compiler reported the existing `PcmWaveWriter.finished` unused-field warning. This was a targeted compiler invocation, not a completed Unity import/domain-reload cycle: the Editor refresh request exceeded its 45-second wait and the current Play session had not loaded the updated assembly at the time of verification. A complete recording and transitions between direct and scaled capture still require verification with the updated assembly loaded.

同一帧对比中，1920×1080 的全部 2,073,600 个像素 RGB 完全一致。验证代码的 CPU 回读仅用于比较，不属于录制实现。修改后的托管程序集使用 Unity 现有编译参数独立编译成功；当前 Play 会话尚未加载新程序集，完整录制及直接采集与缩放路径之间的切换尚未验证。此结果不代表其他图形格式、MSAA 配置或设备已验证。

Matching-size recording no longer allocates the RGBA intermediate. At 1920×1080 this removes 8,294,400 bytes (approximately 7.9 MiB) from the capture texture estimate; the native texture pool remains unchanged. The earlier performance measurements in this report predate this optimization. No updated FPS or GPU-time comparison was collected.

Local probe source and result: `.deps/probe-direct-bgra.cs` and `artifacts/direct-bgra-probe.txt` (ignored).

## Remaining boundaries / 其余未覆盖范围

No built Player, Metal, AMD AMF, Intel QSV, forced GPU-device removal, full-disk condition, or real Wwise event was exercised. The editor Toolbox settings and error properties were checked through C#; mouse interaction with the save dialog was not automated. Changing actual Game View resolution during capture has not been dynamically verified. Texture memory estimates exclude GPU driver and encoder allocations.

An asset refresh with unchanged scripts reported no compilation/reload. A subsequent script-reload request in Play did not produce an observed immediate reload. Those requests are not counted as a passed active-session assembly-reload test; Play-exit cleanup was verified separately.

Local artifacts are stored under ignored `Native~/artifacts/`, including `benchmark-native.mp4`, `benchmark-legacy.mp4`, `sync-final.mp4`, `unity-native-long.mp4`, their session manifests and process/timing measurements. They are not included in the runtime package.
