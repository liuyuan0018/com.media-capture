#pragma once

#include "Common.h"

void CaptureProcessAudio(
    DWORD processId,
    const std::filesystem::path& audioPath,
    const std::filesystem::path& readyPath,
    const std::filesystem::path& stopPath);
