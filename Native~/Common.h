#pragma once

#include <Windows.h>

#include <filesystem>
#include <fstream>
#include <iterator>
#include <new>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#include <cwchar>

#include <wrl/client.h>

inline void ThrowIfFailed(HRESULT result, const char* operation)
{
    if (SUCCEEDED(result))
    {
        return;
    }

    std::ostringstream message;
    message << operation << " failed with HRESULT 0x" << std::hex
            << static_cast<unsigned long>(result);
    throw std::runtime_error(message.str());
}

inline void ThrowLastError(const char* operation)
{
    const DWORD error = GetLastError();
    std::ostringstream message;
    message << operation << " failed with Win32 error " << error;
    throw std::runtime_error(message.str());
}

inline std::string ToUtf8(const std::wstring& value)
{
    if (value.empty())
    {
        return {};
    }

    const int size = WideCharToMultiByte(
        CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string converted(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(
        CP_UTF8, 0, value.data(), static_cast<int>(value.size()), converted.data(), size, nullptr, nullptr);
    return converted;
}

inline std::wstring FromUtf8(const std::string& value)
{
    if (value.empty())
    {
        return {};
    }

    const int size = MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (size <= 0)
    {
        ThrowLastError("MultiByteToWideChar");
    }

    std::wstring converted(static_cast<size_t>(size), L'\0');
    MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), converted.data(), size);
    return converted;
}

inline std::string ReadAllBytes(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary);
    if (!stream)
    {
        throw std::runtime_error("Cannot open " + ToUtf8(path.wstring()));
    }

    return std::string(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
}

inline void WriteUtf8File(const std::filesystem::path& path, const std::string& value)
{
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream)
    {
        throw std::runtime_error("Cannot create " + ToUtf8(path.wstring()));
    }
    stream.write(value.data(), static_cast<std::streamsize>(value.size()));
    if (!stream)
    {
        throw std::runtime_error("Cannot write " + ToUtf8(path.wstring()));
    }
}

inline std::wstring RequireArgument(
    const std::vector<std::wstring>& arguments,
    const std::wstring& name)
{
    for (size_t index = 0; index + 1 < arguments.size(); ++index)
    {
        if (arguments[index] == name)
        {
            return arguments[index + 1];
        }
    }
    throw std::invalid_argument("Missing argument " + ToUtf8(name));
}

inline long long ParseInt64(const std::wstring& value, const char* name)
{
    wchar_t* end = nullptr;
    const long long parsed = wcstoll(value.c_str(), &end, 10);
    if (end == value.c_str() || *end != L'\0')
    {
        throw std::invalid_argument(std::string("Invalid ") + name);
    }
    return parsed;
}
