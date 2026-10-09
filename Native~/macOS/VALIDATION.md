# Metal backend validation — 2026-09-24

Host: Apple M2, macOS 27.0 (26A428), Unity 6000.5.7f1, Metal, JCC Main scene. The package is 0.4.0 in the `codex/media-capture-metal` development worktree. Windows binaries were not changed. The shared C# native interface preserves their existing ABI, but Windows runtime was not retested here.

## Native integration

The standalone chart/tone check selected a hardware encoder (`hardware=1`) and encoded 90/90 frames at 640×360, 30 fps. There were zero repeats/drops and stop/finalization took 400 ms. Independent AVFoundation inspection found H.264, stereo AAC/48 kHz, 3.000 s video and audio, and non-silent decoded RMS −17.76 dBFS. Abort after 10 frames plus an intentionally late render event completed safely.

The universal arm64/x86_64 bundle builds with Apple clang 21, minimum deployment target macOS 12, and passes strict ad-hoc code-sign verification. Only arm64 runtime on the above M2 was tested. Intel runtime and a built Unity Player remain untested.

## Unity 1080×1920 recording

Artifact: `unity-metal-verified-30fps.mp4` under `/Users/lyu/Documents/ChatGPT/金铲铲/exports/media-capture-metal-v0.4.0-20260924/`.

| Measurement | Observed |
|---|---:|
| Game target / requested capture rate | 30 / 30 fps |
| Unity source frame / render callback count | 600 / 600 |
| Recorded audio duration | 20.074667 s |
| MP4 video duration / frame count | 20.1 s / 603 |
| Stop through completion | 0.759 s |
| Distinct submitted frames / CFR repeats | 592 / 11 |
| Rate-skipped source frames / cadence misses | 8 / 9 |
| Encoder pool/backpressure drops | 0 |
| Dropped audio / final pending frames | 0 / 0 |
| Hardware encoding confirmed | true |
| CPU video-pixel readback bytes | 0 |
| Estimated capture textures | 82,944,000 bytes |
| Main-thread capture submission average / maximum | 0.0312 / 1.4272 ms |
| Submission budget violations (>2 ms) | 0 |
| Focus-lost Editor updates during sample | 0 |

Submission timing excludes asynchronous GPU work, VideoToolbox's internal buffers/conversion and hardware-encoder time. Repeated CFR frames are disclosed above; this run does not establish zero repeats under all workloads. At the same source/recording rate, render jitter still produces occasional rate skips and repeats. Final frame count follows audio duration, including the tail.

Full MP4 decode passed. Independent inspection found H.264 and AAC/48 kHz/stereo, decoded RMS −36.04 dBFS, and video/audio duration difference 25.33 ms (under one 30 fps frame).

Twenty scheduled Unity AudioSource 880 Hz pulses were compared with a white marker driven by the same DSP schedule. All 20 video/audio onsets were found, maximum absolute onset difference was 33.33 ms (one frame). This is a diagnostic tone and marker over actual JCC rendering, not a final narrated production video. See `unity-metal-verified-30fps.sync.json`, `.validation.json`, and the session's `manifest.json` / `unity-test.json`.

Frame orientation was visually checked. A GPU display-color conversion fixed linear-light output from UNorm capture targets; four stable UI color patches then differed from the Game View screenshot by at most 4 RGB code values per channel. H.264 is lossy; this is not pixel-perfect or HDR validation. See `source-game.png`, `unity-color-corrected.png` and `color-check.json`.

## Final lifecycle checks

On the final managed implementation: abort during Recording -> Aborted; cancel during Stop -> Aborted with Success=false; immediately start another Automatic capture -> Metal backend -> Completed with Success=true. Background-execution state was restored after both abort and completion. The AudioListener callback and writer finish are synchronized to prevent disposal while a callback/stop is still draining. Evidence: `lifecycle-check.txt` and `lifecycle-restart.mp4`.

The package and JCC compiled after integration. Three existing validator regression cases passed (including exact-zero values). Gameplay logic, scene/prefab assets and formal production outputs were not changed by this backend validation.

## Earlier diagnostic samples

The first test has many source cadence repeats despite zero encoder backpressure; it did not log source-frame count or focus state, so it does not establish their cause. The first focused test also exposed unnecessary skips when using block-quantized DSP time directly. Those samples remain in the artifact directory as diagnostic history; the verified file named above is the current performance result. First-frame warm-up and Shader compilation can also affect a new capture; the current measurements are local observations, not a benchmark against Windows or external recorders.
