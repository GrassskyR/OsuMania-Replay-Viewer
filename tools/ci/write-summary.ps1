[CmdletBinding()]
param([Parameter(Mandatory)][string]$Phase, [Parameter(Mandatory)][string]$Status)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$diagnostics = Join-Path $repo 'out/diagnostics'
New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
$revision = git -C $repo rev-parse HEAD
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("## $Phase validation: $Status")
$lines.Add('')
$lines.Add("Source commit: $revision")
$outcomes = if ($env:VALIDATION_STEPS) { $env:VALIDATION_STEPS | ConvertFrom-Json }
if ($outcomes) {
    $failed = @($outcomes.PSObject.Properties | Where-Object { $_.Value.outcome -eq 'failure' } | ForEach-Object Name)
    if ($failed.Count) { $lines.Add("Failed steps: $($failed -join ', ')") }
}
$lines.Add('')
$lines.Add('| Test report | Passed | Failed | Total |')
$lines.Add('| --- | ---: | ---: | ---: |')
foreach ($report in Get-ChildItem -LiteralPath $diagnostics -Filter '*.trx' -Recurse) {
    [xml]$xml = Get-Content -LiteralPath $report.FullName -Raw
    $counters = $xml.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -ne $counters) { $lines.Add("| $($report.Name) | $($counters.passed) | $($counters.failed) | $($counters.total) |") }
}
$measurements = Join-Path $repo 'artifacts/ManiaReplayViewer-win-x64-aot.measurements.json'
if (Test-Path -LiteralPath $measurements) {
    $package = (Get-Content -LiteralPath $measurements -Raw | ConvertFrom-Json).aot
    $lines.Add('')
    if ($package.PSObject.Properties.Name -contains 'build') { $lines.Add("Package version: $($package.build.version)") }
    $lines.Add("ZIP size: $([Math]::Round($package.zip_bytes / 1MB, 2)) MiB")
    if ($package.PSObject.Properties.Name -contains 'zip_sha256') { $lines.Add("ZIP SHA-256: $($package.zip_sha256)") }
}
$crashLog = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'ManiaReplayViewer/crash.log' }
if ($crashLog -and (Test-Path -LiteralPath $crashLog)) { Copy-Item -LiteralPath $crashLog -Destination (Join-Path $diagnostics 'crash.log') }
$lines.Add('')
$lines.Add('Download the diagnostics artifact for transcripts, MSBuild binlogs and TRX reports. Native diagnostics also include desktop screenshots and FFmpeg config.log when available.')
$lines | Set-Content -LiteralPath (Join-Path $diagnostics 'summary.md') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
