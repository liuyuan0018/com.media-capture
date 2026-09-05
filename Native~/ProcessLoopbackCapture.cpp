#include "ProcessLoopbackCapture.h"

#include <AudioClient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <propvarutil.h>

#include <algorithm>
#include <atomic>
#include <iomanip>
#include <limits>
#include <mutex>

#include <wrl/implements.h>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::FtmBase;
using Microsoft::WRL::Make;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;
using Microsoft::WRL::ClassicCom;

namespace
{
    constexpr WORD OUTPUT_CHANNELS = 2;
    constexpr DWORD OUTPUT_SAMPLE_RATE = 48000;
    constexpr WORD OUTPUT_BITS_PER_SAMPLE = 16;
    constexpr DWORD BYTES_PER_FRAME = OUTPUT_CHANNELS * OUTPUT_BITS_PER_SAMPLE / 8;

    class ActivationHandler final :
        public RuntimeClass<
            RuntimeClassFlags<ClassicCom>,
            FtmBase,
            IActivateAudioInterfaceCompletionHandler>
    {
    public:
        ActivationHandler()
        {
            completedEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (completedEvent == nullptr)
            {
                ThrowLastError("CreateEventW");
            }
        }

        ~ActivationHandler()
        {
            CloseHandle(completedEvent);
        }

        STDMETHODIMP ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override
        {
            HRESULT activationResult = E_UNEXPECTED;
            ComPtr<IUnknown> activated;
            HRESULT operationResult = operation->GetActivateResult(&activationResult, &activated);
            if (SUCCEEDED(operationResult))
            {
                operationResult = activationResult;
            }
            if (SUCCEEDED(operationResult))
            {
                operationResult = activated.As(&audioClient);
            }
            result = operationResult;
            SetEvent(completedEvent);
            return S_OK;
        }

        ComPtr<IAudioClient> Wait()
        {
            if (WaitForSingleObject(completedEvent, 10000) != WAIT_OBJECT_0)
            {
                throw std::runtime_error("Timed out while activating WASAPI process loopback.");
            }
            ThrowIfFailed(result, "ActivateAudioInterfaceAsync");
            return audioClient;
        }

    private:
        HANDLE completedEvent = nullptr;
        HRESULT result = E_UNEXPECTED;
        ComPtr<IAudioClient> audioClient;
    };

    class WaveWriter
    {
    public:
        explicit WaveWriter(const std::filesystem::path& path)
        {
            file = CreateFileW(
                path.c_str(),
                GENERIC_WRITE | GENERIC_READ,
                FILE_SHARE_READ,
                nullptr,
                CREATE_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                ThrowLastError("CreateFileW(audio.wav)");
            }
            WriteHeader(0);
        }

        ~WaveWriter()
        {
            if (file != INVALID_HANDLE_VALUE)
            {
                CloseHandle(file);
            }
        }

        void Write(const BYTE* data, UINT32 frames, bool silent)
        {
            if (frames == 0)
            {
                return;
            }
            if (writtenFrames + frames > (std::numeric_limits<DWORD>::max() - 36ULL) / BYTES_PER_FRAME)
                throw std::runtime_error("Captured WAV exceeds the RIFF 4 GiB limit.");
            const DWORD byteCount = frames * BYTES_PER_FRAME;
            if (silent)
            {
                std::vector<BYTE> zeros(byteCount, 0);
                WriteBytes(zeros.data(), byteCount);
            }
            else
            {
                WriteBytes(data, byteCount);
            }
            writtenFrames += frames;
        }

        void WriteSilence(UINT64 frames)
        {
            constexpr UINT32 CHUNK_FRAMES = 4096;
            while (frames > 0)
            {
                const UINT32 count = static_cast<UINT32>(std::min<UINT64>(frames, CHUNK_FRAMES));
                Write(nullptr, count, true);
                frames -= count;
            }
        }

        void Finalize(UINT64 expectedFrames)
        {
            if (writtenFrames < expectedFrames)
            {
                WriteSilence(expectedFrames - writtenFrames);
            }
            else if (writtenFrames > expectedFrames)
            {
                LARGE_INTEGER position{};
                position.QuadPart = static_cast<LONGLONG>(44 + expectedFrames * BYTES_PER_FRAME);
                if (!SetFilePointerEx(file, position, nullptr, FILE_BEGIN) || !SetEndOfFile(file))
                {
                    ThrowLastError("SetEndOfFile(audio.wav)");
                }
                writtenFrames = expectedFrames;
            }

            SetFilePointer(file, 0, nullptr, FILE_BEGIN);
            WriteHeader(writtenFrames);
            FlushFileBuffers(file);
        }

        UINT64 Frames() const { return writtenFrames; }

