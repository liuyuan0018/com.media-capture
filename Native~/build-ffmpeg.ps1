param(
    [string]$BashPath = 'C:\Program Files\Git\bin\bash.exe',
    [ValidateRange(1, 64)][int]$Jobs = 8
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $BashPath)) { throw 'Git for Windows bash was not found. Pass -BashPath explicitly.' }
$dependency = Get-Content -LiteralPath "$PSScriptRoot/ffmpeg-dependency.json" -Raw | ConvertFrom-Json
$cache = Join-Path $PSScriptRoot '.deps'
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Assert-Hash([string]$Path, [string]$Expected)
{
    if (!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected)
    { throw "Dependency checksum mismatch: $Path" }
}

foreach ($source in $dependency.sources)
{
    $archive = Join-Path $PSScriptRoot $source.archive
    Assert-Hash $archive $source.sha256
    $destination = Join-Path $cache 'source'
    if (!(Test-Path -LiteralPath (Join-Path $destination $source.directory)))
    { Expand-Archive -LiteralPath $archive -DestinationPath $destination }
}

$tools = Join-Path $cache 'build-tools'
New-Item -ItemType Directory -Force -Path $tools | Out-Null
foreach ($tool in $dependency.buildTools)
{
    $archive = Join-Path $cache $tool.archive
    if (!(Test-Path -LiteralPath $archive))
    {
        $download = "$archive.download"
        Invoke-WebRequest -Uri $tool.url -OutFile $download
        Assert-Hash $download $tool.sha256
        Move-Item -LiteralPath $download -Destination $archive
    }
    Assert-Hash $archive $tool.sha256
    & tar -xf $archive -C $tools
    if ($LASTEXITCODE) { throw "Could not extract build tool: $archive" }
}

$vsPath = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsPath) { throw 'Visual Studio C++ desktop tools were not found.' }
$vcvars = Join-Path $vsPath 'VC/Auxiliary/Build/vcvars64.bat'
$scriptPath = Join-Path $PSScriptRoot 'ffmpeg-configure.sh'
$logPath = Join-Path $cache 'build-ffmpeg.log'
$batch = '@echo off' + "`r`n" + 'call "' + $vcvars + '" >nul' + "`r`n"
$batch += 'set "MC_BUILD_JOBS=' + $Jobs + '"' + "`r`n"
$batch += '"' + $BashPath + '" --noprofile --norc "' + $scriptPath + '" >"' + $logPath + '" 2>&1' + "`r`nexit /b %errorlevel%`r`n"
$batchPath = Join-Path $cache 'build-ffmpeg.cmd'
[IO.File]::WriteAllText($batchPath, $batch, [Text.Encoding]::ASCII)
Write-Host "Building the pinned FFmpeg libraries. Log: $logPath"
& $env:ComSpec /d /c $batchPath
if ($LASTEXITCODE)
{
    Get-Content -LiteralPath $logPath -Tail 40 | Write-Host
    throw "FFmpeg build failed. See $logPath"
}
$root = Join-Path $cache 'ffmpeg-minimal'
# FFmpeg's MSVC install puts import libraries beside its DLLs.
foreach ($name in @('avcodec', 'avformat', 'avutil', 'swresample'))
{ Copy-Item -LiteralPath (Join-Path $root "bin/$name.lib") -Destination (Join-Path $root "lib/$name.lib") }
Write-Output $root
