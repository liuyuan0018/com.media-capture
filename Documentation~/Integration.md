# Integration and verification

Use PlayMode or a Player for runtime-facing verification. Stop the recorder before leaving PlayMode so audio capture and MP4 finalization can finish. Windows does not require an active Unity `AudioListener`; the helper captures final audio emitted by the Unity process. The macOS path still captures the active Unity `AudioListener` mix.

For automated Player capture, pass an absolute output path and a bounded duration through the command-line arguments documented in the package README. Game-specific showcase arguments may be supplied alongside them; the recorder has no dependency on the game's lifecycle or composition root.

Verify every produced artifact independently:

- exactly one H.264 video track;
- exactly one AAC audio track with the expected sample rate and channel count;
- A/V duration difference no greater than one output video frame;
- decoded audio peak and RMS above the chosen non-silence threshold for a scene known to emit sound;
- manifest has zero pending frames and reports any duplicated video or dropped audio samples.

`Time.captureFramerate` is deliberately not changed. It alters simulation time but Unity audio continues on the DSP clock, which can create a false sense of deterministic synchronization. This package instead emits constant-frame-rate video from real sample timestamps.
