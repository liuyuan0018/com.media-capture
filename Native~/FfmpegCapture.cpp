#define MC_EXPORTS
#include "FfmpegCapture.h"
#include <d3d11.h>
#include <d3d10.h>
#include <wrl/client.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/opt.h>
}
using Microsoft::WRL::ComPtr;

void McMuxAudio(const std::string& video, const std::string& audio, const std::string& output,
    const std::atomic<bool>& cancelled);

namespace
{
void Check(int result, const char* action)
{
    if (result >= 0) return;
    char message[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(result, message, sizeof(message));
    throw std::runtime_error(std::string(action) + ": " + message);
}
void CheckHr(HRESULT result, const char* action)
{
    if (FAILED(result)) throw std::runtime_error(std::string(action) + " HRESULT=" + std::to_string(result));
}
void CopyText(const std::string& text, char* output, int capacity)
{
    if (!output || capacity <= 0) return;
    size_t length = std::min(text.size(), static_cast<size_t>(capacity - 1));
    memcpy(output, text.data(), length);
    output[length] = 0;
}

struct Slot
{
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11Query> completion;
    std::atomic<bool> busy{false};
    int64_t sample = 0;
};
using SlotRef = std::shared_ptr<Slot>;
struct FrameLease
{
    SlotRef slot;
    explicit FrameLease(SlotRef value) : slot(std::move(value)) {}
    ~FrameLease() { slot->busy = false; }
};
using LeaseRef = std::shared_ptr<FrameLease>;

struct DeviceProtection
{
    ComPtr<ID3D10Multithread> multithread;
    BOOL wasProtected;
    explicit DeviceProtection(ID3D11DeviceContext* context)
    {
        CheckHr(context->QueryInterface(IID_PPV_ARGS(&multithread)), "Get D3D11 multithread interface");
        wasProtected = multithread->SetMultithreadProtected(TRUE);
    }
    ~DeviceProtection() { multithread->SetMultithreadProtected(wasProtected); }
};
std::mutex deviceProtectionMutex;
std::unordered_map<ID3D11Device*, std::weak_ptr<DeviceProtection>> deviceProtections;
std::shared_ptr<DeviceProtection> ProtectDevice(ID3D11Device* device, ID3D11DeviceContext* context)
{
    std::lock_guard<std::mutex> lock(deviceProtectionMutex);
    auto existing = deviceProtections[device].lock();
    if (existing) return existing;
    auto protection = std::make_shared<DeviceProtection>(context);
    deviceProtections[device] = protection;
    return protection;
}

class Session
{
public:
    McOptions options{};
    std::string videoPath;
    std::string audioPath;
    std::string outputPath;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    std::shared_ptr<DeviceProtection> deviceProtection;
    std::vector<SlotRef> slots;
    std::deque<SlotRef> pendingGpu; // Rendering-thread only.
    std::deque<LeaseRef> ready;
    std::mutex mutex;
    std::condition_variable signal;
    std::thread worker;
    std::atomic<int> state{0};
    std::atomic<int> outstandingEvents{0};
    std::atomic<int> gpuPending{0};
    std::atomic<bool> cancelled{false};
    std::atomic<bool> workerDone{false};
    std::atomic<int64_t> captured{0}, encoded{0}, duplicated{0}, dropped{0}, maxLag{0};
    int64_t durationSamples = 0;
    int64_t lastSubmittedSample = -1;
    std::string error;
    AVBufferRef* hardwareDevice = nullptr;
    AVBufferRef* hardwareFrames = nullptr;
    AVCodecContext* codec = nullptr;
    AVFormatContext* format = nullptr;
    AVStream* stream = nullptr;
    AVPacket* packet = nullptr;

    Session(ID3D11Texture2D* source, const McOptions& input) : options(input), videoPath(input.videoPath)
    {
        source->GetDevice(&device);
        device->GetImmediateContext(&context);
        if (device->GetCreationFlags() & D3D11_CREATE_DEVICE_SINGLETHREADED)
            throw std::runtime_error("The D3D11 device does not allow a hardware encoding worker.");
        deviceProtection = ProtectDevice(device.Get(), context.Get());
        D3D11_TEXTURE2D_DESC desc{};
        source->GetDesc(&desc);
        if (desc.Width != static_cast<UINT>(options.width) || desc.Height != static_cast<UINT>(options.height) ||
            desc.SampleDesc.Count != 1 || desc.MipLevels != 1 || desc.ArraySize != 1)
            throw std::runtime_error("Expected a fixed-size, single-sample D3D11 texture.");
        AVPixelFormat pixelFormat;
        switch (desc.Format)
        {
        case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        case DXGI_FORMAT_B8G8R8A8_TYPELESS:
            pixelFormat = AV_PIX_FMT_BGRA;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            break;
        default: throw std::runtime_error("The native encoder requires a BGRA8 D3D11 input texture.");
        }
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = 0;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        desc.MiscFlags = 0;
        for (int i = 0; i < options.poolSize; ++i)
        {
            auto slot = std::make_shared<Slot>();
            CheckHr(device->CreateTexture2D(&desc, nullptr, &slot->texture), "Create recording texture");
            D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0};
            CheckHr(device->CreateQuery(&query, &slot->completion), "Create GPU completion query");
            slots.push_back(slot);
        }
        try { OpenEncoder(pixelFormat); }
        catch (...) { CloseEncoder(); throw; }
    }

    ~Session()
    {
        cancelled = true;
        signal.notify_all();
        if (worker.joinable()) worker.join();
        CloseEncoder();
    }

    void Fail(std::string_view message) noexcept
    {
        state = 3;
        cancelled = true;
        try
        {
            std::lock_guard<std::mutex> lock(mutex);
            if (error.empty()) error.assign(message.data(), message.size());
        }
        catch (...) {}
        signal.notify_all();
    }

    void OpenEncoder(AVPixelFormat pixelFormat)
    {
        // Reuse Unity's D3D11 device. No software frame or GPU-to-CPU transfer is created.
        hardwareDevice = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
        if (!hardwareDevice) throw std::bad_alloc();
        auto* deviceContext = reinterpret_cast<AVD3D11VADeviceContext*>(
            reinterpret_cast<AVHWDeviceContext*>(hardwareDevice->data)->hwctx);
        deviceContext->device = device.Get();
        deviceContext->device->AddRef();
        Check(av_hwdevice_ctx_init(hardwareDevice), "Initialize D3D11 FFmpeg device");
        hardwareFrames = av_hwframe_ctx_alloc(hardwareDevice);
        if (!hardwareFrames) throw std::bad_alloc();
        auto* frames = reinterpret_cast<AVHWFramesContext*>(hardwareFrames->data);
        frames->format = AV_PIX_FMT_D3D11;
        frames->sw_format = pixelFormat;
        frames->width = options.width;
        frames->height = options.height;
        frames->initial_pool_size = 0;
        Check(av_hwframe_ctx_init(hardwareFrames), "Initialize D3D11 FFmpeg frames");
        const AVCodec* encoder = avcodec_find_encoder_by_name("h264_nvenc");
        if (!encoder) throw std::runtime_error("This FFmpeg build has no h264_nvenc encoder.");
        codec = avcodec_alloc_context3(encoder);
        if (!codec) throw std::bad_alloc();
        codec->width = options.width;
        codec->height = options.height;
        codec->pix_fmt = AV_PIX_FMT_D3D11;
        codec->time_base = AVRational{options.fpsDenominator, options.fpsNumerator};
        codec->framerate = AVRational{options.fpsNumerator, options.fpsDenominator};
        codec->hw_frames_ctx = av_buffer_ref(hardwareFrames);
        codec->gop_size = std::max(1, options.fpsNumerator * 2 / options.fpsDenominator);
        codec->max_b_frames = 0;
        codec->bit_rate = 0;
        codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
        codec->color_range = AVCOL_RANGE_MPEG;
        codec->colorspace = AVCOL_SPC_BT709;
        codec->color_primaries = AVCOL_PRI_BT709;
        codec->color_trc = AVCOL_TRC_IEC61966_2_1;
        Check(av_opt_set(codec->priv_data, "preset", "p4", 0), "Set NVENC preset");
        Check(av_opt_set(codec->priv_data, "rc", "vbr", 0), "Set NVENC rate control");
        Check(av_opt_set_int(codec->priv_data, "cq", options.quality, 0), "Set NVENC quality");
        Check(av_opt_set_int(codec->priv_data, "rc-lookahead", 0, 0), "Disable lookahead");
        Check(av_opt_set_int(codec->priv_data, "zerolatency", 1, 0), "Set NVENC zero reordering");
        Check(av_opt_set_int(codec->priv_data, "delay", 0, 0), "Set NVENC output delay");
        Check(avcodec_open2(codec, encoder, nullptr), "Open NVENC hardware encoder");
        Check(avformat_alloc_output_context2(&format, nullptr, "mp4", videoPath.c_str()), "Create video MP4");
        stream = avformat_new_stream(format, nullptr);
        if (!stream) throw std::bad_alloc();
        stream->time_base = codec->time_base;
        Check(avcodec_parameters_from_context(stream->codecpar, codec), "Copy video stream parameters");
        Check(avio_open(&format->pb, videoPath.c_str(), AVIO_FLAG_WRITE), "Open video file");
        AVDictionary* flags = nullptr;
        av_dict_set(&flags, "movflags", "frag_keyframe+empty_moov+default_base_moof", 0);
        int result = avformat_write_header(format, &flags);
        av_dict_free(&flags);
        Check(result, "Write video header");
        packet = av_packet_alloc();
        if (!packet) throw std::bad_alloc();
    }

    void CloseEncoder()
    {
        av_packet_free(&packet);
        avcodec_free_context(&codec);
        if (format) { avio_closep(&format->pb); avformat_free_context(format); format = nullptr; }
        av_buffer_unref(&hardwareFrames);
        av_buffer_unref(&hardwareDevice);
    }

    void ReadPackets()
    {
        while (true)
        {
            int result = avcodec_receive_packet(codec, packet);
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return;
            Check(result, "Receive H.264 packet");
            av_packet_rescale_ts(packet, codec->time_base, stream->time_base);
            packet->stream_index = stream->index;
            if (packet->duration <= 0)
                packet->duration = av_rescale_q(1, codec->time_base, stream->time_base);
            result = av_interleaved_write_frame(format, packet);
            av_packet_unref(packet);
            Check(result, "Write H.264 packet");
        }
    }

    void Encode(const LeaseRef& lease)
    {
        if (cancelled) throw std::runtime_error("Recording cancelled.");
        AVFrame* frame = av_frame_alloc();
        if (!frame) throw std::bad_alloc();
        auto releaseFrame = std::unique_ptr<AVFrame, void(*)(AVFrame*)>(frame, [](AVFrame* p){ av_frame_free(&p); });
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
        if (!frame->buf[0]) { delete owner; throw std::bad_alloc(); }
        int result = avcodec_send_frame(codec, frame);
        if (result == AVERROR(EAGAIN)) { ReadPackets(); result = avcodec_send_frame(codec, frame); }
        Check(result, "Submit D3D11 hardware frame");
        ReadPackets();
        ++encoded;
    }

    int64_t TargetSample(int64_t frame) const
    {
        return av_rescale_q(frame, AVRational{options.fpsDenominator, options.fpsNumerator},
            AVRational{1, options.sampleRate});
    }

    void Run()
    {
        try
        {
            LeaseRef previous;
            bool previousUsed = false;
            while (!cancelled)
            {
                LeaseRef current;
                {
                    std::unique_lock<std::mutex> lock(mutex);
                    signal.wait_for(lock, std::chrono::milliseconds(20), [&] {
                        return cancelled || !ready.empty() ||
                            (state == 1 && outstandingEvents == 0 && gpuPending == 0);
                    });
                    if (cancelled) break;
                    if (!ready.empty()) { current = std::move(ready.front()); ready.pop_front(); }
                    else if (state == 1 && outstandingEvents == 0 && gpuPending == 0) break;
                    else continue;
                }
                if (!previous) previous = current;
                int64_t lag = std::max<int64_t>(0, av_rescale_q(current->slot->sample,
                    AVRational{1, options.sampleRate}, codec->time_base) - encoded.load());
                maxLag = std::max(maxLag.load(), lag);
                if (lag * 1000 * options.fpsDenominator > static_cast<int64_t>(options.maxLagMilliseconds) * options.fpsNumerator)
                    throw std::runtime_error("Hardware encoding fell behind the recording clock.");
                while (TargetSample(encoded) < current->slot->sample)
                {
                    Encode(previous);
                    if (previousUsed) ++duplicated;
                    previousUsed = true;
                }
                if (previous != current) { previous = current; previousUsed = false; }
            }
            if (cancelled) { if (state != 3) state = 4; }
            else
            {
                if (!previous) throw std::runtime_error("No video frames were captured.");
                int64_t count = av_rescale_q_rnd(durationSamples, AVRational{1, options.sampleRate},
                    codec->time_base, AV_ROUND_UP);
                while (encoded < std::max<int64_t>(1, count))
                {
                    Encode(previous);
                    if (previousUsed) ++duplicated;
                    previousUsed = true;
                }
                previous.reset();
                Check(avcodec_send_frame(codec, nullptr), "Drain video encoder");
                ReadPackets();
                Check(av_write_trailer(format), "Finish video MP4");
                CloseEncoder();
                if (!audioPath.empty()) McMuxAudio(videoPath, audioPath, outputPath, cancelled);
                state = cancelled ? 4 : 2;
            }
        }
        catch (const std::exception& exception) { if (!cancelled) Fail(exception.what()); }
        catch (...) { Fail("Unhandled native encoding failure."); }
        CloseEncoder();
        { std::lock_guard<std::mutex> lock(mutex); ready.clear(); }
        workerDone = true;
    }

    void PollGpu()
    {
        while (!pendingGpu.empty())
        {
            auto slot = pendingGpu.front();
            HRESULT result = context->GetData(slot->completion.Get(), nullptr, 0, D3D11_ASYNC_GETDATA_DONOTFLUSH);
            if (result == S_FALSE) break;
            CheckHr(result, "Poll recording GPU copy");
            pendingGpu.pop_front();
            auto lease = std::make_shared<FrameLease>(slot);
            { std::lock_guard<std::mutex> lock(mutex); if (!cancelled) ready.push_back(lease); }
            --gpuPending;
            signal.notify_one();
        }
    }

    void Submit(ID3D11Texture2D* source, int64_t sample)
    {
        PollGpu();
        if (cancelled || state > 1) return;
        ComPtr<ID3D11Device> sourceDevice;
        source->GetDevice(&sourceDevice);
        D3D11_TEXTURE2D_DESC sourceDesc{}, targetDesc{};
        source->GetDesc(&sourceDesc);
        slots.front()->texture->GetDesc(&targetDesc);
        if (sourceDevice.Get() != device.Get() || sourceDesc.Width != targetDesc.Width ||
            sourceDesc.Height != targetDesc.Height || sourceDesc.SampleDesc.Count != 1 ||
            sourceDesc.MipLevels != 1 || sourceDesc.ArraySize != 1 ||
            (sourceDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM && sourceDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB &&
                sourceDesc.Format != DXGI_FORMAT_B8G8R8A8_TYPELESS))
            throw std::runtime_error("Recording source device or texture dimensions changed.");
        if (sample < lastSubmittedSample) throw std::runtime_error("Capture timestamps are out of order.");
        lastSubmittedSample = sample;
        for (const auto& slot : slots)
        {
            bool free = false;
            if (!slot->busy.compare_exchange_strong(free, true)) continue;
            slot->sample = sample;
            context->CopyResource(slot->texture.Get(), source);
            context->End(slot->completion.Get());
            pendingGpu.push_back(slot);
            ++gpuPending;
            ++captured;
            return;
        }
        ++dropped;
    }
};

std::recursive_mutex apiMutex;
std::mutex registryMutex;
std::unordered_map<uint64_t, std::shared_ptr<Session>> sessions;
std::atomic<uint64_t> nextHandle{1};
std::shared_ptr<Session> Get(uint64_t handle)
{
    std::lock_guard<std::mutex> lock(registryMutex);
    auto found = sessions.find(handle);
    return found == sessions.end() ? nullptr : found->second;
}
struct Request
{
    std::shared_ptr<Session> session;
    ComPtr<ID3D11Texture2D> source;
    int64_t sample;
};
std::unordered_map<uintptr_t, std::unique_ptr<Request>> requests;
uintptr_t nextRequest = 1;
void __stdcall RenderEvent(int eventId, void* data)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    if (eventId == 1) { mc_poll_render(reinterpret_cast<uintptr_t>(data)); return; }
    std::unique_ptr<Request> request;
    {
        std::lock_guard<std::mutex> lock(registryMutex);
        auto found = requests.find(reinterpret_cast<uintptr_t>(data));
        if (found == requests.end()) return;
        request = std::move(found->second);
        requests.erase(found);
    }
    try { request->session->Submit(request->source.Get(), request->sample); }
    catch (const std::exception& exception) { request->session->Fail(exception.what()); }
    --request->session->outstandingEvents;
    request->session->signal.notify_one();
}
catch (...) {}
}

