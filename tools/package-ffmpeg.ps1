# Download a pinned, static GPL build (including libx264) for Windows x64 releases.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$archiveName = 'ffmpeg-9.0.2-essentials_build.zip'
$url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/$archiveName"
$expectedHash = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$cache = Join-Path $repo 'out/dependencies/ffmpeg'
$archive = Join-Path $cache $archiveName
New-Item -ItemType Directory -Path $cache -Force | Out-Null

if (!(Test-Path -LiteralPath $archive)) {
    $download = "$archive.download"
    Write-Host "Downloading FFmpeg: $url"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source --fail --location --silent --show-error --retry 3 --connect-timeout 30 --output $download $url
        if ($LASTEXITCODE -ne 0) { throw 'FFmpeg download failed.' }
    } else {
        Invoke-WebRequest -Uri $url -OutFile $download -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash) {
        Remove-Item -LiteralPath $download
        throw 'FFmpeg archive checksum mismatch.'
    }
    Move-Item -LiteralPath $download -Destination $archive -Force
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw "Invalid FFmpeg cache. Remove $archive and publish again."
}

# Extract only ffmpeg.exe and upstream documentation; ffplay/ffprobe are not runtime dependencies.
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$notices = Join-Path $Destination 'licenses/ffmpeg'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $executables = @($zip.Entries | Where-Object { $_.FullName -match '/bin/ffmpeg\.exe$' })
    if ($executables.Count -ne 1) { throw 'Expected one static ffmpeg.exe in the upstream archive.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($executables[0], (Join-Path $Destination 'ffmpeg.exe'), $true)
    foreach ($entry in $zip.Entries) {
        # Retain top-level license/readme documents without copying ffplay or ffprobe.
        if ($entry.FullName -match '^[^/]+/[^/]+$') {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $notices $entry.Name), $true)
        }
    }
} finally {
    $zip.Dispose()
}
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $notices 'GPL-3.0.txt') -Force
$versionInfo = & (Join-Path $Destination 'ffmpeg.exe') -version
if ($LASTEXITCODE -ne 0) { throw 'Bundled FFmpeg failed to start.' }
@"
FFmpeg 9.0.2 essentials (Windows x64, static GPLv3 build with libx264)
Copyright (c) 2000-2026 the FFmpeg developers and respective library authors.
Binary provider: https://www.gyan.dev/ffmpeg/builds/
Binary archive: $url
SHA256: $expectedHash
FFmpeg source: https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz
Build configuration and dependency sources: https://www.gyan.dev/ffmpeg/builds/#libraries
Licensing: https://ffmpeg.org/legal.html

Invoked as a separate executable. See the upstream readme and GPL-3.0.txt.
When redistributing, also provide the corresponding source for this build and its
libraries, as required by GPLv3; the links above identify the upstream sources.

$($versionInfo -join [Environment]::NewLine)
"@ | Set-Content -LiteralPath (Join-Path $notices 'BUILD.txt') -Encoding UTF8
Write-Host "Bundled FFmpeg: $(Join-Path $Destination 'ffmpeg.exe')"