    private:
        void WriteHeader(UINT64 frames)
        {
            const UINT64 dataSize64 = frames * BYTES_PER_FRAME;
            if (dataSize64 > std::numeric_limits<DWORD>::max() - 36)
            {
                throw std::runtime_error("Captured WAV exceeds the RIFF 4 GiB limit.");
            }

            const DWORD dataSize = static_cast<DWORD>(dataSize64);
            const DWORD riffSize = 36 + dataSize;
            const DWORD byteRate = OUTPUT_SAMPLE_RATE * BYTES_PER_FRAME;
            const WORD blockAlign = BYTES_PER_FRAME;
            WriteBytes("RIFF", 4);
            WriteValue(riffSize);
            WriteBytes("WAVEfmt ", 8);
            WriteValue<DWORD>(16);
            WriteValue<WORD>(WAVE_FORMAT_PCM);
            WriteValue<WORD>(OUTPUT_CHANNELS);
            WriteValue<DWORD>(OUTPUT_SAMPLE_RATE);
            WriteValue<DWORD>(byteRate);
            WriteValue<WORD>(blockAlign);
            WriteValue<WORD>(OUTPUT_BITS_PER_SAMPLE);
            WriteBytes("data", 4);
            WriteValue(dataSize);
        }

        template<typename TValue>
        void WriteValue(TValue value)
        {
            WriteBytes(&value, sizeof(value));
        }

        void WriteBytes(const void* data, DWORD size)
        {
            DWORD written = 0;
            if (!WriteFile(file, data, size, &written, nullptr) || written != size)
            {
                ThrowLastError("WriteFile(audio.wav)");
            }
        }

        HANDLE file = INVALID_HANDLE_VALUE;
        UINT64 writtenFrames = 0;
    };

    ComPtr<IAudioClient> ActivateProcessLoopback(DWORD processId)
    {
        AUDIOCLIENT_ACTIVATION_PARAMS activation{};
        activation.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        activation.ProcessLoopbackParams.TargetProcessId = processId;
        activation.ProcessLoopbackParams.ProcessLoopbackMode =
            PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

        PROPVARIANT parameters{};
        parameters.vt = VT_BLOB;
        parameters.blob.cbSize = sizeof(activation);
        parameters.blob.pBlobData = reinterpret_cast<BYTE*>(&activation);

        auto handler = Make<ActivationHandler>();
        if (handler == nullptr)
        {
            throw std::bad_alloc();
        }
        ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
        ThrowIfFailed(
            ActivateAudioInterfaceAsync(
                VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                __uuidof(IAudioClient),
                &parameters,
                handler.Get(),
                &operation),
            "ActivateAudioInterfaceAsync");
        return handler->Wait();
    }

    UINT64 QpcToHundredNanoseconds(LONGLONG qpc, LONGLONG frequency)
    {
        return static_cast<UINT64>(
            (static_cast<long double>(qpc) * 10000000.0L) /
            static_cast<long double>(frequency));
    }

    bool TryReadStopTimestamp(const std::filesystem::path& path, LONGLONG& timestamp)
    {
        if (!std::filesystem::exists(path))
        {
            return false;
        }
        try
        {
            const std::string value = ReadAllBytes(path);
            size_t parsed = 0;
            timestamp = std::stoll(value, &parsed, 10);
            return parsed > 0 && timestamp > 0;
        }
        catch (...)
        {
            return false;
        }
    }

