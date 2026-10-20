# Publish into a new staging directory; retain any previous output as a recoverable backup.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$DotnetPath, [string]$Configuration = 'Release', [string[]]$RestoreOptions = @())
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$stagingRoot = Join-Path $artifacts ('.aot-staging-' + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $stagingRoot 'win-x64-aot'
$destination = Join-Path $artifacts 'win-x64-aot'
$archiveName = 'ManiaReplayViewer-win-x64-aot.zip'
$measurementsName = 'ManiaReplayViewer-win-x64-aot.measurements.json'
$archive = Join-Path $artifacts $archiveName
$backup = Join-Path $artifacts ('win-x64-aot.previous-' + [Guid]::NewGuid().ToString('N'))
# All paths used by moves are resolved and checked against the intended artifacts directory.
foreach ($path in @($stagingRoot, $staging, $destination, $archive, $backup)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid package path: $path"
    }
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null
& $DotnetPath publish (Join-Path $repo 'src/ManiaReplayMaster.Desktop') -c $Configuration -r win-x64 `
    -p:PublishAot=true -p:OptimizationPreference=Size -p:DebugType=None -p:DebugSymbols=false -o $staging @RestoreOptions
if ($LASTEXITCODE -ne 0) { throw "AOT publish failed. Staging retained at $staging" }
& (Join-Path $PSScriptRoot 'package-ffmpeg.ps1') -Destination $staging

# Copy the licenses from the exact restored package graph, including native Skia/HarfBuzz/ANGLE
# notices and the Native AOT runtime. No WindowsDesktop runtime is part of this graph.
$assets = Get-Content (Join-Path $repo 'src/ManiaReplayMaster.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$folders = @($assets.packageFolders.PSObject.Properties.Name)
$manifest = [Collections.Generic.List[string]]::new()
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $manifest.Add($library.Name)
    $package = $null
    foreach ($folder in $folders) {
        $candidate = Join-Path $folder $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
    }
    if (-not $package) { throw "Missing restored package: $($library.Name)" }
    $notices = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|THIRD.PARTY.NOTICES)' -or $_.Extension -eq '.nuspec' })
    $target = Join-Path $staging ('licenses/packages/' + $library.Name)
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $target }
}
$manifest | Sort-Object | Set-Content -LiteralPath (Join-Path $staging 'licenses/PACKAGES.txt') -Encoding utf8
& python (Join-Path $PSScriptRoot 'package-audit.py') $staging (Join-Path $stagingRoot $archiveName)
if ($LASTEXITCODE -ne 0) { throw "Package validation failed. Staging retained at $staging" }

# Promote the directory, archive and measurements together, after every validation succeeds.
# On a failed move (for example a locked output), restore the previous set without deleting it.
$names = @('win-x64-aot', $archiveName, $measurementsName)
$saved = [Collections.Generic.List[string]]::new()
$promoted = [Collections.Generic.List[string]]::new()
try {
    foreach ($name in $names) {
        $current = Join-Path $artifacts $name
        if (Test-Path -LiteralPath $current) {
            New-Item -ItemType Directory -Force -Path $backup | Out-Null
            Move-Item -LiteralPath $current -Destination (Join-Path $backup $name)
            $saved.Add($name)
        }
    }
    foreach ($name in $names) {
        Move-Item -LiteralPath (Join-Path $stagingRoot $name) -Destination (Join-Path $artifacts $name)
        $promoted.Add($name)
    }
} catch {
    foreach ($name in $promoted) {
        Move-Item -LiteralPath (Join-Path $artifacts $name) -Destination (Join-Path $stagingRoot $name)
    }
    foreach ($name in $saved) {
        Move-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $artifacts $name)
    }
    throw
}
Remove-Item -LiteralPath $stagingRoot
if ($saved.Count -gt 0) { Write-Host "Previous package set preserved: $backup" }
Write-Host "Native AOT package: $archive"
