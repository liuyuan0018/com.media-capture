// Unity framebuffer -> IOSurface-backed Metal textures -> VideoToolbox H.264.
// No pixel readback or image intermediates. All encoding/file work is off the render thread.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <CoreVideo/CoreVideo.h>
#import <VideoToolbox/VideoToolbox.h>
#import <AVFoundation/AVFoundation.h>
#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
#include <thread>
#include <algorithm>
#include <stdexcept>

#define MC_EXPORT extern "C" __attribute__((visibility("default")))
namespace {
struct Options {
    int abi, width, height, fpsNumerator, fpsDenominator, sampleRate, poolSize, quality, maxLagMs;
    const char* videoPath;
};
struct Status {
    int state, queued;
    int64_t captured, encoded, duplicated, dropped, gpuBytes, maxLagFrames;
};
static IUnityGraphicsMetalV1* unityMetal = nullptr;
static void Check(OSStatus result, const char* operation) {
    if (result != noErr) throw std::runtime_error(std::string(operation) + ": " + std::to_string(result));
}
static std::string ErrorText(NSError* e) { return e ? e.localizedDescription.UTF8String : "Unknown AVFoundation error"; }
static void CopyError(const std::string& e, char* out, int capacity) {
    if (out && capacity > 0) { size_t n = std::min(e.size(), (size_t)capacity - 1); memcpy(out, e.data(), n); out[n] = 0; }
}
struct Slot {
    CVPixelBufferRef pixels = nullptr;
    CVMetalTextureRef view = nullptr;
    id<MTLTexture> texture;
    ~Slot() { if (view) CFRelease(view); if (pixels) CFRelease(pixels); }
};
struct Session;
struct EncodeContext { std::shared_ptr<Session> session; std::shared_ptr<Slot> slot; };
static void Encoded(void*, void*, OSStatus, VTEncodeInfoFlags, CMSampleBufferRef);

struct Session : std::enable_shared_from_this<Session> {
    Options options;
    std::string videoPath, audioPath, outputPath;
    std::atomic<int> state{0}, requests{0}, gpuPending{0}, workerPending{0}, inFlight{0};
    std::atomic<int64_t> captured{0}, encoded{0}, duplicated{0}, dropped{0};
    std::mutex errorMutex, poolMutex, flightMutex;
    std::condition_variable flightChanged;
    std::string error;
    CVMetalTextureCacheRef cache = nullptr;
    VTCompressionSessionRef compression = nullptr;
    bool hardware = false;
    std::vector<std::shared_ptr<Slot>> slots;
    std::shared_ptr<Slot> previous;
    int64_t nextFrame = 0, finalSamples = 0;
    dispatch_queue_t worker = dispatch_queue_create("media.capture.metal.encode", DISPATCH_QUEUE_SERIAL);
    dispatch_queue_t muxer = dispatch_queue_create("media.capture.metal.write", DISPATCH_QUEUE_SERIAL);
    AVAssetWriter* writer;
    AVAssetWriterInput* videoInput;
    int64_t lagFrames() const { return std::max<int64_t>(1, (int64_t)options.maxLagMs * options.fpsNumerator / (1000LL * options.fpsDenominator)); }
    std::string Error() { std::lock_guard<std::mutex> l(errorMutex); return error; }
    void Fail(const std::string& message) {
        std::lock_guard<std::mutex> l(errorMutex);
        if (state < 2) { error = message; state = 3; }
        flightChanged.notify_all();
    }
    bool cancelled() const { return state >= 3; }
    void Schedule(void (^job)()) {
        workerPending++;
        auto self = shared_from_this();
        dispatch_async(worker, ^{ @autoreleasepool {
            try { job(); } catch (const std::exception& e) { self->Fail(e.what()); }
            self->workerPending--;
        }});
    }
    void Init(id<MTLDevice> device, const Options& input) {
        options = input; videoPath = input.videoPath;
        Check(CVMetalTextureCacheCreate(kCFAllocatorDefault, nullptr, device, nullptr, &cache), "CVMetalTextureCacheCreate");
        NSDictionary* attrs = @{
            (id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
            (id)kCVPixelBufferWidthKey: @(input.width), (id)kCVPixelBufferHeightKey: @(input.height),
            (id)kCVPixelBufferMetalCompatibilityKey: @YES, (id)kCVPixelBufferIOSurfacePropertiesKey: @{}
        };
        NSDictionary* spec = @{ (id)kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder: @YES };
        Check(VTCompressionSessionCreate(kCFAllocatorDefault, input.width, input.height, kCMVideoCodecType_H264,
            (__bridge CFDictionaryRef)spec, (__bridge CFDictionaryRef)attrs, nullptr, Encoded, nullptr, &compression), "Create hardware H.264 encoder");
        Check(VTSessionSetProperty(compression, kVTCompressionPropertyKey_RealTime, kCFBooleanTrue), "Set realtime");
        Check(VTSessionSetProperty(compression, kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse), "Disable frame reorder");
        Check(VTSessionSetProperty(compression, kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel), "Set H.264 profile");
        double fps = (double)input.fpsNumerator / input.fpsDenominator;
        // Preserve the public 0..51 quality scale (lower is better); this is not NVENC CQ equivalence.
        double quality = 1.0 - (double)input.quality / 51.0;
        Check(VTSessionSetProperty(compression, kVTCompressionPropertyKey_Quality, (__bridge CFNumberRef)@(quality)), "Set quality");
        Check(VTSessionSetProperty(compression, kVTCompressionPropertyKey_ExpectedFrameRate, (__bridge CFNumberRef)@(fps)), "Set frame rate");
        Check(VTSessionSetProperty(compression, kVTCompressionPropertyKey_MaxKeyFrameInterval, (__bridge CFNumberRef)@((int)(fps * 2))), "Set keyframe interval");
        VTSessionSetProperty(compression, kVTCompressionPropertyKey_ColorPrimaries, kCVImageBufferColorPrimaries_ITU_R_709_2);
        VTSessionSetProperty(compression, kVTCompressionPropertyKey_TransferFunction, kCVImageBufferTransferFunction_ITU_R_709_2);
        VTSessionSetProperty(compression, kVTCompressionPropertyKey_YCbCrMatrix, kCVImageBufferYCbCrMatrix_ITU_R_709_2);
        Check(VTCompressionSessionPrepareToEncodeFrames(compression), "Prepare hardware encoder");
        CFTypeRef used = nullptr;
        Check(VTSessionCopyProperty(compression, kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder, nullptr, &used), "Query hardware encoder");
        hardware = used && CFEqual(used, kCFBooleanTrue); if (used) CFRelease(used);
        if (!hardware) throw std::runtime_error("VideoToolbox did not select a hardware encoder");
        for (int i = 0; i < input.poolSize; i++) {
            auto slot = std::make_shared<Slot>();
            Check(CVPixelBufferCreate(kCFAllocatorDefault, input.width, input.height, kCVPixelFormatType_32BGRA,
                (__bridge CFDictionaryRef)attrs, &slot->pixels), "Allocate IOSurface pixel buffer");
            Check(CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault, cache, slot->pixels, nullptr,
                MTLPixelFormatBGRA8Unorm, input.width, input.height, 0, &slot->view), "Map IOSurface to Metal");
            slot->texture = CVMetalTextureGetTexture(slot->view);
            if (!slot->texture) throw std::runtime_error("Metal pixel-buffer texture is unavailable");
            slots.push_back(std::move(slot));
        }
    }
    std::shared_ptr<Slot> Acquire() {
        std::lock_guard<std::mutex> l(poolMutex);
        for (auto& slot : slots) if (slot.use_count() == 1) return slot;
        dropped++; return {};
    }
    void Encode(const std::shared_ptr<Slot>& slot) {
        std::unique_lock<std::mutex> l(flightMutex);
        if (!flightChanged.wait_for(l, std::chrono::milliseconds(options.maxLagMs), [&] { return inFlight < options.poolSize || cancelled(); }))
            throw std::runtime_error("Hardware encoder exceeded its bounded queue lag");
        if (cancelled()) return;
        inFlight++; l.unlock();
        auto* context = new EncodeContext{shared_from_this(), slot};
        auto result = VTCompressionSessionEncodeFrame(compression, slot->pixels,
            CMTimeMake(nextFrame * options.fpsDenominator, options.fpsNumerator),
            CMTimeMake(options.fpsDenominator, options.fpsNumerator), nullptr, context, nullptr);
        if (result != noErr) { delete context; inFlight--; flightChanged.notify_all(); Check(result, "Encode hardware frame"); }
        nextFrame++;
    }
    void Ready(const std::shared_ptr<Slot>& slot, int64_t sample) {
        if (cancelled()) return;
        int64_t ordinal = sample * options.fpsNumerator / ((int64_t)options.sampleRate * options.fpsDenominator);
        if (ordinal < nextFrame) { dropped++; return; }
        if (ordinal - nextFrame > lagFrames()) throw std::runtime_error("Capture cadence gap exceeded MaxEncodingLagMilliseconds");
        if (!previous) previous = slot;
        while (nextFrame < ordinal && !cancelled()) { Encode(previous); duplicated++; }
        if (!cancelled()) { Encode(slot); previous = slot; captured++; }
    }
    void Append(CMSampleBufferRef sample) {
        if (cancelled()) return;
        if (!writer) {
            NSError* error = nil;
            writer = [[AVAssetWriter alloc] initWithURL:[NSURL fileURLWithPath:@(videoPath.c_str())] fileType:AVFileTypeMPEG4 error:&error];
            if (!writer) throw std::runtime_error(ErrorText(error));
            videoInput = [[AVAssetWriterInput alloc] initWithMediaType:AVMediaTypeVideo outputSettings:nil sourceFormatHint:CMSampleBufferGetFormatDescription(sample)];
            videoInput.expectsMediaDataInRealTime = YES;
            if (![writer canAddInput:videoInput]) throw std::runtime_error("MP4 writer rejected H.264 input");
            [writer addInput:videoInput];
            if (![writer startWriting]) throw std::runtime_error(ErrorText(writer.error));
            [writer startSessionAtSourceTime:kCMTimeZero];
        }
        auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(options.maxLagMs);
        while (!videoInput.readyForMoreMediaData && !cancelled()) {
            if (writer.status == AVAssetWriterStatusFailed) throw std::runtime_error(ErrorText(writer.error));
            if (std::chrono::steady_clock::now() > deadline) throw std::runtime_error("MP4 writer exceeded its bounded queue lag");
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        if (!cancelled()) {
            if (![videoInput appendSampleBuffer:sample]) throw std::runtime_error(ErrorText(writer.error));
            encoded++;
        }
    }
    void FinishWriter(AVAssetWriter* target) {
        dispatch_semaphore_t done = dispatch_semaphore_create(0);
        [target finishWritingWithCompletionHandler:^{ dispatch_semaphore_signal(done); }];
        while (dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, 100 * NSEC_PER_MSEC))) {
            if (cancelled()) { [target cancelWriting]; throw std::runtime_error("Recording cancelled"); }
        }
        if (target.status != AVAssetWriterStatusCompleted) throw std::runtime_error(ErrorText(target.error));
    }
    void MuxAudio() {
        // Video is already H.264. Copy compressed video packets and encode only the PCM audio as AAC.
        NSError* error = nil;
        AVURLAsset* va = [AVURLAsset URLAssetWithURL:[NSURL fileURLWithPath:@(videoPath.c_str())] options:nil];
        AVURLAsset* aa = [AVURLAsset URLAssetWithURL:[NSURL fileURLWithPath:@(audioPath.c_str())] options:nil];
        AVAssetTrack* vt = [va tracksWithMediaType:AVMediaTypeVideo].firstObject;
        AVAssetTrack* at = [aa tracksWithMediaType:AVMediaTypeAudio].firstObject;
        if (!vt || !at) throw std::runtime_error("Finalization requires captured video and Unity PCM audio");
        AVAssetReader* vr = [[AVAssetReader alloc] initWithAsset:va error:&error];
        AVAssetReader* ar = [[AVAssetReader alloc] initWithAsset:aa error:&error];
        if (!vr || !ar) throw std::runtime_error(ErrorText(error));
        AVAssetReaderTrackOutput* vo = [[AVAssetReaderTrackOutput alloc] initWithTrack:vt outputSettings:nil];
        AVAssetReaderTrackOutput* ao = [[AVAssetReaderTrackOutput alloc] initWithTrack:at outputSettings:@{
            AVFormatIDKey: @(kAudioFormatLinearPCM), AVLinearPCMBitDepthKey: @16,
            AVLinearPCMIsFloatKey: @NO, AVLinearPCMIsBigEndianKey: @NO, AVLinearPCMIsNonInterleaved: @NO }];
        vo.alwaysCopiesSampleData = NO; ao.alwaysCopiesSampleData = NO;
        [vr addOutput:vo]; [ar addOutput:ao];
        ar.timeRange = CMTimeRangeMake(kCMTimeZero, CMTimeMake(finalSamples, options.sampleRate));
        AVAssetWriter* result = [[AVAssetWriter alloc] initWithURL:[NSURL fileURLWithPath:@(outputPath.c_str())] fileType:AVFileTypeMPEG4 error:&error];
        if (!result) throw std::runtime_error(ErrorText(error));
        AVAssetWriterInput* vi = [[AVAssetWriterInput alloc] initWithMediaType:AVMediaTypeVideo outputSettings:nil
            sourceFormatHint:(__bridge CMFormatDescriptionRef)vt.formatDescriptions.firstObject];
        AVAssetWriterInput* ai = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeAudio outputSettings:@{
            AVFormatIDKey: @(kAudioFormatMPEG4AAC), AVSampleRateKey: @(options.sampleRate),
            AVNumberOfChannelsKey: @2, AVEncoderBitRateKey: @192000 }];
        if (![result canAddInput:vi] || ![result canAddInput:ai]) throw std::runtime_error("Final MP4 writer rejected tracks");
        [result addInput:vi]; [result addInput:ai]; result.shouldOptimizeForNetworkUse = YES;
        if (![result startWriting] || ![vr startReading] || ![ar startReading]) throw std::runtime_error(ErrorText(result.error ?: vr.error ?: ar.error));
        [result startSessionAtSourceTime:kCMTimeZero];
        bool vd = false, ad = false;
        while ((!vd || !ad) && !cancelled()) { @autoreleasepool {
            bool progress = false;
            if (!vd && vi.readyForMoreMediaData) {
                CMSampleBufferRef b = [vo copyNextSampleBuffer];
                if (b) { BOOL ok = [vi appendSampleBuffer:b]; CFRelease(b); if (!ok) throw std::runtime_error(ErrorText(result.error)); }
                else { if (vr.status == AVAssetReaderStatusFailed) throw std::runtime_error(ErrorText(vr.error)); [vi markAsFinished]; vd = true; }
                progress = true;
            }
            if (!ad && ai.readyForMoreMediaData) {
                CMSampleBufferRef b = [ao copyNextSampleBuffer];
                if (b) { BOOL ok = [ai appendSampleBuffer:b]; CFRelease(b); if (!ok) throw std::runtime_error(ErrorText(result.error)); }
                else { if (ar.status == AVAssetReaderStatusFailed) throw std::runtime_error(ErrorText(ar.error)); [ai markAsFinished]; ad = true; }
                progress = true;
            }
            if (result.status == AVAssetWriterStatusFailed) throw std::runtime_error(ErrorText(result.error));
            if (!progress) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }}
        if (cancelled()) { [vr cancelReading]; [ar cancelReading]; [result cancelWriting]; return; }
        FinishWriter(result);
    }
    void Finish() {
        if (cancelled()) return;
        if (!previous) throw std::runtime_error("No Metal video frames were captured");
        int64_t target = (finalSamples * options.fpsNumerator + (int64_t)options.sampleRate * options.fpsDenominator - 1) /
            ((int64_t)options.sampleRate * options.fpsDenominator);
        if (target - nextFrame > lagFrames()) throw std::runtime_error("Final frame gap exceeded MaxEncodingLagMilliseconds");
        while (nextFrame < target && !cancelled()) { Encode(previous); duplicated++; }
        Check(VTCompressionSessionCompleteFrames(compression, kCMTimeInvalid), "Drain hardware encoder");
        dispatch_sync(muxer, ^{});
        if (cancelled()) return;
        if (!writer || encoded == 0) throw std::runtime_error("Hardware encoder produced no video");
        [writer endSessionAtSourceTime:CMTimeMake(nextFrame * options.fpsDenominator, options.fpsNumerator)];
        [videoInput markAsFinished]; FinishWriter(writer);
        VTCompressionSessionInvalidate(compression); CFRelease(compression); compression = nullptr;
        MuxAudio();
        if (!cancelled()) state = 2;
        previous.reset();
    }
    void FinishWhenGpuDrained() {
        if (requests || gpuPending) {
            auto self = shared_from_this();
            workerPending++;
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_MSEC), worker, ^{ @autoreleasepool {
                try { self->FinishWhenGpuDrained(); } catch (const std::exception& e) { self->Fail(e.what()); }
                self->workerPending--;
            }});
            return;
        }
        if (!cancelled()) Finish();
        else {
            if (compression) {
                VTCompressionSessionCompleteFrames(compression, kCMTimeInvalid);
                VTCompressionSessionInvalidate(compression); CFRelease(compression); compression = nullptr;
            }
            dispatch_sync(muxer, ^{});
            if (writer.status == AVAssetWriterStatusWriting) [writer cancelWriting];
            previous.reset();
        }
    }
    void Copy(id<MTLCommandBuffer> commands, id<MTLTexture> source, std::shared_ptr<Slot> slot, int64_t sample) {
        if (source.width != (NSUInteger)options.width || source.height != (NSUInteger)options.height ||
            source.pixelFormat != MTLPixelFormatBGRA8Unorm || source.device != slot->texture.device)
            throw std::runtime_error("Metal source must be matching-size BGRA8 on Unity's device");
        id<MTLBlitCommandEncoder> blit = [commands blitCommandEncoder];
        [blit copyFromTexture:source sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0,0,0)
            sourceSize:MTLSizeMake(options.width, options.height,1) toTexture:slot->texture destinationSlice:0
            destinationLevel:0 destinationOrigin:MTLOriginMake(0,0,0)];
        [blit endEncoding];
        auto self = shared_from_this(); gpuPending++;
        [commands addCompletedHandler:^(id<MTLCommandBuffer> completed) {
            if (completed.status == MTLCommandBufferStatusError) self->Fail(ErrorText(completed.error));
            else self->Schedule(^{ self->Ready(slot, sample); });
            self->gpuPending--;
        }];
    }
    ~Session() {
        if (compression) { VTCompressionSessionInvalidate(compression); CFRelease(compression); }
        if (cache) CFRelease(cache);
    }
};
static void Encoded(void*, void* frameRef, OSStatus result, VTEncodeInfoFlags flags, CMSampleBufferRef sample) {
    std::unique_ptr<EncodeContext> context((EncodeContext*)frameRef);
    auto self = context->session; auto slot = context->slot;
    if (result != noErr || !sample || (flags & kVTEncodeInfo_FrameDropped)) {
        self->Fail("VideoToolbox failed or dropped a frame: " + std::to_string(result));
        self->inFlight--; self->flightChanged.notify_all(); return;
    }
    CFRetain(sample);
    dispatch_async(self->muxer, ^{ @autoreleasepool {
        try { self->Append(sample); } catch (const std::exception& e) { self->Fail(e.what()); }
        CFRelease(sample);
        // Keep the IOSurface lease through both encoding and writing.
        (void)slot;
        self->inFlight--; self->flightChanged.notify_all();
    }});
}
struct Request { std::shared_ptr<Session> session; std::shared_ptr<Slot> slot; id<MTLTexture> source; int64_t sample; };
std::mutex registryMutex;
std::unordered_map<uint64_t,std::shared_ptr<Session>> sessions;
std::unordered_map<uint64_t,Request> requests;
uint64_t nextId = 1;
static std::shared_ptr<Session> Find(uint64_t handle) {
    std::lock_guard<std::mutex> l(registryMutex); auto it = sessions.find(handle); return it == sessions.end() ? nullptr : it->second;
}
static void UNITY_INTERFACE_API RenderEvent(int, void* data) {
    @autoreleasepool {
        Request request;
        { std::lock_guard<std::mutex> l(registryMutex);
          auto it = requests.find((uint64_t)data); if (it == requests.end()) return;
          request = std::move(it->second); requests.erase(it); }
        auto self = request.session;
        try {
            if (!self->cancelled()) {
                if (!unityMetal) throw std::runtime_error("Unity Metal plugin interface is unavailable");
                unityMetal->EndCurrentCommandEncoder();
                auto commands = unityMetal->CurrentCommandBuffer();
                if (!commands) throw std::runtime_error("Unity has no current Metal command buffer");
                self->Copy(commands, request.source, request.slot, request.sample);
            }
        } catch (const std::exception& e) { self->Fail(e.what()); }
        self->requests--;
    }
}
} // namespace