    void PublishReady(
        const std::filesystem::path& path,
        LONGLONG startTimestamp,
        LONGLONG frequency)
    {
        const std::filesystem::path temporary = path.wstring() + L".tmp";
        std::ostringstream value;
        value << startTimestamp << '\t' << frequency << '\t' << OUTPUT_SAMPLE_RATE;
        WriteUtf8File(temporary, value.str());
        if (!MoveFileExW(
                temporary.c_str(),
                path.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            ThrowLastError("MoveFileExW(audio ready)");
        }
    }
}

void CaptureProcessAudio(
    DWORD processId,
    const std::filesystem::path& audioPath,
    const std::filesystem::path& readyPath,
    const std::filesystem::path& stopPath)
{
    ComPtr<IAudioClient> audioClient = ActivateProcessLoopback(processId);

    WAVEFORMATEX format{};
    format.wFormatTag = WAVE_FORMAT_PCM;
    format.nChannels = OUTPUT_CHANNELS;
    format.nSamplesPerSec = OUTPUT_SAMPLE_RATE;
    format.wBitsPerSample = OUTPUT_BITS_PER_SAMPLE;
    format.nBlockAlign = BYTES_PER_FRAME;
    format.nAvgBytesPerSec = OUTPUT_SAMPLE_RATE * BYTES_PER_FRAME;

    const DWORD streamFlags =
        AUDCLNT_STREAMFLAGS_LOOPBACK |
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
        AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    ThrowIfFailed(
        audioClient->Initialize(
            AUDCLNT_SHAREMODE_SHARED, streamFlags, 0, 0, &format, nullptr),
        "IAudioClient::Initialize");

    ComPtr<IAudioCaptureClient> captureClient;
    ThrowIfFailed(
        audioClient->GetService(IID_PPV_ARGS(&captureClient)),
        "IAudioClient::GetService(IAudioCaptureClient)");

    HANDLE sampleReady = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (sampleReady == nullptr)
    {
        ThrowLastError("CreateEventW(sample ready)");
    }
    struct EventCloser
    {
        HANDLE value;
        ~EventCloser() { CloseHandle(value); }
    } eventCloser{ sampleReady };
    ThrowIfFailed(audioClient->SetEventHandle(sampleReady), "IAudioClient::SetEventHandle");

    WaveWriter writer(audioPath);
    ThrowIfFailed(audioClient->Start(), "IAudioClient::Start");

    LARGE_INTEGER startTimestamp{};
    LARGE_INTEGER frequency{};
    QueryPerformanceCounter(&startTimestamp);
    QueryPerformanceFrequency(&frequency);
    PublishReady(readyPath, startTimestamp.QuadPart, frequency.QuadPart);

    const UINT64 startHundredNanoseconds =
        QpcToHundredNanoseconds(startTimestamp.QuadPart, frequency.QuadPart);
    LONGLONG stopTimestamp = 0;
    UINT64 packets = 0;
    UINT64 discontinuities = 0;
    UINT64 timestampErrors = 0;
    UINT64 insertedSilenceFrames = 0;

    while (true)
    {
        const bool stopRequested = TryReadStopTimestamp(stopPath, stopTimestamp);
        if (!stopRequested)
        {
            WaitForSingleObject(sampleReady, 50);
        }

        UINT32 packetFrames = 0;
        while (true)
        {
            ThrowIfFailed(captureClient->GetNextPacketSize(&packetFrames), "IAudioCaptureClient::GetNextPacketSize");
            if (packetFrames == 0) break;
            BYTE* data = nullptr;
            DWORD flags = 0;
            UINT64 packetQpc = 0;
            ThrowIfFailed(
                captureClient->GetBuffer(
                    &data, &packetFrames, &flags, nullptr, &packetQpc),
                "IAudioCaptureClient::GetBuffer");
            struct PacketRelease
            {
                IAudioCaptureClient* client;
                UINT32 frames;
                ~PacketRelease() { client->ReleaseBuffer(frames); }
            } release{captureClient.Get(), packetFrames};
            if (packets > 0 && (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0) ++discontinuities;
            if ((flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0) ++timestampErrors;
            ++packets;

            UINT64 targetStartFrame = writer.Frames();
            if ((flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0)
            {
                if (packetQpc > startHundredNanoseconds)
                {
                    targetStartFrame =
                        (packetQpc - startHundredNanoseconds) * OUTPUT_SAMPLE_RATE / 10000000;
                }
            }

            if (targetStartFrame > writer.Frames())
            {
                insertedSilenceFrames += targetStartFrame - writer.Frames();
                writer.WriteSilence(targetStartFrame - writer.Frames());
            }
            const UINT32 skipFrames = static_cast<UINT32>(std::min<UINT64>(
                packetFrames,
                writer.Frames() > targetStartFrame ? writer.Frames() - targetStartFrame : 0));
            writer.Write(
                data == nullptr ? nullptr : data + static_cast<size_t>(skipFrames) * BYTES_PER_FRAME,
                packetFrames - skipFrames,
                (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
        }

        if (stopRequested)
        {
            break;
        }
    }

    audioClient->Stop();
    const LONGLONG elapsed = std::max<LONGLONG>(1, stopTimestamp - startTimestamp.QuadPart);
    const UINT64 expectedFrames = static_cast<UINT64>(
        (static_cast<long double>(elapsed) * OUTPUT_SAMPLE_RATE /
            static_cast<long double>(frequency.QuadPart)) + 0.5L);
    const UINT64 finalFrames = std::max<UINT64>(1, expectedFrames);
    if (writer.Frames() < finalFrames) insertedSilenceFrames += finalFrames - writer.Frames();
    writer.Finalize(finalFrames);
    std::ostringstream statistics;
    statistics << "{\"version\":1,\"audioFrames\":" << finalFrames
        << ",\"packets\":" << packets << ",\"discontinuityEvents\":" << discontinuities
        << ",\"timestampErrorPackets\":" << timestampErrors
        << ",\"insertedSilenceFrames\":" << insertedSilenceFrames << "}";
    WriteUtf8File(audioPath.wstring() + L".stats.json", statistics.str());
}
