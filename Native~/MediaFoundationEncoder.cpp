#include "MediaFoundationEncoder.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <propvarutil.h>
#include <wincodec.h>

#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr LONGLONG HUNDRED_NANOSECONDS_PER_SECOND = 10000000;
    constexpr UINT32 AUDIO_CHUNK_FRAMES = 1024;

    struct WaveData
    {
        UINT16 channels = 0;
        UINT32 sampleRate = 0;
        UINT16 bitsPerSample = 0;
        UINT16 blockAlign = 0;
        std::vector<BYTE> samples;
    };

    UINT16 ReadUInt16(const BYTE* data)
    {
        return static_cast<UINT16>(data[0] | (data[1] << 8));
    }

    UINT32 ReadUInt32(const BYTE* data)
    {
        return static_cast<UINT32>(
            static_cast<UINT32>(data[0]) |
            (static_cast<UINT32>(data[1]) << 8) |
            (static_cast<UINT32>(data[2]) << 16) |
            (static_cast<UINT32>(data[3]) << 24));
    }

    WaveData ReadWave(const std::filesystem::path& path)
    {
        const std::string contents = ReadAllBytes(path);
        if (contents.size() < 44 ||
            std::memcmp(contents.data(), "RIFF", 4) != 0 ||
            std::memcmp(contents.data() + 8, "WAVE", 4) != 0)
        {
            throw std::runtime_error("Audio input is not a RIFF WAVE file.");
        }

        WaveData wave;
        bool foundFormat = false;
        bool foundData = false;
        size_t offset = 12;
        while (offset + 8 <= contents.size())
        {
            const BYTE* chunk = reinterpret_cast<const BYTE*>(contents.data() + offset);
            const UINT32 chunkSize = ReadUInt32(chunk + 4);
            const size_t dataOffset = offset + 8;
            if (dataOffset + chunkSize > contents.size())
            {
                throw std::runtime_error("WAV chunk extends beyond the file.");
            }

            if (std::memcmp(chunk, "fmt ", 4) == 0)
            {
                if (chunkSize < 16 || ReadUInt16(chunk + 8) != WAVE_FORMAT_PCM)
                {
                    throw std::runtime_error("Windows encoder requires PCM WAV audio.");
                }
                wave.channels = ReadUInt16(chunk + 10);
                wave.sampleRate = ReadUInt32(chunk + 12);
                wave.blockAlign = ReadUInt16(chunk + 20);
                wave.bitsPerSample = ReadUInt16(chunk + 22);
                foundFormat = true;
            }
            else if (std::memcmp(chunk, "data", 4) == 0)
            {
                const BYTE* begin = reinterpret_cast<const BYTE*>(contents.data() + dataOffset);
                wave.samples.assign(begin, begin + chunkSize);
                foundData = true;
            }

            offset = dataOffset + chunkSize + (chunkSize & 1u);
        }

        if (!foundFormat || !foundData || wave.channels != 2 ||
            wave.bitsPerSample != 16 || wave.blockAlign != 4 || wave.sampleRate == 0)
        {
            throw std::runtime_error("Windows encoder requires stereo 16-bit PCM WAV audio.");
        }
        return wave;
    }

    std::vector<std::filesystem::path> ReadFramePaths(const std::filesystem::path& path)
    {
        const std::string contents = ReadAllBytes(path);
        std::vector<std::filesystem::path> frames;
        size_t offset = 0;
        while (offset < contents.size())
        {
            size_t end = contents.find_first_of("\r\n", offset);
            if (end == std::string::npos)
            {
                end = contents.size();
            }
            const std::string line = contents.substr(offset, end - offset);
            const size_t tab = line.find('\t');
            if (tab != std::string::npos && tab + 1 < line.size())
            {
                frames.emplace_back(FromUtf8(line.substr(tab + 1)));
            }
            offset = end;
            while (offset < contents.size() &&
                (contents[offset] == '\r' || contents[offset] == '\n'))
            {
                ++offset;
            }
        }
        if (frames.empty())
        {
            throw std::runtime_error("Frame plan contains no image paths.");
        }
        return frames;
    }

    class ImageDecoder
    {
    public:
        ImageDecoder()
        {
            ThrowIfFailed(
                CoCreateInstance(
                    CLSID_WICImagingFactory,
                    nullptr,
                    CLSCTX_INPROC_SERVER,
                    IID_PPV_ARGS(&factory)),
                "CoCreateInstance(WICImagingFactory)");
        }

        std::pair<UINT32, UINT32> GetSize(const std::filesystem::path& path)
        {
            ComPtr<IWICBitmapFrameDecode> frame = OpenFrame(path);
            UINT32 width = 0;
            UINT32 height = 0;
            ThrowIfFailed(frame->GetSize(&width, &height), "IWICBitmapFrameDecode::GetSize");
            return { width, height };
        }

        std::vector<BYTE> DecodeBgraBottomUp(
            const std::filesystem::path& path,
            UINT32 width,
            UINT32 height)
        {
            ComPtr<IWICBitmapFrameDecode> frame = OpenFrame(path);
            UINT32 sourceWidth = 0;
            UINT32 sourceHeight = 0;
            ThrowIfFailed(frame->GetSize(&sourceWidth, &sourceHeight), "IWICBitmapFrameDecode::GetSize");
            if (sourceWidth < width || sourceHeight < height)
            {
                throw std::runtime_error("A captured frame is smaller than the first frame.");
            }

            ComPtr<IWICFormatConverter> converter;
            ThrowIfFailed(factory->CreateFormatConverter(&converter), "IWICImagingFactory::CreateFormatConverter");
            ThrowIfFailed(
                converter->Initialize(
                    frame.Get(),
                    GUID_WICPixelFormat32bppBGRA,
                    WICBitmapDitherTypeNone,
                    nullptr,
                    0,
                    WICBitmapPaletteTypeCustom),
                "IWICFormatConverter::Initialize");

            const UINT32 stride = width * 4;
            std::vector<BYTE> topDown(static_cast<size_t>(stride) * height);
            WICRect rectangle{ 0, 0, static_cast<INT>(width), static_cast<INT>(height) };
            ThrowIfFailed(
                converter->CopyPixels(
                    &rectangle,
                    stride,
                    static_cast<UINT32>(topDown.size()),
                    topDown.data()),
                "IWICFormatConverter::CopyPixels");

            std::vector<BYTE> bottomUp(topDown.size());
            for (UINT32 row = 0; row < height; ++row)
            {
                std::memcpy(
                    bottomUp.data() + static_cast<size_t>(row) * stride,
                    topDown.data() + static_cast<size_t>(height - row - 1) * stride,
                    stride);
            }
            return bottomUp;
        }

    private:
        ComPtr<IWICBitmapFrameDecode> OpenFrame(const std::filesystem::path& path)
        {
            ComPtr<IWICBitmapDecoder> decoder;
            ThrowIfFailed(
                factory->CreateDecoderFromFilename(
                    path.c_str(),
                    nullptr,
                    GENERIC_READ,
                    WICDecodeMetadataCacheOnDemand,
                    &decoder),
                "IWICImagingFactory::CreateDecoderFromFilename");
            ComPtr<IWICBitmapFrameDecode> frame;
            ThrowIfFailed(decoder->GetFrame(0, &frame), "IWICBitmapDecoder::GetFrame");
            return frame;
        }

        ComPtr<IWICImagingFactory> factory;
    };

    ComPtr<IMFSample> CreateSample(const BYTE* data, DWORD size, LONGLONG time, LONGLONG duration)
    {
        ComPtr<IMFMediaBuffer> buffer;
        ThrowIfFailed(MFCreateMemoryBuffer(size, &buffer), "MFCreateMemoryBuffer");
        BYTE* destination = nullptr;
        DWORD maximumLength = 0;
        ThrowIfFailed(buffer->Lock(&destination, &maximumLength, nullptr), "IMFMediaBuffer::Lock");
        if (maximumLength < size)
        {
            buffer->Unlock();
            throw std::runtime_error("Media Foundation sample buffer is too small.");
        }
        std::memcpy(destination, data, size);
        buffer->Unlock();
        ThrowIfFailed(buffer->SetCurrentLength(size), "IMFMediaBuffer::SetCurrentLength");

        ComPtr<IMFSample> sample;
        ThrowIfFailed(MFCreateSample(&sample), "MFCreateSample");
        ThrowIfFailed(sample->AddBuffer(buffer.Get()), "IMFSample::AddBuffer");
        ThrowIfFailed(sample->SetSampleTime(time), "IMFSample::SetSampleTime");
        ThrowIfFailed(sample->SetSampleDuration(duration), "IMFSample::SetSampleDuration");
        return sample;
    }

    ComPtr<IMFSinkWriter> CreateSinkWriter(
        const std::filesystem::path& path,
        UINT32 width,
        UINT32 height,
        UINT32 frameRateNumerator,
        UINT32 frameRateDenominator,
        const WaveData& wave,
        DWORD& videoStream,
        DWORD& audioStream)
    {
        ComPtr<IMFAttributes> attributes;
        ThrowIfFailed(MFCreateAttributes(&attributes, 3), "MFCreateAttributes");
        attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE);
        attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING, TRUE);
        attributes->SetUINT32(MF_LOW_LATENCY, FALSE);

        ComPtr<IMFSinkWriter> writer;
        ThrowIfFailed(
            MFCreateSinkWriterFromURL(path.c_str(), nullptr, attributes.Get(), &writer),
            "MFCreateSinkWriterFromURL");

        ComPtr<IMFMediaType> videoOutput;
        ThrowIfFailed(MFCreateMediaType(&videoOutput), "MFCreateMediaType(video output)");
        videoOutput->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        videoOutput->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        videoOutput->SetUINT32(MF_MT_AVG_BITRATE, 6000000);
        videoOutput->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        MFSetAttributeSize(videoOutput.Get(), MF_MT_FRAME_SIZE, width, height);
        MFSetAttributeRatio(
            videoOutput.Get(), MF_MT_FRAME_RATE, frameRateNumerator, frameRateDenominator);
        MFSetAttributeRatio(videoOutput.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        ThrowIfFailed(writer->AddStream(videoOutput.Get(), &videoStream), "IMFSinkWriter::AddStream(video)");

        ComPtr<IMFMediaType> videoInput;
        ThrowIfFailed(MFCreateMediaType(&videoInput), "MFCreateMediaType(video input)");
        videoInput->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        videoInput->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
        videoInput->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        videoInput->SetUINT32(MF_MT_DEFAULT_STRIDE, width * 4);
        MFSetAttributeSize(videoInput.Get(), MF_MT_FRAME_SIZE, width, height);
        MFSetAttributeRatio(
            videoInput.Get(), MF_MT_FRAME_RATE, frameRateNumerator, frameRateDenominator);
        MFSetAttributeRatio(videoInput.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        ThrowIfFailed(
            writer->SetInputMediaType(videoStream, videoInput.Get(), nullptr),
            "IMFSinkWriter::SetInputMediaType(video)");

        ComPtr<IMFMediaType> audioOutput;
        ThrowIfFailed(MFCreateMediaType(&audioOutput), "MFCreateMediaType(audio output)");
        audioOutput->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        audioOutput->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
        audioOutput->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, wave.channels);
        audioOutput->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, wave.sampleRate);
        audioOutput->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, wave.bitsPerSample);
        audioOutput->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
        audioOutput->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
        ThrowIfFailed(writer->AddStream(audioOutput.Get(), &audioStream), "IMFSinkWriter::AddStream(audio)");

        ComPtr<IMFMediaType> audioInput;
        ThrowIfFailed(MFCreateMediaType(&audioInput), "MFCreateMediaType(audio input)");
        audioInput->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
        audioInput->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM);
        audioInput->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, wave.channels);
        audioInput->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, wave.sampleRate);
        audioInput->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, wave.bitsPerSample);
        audioInput->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, wave.blockAlign);
        audioInput->SetUINT32(
            MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
            wave.sampleRate * wave.blockAlign);
        audioInput->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
        ThrowIfFailed(
            writer->SetInputMediaType(audioStream, audioInput.Get(), nullptr),
            "IMFSinkWriter::SetInputMediaType(audio)");
        return writer;
    }

    LONGLONG VideoTime(UINT64 index, UINT32 numerator, UINT32 denominator)
    {
        return static_cast<LONGLONG>(
            (static_cast<unsigned long long>(index) * denominator *
                HUNDRED_NANOSECONDS_PER_SECOND) / numerator);
    }

    LONGLONG AudioTime(UINT64 frame, UINT32 sampleRate)
    {
        return static_cast<LONGLONG>(
            (frame * HUNDRED_NANOSECONDS_PER_SECOND) / sampleRate);
    }
}

