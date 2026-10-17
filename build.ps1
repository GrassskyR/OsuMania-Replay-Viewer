# Requires Windows and a .NET 8 x64 SDK. See README.md for examples.
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('help', 'restore', 'build', 'test', 'test-all', 'e2e', 'publish', 'app', 'cli', 'appcheck', 'gamecheck')]
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
  publish       Publish a self-contained Windows x64 app to artifacts/win-x64
  app           Build and launch the WPF application
  cli <args>    Run the parser/judgement CLI
  appcheck      Build and run the interactive GUI smoke check
  gamecheck     Run game diagnostics (default: help); pass memory32 or live explicitly

Options: -Configuration Debug|Release, -DotnetPath <dotnet.exe>, -PackageSource <feed>
CLI/gamecheck arguments follow --, for example: ./build.ps1 cli -- dump-map file.osu
'@
    exit 0
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This solution requires Windows (WPF, GDI+ and process memory APIs).'
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
$appProject = Join-Path $PSScriptRoot 'src/ManiaReplayMaster.App/ManiaReplayMaster.App.csproj'
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
        'publish' {
            Invoke-Dotnet (@('publish', $appProject, '-c', $Configuration, '-r', 'win-x64',
                '--self-contained', 'true', '-p:PublishTrimmed=false',
                '-o', (Join-Path $PSScriptRoot 'artifacts/win-x64')) + $restoreOptions + $forwarded)
        }
        'app' { Invoke-Dotnet (@('run', '--project', $appProject, '-c', $Configuration) + $restoreOptions + @('--') + $forwarded) }
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
            Invoke-Dotnet (@('build', $appProject, '-c', $Configuration) + $restoreOptions)
            $appExe = Join-Path $PSScriptRoot "src/ManiaReplayMaster.App/bin/$Configuration/net8.0-windows/ManiaReplayMaster.exe"
            & (Join-Path $PSScriptRoot 'tools/appcheck/app-smoke.ps1') -Exe $appExe @forwarded
            if ($LASTEXITCODE -ne 0) { throw "GUI check failed (exit code $LASTEXITCODE)." }
        }
    }
} finally {
    Pop-Location
}
