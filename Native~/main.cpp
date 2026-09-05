#include "Common.h"
#include "MediaFoundationEncoder.h"
#include "ProcessLoopbackCapture.h"

#include <iostream>
#include <limits>

namespace
{
    void PrintUsage()
    {
        std::wcerr <<
            L"Usage:\n"
            L"  WindowsMediaCaptureHelper capture --pid <pid> --audio <wav> --ready <tsv> --stop <file>\n"
            L"  WindowsMediaCaptureHelper encode --frames <tsv> --audio <wav> --output <mp4> --fps-num <n> --fps-den <d>\n";
    }
}

int wmain(int argc, wchar_t* argv[])
{
    try
    {
        if (argc < 2)
        {
            PrintUsage();
            return 2;
        }

        ThrowIfFailed(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "CoInitializeEx");
        struct CoUninitializer
        {
            ~CoUninitializer() { CoUninitialize(); }
        } coUninitializer;

        std::vector<std::wstring> arguments(argv + 2, argv + argc);
        const std::wstring command = argv[1];
        if (command == L"capture")
        {
            const long long processId = ParseInt64(RequireArgument(arguments, L"--pid"), "pid");
            if (processId <= 0 || processId > std::numeric_limits<DWORD>::max())
            {
                throw std::invalid_argument("PID is outside the Windows process ID range.");
            }
            CaptureProcessAudio(
                static_cast<DWORD>(processId),
                RequireArgument(arguments, L"--audio"),
                RequireArgument(arguments, L"--ready"),
                RequireArgument(arguments, L"--stop"));
            std::cout << "Captured process audio with WASAPI process loopback." << std::endl;
            return 0;
        }

        if (command == L"encode")
        {
            const long long numerator = ParseInt64(
                RequireArgument(arguments, L"--fps-num"), "fps numerator");
            const long long denominator = ParseInt64(
                RequireArgument(arguments, L"--fps-den"), "fps denominator");
            if (numerator <= 0 || denominator <= 0 ||
                numerator > std::numeric_limits<UINT32>::max() ||
                denominator > std::numeric_limits<UINT32>::max())
            {
                throw std::invalid_argument("Frame rate is outside the supported range.");
            }
            EncodeMediaFoundationMp4(
                RequireArgument(arguments, L"--frames"),
                RequireArgument(arguments, L"--audio"),
                RequireArgument(arguments, L"--output"),
                static_cast<UINT32>(numerator),
                static_cast<UINT32>(denominator));
            std::cout << "Encoded H.264/AAC MP4 with Windows Media Foundation." << std::endl;
            return 0;
        }

        PrintUsage();
        return 2;
    }
    catch (const std::exception& exception)
    {
        std::cerr << "WindowsMediaCaptureHelper: " << exception.what() << std::endl;
        return 1;
    }
}
