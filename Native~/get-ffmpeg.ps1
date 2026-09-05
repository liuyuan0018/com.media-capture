param([switch]$Rebuild)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '.deps/ffmpeg-minimal'
if ($Rebuild -or !(Test-Path -LiteralPath (Join-Path $root 'lib/avcodec.lib')))
{ $root = & "$PSScriptRoot/build-ffmpeg.ps1" }
foreach ($library in @('avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll'))
{
    if (!(Test-Path -LiteralPath (Join-Path $root "bin/$library")))
    { throw "Incomplete FFmpeg build: $library. Run build-ffmpeg.ps1." }
}
Write-Output $root
