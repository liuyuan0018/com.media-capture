#include "FfmpegCapture.h"
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <string>
#include <thread>
using Microsoft::WRL::ComPtr;

static void Check(HRESULT result) { if (FAILED(result)) throw std::runtime_error("D3D11 failure: " + std::to_string(result)); }
static void WriteTone(const std::string& path, int samples)
{
    std::ofstream file(path, std::ios::binary);
    auto u16 = [&](uint16_t n) { file.write(reinterpret_cast<const char*>(&n), 2); };
    auto u32 = [&](uint32_t n) { file.write(reinterpret_cast<const char*>(&n), 4); };
    file.write("RIFF", 4); u32(36 + samples * 4); file.write("WAVEfmt ", 8); u32(16);
    u16(1); u16(2); u32(48000); u32(192000); u16(4); u16(16); file.write("data", 4); u32(samples * 4);
    for (int i = 0; i < samples; ++i)
    {
        int16_t sample = static_cast<int16_t>(std::sin(i * 6.283185307179586 * 440 / 48000) * 8000);
        u16(static_cast<uint16_t>(sample)); u16(static_cast<uint16_t>(sample));
    }
    if (!file) throw std::runtime_error("Cannot write tone WAV.");
}

int main(int argc, char** argv)
{
    fprintf(stderr, "Starting D3D11 check\n"); uint64_t handle = 0;
    try
    {
        const std::string base = argc > 1 ? argv[1] : "capture-check";
        const bool mixedRun = argc > 2 && std::string(argv[2]) == "mixed";
        const bool abortRun = argc > 2 && std::string(argv[2]) == "abort";
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT i = 0;; ++i)
        {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory->EnumAdapters1(i, &candidate) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 info{}; Check(candidate->GetDesc1(&info));
            if (info.VendorId == 0x10de) { adapter = candidate; break; }
        }
        if (!adapter) throw std::runtime_error("No NVIDIA adapter available for NVENC validation.");
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        fprintf(stderr, "Creating D3D11 device\n"); Check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &device, nullptr, &context));
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = 1280; desc.Height = 720; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<ID3D11RenderTargetView> view;
        Check(device->CreateTexture2D(&desc, nullptr, &texture));
        Check(device->CreateRenderTargetView(texture.Get(), nullptr, &view));
        std::string video = base + ".video.mp4", audio = base + ".wav", output = base + ".mp4";
        McOptions options{1, 1280, 720, 30, 1, 48000, 8, 20, 2000, video.c_str()};
        char error[2048]{};
        McOptions invalid = options; invalid.width = 1;
        if (mc_create(texture.Get(), &invalid, error, sizeof(error))) throw std::runtime_error("Invalid options accepted.");
        fprintf(stderr, "Creating native session\n"); handle = mc_create(texture.Get(), &options, error, sizeof(error));
        if (!handle) throw std::runtime_error(error); fprintf(stderr, "Session ready\n");
        using RenderCallback = void(__stdcall*)(int, void*);
        auto callback = reinterpret_cast<RenderCallback>(mc_get_render_callback());
        const int count = abortRun ? 4 : 90;
        const auto start = std::chrono::steady_clock::now();
        for (int i = 0; i < count; ++i)
        {
            const float color[4]{mixedRun ? 163.0f / 255 : i < 30 ? 1.0f : 0.0f, mixedRun ? 232.0f / 255 : i >= 30 && i < 60 ? 1.0f : 0.0f, mixedRun ? 129.0f / 255 : i >= 60 ? 1.0f : 0.0f, 1.0f};
            context->ClearRenderTargetView(view.Get(), color);
            auto request = mc_queue_frame(handle, texture.Get(), static_cast<int64_t>(i) * 1600);
            if (request) callback(0, request);
            context->Flush();
            mc_poll_render(handle);
            std::this_thread::sleep_until(start + std::chrono::milliseconds((i + 1) * 1000 / 30));
        }
        void* abandonedRequest = nullptr;
        if (abortRun)
        {
            abandonedRequest = mc_queue_frame(handle, texture.Get(), static_cast<int64_t>(count) * 1600);
            mc_abort(handle);
        }
        else
        {
            WriteTone(audio, count * 1600);
            if (!mc_stop(handle, count * 1600, audio.c_str(), output.c_str())) throw std::runtime_error("Stop rejected.");
        }
        McStatus status{};
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(20);
        while (true)
        {
            context->Flush(); mc_poll_render(handle);
            if (!mc_status(handle, &status, error, sizeof(error))) throw std::runtime_error("Status unavailable.");
            if (status.state >= 2 && mc_destroy(handle)) { handle = 0; break; }
            if (std::chrono::steady_clock::now() >= deadline) throw std::runtime_error("Native capture did not finish in 20 seconds.");
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
        printf("state=%d captured=%lld encoded=%lld duplicated=%lld dropped=%lld gpuBytes=%lld maxLagFrames=%lld error=%s\n",
            status.state, status.captured, status.encoded, status.duplicated, status.dropped, status.gpuBytes, status.maxLagFrames, error);
        if (abandonedRequest) callback(0, abandonedRequest);
        if (status.state != (abortRun ? 4 : 2)) throw std::runtime_error(error);
        if (!abortRun && (status.encoded != count || status.dropped != 0 || status.duplicated != 0))
            throw std::runtime_error("Unexpected frame accounting.");
        return 0;
    }
    catch (const std::exception& exception)
    {
        fprintf(stderr, "%s\n", exception.what());
        if (handle) mc_abort(handle);
        // Avoid global DLL destruction while a failed device/driver may still own work.
        ExitProcess(1);
    }
}
