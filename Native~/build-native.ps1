param(
    [string]$FfmpegRoot,
    [switch]$TestOnly,
    [switch]$BuildCheck
)
$ErrorActionPreference = 'Stop'
if (!$FfmpegRoot) { $FfmpegRoot = & "$PSScriptRoot/get-ffmpeg.ps1" }
$ffmpegPath = (Resolve-Path -LiteralPath $FfmpegRoot).Path
$vsPath = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'Visual Studio C++ tools not found.' }
$vcvars = Join-Path $vsPath 'VC/Auxiliary/Build/vcvars64.bat'
$outputPath = Join-Path $PSScriptRoot 'bin/ffmpeg-native'
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$compile = '@echo off' + "`r`n" + 'call "' + $vcvars + '" >nul' + "`r`n"
$compile += 'cl /nologo /std:c++17 /EHsc /MD /W4 /WX /O2 /DWIN32_LEAN_AND_MEAN /DNOMINMAX /LD /external:W0 /external:I"' + $ffmpegPath + '/include" "' + $PSScriptRoot + '/FfmpegCapture.cpp" "' + $PSScriptRoot + '/FfmpegMux.cpp" /link /OUT:GameFrameworkMediaCapture.dll /IMPLIB:GameFrameworkMediaCapture.lib /LIBPATH:"' + $ffmpegPath + '/lib" avcodec.lib avformat.lib avutil.lib swresample.lib d3d11.lib dxgi.lib' + "`r`n"
$compile += 'exit /b %errorlevel%' + "`r`n"
if ($BuildCheck)
{
    $compile = $compile.Replace('exit /b %errorlevel%', 'if errorlevel 1 exit /b %errorlevel%')
    $compile += 'cl /nologo /std:c++17 /EHsc /MD /W4 /O2 /DWIN32_LEAN_AND_MEAN /DNOMINMAX "' + $PSScriptRoot + '/D3D11CaptureCheck.cpp" /link /OUT:D3D11CaptureCheck.exe GameFrameworkMediaCapture.lib d3d11.lib dxgi.lib' + "`r`nexit /b %errorlevel%`r`n"
}
$batchPath = Join-Path $outputPath 'compile.cmd'
[IO.File]::WriteAllText($batchPath, $compile, [Text.Encoding]::ASCII)
Push-Location $outputPath
try { & $env:ComSpec /d /c $batchPath; if ($LASTEXITCODE) { throw 'Native compilation failed.' } }
finally { Pop-Location }
if (!$TestOnly)
{
    $pluginPath = Join-Path $PSScriptRoot '../Runtime/Plugins/x86_64'
    New-Item -ItemType Directory -Force -Path $pluginPath | Out-Null
    Copy-Item -LiteralPath (Join-Path $outputPath 'GameFrameworkMediaCapture.dll') -Destination $pluginPath
    foreach ($library in @('avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll'))
    { Copy-Item -LiteralPath (Join-Path $ffmpegPath "bin/$library") -Destination $pluginPath }
}
