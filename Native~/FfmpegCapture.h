#pragma once
#include <stdint.h>

#ifdef MC_EXPORTS
#define MC_API extern "C" __declspec(dllexport)
#else
#define MC_API extern "C" __declspec(dllimport)
#endif

// All functions return errors through status/error text; C++ exceptions never cross this ABI.
struct McOptions
{
    int32_t abiVersion;
    int32_t width;
    int32_t height;
    int32_t fpsNumerator;
    int32_t fpsDenominator;
    int32_t sampleRate;
    int32_t poolSize;
    int32_t quality;
    int32_t maxLagMilliseconds;
    const char* videoPath;
};

struct McStatus
{
    int32_t state; // 0 recording, 1 stopping, 2 complete, 3 faulted, 4 aborted
    int32_t queued;
    int64_t captured;
    int64_t encoded;
    int64_t duplicated;
    int64_t dropped;
    int64_t gpuBytes;
    int64_t maxLagFrames;
};

MC_API uint64_t __cdecl mc_create(void* sourceTexture, const McOptions* options, char* error, int errorCapacity);
MC_API void* __cdecl mc_queue_frame(uint64_t handle, void* sourceTexture, int64_t audioSample);
MC_API void* __cdecl mc_get_render_callback();
MC_API void __cdecl mc_poll_render(uint64_t handle);
MC_API int __cdecl mc_stop(uint64_t handle, int64_t durationSamples, const char* audioPath, const char* outputPath);
MC_API int __cdecl mc_status(uint64_t handle, McStatus* status, char* error, int errorCapacity);
MC_API void __cdecl mc_abort(uint64_t handle);
MC_API int __cdecl mc_destroy(uint64_t handle);
MC_API void __cdecl mc_shutdown();
MC_API const char* __cdecl mc_version();
