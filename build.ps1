# Requires Windows and a .NET 10 x64 SDK. See README.md for examples.
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('help', 'restore', 'build', 'test', 'test-all', 'e2e', 'publish', 'app', 'cli', 'appcheck', 'gamecheck', 'desktop', 'publish-aot', 'app-aot', 'appcheck-aot', 'appcheck-headless', 'aotcheck')]
    [string]$Command = 'help',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotnetPath = '',
    [string]$PackageSource = '',
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Command -eq 'help') {
    Write-Host @'
Commands:
  restore       Restore solution dependencies
  build         Build the solution (Release by default)
  test          Run tests that do not need FFmpeg
  e2e           Run FFmpeg export, cancellation and scroll cadence tests
  test-all      Run all tests (requires FFmpeg and ffprobe)
  publish       Publish the Avalonia Native AOT ZIP to artifacts/win-x64-aot
  app           Build and launch Avalonia (JIT development mode)
  desktop       Alias for app
  publish-aot   Alias for publish
  app-aot       Publish and launch the native desktop application
  appcheck-aot  Run UI Automation smoke against the published native application
  appcheck-headless Run encoder menu regression without desktop windows or input
  aotcheck      Publish/run native CLI, rendering, cache and x86 memory checks
  cli <args>    Run the parser/judgement CLI
  appcheck      Build Avalonia and run the interactive GUI smoke check (JIT)
  gamecheck     Run game diagnostics (default: help); pass memory32 or live explicitly

Options: -Configuration Debug|Release, -DotnetPath <dotnet.exe>, -PackageSource <feed>
CLI/gamecheck arguments follow --, for example: ./build.ps1 cli -- dump-map file.osu
'@
    exit 0
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This solution requires Windows (GDI+ and process memory APIs).'
}

if (-not $DotnetPath) {
    if ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    } elseif (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'dotnet/dotnet.exe')) {
        $DotnetPath = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
    } else {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path
$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$restoreOptions = @()
if ($PackageSource) {
    if (Test-Path -LiteralPath $PackageSource) {
        $PackageSource = (Resolve-Path -LiteralPath $PackageSource).Path
    }
    $restoreOptions += "-p:RestoreSources=$PackageSource"
}
$forwarded = @($Rest | Where-Object { $_ -ne '--' })
$solution = Join-Path $PSScriptRoot 'ManiaReplayMaster.sln'
$desktopProject = Join-Path $PSScriptRoot 'src/ManiaReplayMaster.Desktop/ManiaReplayMaster.Desktop.csproj'
$gameProject = Join-Path $PSScriptRoot 'tools/gamecheck/harness/ManiaReplayMaster.GameCheck.csproj'

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed (exit code $LASTEXITCODE)." }
}