void EncodeMediaFoundationMp4(
    const std::filesystem::path& framesPath,
    const std::filesystem::path& audioPath,
    const std::filesystem::path& outputPath,
    UINT32 frameRateNumerator,
    UINT32 frameRateDenominator)
{
    if (frameRateNumerator == 0 || frameRateDenominator == 0)
    {
        throw std::invalid_argument("Frame rate must be positive.");
    }

    const std::vector<std::filesystem::path> framePaths = ReadFramePaths(framesPath);
    const WaveData wave = ReadWave(audioPath);
    ImageDecoder decoder;
    auto [sourceWidth, sourceHeight] = decoder.GetSize(framePaths.front());
    const UINT32 width = sourceWidth & ~1u;
    const UINT32 height = sourceHeight & ~1u;
    if (width == 0 || height == 0)
    {
        throw std::runtime_error("Captured framebuffer dimensions are invalid.");
    }

    const std::filesystem::path mediaFoundationOutput = outputPath.wstring() + L".mp4";
    DeleteFileW(mediaFoundationOutput.c_str());
    DeleteFileW(outputPath.c_str());

    ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup");
    try
    {
        DWORD videoStream = 0;
        DWORD audioStream = 0;
        ComPtr<IMFSinkWriter> writer = CreateSinkWriter(
            mediaFoundationOutput,
            width,
            height,
            frameRateNumerator,
            frameRateDenominator,
            wave,
            videoStream,
            audioStream);
        ThrowIfFailed(writer->BeginWriting(), "IMFSinkWriter::BeginWriting");

        UINT64 videoIndex = 0;
        UINT64 audioFrame = 0;
        const UINT64 totalAudioFrames = wave.samples.size() / wave.blockAlign;
        std::filesystem::path cachedPath;
        std::vector<BYTE> cachedImage;

        while (videoIndex < framePaths.size() || audioFrame < totalAudioFrames)
        {
            const LONGLONG nextVideoTime = videoIndex < framePaths.size()
                ? VideoTime(videoIndex, frameRateNumerator, frameRateDenominator)
                : std::numeric_limits<LONGLONG>::max();
            const LONGLONG nextAudioTime = audioFrame < totalAudioFrames
                ? AudioTime(audioFrame, wave.sampleRate)
                : std::numeric_limits<LONGLONG>::max();

            if (nextVideoTime <= nextAudioTime)
            {
                if (cachedPath != framePaths[videoIndex])
                {
                    cachedPath = framePaths[videoIndex];
                    cachedImage = decoder.DecodeBgraBottomUp(cachedPath, width, height);
                }
                const LONGLONG end = VideoTime(
                    videoIndex + 1, frameRateNumerator, frameRateDenominator);
                ComPtr<IMFSample> sample = CreateSample(
                    cachedImage.data(),
                    static_cast<DWORD>(cachedImage.size()),
                    nextVideoTime,
                    end - nextVideoTime);
                ThrowIfFailed(
                    writer->WriteSample(videoStream, sample.Get()),
                    "IMFSinkWriter::WriteSample(video)");
                ++videoIndex;
            }
            else
            {
                const UINT32 frames = static_cast<UINT32>(
                    std::min<UINT64>(AUDIO_CHUNK_FRAMES, totalAudioFrames - audioFrame));
                const DWORD bytes = frames * wave.blockAlign;
                const LONGLONG end = AudioTime(audioFrame + frames, wave.sampleRate);
                ComPtr<IMFSample> sample = CreateSample(
                    wave.samples.data() + audioFrame * wave.blockAlign,
                    bytes,
                    nextAudioTime,
                    end - nextAudioTime);
                ThrowIfFailed(
                    writer->WriteSample(audioStream, sample.Get()),
                    "IMFSinkWriter::WriteSample(audio)");
                audioFrame += frames;
            }
        }

        ThrowIfFailed(writer->Finalize(), "IMFSinkWriter::Finalize");
        writer.Reset();
        if (!MoveFileExW(
                mediaFoundationOutput.c_str(),
                outputPath.c_str(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        {
            ThrowLastError("MoveFileExW(encoded MP4)");
        }
        MFShutdown();
    }
    catch (...)
    {
        MFShutdown();
        DeleteFileW(mediaFoundationOutput.c_str());
        throw;
    }
}
