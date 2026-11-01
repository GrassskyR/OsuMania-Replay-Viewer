[CmdletBinding()]
param([string]$MsysRoot = 'C:/msys64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = Join-Path $repo 'out/dependencies/ffmpeg-minimal'
$diagnostics = Join-Path $repo 'out/diagnostics'
New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
Start-Transcript -LiteralPath (Join-Path $diagnostics 'ffmpeg.log') -Append | Out-Null
try {
$fingerprint = & (Join-Path $PSScriptRoot 'get-build-fingerprint.ps1') -MsysRoot $MsysRoot
$stamp = Join-Path $root 'package/build-inputs.txt'
if ((Test-Path -LiteralPath $stamp) -and (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $fingerprint.Trim()) {
    $cached = Join-Path $root 'package/ffmpeg.exe'
    $hashFile = Join-Path $root 'package/ffmpeg.sha256'
    if ((Test-Path -LiteralPath $cached) -and (Test-Path -LiteralPath $hashFile) -and
        (Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -eq (Get-Content -LiteralPath $hashFile -Raw).Trim()) {
        Write-Host "Minimal FFmpeg already built: $cached"
        return
    }
}
$downloads = Join-Path $root 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$sources = Get-Content (Join-Path $PSScriptRoot 'sources.json') -Raw | ConvertFrom-Json
foreach ($item in $sources.PSObject.Properties.Value) {
    $file = Join-Path $downloads $item.file
    if (-not (Test-Path -LiteralPath $file)) {
        & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 30 --output "$file.download" $item.url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($item.url)" }
        if ((Get-FileHash -LiteralPath "$file.download" -Algorithm SHA256).Hash -ne $item.sha256) { throw "Checksum mismatch: $file" }
        Move-Item -LiteralPath "$file.download" -Destination $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.sha256) { throw "Checksum mismatch: $file" }
}
Expand-Archive -LiteralPath (Join-Path $downloads $sources.nasm.file) -DestinationPath (Join-Path $root 'nasm') -Force
$bash = Join-Path $MsysRoot 'usr/bin/bash.exe'
$oldMsystem = $env:MSYSTEM
$oldChere = $env:CHERE_INVOKING
$oldWorkRoot = $env:MRM_FFMPEG_WORK_ROOT
$env:MSYSTEM = 'MINGW64'
$env:CHERE_INVOKING = '1'
# Never mix static libraries or extracted sources from different build inputs/toolchains.
$env:MRM_FFMPEG_WORK_ROOT = Join-Path $root "work/$fingerprint"
$script = (Join-Path $PSScriptRoot 'build-minimal.sh').Replace('\', '/')
try {
    & $bash -lc ('bash "' + $script + '"') 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Minimal FFmpeg build failed. See out/diagnostics/ffmpeg.log and out/dependencies/ffmpeg-minimal/config.log.' }
} finally {
    $env:MSYSTEM = $oldMsystem
    $env:CHERE_INVOKING = $oldChere
    $env:MRM_FFMPEG_WORK_ROOT = $oldWorkRoot
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sources.json'), (Join-Path $PSScriptRoot 'build-minimal.sh') -Destination (Join-Path $root 'package/licenses/ffmpeg')
@'
This FFmpeg is built from the pinned sources in sources.json. The complete build
configuration is in build-minimal.sh; source URLs and SHA-256 are in sources.json.
FFmpeg/x264: GPL; LAME: LGPL; zlib: zlib license. Source archives are unmodified.
Rebuild with tools/ffmpeg/build-minimal.ps1 in the Replay Viewer source repository.
'@ | Set-Content -LiteralPath (Join-Path $root 'package/licenses/ffmpeg/SOURCES.txt') -Encoding utf8
$ffmpeg = Join-Path $root 'package/ffmpeg.exe'
$encoders = & $ffmpeg -hide_banner -encoders 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw 'Cannot list minimal encoders.' }
if ($encoders -match 'h264_(nvenc|qsv|amf)' -or $encoders -notmatch '\blibx264\b' -or $encoders -notmatch '\baac\b' -or $encoders -notmatch '\blibmp3lame\b') { throw 'Unexpected encoder set.' }
$hash = (Get-FileHash -LiteralPath $ffmpeg -Algorithm SHA256).Hash.ToLowerInvariant()
$hash | Set-Content -LiteralPath (Join-Path $root 'package/ffmpeg.sha256') -Encoding ascii
$fingerprint | Set-Content -LiteralPath $stamp -Encoding ascii
Write-Host "Minimal FFmpeg: $ffmpeg ($((Get-Item -LiteralPath $ffmpeg).Length) bytes)"
} finally { Stop-Transcript | Out-Null }
