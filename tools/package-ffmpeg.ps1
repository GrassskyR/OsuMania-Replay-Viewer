# Bundle the project's minimal static FFmpeg; the full development tool is never packaged.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination, [string]$MsysRoot = 'C:/msys64', [string]$PackageDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($PackageDirectory) {
    $package = (Resolve-Path -LiteralPath $PackageDirectory).Path
} else {
    & (Join-Path $PSScriptRoot 'ffmpeg/build-minimal.ps1') -MsysRoot $MsysRoot
    $package = Join-Path $repo 'out/dependencies/ffmpeg-minimal/package'
}
$ffmpeg = Join-Path $package 'ffmpeg.exe'
$expected = (Get-Content -LiteralPath (Join-Path $package 'ffmpeg.sha256') -Raw).Trim()
if ((Get-FileHash -LiteralPath $ffmpeg -Algorithm SHA256).Hash -ne $expected) { throw 'Minimal FFmpeg checksum mismatch. Rebuild tools/ffmpeg/build-minimal.ps1.' }
$encoders = & $ffmpeg -hide_banner -encoders 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg failed to start.' }
$names = @($encoders -split "`n" | ForEach-Object { if ($_ -match '^\s*V\S{5}\s+(\S+)\s' -and $Matches[1] -ne '=') { $Matches[1] } })
if ('libx264' -notin $names -or @($names | Where-Object { $_ -notin 'libx264','wrapped_avframe' }).Count) { throw 'Unexpected video encoder in minimal FFmpeg.' }
foreach ($notice in @('FFmpeg-GPL-3.0.txt', 'x264-COPYING.txt', 'LAME-COPYING.txt', 'SOURCES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package "licenses/ffmpeg/$notice") -PathType Leaf)) {
        throw "Minimal FFmpeg package is missing $notice"
    }
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $Destination 'ffmpeg.exe') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'licenses/ffmpeg') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $package 'licenses/ffmpeg') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination 'licenses/ffmpeg') -Force
}
Write-Host "Bundled minimal FFmpeg: $((Get-Item -LiteralPath $ffmpeg).Length) bytes"