MC_EXPORT uint64_t mcmetal_create(void* source, const Options* options, char* error, int capacity) {
    @autoreleasepool { try {
        if (!source || !options || options->abi != 1 || !options->videoPath || options->width < 2 || options->height < 2 ||
            (options->width & 1) || (options->height & 1) || options->fpsNumerator <= 0 || options->fpsDenominator <= 0 ||
            options->sampleRate <= 0 || options->poolSize < 4 || options->poolSize > 32 || options->maxLagMs < 100)
            throw std::runtime_error("Invalid Metal recording options");
        auto s = std::make_shared<Session>(); s->Init(((__bridge id<MTLTexture>)source).device, *options);
        std::lock_guard<std::mutex> l(registryMutex); uint64_t id = nextId++; sessions[id] = s; return id;
    } catch (const std::exception& e) { CopyError(e.what(), error, capacity); return 0; }}
}
MC_EXPORT void* mcmetal_queue_frame(uint64_t handle, void* source, int64_t sample) {
    auto s = Find(handle); if (!s || s->state != 0 || !source || sample < 0) return nullptr;
    auto slot = s->Acquire(); if (!slot) return nullptr;
    std::lock_guard<std::mutex> l(registryMutex);
    if (s->state != 0) return nullptr;
    uint64_t requestId = nextId++; s->requests++; requests.emplace(requestId, Request{s,slot,(__bridge id<MTLTexture>)source,sample});
    return (void*)requestId;
}
MC_EXPORT void* mcmetal_get_render_callback() { return (void*)&RenderEvent; }
MC_EXPORT int mcmetal_status(uint64_t handle, Status* status, char* error, int capacity) {
    auto s = Find(handle); if (!s || !status) return 0;
    *status = {s->state, s->requests + s->gpuPending + s->inFlight, s->captured, s->encoded, s->duplicated, s->dropped,
        (int64_t)s->options.width*s->options.height*4*s->options.poolSize, s->lagFrames()};
    CopyError(s->Error(), error, capacity); return 1;
}
MC_EXPORT int mcmetal_hardware(uint64_t handle) { auto s = Find(handle); return s && s->hardware ? 1 : 0; }
MC_EXPORT int mcmetal_stop(uint64_t handle, int64_t samples, const char* audio, const char* output) {
    auto s = Find(handle); if (!s || !audio || !output || samples <= 0) return 0;
    int expected = 0; if (!s->state.compare_exchange_strong(expected,1)) return 0;
    s->finalSamples = samples; s->audioPath = audio; s->outputPath = output;
    s->Schedule(^{ s->FinishWhenGpuDrained(); }); return 1;
}
MC_EXPORT void mcmetal_abort(uint64_t handle) {
    auto s = Find(handle); if (!s || s->state == 2 || s->state == 4) return;
    s->state = 4; s->flightChanged.notify_all();
    { std::lock_guard<std::mutex> l(registryMutex);
      for (auto it = requests.begin(); it != requests.end();) {
          if (it->second.session == s) { s->requests--; it = requests.erase(it); } else ++it;
      }}
    s->Schedule(^{ s->FinishWhenGpuDrained(); });
}
MC_EXPORT int mcmetal_destroy(uint64_t handle) {
    auto s = Find(handle); if (!s) return 1;
    if (s->state < 2 || s->requests || s->gpuPending || s->workerPending || s->inFlight) return 0;
    std::lock_guard<std::mutex> l(registryMutex); sessions.erase(handle); return 1;
}
MC_EXPORT void mcmetal_shutdown() {
    std::vector<uint64_t> ids;
    { std::lock_guard<std::mutex> l(registryMutex); for (auto& p : sessions) ids.push_back(p.first); }
    for (auto id : ids) mcmetal_abort(id);
}
MC_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) { unityMetal = interfaces->Get<IUnityGraphicsMetalV1>(); }
MC_EXPORT void UNITY_INTERFACE_API UnityPluginUnload() {
    mcmetal_shutdown();
    // Unity unloads after draining its render thread. Drain our asynchronous native work as well.
    for (;;) {
        std::vector<uint64_t> ids;
        { std::lock_guard<std::mutex> l(registryMutex); for (auto& p : sessions) ids.push_back(p.first); }
        if (ids.empty()) break;
        for (auto id : ids) mcmetal_destroy(id);
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    unityMetal = nullptr;
}