MC_API uint64_t __cdecl mc_create(void* source, const McOptions* options, char* error, int capacity)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    try
    {
        if (!source || !options || options->abiVersion != 1 || !options->videoPath ||
            options->width < 2 || options->height < 2 || options->width % 2 || options->height % 2 ||
            options->fpsNumerator < 1 || options->fpsNumerator > 240000 || options->fpsDenominator < 1 ||
            options->sampleRate < 1 || options->poolSize < 4 || options->poolSize > 32 ||
            options->quality < 0 || options->quality > 51 || options->maxLagMilliseconds < 100)
            throw std::runtime_error("Invalid native recording options.");
        auto session = std::make_shared<Session>(static_cast<ID3D11Texture2D*>(source), *options);
        uint64_t handle = nextHandle++;
        { std::lock_guard<std::mutex> lock(registryMutex); sessions.emplace(handle, session); }
        try { session->worker = std::thread([pointer = session.get()] { pointer->Run(); }); }
        catch (...) { std::lock_guard<std::mutex> lock(registryMutex); sessions.erase(handle); throw; }
        return handle;
    }
    catch (const std::exception& exception) { CopyText(exception.what(), error, capacity); return 0; }
}
catch (...) { return 0; }
MC_API void* __cdecl mc_queue_frame(uint64_t handle, void* source, int64_t sample)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    auto session = Get(handle);
    if (!session || !source || session->state != 0 || sample < 0) return nullptr;
    if (++session->outstandingEvents > session->options.poolSize)
    { --session->outstandingEvents; ++session->dropped; return nullptr; }
    try
    {
        auto request = std::make_unique<Request>(Request{session, static_cast<ID3D11Texture2D*>(source), sample});
        std::lock_guard<std::mutex> lock(registryMutex);
        uintptr_t id = nextRequest++;
        requests.emplace(id, std::move(request));
        return reinterpret_cast<void*>(id);
    }
    catch (...) { --session->outstandingEvents; session->Fail("Cannot allocate render request."); return nullptr; }
}
catch (...) { return nullptr; }
MC_API void* __cdecl mc_get_render_callback() { return reinterpret_cast<void*>(&RenderEvent); }
MC_API void __cdecl mc_poll_render(uint64_t handle)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    auto session = Get(handle);
    if (!session) return;
    try { session->PollGpu(); }
    catch (const std::exception& exception) { session->Fail(exception.what()); }
}
catch (...) { return; }
MC_API int __cdecl mc_stop(uint64_t handle, int64_t duration, const char* audio, const char* output)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    auto session = Get(handle);
    if (!session || duration <= 0) return 0;
    std::lock_guard<std::mutex> lock(session->mutex);
    if (session->state != 0) return session->state == 1 ? 1 : 0;
    session->durationSamples = duration;
    session->audioPath = audio ? audio : "";
    session->outputPath = output ? output : "";
    session->state = 1;
    session->signal.notify_one();
    return 1;
}
catch (...) { return 0; }
MC_API int __cdecl mc_status(uint64_t handle, McStatus* status, char* error, int capacity)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    auto session = Get(handle);
    if (!session || !status) return 0;
    std::lock_guard<std::mutex> lock(session->mutex);
    *status = McStatus{session->state, static_cast<int>(session->ready.size()) + session->gpuPending + session->outstandingEvents,
        session->captured, session->encoded, session->duplicated, session->dropped,
        static_cast<int64_t>(session->options.width) * session->options.height * 4 * session->options.poolSize,
        session->maxLag};
    CopyText(session->error, error, capacity);
    return 1;
}
catch (...) { return 0; }
MC_API void __cdecl mc_abort(uint64_t handle)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    auto session = Get(handle);
    if (session)
    {
        std::lock_guard<std::mutex> lock(session->mutex);
        if (session->state >= 2) return;
        session->cancelled = true;
        session->state = 4;
        session->signal.notify_all();
    }
}
catch (...) { return; }
MC_API int __cdecl mc_destroy(uint64_t handle)
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    auto session = Get(handle);
    if (!session) return 1;
    if (!session->workerDone) return 0;
    if (!session->cancelled && (session->outstandingEvents != 0 || session->gpuPending != 0)) return 0;
    if (session->worker.joinable()) session->worker.join();
    std::lock_guard<std::mutex> lock(registryMutex);
    for (auto iterator = requests.begin(); iterator != requests.end();)
    {
        if (iterator->second->session == session)
        { --session->outstandingEvents; iterator = requests.erase(iterator); }
        else ++iterator;
    }
    sessions.erase(handle);
    return 1;
}
catch (...) { return 0; }
MC_API const char* __cdecl mc_version() { return av_version_info(); }

MC_API void __cdecl mc_shutdown()
try
{
    std::lock_guard<std::recursive_mutex> apiLock(apiMutex);
    std::vector<std::shared_ptr<Session>> active;
    {
        std::lock_guard<std::mutex> lock(registryMutex);
        for (auto& entry : sessions) active.push_back(entry.second);
    }
    for (auto& session : active)
    {
        session->cancelled = true;
        session->signal.notify_all();
    }
    for (auto& session : active) if (session->worker.joinable()) session->worker.join();
    {
        std::lock_guard<std::mutex> lock(registryMutex);
        requests.clear();
        sessions.clear();
    }
}
catch (...) { return; }

extern "C" __declspec(dllexport) void __stdcall UnityPluginLoad(void*) {}
extern "C" __declspec(dllexport) void __stdcall UnityPluginUnload() { mc_shutdown(); }
