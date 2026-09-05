#pragma once

#include "Common.h"

void EncodeMediaFoundationMp4(
    const std::filesystem::path& framesPath,
    const std::filesystem::path& audioPath,
    const std::filesystem::path& outputPath,
    UINT32 frameRateNumerator,
    UINT32 frameRateDenominator);