Push-Location $PSScriptRoot
try {
    Invoke-Dotnet @('--version')
    switch ($Command) {
        'restore' { Invoke-Dotnet (@('restore', $solution) + $restoreOptions + $forwarded) }
        'build' { Invoke-Dotnet (@('build', $solution, '-c', $Configuration) + $restoreOptions + $forwarded) }
        { $_ -in 'test', 'test-all', 'e2e' } {
            $filter = @()
            if ($Command -eq 'test') { $filter = @('--filter', 'Category!=EndToEnd') }
            if ($Command -eq 'e2e') { $filter = @('--filter', 'Category=EndToEnd') }
            Invoke-Dotnet (@('test', $solution, '-c', $Configuration, '--nologo') + $restoreOptions + $filter + $forwarded)
        }
        { $_ -in 'publish', 'publish-aot', 'app-aot' } {
            & (Join-Path $PSScriptRoot 'tools/package-aot.ps1') -DotnetPath $DotnetPath -Configuration $Configuration -RestoreOptions $restoreOptions
            if ($Command -eq 'app-aot') {
                Start-Process -FilePath (Join-Path $PSScriptRoot 'artifacts/win-x64-aot/ManiaReplayViewer.exe') -WorkingDirectory $PSScriptRoot
            }
        }
        { $_ -in 'app', 'desktop' } { Invoke-Dotnet (@('run', '--project', $desktopProject, '-c', $Configuration) + $restoreOptions + @('--') + $forwarded) }
        'appcheck-headless' {
            Invoke-Dotnet (@('run', '--project', (Join-Path $PSScriptRoot 'tools/appcheck/headless'), '-c', $Configuration) + $restoreOptions + $forwarded)
        }
        'appcheck-aot' {
            & (Join-Path $PSScriptRoot 'tools/appcheck/desktop-smoke.ps1') -Exe (Join-Path $PSScriptRoot 'artifacts/win-x64-aot/ManiaReplayViewer.exe') @forwarded
            if ($LASTEXITCODE -ne 0) { throw 'Native GUI smoke failed.' }
        }
        'aotcheck' {
            $toolsOutput = Join-Path $PSScriptRoot 'artifacts/aot-tools'
            foreach ($entry in @(@('src/ManiaReplayMaster.Cli', 'cli'), @('tools/aotcheck', 'render'), @('tools/gamecheck/harness', 'game'))) {
                Invoke-Dotnet (@('publish', (Join-Path $PSScriptRoot $entry[0]), '-c', $Configuration, '-r', 'win-x64',
                    '-p:PublishAot=true', '-p:OptimizationPreference=Size', '-p:TrimmerSingleWarn=false', '-o', (Join-Path $toolsOutput $entry[1])) + $restoreOptions)
            }
            & python (Join-Path $PSScriptRoot 'tools/aotcheck/verify-cli.py') (Join-Path $toolsOutput 'cli/mrm.exe')
            if ($LASTEXITCODE -ne 0) { throw 'Native CLI verification failed.' }
            & (Join-Path $toolsOutput 'render/aotcheck.exe') $PSScriptRoot --suite --require-native
            if ($LASTEXITCODE -ne 0) { throw 'Native render/cache verification failed.' }
            $helperOutput = Join-Path $PSScriptRoot 'artifacts/gamecheck-x86'
            Invoke-Dotnet (@('publish', (Join-Path $PSScriptRoot 'tools/gamecheck/x86target'), '-c', $Configuration, '-o', $helperOutput) + $restoreOptions)
            & (Join-Path $toolsOutput 'game/gamecheck.exe') memory32 --helper (Join-Path $helperOutput 'x86target.exe')
            if ($LASTEXITCODE -ne 0) { throw 'Native memory32 verification failed.' }
        }
        'cli' {
            Invoke-Dotnet (@('run', '--project', (Join-Path $PSScriptRoot 'src/ManiaReplayMaster.Cli'),
                '-c', $Configuration) + $restoreOptions + @('--') + $forwarded)
        }
        'gamecheck' {
            if ($forwarded.Count -eq 0) { $forwarded = @('help') }
            if ($forwarded[0] -eq 'memory32') {
                $helperProject = Join-Path $PSScriptRoot 'tools/gamecheck/x86target/ManiaReplayMaster.X86Target.csproj'
                $helperOutput = Join-Path $PSScriptRoot 'artifacts/gamecheck-x86'
                Invoke-Dotnet (@('publish', $helperProject, '-c', $Configuration, '-o', $helperOutput) + $restoreOptions)
                $forwarded += @('--helper', (Join-Path $helperOutput 'x86target.exe'))
            }
            Invoke-Dotnet (@('run', '--project', $gameProject, '-c', $Configuration) + $restoreOptions + @('--') + $forwarded)
        }
        'appcheck' {
            Invoke-Dotnet (@('build', $desktopProject, '-c', $Configuration) + $restoreOptions)
            $appExe = Join-Path $PSScriptRoot "src/ManiaReplayMaster.Desktop/bin/$Configuration/net10.0-windows/ManiaReplayViewer.exe"
            & (Join-Path $PSScriptRoot 'tools/appcheck/desktop-smoke.ps1') -Exe $appExe -AllowManagedRuntime @forwarded
            if ($LASTEXITCODE -ne 0) { throw "GUI check failed (exit code $LASTEXITCODE)." }
        }
    }
} finally {
    Pop-Location
}
